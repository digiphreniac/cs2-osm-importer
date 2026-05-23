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

            // build map of way elements by id for relation parsing
            var wayElementById = new Dictionary<long, XmlElement>();
            foreach (XmlElement we in wayElements)
            {
                var id = ReadLong(we, "id");
                wayElementById[id] = we;
            }

            // parse parking area relations (amenity=parking, landuse=parking, parking=surface)
            var parkingAreas = ReadParkingAreas(xml, wayElementById);

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

            // include nodes referenced by parking area relations so their coordinates get projected
            if (parkingAreas != null)
            {
                foreach (var pa in parkingAreas)
                {
                    foreach (var nodeId in pa.MemberNodeIds)
                    {
                        if (nodes.ContainsKey(nodeId))
                            roadNodeIds.Add(nodeId);
                    }
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

            // remove road ways that are fully inside parking area polygons (keep perimeter/member ways)
            if (parkingAreas != null && parkingAreas.Count > 0)
            {
                FilterWaysInsideParkingAreas(document, parkingAreas, nodes, options);
            }

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