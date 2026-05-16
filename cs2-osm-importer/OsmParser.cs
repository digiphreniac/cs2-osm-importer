using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal static class OsmParser
    {
        private const double EarthRadiusMeters = 6378137d;
        private const float MaxTerrainSegmentLengthMeters = 15f;
        private const float DefaultInclinePercent = 4f;

        private sealed class GeoNode
        {
            public double Lat;
            public double Lon;
            public double X;
            public double Y;
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

            var xml = new XmlDocument { XmlResolver = null };
            xml.Load(options.Path);

            var nodes = ReadNodes(xml);
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

            var parsedWays = new List<OsmWay>();
            foreach (var wayElement in supportedWays)
            {
                if (TryReadRoadWay(wayElement, nodes, options, sampleHeight, worldOffset, out var way))
                    parsedWays.Add(way);
            }

            var mergedWays = MergeSimpleConnectedWays(parsedWays, out var mergedWayCount);
            if (mergedWayCount > 0)
                Mod.log.Info($"Merged {mergedWayCount} connected OSM ways into simpler chains before import.");
            if (mergedWayCount > 0)
                document.Ways.AddRange(mergedWays);
            else
                document.Ways.AddRange(parsedWays);

            if (document.Ways.Count > Math.Max(1, options.MaxWays))
                document.Ways.RemoveRange(Math.Max(1, options.MaxWays), document.Ways.Count - Math.Max(1, options.MaxWays));

            foreach (var way in document.Ways)
                IncludeBounds(document, way);

            document.InferredLaneCount = NormalizeContiguousLaneCounts(document.Ways);
            return document;
        }

        private static Dictionary<long, GeoNode> ReadNodes(XmlDocument xml)
        {
            var nodes = new Dictionary<long, GeoNode>();
            var nodeElements = xml.SelectNodes("/osm/node");
            if (nodeElements == null)
                return nodes;

            foreach (XmlElement nodeElement in nodeElements)
            {
                var id = ReadLong(nodeElement, "id");
                nodes[id] = new GeoNode
                {
                    Lat = ReadDouble(nodeElement, "lat"),
                    Lon = ReadDouble(nodeElement, "lon")
                };
            }

            return nodes;
        }

        private static bool TryReadRoadWay(
            XmlElement wayElement,
            Dictionary<long, GeoNode> nodes,
            OsmImportOptions options,
            System.Func<float3, float> sampleHeight,
            float3 worldOffset,
            out OsmWay way)
        {
            way = new OsmWay { Id = ReadLong(wayElement, "id") };
            var isArea = false;
            var hasLayer = false;
            var layer = 0;
            var isBridge = false;
            var isTunnel = false;
            var isCovered = false;
            var isEmbankment = false;
            var isCutting = false;
            var inclineDirection = 0;
            var inclinePercent = 0f;
            var hasExplicitLaneCount = false;
            var forwardLaneCount = 0;
            var backwardLaneCount = 0;
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
                    else if (key == "bridge")
                        isBridge = IsTruthy(value);
                    else if (key == "tunnel")
                        isTunnel = IsTruthy(value);
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
                    else if (key == "area" && value == "yes")
                        isArea = true;
                }
            }

            if (isArea || string.IsNullOrEmpty(way.Highway) || !IsSupportedHighway(way.Highway))
                return false;

            if (!hasExplicitLaneCount && (forwardLaneCount > 0 || backwardLaneCount > 0))
            {
                way.Lanes = forwardLaneCount + backwardLaneCount;
                way.HasExplicitLaneCount = true;
            }

            if (hasExplicitLaneCount)
                way.HasExplicitLaneCount = true;

            way.BaseElevationOffsetMeters = EstimateBaseElevationOffset(
                options,
                hasLayer,
                layer,
                isBridge,
                isTunnel,
                isCovered,
                isEmbankment,
                isCutting);
            way.UsesStructuralElevation = hasLayer || isBridge || isTunnel || isCovered || isEmbankment || isCutting;
            way.InclineDirection = inclineDirection;
            way.InclinePercent = inclinePercent;
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
                way.TerrainHeights.Add(currentPoint.y);

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
                Simplify(way.Points, Math.Max(0f, options.SimplifyToleranceMeters * options.ScaleFactor));
            return way.Points.Count >= 2;
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
                Lanes = way.Lanes,
                HasExplicitLaneCount = way.HasExplicitLaneCount,
                UsesStructuralElevation = way.UsesStructuralElevation,
                BaseElevationOffsetMeters = way.BaseElevationOffsetMeters,
                InclineDirection = way.InclineDirection,
                InclinePercent = way.InclinePercent
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
                Lanes = ways[0].Lanes,
                HasExplicitLaneCount = ways[0].HasExplicitLaneCount,
                UsesStructuralElevation = ways[0].UsesStructuralElevation,
                BaseElevationOffsetMeters = ways[0].BaseElevationOffsetMeters,
                InclineDirection = ways[0].InclineDirection,
                InclinePercent = ways[0].InclinePercent
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
                    continue;
                }

                for (var nodeIndex = 1; nodeIndex < way.NodeIds.Count; nodeIndex++)
                    merged.NodeIds.Add(way.NodeIds[nodeIndex]);
            }

            return merged.Points.Count >= 2 ? merged : null;
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

            if (!ApproximatelyEqual(a.BaseElevationOffsetMeters, b.BaseElevationOffsetMeters))
                return false;

            if (a.InclineDirection != b.InclineDirection)
                return false;

            if (!ApproximatelyEqual(a.InclinePercent, b.InclinePercent))
                return false;

            if (a.Lanes > 0 && b.Lanes > 0 && a.Lanes != b.Lanes)
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
            var tags = wayElement.SelectNodes("tag");

            if (tags != null)
            {
                foreach (XmlElement tag in tags)
                {
                    var key = tag.GetAttribute("k");
                    var value = tag.GetAttribute("v");

                    if (key == "highway")
                        highway = value;
                    else if (key == "area" && value == "yes")
                        isArea = true;
                }
            }

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

            return $"{way.Highway.Trim()}|{roadLabel}";
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

            for (var i = points.Count - 2; i > 0; i--)
            {
                var distance = DistanceToSegment(points[i], points[i - 1], points[i + 1]);
                if (distance <= tolerance)
                    points.RemoveAt(i);
            }
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
                var point = math.lerp(startPoint, endPoint, t);
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

            var normalizedInclinePercent = inclinePercent > 0f ? inclinePercent : DefaultInclinePercent;
            var inclineRiseMeters = totalLength * (normalizedInclinePercent / 100f);
            var startTerrain = terrainHeights[0];
            var endTerrain = terrainHeights[sampleCount - 1];

            for (var i = 0; i < sampleCount; i++)
            {
                var point = points[i];
                var t = cumulativeLengths[i] / totalLength;
                var terrainBase = structuralElevation
                    ? math.lerp(startTerrain, endTerrain, t)
                    : terrainHeights[i];
                var inclineOffsetMeters = inclineDirection == 0
                    ? 0f
                    : inclineDirection * inclineRiseMeters * t;

                point.y = terrainBase + baseElevationOffsetMeters + inclineOffsetMeters;
                points[i] = point;
            }
        }

        private static float EstimateBaseElevationOffset(
            OsmImportOptions options,
            bool hasLayer,
            int layer,
            bool isBridge,
            bool isTunnel,
            bool isCovered,
            bool isEmbankment,
            bool isCutting)
        {
            var offset = 0f;

            if (hasLayer && layer != 0)
            {
                offset += layer * options.LayerElevationStepMeters;
            }
            else if (isBridge)
            {
                offset += options.BridgeElevationMeters;
            }
            else if (isTunnel || isCovered)
            {
                offset -= isTunnel ? options.TunnelDepthMeters : options.CoveredDepthMeters;
            }

            if (isEmbankment)
                offset += options.EmbankmentElevationMeters;

            if (isCutting)
                offset -= options.CuttingDepthMeters;

            return offset;
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
