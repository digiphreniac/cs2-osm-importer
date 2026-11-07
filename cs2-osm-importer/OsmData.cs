using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal enum OsmSidewalkType
    {
        None = 0,
        Sidewalk = 1,
        WideSidewalk = 2,
    }

    internal enum OsmParkingSideType
    {
        None = 0,
        Left = 1,
        Right = 2,
        Both = 3,
    }

    internal enum OsmParkingStyleType
    {
        None = 0,
        Parallel = 1,
        Diagonal = 2,
        Perpendicular = 3,
    }

    internal enum OsmRoadFeatureSide
    {
        None = 0,
        Left = 1,
        Right = 2,
        Both = 3,
    }

    internal struct OsmImportDefinition : IComponentData
    {
        public int SessionId;
    }

    internal sealed class OsmImportOptions
    {
        public string Path { get; set; } = string.Empty;
        public float SimplifyToleranceMeters { get; set; } = 1f;
        /// <summary>
        /// Drops road points that lie on the straight line between their neighbours, within these tolerances: in plan
        /// (map position, so straight roads stay exactly on OSM) and in height (how closely the road follows the ground
        /// between its points). Junctions, shared points and node features are always kept. 0 turns it off.
        /// </summary>
        public float SimplifyPlanToleranceMeters { get; set; } = 0.05f;
        public float SimplifyHeightToleranceMeters { get; set; } = 0.5f;
        public float CurveToleranceMeters { get; set; } = 0.5f;
        public float ScaleFactor { get; set; } = 1f;
        public float ElevationMeters { get; set; } = 0.2f;
        public float LayerElevationStepMeters { get; set; } = 10f;
        public float BridgeElevationMeters { get; set; } = 10f;
        public float TunnelDepthMeters { get; set; } = 10f;
        public float CoveredDepthMeters { get; set; } = 10f;
        public float EmbankmentElevationMeters { get; set; } = 3f;
        public float CuttingDepthMeters { get; set; } = 3f;
        public bool UseImportHighwayTypesFilter { get; set; }
        public HashSet<string> ImportHighwayTypes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string PriorityRoadNamesPath { get; set; } = string.Empty;
        public string RoadPrefabName { get; set; } = string.Empty;
        public int MaxWays { get; set; } = 10000;
    }

    internal sealed class OsmGenerateOptions
    {
        public HashSet<string> HighwayTypes { get; } = new HashSet<string>();
        public float IntersectionThresholdMeters { get; set; } = 8f;
        public bool GenerateBikeLanes { get; set; } = true;
        public bool GenerateTurnRestrictions { get; set; } = true;
        public bool GenerateCrosswalks { get; set; } = true;
        public bool GenerateStopSigns { get; set; } = true;
        public bool GenerateTrafficLights { get; set; } = true;
        public bool GenerateRestrictedLanes { get; set; } = true;

        // Build amenity=parking surface lots (pavement + aisles + working stalls) and skip the OSM parking aisles
        // inside them, which the generated aisles replace.
        public bool GenerateParkingLots { get; set; } = true;

        // Build leisure=park areas: grass in the outline, vanilla park buildings along its streets (leisure,
        // attractiveness, maintenance) and trees in the woods mapped inside it (land value).
        public bool GenerateParks { get; set; } = true;

        // Fill natural=water lakes, ponds and reservoirs with water sources at their shore height (the heightmap
        // must have the lake bed below the shore).
        public bool GenerateLakes { get; set; } = true;

        // Road segments queued per frame within a generation phase. 0 (default) = all in one frame, which avoids
        // CS2 merging new points into nodes from earlier frames (see OsmImportSystem.SegmentsPerFrameLimit).
        // Only lower it to spread a very large import over frames, at the cost of broken chains at batch boundaries.
        public int SegmentsPerFrame { get; set; }
    }

    internal sealed class OsmImportResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ParsedNodes { get; set; }
        public int ParsedWays { get; set; }
        public int ImportedSegments { get; set; }
    }

    internal sealed class OsmDocument
    {
        public List<OsmWay> Ways { get; } = new List<OsmWay>();
        public int NodeCount { get; set; }
        public int InferredLaneCount { get; set; }
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }
        public float ElevationMeters { get; set; }
        public float3 Min { get; set; }
        public float3 Max { get; set; }
        public float3 OriginOffset { get; set; }
        public bool HasBounds { get; set; }

        // Projected XZ position (meters, scale applied) of every road node, before interpolation,
        // simplification or way merging. Used as ground truth by the geometry report.
        public Dictionary<long, float2> SourceNodePositions { get; } = new Dictionary<long, float2>();

        // amenity=parking areas (closed ways and multipolygon outer rings), projected like the roads.
        public List<OsmParkingArea> ParkingAreas { get; } = new List<OsmParkingArea>();

        // leisure=park outlines with the woods mapped inside them.
        public List<OsmParkArea> Parks { get; } = new List<OsmParkArea>();

        // Lakes, ponds and reservoirs (natural=water that is not a river, stream or canal).
        public List<OsmLake> Lakes { get; } = new List<OsmLake>();
    }

    internal sealed class OsmLake
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<float2> Ring { get; } = new List<float2>();
        public List<List<float2>> Holes { get; } = new List<List<float2>>();
        public float AreaSquareMeters { get; set; }
    }

    internal sealed class OsmParkArea
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;

        // Outline in world XZ, counter-clockwise, without a repeated closing point. Holes are ignored.
        public List<float2> Ring { get; } = new List<float2>();

        // natural=wood / landuse=forest outlines lying inside the park.
        public List<List<float2>> Woods { get; } = new List<List<float2>>();
        public float AreaSquareMeters { get; set; }

        // Public playgrounds and dog parks mapped inside the park (or, for a standalone dog park, the park itself).
        public List<OsmParkFeature> Features { get; } = new List<OsmParkFeature>();

        // Holes in the park's multipolygon and water mapped inside it (lakes, ponds): nothing is placed on them.
        public List<List<float2>> Obstacles { get; } = new List<List<float2>>();

        // A public dog park outside any park, built as a small park of its own (gets no other park buildings).
        public bool IsStandaloneFeature { get; set; }
    }

    internal enum OsmParkFeatureKind
    {
        Playground = 0,
        DogPark = 1,
    }

    internal sealed class OsmParkFeature
    {
        public long Id { get; set; }
        public OsmParkFeatureKind Kind { get; set; }
        public bool FromPoint { get; set; }
        public float2 Center { get; set; }
        public float AreaSquareMeters { get; set; }

        // Mapped outline, or a square of the typical size for a feature mapped as a point.
        public List<float2> Ring { get; } = new List<float2>();
    }

    internal enum OsmParkingKind
    {
        Surface = 0,
        MultiStorey = 1,
        Underground = 2,
        Rooftop = 3,
        StreetSide = 4,
        Other = 5,
    }

    internal sealed class OsmParkingArea
    {
        public long Id { get; set; }
        public bool FromRelation { get; set; }
        public string Name { get; set; } = string.Empty;
        public OsmParkingKind Kind { get; set; }
        public int Capacity { get; set; }

        // Outline in world XZ (metres, scale applied), counter-clockwise, without a repeated closing point.
        public List<float2> Ring { get; } = new List<float2>();
    }

    internal sealed class OsmWay
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Ref { get; set; } = string.Empty;
        public string Highway { get; set; } = string.Empty;
        public string Footway { get; set; } = string.Empty;
        public string SidewalkTag { get; set; } = string.Empty;
        // A crossing way tagged crossing=unmarked/informal/no or crossing:markings=no.
        public bool IsUnmarkedCrossing { get; set; }
        public bool IsRoundabout { get; set; }

        // service=parking_aisle lying inside an imported surface parking lot (replaced by the lot's own aisles).
        public bool IsParkingAisle { get; set; }
        public bool IsInsideParkingLot { get; set; }

        // Small roundabout built as a CS2 roundabout object on a single junction node instead of as a ring road.
        // The ring itself is not queued; connecting ways are extended to RoundaboutCenter (see OsmParser.ConvertRoundabouts).
        public bool IsNodeRoundabout { get; set; }
        public float3 RoundaboutCenter { get; set; }
        public float RoundaboutRadius { get; set; }
        public int Lanes { get; set; }
        public int LanesForward { get; set; }
        public int LanesBackward { get; set; }
        public int LanesBothWays { get; set; }
        public bool HasExplicitLaneCount { get; set; }
        public bool UsesStructuralElevation { get; set; }
        public bool IsBridge { get; set; }
        // man_made=pier: a flat deck over the water; its height is set from the water level at generation.
        public bool IsPier { get; set; }
        public bool IsTunnel { get; set; }
        public bool IsOneWay { get; set; }
        public int OneWayDirection { get; set; }
        public float BaseElevationOffsetMeters { get; set; }
        public int InclineDirection { get; set; }
        public float InclinePercent { get; set; }
        public OsmSidewalkType Sidewalk { get; set; }
        public OsmParkingSideType ParkingSide { get; set; }
        public OsmParkingStyleType ParkingStyle { get; set; }
        public bool HasBikeLanes { get; set; }
        public bool HasTurnRestrictions { get; set; }
        public bool HasRestrictedLanes { get; set; }
        public OsmRoadFeatureSide BikeLaneSide { get; set; }
        public OsmRoadFeatureSide RestrictedLaneSide { get; set; }
        public List<long> NodeIds { get; } = new List<long>();
        public List<float> TerrainHeights { get; } = new List<float>();
        public List<float3> Points { get; } = new List<float3>();
        public List<OsmRoadNodeFeature> Crosswalks { get; } = new List<OsmRoadNodeFeature>();
        // OSM crossings within a few metres of a junction: not upgrades of their own (the junction draws crosswalks),
        // but they say which of the junction's legs has a real crossing.
        public List<OsmRoadNodeFeature> JunctionCrossings { get; } = new List<OsmRoadNodeFeature>();
        public List<OsmRoadNodeFeature> StopSigns { get; } = new List<OsmRoadNodeFeature>();
        public List<OsmRoadNodeFeature> TrafficLights { get; } = new List<OsmRoadNodeFeature>();
        public List<OsmTurnRestriction> TurnRestrictions { get; } = new List<OsmTurnRestriction>();
    }

    internal sealed class OsmRoadNodeFeature
    {
        public long NodeId { get; set; }
        public float3 Position { get; set; }
    }

    internal sealed class OsmTurnRestriction
    {
        public long ViaNodeId { get; set; }
        public float3 ViaPosition { get; set; }
        public bool ForbidLeftTurn { get; set; }
        public bool ForbidRightTurn { get; set; }
        public bool ForbidStraight { get; set; }
        public bool ForbidUTurn { get; set; }
    }
}
