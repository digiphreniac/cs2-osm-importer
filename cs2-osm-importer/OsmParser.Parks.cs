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

        private static readonly HashSet<string> NonLakeWater = new HashSet<string>
        {
            "river", "stream", "canal", "ditch", "drain", "wastewater", "fountain", "stream_pool", "rapids", "lock", "fish_pass", "tidal_channel",
        };

        /// <summary>Standing water the importer fills: lakes, ponds, reservoirs. Not rivers or canals (they slope).</summary>
        private static bool IsLake(Dictionary<string, string> tags)
        {
            if (tags.ContainsKey("highway") || tags.ContainsKey("building") || tags.ContainsKey("leisure"))
                return false;
            if (tags.TryGetValue("intermittent", out var intermittent) && intermittent == "yes")
                return false;
            if (tags.TryGetValue("natural", out var natural) && natural == "water")
                return !(tags.TryGetValue("water", out var kind) && NonLakeWater.Contains(kind));
            return tags.TryGetValue("landuse", out var landuse) && landuse == "reservoir";
        }

        private static bool IsWater(Dictionary<string, string> tags) =>
            (tags.TryGetValue("natural", out var natural) && (natural == "water" || natural == "wetland")) ||
            (tags.TryGetValue("water", out _) && !tags.ContainsKey("highway")) ||
            (tags.TryGetValue("landuse", out var landuse) && (landuse == "reservoir" || landuse == "basin")) ||
            (tags.TryGetValue("leisure", out var leisure) && leisure == "swimming_pool");

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

            var waters = new List<List<float2>>();

            void TakeLake(long id, Dictionary<string, string> tags, List<List<long>> rings, List<List<long>> holes)
            {
                var holeRings = (holes ?? new List<List<long>>()).Select(Ring).Where(h => h != null).ToList();
                foreach (var ids in rings)
                {
                    var ring = Ring(ids);
                    if (ring == null)
                        continue;
                    var area = ParkingGeometry.SignedArea(ring);
                    var centroid = ParkingGeometry.Centroid(ring);
                    // The same lake tagged on its outline way and on its multipolygon.
                    if (document.Lakes.Any(l => math.abs(l.AreaSquareMeters - area) < area * 0.01f && math.distance(ParkingGeometry.Centroid(l.Ring), centroid) < 5f))
                        continue;
                    var lake = new OsmLake
                    {
                        Id = id,
                        Name = tags.TryGetValue("name", out var name) ? name : string.Empty,
                        AreaSquareMeters = area,
                    };
                    lake.Ring.AddRange(ring);
                    foreach (var h in holeRings)
                    {
                        if (ParkingGeometry.Contains(ring, ParkingGeometry.Centroid(h)))
                            lake.Holes.Add(h);
                    }
                    document.Lakes.Add(lake);
                }
            }

            void Take(long id, Dictionary<string, string> tags, List<List<long>> rings, List<List<long>> holes = null)
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
                    foreach (var hole in holes ?? new List<List<long>>())
                    {
                        var h = Ring(hole);
                        if (h != null && ParkingGeometry.Contains(ring, ParkingGeometry.Centroid(h)))
                            park.Obstacles.Add(h);
                    }
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
                if (IsLake(tags))
                    TakeLake(ReadLong(wayElement, "id"), tags, new List<List<long>> { ids }, null);
                if (IsWater(tags))
                {
                    var w = Ring(ids);
                    if (w != null)
                        waters.Add(w);
                }
            }

            var relations = xml.SelectNodes("/osm/relation");
            if (relations != null)
            {
                foreach (XmlElement relation in relations)
                {
                    var tags = ReadTags(relation);
                    if (!tags.TryGetValue("type", out var type) || type != "multipolygon")
                        continue;
                    var water = IsWater(tags);
                    if (!water &&
                        !(tags.TryGetValue("leisure", out var l) && l == "park") &&
                        !(tags.TryGetValue("natural", out var n) && n == "wood") &&
                        !(tags.TryGetValue("landuse", out var lu) && lu == "forest"))
                        continue;
                    var outerParts = new List<List<long>>();
                    var innerParts = new List<List<long>>();
                    foreach (XmlElement member in relation.SelectNodes("member"))
                    {
                        if (member.GetAttribute("type") != "way")
                            continue;
                        if (WayById().TryGetValue(ReadLong(member, "ref"), out var memberWay))
                            (member.GetAttribute("role") == "inner" ? innerParts : outerParts).Add(ReadNodeRefs(memberWay));
                    }
                    if (water)
                    {
                        if (IsLake(tags))
                            TakeLake(ReadLong(relation, "id"), tags, AssembleRings(outerParts), AssembleRings(innerParts));
                        foreach (var ids in AssembleRings(outerParts))
                        {
                            var w = Ring(ids);
                            if (w != null)
                                waters.Add(w);
                        }
                        continue;
                    }
                    Take(ReadLong(relation, "id"), tags, AssembleRings(outerParts), AssembleRings(innerParts));
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

            var waterInParks = 0;
            foreach (var w in waters)
            {
                var c = ParkingGeometry.Centroid(w);
                foreach (var park in document.Parks)
                {
                    if (ParkingGeometry.Contains(park.Ring, c) || w.Any(p => ParkingGeometry.Contains(park.Ring, p)))
                    {
                        park.Obstacles.Add(w);
                        waterInParks++;
                    }
                }
            }

            ReadParkFeatures(wayElements, nodes, document, Ring, Position);

            if (document.Parks.Count > 0)
                Mod.log.Info($"Read {document.Parks.Count} OSM parks ({document.Parks.Count(p => p.AreaSquareMeters >= 40000f)} of 4 ha or more) with {attached} wooded areas and {waterInParks} water areas or holes inside them ({document.Parks.Sum(p => p.Obstacles.Count)} kept clear).");
        }

        // Typical sizes (Seattle medians) for a playground or dog park mapped as a point.
        private const float PointPlaygroundSquareMeters = 280f;
        private const float PointDogParkSquareMeters = 800f;

        /// <summary>
        /// Public leisure=playground / leisure=dog_park areas and points. Each is attached to the park it lies in;
        /// a public dog park outside any park becomes a small park of its own. Private ones (access=private,
        /// customers, students) and playgrounds outside parks (schools, apartment grounds: not imported yet) are left
        /// out and counted.
        /// </summary>
        private static void ReadParkFeatures(XmlNodeList wayElements, Dictionary<long, GeoNode> nodes, OsmDocument document, Func<List<long>, List<float2>> ring, Func<long, float2?> position)
        {
            var features = new List<(OsmParkFeature Feature, string Name)>();
            int privateOnes = 0;

            bool Public(Dictionary<string, string> tags)
            {
                if (tags.TryGetValue("access", out var access) && (access == "private" || access == "customers" || access == "students" || access == "no"))
                {
                    privateOnes++;
                    return false;
                }
                return true;
            }

            OsmParkFeatureKind? KindOf(Dictionary<string, string> tags)
            {
                if (!tags.TryGetValue("leisure", out var leisure))
                    return null;
                return leisure == "playground" ? OsmParkFeatureKind.Playground : leisure == "dog_park" ? OsmParkFeatureKind.DogPark : (OsmParkFeatureKind?)null;
            }

            foreach (XmlElement wayElement in wayElements)
            {
                var tags = ReadTags(wayElement);
                var kind = KindOf(tags);
                if (kind == null || !Public(tags))
                    continue;
                var ids = ReadNodeRefs(wayElement);
                if (ids.Count < 4 || ids[0] != ids[ids.Count - 1])
                    continue;
                var r = ring(ids);
                if (r == null)
                    continue;
                var f = new OsmParkFeature { Id = ReadLong(wayElement, "id"), Kind = kind.Value, Center = ParkingGeometry.Centroid(r), AreaSquareMeters = ParkingGeometry.SignedArea(r) };
                f.Ring.AddRange(r);
                features.Add((f, tags.TryGetValue("name", out var n) ? n : string.Empty));
            }

            foreach (var pair in nodes)
            {
                var kind = KindOf(pair.Value.Tags);
                if (kind == null || !Public(pair.Value.Tags))
                    continue;
                var p = position(pair.Key);
                if (!p.HasValue)
                    continue;
                // Skip a point that only repeats a mapped outline.
                if (features.Any(x => x.Feature.Kind == kind.Value && ParkingGeometry.Contains(x.Feature.Ring, p.Value)))
                    continue;
                var area = kind == OsmParkFeatureKind.Playground ? PointPlaygroundSquareMeters : PointDogParkSquareMeters;
                var h = math.sqrt(area) * 0.5f;
                var f = new OsmParkFeature { Id = pair.Key, Kind = kind.Value, FromPoint = true, Center = p.Value, AreaSquareMeters = area };
                f.Ring.AddRange(new[] { p.Value + new float2(-h, -h), p.Value + new float2(h, -h), p.Value + new float2(h, h), p.Value + new float2(-h, h) });
                features.Add((f, pair.Value.Tags.TryGetValue("name", out var n) ? n : string.Empty));
            }

            int inParks = 0, standaloneDogParks = 0, playgroundsOutside = 0;
            foreach (var (feature, name) in features)
            {
                var park = document.Parks.Where(x => ParkingGeometry.Contains(x.Ring, feature.Center)).OrderBy(x => x.AreaSquareMeters).FirstOrDefault();
                if (park != null)
                {
                    park.Features.Add(feature);
                    inParks++;
                    continue;
                }
                if (feature.Kind == OsmParkFeatureKind.Playground)
                {
                    playgroundsOutside++;
                    continue;
                }
                // A public dog park on its own: a small park of its own.
                var own = new OsmParkArea { Id = feature.Id, Name = name, AreaSquareMeters = feature.AreaSquareMeters, IsStandaloneFeature = true };
                own.Ring.AddRange(feature.Ring);
                own.Features.Add(feature);
                document.Parks.Add(own);
                standaloneDogParks++;
            }

            if (features.Count + privateOnes > 0)
                Mod.log.Info($"Read {features.Count} public playgrounds and dog parks: {inParks} inside parks, {standaloneDogParks} standalone dog parks (built as small parks), " +
                             $"{playgroundsOutside} playgrounds outside parks (schools, apartment grounds: not imported yet); {privateOnes} private ones left out.");
        }
    }
}
