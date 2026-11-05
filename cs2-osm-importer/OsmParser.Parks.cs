using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal static partial class OsmParser
    {
        private const float MinParkSquareMeters = 50f;

        /// <summary>
        /// Reads leisure=park outlines (closed ways and multipolygon outer rings) and the natural=wood /
        /// landuse=forest outlines inside them. Individual OSM trees are deliberately not read (not reliable).
        /// </summary>
        private static void ReadParks(XmlDocument xml, XmlNodeList wayElements, Dictionary<long, GeoNode> nodes, HashSet<long> roadNodeIds, OsmDocument document, OsmImportOptions options)
        {
            var scale = options.ScaleFactor;
            var projected = new HashSet<long>(roadNodeIds);
            float2? Position(long nodeId)
            {
                if (!nodes.TryGetValue(nodeId, out var node))
                    return null;
                if (projected.Add(nodeId))
                    Project(node, document.CenterLat, document.CenterLon);
                return new float2((float)(node.X * scale), (float)(node.Y * scale));
            }

            List<float2> Ring(List<long> ids)
            {
                var points = new List<float2>();
                foreach (var id in ids)
                {
                    var p = Position(id);
                    if (!p.HasValue)
                        continue;
                    if (points.Count > 0 && math.distance(points[points.Count - 1], p.Value) < MinParkingRingPointSpacing)
                        continue;
                    points.Add(p.Value);
                }
                while (points.Count > 1 && math.distance(points[0], points[points.Count - 1]) < MinParkingRingPointSpacing)
                    points.RemoveAt(points.Count - 1);
                if (points.Count < 3)
                    return null;
                if (ParkingGeometry.SignedArea(points) < 0f)
                    points.Reverse();
                return points;
            }

            var woods = new List<List<float2>>();
            Dictionary<long, XmlElement> wayById = null;
            Dictionary<long, XmlElement> WayById()
            {
                if (wayById == null)
                {
                    wayById = new Dictionary<long, XmlElement>();
                    foreach (XmlElement w in wayElements)
                        wayById[ReadLong(w, "id")] = w;
                }
                return wayById;
            }

            void Take(long id, Dictionary<string, string> tags, List<List<long>> rings)
            {
                var isPark = tags.TryGetValue("leisure", out var leisure) && leisure == "park";
                var isWood = (tags.TryGetValue("natural", out var natural) && natural == "wood") ||
                             (tags.TryGetValue("landuse", out var landuse) && landuse == "forest");
                if (!isPark && !isWood)
                    return;
                foreach (var ids in rings)
                {
                    var ring = Ring(ids);
                    if (ring == null)
                        continue;
                    var area = ParkingGeometry.SignedArea(ring);
                    if (isWood)
                    {
                        woods.Add(ring);
                        continue;
                    }
                    if (area < MinParkSquareMeters)
                        continue;
                    var park = new OsmParkArea
                    {
                        Id = id,
                        Name = tags.TryGetValue("name", out var name) ? name : string.Empty,
                        AreaSquareMeters = area,
                    };
                    park.Ring.AddRange(ring);
                    document.Parks.Add(park);
                }
            }

            foreach (XmlElement wayElement in wayElements)
            {
                var tags = ReadTags(wayElement);
                var ids = ReadNodeRefs(wayElement);
                if (ids.Count < 4 || ids[0] != ids[ids.Count - 1])
                    continue;
                Take(ReadLong(wayElement, "id"), tags, new List<List<long>> { ids });
            }

            var relations = xml.SelectNodes("/osm/relation");
            if (relations != null)
            {
                foreach (XmlElement relation in relations)
                {
                    var tags = ReadTags(relation);
                    if (!tags.TryGetValue("type", out var type) || type != "multipolygon")
                        continue;
                    if (!(tags.TryGetValue("leisure", out var l) && l == "park") &&
                        !(tags.TryGetValue("natural", out var n) && n == "wood") &&
                        !(tags.TryGetValue("landuse", out var lu) && lu == "forest"))
                        continue;
                    var outerParts = new List<List<long>>();
                    foreach (XmlElement member in relation.SelectNodes("member"))
                    {
                        if (member.GetAttribute("type") != "way" || member.GetAttribute("role") == "inner")
                            continue;
                        if (WayById().TryGetValue(ReadLong(member, "ref"), out var memberWay))
                            outerParts.Add(ReadNodeRefs(memberWay));
                    }
                    Take(ReadLong(relation, "id"), tags, AssembleRings(outerParts));
                }
            }

            // Woods belong to the park containing them (by centroid).
            var attached = 0;
            foreach (var wood in woods)
            {
                var c = ParkingGeometry.Centroid(wood);
                var park = document.Parks.FirstOrDefault(p => ParkingGeometry.Contains(p.Ring, c));
                if (park == null)
                    continue;
                park.Woods.Add(wood);
                attached++;
            }

            if (document.Parks.Count > 0)
                Mod.log.Info($"Read {document.Parks.Count} OSM parks ({document.Parks.Count(p => p.AreaSquareMeters >= 40000f)} of 4 ha or more) with {attached} wooded areas inside them.");
        }
    }
}
