using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal static partial class OsmParser
    {
        private const double EarthRadiusMeters = 6378137d;
        private const float MaxTerrainSegmentLengthMeters = 15f;
        private const float RampLengthMeters = 60f;
        private const float SidewalkSpatialBucketSizeMeters = 64f;

        // Must match OsmImportSystem.MinSegmentLengthMeters: shorter segments are not queued.
        private const float MinSegmentLengthMeters = 1f;

        /// <summary>
        /// Gives road-network nodes closer than minDistance (projected units) one shared position. The importer does not
        /// queue segments shorter than 1 m, and skipping one left a gap: the way's next segment started a fraction of a
        /// metre from where its neighbour ended, so CS2 built two unconnected nodes (63 of 99 same-height junction
        /// failures on the UW campus crop, e.g. a stop-line node 0.57 m from a junction). Merged nodes produce
        /// zero-length segments instead, which are skipped without leaving a gap.
        /// Consecutive nodes of one way always merge; nodes of different ways merge only when neither is on a
        /// bridge or (underground) tunnel, so a structure is never joined to the road under it.
        /// </summary>
        private static void MergeCloseRoadNodes(Dictionary<long, GeoNode> nodes, List<XmlElement> wayElements, HashSet<long> roadNodeIds, double minDistance)
        {
            var parent = new Dictionary<long, long>();
            long Find(long x) { while (parent.TryGetValue(x, out var p) && p != x) x = p; return x; }
            void Union(long a, long b)
            {
                if (!parent.ContainsKey(a)) parent[a] = a;
                if (!parent.ContainsKey(b)) parent[b] = b;
                var ra = Find(a);
                var rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            double Dist(GeoNode a, GeoNode b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

            var useCount = new Dictionary<long, int>();
            var structural = new HashSet<long>();
            foreach (var way in wayElements)
            {
                var ids = new List<long>();
                var nds = way.SelectNodes("nd");
                if (nds == null)
                    continue;
                foreach (XmlElement nd in nds)
                    ids.Add(ReadLong(nd, "ref"));

                var isStructure = false;
                var tags = way.SelectNodes("tag");
                if (tags != null)
                {
                    foreach (XmlElement tag in tags)
                    {
                        var key = tag.GetAttribute("k");
                        var value = tag.GetAttribute("v");
                        // Same rule as EstimateBaseElevationOffset: only real bridges and tunnels leave the ground.
                        if (key == "bridge" && IsBridgeValue(value) || key == "tunnel" && IsTunnelValue(value))
                            isStructure = true;
                    }
                }

                foreach (var id in ids.Distinct())
                {
                    useCount[id] = useCount.TryGetValue(id, out var c) ? c + 1 : 1;
                    if (isStructure)
                        structural.Add(id);
                }

                for (var i = 1; i < ids.Count; i++)
                {
                    if (ids[i] != ids[i - 1] && nodes.TryGetValue(ids[i - 1], out var a) && nodes.TryGetValue(ids[i], out var b) && Dist(a, b) < minDistance)
                        Union(ids[i - 1], ids[i]);
                }
            }

            // Nodes of different (non-structural) ways within minDistance: grid search.
            var cell = Math.Max(minDistance, 1e-6);
            var grid = new Dictionary<(long, long), List<long>>();
            foreach (var id in roadNodeIds)
            {
                if (structural.Contains(id) || !nodes.TryGetValue(id, out var n))
                    continue;
                var key = ((long)Math.Floor(n.X / cell), (long)Math.Floor(n.Y / cell));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<long>();
                list.Add(id);
            }
            foreach (var kv in grid)
            {
                for (var dx = 0; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy < 0)
                            continue;
                        if (!grid.TryGetValue((kv.Key.Item1 + dx, kv.Key.Item2 + dy), out var other))
                            continue;
                        foreach (var a in kv.Value)
                            foreach (var b in other)
                                if (a < b || (dx != 0 || dy != 0))
                                    if (a != b && Dist(nodes[a], nodes[b]) < minDistance)
                                        Union(a, b);
                    }
            }

            // Each group takes the position of its most-used node (the junction), so junctions do not move.
            var groups = new Dictionary<long, List<long>>();
            foreach (var id in parent.Keys.ToList())
            {
                var root = Find(id);
                if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<long>();
                g.Add(id);
            }
            var moved = 0;
            foreach (var g in groups.Values)
            {
                var members = g.Where(nodes.ContainsKey).Distinct().ToList();
                if (members.Count < 2)
                    continue;
                var anchor = members.OrderByDescending(id => useCount.TryGetValue(id, out var c) ? c : 0).ThenBy(id => id).First();
                foreach (var id in members)
                {
                    if (id == anchor)
                        continue;
                    nodes[id].X = nodes[anchor].X;
                    nodes[id].Y = nodes[anchor].Y;
                    moved++;
                }
            }
            if (moved > 0)
                Mod.log.Info($"Merged {moved} road nodes into a node closer than {MinSegmentLengthMeters} m (avoids dropped tiny segments leaving gaps).");
        }

        private sealed class GeoNode
        {
            public double Lat;
            public double Lon;
            public double X;
            public double Y;
            // Most nodes have no tags: they share this empty, never-written dictionary instead of one each.
            public Dictionary<string, string> Tags = NoTags;

            public static readonly Dictionary<string, string> NoTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static OsmDocument Load(
            OsmImportOptions options,
            System.Func<float3, float> sampleHeight = null,
            float3 worldOffset = default,
            double? anchorLatitude = null,
            double? anchorLongitude = null)
        {
            if (string.IsNullOrWhiteSpace(options.Path))
                throw new ArgumentException("Choose an .osm file before importing.");

            if (!File.Exists(options.Path))
                throw new FileNotFoundException("OSM file was not found.", options.Path);

            var priorityRoadNames = ReadPriorityRoadNames(options.PriorityRoadNamesPath);
            var timer = new ParseTimer();

            var xml = new XmlDocument { XmlResolver = null };
            var nodes = ReadOsmFile(options.Path, xml);
            timer.Mark("read");
            if (nodes.Count == 0)
                throw new InvalidDataException("The OSM file does not contain any nodes.");

            var fileBoundsCenter = TryGetFileBoundsCenter(xml);

            var document = new OsmDocument
            {
                NodeCount = nodes.Count,
                ElevationMeters = options.ElevationMeters,
                OriginOffset = float3.zero
            };
            var wayElements = xml.SelectNodes("/osm/way");
            if (wayElements == null)
                return document;

            var supportedWays = new List<XmlElement>();
            var roadNodeIds = new HashSet<long>();
            Dictionary<long, List<OsmTurnRestriction>> turnRestrictionsByWayId;

            foreach (XmlElement wayElement in wayElements)
            {
                if (!TryReadSupportedRoadWay(wayElement, options.UseImportHighwayTypesFilter, options.ImportHighwayTypes, out var highway, out var isArea))
                    continue;

                var nds = wayElement.SelectNodes("nd");
                if (nds == null)
                    continue;

                supportedWays.Add(wayElement);
                foreach (XmlElement nd in nds)
                {
                    var nodeId = ReadLong(nd, "ref");
                    if (nodes.ContainsKey(nodeId))
                        roadNodeIds.Add(nodeId);
                }
            }

            timer.Mark("wayScan");
            AddPierWays(xml, wayElements, nodes, supportedWays, roadNodeIds,
                !options.UseImportHighwayTypesFilter || (options.ImportHighwayTypes != null && options.ImportHighwayTypes.Contains("footway")));
            timer.Mark("piers");

            if (roadNodeIds.Count == 0)
                return document;

            var roadNodes = new List<GeoNode>(roadNodeIds.Count);
            foreach (var nodeId in roadNodeIds)
            {
                if (!nodes.TryGetValue(nodeId, out var node))
                    continue;

                roadNodes.Add(node);
            }

            if (roadNodes.Count == 0)
                return document;

            if (anchorLatitude.HasValue && anchorLongitude.HasValue)
            {
                foreach (var nodeId in roadNodeIds)
                {
                    if (nodes.TryGetValue(nodeId, out var node))
                        Project(node, anchorLatitude.Value, anchorLongitude.Value);
                }

                document.CenterLat = anchorLatitude.Value;
                document.CenterLon = anchorLongitude.Value;
            }
            else if (fileBoundsCenter.HasValue)
            {
                var boundsCenter = fileBoundsCenter.Value;
                foreach (var nodeId in roadNodeIds)
                {
                    if (nodes.TryGetValue(nodeId, out var node))
                        Project(node, boundsCenter.Lat, boundsCenter.Lon);
                }

                document.CenterLat = boundsCenter.Lat;
                document.CenterLon = boundsCenter.Lon;
            }
            else
            {
                var center = GetCenter(roadNodes);
                foreach (var nodeId in roadNodeIds)
                {
                    if (nodes.TryGetValue(nodeId, out var node))
                        Project(node, center.Lat, center.Lon);
                }

                document.CenterLat = center.Lat;
                document.CenterLon = center.Lon;
            }

            timer.Mark("project");
            MergeCloseRoadNodes(nodes, supportedWays, roadNodeIds, MinSegmentLengthMeters / Math.Max(1e-6f, options.ScaleFactor));
            timer.Mark("mergeNodes");

            foreach (var nodeId in roadNodeIds)
            {
                if (nodes.TryGetValue(nodeId, out var node))
                {
                    document.SourceNodePositions[nodeId] = new float2(
                        (float)(node.X * options.ScaleFactor),
                        (float)(node.Y * options.ScaleFactor));
                }
            }

            turnRestrictionsByWayId = ReadTurnRestrictions(xml, nodes, options, sampleHeight);
            timer.Mark("turnRestrictions");

            var parsedWays = new List<OsmWay>();
            foreach (var wayElement in supportedWays)
            {
                if (TryReadRoadWay(wayElement, nodes, options, sampleHeight, worldOffset, turnRestrictionsByWayId, out var way))
                    parsedWays.Add(way);
            }
            timer.Mark("readWays");

            parsedWays = AttachSidewalkWays(parsedWays, out var attachedSidewalkCount);
            if (attachedSidewalkCount > 0)
                Mod.log.Info($"Attached {attachedSidewalkCount} footway sidewalk segments to eligible roadway segments.");

            var mergedWays = MergeSimpleConnectedWays(parsedWays, out var mergedWayCount);
            if (mergedWayCount > 0)
                Mod.log.Info($"Merged {mergedWayCount} connected OSM ways into simpler chains before import.");
            var finalWays = mergedWayCount > 0 ? mergedWays : parsedWays;
            timer.Mark("sidewalksAndChains");
            // Grade-limited transitions over the whole network (replaces HarmonizeGroupElevations' 60 m ramps).
            ApplyGradeLimitedElevations(finalWays);
            timer.Mark("harmonize");
            ConvertRoundabouts(finalWays, sampleHeight);
            timer.Mark("roundabouts");
            finalWays = ReplaceMappedPedestrianDetails(finalWays, document.SourceNodePositions,
                id => nodes.TryGetValue(id, out var n) && IsUnmarkedCrossingNode(n.Tags));
            RelocatePathJoins(finalWays);
            SquarePathApproaches(finalWays);
            timer.Mark("pedestrianDetails");
            AnchorSharedNodeHeights(finalWays, document.SourceNodePositions);
            UnifySharedPoints(finalWays);
            timer.Mark("sharedPoints");
            ReadParkingAreas(xml, wayElements, nodes, roadNodeIds, document, options);
            timer.Mark("parking");
            ReadParks(xml, wayElements, nodes, roadNodeIds, document, options);
            timer.Mark("parks");
            MarkAislesInsideParkingLots(finalWays, document.ParkingAreas);
            finalWays = PrioritizeWays(finalWays, priorityRoadNames);
            timer.Mark("aislesAndPriority");
            // In game only (terrain sampled): the 15 m terrain split is what adds most of the removable points.
            if (sampleHeight != null)
                SimplifyRoadPoints(finalWays, options.SimplifyPlanToleranceMeters, options.SimplifyHeightToleranceMeters);
            ClearJunctionApproaches(finalWays);
            timer.Mark("simplify");
            document.Ways.AddRange(finalWays);

            if (document.Ways.Count > Math.Max(1, options.MaxWays))
                document.Ways.RemoveRange(Math.Max(1, options.MaxWays), document.Ways.Count - Math.Max(1, options.MaxWays));

            foreach (var way in document.Ways)
                IncludeBounds(document, way);

            document.InferredLaneCount = NormalizeContiguousLaneCounts(document.Ways);
            timer.Mark("lanes");
            Mod.log.Info(timer.Summary());
            return document;
        }

        /// <summary>Wall time and managed heap after each parse phase, logged once per load to find slow phases on large imports.</summary>
        private sealed class ParseTimer
        {
            private readonly System.Diagnostics.Stopwatch _total = System.Diagnostics.Stopwatch.StartNew();
            private readonly System.Text.StringBuilder _phases = new System.Text.StringBuilder();
            private double _last;

            public void Mark(string phase)
            {
                var now = _total.Elapsed.TotalSeconds;
                _phases.Append($" {phase}={now - _last:F1}s");
                _last = now;
            }

            public string Summary()
            {
                var heapMb = GC.GetTotalMemory(false) / (1024 * 1024);
                return $"OSM parse timing: total={_total.Elapsed.TotalSeconds:F1}s (managed heap {heapMb} MB at end):{_phases}";
            }
        }

        /// <summary>
        /// Streams the OSM file. Nodes, the bulk of any OSM file, go straight into GeoNodes and never become XML
        /// elements: as a full XmlDocument, full Seattle (1.7 million nodes) took 4.5 GB and most of the parse time.
        /// Everything else (bounds, ways, relations) goes into <paramref name="xml"/> under an osm root, in file
        /// order, for the passes that query it.
        /// </summary>
        /// <summary>
        /// Way tag keys some pass selects ways by (roads and rails, piers, parking, parks, woods, water). Other keys
        /// (building, address, name...) are only read on ways that have one of these as well.
        /// </summary>
        private static readonly HashSet<string> UsedWayKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "highway", "railway", "leisure", "natural", "landuse", "man_made", "water", "amenity", "parking",
        };

        /// <summary>A way read without building its XML element yet: attributes and child elements, in file order.</summary>
        private sealed class PendingWay
        {
            public List<(string Name, string Value)> Attributes;
            public List<(string Name, List<(string Name, string Value)> Attributes)> Children;
            public long Id;
        }

        private static List<(string Name, string Value)> ReadAttributes(XmlReader reader)
        {
            var list = new List<(string, string)>(reader.AttributeCount);
            if (reader.MoveToFirstAttribute())
            {
                do
                    list.Add((reader.Name, reader.Value));
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            return list;
        }

        private static XmlElement BuildElement(XmlDocument xml, string name, List<(string Name, string Value)> attributes)
        {
            var element = xml.CreateElement(name);
            foreach (var (n, v) in attributes)
                element.SetAttribute(n, v);
            return element;
        }

        /// <summary>
        /// Reads one way (the reader on its start tag; leaves it past the end tag). A way with a used key is built as an
        /// XML element at once; any other way is returned as a PendingWay.
        /// </summary>
        private static object ReadWay(XmlReader reader, XmlDocument xml, int depth)
        {
            var way = new PendingWay
            {
                Attributes = ReadAttributes(reader),
                Children = new List<(string, List<(string, string)>)>(),
            };
            long.TryParse(reader.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out way.Id);
            var used = false;
            if (reader.IsEmptyElement)
            {
                reader.Read();
            }
            else
            {
                reader.Read();
                while (!reader.EOF && reader.Depth > depth)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
                    {
                        var name = reader.Name;
                        var attributes = ReadAttributes(reader);
                        way.Children.Add((name, attributes));
                        if (!used && name == "tag")
                        {
                            foreach (var (n, v) in attributes)
                            {
                                if (n == "k" && UsedWayKeys.Contains(v))
                                    used = true;
                            }
                        }
                    }
                    reader.Read();
                }
                // Past </way>.
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    reader.Read();
            }
            return used ? BuildWay(xml, way) : (object)way;
        }

        private static XmlElement BuildWay(XmlDocument xml, PendingWay way)
        {
            var element = BuildElement(xml, "way", way.Attributes);
            foreach (var (name, attributes) in way.Children)
                element.AppendChild(BuildElement(xml, name, attributes));
            return element;
        }

        private static Dictionary<long, GeoNode> ReadOsmFile(string path, XmlDocument xml)
        {
            var nodes = new Dictionary<long, GeoNode>();
            var root = xml.CreateElement("osm");
            xml.AppendChild(root);
            // Top-level elements other than nodes, in file order: built elements, or ways without a used key, which
            // are only built if a relation lists them (multipolygon outlines are usually untagged ways). Buildings,
            // the bulk of a city's ways, never become XML elements.
            var ordered = new List<object>();
            var relationWayIds = new HashSet<long>();

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using (var reader = XmlReader.Create(path, settings))
            {
                if (reader.MoveToContent() != XmlNodeType.Element || reader.IsEmptyElement)
                    return nodes;
                var childDepth = reader.Depth + 1;
                reader.Read();
                while (!reader.EOF)
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Depth != childDepth)
                    {
                        reader.Read();
                        continue;
                    }

                    if (reader.Name == "way")
                    {
                        ordered.Add(ReadWay(reader, xml, childDepth));
                        continue;
                    }

                    if (reader.Name != "node")
                    {
                        // Moves the reader past the element.
                        var element = xml.ReadNode(reader);
                        if (element is XmlElement relation && relation.Name == "relation")
                        {
                            foreach (XmlNode child in relation.ChildNodes)
                            {
                                if (child is XmlElement member && member.Name == "member" && member.GetAttribute("type") == "way" &&
                                    long.TryParse(member.GetAttribute("ref"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wayId))
                                    relationWayIds.Add(wayId);
                            }
                        }
                        ordered.Add(element);
                        continue;
                    }

                    var id = long.Parse(reader.GetAttribute("id"), CultureInfo.InvariantCulture);
                    var node = new GeoNode
                    {
                        Lat = double.Parse(reader.GetAttribute("lat"), CultureInfo.InvariantCulture),
                        Lon = double.Parse(reader.GetAttribute("lon"), CultureInfo.InvariantCulture),
                    };
                    if (reader.IsEmptyElement)
                    {
                        reader.Read();
                    }
                    else
                    {
                        reader.Read();
                        while (!reader.EOF && reader.Depth > childDepth)
                        {
                            if (reader.NodeType == XmlNodeType.Element && reader.Name == "tag")
                            {
                                if (ReferenceEquals(node.Tags, GeoNode.NoTags))
                                    node.Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                node.Tags[reader.GetAttribute("k") ?? string.Empty] = reader.GetAttribute("v") ?? string.Empty;
                            }
                            reader.Read();
                        }
                        // Past </node>.
                        if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == childDepth)
                            reader.Read();
                    }
                    nodes[id] = node;
                }
            }

            foreach (var item in ordered)
            {
                if (item is PendingWay pending)
                {
                    if (relationWayIds.Contains(pending.Id))
                        root.AppendChild(BuildWay(xml, pending));
                }
                else if (item is XmlNode element)
                {
                    root.AppendChild(element);
                }
            }

            return nodes;
        }

        private static Dictionary<long, List<OsmTurnRestriction>> ReadTurnRestrictions(
            XmlDocument xml,
            Dictionary<long, GeoNode> nodes,
            OsmImportOptions options,
            System.Func<float3, float> sampleHeight)
        {
            var result = new Dictionary<long, List<OsmTurnRestriction>>();
            var relations = xml.SelectNodes("/osm/relation");
            if (relations == null)
                return result;

            foreach (XmlElement relation in relations)
            {
                var tags = relation.SelectNodes("tag");
                var isRestriction = false;
                var restriction = string.Empty;
                if (tags != null)
                {
                    foreach (XmlElement tag in tags)
                    {
                        var key = tag.GetAttribute("k");
                        var value = tag.GetAttribute("v");
                        if (key == "restriction")
                            restriction = value ?? string.Empty;

                        if ((key == "type" && value == "restriction") ||
                            (key == "restriction" && value.StartsWith("no_", StringComparison.OrdinalIgnoreCase)) ||
                            (key == "restriction" && value.StartsWith("only_", StringComparison.OrdinalIgnoreCase)))
                        {
                            isRestriction = true;
                            break;
                        }
                    }
                }

                if (!isRestriction)
                    continue;

                var members = relation.SelectNodes("member");
                if (members == null)
                    continue;

                var viaNodeId = 0L;
                var wayIds = new List<long>();
                foreach (XmlElement member in members)
                {
                    var type = member.GetAttribute("type");
                    var role = member.GetAttribute("role");
                    if (string.Equals(type, "node", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(role, "via", StringComparison.OrdinalIgnoreCase))
                    {
                        viaNodeId = ReadLong(member, "ref");
                        continue;
                    }

                    if (!string.Equals(type, "way", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.Equals(role, "from", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(role, "to", StringComparison.OrdinalIgnoreCase))
                        wayIds.Add(ReadLong(member, "ref"));
                }

                if (!TryCreateTurnRestriction(restriction, viaNodeId, nodes, options, sampleHeight, out var turnRestriction))
                    continue;

                foreach (var wayId in wayIds)
                {
                    if (!result.TryGetValue(wayId, out var list))
                    {
                        list = new List<OsmTurnRestriction>();
                        result[wayId] = list;
                    }

                    list.Add(turnRestriction);
                }
            }

            return result;
        }

        private static bool TryCreateTurnRestriction(
            string restriction,
            long viaNodeId,
            Dictionary<long, GeoNode> nodes,
            OsmImportOptions options,
            System.Func<float3, float> sampleHeight,
            out OsmTurnRestriction turnRestriction)
        {
            turnRestriction = null;
            var normalized = (restriction ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
                return false;

            var parsed = new OsmTurnRestriction
            {
                ViaNodeId = viaNodeId,
                ForbidLeftTurn = normalized == "no_left_turn" || normalized == "only_right_turn" || normalized == "only_straight_on",
                ForbidRightTurn = normalized == "no_right_turn" || normalized == "only_left_turn" || normalized == "only_straight_on",
                ForbidStraight = normalized == "no_straight_on" || normalized == "only_left_turn" || normalized == "only_right_turn",
                ForbidUTurn = normalized == "no_u_turn",
            };

            if (!parsed.ForbidLeftTurn && !parsed.ForbidRightTurn && !parsed.ForbidStraight && !parsed.ForbidUTurn)
                return false;

            if (viaNodeId != 0 && nodes != null && nodes.TryGetValue(viaNodeId, out var viaNode))
            {
                var viaPosition = new float3(
                    (float)(viaNode.X * options.ScaleFactor),
                    0f,
                    (float)(viaNode.Y * options.ScaleFactor));
                viaPosition.y = sampleHeight != null ? sampleHeight(viaPosition) : options.ElevationMeters;
                parsed.ViaPosition = viaPosition;
            }

            turnRestriction = parsed;
            return true;
        }

        private static bool TryReadRoadWay(
            XmlElement wayElement,
            Dictionary<long, GeoNode> nodes,
            OsmImportOptions options,
            System.Func<float3, float> sampleHeight,
            float3 worldOffset,
            Dictionary<long, List<OsmTurnRestriction>> turnRestrictionsByWayId,
            out OsmWay way)
        {
            way = new OsmWay { Id = ReadLong(wayElement, "id") };
            var isArea = false;
            var pathSurface = string.Empty;
            var pathBicycle = string.Empty;
            var hasLayer = false;
            var layer = 0;
            var isBridge = false;
            var isPier = false;
            var isTunnel = false;
            var oneWayDirection = 0;
            var isCovered = false;
            var isEmbankment = false;
            var isCutting = false;
            var inclineDirection = 0;
            var inclinePercent = 0f;
            var isRoundabout = false;
            var hasExplicitLaneCount = false;
            var forwardLaneCount = 0;
            var backwardLaneCount = 0;
            var bothWaysLaneCount = 0;
            var wayRailway = string.Empty;
            var footwayType = string.Empty;
            var sidewalkTag = string.Empty;
            var sidewalkLeft = false;
            var sidewalkRight = false;
            var parkingLeftTag = string.Empty;
            var parkingRightTag = string.Empty;
            var parkingBothTag = string.Empty;
            var hasBikeLanes = false;
            var bikeLaneSide = OsmRoadFeatureSide.None;
            var hasDesignatedBusLanes = false;
            var designatedBusLaneSide = OsmRoadFeatureSide.None;
            var hasBroadBusDesignation = false;
            var tags = wayElement.SelectNodes("tag");

            if (tags != null)
            {
                foreach (XmlElement tag in tags)
                {
                    var key = tag.GetAttribute("k");
                    var value = tag.GetAttribute("v");

                    if (key == "highway")
                        way.Highway = value;
                    else if (key == "name")
                        way.Name = value;
                    else if (key == "ref")
                        way.Ref = value;
                    else if (key == "service" && value == "parking_aisle")
                        way.IsParkingAisle = true;
                    else if (key == "surface")
                        pathSurface = value;
                    else if (key == "bicycle")
                        pathBicycle = value;
                    else if (key == "lanes" && TryReadLaneCount(value, out var parsedLaneCount))
                    {
                        way.Lanes = parsedLaneCount;
                        hasExplicitLaneCount = true;
                    }
                    else if (key == "lanes:forward" && TryReadLaneCount(value, out var parsedForwardLaneCount))
                    {
                        forwardLaneCount = parsedForwardLaneCount;
                    }
                    else if (key == "lanes:backward" && TryReadLaneCount(value, out var parsedBackwardLaneCount))
                    {
                        backwardLaneCount = parsedBackwardLaneCount;
                    }
                    else if (key == "lanes:both_ways" && TryReadLaneCount(value, out var parsedBothWaysLaneCount))
                    {
                        bothWaysLaneCount = parsedBothWaysLaneCount;
                    }
                    else if (key == "bridge")
                        isBridge = IsBridgeValue(value);
                    else if (key == "man_made" && value == "pier")
                        isPier = true;
                    else if (key == "tunnel")
                        isTunnel = IsTunnelValue(value);
                    else if (key == "oneway")
                    {
                        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                        if (normalized == "-1" || normalized == "reverse" || normalized == "backward")
                            oneWayDirection = -1;
                        else if (normalized == "0" || normalized == "no" || normalized == "false" || normalized == "off")
                            oneWayDirection = 0;
                        else if (IsTruthy(value) || normalized == "yes" || normalized == "true" || normalized == "1" || normalized == "forward" || normalized == "fwd")
                            oneWayDirection = 1;
                    }
                    else if (key == "covered")
                        isCovered = IsTruthy(value);
                    else if (key == "embankment")
                        isEmbankment = IsTruthy(value);
                    else if (key == "cutting")
                        isCutting = IsTruthy(value);
                    else if (key == "layer" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLayer))
                    {
                        layer = parsedLayer;
                        hasLayer = true;
                    }
                    else if (key == "incline" && TryReadIncline(value, out inclineDirection, out inclinePercent))
                    {
                        // parsed above
                    }
                    else if (key == "junction" && IsRoundaboutJunction(value))
                        isRoundabout = true;
                    else if (key == "railway")
                        wayRailway = value;
                    else if (key == "footway")
                        footwayType = value;
                    else if ((key == "crossing" || key == "crossing:markings") && IsUnmarkedCrossingTag(key, value))
                        way.IsUnmarkedCrossing = true;
                    else if (key == "sidewalk")
                        sidewalkTag = value;
                    else if (key == "sidewalk:left")
                        sidewalkLeft = IsTruthy(value) || value.Equals("separate", StringComparison.OrdinalIgnoreCase) || value.Equals("both", StringComparison.OrdinalIgnoreCase);
                    else if (key == "sidewalk:right")
                        sidewalkRight = IsTruthy(value) || value.Equals("separate", StringComparison.OrdinalIgnoreCase) || value.Equals("both", StringComparison.OrdinalIgnoreCase);
                    else if (key == "parking:left" || key == "parking:right" || key == "parking:both" ||
                             key == "parking:lane:left" || key == "parking:lane:right" || key == "parking:lane:both")
                    {
                        if (key.EndsWith(":left", StringComparison.OrdinalIgnoreCase))
                            parkingLeftTag = value;
                        else if (key.EndsWith(":right", StringComparison.OrdinalIgnoreCase))
                            parkingRightTag = value;
                        else if (key.EndsWith(":both", StringComparison.OrdinalIgnoreCase))
                            parkingBothTag = value;
                    }
                    else if (TryParseBikeLaneTag(key, value, out var parsedBikeLaneSide))
                    {
                        hasBikeLanes = true;
                        bikeLaneSide = MergeRoadFeatureSide(bikeLaneSide, parsedBikeLaneSide);
                    }
                    else if (TryParseDesignatedBusLaneTag(key, value, out var parsedBusLaneSide))
                    {
                        hasDesignatedBusLanes = true;
                        designatedBusLaneSide = MergeRoadFeatureSide(designatedBusLaneSide, parsedBusLaneSide);
                    }
                    else if (IsBroadBusDesignationTag(key, value))
                    {
                        hasBroadBusDesignation = true;
                    }
                    else if (key == "area" && value == "yes")
                        isArea = true;
                }
            }

            if (string.IsNullOrEmpty(way.Highway))
                way.Highway = wayRailway;
            else if (!string.IsNullOrEmpty(wayRailway) &&
                     !string.Equals(way.Highway, wayRailway, StringComparison.OrdinalIgnoreCase) &&
                     IsElevatedRailType(wayRailway))
                way.Highway = wayRailway;

            // A paved path designated for bicycles is a cycleway in OSM terms; other paths (trails, unpaved or
            // foot paths) stay "path" and use their own prefab rules.
            if (way.Highway == "path" && pathBicycle == "designated" && IsPavedSurface(pathSurface))
                way.Highway = "cycleway";

            if (isArea || string.IsNullOrEmpty(way.Highway) || !IsSupportedHighway(way.Highway))
                return false;

            way.LanesForward = forwardLaneCount;
            way.LanesBackward = backwardLaneCount;
            way.LanesBothWays = bothWaysLaneCount;

            if (!hasExplicitLaneCount && (forwardLaneCount > 0 || backwardLaneCount > 0 || bothWaysLaneCount > 0))
            {
                way.Lanes = forwardLaneCount + backwardLaneCount + bothWaysLaneCount;
                way.HasExplicitLaneCount = true;
            }

            if (hasExplicitLaneCount)
                way.HasExplicitLaneCount = true;

            way.BaseElevationOffsetMeters = EstimateBaseElevationOffset(
                options,
                way.Highway,
                wayRailway,
                hasLayer,
                layer,
                isBridge,
                isTunnel,
                isCovered,
                isEmbankment,
                isCutting);
            way.UsesStructuralElevation = (isBridge || isTunnel || isEmbankment || isCutting) &&
                                          !IsElevatedRailType(way.Highway) && !IsElevatedRailType(wayRailway);
            // A pier is a flat deck: no bridge offset, one height for the whole way (set at generation).
            way.IsPier = isPier;
            if (isPier)
            {
                way.UsesStructuralElevation = true;
                way.BaseElevationOffsetMeters = 0f;
            }
            way.IsBridge = isBridge;
            way.IsTunnel = isTunnel;
            // Only junction=roundabout/circular. A closed loop street (around a block, a cul-de-sac loop) is an ordinary road.
            way.IsRoundabout = isRoundabout;
            way.OneWayDirection = oneWayDirection;
            way.IsOneWay = oneWayDirection != 0 || way.IsRoundabout;
            if (way.IsRoundabout && way.OneWayDirection == 0)
                way.OneWayDirection = 1;

            way.InclineDirection = inclineDirection;
            way.InclinePercent = inclinePercent;
            way.Footway = footwayType;
            way.SidewalkTag = sidewalkTag;
            way.Sidewalk = ParseSidewalkType(sidewalkTag, sidewalkLeft, sidewalkRight);
            ResolveParkingTags(parkingLeftTag, parkingRightTag, parkingBothTag, out var parkingSide, out var parkingStyle);
            way.ParkingSide = parkingSide;
            way.ParkingStyle = parkingStyle;
            way.HasBikeLanes = hasBikeLanes;
            way.HasTurnRestrictions = turnRestrictionsByWayId != null && turnRestrictionsByWayId.ContainsKey(way.Id);
            way.HasRestrictedLanes = hasDesignatedBusLanes || hasBroadBusDesignation;
            way.BikeLaneSide = NormalizeDetectedSide(bikeLaneSide, way.HasBikeLanes);
            way.RestrictedLaneSide = hasDesignatedBusLanes
                ? NormalizeDetectedSide(designatedBusLaneSide, true)
                : NormalizeDetectedSide(hasBroadBusDesignation ? OsmRoadFeatureSide.Both : OsmRoadFeatureSide.None, way.HasRestrictedLanes);
            if (turnRestrictionsByWayId != null && turnRestrictionsByWayId.TryGetValue(way.Id, out var restrictions))
                way.TurnRestrictions.AddRange(restrictions);
            way.TerrainHeights.Clear();

            var nds = wayElement.SelectNodes("nd");
            if (nds == null)
                return false;

            foreach (XmlElement nd in nds)
            {
                var nodeId = ReadLong(nd, "ref");
                way.NodeIds.Add(nodeId);

                if (!nodes.TryGetValue(nodeId, out var node))
                    continue;

                var currentPoint = new float3(
                    (float)(node.X * options.ScaleFactor),
                    0f,
                    (float)(node.Y * options.ScaleFactor));

                currentPoint.y = sampleHeight != null
                    ? sampleHeight(currentPoint)
                    : options.ElevationMeters;

                AddNodeFeatureLocations(way, nodeId, node, currentPoint);

                // Merged close nodes (MergeCloseRoadNodes) share a position: keep one point.
                if (way.Points.Count > 0 && math.distancesq(way.Points[way.Points.Count - 1].xz, currentPoint.xz) < 1e-8f)
                    continue;

                if (sampleHeight != null && way.Points.Count > 0)
                {
                    AddInterpolatedPoints(
                        way.Points,
                        way.TerrainHeights,
                        way.Points[way.Points.Count - 1],
                        currentPoint,
                        sampleHeight,
                        options.ElevationMeters);
                }
                else
                {
                    way.Points.Add(currentPoint);
                    way.TerrainHeights.Add(currentPoint.y);
                }
            }

            ApplyElevationProfile(
                way.Points,
                way.TerrainHeights,
                way.BaseElevationOffsetMeters,
                way.InclineDirection,
                way.InclinePercent,
                way.UsesStructuralElevation);

            if (sampleHeight == null)
            {
                if (!way.IsRoundabout)
                {
                    var simplifyTolerance = Math.Max(0f, options.SimplifyToleranceMeters * options.ScaleFactor);
                    Simplify(way.Points, simplifyTolerance);
                }
            }
            return way.Points.Count >= 2;
        }

        private static void AddNodeFeatureLocations(OsmWay way, long nodeId, GeoNode node, float3 position)
        {
            if (way == null || node == null || node.Tags == null || node.Tags.Count == 0)
                return;

            if (IsCrosswalkNode(node.Tags))
                way.Crosswalks.Add(new OsmRoadNodeFeature { NodeId = nodeId, Position = position });

            if (IsStopSignNode(node.Tags))
                way.StopSigns.Add(new OsmRoadNodeFeature { NodeId = nodeId, Position = position });

            if (IsTrafficLightNode(node.Tags))
                way.TrafficLights.Add(new OsmRoadNodeFeature { NodeId = nodeId, Position = position });
        }

        private static List<OsmWay> AttachSidewalkWays(List<OsmWay> ways, out int attachedCount)
        {
            attachedCount = 0;

            if (ways == null || ways.Count == 0)
                return new List<OsmWay>();

            var roadWays = new List<OsmWay>();
            var roadBounds = new Dictionary<long, (float MinX, float MinZ, float MaxX, float MaxZ)>();
            var roadIndex = new Dictionary<string, List<OsmWay>>(StringComparer.Ordinal);

            foreach (var way in ways)
            {
                if (!CanReceiveAttachedSidewalks(way))
                    continue;

                roadWays.Add(way);
                var bounds = GetWayBounds(way);
                roadBounds[way.Id] = bounds;

                foreach (var cellKey in GetBucketKeys(bounds.MinX, bounds.MinZ, bounds.MaxX, bounds.MaxZ))
                {
                    if (!roadIndex.TryGetValue(cellKey, out var cellWays))
                    {
                        cellWays = new List<OsmWay>();
                        roadIndex[cellKey] = cellWays;
                    }

                    cellWays.Add(way);
                }
            }

            if (roadWays.Count == 0)
                return ways;

            var attachedFootwayIds = new HashSet<long>();
            var roadAttachmentCounts = new Dictionary<long, int>();

            var footways = new List<OsmWay>();
            foreach (var way in ways)
            {
                if (IsFootwaySidewalkCandidate(way))
                    footways.Add(way);
            }

            footways.Sort((a, b) =>
            {
                var aPriority = IsExplicitSidewalkFootway(a) ? 0 : 1;
                var bPriority = IsExplicitSidewalkFootway(b) ? 0 : 1;
                var cmp = aPriority.CompareTo(bPriority);
                if (cmp != 0)
                    return cmp;

                return GetWayLength(b).CompareTo(GetWayLength(a));
            });

            foreach (var footway in footways)
            {
                if (TryAttachFootwayToRoad(footway, roadIndex, roadBounds, roadAttachmentCounts, out var road))
                {
                    attachedFootwayIds.Add(footway.Id);
                    attachedCount++;

                    if (road != null && road.Sidewalk == OsmSidewalkType.None)
                        road.Sidewalk = OsmSidewalkType.Sidewalk;
                }
            }

            if (attachedFootwayIds.Count == 0)
                return ways;

            return ways.Where(way => !attachedFootwayIds.Contains(way.Id)).ToList();
        }

        private static bool TryAttachFootwayToRoad(
            OsmWay footway,
            Dictionary<string, List<OsmWay>> roadIndex,
            Dictionary<long, (float MinX, float MinZ, float MaxX, float MaxZ)> roadBounds,
            Dictionary<long, int> roadAttachmentCounts,
            out OsmWay road)
        {
            road = null;

            if (footway == null || roadIndex == null || roadIndex.Count == 0 || footway.Points.Count < 2)
                return false;

            if (!IsExplicitSidewalkFootway(footway) && IsClearlyStandaloneFootway(footway))
                return false;

            var bestRoad = default(OsmWay);
            var bestScore = double.MaxValue;
            var footwayBounds = GetWayBounds(footway);
            var candidateRoads = GatherNearbyRoads(roadIndex, roadBounds, footwayBounds, footway);
            var threshold = GetFootwayAttachmentThreshold(footway);
            var footwayStart = footway.Points[0];
            var footwayEnd = footway.Points[footway.Points.Count - 1];

            foreach (var candidate in candidateRoads)
            {
                var startDistance = DistancePointToPolyline(footwayStart, candidate.Points);
                var endDistance = DistancePointToPolyline(footwayEnd, candidate.Points);
                if (startDistance > threshold || endDistance > threshold)
                    continue;

                var score = ScoreFootwayAttachment(candidate, footway);
                if (score < 0d || score >= bestScore)
                    continue;

                bestScore = score;
                bestRoad = candidate;
            }

            if (bestRoad == null)
                return false;

            if (bestScore > threshold)
                return false;

            road = bestRoad;
            var attachmentCount = roadAttachmentCounts.TryGetValue(road.Id, out var count) ? count + 1 : 1;
            roadAttachmentCounts[road.Id] = attachmentCount;

            if (road.Sidewalk == OsmSidewalkType.None)
                road.Sidewalk = attachmentCount > 1 ? OsmSidewalkType.WideSidewalk : OsmSidewalkType.Sidewalk;
            else if (road.Sidewalk == OsmSidewalkType.Sidewalk && attachmentCount > 1)
                road.Sidewalk = OsmSidewalkType.WideSidewalk;

            return true;
        }

        private static List<OsmWay> GatherNearbyRoads(
            Dictionary<string, List<OsmWay>> roadIndex,
            Dictionary<long, (float MinX, float MinZ, float MaxX, float MaxZ)> roadBounds,
            (float MinX, float MinZ, float MaxX, float MaxZ) footwayBounds,
            OsmWay footway)
        {
            var result = new List<OsmWay>();
            var seen = new HashSet<long>();
            var threshold = (float)GetFootwayAttachmentThreshold(footway);
            var minX = footwayBounds.MinX - threshold;
            var minZ = footwayBounds.MinZ - threshold;
            var maxX = footwayBounds.MaxX + threshold;
            var maxZ = footwayBounds.MaxZ + threshold;

            foreach (var key in GetBucketKeys(minX, minZ, maxX, maxZ))
            {
                if (!roadIndex.TryGetValue(key, out var roads))
                    continue;

                foreach (var road in roads)
                {
                    if (road == null || !seen.Add(road.Id))
                        continue;

                    if (!roadBounds.TryGetValue(road.Id, out var bounds) || !IntersectsExpandedBounds(bounds, footwayBounds, threshold))
                        continue;

                    result.Add(road);
                }
            }

            return result;
        }

        private static bool IntersectsExpandedBounds((float MinX, float MinZ, float MaxX, float MaxZ) roadBounds, (float MinX, float MinZ, float MaxX, float MaxZ) footwayBounds, float padding)
        {
            return roadBounds.MinX <= footwayBounds.MaxX + padding &&
                   roadBounds.MaxX >= footwayBounds.MinX - padding &&
                   roadBounds.MinZ <= footwayBounds.MaxZ + padding &&
                   roadBounds.MaxZ >= footwayBounds.MinZ - padding;
        }

        private static IEnumerable<string> GetBucketKeys(float minX, float minZ, float maxX, float maxZ)
        {
            var minBucketX = (int)Math.Floor(minX / SidewalkSpatialBucketSizeMeters);
            var maxBucketX = (int)Math.Floor(maxX / SidewalkSpatialBucketSizeMeters);
            var minBucketZ = (int)Math.Floor(minZ / SidewalkSpatialBucketSizeMeters);
            var maxBucketZ = (int)Math.Floor(maxZ / SidewalkSpatialBucketSizeMeters);

            for (var x = minBucketX; x <= maxBucketX; x++)
            {
                for (var z = minBucketZ; z <= maxBucketZ; z++)
                    yield return MakeBucketKey(x, z);
            }
        }

        private static string MakeBucketKey(int bucketX, int bucketZ) => $"{bucketX}:{bucketZ}";

        private static (float MinX, float MinZ, float MaxX, float MaxZ) GetWayBounds(OsmWay way)
        {
            if (way == null || way.Points.Count == 0)
                return (0f, 0f, 0f, 0f);

            var minX = way.Points[0].x;
            var maxX = way.Points[0].x;
            var minZ = way.Points[0].z;
            var maxZ = way.Points[0].z;

            for (var i = 1; i < way.Points.Count; i++)
            {
                var p = way.Points[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            return (minX, minZ, maxX, maxZ);
        }

        private static double GetFootwayAttachmentThreshold(OsmWay footway)
        {
            return IsExplicitSidewalkFootway(footway) ? 30d : 18d;
        }

        private static bool IsFootwaySidewalkCandidate(OsmWay way)
        {
            if (way == null || !string.Equals(way.Highway, "footway", StringComparison.OrdinalIgnoreCase))
                return false;

            return !IsClearlyStandaloneFootway(way);
        }

        private static bool IsExplicitSidewalkFootway(OsmWay way)
        {
            if (way == null)
                return false;

            return string.Equals(way.Footway, "sidewalk", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(way.SidewalkTag, "sidewalk", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(way.SidewalkTag, "both", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(way.SidewalkTag, "left", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(way.SidewalkTag, "right", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsClearlyStandaloneFootway(OsmWay way)
        {
            if (way == null)
                return true;

            if (!string.Equals(way.Highway, "footway", StringComparison.OrdinalIgnoreCase))
                return false;

            switch ((way.Footway ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "crossing":
                case "link":
                case "traffic_island":
                case "escape":
                case "path":
                case "trail":
                    return true;
                default:
                    return false;
            }
        }

        private static bool CanReceiveAttachedSidewalks(OsmWay way)
        {
            if (way == null || string.IsNullOrWhiteSpace(way.Highway))
                return false;

            switch (way.Highway.ToLowerInvariant())
            {
                case "motorway":
                case "motorway_link":
                case "trunk":
                case "trunk_link":
                case "primary_link":
                case "secondary_link":
                case "tertiary_link":
                case "service":
                case "rail":
                case "monorail":
                case "light_rail":
                case "subway":
                case "tram":
                case "track":
                case "cycleway":
                case "bridleway":
                case "footway":
                case "steps":
                case "path":
                case "pedestrian":
                    return false;
                default:
                    return true;
            }
        }

        private static double ScoreFootwayAttachment(OsmWay road, OsmWay footway)
        {
            if (road == null || footway == null || road.Points.Count < 2 || footway.Points.Count < 2)
                return -1d;

            var roadLength = GetWayLength(road);
            var footwayLength = GetWayLength(footway);
            if (roadLength <= 0d || footwayLength <= 0d)
                return -1d;

            var alignment = Math.Abs(GetDirectionDot(road, footway));
            if (alignment < 0.35d)
                return -1d;

            var avgDistance = AverageDistanceToPolyline(footway.Points, road.Points);
            if (double.IsNaN(avgDistance) || double.IsInfinity(avgDistance))
                return -1d;

            var startDistance = DistancePointToPolyline(footway.Points[0], road.Points);
            var endDistance = DistancePointToPolyline(footway.Points[footway.Points.Count - 1], road.Points);
            var endpointPenalty = (startDistance + endDistance) * 0.5d;

            var roadSpan = GetPolylineSpan(road.Points);
            var footwaySpan = GetPolylineSpan(footway.Points);
            var spanPenalty = Math.Abs(roadSpan - footwaySpan) * 0.05d;
            var lengthPenalty = Math.Abs(roadLength - footwayLength) / Math.Max(roadLength, footwayLength) * 6d;
            var tagBonus = IsExplicitSidewalkFootway(footway) ? -4d : 0d;

            return avgDistance + endpointPenalty + spanPenalty + lengthPenalty - alignment * 2d + tagBonus;
        }

        private static double GetDirectionDot(OsmWay a, OsmWay b)
        {
            var aVector = a.Points[a.Points.Count - 1] - a.Points[0];
            var bVector = b.Points[b.Points.Count - 1] - b.Points[0];
            aVector.y = 0f;
            bVector.y = 0f;

            var aLength = math.length(aVector);
            var bLength = math.length(bVector);
            if (aLength <= 0.001f || bLength <= 0.001f)
                return 0d;

            return math.dot(aVector / aLength, bVector / bLength);
        }

        private static double AverageDistanceToPolyline(List<float3> source, List<float3> target)
        {
            if (source == null || target == null || source.Count == 0 || target.Count == 0)
                return double.NaN;

            var total = 0d;
            var count = 0;

            for (var i = 0; i < source.Count; i += Math.Max(1, source.Count / 3))
            {
                total += DistancePointToPolyline(source[i], target);
                count++;
            }

            if (source.Count > 1)
            {
                total += DistancePointToPolyline(source[source.Count - 1], target);
                count++;
            }

            return count == 0 ? double.NaN : total / count;
        }

        private static double DistancePointToPolyline(float3 point, List<float3> polyline)
        {
            if (polyline == null || polyline.Count == 0)
                return double.NaN;

            var best = double.MaxValue;
            for (var i = 0; i < polyline.Count - 1; i++)
            {
                var distance = DistancePointToSegment(point, polyline[i], polyline[i + 1]);
                if (distance < best)
                    best = distance;
            }

            return best;
        }

        private static double DistancePointToSegment(float3 point, float3 a, float3 b)
        {
            var ap = new float2(point.x - a.x, point.z - a.z);
            var ab = new float2(b.x - a.x, b.z - a.z);
            var abLengthSq = math.lengthsq(ab);
            if (abLengthSq <= 0.000001f)
                return math.length(ap);

            var t = math.clamp(math.dot(ap, ab) / abLengthSq, 0f, 1f);
            var closest = new float2(a.x, a.z) + ab * t;
            return math.distance(new float2(point.x, point.z), closest);
        }

        private static double GetWayLength(OsmWay way)
        {
            if (way == null || way.Points.Count < 2)
                return 0d;

            var length = 0d;
            for (var i = 1; i < way.Points.Count; i++)
                length += math.distance(way.Points[i - 1], way.Points[i]);

            return length;
        }

        private static Dictionary<string, int> ReadPriorityRoadNames(string path)
        {
            var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path))
                return priorities;

            if (!File.Exists(path))
                throw new FileNotFoundException("Priority road names file was not found.", path);

            if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Priority road names file must be a .txt file.");

            foreach (var line in File.ReadLines(path))
            {
                var name = (line ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name) || priorities.ContainsKey(name))
                    continue;

                priorities[name] = priorities.Count;
            }

            if (priorities.Count > 0)
                Mod.log.Info($"Loaded {priorities.Count} priority road names from '{path}'.");

            return priorities;
        }

        private static List<OsmWay> PrioritizeWays(List<OsmWay> ways, Dictionary<string, int> priorityRoadNames)
        {
            if (ways == null || ways.Count == 0 || priorityRoadNames == null || priorityRoadNames.Count == 0)
                return ways;

            var prioritizedCount = ways.Count(way => GetPriorityRank(way, priorityRoadNames) < int.MaxValue);
            if (prioritizedCount > 0)
                Mod.log.Info($"Prioritized {prioritizedCount} OSM ways by name before applying the max ways limit.");

            return ways
                .Select((way, index) => new { Way = way, Index = index, Priority = GetPriorityRank(way, priorityRoadNames) })
                .OrderBy(item => item.Priority == int.MaxValue ? 1 : 0)
                .ThenBy(item => item.Priority)
                .ThenBy(item => item.Index)
                .Select(item => item.Way)
                .ToList();
        }

        private static int GetPriorityRank(OsmWay way, Dictionary<string, int> priorityRoadNames)
        {
            if (way == null || priorityRoadNames == null || priorityRoadNames.Count == 0)
                return int.MaxValue;

            var displayName = GetWayDisplayName(way.Name, way.Ref);
            return !string.IsNullOrWhiteSpace(displayName) && priorityRoadNames.TryGetValue(displayName, out var rank)
                ? rank
                : int.MaxValue;
        }

        private static string GetWayDisplayName(string name, string reference)
        {
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim();

            return string.IsNullOrWhiteSpace(reference) ? string.Empty : reference.Trim();
        }

        private static double GetPolylineSpan(List<float3> points)
        {
            if (points == null || points.Count < 2)
                return 0d;

            return math.distance(points[0], points[points.Count - 1]);
        }

        private static OsmSidewalkType ParseSidewalkType(string sidewalkTag, bool sidewalkLeft, bool sidewalkRight)
        {
            if (string.IsNullOrWhiteSpace(sidewalkTag))
                return sidewalkLeft && sidewalkRight ? OsmSidewalkType.WideSidewalk :
                       sidewalkLeft || sidewalkRight ? OsmSidewalkType.Sidewalk : OsmSidewalkType.None;

            switch (sidewalkTag.Trim().ToLowerInvariant())
            {
                case "both":
                    return OsmSidewalkType.WideSidewalk;
                case "left":
                case "right":
                case "yes":
                case "separate":
                    return OsmSidewalkType.Sidewalk;
                case "no":
                case "none":
                    return OsmSidewalkType.None;
                default:
                    return sidewalkLeft && sidewalkRight ? OsmSidewalkType.WideSidewalk :
                           sidewalkLeft || sidewalkRight ? OsmSidewalkType.Sidewalk : OsmSidewalkType.None;
            }
        }

        private static bool TryParseBikeLaneTag(string key, string value, out OsmRoadFeatureSide side)
        {
            side = OsmRoadFeatureSide.None;
            key = (key ?? string.Empty).Trim().ToLowerInvariant();
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || IsFalsey(value))
                return false;

            if (key == "bicycle" && value == "designated")
            {
                side = OsmRoadFeatureSide.Both;
                return true;
            }

            if (IsCyclewaySideKey(key) && IsBicycleLaneValue(value))
            {
                side = ParseRoadFeatureSide(key);
                return true;
            }

            if ((key == "cycleway:lanes" || key.StartsWith("cycleway:lanes:", StringComparison.Ordinal)) &&
                TryParseBicycleLaneSide(value, IsBicycleLaneValue, out side))
            {
                return true;
            }

            if ((key == "bicycle:lanes" || key.StartsWith("bicycle:lanes:", StringComparison.Ordinal)) &&
                TryParseBicycleLaneSide(value, laneValue => laneValue == "designated", out side))
            {
                return true;
            }

            return false;
        }

        private static bool IsCyclewaySideKey(string key)
        {
            return key == "cycleway" ||
                   key == "cycleway:left" ||
                   key == "cycleway:right" ||
                   key == "cycleway:both";
        }

        private static bool IsBicycleLaneValue(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "lane":
                case "opposite_lane":
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseBicycleLaneSide(
            string value,
            System.Func<string, bool> isLaneValue,
            out OsmRoadFeatureSide side)
        {
            side = OsmRoadFeatureSide.None;
            if (isLaneValue == null)
                return false;

            var laneValues = (value ?? string.Empty)
                .Split('|')
                .Select(part => (part ?? string.Empty).Trim().ToLowerInvariant())
                .ToList();

            if (laneValues.Count == 0)
                return false;

            var hasLane = false;
            var hasLeft = false;
            var hasRight = false;

            for (var i = 0; i < laneValues.Count; i++)
            {
                if (!isLaneValue(laneValues[i]))
                    continue;

                hasLane = true;
                if (i == 0)
                    hasLeft = true;
                if (i == laneValues.Count - 1 || (i > 0 && i < laneValues.Count - 1))
                    hasRight = true;
            }

            if (!hasLane)
                return false;

            side = hasLeft && hasRight ? OsmRoadFeatureSide.Both :
                   hasLeft ? OsmRoadFeatureSide.Left :
                   OsmRoadFeatureSide.Right;
            return true;
        }

        private static OsmRoadFeatureSide ParseRoadFeatureSide(string key)
        {
            key = (key ?? string.Empty).Trim().ToLowerInvariant();
            if (key.Contains(":both"))
                return OsmRoadFeatureSide.Both;
            if (key.Contains(":left"))
                return OsmRoadFeatureSide.Left;
            if (key.Contains(":right"))
                return OsmRoadFeatureSide.Right;
            return OsmRoadFeatureSide.None;
        }

        private static OsmRoadFeatureSide MergeRoadFeatureSide(OsmRoadFeatureSide current, OsmRoadFeatureSide next)
        {
            if (next == OsmRoadFeatureSide.None)
                return current;
            if (current == OsmRoadFeatureSide.None)
                return next;
            if (current == next)
                return current;
            return OsmRoadFeatureSide.Both;
        }

        private static OsmRoadFeatureSide NormalizeDetectedSide(OsmRoadFeatureSide side, bool hasFeature)
        {
            if (!hasFeature)
                return OsmRoadFeatureSide.None;
            return side == OsmRoadFeatureSide.None ? OsmRoadFeatureSide.Both : side;
        }

        private static bool IsBroadBusDesignationTag(string key, string value)
        {
            key = (key ?? string.Empty).Trim().ToLowerInvariant();
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return key == "bus" && value == "designated";
        }

        private static bool TryParseDesignatedBusLaneTag(string key, string value, out OsmRoadFeatureSide side)
        {
            side = OsmRoadFeatureSide.None;
            key = (key ?? string.Empty).Trim().ToLowerInvariant();
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                return false;

            if (key == "bus:lanes" || key.StartsWith("bus:lanes:", StringComparison.Ordinal))
                return TryParseDesignatedBusLaneSide(value, out side);

            return false;
        }

        private static bool TryParseDesignatedBusLaneSide(string value, out OsmRoadFeatureSide side)
        {
            side = OsmRoadFeatureSide.None;

            var laneValues = (value ?? string.Empty)
                .Split('|')
                .Select(part => (part ?? string.Empty).Trim().ToLowerInvariant())
                .ToList();

            if (laneValues.Count == 0)
                return false;

            var hasDesignated = false;
            var hasLeft = false;
            var hasRight = false;

            for (var i = 0; i < laneValues.Count; i++)
            {
                if (laneValues[i] != "designated")
                    continue;

                hasDesignated = true;
                if (i == 0)
                    hasLeft = true;
                if (i == laneValues.Count - 1 || (i > 0 && i < laneValues.Count - 1))
                    hasRight = true;
            }

            if (!hasDesignated)
                return false;

            side = hasLeft && hasRight ? OsmRoadFeatureSide.Both :
                   hasLeft ? OsmRoadFeatureSide.Left :
                   OsmRoadFeatureSide.Right;
            return true;
        }

        private static bool IsCrosswalkNode(Dictionary<string, string> tags)
        {
            if (tags == null || IsUnmarkedCrossingNode(tags))
                return false;

            return HasTag(tags, "highway", "crossing") ||
                   HasTag(tags, "footway", "crossing") ||
                   HasTag(tags, "crossing") ||
                   HasTag(tags, "crossing:markings");
        }

        // crossing=unmarked/informal/no or crossing:markings=no: people cross there, but nothing is painted. A CS2
        // crosswalk is painted stripes, so these get none.
        private static bool IsUnmarkedCrossingTag(string key, string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return key == "crossing:markings" ? value == "no" : value == "unmarked" || value == "informal" || value == "no";
        }

        private static bool IsUnmarkedCrossingNode(Dictionary<string, string> tags)
        {
            return tags != null &&
                   ((tags.TryGetValue("crossing", out var c) && IsUnmarkedCrossingTag("crossing", c)) ||
                    (tags.TryGetValue("crossing:markings", out var m) && IsUnmarkedCrossingTag("crossing:markings", m)));
        }

        private static bool IsStopSignNode(Dictionary<string, string> tags)
        {
            return HasTag(tags, "highway", "stop") ||
                   HasTag(tags, "traffic_sign", "stop");
        }

        private static bool IsTrafficLightNode(Dictionary<string, string> tags)
        {
            return HasTag(tags, "highway", "traffic_signals") ||
                   HasTag(tags, "traffic_signals");
        }

        private static bool HasTag(Dictionary<string, string> tags, string key)
        {
            return tags != null &&
                   tags.TryGetValue(key, out var value) &&
                   !string.IsNullOrWhiteSpace(value) &&
                   !IsFalsey(value);
        }

        private static bool HasTag(Dictionary<string, string> tags, string key, string expectedValue)
        {
            return tags != null &&
                   tags.TryGetValue(key, out var value) &&
                   string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFalsey(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "no":
                case "none":
                case "false":
                case "0":
                case "off":
                    return true;
                default:
                    return false;
            }
        }

        private static void ResolveParkingTags(
            string parkingLeftTag,
            string parkingRightTag,
            string parkingBothTag,
            out OsmParkingSideType parkingSide,
            out OsmParkingStyleType parkingStyle)
        {
            parkingSide = OsmParkingSideType.None;
            parkingStyle = OsmParkingStyleType.None;

            var bothStyle = ParseParkingStyleType(parkingBothTag);
            if (bothStyle != OsmParkingStyleType.None)
            {
                parkingSide = OsmParkingSideType.Both;
                parkingStyle = bothStyle;
                return;
            }

            var leftStyle = ParseParkingStyleType(parkingLeftTag);
            var rightStyle = ParseParkingStyleType(parkingRightTag);

            if (leftStyle != OsmParkingStyleType.None && rightStyle != OsmParkingStyleType.None)
            {
                parkingSide = OsmParkingSideType.Both;
                parkingStyle = leftStyle == rightStyle ? leftStyle : leftStyle;
                return;
            }

            if (leftStyle != OsmParkingStyleType.None)
            {
                parkingSide = OsmParkingSideType.Left;
                parkingStyle = leftStyle;
                return;
            }

            if (rightStyle != OsmParkingStyleType.None)
            {
                parkingSide = OsmParkingSideType.Right;
                parkingStyle = rightStyle;
                return;
            }
        }

        private static OsmParkingStyleType ParseParkingStyleType(string parkingStyleTag)
        {
            switch ((parkingStyleTag ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "parallel":
                    return OsmParkingStyleType.Parallel;
                case "diagonal":
                    return OsmParkingStyleType.Diagonal;
                case "perpendicular":
                    return OsmParkingStyleType.Perpendicular;
                default:
                    return OsmParkingStyleType.None;
            }
        }

        private static bool IsRoundaboutJunction(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "roundabout":
                case "circular":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsClosedLoopWay(List<long> nodeIds)
        {
            return nodeIds != null && nodeIds.Count >= 4 && nodeIds[0] == nodeIds[nodeIds.Count - 1];
        }

        private static List<OsmWay> MergeSimpleConnectedWays(List<OsmWay> ways, out int mergedWayCount)
        {
            mergedWayCount = 0;

            if (ways == null || ways.Count == 0)
                return new List<OsmWay>();

            var parent = new int[ways.Count];
            for (var i = 0; i < parent.Length; i++)
                parent[i] = i;

            var nodeUsageCounts = BuildEndpointUsageCounts(ways);
            var nodeToWays = BuildNodeToWayIndexMap(ways);

            foreach (var pair in nodeToWays)
            {
                var indices = pair.Value;
                if (indices.Count < 2)
                    continue;

                for (var i = 0; i < indices.Count; i++)
                {
                    for (var j = i + 1; j < indices.Count; j++)
                    {
                        if (!CanMergeWays(ways[indices[i]], ways[indices[j]]))
                            continue;

                        Union(parent, indices[i], indices[j]);
                    }
                }
            }

            var components = new Dictionary<int, List<int>>();
            for (var i = 0; i < ways.Count; i++)
            {
                var root = Find(parent, i);
                if (!components.TryGetValue(root, out var component))
                {
                    component = new List<int>();
                    components[root] = component;
                }

                component.Add(i);
            }

            var result = new List<OsmWay>(ways.Count);
            foreach (var component in components.Values.OrderBy(component => component.Min()))
            {
                if (component.Count < 2)
                {
                    result.Add(ways[component[0]]);
                    continue;
                }

                if (TryMergeComponent(ways, component, nodeUsageCounts, out var mergedWay))
                {
                    result.Add(mergedWay);
                    mergedWayCount += component.Count - 1;
                    continue;
                }

                foreach (var index in component.OrderBy(index => index))
                    result.Add(ways[index]);
            }

            return result;
        }

        private static Dictionary<long, int> BuildEndpointUsageCounts(List<OsmWay> ways)
        {
            var counts = new Dictionary<long, int>();

            foreach (var way in ways)
            {
                if (way == null || way.NodeIds.Count < 2)
                    continue;

                IncrementCount(counts, way.NodeIds[0]);
                IncrementCount(counts, way.NodeIds[way.NodeIds.Count - 1]);
            }

            return counts;
        }

        private static Dictionary<long, List<int>> BuildNodeToWayIndexMap(List<OsmWay> ways)
        {
            var map = new Dictionary<long, List<int>>();

            for (var i = 0; i < ways.Count; i++)
            {
                var way = ways[i];
                if (way == null || way.NodeIds.Count < 2)
                    continue;

                AddNodeWayIndex(map, way.NodeIds[0], i);
                AddNodeWayIndex(map, way.NodeIds[way.NodeIds.Count - 1], i);
            }

            return map;
        }

        private static void AddNodeWayIndex(Dictionary<long, List<int>> map, long nodeId, int wayIndex)
        {
            if (!map.TryGetValue(nodeId, out var indices))
            {
                indices = new List<int>();
                map[nodeId] = indices;
            }

            indices.Add(wayIndex);
        }

        private static void IncrementCount(Dictionary<long, int> counts, long nodeId)
        {
            counts.TryGetValue(nodeId, out var count);
            counts[nodeId] = count + 1;
        }

        private static bool TryMergeComponent(
            List<OsmWay> ways,
            List<int> component,
            Dictionary<long, int> nodeUsageCounts,
            out OsmWay mergedWay)
        {
            mergedWay = null;

            if (component == null || component.Count < 2)
                return false;

            var nodeCounts = new Dictionary<long, int>();
            foreach (var index in component)
            {
                var way = ways[index];
                if (way == null || way.NodeIds.Count < 2)
                    return false;

                if (way.NodeIds[0] == way.NodeIds[way.NodeIds.Count - 1])
                    return false;

                IncrementCount(nodeCounts, way.NodeIds[0]);
                IncrementCount(nodeCounts, way.NodeIds[way.NodeIds.Count - 1]);

                foreach (var nodeId in way.NodeIds)
                {
                    if (!nodeUsageCounts.TryGetValue(nodeId, out var usageCount) || usageCount > 2)
                        return false;
                }
            }

            if (nodeCounts.Count != component.Count + 1)
                return false;

            var endpoints = nodeCounts.Where(pair => pair.Value == 1).Select(pair => pair.Key).ToList();
            if (endpoints.Count != 2 || nodeCounts.Any(pair => pair.Value > 2))
                return false;

            var orderedWays = new List<OsmWay>(component.Count);
            var remaining = new HashSet<int>(component);
            var currentNodeId = endpoints[0];
            var startIndex = FindIncidentWayIndex(ways, remaining, currentNodeId);
            if (startIndex < 0)
                return false;

            var currentWay = CloneOrientedWay(ways[startIndex], currentNodeId, out var currentEndNodeId);
            if (currentWay == null)
                return false;

            orderedWays.Add(currentWay);
            remaining.Remove(startIndex);

            while (remaining.Count > 0)
            {
                var nextIndex = FindIncidentWayIndex(ways, remaining, currentEndNodeId);
                if (nextIndex < 0)
                    return false;

                var nextWay = CloneOrientedWay(ways[nextIndex], currentEndNodeId, out var nextEndNodeId);
                if (nextWay == null)
                    return false;

                if (!IsStraightContinuation(orderedWays[orderedWays.Count - 1], nextWay))
                    return false;

                orderedWays.Add(nextWay);
                remaining.Remove(nextIndex);
                currentEndNodeId = nextEndNodeId;
            }

            if (currentEndNodeId != endpoints[1])
                return false;

            mergedWay = MergeOrientedWays(orderedWays);
            return mergedWay != null && mergedWay.Points.Count >= 2;
        }

        private static int FindIncidentWayIndex(List<OsmWay> ways, HashSet<int> remaining, long nodeId)
        {
            foreach (var index in remaining.OrderBy(index => index))
            {
                var way = ways[index];
                if (way == null || way.NodeIds.Count < 2)
                    continue;

                if (way.NodeIds[0] == nodeId || way.NodeIds[way.NodeIds.Count - 1] == nodeId)
                    return index;
            }

            return -1;
        }

        private static OsmWay CloneOrientedWay(OsmWay way, long startNodeId, out long endNodeId)
        {
            endNodeId = 0;

            if (way == null || way.NodeIds.Count < 2)
                return null;

            var reversed = way.NodeIds[way.NodeIds.Count - 1] == startNodeId;
            if (!reversed && way.NodeIds[0] != startNodeId)
                return null;

            var clone = new OsmWay
            {
                Id = way.Id,
                Name = way.Name,
                Ref = way.Ref,
                Highway = way.Highway,
                Footway = way.Footway,
                SidewalkTag = way.SidewalkTag,
                Lanes = way.Lanes,
                LanesForward = way.LanesForward,
                LanesBackward = way.LanesBackward,
                LanesBothWays = way.LanesBothWays,
                HasExplicitLaneCount = way.HasExplicitLaneCount,
                UsesStructuralElevation = way.UsesStructuralElevation,
                IsBridge = way.IsBridge,
                IsPier = way.IsPier,
                IsTunnel = way.IsTunnel,
                IsOneWay = way.IsOneWay,
                OneWayDirection = way.OneWayDirection,
                BaseElevationOffsetMeters = way.BaseElevationOffsetMeters,
                InclineDirection = way.InclineDirection,
                InclinePercent = way.InclinePercent,
                Sidewalk = way.Sidewalk,
                ParkingSide = way.ParkingSide,
                ParkingStyle = way.ParkingStyle,
                HasBikeLanes = way.HasBikeLanes,
                HasTurnRestrictions = way.HasTurnRestrictions,
                HasRestrictedLanes = way.HasRestrictedLanes,
                BikeLaneSide = way.BikeLaneSide,
                RestrictedLaneSide = way.RestrictedLaneSide,
                IsRoundabout = way.IsRoundabout,
                IsParkingAisle = way.IsParkingAisle
            };

            if (reversed)
            {
                for (var i = way.NodeIds.Count - 1; i >= 0; i--)
                    clone.NodeIds.Add(way.NodeIds[i]);

                for (var i = way.Points.Count - 1; i >= 0; i--)
                    clone.Points.Add(way.Points[i]);

                for (var i = way.TerrainHeights.Count - 1; i >= 0; i--)
                    clone.TerrainHeights.Add(way.TerrainHeights[i]);
            }
            else
            {
                clone.NodeIds.AddRange(way.NodeIds);
                clone.Points.AddRange(way.Points);
                clone.TerrainHeights.AddRange(way.TerrainHeights);
            }

            clone.Crosswalks.AddRange(way.Crosswalks);
            clone.StopSigns.AddRange(way.StopSigns);
            clone.TrafficLights.AddRange(way.TrafficLights);
            clone.TurnRestrictions.AddRange(way.TurnRestrictions);

            endNodeId = clone.NodeIds[clone.NodeIds.Count - 1];
            return clone;
        }

        private static OsmWay MergeOrientedWays(List<OsmWay> ways)
        {
            if (ways == null || ways.Count == 0)
                return null;

            var merged = new OsmWay
            {
                Id = ways[0].Id,
                Name = ways[0].Name,
                Ref = ways[0].Ref,
                Highway = ways[0].Highway,
                Footway = ways[0].Footway,
                SidewalkTag = ways[0].SidewalkTag,
                Lanes = ways[0].Lanes,
                LanesForward = ways[0].LanesForward,
                LanesBackward = ways[0].LanesBackward,
                LanesBothWays = ways[0].LanesBothWays,
                HasExplicitLaneCount = ways[0].HasExplicitLaneCount,
                UsesStructuralElevation = ways[0].UsesStructuralElevation,
                IsBridge = ways[0].IsBridge,
                IsPier = ways[0].IsPier,
                IsTunnel = ways[0].IsTunnel,
                IsOneWay = ways[0].IsOneWay,
                OneWayDirection = ways[0].OneWayDirection,
                BaseElevationOffsetMeters = ways[0].BaseElevationOffsetMeters,
                InclineDirection = ways[0].InclineDirection,
                InclinePercent = ways[0].InclinePercent,
                Sidewalk = ways[0].Sidewalk,
                ParkingSide = ways[0].ParkingSide,
                ParkingStyle = ways[0].ParkingStyle,
                HasBikeLanes = ways[0].HasBikeLanes,
                HasTurnRestrictions = ways[0].HasTurnRestrictions,
                HasRestrictedLanes = ways[0].HasRestrictedLanes,
                BikeLaneSide = ways[0].BikeLaneSide,
                RestrictedLaneSide = ways[0].RestrictedLaneSide,
                IsRoundabout = ways[0].IsRoundabout,
                IsParkingAisle = ways[0].IsParkingAisle
            };

            if (merged.Lanes <= 0)
            {
                foreach (var way in ways)
                {
                    if (way.Lanes <= 0)
                        continue;

                    merged.Lanes = way.Lanes;
                    merged.HasExplicitLaneCount |= way.HasExplicitLaneCount;
                    break;
                }
            }

            if (merged.LanesForward <= 0)
            {
                foreach (var way in ways)
                {
                    if (way.LanesForward <= 0)
                        continue;

                    merged.LanesForward = way.LanesForward;
                    break;
                }
            }

            if (merged.LanesBackward <= 0)
            {
                foreach (var way in ways)
                {
                    if (way.LanesBackward <= 0)
                        continue;

                    merged.LanesBackward = way.LanesBackward;
                    break;
                }
            }

            if (merged.LanesBothWays <= 0)
            {
                foreach (var way in ways)
                {
                    if (way.LanesBothWays <= 0)
                        continue;

                    merged.LanesBothWays = way.LanesBothWays;
                    break;
                }
            }

            foreach (var way in ways.Skip(1))
            {
                if (way.Sidewalk > merged.Sidewalk)
                    merged.Sidewalk = way.Sidewalk;

                if (way.ParkingSide > merged.ParkingSide)
                    merged.ParkingSide = way.ParkingSide;

                if (way.ParkingStyle > merged.ParkingStyle)
                    merged.ParkingStyle = way.ParkingStyle;

                merged.HasBikeLanes |= way.HasBikeLanes;
                merged.HasTurnRestrictions |= way.HasTurnRestrictions;
                merged.HasRestrictedLanes |= way.HasRestrictedLanes;
                merged.BikeLaneSide = MergeRoadFeatureSide(merged.BikeLaneSide, way.BikeLaneSide);
                merged.RestrictedLaneSide = MergeRoadFeatureSide(merged.RestrictedLaneSide, way.RestrictedLaneSide);
            }

            for (var i = 0; i < ways.Count; i++)
            {
                var way = ways[i];
                var startIndex = i == 0 ? 0 : 1;

                for (var pointIndex = startIndex; pointIndex < way.Points.Count; pointIndex++)
                    merged.Points.Add(way.Points[pointIndex]);

                for (var heightIndex = startIndex; heightIndex < way.TerrainHeights.Count; heightIndex++)
                    merged.TerrainHeights.Add(way.TerrainHeights[heightIndex]);

                if (i == 0)
                {
                    merged.NodeIds.AddRange(way.NodeIds);
                    AddDistinctNodeFeatures(merged.Crosswalks, way.Crosswalks);
                    AddDistinctNodeFeatures(merged.StopSigns, way.StopSigns);
                    AddDistinctNodeFeatures(merged.TrafficLights, way.TrafficLights);
                    AddDistinctTurnRestrictions(merged.TurnRestrictions, way.TurnRestrictions);
                    continue;
                }

                for (var nodeIndex = 1; nodeIndex < way.NodeIds.Count; nodeIndex++)
                    merged.NodeIds.Add(way.NodeIds[nodeIndex]);

                AddDistinctNodeFeatures(merged.Crosswalks, way.Crosswalks);
                AddDistinctNodeFeatures(merged.StopSigns, way.StopSigns);
                AddDistinctNodeFeatures(merged.TrafficLights, way.TrafficLights);
                AddDistinctTurnRestrictions(merged.TurnRestrictions, way.TurnRestrictions);
            }

            return merged.Points.Count >= 2 ? merged : null;
        }

        private static void AddDistinctNodeFeatures(List<OsmRoadNodeFeature> target, List<OsmRoadNodeFeature> source)
        {
            if (target == null || source == null || source.Count == 0)
                return;

            var seen = new HashSet<long>(target.Select(feature => feature.NodeId));
            foreach (var feature in source)
            {
                if (feature == null || !seen.Add(feature.NodeId))
                    continue;

                target.Add(feature);
            }
        }

        private static void AddDistinctTurnRestrictions(List<OsmTurnRestriction> target, List<OsmTurnRestriction> source)
        {
            if (target == null || source == null || source.Count == 0)
                return;

            var seen = new HashSet<string>(target.Select(GetTurnRestrictionKey), StringComparer.Ordinal);
            foreach (var restriction in source)
            {
                if (restriction == null || !seen.Add(GetTurnRestrictionKey(restriction)))
                    continue;

                target.Add(restriction);
            }
        }

        private static string GetTurnRestrictionKey(OsmTurnRestriction restriction)
        {
            if (restriction == null)
                return string.Empty;

            return $"{restriction.ViaNodeId}:{restriction.ForbidLeftTurn}:{restriction.ForbidRightTurn}:{restriction.ForbidStraight}:{restriction.ForbidUTurn}";
        }

        // Largest ring (centreline radius) built as a CS2 roundabout object. CS2's largest roundabout object has a
        // 30 m island; bigger OSM rings are built as ordinary one-way ring roads.
        private const float MaxNodeRoundaboutRadiusMeters = 30f;
        private const float MinNodeRoundaboutRadiusMeters = 3f;
        // Connector points up to this far beyond the OSM ring radius are dropped too (about half a carriageway).
        private const float RoundaboutClearanceMeters = 6f;

        /// <summary>
        /// CS2 does not build roundabouts from ring roads: a roundabout is an object placed on ONE junction node. So for
        /// each OSM roundabout (junction=roundabout ways forming a closed ring, possibly split over several ways):
        /// - small ring: mark its ways IsNodeRoundabout (not queued as roads), and extend every way that ends on the ring
        ///   to the ring centre, so they all meet at one node where the importer then places the roundabout object;
        /// - large ring or an open (incomplete) ring: build it as an ordinary one-way road (IsRoundabout = false).
        /// </summary>
        private static void ConvertRoundabouts(List<OsmWay> ways, System.Func<float3, float> sampleHeight)
        {
            var ringWays = ways.Where(w => w.IsRoundabout && w.NodeIds.Count >= 2 && w.Points.Count >= 2).ToList();
            if (ringWays.Count == 0)
                return;

            // Group roundabout ways that share endpoints (OSM often splits one roundabout into several ways).
            var parent = new Dictionary<OsmWay, OsmWay>();
            foreach (var w in ringWays) parent[w] = w;
            OsmWay Find(OsmWay x) { while (parent[x] != x) x = parent[x]; return x; }
            var byEndpoint = new Dictionary<long, OsmWay>();
            foreach (var w in ringWays)
            {
                foreach (var id in new[] { w.NodeIds[0], w.NodeIds[w.NodeIds.Count - 1] })
                {
                    if (byEndpoint.TryGetValue(id, out var other))
                        parent[Find(w)] = Find(other);
                    else
                        byEndpoint[id] = w;
                }
            }

            // Ways by node, so each ring only visits the ways that touch it (a scan of every way per ring took ~19 s
            // on full Seattle). They are visited in list order, as the full scan did.
            var ringWayIndexByNode = new Dictionary<long, List<int>>();
            for (var i = 0; i < ways.Count; i++)
            {
                foreach (var id in ways[i].NodeIds)
                {
                    if (!ringWayIndexByNode.TryGetValue(id, out var list))
                        ringWayIndexByNode[id] = list = new List<int>();
                    if (list.Count == 0 || list[list.Count - 1] != i)
                        list.Add(i);
                }
            }

            var converted = 0;
            var asRingRoads = 0;
            var extended = 0;
            var passThrough = 0;
            foreach (var group in ringWays.GroupBy(Find).Select(g => g.ToList()))
            {
                // Closed ring: every endpoint is used exactly twice within the group.
                var endpointUse = new Dictionary<long, int>();
                foreach (var w in group)
                    foreach (var id in new[] { w.NodeIds[0], w.NodeIds[w.NodeIds.Count - 1] })
                        endpointUse[id] = endpointUse.TryGetValue(id, out var c) ? c + 1 : 1;
                var closed = endpointUse.Values.All(c => c % 2 == 0);

                var points = group.SelectMany(w => w.Points).ToList();
                var center = float3.zero;
                foreach (var p in points) center += p;
                center /= points.Count;
                var radius = points.Average(p => math.distance(p.xz, center.xz));

                if (!closed || radius > MaxNodeRoundaboutRadiusMeters || radius < MinNodeRoundaboutRadiusMeters)
                {
                    foreach (var w in group)
                        w.IsRoundabout = false; // build the OSM ring as a normal one-way road
                    asRingRoads++;
                    continue;
                }

                center.y = sampleHeight != null ? sampleHeight(center) : points.Average(p => p.y);
                var ringNodes = new HashSet<long>(group.SelectMany(w => w.NodeIds));
                foreach (var w in group)
                {
                    w.IsNodeRoundabout = true;
                    w.RoundaboutCenter = center;
                    w.RoundaboutRadius = radius;
                }

                // Every way that ends on the ring now continues to the centre (the exact same float3, so CS2 joins them).
                // Only ways with a node on the ring can end on it or pass through it.
                var touching = new SortedSet<int>();
                foreach (var id in ringNodes)
                {
                    if (ringWayIndexByNode.TryGetValue(id, out var list))
                        touching.UnionWith(list);
                }
                foreach (var w in touching.Select(i => ways[i]))
                {
                    if (w.IsRoundabout || w.NodeIds.Count < 2 || w.Points.Count < 1)
                        continue;
                    // Points inside the ring (the OSM ring node itself and anything closer to the centre) would sit
                    // inside CS2's roundabout and leave stray nodes there, so they are replaced by the centre.
                    var clearRadius = radius + RoundaboutClearanceMeters;
                    var touches = false;
                    if (ringNodes.Contains(w.NodeIds[0]))
                    {
                        while (w.Points.Count > 1 && math.distance(w.Points[0].xz, center.xz) <= clearRadius)
                        {
                            w.Points.RemoveAt(0);
                            if (w.TerrainHeights.Count > 0) w.TerrainHeights.RemoveAt(0);
                        }
                        w.Points.Insert(0, center);
                        if (w.TerrainHeights.Count > 0) w.TerrainHeights.Insert(0, center.y);
                        touches = true;
                    }
                    if (ringNodes.Contains(w.NodeIds[w.NodeIds.Count - 1]))
                    {
                        while (w.Points.Count > 1 && math.distance(w.Points[w.Points.Count - 1].xz, center.xz) <= clearRadius)
                        {
                            w.Points.RemoveAt(w.Points.Count - 1);
                            if (w.TerrainHeights.Count > 0) w.TerrainHeights.RemoveAt(w.TerrainHeights.Count - 1);
                        }
                        w.Points.Add(center);
                        if (w.TerrainHeights.Count > 0) w.TerrainHeights.Add(center.y);
                        touches = true;
                    }
                    if (touches)
                        extended++;
                    else if (w.NodeIds.Skip(1).Take(w.NodeIds.Count - 2).Any(ringNodes.Contains))
                        passThrough++;
                }
                converted++;
            }

            Mod.log.Info($"Roundabouts: {converted} built as CS2 roundabout objects ({extended} connecting ways extended to the centre), " +
                         $"{asRingRoads} built as ring roads (open, or radius outside {MinNodeRoundaboutRadiusMeters}-{MaxNodeRoundaboutRadiusMeters} m)." +
                         (passThrough > 0 ? $" {passThrough} ways pass through a ring node without ending there; left unchanged." : string.Empty));
        }

        // Paths ending on a dropped sidewalk/crossing are extended onto a road within this distance.
        private const float PathToRoadSnapMeters = 15f;
        // A road point this close to the snap position is reused instead of inserting a new one.
        private const float RoadPointReuseMeters = 2f;
        // Crosswalk features this close to a road junction are dropped: the junction draws its own crosswalks.
        private const float JunctionCrosswalkRadiusMeters = 16f;

        private static bool IsPedestrianDetailFootway(OsmWay way)
        {
            if (way == null || !string.Equals(way.Highway, "footway", StringComparison.OrdinalIgnoreCase))
                return false;
            switch ((way.Footway ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "sidewalk":
                case "crossing":
                case "traffic_island":
                case "link":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsVehicleRoad(OsmWay way)
        {
            if (way == null || string.IsNullOrWhiteSpace(way.Highway) || way.IsNodeRoundabout)
                return false;
            switch (way.Highway.ToLowerInvariant())
            {
                case "motorway": case "motorway_link": case "trunk": case "trunk_link":
                case "primary": case "primary_link": case "secondary": case "secondary_link":
                case "tertiary": case "tertiary_link": case "unclassified": case "residential":
                case "living_street": case "service": case "road":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Builds pedestrian details the way CS2 gameplay does instead of copying OSM's drawing literally. CS2 roads
        /// carry their own sidewalks, junctions draw their own crosswalks, and a crosswalk anywhere else is a node
        /// upgrade (Upgraded + CompositionFlags Crosswalk, applied by OsmImportSystem.ApplyRoadServiceUpgrades).
        /// Built literally, OSM's separately mapped sidewalks ran inside the road's own sidewalk (dead-end caps poking
        /// out), and crossing ways lay on the road surface (slabs) or added a second crosswalk next to a junction.
        /// - drop footway=sidewalk/crossing/traffic_island/link ways;
        /// - every crossing way's road node becomes a crosswalk feature on that road (unless near a junction);
        /// - real paths (campus walkways etc.) that ended on a dropped detail are extended onto the road: to the
        ///   crossing's road node when they ended on its kerb (so both sides meet one node and CS2 draws the
        ///   crosswalk), otherwise to the nearest road point within PathToRoadSnapMeters.
        /// </summary>
        private static List<OsmWay> ReplaceMappedPedestrianDetails(List<OsmWay> ways, Dictionary<long, float2> sourcePositions,
            Func<long, bool> isUnmarkedCrossingNode = null)
        {
            var details = ways.Where(IsPedestrianDetailFootway).ToList();
            if (details.Count == 0)
                return ways;
            var detailSet = new HashSet<OsmWay>(details);
            var roads = ways.Where(IsVehicleRoad).ToList();
            // IsVehicleRoad lower-cases the highway type on every call; this function asks millions of times on a
            // city-sized import (nothing here changes a way's type), so it asks the set instead.
            var roadSet = new HashSet<OsmWay>(roads);
            // Points inserted into a road lie on one of its segments, inside that segment's box.
            var roadIndex = new PolylineIndex(roads.Count, r => roads[r].Points.Count, (r, i) => roads[r].Points[i].xz, 0.01f);

            var waysByNode = new Dictionary<long, List<OsmWay>>();
            foreach (var w in ways)
                foreach (var id in w.NodeIds.Distinct())
                {
                    if (!waysByNode.TryGetValue(id, out var list))
                        waysByNode[id] = list = new List<OsmWay>();
                    list.Add(w);
                }

            float3? RoadPointAt(OsmWay road, long nodeId)
            {
                if (!sourcePositions.TryGetValue(nodeId, out var xz))
                    return null;
                foreach (var p in road.Points)
                    if (math.distance(p.xz, xz) < 0.01f)
                        return p; // the exact float3 the road will queue
                return null;
            }

            // Crossing ways: their road node becomes a crosswalk; their kerb nodes lead to that road node.
            var kerbToRoadPoint = new Dictionary<long, float3>();
            var crosswalksAdded = 0;
            var crosswalksUnmarked = 0;
            foreach (var crossing in details.Where(d => string.Equals(d.Footway, "crossing", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var id in crossing.NodeIds)
                {
                    if (!waysByNode.TryGetValue(id, out var onNode))
                        continue;
                    var road = onNode.FirstOrDefault(roadSet.Contains);
                    if (road == null)
                        continue;
                    var point = RoadPointAt(road, id);
                    if (point == null)
                        continue;
                    var unmarked = crossing.IsUnmarkedCrossing || (isUnmarkedCrossingNode != null && isUnmarkedCrossingNode(id));
                    if (unmarked)
                        crosswalksUnmarked++;
                    else if (!road.Crosswalks.Any(c => c.NodeId == id))
                    {
                        road.Crosswalks.Add(new OsmRoadNodeFeature { NodeId = id, Position = point.Value });
                        crosswalksAdded++;
                    }
                    foreach (var kerb in crossing.NodeIds)
                        if (kerb != id && !kerbToRoadPoint.ContainsKey(kerb))
                            kerbToRoadPoint[kerb] = point.Value;
                }
            }

            // Junction crosswalks are drawn by the junction itself.
            var junctionPoints = new List<float2>();
            foreach (var kv in waysByNode)
                if (kv.Value.Count(roadSet.Contains) >= 2 && sourcePositions.TryGetValue(kv.Key, out var jp))
                    junctionPoints.Add(jp);
            var junctionGrid = new Dictionary<(int, int), List<float2>>();
            foreach (var jp in junctionPoints)
            {
                var key = ((int)math.floor(jp.x / 16f), (int)math.floor(jp.y / 16f));
                if (!junctionGrid.TryGetValue(key, out var cell)) junctionGrid[key] = cell = new List<float2>();
                cell.Add(jp);
            }
            bool NearJunction(float2 p)
            {
                var cx = (int)math.floor(p.x / 16f);
                var cz = (int)math.floor(p.y / 16f);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (junctionGrid.TryGetValue((cx + dx, cz + dz), out var cell) &&
                            cell.Any(q => math.distance(q, p) <= JunctionCrosswalkRadiusMeters && math.distance(q, p) > 0.01f))
                            return true;
                return false;
            }
            var crosswalksDroppedNearJunction = 0;
            foreach (var road in roads)
            {
                // Kept aside: generation uses them to pick which junction leg keeps CS2's crosswalk at minor junctions.
                road.JunctionCrossings.AddRange(road.Crosswalks.Where(c => NearJunction(c.Position.xz)));
                crosswalksDroppedNearJunction += road.Crosswalks.RemoveAll(c => NearJunction(c.Position.xz));
            }

            // OSM often maps several crossings a few metres apart over one road: a straight one plus diagonals from a
            // stair landing to each kerb (three within 6 m at Volunteer Park's entrance), or one for each of two paths
            // meeting the road side by side. Each became its own crosswalk, a row of stripes. Keep one per
            // CrosswalkMergeMeters of road (never across a junction) and lead the others' paths to it.
            var junctionKeys = new HashSet<(int, int)>(junctionPoints.Select(PointKey));
            var mergedInto = new Dictionary<float3, float3>();
            var crosswalksMerged = 0;
            foreach (var road in roads)
                crosswalksMerged += MergeCloseCrosswalks(road, junctionKeys, mergedInto);
            if (mergedInto.Count > 0)
            {
                foreach (var kerb in kerbToRoadPoint.Keys.ToList())
                {
                    if (mergedInto.TryGetValue(kerbToRoadPoint[kerb], out var kept))
                        kerbToRoadPoint[kerb] = kept;
                }
            }

            // Real paths that ended on a dropped detail: extend them onto the road.
            var snappedToCrossing = 0;
            var snappedToRoad = 0;
            var leftUnsnapped = 0;
            foreach (var path in ways)
            {
                if (detailSet.Contains(path) || roadSet.Contains(path) || path.IsRoundabout || path.NodeIds.Count < 2 || path.Points.Count < 2)
                    continue;
                if (!IsPathLike(path))
                    continue;
                for (var end = 0; end < 2; end++)
                {
                    var nodeId = end == 0 ? path.NodeIds[0] : path.NodeIds[path.NodeIds.Count - 1];
                    if (!waysByNode.TryGetValue(nodeId, out var onNode))
                        continue;
                    if (onNode.Any(roadSet.Contains) || !onNode.Any(detailSet.Contains))
                        continue; // already on a road, or not ending on a dropped detail
                    var endPoint = end == 0 ? path.Points[0] : path.Points[path.Points.Count - 1];

                    float3 target;
                    if (kerbToRoadPoint.TryGetValue(nodeId, out var crossingPoint))
                    {
                        target = crossingPoint;
                        snappedToCrossing++;
                    }
                    else if (TrySnapToRoad(endPoint, roads, roadIndex, out target))
                    {
                        snappedToRoad++;
                    }
                    else
                    {
                        leftUnsnapped++;
                        continue;
                    }

                    if (math.distance(target.xz, endPoint.xz) < 0.01f)
                        continue;
                    if (end == 0)
                    {
                        path.Points.Insert(0, target);
                        if (path.TerrainHeights.Count > 0) path.TerrainHeights.Insert(0, target.y);
                    }
                    else
                    {
                        path.Points.Add(target);
                        if (path.TerrainHeights.Count > 0) path.TerrainHeights.Add(target.y);
                    }
                }
            }

            Mod.log.Info($"Pedestrian details: dropped {details.Count} sidewalk/crossing/island/link footways (CS2 roads draw their own); " +
                         $"{crosswalksAdded} crosswalk upgrades added from crossing ways ({crosswalksUnmarked} unmarked skipped), {crosswalksDroppedNearJunction} dropped near junctions, " +
                         $"{crosswalksMerged} merged into a crosswalk within {CrosswalkMergeMeters:F0} m; " +
                         $"paths extended onto roads: {snappedToCrossing} at crossings, {snappedToRoad} at the nearest road point, {leftUnsnapped} left unconnected.");
            return ways.Where(w => !detailSet.Contains(w)).ToList();
        }

        // Path joins on one road closer than this become one, at their average position.
        private const float PathJoinMergeMeters = 8f;
        // A path join is kept at least this far (along the road) from a road junction.
        private const float PathJoinJunctionClearanceMeters = 16f;

        /// <summary>
        /// Each footpath joining a road mid-block is a junction of its own. Where several joined within a few metres
        /// (two trails 3.5 m apart on the Volunteer Park ring: two sets of misaligned markings), or a few metres from a
        /// road junction (2.8-10.8 m at the ring's entrance and north junction: the road junction had no room for its
        /// corners, so sidewalks ran across the junction's mouth), the roads around them could not be built cleanly.
        /// On each road: joins within PathJoinMergeMeters of each other become one at their average position along the
        /// road, and a join is moved to PathJoinJunctionClearanceMeters from the nearest road junction (to the middle
        /// of the stretch between two junctions closer than twice that). Every path end at a moved join follows it.
        /// Runs before SquarePathApproaches, which then brings the paths in square.
        /// </summary>
        private static void RelocatePathJoins(List<OsmWay> ways)
        {
            var roads = ways.Where(IsVehicleRoad).ToList();
            var roadAt = new Dictionary<(int, int), List<(OsmWay Road, int Index)>>();
            foreach (var road in roads)
                for (var i = 0; i < road.Points.Count; i++)
                {
                    var key = PointKey(road.Points[i].xz);
                    if (!roadAt.TryGetValue(key, out var list))
                        roadAt[key] = list = new List<(OsmWay, int)>();
                    list.Add((road, i));
                }
            bool IsRoadJunction((int, int) key) => roadAt.TryGetValue(key, out var l) && l.Select(r => r.Road).Distinct().Count() > 1;

            // Path ends sitting on a road's interior point (not a road junction or road end), per road point.
            var endsAt = new Dictionary<(int, int), List<(OsmWay Path, int End)>>();
            foreach (var path in ways)
            {
                if (!IsPathLike(path) || path.Points.Count < 2)
                    continue;
                for (var end = 0; end < 2; end++)
                {
                    var key = PointKey((end == 0 ? path.Points[0] : path.Points[path.Points.Count - 1]).xz);
                    if (!roadAt.TryGetValue(key, out var on) || IsRoadJunction(key))
                        continue;
                    var (road, ri) = on[0];
                    if (ri == 0 || ri == road.Points.Count - 1)
                        continue;
                    if (!endsAt.TryGetValue(key, out var list))
                        endsAt[key] = list = new List<(OsmWay, int)>();
                    list.Add((path, end));
                }
            }

            var moved = 0;
            var merged = 0;
            var featuresMoved = 0;
            foreach (var road in roads)
            {
                var pts = road.Points;
                var along = new float[pts.Count];
                for (var i = 1; i < pts.Count; i++)
                    along[i] = along[i - 1] + math.distance(pts[i - 1].xz, pts[i].xz);
                var joins = new List<(int Index, (int, int) Key)>();
                var junctionAlong = new List<float>();
                for (var i = 0; i < pts.Count; i++)
                {
                    var key = PointKey(pts[i].xz);
                    if (endsAt.ContainsKey(key) && roadAt[key][0].Road == road)
                        joins.Add((i, key));
                    if (IsRoadJunction(key))
                        junctionAlong.Add(along[i]);
                }
                if (joins.Count == 0)
                    continue;

                // Group joins closer than PathJoinMergeMeters (no road junction between them).
                var groups = new List<List<(int Index, (int, int) Key)>>();
                foreach (var j in joins)
                {
                    var last = groups.Count > 0 ? groups[groups.Count - 1] : null;
                    if (last != null && along[j.Index] - along[last[last.Count - 1].Index] <= PathJoinMergeMeters &&
                        !junctionAlong.Any(a => a > along[last[last.Count - 1].Index] && a < along[j.Index]))
                        last.Add(j);
                    else
                        groups.Add(new List<(int Index, (int, int) Key)> { j });
                }

                // Each group's target position along the road, worked out before any point is inserted.
                var targets = new List<(List<(int Index, (int, int) Key)> Group, float S)>();
                foreach (var g in groups)
                {
                    var s = g.Average(j => along[j.Index]);
                    var before = junctionAlong.Where(a => a <= s).DefaultIfEmpty(float.NaN).Max();
                    var after = junctionAlong.Where(a => a >= s).DefaultIfEmpty(float.NaN).Min();
                    var lo = float.IsNaN(before) ? float.NegativeInfinity : before + PathJoinJunctionClearanceMeters;
                    var hi = float.IsNaN(after) ? float.PositiveInfinity : after - PathJoinJunctionClearanceMeters;
                    if (lo > hi)
                        s = float.IsNaN(before) || float.IsNaN(after) ? s : (before + after) * 0.5f;
                    else
                        s = math.clamp(s, lo, hi);
                    s = math.clamp(s, 0.5f, along[pts.Count - 1] - 0.5f);
                    targets.Add((g, s));
                }

                // Highest position first, so inserting a point never shifts a later target's segment.
                foreach (var (g, s) in targets.OrderByDescending(t => t.S))
                {
                    if (g.Count == 1 && math.abs(along[g[0].Index] - s) < 0.5f)
                        continue;
                    var seg = 1;
                    while (seg < pts.Count - 1 && along[seg] < s)
                        seg++;
                    float3 target;
                    if (math.abs(along[seg] - s) < 0.5f && seg < pts.Count - 1 && !IsRoadJunction(PointKey(pts[seg].xz)))
                        target = pts[seg];
                    else if (math.abs(along[seg - 1] - s) < 0.5f && seg - 1 > 0 && !IsRoadJunction(PointKey(pts[seg - 1].xz)))
                        target = pts[seg - 1];
                    else
                    {
                        var t = (s - along[seg - 1]) / math.max(0.01f, along[seg] - along[seg - 1]);
                        target = math.lerp(pts[seg - 1], pts[seg], t);
                        if (road.TerrainHeights.Count == pts.Count)
                            road.TerrainHeights.Insert(seg, math.lerp(road.TerrainHeights[seg - 1], road.TerrainHeights[seg], t));
                        pts.Insert(seg, target);
                    }
                    foreach (var j in g)
                        foreach (var (path, end) in endsAt[j.Key])
                        {
                            var idx = end == 0 ? 0 : path.Points.Count - 1;
                            path.Points[idx] = target;
                            if (path.TerrainHeights.Count == path.Points.Count)
                                path.TerrainHeights[idx] = target.y;
                            moved++;
                        }
                    // The crossing, stop sign or signal mapped where a path met the road belongs to that crossing:
                    // it moves with the join. Left behind, a stop sign 10.9 m from the ring's north junction drew two
                    // stop lines across the road where no path crossed any more. A mapped crossing dropped as being
                    // near the junction becomes the moved join's own crosswalk.
                    var oldPoints = g.Select(j => pts[pts.FindIndex(q => PointKey(q.xz) == j.Key)].xz).ToList();
                    bool AtOldJoin(float3 f) => oldPoints.Any(o => math.distance(o, f.xz) < 0.06f);
                    // A stop sign mapped for a path crossing drew two stop lines across the road (and no crosswalk):
                    // dropped, the crossing gets a crosswalk instead.
                    featuresMoved += road.StopSigns.RemoveAll(f => AtOldJoin(f.Position));
                    foreach (var list in new[] { road.Crosswalks, road.TrafficLights })
                    {
                        var hits = list.Where(f => AtOldJoin(f.Position)).ToList();
                        list.RemoveAll(hits.Contains);
                        if (hits.Count > 0)
                        {
                            hits[0].Position = target;
                            list.Add(hits[0]);
                            featuresMoved++;
                        }
                    }
                    var junctionHit = road.JunctionCrossings.FirstOrDefault(f => AtOldJoin(f.Position));
                    if (junctionHit != null && !road.Crosswalks.Any(f => math.distance(f.Position.xz, target.xz) < 0.06f))
                    {
                        road.JunctionCrossings.Remove(junctionHit);
                        junctionHit.Position = target;
                        road.Crosswalks.Add(junctionHit);
                        featuresMoved++;
                    }
                    if (g.Count > 1)
                        merged += g.Count - 1;
                }
            }
            Mod.log.Info($"Path joins: {moved} path ends moved, {merged} joins merged into a neighbour within {PathJoinMergeMeters:F0} m, {featuresMoved} crossings/stop signs/signals moved with them; " +
                         $"joins kept {PathJoinJunctionClearanceMeters:F0} m from road junctions.");
        }

        // A path joining a road ends with a straight piece this long, square to the road.
        private const float PathApproachMeters = 15f;
        // ... but never shorter than this; a shorter one is left as it is.
        private const float MinPathApproachMeters = 8f;

        /// <summary>
        /// Where a footpath joins a road mid-block, CS2 hides the path's last piece under the road only when that piece
        /// is long enough to start outside the road. A short last piece (OSM's kerb point, 6-7 m from the centre line)
        /// next to a road junction had its paving drawn across the sidewalk and lanes (Volunteer Park's entrance;
        /// reproduced on empty land with the bridge's path_join_test: a 7 m path 5 m from a T-junction shows it, a
        /// 15 m one square to the road does not, even 3 m from the junction or at 60 degrees). So every path end on a
        /// road point gets a last piece PathApproachMeters long, straight out from the road: the path's points
        /// closer than that are dropped and one point is put square to the road. Points another way uses are kept (the
        /// approach is shortened to stop before them). Ends at a road junction, where "square" has no single meaning,
        /// are left as they are.
        /// </summary>
        private static void SquarePathApproaches(List<OsmWay> ways)
        {
            var roads = ways.Where(IsVehicleRoad).ToList();
            var roadAt = new Dictionary<(int, int), List<(OsmWay Road, int Index)>>();
            foreach (var road in roads)
                for (var i = 0; i < road.Points.Count; i++)
                {
                    var key = PointKey(road.Points[i].xz);
                    if (!roadAt.TryGetValue(key, out var list))
                        roadAt[key] = list = new List<(OsmWay, int)>();
                    list.Add((road, i));
                }
            var useCount = new Dictionary<(int, int), int>();
            foreach (var w in ways)
                foreach (var key in w.Points.Select(pt => PointKey(pt.xz)).Distinct())
                    useCount[key] = useCount.TryGetValue(key, out var c) ? c + 1 : 1;

            // Path points on a 1 m grid: a new square point within 2 m of one takes it over, so two paths reaching the
            // same road point from the same side share one point instead of leaving two nodes centimetres apart.
            var pathPointGrid = new Dictionary<(int, int), List<float3>>();
            (int, int) Cell1(float2 q) => ((int)math.floor(q.x), (int)math.floor(q.y));
            void AddPathPoint(float3 q)
            {
                var c = Cell1(q.xz);
                if (!pathPointGrid.TryGetValue(c, out var l))
                    pathPointGrid[c] = l = new List<float3>();
                l.Add(q);
            }
            bool TryPathPointNear(float3 q, out float3 found)
            {
                found = default;
                var best = 2f;
                var c = Cell1(q.xz);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (pathPointGrid.TryGetValue((c.Item1 + dx, c.Item2 + dz), out var l))
                            foreach (var cand in l)
                            {
                                var d = math.distance(cand.xz, q.xz);
                                if (d < best)
                                {
                                    best = d;
                                    found = cand;
                                }
                            }
                return best < 2f;
            }
            foreach (var w in ways)
                if (IsPathLike(w))
                    foreach (var q in w.Points)
                        AddPathPoint(q);

            var squared = 0;
            var snappedToPathPoint = 0;
            var skippedShort = 0;
            foreach (var path in ways)
            {
                if (!IsPathLike(path) || path.Points.Count < 2)
                    continue;
                for (var end = 0; end < 2; end++)
                {
                    var pts = path.Points;
                    var n = pts.Count;
                    var endIndex = end == 0 ? 0 : n - 1;
                    var step = end == 0 ? 1 : -1;
                    var endPoint = pts[endIndex];
                    if (!roadAt.TryGetValue(PointKey(endPoint.xz), out var onRoads))
                        continue;
                    if (onRoads.Select(r => r.Road).Distinct().Count() > 1 ||
                        onRoads[0].Index == 0 || onRoads[0].Index == onRoads[0].Road.Points.Count - 1)
                        continue; // a road junction or a road end
                    var (road, ri) = onRoads[0];
                    var roadDir = math.normalizesafe(road.Points[ri + 1].xz - road.Points[ri - 1].xz);
                    var normal = new float2(-roadDir.y, roadDir.x);
                    var side = math.dot(pts[endIndex + step].xz - endPoint.xz, normal) >= 0f ? 1f : -1f;

                    // How far out the approach can go: up to the first point another way uses, and within the path.
                    var length = PathApproachMeters;
                    var along = 0f;
                    var firstKept = -1; // first path point (walking from the road) at or beyond the approach length
                    for (var k = endIndex + step; k >= 0 && k < n; k += step)
                    {
                        along += math.distance(pts[k - step].xz, pts[k].xz);
                        var shared = useCount.TryGetValue(PointKey(pts[k].xz), out var uses) && uses > 1;
                        var isWayEnd = k == 0 || k == n - 1;
                        if (shared || isWayEnd)
                        {
                            length = math.min(length, 0.9f * math.distance(pts[k].xz, endPoint.xz));
                            firstKept = k;
                            break;
                        }
                        if (math.distance(pts[k].xz, endPoint.xz) >= length)
                        {
                            firstKept = k;
                            break;
                        }
                    }
                    if (firstKept < 0 || length < MinPathApproachMeters)
                    {
                        skippedShort++;
                        continue;
                    }

                    var outXz = endPoint.xz + normal * side * length;
                    var t = math.saturate(length / math.max(0.01f, math.distance(pts[firstKept].xz, endPoint.xz)));
                    var outPoint = new float3(outXz.x, math.lerp(endPoint.y, pts[firstKept].y, t), outXz.y);
                    var heights = path.TerrainHeights.Count == n ? path.TerrainHeights : null;
                    var outHeight = heights != null ? math.lerp(heights[endIndex], heights[firstKept], t) : outPoint.y;
                    if (TryPathPointNear(outPoint, out var existing))
                    {
                        outPoint = existing;
                        outHeight = existing.y;
                        snappedToPathPoint++;
                    }
                    else
                    {
                        AddPathPoint(outPoint);
                    }

                    // Drop the points between the road and firstKept, then put the square point there.
                    var lo = math.min(endIndex, firstKept);
                    var hi = math.max(endIndex, firstKept);
                    var removeFrom = lo + 1;
                    var removeCount = hi - lo - 1;
                    if (removeCount > 0)
                    {
                        pts.RemoveRange(removeFrom, removeCount);
                        heights?.RemoveRange(removeFrom, removeCount);
                    }
                    pts.Insert(removeFrom, outPoint);
                    heights?.Insert(removeFrom, outHeight);
                    squared++;
                }
            }
            Mod.log.Info($"Path approaches: {squared} path ends on a road given a {PathApproachMeters:F0} m piece square to the road ({snappedToPathPoint} sharing another path's point) " +
                         $"({skippedShort} left as they were: too short or another way within {MinPathApproachMeters:F0} m).");
        }

        // Crosswalks closer than this along one road (with no junction between them) become one.
        private const float CrosswalkMergeMeters = 20f;

        private static (int, int) PointKey(float2 p) => ((int)math.round(p.x * 100f), (int)math.round(p.y * 100f));

        /// <summary>
        /// Groups the road's crosswalks along the road into runs no longer than CrosswalkMergeMeters with no junction
        /// point inside, keeps the one nearest each run's middle and records where the others went. Returns how many
        /// were dropped.
        /// </summary>
        private static int MergeCloseCrosswalks(OsmWay road, HashSet<(int, int)> junctionKeys, Dictionary<float3, float3> mergedInto)
        {
            if (road.Crosswalks.Count < 2)
                return 0;
            var points = road.Points;
            var along = new float[points.Count];
            for (var i = 1; i < points.Count; i++)
                along[i] = along[i - 1] + math.distance(points[i - 1].xz, points[i].xz);

            var items = new List<(OsmRoadNodeFeature Feature, int Index)>();
            foreach (var c in road.Crosswalks)
            {
                var index = points.FindIndex(p => math.distance(p.xz, c.Position.xz) < 0.01f);
                if (index >= 0)
                    items.Add((c, index));
            }
            if (items.Count < 2)
                return 0;
            items.Sort((a, b) => a.Index.CompareTo(b.Index));

            bool JunctionBetween(int from, int to)
            {
                for (var k = from + 1; k < to; k++)
                {
                    if (junctionKeys.Contains(PointKey(points[k].xz)))
                        return true;
                }
                return false;
            }

            var dropped = new HashSet<OsmRoadNodeFeature>();
            for (var start = 0; start < items.Count;)
            {
                var end = start + 1;
                while (end < items.Count &&
                       along[items[end].Index] - along[items[start].Index] <= CrosswalkMergeMeters &&
                       !JunctionBetween(items[end - 1].Index, items[end].Index))
                    end++;
                if (end - start > 1)
                {
                    var middle = (along[items[start].Index] + along[items[end - 1].Index]) * 0.5f;
                    var keep = start;
                    for (var k = start + 1; k < end; k++)
                    {
                        if (math.abs(along[items[k].Index] - middle) < math.abs(along[items[keep].Index] - middle))
                            keep = k;
                    }
                    for (var k = start; k < end; k++)
                    {
                        if (k == keep)
                            continue;
                        dropped.Add(items[k].Feature);
                        mergedInto[items[k].Feature.Position] = items[keep].Feature.Position;
                    }
                }
                start = end;
            }
            road.Crosswalks.RemoveAll(dropped.Contains);
            return dropped.Count;
        }

        private static bool IsPathLike(OsmWay way)
        {
            switch ((way.Highway ?? string.Empty).ToLowerInvariant())
            {
                case "footway": case "path": case "pedestrian": case "cycleway": case "steps": case "bridleway":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Nearest point on any road within PathToRoadSnapMeters. An existing road point within RoadPointReuseMeters is
        /// reused; otherwise the projected point is inserted into that road so CS2 has a node to join the path to.
        /// </summary>
        /// <param name="roadIndex">Spatial prefilter over <paramref name="roads"/>: only roads near p are scanned, in list order.</param>
        private static bool TrySnapToRoad(float3 p, List<OsmWay> roads, PolylineIndex roadIndex, out float3 target)
        {
            target = default;
            OsmWay bestRoad = null;
            var bestSegment = -1;
            var bestT = 0f;
            var bestDistance = PathToRoadSnapMeters;
            foreach (var r in roadIndex.Query(p.xz - PathToRoadSnapMeters, p.xz + PathToRoadSnapMeters))
            {
                var road = roads[r];
                var pts = road.Points;
                for (var i = 1; i < pts.Count; i++)
                {
                    var a = pts[i - 1].xz;
                    var b = pts[i].xz;
                    if (math.min(a.x, b.x) - bestDistance > p.x || math.max(a.x, b.x) + bestDistance < p.x ||
                        math.min(a.y, b.y) - bestDistance > p.z || math.max(a.y, b.y) + bestDistance < p.z)
                        continue;
                    var ab = b - a;
                    var len2 = math.lengthsq(ab);
                    var t = len2 > 1e-6f ? math.saturate(math.dot(p.xz - a, ab) / len2) : 0f;
                    var d = math.distance(p.xz, a + ab * t);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        bestRoad = road;
                        bestSegment = i;
                        bestT = t;
                    }
                }
            }

            if (bestRoad == null)
                return false;

            var p0 = bestRoad.Points[bestSegment - 1];
            var p1 = bestRoad.Points[bestSegment];
            var projected = math.lerp(p0, p1, bestT);
            if (math.distance(projected.xz, p0.xz) <= RoadPointReuseMeters) { target = p0; return true; }
            if (math.distance(projected.xz, p1.xz) <= RoadPointReuseMeters) { target = p1; return true; }

            // TerrainHeights runs parallel to Points; keep it aligned.
            if (bestRoad.TerrainHeights.Count == bestRoad.Points.Count)
                bestRoad.TerrainHeights.Insert(bestSegment,
                    math.lerp(bestRoad.TerrainHeights[bestSegment - 1], bestRoad.TerrainHeights[bestSegment], bestT));
            bestRoad.Points.Insert(bestSegment, projected);
            target = projected;
            return true;
        }

        /// <summary>
        /// Every OSM node shared by several ways must have one height, or CS2 builds separate nodes there. Elevation
        /// offsets (bridge/tunnel/layer/covered = +-10 m etc.) are per way, and HarmonizeGroupElevations only reconciles
        /// two way ENDS meeting; a structure joined mid-span, or a structure end meeting another way's middle, kept a
        /// 1-20 m difference (27 junction failures on the UW crop). Here, for each shared node:
        /// - target height: a way passing THROUGH the node keeps its height (a footbridge does not sag where a path meets
        ///   it); if the node is only way ends, the way closest to the ground (smallest |offset|) wins;
        /// - every other way there is pulled to the target with a smoothstep ramp along the way, at most RampLengthMeters
        ///   and at most half the distance to that way's neighbouring shared nodes, so ramps never overlap or move another
        ///   junction. UnifySharedPoints then makes the values bit-identical.
        /// </summary>
        private static void AnchorSharedNodeHeights(List<OsmWay> ways, Dictionary<long, float2> sourcePositions)
        {
            var uses = new Dictionary<long, List<OsmWay>>();
            foreach (var w in ways)
                foreach (var id in w.NodeIds.Distinct())
                {
                    if (!uses.TryGetValue(id, out var l)) uses[id] = l = new List<OsmWay>();
                    l.Add(w);
                }

            // Anchors per way: (point index, node id) for shared nodes found among its points.
            var anchors = new Dictionary<OsmWay, List<(int index, long node)>>();
            var cumulative = new Dictionary<OsmWay, float[]>();
            foreach (var w in ways)
            {
                if (w.Points.Count < 2)
                    continue;
                var list = new List<(int, long)>();
                foreach (var id in w.NodeIds.Distinct())
                {
                    if (!uses.TryGetValue(id, out var l) || l.Count < 2 || !sourcePositions.TryGetValue(id, out var xz))
                        continue;
                    for (var i = 0; i < w.Points.Count; i++)
                        if (math.distancesq(w.Points[i].xz, xz) < 1e-6f)
                        {
                            list.Add((i, id));
                            break;
                        }
                }
                if (list.Count == 0)
                    continue;
                anchors[w] = list.OrderBy(a => a.Item1).ToList();
                var c = new float[w.Points.Count];
                for (var i = 1; i < c.Length; i++)
                    c[i] = c[i - 1] + math.distance(w.Points[i - 1].xz, w.Points[i].xz);
                cumulative[w] = c;
            }

            // Target height per shared node.
            var target = new Dictionary<long, float>();
            foreach (var kv in uses)
            {
                if (kv.Value.Count < 2)
                    continue;
                var candidates = new List<(OsmWay way, float y, bool passes)>();
                foreach (var w in kv.Value)
                {
                    if (!anchors.TryGetValue(w, out var al))
                        continue;
                    var a = al.FirstOrDefault(x => x.node == kv.Key);
                    if (a.node != kv.Key)
                        continue;
                    var passes = a.index > 0 && a.index < w.Points.Count - 1;
                    candidates.Add((w, w.Points[a.index].y, passes));
                }
                if (candidates.Count < 2)
                    continue;
                var pick = candidates
                    .OrderBy(x => x.passes ? 0 : 1)
                    .ThenBy(x => math.abs(x.way.BaseElevationOffsetMeters))
                    .First();
                target[kv.Key] = pick.y;
            }

            // Corrections (computed from the current heights, then applied together).
            var adjustedWays = 0;
            var adjustedNodes = 0;
            foreach (var kv in anchors)
            {
                var w = kv.Key;
                var al = kv.Value;
                var c = cumulative[w];
                var deltas = new float[w.Points.Count];
                var changed = false;
                for (var k = 0; k < al.Count; k++)
                {
                    var (index, node) = al[k];
                    if (!target.TryGetValue(node, out var t))
                        continue;
                    var delta = t - w.Points[index].y;
                    if (math.abs(delta) < 0.01f)
                        continue;
                    var s = c[index];
                    var leftLimit = k > 0 ? (s - c[al[k - 1].index]) * 0.5f : s;
                    var rightLimit = k < al.Count - 1 ? (c[al[k + 1].index] - s) * 0.5f : c[c.Length - 1] - s;
                    var rl = math.min(RampLengthMeters, leftLimit);
                    var rr = math.min(RampLengthMeters, rightLimit);
                    for (var i = 0; i < w.Points.Count; i++)
                    {
                        var d = c[i] - s;
                        var r = d < 0f ? rl : rr;
                        if (i != index && (r <= 0f || math.abs(d) >= r))
                            continue;
                        var u = i == index ? 0f : math.abs(d) / r;
                        deltas[i] += delta * (1f - u * u * (3f - 2f * u));
                    }
                    changed = true;
                    adjustedNodes++;
                }
                if (!changed)
                    continue;
                for (var i = 0; i < w.Points.Count; i++)
                {
                    if (deltas[i] == 0f)
                        continue;
                    var p = w.Points[i];
                    p.y += deltas[i];
                    w.Points[i] = p;
                }
                adjustedWays++;
            }

            if (adjustedNodes > 0)
                Mod.log.Info($"Shared-node heights: pulled {adjustedNodes} way/node pairs on {adjustedWays} ways to the junction height (structures joined mid-span or end-to-middle).");
        }

        // Points of different ways at the same XZ whose heights differ by less than this are made bit-identical.
        private const float SharedPointHeightToleranceMeters = 0.05f;

        /// <summary>
        /// CS2 only joins course points that are exactly equal. Height arithmetic can leave a shared point one float step
        /// apart between ways (e.g. a bridge end at terrain + 10 - 10 vs the neighbour at terrain), and CS2 then built two
        /// unconnected nodes (the "structure" junction failures on the UW crop: 9.553215 vs 9.553216). As a last step, all
        /// points of different ways with bit-identical XZ and heights within SharedPointHeightToleranceMeters get one value,
        /// preferring a non-structural way's point. Larger differences (a bridge over a road) are left alone.
        /// </summary>
        private static void UnifySharedPoints(List<OsmWay> ways)
        {
            var groups = new Dictionary<(int, int), List<(OsmWay way, int index)>>();
            foreach (var way in ways)
            {
                for (var i = 0; i < way.Points.Count; i++)
                {
                    var p = way.Points[i];
                    var key = (math.asint(p.x), math.asint(p.z));
                    if (!groups.TryGetValue(key, out var list))
                        groups[key] = list = new List<(OsmWay, int)>();
                    list.Add((way, i));
                }
            }

            var unified = 0;
            foreach (var list in groups.Values)
            {
                if (list.Count < 2 || list.Select(e => e.way).Distinct().Count() < 2)
                    continue;
                var reference = list.OrderBy(e => e.way.UsesStructuralElevation ? 1 : 0).First();
                var target = reference.way.Points[reference.index];
                foreach (var (way, index) in list)
                {
                    var p = way.Points[index];
                    if (p.Equals(target) || math.abs(p.y - target.y) > SharedPointHeightToleranceMeters)
                        continue;
                    way.Points[index] = target;
                    unified++;
                }
            }
            if (unified > 0)
                Mod.log.Info($"Unified {unified} shared points that differed only by float rounding (CS2 joins exact matches only).");
        }

        private static void HarmonizeGroupElevations(List<OsmWay> ways)
        {
            if (ways.Count < 2)
                return;

            var nodeToWays = new Dictionary<long, List<(int index, bool isStart)>>();
            for (var i = 0; i < ways.Count; i++)
            {
                var way = ways[i];
                if (way.NodeIds.Count < 2)
                    continue;

                var e0 = way.NodeIds[0];
                var e1 = way.NodeIds[way.NodeIds.Count - 1];

                if (!nodeToWays.TryGetValue(e0, out var l0))
                    nodeToWays[e0] = l0 = new List<(int, bool)>();
                l0.Add((i, true));

                if (e0 != e1)
                {
                    if (!nodeToWays.TryGetValue(e1, out var l1))
                        nodeToWays[e1] = l1 = new List<(int, bool)>();
                    l1.Add((i, false));
                }
            }

            foreach (var kvp in nodeToWays)
            {
                var entries = kvp.Value;
                if (entries.Count < 2)
                    continue;

                for (var a = 0; a < entries.Count; a++)
                {
                    for (var b = a + 1; b < entries.Count; b++)
                    {
                        var wayA = ways[entries[a].index];
                        var wayB = ways[entries[b].index];
                        if (ApproximatelyEqual(wayA.BaseElevationOffsetMeters, wayB.BaseElevationOffsetMeters))
                            continue;

                        // Ramp the STRUCTURAL way (the one further from ground level, e.g. a bridge or tunnel) so
                        // its end meets the neighbour's height. Ramping the neighbour instead (the old behaviour for
                        // bridges) lifted ordinary roads; on roads shorter than the ramp, or with a junction inside
                        // it, the shared junction node ended up metres above the crossing road, and CS2 built two
                        // unconnected nodes there (unjoined T-junctions).
                        var structuralIsA = math.abs(wayA.BaseElevationOffsetMeters) > math.abs(wayB.BaseElevationOffsetMeters);
                        var structuralWay = structuralIsA ? wayA : wayB;
                        var neighbourWay = structuralIsA ? wayB : wayA;
                        var isStructuralStart = structuralIsA ? entries[a].isStart : entries[b].isStart;

                        // Height change at the shared end: bring the structure down (bridge) or up (tunnel)
                        // to the neighbour's offset.
                        var delta = neighbourWay.BaseElevationOffsetMeters - structuralWay.BaseElevationOffsetMeters;
                        var startIdx = isStructuralStart ? 0 : structuralWay.Points.Count - 1;
                        var step = isStructuralStart ? 1 : -1;

                        // Ramps from both ends must not overlap, or they would overshoot past the neighbour height.
                        var structuralLength = 0f;
                        for (var k = 1; k < structuralWay.Points.Count; k++)
                        {
                            structuralLength += math.distance(
                                new float2(structuralWay.Points[k - 1].x, structuralWay.Points[k - 1].z),
                                new float2(structuralWay.Points[k].x, structuralWay.Points[k].z));
                        }
                        var rampLength = math.max(1f, math.min(RampLengthMeters, structuralLength * 0.5f));

                        var accumulated = 0f;
                        var idx = startIdx;
                        while (idx >= 0 && idx < structuralWay.Points.Count)
                        {
                            if (idx != startIdx)
                            {
                                var prev = idx - step;
                                accumulated += math.distance(
                                    new float2(structuralWay.Points[prev].x, structuralWay.Points[prev].z),
                                    new float2(structuralWay.Points[idx].x, structuralWay.Points[idx].z));
                            }

                            var t = math.saturate(accumulated / rampLength);
                            var weight = 1f - (t * t * (3f - 2f * t));
                            if (weight < 0.001f)
                                break;

                            var p = structuralWay.Points[idx];
                            p.y += delta * weight;
                            structuralWay.Points[idx] = p;

                            idx += step;
                        }
                    }
                }
            }
        }

        private static bool CanMergeWays(OsmWay a, OsmWay b)
        {
            if (a == null || b == null)
                return false;

            if (!string.Equals(a.Highway?.Trim(), b.Highway?.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (!AreCompatibleLabels(a.Name, b.Name) || !AreCompatibleLabels(a.Ref, b.Ref))
                return false;

            if (a.UsesStructuralElevation != b.UsesStructuralElevation)
                return false;

            // A roundabout ring must stay separate from the roads that join it (ConvertRoundabouts needs both).
            if (a.IsRoundabout || b.IsRoundabout)
                return false;

            // Parking aisles may be replaced by a generated lot; keep them apart from driveways and streets.
            if (a.IsParkingAisle != b.IsParkingAisle)
                return false;

            if (!ApproximatelyEqual(a.BaseElevationOffsetMeters, b.BaseElevationOffsetMeters))
                return false;

            if (a.InclineDirection != b.InclineDirection)
                return false;

            if (!ApproximatelyEqual(a.InclinePercent, b.InclinePercent))
                return false;

            if (a.Lanes > 0 && b.Lanes > 0 && a.Lanes != b.Lanes)
                return false;

            if (a.LanesForward != b.LanesForward ||
                a.LanesBackward != b.LanesBackward ||
                a.LanesBothWays != b.LanesBothWays)
                return false;

            if (a.ParkingSide != b.ParkingSide || a.ParkingStyle != b.ParkingStyle)
                return false;

            return true;
        }

        private static bool AreCompatibleLabels(string a, string b)
        {
            a = (a ?? string.Empty).Trim();
            b = (b ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return true;

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ApproximatelyEqual(float a, float b, float tolerance = 0.001f)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        private static bool IsStraightContinuation(OsmWay a, OsmWay b)
        {
            if (a == null || b == null || a.Points.Count < 2 || b.Points.Count < 2)
                return true;

            var aVector = a.Points[a.Points.Count - 1] - a.Points[a.Points.Count - 2];
            var bVector = b.Points[1] - b.Points[0];
            aVector.y = 0f;
            bVector.y = 0f;

            var aLength = math.length(aVector);
            var bLength = math.length(bVector);
            if (aLength <= 0.001f || bLength <= 0.001f)
                return true;

            var dot = math.dot(aVector / aLength, bVector / bLength);
            return dot >= 0.95f;
        }

        private static bool TryReadLaneCount(string value, out int laneCount)
        {
            laneCount = 0;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Split(new[] { ';', ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out laneCount) && laneCount > 0)
                    return true;
            }

            return false;
        }

        private static bool TryReadSupportedRoadWay(XmlElement wayElement, out string highway, out bool isArea)
        {
            highway = string.Empty;
            isArea = false;
            var railway = string.Empty;
            var tags = wayElement.SelectNodes("tag");

            if (tags != null)
            {
                foreach (XmlElement tag in tags)
                {
                    var key = tag.GetAttribute("k");
                    var value = tag.GetAttribute("v");

                    if (key == "highway")
                        highway = value;
                    else if (key == "railway")
                        railway = value;
                    else if (key == "area" && value == "yes")
                        isArea = true;
                }
            }

            if (string.IsNullOrEmpty(highway))
                highway = railway;

            return !isArea && !string.IsNullOrEmpty(highway) && IsSupportedHighway(highway);
        }

        private static bool TryReadSupportedRoadWay(XmlElement wayElement, bool useAllowedHighways, HashSet<string> allowedHighways, out string highway, out bool isArea)
        {
            if (!TryReadSupportedRoadWay(wayElement, out highway, out isArea))
                return false;

            return !useAllowedHighways || (allowedHighways != null && allowedHighways.Contains(highway));
        }

        private static void IncludeBounds(OsmDocument document, OsmWay way)
        {
            foreach (var point in way.Points)
            {
                if (!document.HasBounds)
                {
                    document.Min = point;
                    document.Max = point;
                    document.HasBounds = true;
                    continue;
                }

                document.Min = math.min(document.Min, point);
                document.Max = math.max(document.Max, point);
            }
        }

        private static int NormalizeContiguousLaneCounts(List<OsmWay> ways)
        {
            if (ways == null || ways.Count == 0)
                return 0;

            const float endpointToleranceMeters = 20f;
            var inferredLaneCount = 0;
            var groups = ways
                .Select((way, index) => (Way: way, Index: index, Key: GetLaneNormalizationKey(way)))
                .Where(entry => !string.IsNullOrEmpty(entry.Key) && entry.Way.Points.Count >= 2)
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var entries = group.ToList();
                if (entries.Count < 2)
                    continue;

                var parent = new int[entries.Count];
                for (var i = 0; i < parent.Length; i++)
                    parent[i] = i;

                var endpointBuckets = BuildEndpointBuckets(entries, endpointToleranceMeters);

                for (var i = 0; i < entries.Count; i++)
                {
                    var way = entries[i].Way;
                    if (way.Points.Count < 2)
                        continue;

                    UnionNearbyWays(entries, parent, endpointBuckets, i, way.Points[0], endpointToleranceMeters);
                    UnionNearbyWays(entries, parent, endpointBuckets, i, way.Points[way.Points.Count - 1], endpointToleranceMeters);
                }

                var components = new Dictionary<int, List<int>>();
                for (var i = 0; i < entries.Count; i++)
                {
                    var root = Find(parent, i);
                    if (!components.TryGetValue(root, out var component))
                    {
                        component = new List<int>();
                        components[root] = component;
                    }

                    component.Add(i);
                }

                foreach (var component in components.Values)
                {
                    var laneCount = DetermineComponentLaneCount(entries, component);
                    if (laneCount <= 0)
                        continue;

                    foreach (var entryIndex in component)
                    {
                        var way = entries[entryIndex].Way;
                        if (way.HasExplicitLaneCount || way.Lanes > 0)
                            continue;

                        way.Lanes = laneCount;
                        inferredLaneCount++;
                    }
                }
            }

            inferredLaneCount += NormalizeRemainingLaneCountsByHighway(ways);

            return inferredLaneCount;
        }

        private static int NormalizeRemainingLaneCountsByHighway(List<OsmWay> ways)
        {
            const float endpointToleranceMeters = 20f;
            var inferredLaneCount = 0;
            var groups = ways
                .Select((way, index) => (Way: way, Index: index, Key: (way.Highway ?? string.Empty).Trim()))
                .Where(entry => !string.IsNullOrEmpty(entry.Key) && entry.Way.Points.Count >= 2)
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var entries = group.ToList();
                if (entries.Count < 2)
                    continue;

                var parent = new int[entries.Count];
                for (var i = 0; i < parent.Length; i++)
                    parent[i] = i;

                var endpointBuckets = BuildEndpointBuckets(entries, endpointToleranceMeters);

                for (var i = 0; i < entries.Count; i++)
                {
                    var way = entries[i].Way;
                    if (way.Points.Count < 2)
                        continue;

                    UnionNearbyWays(entries, parent, endpointBuckets, i, way.Points[0], endpointToleranceMeters);
                    UnionNearbyWays(entries, parent, endpointBuckets, i, way.Points[way.Points.Count - 1], endpointToleranceMeters);
                }

                var components = new Dictionary<int, List<int>>();
                for (var i = 0; i < entries.Count; i++)
                {
                    var root = Find(parent, i);
                    if (!components.TryGetValue(root, out var component))
                    {
                        component = new List<int>();
                        components[root] = component;
                    }

                    component.Add(i);
                }

                foreach (var component in components.Values)
                {
                    var laneCount = DetermineComponentLaneCount(entries, component);
                    if (laneCount <= 0)
                        continue;

                    foreach (var entryIndex in component)
                    {
                        var way = entries[entryIndex].Way;
                        if (way.HasExplicitLaneCount || way.Lanes > 0)
                            continue;

                        way.Lanes = laneCount;
                        inferredLaneCount++;
                    }
                }
            }

            return inferredLaneCount;
        }

        private static Dictionary<string, List<int>> BuildEndpointBuckets(
            List<(OsmWay Way, int Index, string Key)> entries,
            float toleranceMeters)
        {
            var buckets = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < entries.Count; i++)
            {
                var way = entries[i].Way;
                AddBucketEntry(buckets, MakeLaneEndpointBucketKey(entries[i].Key, way.Points[0], toleranceMeters), i);
                AddBucketEntry(buckets, MakeLaneEndpointBucketKey(entries[i].Key, way.Points[way.Points.Count - 1], toleranceMeters), i);
            }

            return buckets;
        }

        private static void UnionNearbyWays(
            List<(OsmWay Way, int Index, string Key)> entries,
            int[] parent,
            Dictionary<string, List<int>> buckets,
            int wayIndex,
            float3 point,
            float toleranceMeters)
        {
            var roadKey = entries[wayIndex].Key;
            var binX = (int)Math.Floor(point.x / toleranceMeters);
            var binZ = (int)Math.Floor(point.z / toleranceMeters);

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var key = MakeLaneEndpointBucketKey(roadKey, binX + dx, binZ + dz);
                    if (!buckets.TryGetValue(key, out var candidates))
                        continue;

                    foreach (var candidateIndex in candidates)
                    {
                        if (candidateIndex == wayIndex)
                            continue;

                        if (!EndpointsWithinTolerance(entries[wayIndex].Way, entries[candidateIndex].Way, toleranceMeters))
                            continue;

                        Union(parent, wayIndex, candidateIndex);
                    }
                }
            }
        }

        private static void AddBucketEntry(Dictionary<string, List<int>> buckets, string key, int index)
        {
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<int>();
                buckets[key] = list;
            }

            list.Add(index);
        }

        private static string MakeLaneEndpointBucketKey(string roadKey, float3 point, float toleranceMeters)
        {
            var binX = (int)Math.Floor(point.x / toleranceMeters);
            var binZ = (int)Math.Floor(point.z / toleranceMeters);
            return MakeLaneEndpointBucketKey(roadKey, binX, binZ);
        }

        private static string MakeLaneEndpointBucketKey(string roadKey, int binX, int binZ)
        {
            return $"{roadKey}|{binX}|{binZ}";
        }

        private static bool EndpointsWithinTolerance(OsmWay a, OsmWay b, float toleranceMeters)
        {
            if (a == null || b == null || a.Points.Count < 2 || b.Points.Count < 2)
                return false;

            var aStart = a.Points[0];
            var aEnd = a.Points[a.Points.Count - 1];
            var bStart = b.Points[0];
            var bEnd = b.Points[b.Points.Count - 1];

            var toleranceSq = toleranceMeters * toleranceMeters;
            return math.distancesq(aStart, bStart) <= toleranceSq ||
                   math.distancesq(aStart, bEnd) <= toleranceSq ||
                   math.distancesq(aEnd, bStart) <= toleranceSq ||
                   math.distancesq(aEnd, bEnd) <= toleranceSq;
        }

        private static int DetermineComponentLaneCount(List<(OsmWay Way, int Index, string Key)> entries, List<int> component)
        {
            var counts = new Dictionary<int, int>();

            foreach (var entryIndex in component)
            {
                var way = entries[entryIndex].Way;
                if (way.Lanes <= 0)
                    continue;

                counts.TryGetValue(way.Lanes, out var count);
                counts[way.Lanes] = count + 1;
            }

            if (counts.Count == 0)
                return 0;

            var bestLane = 0;
            var bestCount = 0;
            var tied = false;

            foreach (var pair in counts)
            {
                if (pair.Value > bestCount)
                {
                    bestCount = pair.Value;
                    bestLane = pair.Key;
                    tied = false;
                    continue;
                }

                if (pair.Value == bestCount)
                    tied = true;
            }

            return tied ? 0 : bestLane;
        }

        private static int Find(int[] parent, int index)
        {
            if (parent[index] != index)
                parent[index] = Find(parent, parent[index]);

            return parent[index];
        }

        private static void Union(int[] parent, int a, int b)
        {
            var rootA = Find(parent, a);
            var rootB = Find(parent, b);
            if (rootA == rootB)
                return;

            parent[rootB] = rootA;
        }

        private static string GetLaneNormalizationKey(OsmWay way)
        {
            if (way == null)
                return string.Empty;

            var roadLabel = !string.IsNullOrWhiteSpace(way.Name) ? way.Name.Trim() : way.Ref.Trim();
            if (string.IsNullOrWhiteSpace(roadLabel))
                return string.Empty;

            return $"{way.Highway.Trim()}|{roadLabel}|{way.Lanes}|{way.LanesForward}|{way.LanesBackward}|{way.LanesBothWays}|{way.ParkingSide}|{way.ParkingStyle}|{way.HasBikeLanes}|{way.HasTurnRestrictions}|{way.HasRestrictedLanes}";
        }

        private static bool IsSupportedHighway(string highway)
        {
            switch (highway)
            {
                case "motorway":
                case "trunk":
                case "primary":
                case "secondary":
                case "tertiary":
                case "unclassified":
                case "residential":
                case "service":
                case "living_street":
                case "road":
                case "motorway_link":
                case "trunk_link":
                case "primary_link":
                case "secondary_link":
                case "tertiary_link":
                case "footway":
                case "steps":
                case "path":
                case "bridleway":
                case "monorail":
                case "rail":
                case "light_rail":
                case "subway":
                case "tram":
                case "track":
                case "cycleway":
                case "pedestrian":
                    return true;
                default:
                    return false;
            }
        }

        private static (double Lat, double Lon) GetCenter(IEnumerable<GeoNode> nodes)
        {
            double minLat = double.MaxValue, maxLat = double.MinValue;
            double minLon = double.MaxValue, maxLon = double.MinValue;

            foreach (var node in nodes)
            {
                minLat = Math.Min(minLat, node.Lat);
                maxLat = Math.Max(maxLat, node.Lat);
                minLon = Math.Min(minLon, node.Lon);
                maxLon = Math.Max(maxLon, node.Lon);
            }

            return ((minLat + maxLat) * 0.5d, (minLon + maxLon) * 0.5d);
        }

        private static (double Lat, double Lon)? TryGetFileBoundsCenter(XmlDocument xml)
        {
            var bounds = xml.SelectSingleNode("/osm/bounds") as XmlElement;
            if (bounds == null)
                return null;

            if (!TryReadDouble(bounds, "minlat", out var minLat) ||
                !TryReadDouble(bounds, "maxlat", out var maxLat) ||
                !TryReadDouble(bounds, "minlon", out var minLon) ||
                !TryReadDouble(bounds, "maxlon", out var maxLon))
            {
                return null;
            }

            return ((minLat + maxLat) * 0.5d, (minLon + maxLon) * 0.5d);
        }

        private static void Project(GeoNode node, double centerLat, double centerLon)
        {
            var latRad = DegreesToRadians(node.Lat);
            var lonRad = DegreesToRadians(node.Lon);
            var centerLatRad = DegreesToRadians(centerLat);
            var centerLonRad = DegreesToRadians(centerLon);

            node.X = EarthRadiusMeters * (lonRad - centerLonRad) * Math.Cos(centerLatRad);
            node.Y = EarthRadiusMeters * (latRad - centerLatRad);
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

        private static void Simplify(List<float3> points, float tolerance)
        {
            if (points.Count <= 2 || tolerance <= 0f)
                return;

            if (IsClosedLoop(points))
            {
                SimplifyClosedLoop(points, tolerance);
                return;
            }

            for (var i = points.Count - 2; i > 0; i--)
            {
                var distance = DistanceToSegment(points[i], points[i - 1], points[i + 1]);
                if (distance <= tolerance)
                    points.RemoveAt(i);
            }
        }

        private static void SimplifyClosedLoop(List<float3> points, float tolerance)
        {
            if (points.Count <= 4 || tolerance <= 0f)
                return;

            if (math.distance(new float2(points[0].x, points[0].z), new float2(points[points.Count - 1].x, points[points.Count - 1].z)) <= 0.5f)
                points.RemoveAt(points.Count - 1);

            if (points.Count <= 4)
            {
                points.Add(points[0]);
                return;
            }

            var changed = true;
            while (changed && points.Count > 4)
            {
                changed = false;

                for (var i = 1; i < points.Count; i++)
                {
                    if (points.Count <= 4)
                        break;

                    var prev = points[(i - 1 + points.Count) % points.Count];
                    var curr = points[i];
                    var next = points[(i + 1) % points.Count];

                    if (DistanceToSegment(curr, prev, next) > tolerance)
                        continue;

                    points.RemoveAt(i);
                    changed = true;
                    break;
                }
            }

            points.Add(points[0]);
        }

        private static bool IsClosedLoop(List<float3> points)
        {
            return points != null && points.Count >= 4 &&
                   math.distance(new float2(points[0].x, points[0].z), new float2(points[points.Count - 1].x, points[points.Count - 1].z)) <= 0.5f;
        }

        private static bool PointsAreNearlyEqual(float3 a, float3 b)
        {
            return math.distance(new float2(a.x, a.z), new float2(b.x, b.z)) <= 0.5f && Math.Abs(a.y - b.y) <= 1f;
        }

        private static float DistanceToSegment(float3 point, float3 start, float3 end)
        {
            var segment = end - start;
            var lengthSq = math.lengthsq(segment);
            if (lengthSq <= 0.0001f)
                return math.distance(point, start);

            var t = math.clamp(math.dot(point - start, segment) / lengthSq, 0f, 1f);
            return math.distance(point, start + segment * t);
        }

        /// <summary>
        /// Drops road points that lie on the straight line between the points kept around them: within planTolerance
        /// in plan (straight roads stay exactly on their OSM line) and heightTolerance in height. Every CS2 edge costs
        /// memory (about 48 KB each, measured), and the 15 m terrain split plus OSM's extra nodes on straight streets
        /// gave full Seattle ~400k edges, more than a 28 GB machine can hold; this removes about 40% of them.
        /// Kept always: way ends, points any other way also uses (junctions; bit-identical after UnifySharedPoints),
        /// and the points of crosswalks, stop signs, traffic lights and turn restrictions, which generation matches to
        /// the game's nodes. Roundabout rings are left as they are. TerrainHeights stays aligned with Points.
        /// </summary>
        private static void SimplifyRoadPoints(List<OsmWay> ways, float planTolerance, float heightTolerance)
        {
            if (planTolerance <= 0f || heightTolerance <= 0f)
                return;

            var useCount = new Dictionary<float3, int>();
            var seen = new HashSet<float3>();
            // Features by plan position on a 5 cm grid: their heights were recorded before the elevation profile.
            var features = new HashSet<(int, int)>();
            (int, int) FeatureKey(float3 p) => ((int)math.round(p.x * 20f), (int)math.round(p.z * 20f));
            foreach (var way in ways)
            {
                seen.Clear();
                foreach (var p in way.Points)
                {
                    if (seen.Add(p))
                        useCount[p] = useCount.TryGetValue(p, out var c) ? c + 1 : 1;
                }
                foreach (var f in way.Crosswalks) features.Add(FeatureKey(f.Position));
                foreach (var f in way.StopSigns) features.Add(FeatureKey(f.Position));
                foreach (var f in way.TrafficLights) features.Add(FeatureKey(f.Position));
                foreach (var r in way.TurnRestrictions) features.Add(FeatureKey(r.ViaPosition));
            }

            bool NearFeature(float3 p)
            {
                var (x, z) = FeatureKey(p);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (features.Contains((x + dx, z + dz)))
                            return true;
                return false;
            }

            int before = 0, after = 0;
            foreach (var way in ways)
            {
                var points = way.Points;
                before += Math.Max(0, points.Count - 1);
                var aligned = way.TerrainHeights.Count == points.Count;
                if (points.Count < 3 || way.IsRoundabout || way.IsNodeRoundabout || !aligned)
                {
                    after += Math.Max(0, points.Count - 1);
                    continue;
                }

                var keep = new bool[points.Count];
                keep[0] = keep[points.Count - 1] = true;
                for (var i = 1; i < points.Count - 1; i++)
                {
                    if (useCount[points[i]] > 1 || NearFeature(points[i]))
                        keep[i] = true;
                }

                var anchor = 0;
                for (var i = 1; i < points.Count; i++)
                {
                    if (!keep[i])
                        continue;
                    MarkSimplifiedPoints(points, anchor, i, planTolerance, heightTolerance, keep);
                    anchor = i;
                }

                var keptPoints = new List<float3>(points.Count);
                var keptHeights = new List<float>(points.Count);
                for (var i = 0; i < points.Count; i++)
                {
                    if (!keep[i])
                        continue;
                    keptPoints.Add(points[i]);
                    keptHeights.Add(way.TerrainHeights[i]);
                }
                points.Clear();
                points.AddRange(keptPoints);
                way.TerrainHeights.Clear();
                way.TerrainHeights.AddRange(keptHeights);
                after += Math.Max(0, points.Count - 1);
            }

            Mod.log.Info($"Simplified road points: {before} segments to {after} ({100f * (before - after) / Math.Max(1, before):F0}% fewer; " +
                         $"points within {planTolerance} m in plan and {heightTolerance} m in height of the line between their neighbours dropped; junctions and node features kept).");
        }

        // Shape points closer than this (along the way) to a junction are dropped, so each junction leg starts with one
        // edge long enough for CS2 to shape the junction's corners.
        private const float JunctionClearanceMeters = 16f;
        // ... as long as the way stays within this of its OSM line (plan) and of its height profile.
        private const float JunctionClearancePlanMeters = 0.5f;
        // ... or within this where a point would otherwise stay closer than CrampedLegMeters to the junction.
        private const float CrampedClearancePlanMeters = 1.5f;
        private const float CrampedLegMeters = 14f;
        private const float JunctionClearanceHeightMeters = 0.5f;

        /// <summary>
        /// A curved OSM way is a chain of short straight edges with a node at every OSM point (the Volunteer Park
        /// ring: 41 points over 204 m, edges of about 5 m). Where such a way meets a junction, its legs were only a
        /// few metres long, too short for CS2 to shape the junction: clipped kerb corners, grey wedges of sidewalk
        /// fill, kinks. Drops each way's shape points within JunctionClearanceMeters of a junction (a point another
        /// way also uses), walking out from it, while the remaining chord stays within the plan and height
        /// tolerances of the dropped points. Never dropped: way ends, junctions, and points of node features.
        /// </summary>
        private static void ClearJunctionApproaches(List<OsmWay> ways)
        {
            var useCount = new Dictionary<float3, int>();
            // Points of roads (not paths). A path keeps its points next to a road: without its kerb point, a footpath joined
            // to a road at its centre line ran its paving straight across the lanes (Volunteer Park ring, north side).
            var roadPoints = new HashSet<float3>(ways.Where(w => !IsPathLike(w)).SelectMany(w => w.Points));
            var seen = new HashSet<float3>();
            var features = new HashSet<(int, int)>();
            (int, int) FeatureKey(float3 p) => ((int)math.round(p.x * 20f), (int)math.round(p.z * 20f));
            foreach (var way in ways)
            {
                seen.Clear();
                foreach (var p in way.Points)
                {
                    if (seen.Add(p))
                        useCount[p] = useCount.TryGetValue(p, out var c) ? c + 1 : 1;
                }
                foreach (var f in way.Crosswalks) features.Add(FeatureKey(f.Position));
                foreach (var f in way.StopSigns) features.Add(FeatureKey(f.Position));
                foreach (var f in way.TrafficLights) features.Add(FeatureKey(f.Position));
                foreach (var r in way.TurnRestrictions) features.Add(FeatureKey(r.ViaPosition));
            }
            bool NearFeature(float3 p)
            {
                var (x, z) = FeatureKey(p);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (features.Contains((x + dx, z + dz)))
                            return true;
                return false;
            }

            var dropped = 0;
            var junctionsCleared = 0;
            foreach (var way in ways)
            {
                var points = way.Points;
                // A path keeps its points next to a road (see isPath below).
                var isPath = IsPathLike(way);
                if (points.Count < 3 || way.IsNodeRoundabout)
                    continue;
                var heights = way.TerrainHeights.Count == points.Count ? way.TerrainHeights : null;
                var n = points.Count;
                var isJunction = new bool[n];
                var fixedPoint = new bool[n];
                for (var i = 0; i < n; i++)
                {
                    isJunction[i] = useCount[points[i]] > 1;
                    fixedPoint[i] = i == 0 || i == n - 1 || isJunction[i] || NearFeature(points[i]);
                    if (isPath && ((i > 0 && roadPoints.Contains(points[i - 1])) || (i < n - 1 && roadPoints.Contains(points[i + 1]))))
                        fixedPoint[i] = true; // the kerb point of a path meeting a road
                }
                var drop = new bool[n];

                // From junction j walking in direction step: the farthest point k (within the clearance, plus the first
                // point past it) such that every point between j and k can go.
                void Clear(int j, int step)
                {
                    var best = FarthestClear(j, step, JunctionClearancePlanMeters);
                    // A tight curve (the Volunteer Park ring, 33 m radius) keeps a point a few metres from the junction
                    // at the normal tolerance, too close for CS2 to shape the junction's corner. Only there, allow
                    // the wider tolerance.
                    var first = best >= 0 ? best : j + step;
                    if (first >= 0 && first < n && math.distance(points[first].xz, points[j].xz) < CrampedLegMeters)
                    {
                        var wide = FarthestClear(j, step, CrampedClearancePlanMeters);
                        if (wide >= 0)
                            best = wide;
                    }
                    if (best < 0)
                        return;
                    junctionsCleared++;
                    for (var i = j + step; i != best; i += step)
                        drop[i] = true;
                }

                int FarthestClear(int j, int step, float planTolerance)
                {
                    var along = 0f;
                    var best = -1;
                    for (var k = j + step; k >= 0 && k < n; k += step)
                    {
                        along += math.distance(points[k - step].xz, points[k].xz);
                        if (math.abs(k - j) >= 2 && ChordHolds(j, k, planTolerance))
                            best = k;
                        if (fixedPoint[k] || along >= JunctionClearanceMeters)
                            break;
                    }
                    return best;
                }

                bool ChordHolds(int a, int b, float planTolerance)
                {
                    var lo = math.min(a, b);
                    var hi = math.max(a, b);
                    for (var i = lo + 1; i < hi; i++)
                    {
                        if (fixedPoint[i])
                            return false;
                        var start = points[lo];
                        var chord = (points[hi] - start).xz;
                        var t = math.saturate(math.dot((points[i] - start).xz, chord) / math.max(1e-6f, math.lengthsq(chord)));
                        if (math.distance(points[i].xz, start.xz + chord * t) > planTolerance)
                            return false;
                        if (math.abs(points[i].y - math.lerp(start.y, points[hi].y, t)) > JunctionClearanceHeightMeters)
                            return false;
                    }
                    return true;
                }

                for (var j = 0; j < n; j++)
                {
                    if (!isJunction[j] || (isPath && roadPoints.Contains(points[j])))
                        continue;
                    if (j > 0) Clear(j, -1);
                    if (j < n - 1) Clear(j, 1);
                }

                var keptPoints = new List<float3>(n);
                var keptHeights = heights != null ? new List<float>(n) : null;
                for (var i = 0; i < n; i++)
                {
                    if (drop[i])
                    {
                        dropped++;
                        continue;
                    }
                    keptPoints.Add(points[i]);
                    keptHeights?.Add(heights[i]);
                }
                if (keptPoints.Count == n)
                    continue;
                points.Clear();
                points.AddRange(keptPoints);
                if (keptHeights != null)
                {
                    way.TerrainHeights.Clear();
                    way.TerrainHeights.AddRange(keptHeights);
                }
            }

            Mod.log.Info($"Junction approaches: dropped {dropped} shape points within {JunctionClearanceMeters:F0} m of " +
                         $"{junctionsCleared} junction legs (way kept within {JunctionClearancePlanMeters} m of OSM), so junction legs start with a longer edge.");
        }

        /// <summary>Douglas-Peucker between kept points a and b: marks the farthest point outside the tolerances, recursively.</summary>
        private static void MarkSimplifiedPoints(List<float3> points, int a, int b, float planTolerance, float heightTolerance, bool[] keep)
        {
            while (b - a >= 2)
            {
                var start = points[a];
                var end = points[b];
                var chord = (end - start).xz;
                var chordLengthSq = math.max(1e-6f, math.lengthsq(chord));
                var worst = 1f;
                var worstIndex = -1;
                for (var i = a + 1; i < b; i++)
                {
                    var t = math.saturate(math.dot((points[i] - start).xz, chord) / chordLengthSq);
                    var plan = math.distance(points[i].xz, start.xz + chord * t) / planTolerance;
                    var height = math.abs(points[i].y - math.lerp(start.y, end.y, t)) / heightTolerance;
                    var score = math.max(plan, height);
                    if (score > worst)
                    {
                        worst = score;
                        worstIndex = i;
                    }
                }
                if (worstIndex < 0)
                    return;
                keep[worstIndex] = true;
                MarkSimplifiedPoints(points, a, worstIndex, planTolerance, heightTolerance, keep);
                a = worstIndex;
            }
        }

        private static void AddInterpolatedPoints(
            List<float3> points,
            List<float> terrainHeights,
            float3 startPoint,
            float3 endPoint,
            System.Func<float3, float> sampleHeight,
            float elevationMeters)
        {
            var segmentLength = math.distance(new float2(startPoint.x, startPoint.z), new float2(endPoint.x, endPoint.z));
            var subdivisions = Math.Max(1, (int)Math.Ceiling(segmentLength / MaxTerrainSegmentLengthMeters));

            for (var i = 1; i <= subdivisions; i++)
            {
                var t = i / (float)subdivisions;
                // The last point must be the OSM node itself, bit for bit: lerp at t = 1 (start + (end - start) * 1)
                // can be one float step off, and the same shared node reached from a different start point then
                // differs between ways. CS2 only joins coincident course points, so the junction got two unconnected
                // nodes (unjoined T-junctions).
                var point = i == subdivisions ? endPoint : math.lerp(startPoint, endPoint, t);
                point.y = sampleHeight(point);
                points.Add(point);
                terrainHeights.Add(point.y);
            }
        }

        private static void ApplyElevationProfile(
            List<float3> points,
            List<float> terrainHeights,
            float baseElevationOffsetMeters,
            int inclineDirection,
            float inclinePercent,
            bool structuralElevation)
        {
            if (points.Count == 0 || terrainHeights.Count == 0)
                return;

            var sampleCount = Math.Min(points.Count, terrainHeights.Count);
            var totalLength = 0f;
            var cumulativeLengths = new float[sampleCount];
            cumulativeLengths[0] = 0f;

            for (var i = 1; i < sampleCount; i++)
            {
                totalLength += math.distance(
                    new float2(points[i - 1].x, points[i - 1].z),
                    new float2(points[i].x, points[i].z));
                cumulativeLengths[i] = totalLength;
            }

            if (totalLength <= 0.0001f)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    var point = points[i];
                    point.y = terrainHeights[i] + baseElevationOffsetMeters;
                    points[i] = point;
                }
                return;
            }

            // incline=* is not applied as an extra height. It describes the slope the way already has: on the ground the
            // terrain provides it, and structural ways interpolate between their end heights. Adding it on top (the
            // former inclineDirection * length * percent * t) left the far end of every inclined way metres off the
            // terrain, so CS2 built two unconnected nodes wherever it met another way (131 of 163 height-mismatch
            // junction failures on the UW campus crop). OsmWay.InclineDirection/InclinePercent remain (way merging compares them).
            var startTerrain = terrainHeights[0];
            var endTerrain = terrainHeights[sampleCount - 1];

            for (var i = 0; i < sampleCount; i++)
            {
                var point = points[i];
                var t = cumulativeLengths[i] / totalLength;
                var terrainBase = structuralElevation
                    ? math.lerp(startTerrain, endTerrain, t)
                    : terrainHeights[i];

                point.y = terrainBase + baseElevationOffsetMeters;
                points[i] = point;
            }
        }

        private static float EstimateBaseElevationOffset(
            OsmImportOptions options,
            string highwayType,
            string railwayType,
            bool hasLayer,
            int layer,
            bool isBridge,
            bool isTunnel,
            bool isCovered,
            bool isEmbankment,
            bool isCutting)
        {
            var offset = 0f;

            // Only real bridges and tunnels leave the ground. layer=* alone means "drawn above/below something
            // else" (covered walkways, paths over a culvert, floors of a building passage), and covered=yes is a roof
            // over a way at street level. Treating those as +-10 m put ground ways in the air or underground, and
            // connecting them to the ground network forced steep ramps (tall elevated stubs on the UW campus crop).
            // layer still sets the height of a bridge or tunnel (layer 2 bridge = two steps up).
            if (isBridge)
            {
                offset += hasLayer && layer > 0 ? layer * options.LayerElevationStepMeters : options.BridgeElevationMeters;
            }
            else if (isTunnel)
            {
                offset -= hasLayer && layer < 0 ? -layer * options.LayerElevationStepMeters : options.TunnelDepthMeters;
            }

            if (isEmbankment)
                offset += options.EmbankmentElevationMeters;

            if (isCutting)
                offset -= options.CuttingDepthMeters;

            if (!hasLayer && offset == 0f && (IsElevatedRailType(highwayType) || IsElevatedRailType(railwayType)))
                offset += options.LayerElevationStepMeters;

            return offset;
        }

        // bridge=yes/viaduct/boardwalk/movable/cantilever/trestle/... all mean a bridge (IsTruthy only accepted yes/true/1).
        private static bool IsPavedSurface(string surface) =>
            string.IsNullOrEmpty(surface) || surface == "asphalt" || surface == "paved" || surface == "concrete" ||
            surface == "paving_stones" || surface == "concrete:plates" || surface == "sett" || surface == "bricks";

        private static bool IsBridgeValue(string value) =>
            !string.IsNullOrWhiteSpace(value) && !IsFalsey(value);

        // A tunnel that goes underground. building_passage (through a building at street level), covered (a roof
        // over the way) and avalanche_protector (a gallery on the surface) are at ground level.
        private static bool IsTunnelValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || IsFalsey(value))
                return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "building_passage":
                case "covered":
                case "avalanche_protector":
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsElevatedRailType(string type)
        {
            return string.Equals(type, "monorail", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "light_rail", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "subway", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTruthy(string value)
        {
            return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadIncline(string value, out int direction, out float percent)
        {
            direction = 0;
            percent = 0f;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim().ToLowerInvariant();
            if (normalized == "up")
            {
                direction = 1;
                return true;
            }

            if (normalized == "down")
            {
                direction = -1;
                return true;
            }

            normalized = normalized.Replace("%", string.Empty).Trim();
            if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPercent))
            {
                direction = parsedPercent >= 0f ? 1 : -1;
                percent = Math.Abs(parsedPercent);
                return true;
            }

            return false;
        }

        private static long ReadLong(XmlElement element, string attribute) =>
            long.Parse(element.GetAttribute(attribute), CultureInfo.InvariantCulture);

        private static double ReadDouble(XmlElement element, string attribute) =>
            double.Parse(element.GetAttribute(attribute), CultureInfo.InvariantCulture);

        private static bool TryReadDouble(XmlElement element, string attribute, out double value)
        {
            return double.TryParse(
                element.GetAttribute(attribute),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value);
        }
    }
}
