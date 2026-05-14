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
        private const float LayerElevationStepMeters = 6f;
        private const float BridgeElevationMeters = 6f;
        private const float TunnelDepthMeters = 6f;
        private const float CoveredDepthMeters = 5f;
        private const float EmbankmentElevationMeters = 3f;
        private const float CuttingDepthMeters = 3f;
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
                if (!TryReadSupportedRoadWay(wayElement, out var highway, out var isArea))
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

            foreach (var wayElement in supportedWays)
            {
                if (document.Ways.Count >= Math.Max(1, options.MaxWays))
                    break;

                if (TryReadRoadWay(wayElement, nodes, options, sampleHeight, worldOffset, out var way))
                {
                    document.Ways.Add(way);
                    IncludeBounds(document, way);
                }
            }
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
                way.Lanes = forwardLaneCount + backwardLaneCount;

            way.BaseElevationOffsetMeters = EstimateBaseElevationOffset(
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
                if (!nodes.TryGetValue(ReadLong(nd, "ref"), out var node))
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
                offset += layer * LayerElevationStepMeters;
            }
            else if (isBridge)
            {
                offset += BridgeElevationMeters;
            }
            else if (isTunnel || isCovered)
            {
                offset -= isTunnel ? TunnelDepthMeters : CoveredDepthMeters;
            }

            if (isEmbankment)
                offset += EmbankmentElevationMeters;

            if (isCutting)
                offset -= CuttingDepthMeters;

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
