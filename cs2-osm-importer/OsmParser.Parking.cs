using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal static partial class OsmParser
    {
        // Outlines smaller than this are not worth a lot (kerb bays, single spaces mapped as areas).
        private const float MinParkingAreaSquareMeters = 30f;
        private const float MinParkingRingPointSpacing = 0.5f;

        /// <summary>
        /// Reads amenity=parking outlines: closed ways, and the outer rings of multipolygon relations (inner rings,
        /// i.e. holes, are ignored for now; CS2 areas cannot have holes). Positions use the same projection as roads.
        /// </summary>
        private static void ReadParkingAreas(XmlDocument xml, XmlNodeList wayElements, Dictionary<long, GeoNode> nodes, HashSet<long> roadNodeIds, OsmDocument document, OsmImportOptions options)
        {
            var scale = options.ScaleFactor;
            var projected = new HashSet<long>(roadNodeIds);
            float2? Position(long nodeId)
            {
                if (!nodes.TryGetValue(nodeId, out var node))
                    return null;
                // Road nodes are projected (and possibly merged) already; parking-only nodes get the same projection.
                if (projected.Add(nodeId))
                    Project(node, document.CenterLat, document.CenterLon);
                return new float2((float)(node.X * scale), (float)(node.Y * scale));
            }

            Dictionary<long, XmlElement> wayById = null;
            var added = 0;
            var kinds = new Dictionary<OsmParkingKind, int>();

            foreach (XmlElement wayElement in wayElements)
            {
                var tags = ReadTags(wayElement);
                if (!IsParkingArea(tags))
                    continue;
                var ids = ReadNodeRefs(wayElement);
                if (ids.Count < 4 || ids[0] != ids[ids.Count - 1])
                    continue;
                if (TryBuildParkingArea(ReadLong(wayElement, "id"), false, tags, new List<List<long>> { ids }, Position, out var area))
                {
                    document.ParkingAreas.Add(area);
                    added++;
                    kinds[area.Kind] = kinds.TryGetValue(area.Kind, out var c) ? c + 1 : 1;
                }
            }

            var relations = xml.SelectNodes("/osm/relation");
            if (relations != null)
            {
                foreach (XmlElement relation in relations)
                {
                    var tags = ReadTags(relation);
                    if (!tags.TryGetValue("type", out var type) || type != "multipolygon" || !IsParkingArea(tags))
                        continue;
                    if (wayById == null)
                    {
                        wayById = new Dictionary<long, XmlElement>();
                        foreach (XmlElement w in wayElements)
                            wayById[ReadLong(w, "id")] = w;
                    }

                    var outerParts = new List<List<long>>();
                    foreach (XmlElement member in relation.SelectNodes("member"))
                    {
                        if (member.GetAttribute("type") != "way" || member.GetAttribute("role") == "inner")
                            continue;
                        if (wayById.TryGetValue(ReadLong(member, "ref"), out var memberWay))
                            outerParts.Add(ReadNodeRefs(memberWay));
                    }

                    foreach (var ring in AssembleRings(outerParts))
                    {
                        if (TryBuildParkingArea(ReadLong(relation, "id"), true, tags, new List<List<long>> { ring }, Position, out var area))
                        {
                            document.ParkingAreas.Add(area);
                            added++;
                            kinds[area.Kind] = kinds.TryGetValue(area.Kind, out var c) ? c + 1 : 1;
                        }
                    }
                }
            }

            if (added > 0)
                Mod.log.Info($"Read {added} OSM parking areas: " + string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        }

        private static bool IsParkingArea(Dictionary<string, string> tags) =>
            tags.TryGetValue("amenity", out var amenity) && amenity == "parking";

        private static bool TryBuildParkingArea(long id, bool fromRelation, Dictionary<string, string> tags, List<List<long>> rings, Func<long, float2?> position, out OsmParkingArea area)
        {
            area = null;
            var points = new List<float2>();
            foreach (var nodeId in rings[0])
            {
                var p = position(nodeId);
                if (!p.HasValue)
                    continue;
                if (points.Count > 0 && math.distance(points[points.Count - 1], p.Value) < MinParkingRingPointSpacing)
                    continue;
                points.Add(p.Value);
            }
            while (points.Count > 1 && math.distance(points[0], points[points.Count - 1]) < MinParkingRingPointSpacing)
                points.RemoveAt(points.Count - 1);
            if (points.Count < 3)
                return false;

            var signedArea = ParkingGeometry.SignedArea(points);
            if (math.abs(signedArea) < MinParkingAreaSquareMeters)
                return false;
            if (signedArea < 0f)
                points.Reverse();

            area = new OsmParkingArea
            {
                Id = id,
                FromRelation = fromRelation,
                Name = tags.TryGetValue("name", out var name) ? name : string.Empty,
                Kind = ClassifyParking(tags),
                Capacity = tags.TryGetValue("capacity", out var cap) && int.TryParse(cap, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity) ? capacity : 0,
            };
            area.Ring.AddRange(points);
            return true;
        }

        private static OsmParkingKind ClassifyParking(Dictionary<string, string> tags)
        {
            tags.TryGetValue("parking", out var parking);
            switch (parking)
            {
                case "multi-storey":
                    return OsmParkingKind.MultiStorey;
                case "underground":
                    return OsmParkingKind.Underground;
                case "rooftop":
                    return OsmParkingKind.Rooftop;
                case "street_side":
                case "lane":
                case "layby":
                case "on_kerb":
                case "half_on_kerb":
                    return OsmParkingKind.StreetSide;
                case null:
                case "":
                case "surface":
                    break;
                default:
                    return OsmParkingKind.Other;
            }

            if (tags.TryGetValue("location", out var location) && location == "underground")
                return OsmParkingKind.Underground;
            if (tags.TryGetValue("layer", out var layer) && int.TryParse(layer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l < 0)
                return OsmParkingKind.Underground;
            // An unspecified lot drawn as a building is a parking structure.
            if (tags.TryGetValue("building", out var building) && !IsFalsey(building))
                return OsmParkingKind.MultiStorey;
            return OsmParkingKind.Surface;
        }

        /// <summary>Marks service=parking_aisle ways lying inside a surface parking lot; the generated lot replaces them.</summary>
        private static void MarkAislesInsideParkingLots(List<OsmWay> ways, List<OsmParkingArea> areas)
        {
            var lots = areas.Where(a => a.Kind == OsmParkingKind.Surface).ToList();
            if (lots.Count == 0)
                return;
            var bounds = lots.Select(a => ParkingGeometry.Bounds(a.Ring)).ToList();
            var marked = 0;
            foreach (var way in ways)
            {
                if (!way.IsParkingAisle || way.Points.Count < 2)
                    continue;
                var wb = new float4(way.Points.Min(p => p.x), way.Points.Min(p => p.z), way.Points.Max(p => p.x), way.Points.Max(p => p.z));
                for (var i = 0; i < lots.Count; i++)
                {
                    var b = bounds[i];
                    if (wb.z < b.x - 2f || wb.x > b.z + 2f || wb.w < b.y - 2f || wb.y > b.w + 2f)
                        continue;
                    // Most of its length inside the lot: aisles are often drawn out to the street they open onto.
                    if (FractionInside(way.Points, lots[i].Ring) >= 0.6f)
                    {
                        way.IsInsideParkingLot = true;
                        marked++;
                        break;
                    }
                }
            }
            if (marked > 0)
                Mod.log.Info($"{marked} OSM parking aisles lie inside surface parking lots (replaced by the generated lot layout).");
        }

        /// <summary>Share of a polyline's length that lies inside the ring (or within 2 m of its edge).</summary>
        private static float FractionInside(List<float3> points, List<float2> ring)
        {
            float total = 0f, inside = 0f;
            for (var i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i].xz;
                var b = points[i + 1].xz;
                var len = math.distance(a, b);
                var steps = math.max(1, (int)math.ceil(len));
                for (var k = 0; k < steps; k++)
                {
                    var p = math.lerp(a, b, (k + 0.5f) / steps);
                    if (ParkingGeometry.Contains(ring, p) || ParkingGeometry.DistanceToRing(ring, p) < 2f)
                        inside += len / steps;
                }
                total += len;
            }
            return total > 0f ? inside / total : 0f;
        }

        private static Dictionary<string, string> ReadTags(XmlElement element)
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var list = element.SelectNodes("tag");
            if (list == null)
                return tags;
            foreach (XmlElement tag in list)
                tags[tag.GetAttribute("k")] = tag.GetAttribute("v");
            return tags;
        }

        private static List<long> ReadNodeRefs(XmlElement wayElement)
        {
            var ids = new List<long>();
            var nds = wayElement.SelectNodes("nd");
            if (nds == null)
                return ids;
            foreach (XmlElement nd in nds)
                ids.Add(ReadLong(nd, "ref"));
            return ids;
        }

        /// <summary>Joins multipolygon member ways end to end into closed rings; open leftovers are dropped.</summary>
        private static List<List<long>> AssembleRings(List<List<long>> parts)
        {
            var rings = new List<List<long>>();
            var open = parts.Where(p => p.Count >= 2).Select(p => new List<long>(p)).ToList();
            for (var i = open.Count - 1; i >= 0; i--)
            {
                if (open[i][0] == open[i][open[i].Count - 1])
                {
                    rings.Add(open[i]);
                    open.RemoveAt(i);
                }
            }

            while (open.Count > 0)
            {
                var ring = open[0];
                open.RemoveAt(0);
                var progress = true;
                while (ring[0] != ring[ring.Count - 1] && progress)
                {
                    progress = false;
                    for (var i = 0; i < open.Count; i++)
                    {
                        var part = open[i];
                        var end = ring[ring.Count - 1];
                        if (part[0] == end)
                            ring.AddRange(part.Skip(1));
                        else if (part[part.Count - 1] == end)
                            ring.AddRange(Enumerable.Reverse(part).Skip(1));
                        else
                            continue;
                        open.RemoveAt(i);
                        progress = true;
                        break;
                    }
                }
                if (ring.Count >= 4 && ring[0] == ring[ring.Count - 1])
                    rings.Add(ring);
            }
            return rings;
        }
    }
}
