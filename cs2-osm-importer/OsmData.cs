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

    internal struct OsmImportDefinition : IComponentData
    {
        public int SessionId;
    }

    internal sealed class OsmImportOptions
    {
        public string Path { get; set; } = string.Empty;
        public float SimplifyToleranceMeters { get; set; } = 1f;
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
        public string RoadPrefabName { get; set; } = string.Empty;
        public int MaxWays { get; set; } = 10000;
    }

    internal sealed class OsmGenerateOptions
    {
        public HashSet<string> HighwayTypes { get; } = new HashSet<string>();
        public float IntersectionThresholdMeters { get; set; } = 8f;
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
    }

    internal sealed class OsmWay
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Ref { get; set; } = string.Empty;
        public string Highway { get; set; } = string.Empty;
        public string Footway { get; set; } = string.Empty;
        public string SidewalkTag { get; set; } = string.Empty;
        public int Lanes { get; set; }
        public int LanesForward { get; set; }
        public int LanesBackward { get; set; }
        public int LanesBothWays { get; set; }
        public bool HasExplicitLaneCount { get; set; }
        public bool UsesStructuralElevation { get; set; }
        public bool IsBridge { get; set; }
        public bool IsTunnel { get; set; }
        public bool IsOneWay { get; set; }
        public int OneWayDirection { get; set; }
        public float BaseElevationOffsetMeters { get; set; }
        public int InclineDirection { get; set; }
        public float InclinePercent { get; set; }
        public OsmSidewalkType Sidewalk { get; set; }
        public OsmParkingSideType ParkingSide { get; set; }
        public OsmParkingStyleType ParkingStyle { get; set; }
        public List<long> NodeIds { get; } = new List<long>();
        public List<float> TerrainHeights { get; } = new List<float>();
        public List<float3> Points { get; } = new List<float3>();
    }
}
