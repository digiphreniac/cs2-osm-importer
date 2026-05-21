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
        private const float RampLengthMeters = 60f;
        private const float SidewalkSpatialBucketSizeMeters = 64f;

        private sealed class GeoNode
        {
            public double Lat;
            public double Lon;
            public double X;
            public double Y;
            public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

            turnRestrictionsByWayId = ReadTurnRestrictions(xml, nodes, options, sampleHeight);

            var parsedWays = new List<OsmWay>();
            foreach (var wayElement in supportedWays)
            {
                if (TryReadRoadWay(wayElement, nodes, options, sampleHeight, worldOffset, turnRestrictionsByWayId, out var way))
                    parsedWays.Add(way);
            }

            parsedWays = AttachSidewalkWays(parsedWays, out var attachedSidewalkCount);
            if (attachedSidewalkCount > 0)
                Mod.log.Info($"Attached {attachedSidewalkCount} footway sidewalk segments to eligible roadway segments.");

            var mergedWays = MergeSimpleConnectedWays(parsedWays, out var mergedWayCount);
            if (mergedWayCount > 0)
                Mod.log.Info($"Merged {mergedWayCount} connected OSM ways into simpler chains before import.");
            var finalWays = mergedWayCount > 0 ? mergedWays : parsedWays;
            HarmonizeGroupElevations(finalWays);
            finalWays = PrioritizeWays(finalWays, priorityRoadNames);
            document.Ways.AddRange(finalWays);

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
                var node = new GeoNode
                {
                    Lat = ReadDouble(nodeElement, "lat"),
                    Lon = ReadDouble(nodeElement, "lon")
                };

                var tags = nodeElement.SelectNodes("tag");
                if (tags != null)
                {
                    foreach (XmlElement tag in tags)
                        node.Tags[tag.GetAttribute("k")] = tag.GetAttribute("v");
                }

                nodes[id] = node;
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
            var hasLayer = false;
            var layer = 0;
            var isBridge = false;
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
            var hasRoadsideTrees = false;
            var bikeLaneSide = OsmRoadFeatureSide.None;
            var roadsideTreeSide = OsmRoadFeatureSide.None;
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
                        isBridge = IsTruthy(value);
                    else if (key == "tunnel")
                        isTunnel = IsTruthy(value);
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
                    else if (IsRoadsideTreeTag(key, value))
                    {
                        hasRoadsideTrees = true;
                        roadsideTreeSide = MergeRoadFeatureSide(roadsideTreeSide, ParseRoadFeatureSide(key));
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
            way.UsesStructuralElevation = ((hasLayer && layer != 0) || isBridge || isTunnel || isCovered || isEmbankment || isCutting) &&
                                          !IsElevatedRailType(way.Highway) && !IsElevatedRailType(wayRailway);
            way.IsBridge = isBridge;
            way.IsTunnel = isTunnel;
            way.IsRoundabout = isRoundabout || IsClosedLoopWay(way.NodeIds);
            way.OneWayDirection = oneWayDirection;
            way.IsOneWay = oneWayDirection != 0 || way.IsRoundabout;
            if (way.IsRoundabout && way.OneWayDirection == 0)
                way.OneWayDirection = 1;

            if (way.Highway is "monorail" or "light_rail" or "subway" || wayRailway is "monorail" or "light_rail" or "subway")
            {
                Mod.log.Info(
                    $"OSM WAY: Highway=\"{way.Highway}\" Railway=\"{wayRailway}\" " +
                    $"Offset={way.BaseElevationOffsetMeters:F1}m Structural={way.UsesStructuralElevation} " +
                    $"Layer={layer}(hasLayer={hasLayer}) Bridge={isBridge} Tunnel={isTunnel}");
            }

            way.InclineDirection = inclineDirection;
            way.InclinePercent = inclinePercent;
            way.Footway = footwayType;
            way.SidewalkTag = sidewalkTag;
            way.Sidewalk = ParseSidewalkType(sidewalkTag, sidewalkLeft, sidewalkRight);
            ResolveParkingTags(parkingLeftTag, parkingRightTag, parkingBothTag, out var parkingSide, out var parkingStyle);
            way.ParkingSide = parkingSide;
            way.ParkingStyle = parkingStyle;
            way.HasBikeLanes = hasBikeLanes;
            way.HasRoadsideTrees = hasRoadsideTrees;
            way.HasTurnRestrictions = turnRestrictionsByWayId != null && turnRestrictionsByWayId.ContainsKey(way.Id);
            way.HasRestrictedLanes = hasDesignatedBusLanes || hasBroadBusDesignation;
            way.BikeLaneSide = NormalizeDetectedSide(bikeLaneSide, way.HasBikeLanes);
            way.RoadsideTreeSide = NormalizeDetectedSide(roadsideTreeSide, way.HasRoadsideTrees);
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

        private static bool IsRoadsideTreeTag(string key, string value)
        {
            key = (key ?? string.Empty).Trim().ToLowerInvariant();
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || IsFalsey(value))
                return false;

            return key == "tree_lined" ||
                   key == "tree_lined:left" ||
                   key == "tree_lined:right" ||
                   key == "tree_lined:both" ||
                   (key == "natural" && value == "tree_row");
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
            if (tags == null)
                return false;

            return HasTag(tags, "highway", "crossing") ||
                   HasTag(tags, "footway", "crossing") ||
                   HasTag(tags, "crossing") ||
                   HasTag(tags, "crossing:markings");
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
                HasRoadsideTrees = way.HasRoadsideTrees,
                HasTurnRestrictions = way.HasTurnRestrictions,
                HasRestrictedLanes = way.HasRestrictedLanes,
                BikeLaneSide = way.BikeLaneSide,
                RoadsideTreeSide = way.RoadsideTreeSide,
                RestrictedLaneSide = way.RestrictedLaneSide,
                IsRoundabout = way.IsRoundabout
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
                HasRoadsideTrees = ways[0].HasRoadsideTrees,
                HasTurnRestrictions = ways[0].HasTurnRestrictions,
                HasRestrictedLanes = ways[0].HasRestrictedLanes,
                BikeLaneSide = ways[0].BikeLaneSide,
                RoadsideTreeSide = ways[0].RoadsideTreeSide,
                RestrictedLaneSide = ways[0].RestrictedLaneSide,
                IsRoundabout = ways[0].IsRoundabout
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
                merged.HasRoadsideTrees |= way.HasRoadsideTrees;
                merged.HasTurnRestrictions |= way.HasTurnRestrictions;
                merged.HasRestrictedLanes |= way.HasRestrictedLanes;
                merged.BikeLaneSide = MergeRoadFeatureSide(merged.BikeLaneSide, way.BikeLaneSide);
                merged.RoadsideTreeSide = MergeRoadFeatureSide(merged.RoadsideTreeSide, way.RoadsideTreeSide);
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

                        var higherWay = wayA.BaseElevationOffsetMeters > wayB.BaseElevationOffsetMeters ? wayA : wayB;
                        var lowerWay = wayA.BaseElevationOffsetMeters > wayB.BaseElevationOffsetMeters ? wayB : wayA;
                        var isLowerStart = higherWay == wayA ? entries[b].isStart : entries[a].isStart;

                        var delta = higherWay.BaseElevationOffsetMeters - lowerWay.BaseElevationOffsetMeters;
                        var startIdx = isLowerStart ? 0 : lowerWay.Points.Count - 1;
                        var step = isLowerStart ? 1 : -1;

                        var accumulated = 0f;
                        var idx = startIdx;
                        while (idx >= 0 && idx < lowerWay.Points.Count)
                        {
                            if (idx != startIdx)
                            {
                                var prev = idx - step;
                                accumulated += math.distance(
                                    new float2(lowerWay.Points[prev].x, lowerWay.Points[prev].z),
                                    new float2(lowerWay.Points[idx].x, lowerWay.Points[idx].z));
                            }

                            var t = math.saturate(accumulated / RampLengthMeters);
                            var weight = 1f - (t * t * (3f - 2f * t));
                            if (weight < 0.001f)
                                break;

                            var p = lowerWay.Points[idx];
                            p.y += delta * weight;
                            lowerWay.Points[idx] = p;

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

            return $"{way.Highway.Trim()}|{roadLabel}|{way.Lanes}|{way.LanesForward}|{way.LanesBackward}|{way.LanesBothWays}|{way.ParkingSide}|{way.ParkingStyle}|{way.HasBikeLanes}|{way.HasRoadsideTrees}|{way.HasTurnRestrictions}|{way.HasRestrictedLanes}";
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

            if (!hasLayer && offset == 0f && (IsElevatedRailType(highwayType) || IsElevatedRailType(railwayType)))
                offset += options.LayerElevationStepMeters;

            return offset;
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
