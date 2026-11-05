using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Globalization;
using System.Linq;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.UI;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace cs2_osm_importer
{
    public partial class OsmImportSystem : GameSystemBase
    {
        private const int MaxSegmentsPerFrame = 128;

        // Road definitions are queued in ONE frame per phase by default. CS2 applies each frame's definitions as a
        // separate set, and when a set is applied it merges any new course point lying within roughly 13-22 m of a
        // node built by an earlier set into that node (moving the node to the average position). With ~13 m terrain
        // segments that broke the chain at every 128-segment frame boundary and next to earlier roads: dangling stubs,
        // ovals with crosswalks, roads attached to the wrong node. Measured on Keddie_CA (empty network): 667 dead
        // ends / 700 moved nodes at 128 per frame vs 2 / 32 in one frame, and generation 53 s -> 9 s.
        private int SegmentsPerFrameLimit =>
            _activeGenerateOptions != null && _activeGenerateOptions.SegmentsPerFrame > 0 ? _activeGenerateOptions.SegmentsPerFrame : int.MaxValue;
        private const int MaxApplyWaitFrames = 3600;
        private const float MinSegmentLengthMeters = 1f;
        private const int PillarWaitFrameThreshold = 60;

        private enum WorkPhase
        {
            Idle,
            LoadDataQueued,
            LoadData,
            QueueDefinitions,
            QueueRoundaboutConnectors,
            QueueRoundabouts,
            WaitForGameApply,
            RemoveIntersectingPillars,
            DeleteRoads
        }

        private enum ApplyWaitPurpose
        {
            None,
            AfterConnectors,
            AfterRoundabouts,
            AfterRoads
        }

        private PrefabSystem _prefabSystem;
        private NameSystem _nameSystem;
        internal List<(Entity entity, string name)> PendingNames = new List<(Entity, string)>();
        private GroundHeightSystem _groundHeightSystem;
        private TerrainSystem _terrainSystem;
        private OsmImportOptions _pendingLoadOptions;
        private OsmImportOptions _activeOptions;
        private OsmGenerateOptions _pendingGenerateOptions;
        private OsmGenerateOptions _activeGenerateOptions;
        private OsmDocument _document;
        private NetData _netData;
        private Entity _prefabEntity;
        private string _prefabName = string.Empty;
        private readonly Dictionary<string, Entity> _highwayPrefabEntities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _highwayPrefabNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Entity> _laneSpecificPrefabEntities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        private static bool _prefabRulesLoaded;
        private static readonly object _prefabRulesLock = new object();

        // Edit these once to lock your preferred prefab mapping.
        // Lane buckets: 1, 2, 3, 4, 5, 6+
        // Sidewalk buckets: Any, None, Sidewalk, WideSidewalk
        // Oneway buckets: Any, No, AnyOneWay, Forward, Backward
        internal static readonly Dictionary<string, List<PrefabSelectionRule>> DefaultPrefabRulesByRoadType = new Dictionary<string, List<PrefabSelectionRule>>(StringComparer.OrdinalIgnoreCase)
        {
            { "motorway", new List<PrefabSelectionRule>() },
            { "trunk", new List<PrefabSelectionRule>() },
            { "primary", new List<PrefabSelectionRule>() },
            { "secondary", new List<PrefabSelectionRule>() },
            { "tertiary", new List<PrefabSelectionRule>() },
            { "unclassified", new List<PrefabSelectionRule>() },
            { "residential", new List<PrefabSelectionRule>() },
            { "service", new List<PrefabSelectionRule>() },
            { "living_street", new List<PrefabSelectionRule>() },
            { "road", new List<PrefabSelectionRule>() },
            { "motorway_link", new List<PrefabSelectionRule>() },
            { "trunk_link", new List<PrefabSelectionRule>() },
            { "primary_link", new List<PrefabSelectionRule>() },
            { "secondary_link", new List<PrefabSelectionRule>() },
            { "tertiary_link", new List<PrefabSelectionRule>() },
            { "footway", new List<PrefabSelectionRule>() },
            { "steps", new List<PrefabSelectionRule>() },
            { "bridleway", new List<PrefabSelectionRule>() },
            { "monorail", new List<PrefabSelectionRule>() },
            { "rail", new List<PrefabSelectionRule>() },
            { "light_rail", new List<PrefabSelectionRule>() },
            { "subway", new List<PrefabSelectionRule>() },
            { "tram", new List<PrefabSelectionRule>() },
            { "track", new List<PrefabSelectionRule>() },
            { "cycleway", new List<PrefabSelectionRule>() },
            { "pedestrian", new List<PrefabSelectionRule>() },
            { "roundabout", new List<PrefabSelectionRule>() },
        };
        private int _wayIndex;
        private int _pointIndex;
        private int _typeIndex;
        private int _roundaboutWayIndex;
        private int _roundaboutConnectorWayIndex;
        private int _importedSegments;
        private int _lastLoggedSegments;
        private int _sessionId;
        private int _applyWaitFrames;
        private ApplyWaitPurpose _applyWaitPurpose;
        private int _baselineEdgeCount;
        private HashSet<Entity> _baselineSegments = new HashSet<Entity>();
        private HashSet<Entity> _generatedEdgeEntities = new HashSet<Entity>();
        private int _pillarWaitFrames;
        private HashSet<Entity> _baselinePillarEntities = new HashSet<Entity>();
        private List<Entity> _pendingDeleteEntities = new List<Entity>();
        private int _pendingDeleteIndex;
        private int _deletedEdgeCount;
        private bool _deleteCompletionPending;
        private int _lastPendingDefinitions = -1;
        private int _lastWaitLogFrame;
        private bool _loggedFirstQueuedSegment;
        private bool _loggedTerrainMethodProbe;
        private bool _loggedPrefabCatalog;
        private bool _loggedRoundaboutPrefabDiagnostics;
        private bool _loggedRoundaboutRoadPrefabDiagnostics;
        private bool _hasTerrainHeightData;
        private bool _roadServiceUpgradesApplied;
        private TerrainHeightData _terrainHeightData;
        private struct QueuedSegmentInfo
        {
            public float3 StartPos;
            public float3 EndPos;
            public float StartTerrainHeight;
            public float EndTerrainHeight;
            public float ElevationOffset;
            public float RoundaboutRadius;
            public string Name;
            public int Lanes;
            public bool IsRoundabout;
            public OsmWay Way;
            public float3 MidPoint => (StartPos + EndPos) * 0.5f;
        }

        private List<QueuedSegmentInfo> _queuedSegmentInfos = new List<QueuedSegmentInfo>();
        private List<string> _generationTypeOrder = new List<string>();
        private List<OsmWay> _roundaboutWays = new List<OsmWay>();
        private List<OsmWay> _roundaboutConnectorWays = new List<OsmWay>();
        private List<OsmWay> _nodeRoundabouts = new List<OsmWay>();
        private bool _nodeRoundaboutsPlaced;
        private bool _nodeRoundaboutsRefreshed;
        private readonly List<Entity> _nodeRoundaboutNodes = new List<Entity>();
        private Dictionary<string, Entity> _roundaboutPrefabs;
        private HashSet<long> _roundaboutConnectorWayIds = new HashSet<long>();
        private Dictionary<Entity, OsmWay> _generatedEdgeWays = new Dictionary<Entity, OsmWay>();
        private Dictionary<string, int> _typeWayCounts = new Dictionary<string, int>();
        private int _totalGenerationWays;
        private bool _roadNamesApplied;
        private bool _roundaboutNodeUpgradesApplied;
        private WorkPhase _phase = WorkPhase.Idle;

        internal bool IsImporting { get; private set; }
        internal bool IsDeletingSegments => IsImporting && _phase == WorkPhase.DeleteRoads;
        private string _status = OsmLocalization.T("OsmImporter.Status.Ready", "Ready.");
        internal string Status => _status;
        internal string StatusSummary { get; private set; } = string.Empty;
        internal bool HasGeneratedAssets { get; private set; }
        internal bool HasLoadedData => _document != null && _document.Ways.Count > 0;
        internal string RoadTypeCountsJson { get; private set; } = "{}";
        internal IReadOnlyDictionary<string, int> RoadTypeCountsByType { get; private set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal static IReadOnlyDictionary<string, int> SharedRoadTypeCountsByType { get; private set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static string L(string key, string fallback) => OsmLocalization.T(key, fallback);

        private static string LF(string key, string fallback, params object[] args) => OsmLocalization.F(key, fallback, args);

        private static string DisplayHighwayType(string highwayType) => OsmLocalization.GetRoadTypeName(highwayType, highwayType);

        private static void LogOsmRoadFeatureSummary(OsmDocument document)
        {
            if (document?.Ways == null || document.Ways.Count == 0)
                return;

            var bikeLaneWays = document.Ways.Count(way => way.HasBikeLanes);
            var restrictedLaneWays = document.Ways.Count(way => way.HasRestrictedLanes);
            var turnRestrictionWays = document.Ways.Count(way => way.HasTurnRestrictions);
            var crosswalkNodes = document.Ways.Sum(way => way.Crosswalks.Select(feature => feature.NodeId).Distinct().Count());
            var stopSignNodes = document.Ways.Sum(way => way.StopSigns.Select(feature => feature.NodeId).Distinct().Count());
            var trafficLightNodes = document.Ways.Sum(way => way.TrafficLights.Select(feature => feature.NodeId).Distinct().Count());

            Mod.log.Info(
                $"Loaded OSM road feature data: bikeLanes={bikeLaneWays} ways, " +
                $"restrictedBusLanes={restrictedLaneWays} ways, turnRestrictions={turnRestrictionWays} ways, " +
                $"crosswalkNodes={crosswalkNodes}, stopSignNodes={stopSignNodes}, trafficLightNodes={trafficLightNodes}.");
        }

        private static string DescribeGenerateFeatureSettings(OsmGenerateOptions options)
        {
            if (options == null)
                return "all enabled";

            return $"bikeLanes={options.GenerateBikeLanes}, turnRestrictions={options.GenerateTurnRestrictions}, " +
                   $"crosswalks={options.GenerateCrosswalks}, " +
                   $"stopSigns={options.GenerateStopSigns}, trafficLights={options.GenerateTrafficLights}, " +
                   $"restrictedBusLanes={options.GenerateRestrictedLanes}";
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _nameSystem = World.GetOrCreateSystemManaged<NameSystem>();

            _groundHeightSystem = World.GetOrCreateSystemManaged<GroundHeightSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
        }

        protected override void OnUpdate()
        {
            LogPrefabCatalogOnce();
            UpdateGeometryReportRequests();

            if (!IsImporting)
                return;

            switch (_phase)
            {
                case WorkPhase.LoadDataQueued:
                    _phase = WorkPhase.LoadData;
                    break;
                case WorkPhase.LoadData:
                    LoadData();
                    break;
                case WorkPhase.QueueDefinitions:
                    ProcessGenerationBatch();
                    break;
                case WorkPhase.QueueRoundaboutConnectors:
                    ProcessRoundaboutConnectorBatch();
                    break;
                case WorkPhase.QueueRoundabouts:
                    ProcessRoundaboutBatch();
                    break;
                case WorkPhase.WaitForGameApply:
                    WaitForGeneratedRoads();
                    break;
                case WorkPhase.RemoveIntersectingPillars:
                    RemoveIntersectingPillars();
                    break;
                case WorkPhase.DeleteRoads:
                    DeleteRoads();
                    break;
            }
        }

        internal bool LoadOsmData(OsmImportOptions options)
        {
            if (IsImporting)
                return false;

            if (!IsValidOsmPath(options.Path))
            {
                _document = null;
                RoadTypeCountsJson = "{}";
                RoadTypeCountsByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                SharedRoadTypeCountsByType = RoadTypeCountsByType;
                SetStatus(L("OsmImporter.Status.InvalidPath", "The selected file path is not a valid OSM file."));
                return true;
            }

            _pendingLoadOptions = options;
            IsImporting = true;
            _phase = WorkPhase.LoadDataQueued;
            SetStatus(L("OsmImporter.Status.LoadingWithWarning", "loading... (game might be frozen for some time)"));
            Mod.log.Info($"Queued OSM load for '{options.Path}'");
            return true;
        }

        internal bool GenerateRoads(OsmGenerateOptions options)
        {
            if (IsImporting)
                return false;

            if (_document == null || _document.Ways.Count == 0)
            {
                SetStatus(L("OsmImporter.Status.LoadBeforeGenerate", "Load OSM data before generating roads."));
                return true;
            }

            _pendingGenerateOptions = options;
            IsImporting = true;
            _phase = WorkPhase.QueueDefinitions;
            SetStatus(L("OsmImporter.Status.GeneratingPrepared", "Preparing road generation..."));
            Mod.log.Info($"Queued OSM road generation for selected types: {SelectedTypesLabel(options)}. Feature settings: {DescribeGenerateFeatureSettings(options)}");
            PrepareGeneration();
            return true;
        }

        internal bool DeleteAllSegments()
        {
            if (IsImporting)
                return false;

            IsImporting = true;
            _phase = WorkPhase.DeleteRoads;
            _pendingDeleteEntities = _generatedEdgeEntities.Where(entity => entity != Entity.Null).ToList();
            _pendingDeleteEntities.AddRange(FindRoundaboutObjectsOn(_pendingDeleteEntities));
            // Deleting a lot's surface also deletes the aisles and stalls it owns.
            _pendingDeleteEntities.AddRange(TakeGeneratedParkingLots());
            // Park grass, park buildings (with their own paths and areas) and park trees.
            _pendingDeleteEntities.AddRange(TakeGeneratedParks());
            _pendingDeleteIndex = 0;
            _deletedEdgeCount = 0;
            _deleteCompletionPending = false;
            _generatedEdgeEntities.Clear();
            SetStatus(L("OsmImporter.Status.DeletingSegments", "Deleting all segments..."));
            StatusSummary = string.Empty;
            return true;
        }

        private void LoadData()
        {
            try
            {
                _activeOptions = _pendingLoadOptions;
                _pendingLoadOptions = null;
                SetStatus(L("OsmImporter.Status.ParsingFile", "Parsing OSM file..."));
                Mod.log.Info($"Parsing OSM file '{_activeOptions.Path}'");

                if (string.IsNullOrWhiteSpace(_activeOptions.Path) || !File.Exists(_activeOptions.Path))
                {
                    _document = null;
                    RoadTypeCountsJson = "{}";
                    Complete(L("OsmImporter.Status.InvalidPath", "The selected file path is not a valid OSM file."));
                    return;
                }

                var terrainOffset = GetTerrainWorldOffset();
                Mod.log.Info($"Terrain world offset: {FormatPoint(terrainOffset)}");
                CacheTerrainHeightData();

                _document = OsmParser.Load(
                    _activeOptions,
                    SampleTerrainHeight,
                    terrainOffset);
                Mod.log.Info($"Parsed {_document.NodeCount} OSM nodes and {_document.Ways.Count} supported highway ways");
                if (_document.InferredLaneCount > 0)
                    Mod.log.Info($"Normalized {_document.InferredLaneCount} contiguous ways to inherit lane counts from connected segments.");
                LogOsmRoadFeatureSummary(_document);
                LogImportedBounds(_document);

                if (_document.Ways.Count == 0)
                {
                    RoadTypeCountsByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    SharedRoadTypeCountsByType = RoadTypeCountsByType;
                    Complete(L("OsmImporter.Status.NoSupportedWays", "No supported OSM highway ways were found."));
                    return;
                }

                var typeSummary = string.Join(", ", _document.Ways
                    .GroupBy(w => w.Highway)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}: {g.Count()}"));
                Mod.log.Info($"Loaded OSM highway type counts: {typeSummary}");
                RoadTypeCountsByType = _document.Ways
                    .GroupBy(way => way.Highway, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                SharedRoadTypeCountsByType = RoadTypeCountsByType;
                RoadTypeCountsJson = BuildRoadTypeCountsJson(_document);

                Complete(LF("OsmImporter.Status.LoadComplete", "Loaded {0} OSM ways. Select road types and click Generate.", _document.Ways.Count));
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                _document = null;
                RoadTypeCountsJson = "{}";
                RoadTypeCountsByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                SharedRoadTypeCountsByType = RoadTypeCountsByType;
                Complete(LF("OsmImporter.Status.LoadFailed", "Failed to load OSM data: {0}", ex.Message));
            }
        }

        private void PrepareGeneration()
        {
            try
            {
                _activeGenerateOptions = _pendingGenerateOptions;
                _pendingGenerateOptions = null;

                if (!TryGetRoadPrefab(_activeOptions?.RoadPrefabName ?? string.Empty, out var prefab, out _prefabEntity))
                {
                    Complete(L("OsmImporter.Status.NoUsablePrefab", "No usable road prefab was found. Open a city and try again."));
                    return;
                }

                _prefabName = prefab.name;
                _highwayPrefabEntities.Clear();
                _highwayPrefabNames.Clear();
                _laneSpecificPrefabEntities.Clear();
                if (!_prefabSystem.TryGetComponentData(prefab, out _netData))
                {
                    Complete(LF("OsmImporter.Status.PrefabMissingNetData", "Road prefab '{0}' has no NetData.", prefab.name));
                    return;
                }

                _wayIndex = 0;
                _pointIndex = 0;
                _typeIndex = 0;
                _roundaboutWayIndex = 0;
                _roundaboutConnectorWayIndex = 0;
                _importedSegments = 0;
                _lastLoggedSegments = 0;
                _sessionId++;
                _applyWaitFrames = 0;
                _applyWaitPurpose = ApplyWaitPurpose.None;
                _lastPendingDefinitions = -1;
                _lastWaitLogFrame = 0;
                _baselineEdgeCount = CountAllNetEdges();
                using (var edgeArray = GetCurrentNetEdgeArray(Allocator.Temp))
                {
                    _baselineSegments = edgeArray.ToHashSet();
                }
                _baselinePillarEntities = QueryPillarEntities().ToHashSet();
                Mod.log.Info($"Baseline pillar entity count: {_baselinePillarEntities.Count}");
                _generatedEdgeEntities.Clear();
                _generatedEdgeWays.Clear();
                _queuedSegmentInfos.Clear();
                // Roundabouts: small OSM rings are not queued as roads. OsmParser.ConvertRoundabouts extended the ways
                // that join them to the ring centre, so those are ordinary roads queued with everything else (one frame),
                // and PlaceNodeRoundabouts puts a CS2 roundabout object on the centre node once the roads exist.
                // (The former diameter course with a Roundabout component was ignored by CS2 and left a stray road.)
                _roundaboutWays = new List<OsmWay>();
                _roundaboutConnectorWays = new List<OsmWay>();
                _roundaboutConnectorWayIds = new HashSet<long>();
                _nodeRoundabouts = _document?.Ways?
                    .Where(way => way.IsNodeRoundabout)
                    .GroupBy(way => (way.RoundaboutCenter.x, way.RoundaboutCenter.z))
                    .Select(g => g.First())
                    .ToList() ?? new List<OsmWay>();
                _nodeRoundaboutsPlaced = false;
                _nodeRoundaboutsRefreshed = false;
                _nodeRoundaboutNodes.Clear();
                Mod.log.Info($"Detected {_nodeRoundabouts.Count} roundabouts to place as CS2 roundabout objects after the roads apply.");
                _generationTypeOrder = BuildGenerationTypeOrder();
                _typeWayCounts.Clear();
                _totalGenerationWays = 0;
                foreach (var type in _generationTypeOrder)
                {
                    var count = GetWaysForHighwayType(type, false).Count;
                    _typeWayCounts[type] = count;
                    _totalGenerationWays += count;
                }
                _roadNamesApplied = false;
                _roadServiceUpgradesApplied = false;
                ResetParkingLotState();
                ResetParkState();
                StatusSummary = _totalGenerationWays > 0
                    ? $"Generating... (0%)"
                    : L("OsmImporter.Status.GeneratingRoads", "generating...");
                Mod.log.Info($"Baseline edge count before OSM road generation: {_baselineEdgeCount}");
                _phase = _roundaboutConnectorWays.Count > 0
                    ? WorkPhase.QueueRoundaboutConnectors
                    : (_generationTypeOrder.Count > 0 ? WorkPhase.QueueDefinitions : WorkPhase.WaitForGameApply);
                SetStatus(L("OsmImporter.Status.GenerationSelected", "Generating selected OSM roads..."));
                _loggedFirstQueuedSegment = false;
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete(LF("OsmImporter.Status.PrepareGenerationFailed", "Failed to prepare road generation: {0}", ex.Message));
            }
        }

        private void ProcessGenerationBatch()
        {
            try
            {
                var segmentsThisFrame = 0;

                while (_typeIndex < _generationTypeOrder.Count)
                {
                    var highwayType = _generationTypeOrder[_typeIndex];
                    var typeWays = GetWaysForHighwayType(highwayType, false);
                    if (typeWays.Count == 0)
                    {
                        _typeIndex++;
                        _wayIndex = 0;
                        _pointIndex = 0;
                        continue;
                    }

                    while (_wayIndex < typeWays.Count)
                    {
                        var way = typeWays[_wayIndex];
                        var typePrefab = GetPrefabForHighwayType(way);

                        while (_pointIndex < way.Points.Count - 1)
                        {
                            var mergeEnd = _pointIndex + 1;
                            if (way.UsesStructuralElevation)
                            {
                                while (mergeEnd + 1 < way.Points.Count &&
                                       PointsAreCollinear(way.Points[mergeEnd - 1], way.Points[mergeEnd], way.Points[mergeEnd + 1], 0.999f))
                                {
                                    mergeEnd++;
                                }
                            }

                            var segStart = way.Points[_pointIndex];
                            var segEnd = way.Points[mergeEnd];
                            var terrainStart = _pointIndex < way.TerrainHeights.Count
                                ? way.TerrainHeights[_pointIndex] : segStart.y;
                            var terrainEnd = mergeEnd < way.TerrainHeights.Count
                                ? way.TerrainHeights[mergeEnd] : segEnd.y;
                            var created = CreateNetCourseDefinition(
                                segStart, segEnd, typePrefab, way.BaseElevationOffsetMeters);
                            _pointIndex = mergeEnd;
                            if (created != Entity.Null)
                            {
                                HasGeneratedAssets = true;
                                _importedSegments++;
                                _queuedSegmentInfos.Add(new QueuedSegmentInfo
                                {
                                    StartPos = segStart,
                                    EndPos = segEnd,
                                    StartTerrainHeight = terrainStart,
                                    EndTerrainHeight = terrainEnd,
                                    ElevationOffset = way.BaseElevationOffsetMeters,
                                    Name = !string.IsNullOrWhiteSpace(way.Name) ? way.Name : (way.Ref ?? string.Empty),
                                    Lanes = way.Lanes,
                                    Way = way
                                });
                            }
                            segmentsThisFrame++;

                            if (segmentsThisFrame >= SegmentsPerFrameLimit)
                                break;
                        }

                        if (_pointIndex < way.Points.Count - 1)
                            break;

                        _wayIndex++;
                        _pointIndex = 0;
                    }

                    if (_wayIndex < typeWays.Count)
                        break;

                    _typeIndex++;
                    _wayIndex = 0;
                    _pointIndex = 0;
                }

                if (_typeIndex < _generationTypeOrder.Count)
                {
                    var completedWays = 0;
                    for (var i = 0; i < _typeIndex && i < _generationTypeOrder.Count; i++)
                    {
                        if (_typeWayCounts.TryGetValue(_generationTypeOrder[i], out var c))
                            completedWays += c;
                    }
                    var percent = _totalGenerationWays > 0
                        ? (completedWays * 100 / _totalGenerationWays)
                        : 0;
                    StatusSummary = $"Generating... ({percent}%)";
                }
                if (_importedSegments - _lastLoggedSegments >= 1000)
                {
                    _lastLoggedSegments = _importedSegments;
                    Mod.log.Info(
                        $"OSM road generation progress: {_importedSegments} road segments, " +
                        $"type {_typeIndex}/{_generationTypeOrder.Count}, lane summary: {DescribeQueuedLaneCounts()}");
                }

                if (_typeIndex >= _generationTypeOrder.Count)
                {
                    Mod.log.Info(
                        $"Queued {_importedSegments} OSM road definitions using {_prefabName}. " +
                        $"Selected types: {SelectedTypesLabel(_activeGenerateOptions)}. " +
                        $"Lane summary: {DescribeQueuedLaneCounts()}. {DocumentBoundsMessage()}");
                    if (_document.Ways.Count > 0 && _document.Ways[0].Points.Count > 0)
                    {
                        var firstPoint = _document.Ways[0].Points[0];
                        var lastWay = _document.Ways[_document.Ways.Count - 1];
                        var lastPoint = lastWay.Points.Count > 0 ? lastWay.Points[lastWay.Points.Count - 1] : float3.zero;
                        Mod.log.Info(
                            $"Queued road point sample: first={FormatPoint(firstPoint)}, last={FormatPoint(lastPoint)}, " +
                            $"min={FormatPoint(_document.Min)}, max={FormatPoint(_document.Max)}");
                    }
                    _applyWaitPurpose = ApplyWaitPurpose.AfterRoads;
                    _phase = WorkPhase.WaitForGameApply;
                    StatusSummary = "Generation complete";
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete(LF("OsmImporter.Status.GenerateFailed", "Failed to generate roads: {0}", ex.Message));
            }
        }

        private void ProcessRoundaboutConnectorBatch()
        {
            try
            {
                var segmentsThisFrame = 0;

                while (_roundaboutConnectorWayIndex < _roundaboutConnectorWays.Count)
                {
                    var way = _roundaboutConnectorWays[_roundaboutConnectorWayIndex];
                    if (way == null)
                    {
                        _roundaboutConnectorWayIndex++;
                        continue;
                    }

                    var typePrefab = GetPrefabForHighwayType(way);

                    while (_pointIndex < way.Points.Count - 1)
                    {
                        var mergeEnd = _pointIndex + 1;
                        if (way.UsesStructuralElevation)
                        {
                            while (mergeEnd + 1 < way.Points.Count &&
                                   PointsAreCollinear(way.Points[mergeEnd - 1], way.Points[mergeEnd], way.Points[mergeEnd + 1], 0.999f))
                            {
                                mergeEnd++;
                            }
                        }

                        var segStart = way.Points[_pointIndex];
                        var segEnd = way.Points[mergeEnd];
                        var terrainStart = _pointIndex < way.TerrainHeights.Count ? way.TerrainHeights[_pointIndex] : segStart.y;
                        var terrainEnd = mergeEnd < way.TerrainHeights.Count ? way.TerrainHeights[mergeEnd] : segEnd.y;
                        var created = CreateNetCourseDefinition(segStart, segEnd, typePrefab, way.BaseElevationOffsetMeters);
                        _pointIndex = mergeEnd;
                        if (created != Entity.Null)
                        {
                            HasGeneratedAssets = true;
                            _importedSegments++;
                            _queuedSegmentInfos.Add(new QueuedSegmentInfo
                            {
                                StartPos = segStart,
                                EndPos = segEnd,
                                StartTerrainHeight = terrainStart,
                                EndTerrainHeight = terrainEnd,
                                ElevationOffset = way.BaseElevationOffsetMeters,
                                Name = !string.IsNullOrWhiteSpace(way.Name) ? way.Name : (way.Ref ?? string.Empty),
                                Lanes = way.Lanes,
                                Way = way
                            });
                        }

                        segmentsThisFrame++;
                        if (segmentsThisFrame >= SegmentsPerFrameLimit)
                            break;
                    }

                    if (_pointIndex < way.Points.Count - 1)
                        break;

                    _roundaboutConnectorWayIndex++;
                    _pointIndex = 0;
                }

                if (_roundaboutConnectorWayIndex >= _roundaboutConnectorWays.Count)
                {
                    _applyWaitPurpose = ApplyWaitPurpose.AfterConnectors;
                    _phase = WorkPhase.WaitForGameApply;
                    _applyWaitFrames = 0;
                    StatusSummary = "Waiting for roundabout connectors to apply";
                    SetStatus(StatusSummary);
                    Mod.log.Info($"Queued {_roundaboutConnectorWays.Count} roundabout connector ways. Session {_sessionId}");
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete(LF("OsmImporter.Status.RoundaboutConnectorQueueFailed", "Failed to queue roundabout connector roads: {0}", ex.Message));
            }
        }

        private void WaitForGeneratedRoads()
        {
            try
            {
                _applyWaitFrames++;
                var pendingDefinitions = CountPendingDefinitions();
                var generatedRoadEdges = Math.Max(0, CountAllNetEdges() - _baselineEdgeCount);
                var consumedDefinitions = Math.Max(0, _importedSegments - pendingDefinitions);

                var waitMsg = pendingDefinitions > 0
                    ? LF("OsmImporter.Status.WaitingForCS2", "CS2 gen... {0}/{1}", consumedDefinitions, _importedSegments)
                    : LF("OsmImporter.Status.CS2Verifying", "Verifying... CS2 consumed {0}", _importedSegments);
                SetStatus(waitMsg);
                StatusSummary = string.Empty;

                if (pendingDefinitions != _lastPendingDefinitions && _applyWaitFrames - _lastWaitLogFrame >= 300)
                {
                    _lastPendingDefinitions = pendingDefinitions;
                    _lastWaitLogFrame = _applyWaitFrames;
                    Mod.log.Info($"OSM road generation wait: {consumedDefinitions}/{_importedSegments} definitions consumed, {generatedRoadEdges} new road edges detected");
                }

                if (_applyWaitFrames >= 30 && generatedRoadEdges > 0)
                {
                    if (_applyWaitPurpose == ApplyWaitPurpose.AfterConnectors && _roundaboutWays.Count > 0)
                    {
                        _applyWaitPurpose = ApplyWaitPurpose.AfterRoundabouts;
                        _phase = WorkPhase.QueueRoundabouts;
                        _applyWaitFrames = 0;
                        StatusSummary = LF("OsmImporter.Status.QueueingRoundabouts", "Queueing roundabouts after nearby roads apply...");
                        Mod.log.Info($"Connector roads applied. Queueing {_roundaboutWays.Count} roundabouts next. Session {_sessionId}");
                        return;
                    }

                    if (_applyWaitPurpose == ApplyWaitPurpose.AfterConnectors && _generationTypeOrder.Count > 0)
                    {
                        _applyWaitPurpose = ApplyWaitPurpose.AfterRoads;
                        _phase = WorkPhase.QueueDefinitions;
                        _applyWaitFrames = 0;
                        _typeIndex = 0;
                        _wayIndex = 0;
                        _pointIndex = 0;
                        StatusSummary = "Generating remaining roads...";
                        Mod.log.Info($"Connector roads applied. Queueing remaining roads next. Session {_sessionId}");
                        return;
                    }

                    if (_applyWaitPurpose == ApplyWaitPurpose.AfterConnectors)
                    {
                        _applyWaitPurpose = ApplyWaitPurpose.AfterRoads;
                    }

                    if (_applyWaitPurpose == ApplyWaitPurpose.AfterRoundabouts && _generationTypeOrder.Count > 0)
                    {
                        _applyWaitPurpose = ApplyWaitPurpose.AfterRoads;
                        _phase = WorkPhase.QueueDefinitions;
                        _applyWaitFrames = 0;
                        _typeIndex = 0;
                        _wayIndex = 0;
                        _pointIndex = 0;
                        StatusSummary = "Generating remaining roads...";
                        Mod.log.Info($"Roundabouts applied. Queueing remaining roads next. Session {_sessionId}");
                        return;
                    }

                    // Roads are applied, so the roundabout centre nodes exist: place the roundabout objects, then wait
                    // another round of frames for them to apply before naming and completing.
                    if (!_nodeRoundaboutsPlaced && _nodeRoundabouts.Count > 0)
                    {
                        _nodeRoundaboutsPlaced = true;
                        PlaceNodeRoundabouts();
                        _applyWaitFrames = 0;
                        return;
                    }

                    // The roundabout objects are attached now; CS2 only rebuilds the junction as a ring once the node is
                    // updated again (verified with the bridge's roundabout_test: without this the island sits in a plain
                    // crossroads).
                    if (_nodeRoundaboutsPlaced && !_nodeRoundaboutsRefreshed)
                    {
                        _nodeRoundaboutsRefreshed = true;
                        RefreshRoundaboutNodes();
                        _applyWaitFrames = 0;
                        return;
                    }

                    ApplyQueuedRoadNames();
                    ApplyRoadServiceUpgrades();

                    // Parking lots last: their entrances need the finished streets, and their own path edges must not
                    // be mistaken for roads by the name matching above.
                    if (ProcessParkingLotStep())
                    {
                        _applyWaitFrames = 0;
                        return;
                    }

                    // Parks after parking lots: their buildings keep off the lots' aisles and garages.
                    if (ProcessParkStep())
                    {
                        _applyWaitFrames = 0;
                        return;
                    }

                    var message = generatedRoadEdges > 0
                        ? LF("OsmImporter.Status.GeneratedRoads", "CS2 consumed {0} road definitions; detected {1} new road edges. Import bounds: {2}.", _importedSegments, generatedRoadEdges, ShortBounds(_document))
                        : LF("OsmImporter.Status.GeneratedRoadsUnknownSimple", "CS2 consumed {0} road definitions. Generated road count could not be verified.", _importedSegments);

                    Mod.log.Info($"{message} Session {_sessionId}");
                    HasGeneratedAssets = true;
                    Complete(message);
                    ScheduleAutoGeometryReport();
                    return;
                }

                if (_applyWaitFrames >= MaxApplyWaitFrames)
                {
                    if (pendingDefinitions > 0)
                    {
                        if (_applyWaitPurpose == ApplyWaitPurpose.AfterConnectors && _roundaboutWays.Count > 0)
                        {
                            _applyWaitPurpose = ApplyWaitPurpose.AfterRoundabouts;
                            _phase = WorkPhase.QueueRoundabouts;
                            _applyWaitFrames = 0;
                            StatusSummary = LF("OsmImporter.Status.QueueingRoundabouts", "Queueing roundabouts after nearby roads apply...");
                            Mod.log.Info($"Timeout reached while waiting for connector roads. Queueing {_roundaboutWays.Count} roundabouts anyway. Session {_sessionId}, pending definitions {pendingDefinitions}");
                            return;
                        }

                        if (_applyWaitPurpose == ApplyWaitPurpose.AfterRoundabouts && _generationTypeOrder.Count > 0)
                        {
                            _applyWaitPurpose = ApplyWaitPurpose.AfterRoads;
                            _phase = WorkPhase.QueueDefinitions;
                            _applyWaitFrames = 0;
                            _typeIndex = 0;
                            _wayIndex = 0;
                            _pointIndex = 0;
                            StatusSummary = "Generating remaining roads...";
                            Mod.log.Info($"Timeout reached while waiting for roundabouts. Queueing remaining roads anyway. Session {_sessionId}, pending definitions {pendingDefinitions}");
                            return;
                        }

                        var message = LF("OsmImporter.Status.WaitingContinue", "Queued {0} road definitions; CS2 consumed {1} so far. Leave the editor running or try fewer road types.", _importedSegments, consumedDefinitions);
                        Mod.log.Info($"{message} Session {_sessionId}, pending definitions {pendingDefinitions}");
                        StatusSummary = message;
                        _applyWaitFrames = 0;
                        return;
                    }

                    var fallbackMessage = LF("OsmImporter.Status.GeneratedRoadsUnknownSimple", "CS2 consumed {0} road definitions. Generated road count could not be verified.", _importedSegments);
                    Mod.log.Info($"{fallbackMessage} Session {_sessionId}");
                    HasGeneratedAssets = true;
                    Complete(fallbackMessage);
                    ScheduleAutoGeometryReport();
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete(LF("OsmImporter.Status.WaitingFailed", "Failed while waiting for generated roads: {0}", ex.Message));
            }
        }

        private void ProcessRoundaboutBatch()
        {
            try
            {
                var segmentsThisFrame = 0;

                while (_roundaboutWayIndex < _roundaboutWays.Count)
                {
                    var way = _roundaboutWays[_roundaboutWayIndex];
                    if (way == null)
                    {
                        _roundaboutWayIndex++;
                        continue;
                    }

                    LogRoundaboutDiagnostics(way);
                    LogRoundaboutRoadPrefabDiagnostics();

                    var queuedInfo = BuildRoundaboutQueuedInfo(way);
                    if (math.distancesq(queuedInfo.StartPos, float3.zero) < 0.0001f &&
                        math.distancesq(queuedInfo.EndPos, float3.zero) < 0.0001f)
                    {
                        _roundaboutWayIndex++;
                        _pointIndex = 0;
                        continue;
                    }

                    var prefabToUse = GetPrefabForHighwayType(way.Highway, way.Lanes, way.Sidewalk);

                    var created = CreateNetCourseDefinition(queuedInfo.StartPos, queuedInfo.EndPos, prefabToUse, way.BaseElevationOffsetMeters);
                    if (created != Entity.Null)
                    {
                        if (queuedInfo.IsRoundabout && queuedInfo.RoundaboutRadius > 0f)
                        {
                            EntityManager.AddComponentData(created, new Roundabout { m_Radius = queuedInfo.RoundaboutRadius });
                            Mod.log.Info($"Applied roundabout component to queued entity {created.Index}:{created.Version} with radius {queuedInfo.RoundaboutRadius:F2}.");
                        }

                        HasGeneratedAssets = true;
                        _importedSegments++;
                        _queuedSegmentInfos.Add(queuedInfo);
                    }

                    segmentsThisFrame++;

                    _roundaboutWayIndex++;
                    _pointIndex = 0;
                    if (segmentsThisFrame >= SegmentsPerFrameLimit)
                        break;
                }

                if (_roundaboutWayIndex >= _roundaboutWays.Count)
                {
                    _applyWaitPurpose = ApplyWaitPurpose.AfterRoundabouts;
                    _phase = WorkPhase.WaitForGameApply;
                    _applyWaitFrames = 0;
                    _roundaboutNodeUpgradesApplied = false;
                    StatusSummary = "Roundabouts queued";
                    SetStatus(StatusSummary);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete(LF("OsmImporter.Status.RoundaboutQueueFailed", "Failed to queue roundabouts: {0}", ex.Message));
            }
        }

        private bool ApplyRoundaboutNodeUpgrades()
        {
            if (_roundaboutWays == null || _roundaboutWays.Count == 0 || _roundaboutNodeUpgradesApplied)
                return false;

            var appliedCount = 0;
            var loggedSamples = 0;
            var usedNodes = new HashSet<Entity>();
            foreach (var way in _roundaboutWays)
            {
                if (way == null || way.Points == null || way.Points.Count < 3)
                    continue;

                var pointCount = way.Points.Count;
                if (math.distance(new float2(way.Points[0].x, way.Points[0].z), new float2(way.Points[pointCount - 1].x, way.Points[pointCount - 1].z)) <= 0.5f)
                    pointCount--;

                var roundaboutFlags = new CompositionFlags(CompositionFlags.General.Roundabout, 0, 0);
                for (var i = 0; i < pointCount; i++)
                {
                    var targetPos = way.Points[i];
                    var nodeEntity = FindBestRoundaboutNodeEntity(targetPos, usedNodes);
                    if (nodeEntity == Entity.Null)
                        continue;

                    usedNodes.Add(nodeEntity);

                    var currentFlags = EntityManager.HasComponent<Upgraded>(nodeEntity)
                        ? EntityManager.GetComponentData<Upgraded>(nodeEntity).m_Flags
                        : default;

                    var mergedFlags = currentFlags;
                    mergedFlags.m_General |= roundaboutFlags.m_General;

                    if (EntityManager.HasComponent<Upgraded>(nodeEntity))
                        EntityManager.SetComponentData(nodeEntity, new Upgraded { m_Flags = mergedFlags });
                    else
                        EntityManager.AddComponentData(nodeEntity, new Upgraded { m_Flags = mergedFlags });

                    if (EntityManager.HasComponent<Updated>(nodeEntity))
                        EntityManager.SetComponentData(nodeEntity, new Updated());
                    else
                        EntityManager.AddComponentData(nodeEntity, new Updated());

                    if (loggedSamples < 5)
                    {
                        using var componentTypes = EntityManager.GetComponentTypes(nodeEntity, Allocator.Temp);
                        var componentSummary = componentTypes.Length == 0
                            ? string.Empty
                            : string.Join(", ", componentTypes.Select(t => t.ToString()));

                        Mod.log.Info(
                            $"Roundabout node upgrade sample: node={nodeEntity.Index}:{nodeEntity.Version}, target={FormatPoint(targetPos)}, " +
                            $"components=[{componentSummary}], currentGeneral={(uint)currentFlags.m_General}, currentLeft={(uint)currentFlags.m_Left}, currentRight={(uint)currentFlags.m_Right}, " +
                            $"mergedGeneral={(uint)mergedFlags.m_General}, mergedLeft={(uint)mergedFlags.m_Left}, mergedRight={(uint)mergedFlags.m_Right}.");
                        loggedSamples++;
                    }

                    appliedCount++;
                }
            }

            _roundaboutNodeUpgradesApplied = true;
            Mod.log.Info($"Applied roundabout upgrade flags to {appliedCount} roundabout nodes.");
            return appliedCount > 0;
        }

        private Entity FindBestRoundaboutNodeEntity(float3 targetPos, HashSet<Entity> excludedNodes)
        {
            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Node>(),
                ComponentType.ReadOnly<ConnectedEdge>());
            using var nodes = query.ToEntityArray(Allocator.Temp);

            var best = Entity.Null;
            var bestScore = float.MaxValue;

            foreach (var node in nodes)
            {
                if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
                    continue;

                if (excludedNodes != null && excludedNodes.Contains(node))
                    continue;

                var nodePos = EntityManager.GetComponentData<Node>(node).m_Position;
                var distSq = math.distancesq(nodePos.xz, targetPos.xz);
                if (!EntityManager.HasBuffer<ConnectedEdge>(node))
                    continue;

                var connectedEdges = EntityManager.GetBuffer<ConnectedEdge>(node);
                if (connectedEdges.Length < 2)
                    continue;

                if (distSq < bestScore)
                {
                    bestScore = distSq;
                    best = node;
                }
            }

            return best;
        }

        private void LogRoundaboutRoadPrefabDiagnostics()
        {
            if (_loggedRoundaboutRoadPrefabDiagnostics || _prefabSystem == null)
                return;

            _loggedRoundaboutRoadPrefabDiagnostics = true;

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetObjectData>());
            using var prefabs = query.ToEntityArray(Allocator.Temp);

            var matches = new List<string>();
            var nodeUpgradeCount = 0;
            var upgradeCount = 0;
            var roundaboutCompositionCount = 0;
            var supportRoundaboutCount = 0;
            var roundaboutNameCount = 0;
            foreach (var entity in prefabs)
            {
                if (IsDlcGatedPrefab(entity))
                    continue;

                using var componentTypes = EntityManager.GetComponentTypes(entity, Allocator.Temp);
                var componentNames = componentTypes.Select(t => t.ToString()).ToList();
                var hasRoundaboutName = componentNames.Any(n => n.IndexOf("Roundabout", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("SupportRoundabout", StringComparison.OrdinalIgnoreCase) >= 0);

                var hasRoundaboutFlags = false;
                var hasNodeUpgradePlacement = false;
                var hasUpgradePlacement = false;
                var placementFlags = Game.Net.PlacementFlags.None;
                var roundaboutComposition = false;
                var supportRoundaboutGeometry = false;

                if (EntityManager.HasComponent<PlaceableNetData>(entity))
                {
                    placementFlags = EntityManager.GetComponentData<PlaceableNetData>(entity).m_PlacementFlags;
                    hasNodeUpgradePlacement = (placementFlags & Game.Net.PlacementFlags.NodeUpgrade) != Game.Net.PlacementFlags.None;
                    hasUpgradePlacement = (placementFlags & Game.Net.PlacementFlags.IsUpgrade) != Game.Net.PlacementFlags.None;
                    if (hasNodeUpgradePlacement)
                        nodeUpgradeCount++;
                    if (hasUpgradePlacement)
                        upgradeCount++;
                    hasRoundaboutFlags |= hasNodeUpgradePlacement || hasUpgradePlacement;
                }

                if (EntityManager.HasComponent<NetObjectData>(entity))
                {
                    roundaboutComposition = EntityManager.GetComponentData<NetObjectData>(entity).m_CompositionFlags.m_General.HasFlag(CompositionFlags.General.Roundabout);
                    if (roundaboutComposition)
                        roundaboutCompositionCount++;
                    hasRoundaboutFlags |= roundaboutComposition;
                }

                if (EntityManager.HasComponent<NetGeometryData>(entity))
                {
                    supportRoundaboutGeometry = (EntityManager.GetComponentData<NetGeometryData>(entity).m_Flags & Game.Net.GeometryFlags.SupportRoundabout) != 0;
                    if (supportRoundaboutGeometry)
                        supportRoundaboutCount++;
                    hasRoundaboutFlags |= supportRoundaboutGeometry;
                }

                if (hasRoundaboutName)
                    roundaboutNameCount++;

                if (!hasRoundaboutName && !hasRoundaboutFlags)
                    continue;

                matches.Add(
                    $"{DescribePrefab(entity)} [placement={placementFlags}, roundaboutComposition={roundaboutComposition}, supportRoundaboutGeometry={supportRoundaboutGeometry}, components=[{string.Join(", ", componentNames)}]]");
            }

            if (matches.Count == 0)
            {
                Mod.log.Info($"No road prefabs with roundabout-related components were found. nameMatches={roundaboutNameCount}, nodeUpgrade={nodeUpgradeCount}, upgrade={upgradeCount}, roundaboutComposition={roundaboutCompositionCount}, supportRoundabout={supportRoundaboutCount}.");
                return;
            }

            Mod.log.Info($"Roundabout-capable prefab diagnostics ({matches.Count}): nameMatches={roundaboutNameCount}, nodeUpgrade={nodeUpgradeCount}, upgrade={upgradeCount}, roundaboutComposition={roundaboutCompositionCount}, supportRoundabout={supportRoundaboutCount}.");
            foreach (var match in matches.Take(50))
                Mod.log.Info($"  {match}");
        }

        private void RemoveIntersectingPillars()
        {
            try
            {
                _pillarWaitFrames++;

                if (_pillarWaitFrames < PillarWaitFrameThreshold)
                    return;

                var allPillarEntities = QueryPillarEntities();
                if (allPillarEntities == null || allPillarEntities.Count == 0)
                {
                    Mod.log.Info("No pillar entities found. Skipping pillar intersection check.");
                    Complete(L("OsmImporter.Status.RoadGenerationComplete", "Road generation complete."));
                    return;
                }

                var newPillars = new List<Entity>();
                foreach (var entity in allPillarEntities)
                {
                    if (!_baselinePillarEntities.Contains(entity))
                        newPillars.Add(entity);
                }

                if (newPillars.Count == 0)
                {
                    Mod.log.Info("No new pillar entities found to check for intersection.");
                    Complete(L("OsmImporter.Status.RoadGenerationComplete", "Road generation complete."));
                    return;
                }

                Mod.log.Info($"Found {newPillars.Count} new pillar entities (after baseline exclusion).");

                var roadEdgePositions = GetGeneratedRoadEdgePositions();
                if (roadEdgePositions.Length == 0)
                {
                    Mod.log.Info("No generated road edges found. Skipping pillar intersection check.");
                    roadEdgePositions.Dispose();
                    Complete(L("OsmImporter.Status.RoadGenerationComplete", "Road generation complete."));
                    return;
                }

                var cellSize = 5f;
                var spatialIndex = new NativeParallelMultiHashMap<int, int>(roadEdgePositions.Length, Allocator.Temp);
                for (var i = 0; i < roadEdgePositions.Length; i++)
                {
                    var mid = (roadEdgePositions[i].Start + roadEdgePositions[i].End) * 0.5f;
                    spatialIndex.Add(SpatialCellKey(mid, cellSize), i);
                    spatialIndex.Add(SpatialCellKey(roadEdgePositions[i].Start, cellSize), i);
                    spatialIndex.Add(SpatialCellKey(roadEdgePositions[i].End, cellSize), i);
                }

                var threshold = (_activeGenerateOptions?.IntersectionThresholdMeters ?? 8f);
                var thresholdSq = threshold * threshold;
                var intersectingCount = 0;
                var totalChecked = 0;
                var failedPosCount = 0;

                foreach (var pillarEntity in newPillars)
                {
                    if (!EntityManager.Exists(pillarEntity))
                        continue;

                    if (!TryGetPillarPosition(pillarEntity, out var pillarPos))
                    {
                        failedPosCount++;
                        continue;
                    }

                    totalChecked++;
                    var intersects = false;

                    for (var dx = -1; dx <= 1 && !intersects; dx++)
                    {
                        for (var dz = -1; dz <= 1 && !intersects; dz++)
                        {
                            var neighborPos = pillarPos + new float3(dx * cellSize, 0f, dz * cellSize);
                            var key = SpatialCellKey(neighborPos, cellSize);
                            if (!spatialIndex.TryGetFirstValue(key, out var edgeIdx, out var it))
                                continue;

                            do
                            {
                                var edge = roadEdgePositions[edgeIdx];
                                var closestSq = ClosestPointOnSegmentXZSq(pillarPos, edge.Start, edge.End);
                                if (closestSq < thresholdSq)
                                {
                                    intersects = true;
                                    break;
                                }
                            }
                            while (spatialIndex.TryGetNextValue(out edgeIdx, ref it));
                        }
                    }

                    if (intersects)
                    {
                        if (!EntityManager.HasComponent<Deleted>(pillarEntity))
                        {
                            EntityManager.AddComponentData(pillarEntity, new Deleted());
                            EntityManager.AddComponentData(pillarEntity, new Updated());
                        }
                        intersectingCount++;
                    }
                }

                spatialIndex.Dispose();
                roadEdgePositions.Dispose();

                Mod.log.Info(
                    $"Pillar intersection cleanup: checked {totalChecked} pillars, " +
                    $"{failedPosCount} failed position reads, " +
                    $"deleted {intersectingCount} intersecting. Threshold: {threshold:F1}m");

                Complete(LF("OsmImporter.Status.RoadGenerationComplete",
                    "Road generation complete."));
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete(LF("OsmImporter.Status.PillarCleanupFailed",
                    "Failed during pillar intersection cleanup: {0}", ex.Message));
            }
        }

        private List<Entity> QueryPillarEntities()
        {
            try
            {
                using var query = EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<Pillar>(),
                    ComponentType.Exclude<Deleted>());
                var entities = query.ToEntityArray(Allocator.Temp).ToList();
                Mod.log.Info($"QueryPillarEntities found {entities.Count} total pillar entities (excluding Deleted).");
                return entities;
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"QueryPillarEntities failed: {ex.Message}");
                return new List<Entity>();
            }
        }

        private bool TryGetPillarPosition(Entity pillarEntity, out float3 position)
        {
            position = float3.zero;

            try
            {
                if (EntityManager.HasComponent<Game.Objects.Transform>(pillarEntity))
                {
                    var t = EntityManager.GetComponentData<Game.Objects.Transform>(pillarEntity);
                    position = t.m_Position;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Failed to get pillar position: {ex.Message}");
            }

            return false;
        }

        private static float ClosestPointOnSegmentXZSq(float3 point, float3 a, float3 b)
        {
            var ab = new float2(b.x - a.x, b.z - a.z);
            var ap = new float2(point.x - a.x, point.z - a.z);
            var lenSq = math.lengthsq(ab);

            if (lenSq < 0.0001f)
                return math.lengthsq(ap);

            var t = math.clamp(math.dot(ap, ab) / lenSq, 0f, 1f);
            var closest = a + t * (b - a);
            var dx = point.x - closest.x;
            var dz = point.z - closest.z;
            return dx * dx + dz * dz;
        }

        private static bool PointsAreCollinear(float3 prev, float3 mid, float3 next, float cosAngle)
        {
            var d1 = new float2(mid.x - prev.x, mid.z - prev.z);
            var d2 = new float2(next.x - mid.x, next.z - mid.z);
            var len1 = math.lengthsq(d1);
            var len2 = math.lengthsq(d2);
            if (len1 < 0.01f || len2 < 0.01f)
                return true;
            var dot = math.dot(d1, d2) / (math.sqrt(len1 * len2));
            return dot >= cosAngle;
        }

        private struct RoadEdgePosition
        {
            public float3 Start;
            public float3 End;
        }

        private NativeList<RoadEdgePosition> GetGeneratedRoadEdgePositions()
        {
            var result = new NativeList<RoadEdgePosition>(Allocator.Temp);
            using var entities = GetEntityQuery(
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Road>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp);

            var edgeLookup = GetComponentLookup<Edge>(true);
            var nodeLookup = GetComponentLookup<Node>(true);

            foreach (var entity in entities)
            {
                if (_baselineSegments.Contains(entity))
                    continue;

                if (!edgeLookup.HasComponent(entity))
                    continue;

                var edge = edgeLookup[entity];
                if (!nodeLookup.HasComponent(edge.m_Start) || !nodeLookup.HasComponent(edge.m_End))
                    continue;

                result.Add(new RoadEdgePosition
                {
                    Start = nodeLookup[edge.m_Start].m_Position,
                    End = nodeLookup[edge.m_End].m_Position
                });
            }

            return result;
        }

        private void DeleteRoads()
        {
            try
            {
                if (_deleteCompletionPending)
                {
                    _deleteCompletionPending = false;
                    _pendingDeleteEntities.Clear();
                    HasGeneratedAssets = false;
                    Complete(LF("OsmImporter.Status.DeleteComplete", "Marked {0} edges for deletion.", _deletedEdgeCount));
                    Mod.log.Info(Status);
                    return;
                }

                var processedThisFrame = 0;
                while (_pendingDeleteIndex < _pendingDeleteEntities.Count && processedThisFrame < MaxSegmentsPerFrame)
                {
                    var entity = _pendingDeleteEntities[_pendingDeleteIndex++];
                    if (EntityManager.Exists(entity) && !EntityManager.HasComponent<Deleted>(entity))
                    {
                        EntityManager.AddComponentData(entity, new Deleted());
                        EntityManager.AddComponentData(entity, new Updated());
                        _deletedEdgeCount++;
                    }

                    processedThisFrame++;
                }

                if (_pendingDeleteIndex < _pendingDeleteEntities.Count)
                {
                    SetStatus(L("OsmImporter.Status.DeletingSegments", "Deleting all segments..."));
                    StatusSummary = LF("OsmImporter.Status.DeleteProgress", "Deleting roads... {0}/{1}", _pendingDeleteIndex, _pendingDeleteEntities.Count);
                    return;
                }

                SetStatus(L("OsmImporter.Status.DeletingSegments", "Deleting all segments..."));
                StatusSummary = LF("OsmImporter.Status.DeleteProgress", "Deleting roads... {0}/{1}", _pendingDeleteIndex, _pendingDeleteEntities.Count);
                _deleteCompletionPending = true;
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete(LF("OsmImporter.Status.DeleteFailed", "Failed to delete segments: {0}", ex.Message));
            }
        }

        private void Complete(string message)
        {
            SetStatus(message);
            StatusSummary = string.Empty;
            IsImporting = false;
            _phase = WorkPhase.Idle;
            _pendingLoadOptions = null;
            _pendingGenerateOptions = null;
            _activeGenerateOptions = null;
        }

        private void SetStatus(string message)
        {
            _status = message;
        }

        private static bool IsValidOsmPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!File.Exists(path))
                return false;

            return string.Equals(Path.GetExtension(path), ".osm", StringComparison.OrdinalIgnoreCase);
        }

        private Entity CreateNetCourseDefinition(float3 start, float3 end, Entity prefabEntity, float elevationOffset = 0f)
        {
            var delta = end - start;
            var length = math.length(delta);
            if (length < MinSegmentLengthMeters)
                return Entity.Null;

            if (!_loggedFirstQueuedSegment)
            {
                _loggedFirstQueuedSegment = true;
                Mod.log.Info(
                    $"First queued segment sample: start={FormatPoint(start)}, end={FormatPoint(end)}, length={length:F2}, prefab={DescribePrefab(prefabEntity)}, elevationOffset={elevationOffset:F1}");
            }

            var tangent = length > 0.001f ? delta / 3f : new float3(0f, 0f, 0f);

            var entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition
            {
                m_Prefab = prefabEntity,
                m_SubPrefab = Entity.Null,
                m_Original = Entity.Null,
                m_Owner = Entity.Null,
                m_Attached = Entity.Null,
                m_Flags = CreationFlags.Permanent,
                m_RandomSeed = ImportedHash(start, end)
            });
            EntityManager.AddComponentData(entity, new NetCourse
            {
                m_StartPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = start,
                    m_Rotation = quaternion.identity,
                    m_Elevation = float2.zero,
                    m_CourseDelta = 0f,
                    m_SplitPosition = 0f,
                    m_Flags = CoursePosFlags.IsFirst,
                    m_ParentMesh = -1
                },
                m_EndPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = end,
                    m_Rotation = quaternion.identity,
                    m_Elevation = float2.zero,
                    m_CourseDelta = 1f,
                    m_SplitPosition = 1f,
                    m_Flags = CoursePosFlags.IsLast,
                    m_ParentMesh = -1
                },
                m_Curve = new Bezier4x3(start, start + tangent, end - tangent, end),
                m_Elevation = float2.zero,
                m_Length = length
            });
            EntityManager.AddComponentData(entity, new Updated());
            EntityManager.AddComponentData(entity, new OsmImportDefinition { SessionId = _sessionId });

            return entity;
        }

        private void LogRoundaboutDiagnostics(OsmWay way)
        {
            if (way == null || way.Points == null || way.Points.Count < 3)
                return;

            if (!_loggedFirstQueuedSegment)
            {
                _loggedFirstQueuedSegment = true;
                Mod.log.Info($"First queued roundabout sample: points={way.Points.Count}, highway={way.Highway}, name={way.Name}");
            }

            if (!TryBuildRoundaboutGeometry(way, out var center, out _, out _, out var radius))
                return;

            Mod.log.Info($"Roundabout diagnostics: center={FormatPoint(center)}, radius={radius:F2}, points={way.Points.Count}.");
        }

        // CS2 roundabout objects (StaticObjectPrefab + NetObjectData) by island diameter; variant 01 of each size.
        private static readonly (float islandDiameter, string prefab)[] RoundaboutObjectSizes =
        {
            (6f, "SmallRoundabout01"),
            (14f, "MediumRoundabout01"),
            (22f, "LargeRoundabout01"),
            (30f, "XLRoundabout01"),
        };

        /// <summary>
        /// Place a CS2 roundabout object on the junction node at each small roundabout's centre, the way the road tool
        /// does: a CreationDefinition attached to the node plus an ObjectDefinition. The size whose island best fits the
        /// OSM ring (centreline radius minus about half a carriageway) is used.
        /// </summary>
        private void PlaceNodeRoundabouts()
        {
            if (_roundaboutPrefabs == null)
            {
                _roundaboutPrefabs = new Dictionary<string, Entity>();
                var wanted = new HashSet<string>(RoundaboutObjectSizes.Select(s => s.prefab));
                using var prefabs = GetEntityQuery(ComponentType.ReadOnly<PrefabData>()).ToEntityArray(Allocator.Temp);
                foreach (var p in prefabs)
                {
                    var name = _prefabSystem.GetPrefabName(p);
                    if (wanted.Contains(name))
                        _roundaboutPrefabs[name] = p;
                }
            }

            var nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Owner>() }
            });
            using var nodeArray = nodes.ToEntityArray(Allocator.Temp);

            var placed = 0;
            foreach (var way in _nodeRoundabouts)
            {
                var center = way.RoundaboutCenter;
                var node = Entity.Null;
                var best = 2f;
                foreach (var n in nodeArray)
                {
                    var d = math.distance(EntityManager.GetComponentData<Node>(n).m_Position.xz, center.xz);
                    if (d < best)
                    {
                        best = d;
                        node = n;
                    }
                }

                if (node == Entity.Null)
                {
                    Mod.log.Warn($"Roundabout way {way.Id}: no junction node at its centre {FormatPoint(center)} (were its connecting roads generated?). Skipped.");
                    continue;
                }

                const float halfCarriageway = 6f;
                var size = RoundaboutObjectSizes
                    .OrderBy(s => math.abs(s.islandDiameter * 0.5f + halfCarriageway - way.RoundaboutRadius))
                    .First();
                if (!_roundaboutPrefabs.TryGetValue(size.prefab, out var prefab))
                {
                    Mod.log.Warn($"Roundabout prefab '{size.prefab}' not found. Skipped roundabout way {way.Id}.");
                    continue;
                }

                var entity = EntityManager.CreateEntity();
                EntityManager.AddComponentData(entity, new CreationDefinition
                {
                    m_Prefab = prefab,
                    m_Attached = node,
                    m_Flags = CreationFlags.Permanent,
                    m_RandomSeed = ImportedHash(center, center + new float3(1f, 0f, 0f))
                });
                EntityManager.AddComponentData(entity, new ObjectDefinition
                {
                    m_Position = EntityManager.GetComponentData<Node>(node).m_Position,
                    m_Rotation = quaternion.identity,
                    m_LocalRotation = quaternion.identity,
                    m_Scale = new float3(1f),
                    m_Intensity = 1f,
                    m_Probability = 100,
                    m_ParentMesh = -1,
                    m_PrefabSubIndex = -1
                });
                EntityManager.AddComponentData(entity, new Updated());
                placed++;
                _nodeRoundaboutNodes.Add(node);
                Mod.log.Info($"Roundabout way {way.Id} (radius {way.RoundaboutRadius:F1} m): placing {size.prefab} on node {node.Index}:{node.Version}.");
            }

            Mod.log.Info($"Placed {placed} of {_nodeRoundabouts.Count} roundabout objects.");
        }

        private void RefreshRoundaboutNodes()
        {
            foreach (var node in _nodeRoundaboutNodes)
            {
                if (!EntityManager.Exists(node))
                    continue;
                if (!EntityManager.HasComponent<Updated>(node))
                    EntityManager.AddComponent<Updated>(node);
                if (!EntityManager.HasBuffer<ConnectedEdge>(node))
                    continue;
                // Copy first: adding components is a structural change that invalidates the buffer.
                var edges = new List<Entity>();
                foreach (var connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    edges.Add(connected.m_Edge);
                foreach (var edge in edges)
                {
                    if (EntityManager.Exists(edge) && !EntityManager.HasComponent<Updated>(edge))
                        EntityManager.AddComponent<Updated>(edge);
                }
            }
            Mod.log.Info($"Refreshed {_nodeRoundaboutNodes.Count} roundabout junction nodes.");
        }

        /// <summary>Roundabout objects (placed by PlaceNodeRoundabouts) attached to nodes of the given edges.</summary>
        private List<Entity> FindRoundaboutObjectsOn(List<Entity> edges)
        {
            var nodes = new HashSet<Entity>();
            foreach (var e in edges)
            {
                if (!EntityManager.Exists(e) || !EntityManager.HasComponent<Edge>(e))
                    continue;
                var edge = EntityManager.GetComponentData<Edge>(e);
                nodes.Add(edge.m_Start);
                nodes.Add(edge.m_End);
            }

            var result = new List<Entity>();
            var names = new HashSet<string>(RoundaboutObjectSizes.Select(s => s.prefab));
            using var attached = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Attached>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            }).ToEntityArray(Allocator.Temp);
            foreach (var o in attached)
            {
                if (!nodes.Contains(EntityManager.GetComponentData<Attached>(o).m_Parent))
                    continue;
                if (names.Contains(GetPrefabName(EntityManager.GetComponentData<PrefabRef>(o).m_Prefab)))
                    result.Add(o);
            }
            return result;
        }

        private static QueuedSegmentInfo BuildRoundaboutQueuedInfo(OsmWay way)
        {
            if (way == null || !TryBuildRoundaboutGeometry(way, out _, out var start, out var end, out var radius))
                return default;

            return new QueuedSegmentInfo
            {
                StartPos = start,
                EndPos = end,
                StartTerrainHeight = start.y,
                EndTerrainHeight = end.y,
                ElevationOffset = way.BaseElevationOffsetMeters,
                RoundaboutRadius = radius,
                Name = !string.IsNullOrWhiteSpace(way.Name) ? way.Name : (way.Ref ?? string.Empty),
                Lanes = way.Lanes,
                IsRoundabout = true,
                Way = way,
            };
        }

        private static bool TryBuildRoundaboutGeometry(OsmWay way, out float3 center, out float3 start, out float3 end, out float radius)
        {
            center = float3.zero;
            start = float3.zero;
            end = float3.zero;
            radius = 0f;

            var points = way?.Points;
            if (points == null || points.Count < 3)
                return false;

            var closedCount = points.Count;
            if (math.distance(new float2(points[0].x, points[0].z), new float2(points[points.Count - 1].x, points[points.Count - 1].z)) <= 0.5f)
                closedCount--;

            if (closedCount < 3)
                return false;

            for (var i = 0; i < closedCount; i++)
                center += points[i];
            center /= closedCount;

            for (var i = 0; i < closedCount; i++)
                radius += math.distance(new float2(points[i].x, points[i].z), new float2(center.x, center.z));
            radius /= closedCount;

            if (radius < MinSegmentLengthMeters)
                return false;

            start = points[0];
            var oppositeIndex = closedCount / 2;
            end = points[Math.Min(oppositeIndex, closedCount - 1)];
            if (math.distance(new float2(start.x, start.z), new float2(end.x, end.z)) < MinSegmentLengthMeters)
            {
                start = center + new float3(radius, 0f, 0f);
                end = center - new float3(radius, 0f, 0f);
            }

            return true;
        }

        private void ApplyQueuedRoadNames()
        {
            if (_roadNamesApplied || _nameSystem == null || _queuedSegmentInfos.Count == 0)
                return;

            using var edgeArray = GetCurrentNetEdgeArray(Allocator.Temp);
            if (edgeArray.Length == 0)
                return;

            var newEdges = new List<Entity>();
            foreach (var entity in edgeArray)
            {
                if (!_baselineSegments.Contains(entity))
                    newEdges.Add(entity);
            }

            if (newEdges.Count == 0)
                return;

            var edgeLookup = GetComponentLookup<Edge>(true);
            var nodeLookup = GetComponentLookup<Node>(true);

            var cellSize = 5f;
            var spatialIndex = new NativeParallelMultiHashMap<int, int>(_queuedSegmentInfos.Count, Allocator.Temp);

            for (var i = 0; i < _queuedSegmentInfos.Count; i++)
            {
                var queuedInfo = _queuedSegmentInfos[i];
                spatialIndex.Add(SpatialCellKey(queuedInfo.MidPoint, cellSize), i);
                spatialIndex.Add(SpatialCellKey(queuedInfo.StartPos, cellSize), i);
                spatialIndex.Add(SpatialCellKey(queuedInfo.EndPos, cellSize), i);
            }

            var matchedSegments = new NativeParallelHashSet<int>(_queuedSegmentInfos.Count, Allocator.Temp);
            var namedTargets = new HashSet<Entity>();
            var namedCount = 0;
            var proxCheckCount = 0;
            var proxMissCount = 0;
            var dirMismatchCount = 0;
            var globalFallbackCount = 0;
            var unmatchedLogCount = 0;

            foreach (var edgeEntity in newEdges)
            {
                if (!edgeLookup.HasComponent(edgeEntity))
                    continue;

                var edge = edgeLookup[edgeEntity];
                var hasStart = nodeLookup.HasComponent(edge.m_Start);
                var hasEnd = nodeLookup.HasComponent(edge.m_End);
                if (!hasStart || !hasEnd)
                    continue;

                var startPos = nodeLookup[edge.m_Start].m_Position;
                var endPos = nodeLookup[edge.m_End].m_Position;
                var edgeMid = (startPos + endPos) * 0.5f;
                var edgeDir = math.normalizesafe(endPos - startPos, new float3(0f, 0f, 1f));

                var bestEndpointScore = float.MaxValue;
                var bestMidScore = float.MaxValue;
                var bestIndex = -1;

                ConsiderQueuedSegmentMatches(spatialIndex, matchedSegments, startPos, endPos, startPos, edgeMid, edgeDir, cellSize, ref bestIndex, ref bestEndpointScore, ref bestMidScore, ref dirMismatchCount);
                ConsiderQueuedSegmentMatches(spatialIndex, matchedSegments, startPos, endPos, endPos, edgeMid, edgeDir, cellSize, ref bestIndex, ref bestEndpointScore, ref bestMidScore, ref dirMismatchCount);
                ConsiderQueuedSegmentMatches(spatialIndex, matchedSegments, startPos, endPos, edgeMid, edgeMid, edgeDir, cellSize, ref bestIndex, ref bestEndpointScore, ref bestMidScore, ref dirMismatchCount);

                if (bestIndex < 0)
                {
                    if (TryFindBestQueuedSegmentGlobally(startPos, endPos, edgeMid, edgeDir, matchedSegments, out var globalIndex, out var globalScore))
                    {
                        bestIndex = globalIndex;
                        bestEndpointScore = globalScore;
                        globalFallbackCount++;
                        if (globalFallbackCount <= 5)
                            Mod.log.Info($"Global fallback matched edge {edgeEntity.Index}:{edgeEntity.Version} to '{_queuedSegmentInfos[bestIndex].Name}' with score {globalScore:F1}.");
                    }
                }

                if (bestIndex < 0)
                {
                    proxMissCount++;
                    if (unmatchedLogCount < 5)
                    {
                        unmatchedLogCount++;
                        LogUnmatchedRoadEdge(spatialIndex, edgeEntity, startPos, endPos, edgeMid, edgeDir);
                    }
                    continue;
                }

                matchedSegments.Add(bestIndex);
                var roadName = _queuedSegmentInfos[bestIndex].Name;
                if (!string.IsNullOrWhiteSpace(roadName))
                {
                    var targetEntity = ResolveRoadNameTarget(edgeEntity);
                    if (namedTargets.Add(targetEntity))
                    {
                        PendingNames.Add((targetEntity, roadName));
                        proxCheckCount++;
                    }
                }
                namedCount++;
                _generatedEdgeEntities.Add(edgeEntity);
                _generatedEdgeWays[edgeEntity] = _queuedSegmentInfos[bestIndex].Way;
            }

            _roadNamesApplied = true;
            Mod.log.Info(
                $"Deferred {proxCheckCount} road names for SetCustomName on aggregates (UIUpdate). " +
                $"{namedCount} total edge matches, {proxMissCount} unmatched edges, " +
                $"{dirMismatchCount} direction mismatches skipped, {globalFallbackCount} global fallbacks. " +
                $"{newEdges.Count} new edges, {_queuedSegmentInfos.Count} queued segments.");

            spatialIndex.Dispose();
            matchedSegments.Dispose();
        }

        private void ApplyRoadServiceUpgrades()
        {
            if (_roadServiceUpgradesApplied)
                return;

            _roadServiceUpgradesApplied = true;

            if (_generatedEdgeWays.Count == 0)
                return;

            var edgeLookup = GetComponentLookup<Edge>(true);
            var nodeLookup = GetComponentLookup<Node>(true);
            var upgradedEdges = 0;
            var upgradedNodes = 0;
            var chunkEntities = new HashSet<Entity>();
            var touchedNodes = new HashSet<Entity>();

            foreach (var pair in _generatedEdgeWays)
            {
                var edgeEntity = pair.Key;
                var way = pair.Value;
                if (way == null || !EntityManager.Exists(edgeEntity) || !edgeLookup.HasComponent(edgeEntity))
                    continue;

                var flags = default(CompositionFlags);

                if (_activeGenerateOptions == null || _activeGenerateOptions.GenerateBikeLanes)
                    AddSideFlags(ref flags, way.BikeLaneSide, CompositionFlags.Side.SecondaryLane);

                if (_activeGenerateOptions == null || _activeGenerateOptions.GenerateRestrictedLanes)
                    AddSideFlags(ref flags, way.RestrictedLaneSide, CompositionFlags.Side.PrimaryLane);

                if (HasAnyFlags(flags) && MergeUpgradedFlags(edgeEntity, flags))
                {
                    upgradedEdges++;
                    chunkEntities.Add(edgeEntity);
                }

                var edge = edgeLookup[edgeEntity];
                if (!nodeLookup.HasComponent(edge.m_Start) || !nodeLookup.HasComponent(edge.m_End))
                    continue;

                ApplyNodeRoadServiceUpgrades(way, edge.m_Start, nodeLookup[edge.m_Start].m_Position, touchedNodes, chunkEntities, ref upgradedNodes);
                ApplyNodeRoadServiceUpgrades(way, edge.m_End, nodeLookup[edge.m_End].m_Position, touchedNodes, chunkEntities, ref upgradedNodes);
            }

            MarkChunksDirty(chunkEntities);
            Mod.log.Info($"Applied road service upgrade flags to {upgradedEdges} generated edges and {upgradedNodes} generated nodes.");
        }

        private void ApplyNodeRoadServiceUpgrades(
            OsmWay way,
            Entity nodeEntity,
            float3 nodePosition,
            HashSet<Entity> touchedNodes,
            HashSet<Entity> chunkEntities,
            ref int upgradedNodes)
        {
            if (way == null || nodeEntity == Entity.Null || !EntityManager.Exists(nodeEntity))
                return;

            var flags = default(CompositionFlags);

            if ((_activeGenerateOptions == null || _activeGenerateOptions.GenerateCrosswalks) &&
                HasNearbyNodeFeature(way.Crosswalks, nodePosition, 6f))
            {
                flags.m_General |= CompositionFlags.General.Crosswalk;
                flags.m_Left |= CompositionFlags.Side.AddCrosswalk;
                flags.m_Right |= CompositionFlags.Side.AddCrosswalk;
            }

            if ((_activeGenerateOptions == null || _activeGenerateOptions.GenerateStopSigns) &&
                HasNearbyNodeFeature(way.StopSigns, nodePosition, 6f))
            {
                flags.m_General |= CompositionFlags.General.AllWayStop;
            }

            if ((_activeGenerateOptions == null || _activeGenerateOptions.GenerateTrafficLights) &&
                HasNearbyNodeFeature(way.TrafficLights, nodePosition, 6f))
            {
                flags.m_General |= CompositionFlags.General.TrafficLights;
            }

            if (_activeGenerateOptions == null || _activeGenerateOptions.GenerateTurnRestrictions)
                AddTurnRestrictionFlags(ref flags, way.TurnRestrictions, nodePosition);

            if (!HasAnyFlags(flags))
                return;

            if (MergeUpgradedFlags(nodeEntity, flags) && touchedNodes.Add(nodeEntity))
            {
                upgradedNodes++;
                chunkEntities.Add(nodeEntity);
            }
        }

        private bool MergeUpgradedFlags(Entity entity, CompositionFlags flags)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) || !HasAnyFlags(flags))
                return false;

            var currentFlags = EntityManager.HasComponent<Upgraded>(entity)
                ? EntityManager.GetComponentData<Upgraded>(entity).m_Flags
                : default;

            var merged = currentFlags;
            merged.m_General |= flags.m_General;
            merged.m_Left |= flags.m_Left;
            merged.m_Right |= flags.m_Right;

            if (merged.m_General == currentFlags.m_General &&
                merged.m_Left == currentFlags.m_Left &&
                merged.m_Right == currentFlags.m_Right)
                return false;

            if (EntityManager.HasComponent<Upgraded>(entity))
                EntityManager.SetComponentData(entity, new Upgraded { m_Flags = merged });
            else
                EntityManager.AddComponentData(entity, new Upgraded { m_Flags = merged });

            if (EntityManager.HasComponent<Updated>(entity))
                EntityManager.SetComponentData(entity, new Updated());
            else
                EntityManager.AddComponentData(entity, new Updated());

            return true;
        }

        private static void AddSideFlags(ref CompositionFlags flags, OsmRoadFeatureSide side, CompositionFlags.Side sideFlag)
        {
            switch (side)
            {
                case OsmRoadFeatureSide.Left:
                    flags.m_Left |= sideFlag;
                    break;
                case OsmRoadFeatureSide.Right:
                    flags.m_Right |= sideFlag;
                    break;
                case OsmRoadFeatureSide.Both:
                    flags.m_Left |= sideFlag;
                    flags.m_Right |= sideFlag;
                    break;
            }
        }

        private static bool HasAnyFlags(CompositionFlags flags)
        {
            return !flags.m_General.Equals(default(CompositionFlags.General)) ||
                   !flags.m_Left.Equals(default(CompositionFlags.Side)) ||
                   !flags.m_Right.Equals(default(CompositionFlags.Side));
        }

        private static bool HasNearbyNodeFeature(List<OsmRoadNodeFeature> features, float3 nodePosition, float maxDistanceMeters)
        {
            if (features == null || features.Count == 0)
                return false;

            var maxDistanceSq = maxDistanceMeters * maxDistanceMeters;
            foreach (var feature in features)
            {
                if (feature == null)
                    continue;

                if (math.distancesq(feature.Position.xz, nodePosition.xz) <= maxDistanceSq)
                    return true;
            }

            return false;
        }

        private static void AddTurnRestrictionFlags(ref CompositionFlags flags, List<OsmTurnRestriction> restrictions, float3 nodePosition)
        {
            if (restrictions == null || restrictions.Count == 0)
                return;

            const float maxDistanceMeters = 8f;
            var maxDistanceSq = maxDistanceMeters * maxDistanceMeters;
            foreach (var restriction in restrictions)
            {
                if (restriction == null)
                    continue;

                if (restriction.ViaNodeId == 0)
                    continue;

                if (math.distancesq(restriction.ViaPosition.xz, nodePosition.xz) > maxDistanceSq)
                    continue;

                if (restriction.ForbidLeftTurn || restriction.ForbidUTurn)
                {
                    flags.m_Left |= CompositionFlags.Side.ForbidLeftTurn;
                    flags.m_Right |= CompositionFlags.Side.ForbidLeftTurn;
                }

                if (restriction.ForbidRightTurn || restriction.ForbidUTurn)
                {
                    flags.m_Left |= CompositionFlags.Side.ForbidRightTurn;
                    flags.m_Right |= CompositionFlags.Side.ForbidRightTurn;
                }

                if (restriction.ForbidStraight)
                {
                    flags.m_Left |= CompositionFlags.Side.ForbidStraight;
                    flags.m_Right |= CompositionFlags.Side.ForbidStraight;
                }
            }
        }

        internal void ApplyPendingNames()
        {
            if (_nameSystem == null || PendingNames.Count == 0)
                return;

            var appliedCount = 0;
            var errorCount = 0;
            var chunkEntities = new HashSet<Entity>();

            foreach (var (entity, name) in PendingNames)
            {
                var targetEntity = ResolveRoadNameTarget(entity);
                if (!EntityManager.Exists(targetEntity) || string.IsNullOrWhiteSpace(name))
                    continue;

                try
                {
                    _nameSystem.SetCustomName(targetEntity, name);

                    if (EntityManager.HasComponent<CustomName>(targetEntity))
                    {
                        EntityManager.SetComponentData(targetEntity, new CustomName());
                    }
                    else
                    {
                        EntityManager.AddComponentData(targetEntity, new CustomName());
                    }

                    if (EntityManager.HasComponent<Updated>(targetEntity))
                        EntityManager.SetComponentData(targetEntity, new Updated());
                    else
                        EntityManager.AddComponentData(targetEntity, new Updated());

                    chunkEntities.Add(targetEntity);
                    appliedCount++;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    if (errorCount <= 5)
                        Mod.log.Warn($"SetCustomName failed for entity {targetEntity}: {ex.Message}");
                }
            }

            MarkChunksDirty(chunkEntities);

            Mod.log.Info(
                $"Applied {appliedCount} road names to aggregate entities (SetCustomName + CustomName + Updated + BatchesUpdated). " +
                $"{errorCount} errors.");
            PendingNames.Clear();
        }

        private void MarkChunksDirty(HashSet<Entity> entities)
        {
            foreach (var entity in entities)
            {
                if (!EntityManager.Exists(entity))
                    continue;
                if (EntityManager.HasComponent<BatchesUpdated>(entity))
                    EntityManager.SetComponentData(entity, new BatchesUpdated());
                else
                    EntityManager.AddComponentData(entity, new BatchesUpdated());
            }
        }



        private static int SpatialCellKey(float3 position, float cellSize)
        {
            var cx = (int)math.floor(position.x / cellSize);
            var cz = (int)math.floor(position.z / cellSize);
            return (int)math.hash(new int2(cx, cz));
        }

        private void ConsiderQueuedSegmentMatches(
            NativeParallelMultiHashMap<int, int> spatialIndex,
            NativeParallelHashSet<int> matchedSegments,
            float3 edgeStart,
            float3 edgeEnd,
            float3 centerPos,
            float3 edgeMid,
            float3 edgeDir,
            float cellSize,
            ref int bestIndex,
            ref float bestEndpointScore,
            ref float bestMidScore,
            ref int dirMismatchCount)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var neighborPos = centerPos + new float3(dx * cellSize, 0f, dz * cellSize);
                    var neighborKey = SpatialCellKey(neighborPos, cellSize);
                    if (!spatialIndex.TryGetFirstValue(neighborKey, out var segIndex, out var iterator))
                        continue;

                    do
                    {
                        if (matchedSegments.Contains(segIndex))
                            continue;

                var queuedInfo = _queuedSegmentInfos[segIndex];
                        var queuedDir = math.normalizesafe(
                            queuedInfo.EndPos - queuedInfo.StartPos, new float3(0f, 0f, 1f));
                        var dirDot = math.abs(math.dot(edgeDir, queuedDir));
                        if (dirDot < 0.5f)
                            dirMismatchCount++;

                        var sameOrientationScore = math.distancesq(edgeStart, queuedInfo.StartPos) +
                                           math.distancesq(edgeEnd, queuedInfo.EndPos);
                var reversedOrientationScore = math.distancesq(edgeStart, queuedInfo.EndPos) +
                                                math.distancesq(edgeEnd, queuedInfo.StartPos);
                var endpointScore = math.min(sameOrientationScore, reversedOrientationScore);
                var midScore = math.distancesq(queuedInfo.MidPoint, edgeMid);
                var directionPenalty = (1f - math.clamp(dirDot, 0f, 1f)) * 25f;
                var totalScore = endpointScore + midScore * 0.25f + directionPenalty;

                if (totalScore < bestEndpointScore ||
                    (math.abs(totalScore - bestEndpointScore) <= 0.0001f && midScore < bestMidScore))
                {
                    bestEndpointScore = totalScore;
                    bestMidScore = midScore;
                    bestIndex = segIndex;
                }
                    }
                    while (spatialIndex.TryGetNextValue(out segIndex, ref iterator));
                }
            }
        }

        private void LogUnmatchedRoadEdge(
            NativeParallelMultiHashMap<int, int> spatialIndex,
            Entity edgeEntity,
            float3 startPos,
            float3 endPos,
            float3 edgeMid,
            float3 edgeDir)
        {
            var candidates = new List<string>();
            var cellSize = 5f;
            for (var dx = -1; dx <= 1 && candidates.Count < 5; dx++)
            {
                for (var dz = -1; dz <= 1 && candidates.Count < 5; dz++)
                {
                    var neighborKey = SpatialCellKey(edgeMid + new float3(dx * cellSize, 0f, dz * cellSize), cellSize);
                    if (!TryGetQueuedSegmentCandidates(spatialIndex, neighborKey, startPos, endPos, edgeMid, edgeDir, candidates))
                        continue;
                }
            }

            if (candidates.Count == 0)
                AddGlobalQueuedCandidateSummary(startPos, endPos, edgeMid, edgeDir, candidates);

            var candidateSummary = candidates.Count > 0 ? string.Join("; ", candidates) : "(none)";
            Mod.log.Info(
                $"Unmatched edge {edgeEntity.Index}:{edgeEntity.Version} start={FormatPoint(startPos)} end={FormatPoint(endPos)} mid={FormatPoint(edgeMid)} dir={FormatPoint(edgeDir)} candidates={candidateSummary}");
        }

        private bool TryFindBestQueuedSegmentGlobally(
            float3 edgeStart,
            float3 edgeEnd,
            float3 edgeMid,
            float3 edgeDir,
            NativeParallelHashSet<int> matchedSegments,
            out int bestIndex,
            out float bestScore)
        {
            bestIndex = -1;
            bestScore = float.MaxValue;

            for (var i = 0; i < _queuedSegmentInfos.Count; i++)
            {
                if (matchedSegments.Contains(i))
                    continue;

                var score = ScoreQueuedSegment(edgeStart, edgeEnd, edgeMid, edgeDir, _queuedSegmentInfos[i], out _);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            return bestIndex >= 0;
        }

        private bool TryGetQueuedSegmentCandidates(
            NativeParallelMultiHashMap<int, int> spatialIndex,
            int neighborKey,
            float3 edgeStart,
            float3 edgeEnd,
            float3 edgeMid,
            float3 edgeDir,
            List<string> candidates)
        {
            if (!spatialIndex.TryGetFirstValue(neighborKey, out var segIndex, out var iterator))
                return false;

            var foundAny = false;
            do
            {
                var queuedInfo = _queuedSegmentInfos[segIndex];
                var totalScore = ScoreQueuedSegment(edgeStart, edgeEnd, edgeMid, edgeDir, queuedInfo, out var midScore);
                var queuedDir = math.normalizesafe(queuedInfo.EndPos - queuedInfo.StartPos, new float3(0f, 0f, 1f));
                var dirDot = math.abs(math.dot(edgeDir, queuedDir));
                var endpointScore = math.min(
                    math.distancesq(edgeStart, queuedInfo.StartPos) + math.distancesq(edgeEnd, queuedInfo.EndPos),
                    math.distancesq(edgeStart, queuedInfo.EndPos) + math.distancesq(edgeEnd, queuedInfo.StartPos));

                candidates.Add($"{queuedInfo.Name ?? string.Empty}@{totalScore:F1}/{endpointScore:F1}/{midScore:F1}/d{dirDot:F2}/l{queuedInfo.Lanes}");
                foundAny = true;
            }
            while (spatialIndex.TryGetNextValue(out segIndex, ref iterator) && candidates.Count < 5);

            return foundAny;
        }

        private void AddGlobalQueuedCandidateSummary(
            float3 edgeStart,
            float3 edgeEnd,
            float3 edgeMid,
            float3 edgeDir,
            List<string> candidates)
        {
            var scored = new List<(float Score, string Text)>();
            foreach (var queuedInfo in _queuedSegmentInfos)
            {
                var score = ScoreQueuedSegment(edgeStart, edgeEnd, edgeMid, edgeDir, queuedInfo, out var midScore);
                var queuedDir = math.normalizesafe(queuedInfo.EndPos - queuedInfo.StartPos, new float3(0f, 0f, 1f));
                var dirDot = math.abs(math.dot(edgeDir, queuedDir));
                var endpointScore = math.min(
                    math.distancesq(edgeStart, queuedInfo.StartPos) + math.distancesq(edgeEnd, queuedInfo.EndPos),
                    math.distancesq(edgeStart, queuedInfo.EndPos) + math.distancesq(edgeEnd, queuedInfo.StartPos));
                scored.Add((score, $"{queuedInfo.Name ?? string.Empty}@{score:F1}/{endpointScore:F1}/{midScore:F1}/d{dirDot:F2}/l{queuedInfo.Lanes}"));
            }

            foreach (var item in scored.OrderBy(s => s.Score).Take(5))
                candidates.Add(item.Text);
        }

        private static float ScoreQueuedSegment(
            float3 edgeStart,
            float3 edgeEnd,
            float3 edgeMid,
            float3 edgeDir,
            QueuedSegmentInfo queuedInfo,
            out float midScore)
        {
            var queuedDir = math.normalizesafe(queuedInfo.EndPos - queuedInfo.StartPos, new float3(0f, 0f, 1f));
            var dirDot = math.abs(math.dot(edgeDir, queuedDir));
            var sameOrientationScore = math.distancesq(edgeStart, queuedInfo.StartPos) +
                                       math.distancesq(edgeEnd, queuedInfo.EndPos);
            var reversedOrientationScore = math.distancesq(edgeStart, queuedInfo.EndPos) +
                                            math.distancesq(edgeEnd, queuedInfo.StartPos);
            var endpointScore = math.min(sameOrientationScore, reversedOrientationScore);
            midScore = math.distancesq(queuedInfo.MidPoint, edgeMid);
            var directionPenalty = (1f - math.clamp(dirDot, 0f, 1f)) * 25f;
            return endpointScore + midScore * 0.25f + directionPenalty;
        }

        private Entity ResolveRoadNameTarget(Entity entity)
        {
            if (!EntityManager.Exists(entity))
                return entity;

            if (EntityManager.HasComponent<Aggregate>(entity))
                return entity;

            if (EntityManager.HasComponent<Aggregated>(entity))
            {
                var aggregated = EntityManager.GetComponentData<Aggregated>(entity);
                if (EntityManager.Exists(aggregated.m_Aggregate))
                    return aggregated.m_Aggregate;
            }

            return entity;
        }

#if DEBUG
        internal string TryValidateNames()
        {
            if (!_roadNamesApplied || _nameSystem == null)
                return L("OsmImporter.Status.ValidationNotApplied", "Validation: names not yet applied.");

            var edgeArray = GetCurrentNetEdgeArray(Allocator.Temp);
            var newEdges = new List<Entity>();
            foreach (var entity in edgeArray)
            {
                if (!_baselineSegments.Contains(entity))
                    newEdges.Add(entity);
            }
            edgeArray.Dispose();

            var totalNewEdges = newEdges.Count;
            var namedCount = 0;
            var unnamedCount = 0;
            var samples = new List<string>();
            var seenTargets = new HashSet<Entity>();

            foreach (var entity in newEdges)
            {
                var targetEntity = ResolveRoadNameTarget(entity);
                if (!seenTargets.Add(targetEntity))
                    continue;

                if (_nameSystem.TryGetCustomName(targetEntity, out var name) && !string.IsNullOrWhiteSpace(name))
                {
                    namedCount++;
                    if (samples.Count < 20)
                        samples.Add(name);
                }
                else
                {
                    unnamedCount++;
                }
            }

            var unique = samples.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sampleStr = unique.Count > 0
                ? string.Join(", ", unique.Take(10).Select(n => $"\"{EscapeJson(n)}\""))
                : "(none)";

            var queuedTotal = _queuedSegmentInfos.Count;
            var queuedNonEmpty = _queuedSegmentInfos.Count(n => !string.IsNullOrWhiteSpace(n.Name));
            var baselineEdgeCount = _baselineSegments.Count;

            var result = LF("OsmImporter.Status.ValidationSummary", "Validation: {0} new edges ({1} baseline), {2} named, {3} unnamed. Queued: {4} total, {5} non-empty. Samples: {6}", totalNewEdges, baselineEdgeCount, namedCount, unnamedCount, queuedTotal, queuedNonEmpty, sampleStr);
            Mod.log.Info(result);
            return result;
        }
#endif

        private bool ShouldGenerateWay(OsmWay way)
        {
            return _activeGenerateOptions == null ||
                   _activeGenerateOptions.HighwayTypes.Count == 0 ||
                   _activeGenerateOptions.HighwayTypes.Contains(way.Highway);
        }

        private Entity GetPrefabForHighwayType(OsmWay way)
        {
            if (way == null || string.IsNullOrWhiteSpace(way.Highway))
                return _prefabEntity;

            if (way.IsRoundabout)
                return GetPrefabForHighwayType(way.Highway, way.Lanes, way.Sidewalk);

            if (HasManualPrefabRulesForWay(way))
            {
                if (TryGetManualPrefabForWay(way, out var manualPrefab))
                    return manualPrefab;

                Mod.log.Warn($"Manual prefab rules exist for highway type '{way.Highway}' but no exact prefab name matched. Using the default fallback prefab instead of heuristic road selection.");
                return _prefabEntity;
            }

            return GetPrefabForHighwayType(way.Highway, way.Lanes, way.Sidewalk);
        }

        private Entity GetPrefabForHighwayType(string highwayType, int lanes, OsmSidewalkType sidewalk)
        {
            if (string.IsNullOrWhiteSpace(highwayType))
                return _prefabEntity;

            var requiresSidewalk = sidewalk != OsmSidewalkType.None;
            var cacheKey = GetRoadPrefabCacheKey(highwayType, lanes, sidewalk);

            if (IsTrackLikeHighwayType(highwayType))
            {
                var trackMatch = TryGetTrackPrefabForHighwayType(highwayType, lanes);
                if (trackMatch != Entity.Null)
                    return trackMatch;

                Mod.log.Info(
                    $"No track-specific prefab match for highway type '{highwayType}' with {Math.Max(lanes, 0)} lanes. " +
                    "Falling back to road-prefab selection.");
            }

            if (IsPathLikeHighwayType(highwayType))
            {
                var pathMatch = TryGetPathPrefabForHighwayType(highwayType, lanes);
                if (pathMatch != Entity.Null)
                {
                    return pathMatch;
                }

                Mod.log.Info(
                    $"No path-specific prefab match for highway type '{highwayType}' with {Math.Max(lanes, 0)} lanes. " +
                    "Falling back to road-prefab selection.");
            }

            if (lanes <= 0)
            {
                var defaultLaneCount = GetDefaultLaneCountForHighwayType(highwayType);
                if (defaultLaneCount > 0)
                {
                    var defaultLaneKey = GetRoadPrefabCacheKey(highwayType, defaultLaneCount, sidewalk);
                    if (_laneSpecificPrefabEntities.TryGetValue(defaultLaneKey, out var cachedDefaultLane))
                        return cachedDefaultLane;

                    var defaultLaneMatch = TryFindPrefabForHighwayType(highwayType, defaultLaneCount, requiresSidewalk);
                    if (defaultLaneMatch != Entity.Null)
                    {
                        _laneSpecificPrefabEntities[defaultLaneKey] = defaultLaneMatch;
                        Mod.log.Info(
                            $"Selected default lane prefab '{DescribePrefab(defaultLaneMatch)}' for highway type '{highwayType}' with {defaultLaneCount} lanes.");
                        return defaultLaneMatch;
                    }
                }
            }

            if (lanes > 0)
            {
                var laneKey = GetRoadPrefabCacheKey(highwayType, lanes, sidewalk);
                if (_laneSpecificPrefabEntities.TryGetValue(laneKey, out var laneCached))
                    return laneCached;

                var laneMatch = TryFindPrefabForHighwayType(highwayType, lanes, requiresSidewalk);
                if (laneMatch != Entity.Null)
                {
                    _laneSpecificPrefabEntities[laneKey] = laneMatch;
                    Mod.log.Info(
                        $"Selected prefab '{DescribePrefab(laneMatch)}' for highway type '{highwayType}' " +
                        $"with {lanes} lanes.");
                    return laneMatch;
                }

                Mod.log.Info(
                    $"No lane-specific prefab match for highway type '{highwayType}' with {lanes} lanes. " +
                    "Falling back to type-only selection.");
            }

            if (_highwayPrefabEntities.TryGetValue(cacheKey, out var cached))
                return cached;

            var typeMatch = TryFindPrefabForHighwayType(highwayType, 0, requiresSidewalk);
            if (typeMatch != Entity.Null)
            {
                _highwayPrefabEntities[cacheKey] = typeMatch;
                Mod.log.Info($"Selected road prefab '{DescribePrefab(typeMatch)}' for highway type '{highwayType}'.");
                return typeMatch;
            }

            _highwayPrefabEntities[cacheKey] = _prefabEntity;
            _highwayPrefabNames[cacheKey] = _prefabName;
            Mod.log.Warn($"No dedicated prefab found for highway type '{highwayType}'. Falling back to '{_prefabName}'.");
            return _prefabEntity;
        }

        private Entity TryFindPrefabForHighwayType(string highwayType, int lanes, bool requiresSidewalk)
            => TryFindPrefabForHighwayType(highwayType, lanes, requiresSidewalk, IsSuitableRoadPrefabName, GetPreferredPrefabNames(highwayType, lanes, requiresSidewalk), GetRoadPrefabCandidates().ToList());

        private Entity TryGetPathPrefabForHighwayType(string highwayType, int lanes)
        {
            var preferredNames = GetPreferredPathPrefabNames(highwayType);
            var candidates = GetPathPrefabCandidates().ToList();

            if (lanes <= 0)
            {
                var defaultLaneCount = GetDefaultLaneCountForHighwayType(highwayType);
                if (defaultLaneCount > 0)
                {
                    var defaultLaneKey = $"path:{highwayType}:{defaultLaneCount}";
                    if (_laneSpecificPrefabEntities.TryGetValue(defaultLaneKey, out var cachedDefaultLane))
                        return cachedDefaultLane;

                    var defaultLaneMatch = TryFindPrefabForHighwayType(highwayType, defaultLaneCount, false, IsSuitablePathPrefabName, preferredNames, candidates);
                    if (defaultLaneMatch != Entity.Null)
                    {
                        _laneSpecificPrefabEntities[defaultLaneKey] = defaultLaneMatch;
                        Mod.log.Info(
                            $"Selected default lane path prefab '{DescribePrefab(defaultLaneMatch)}' for highway type '{highwayType}' with {defaultLaneCount} lanes.");
                        return defaultLaneMatch;
                    }
                }
            }

            if (lanes > 0)
            {
                var laneKey = $"path:{highwayType}:{lanes}";
                if (_laneSpecificPrefabEntities.TryGetValue(laneKey, out var laneCached))
                    return laneCached;

                var laneMatch = TryFindPrefabForHighwayType(highwayType, lanes, false, IsSuitablePathPrefabName, preferredNames, candidates);
                if (laneMatch != Entity.Null)
                {
                    _laneSpecificPrefabEntities[laneKey] = laneMatch;
                    Mod.log.Info(
                        $"Selected path prefab '{DescribePrefab(laneMatch)}' for highway type '{highwayType}' " +
                        $"with {lanes} lanes.");
                    return laneMatch;
                }

                Mod.log.Info(
                    $"No lane-specific path prefab match for highway type '{highwayType}' with {lanes} lanes. " +
                    "Falling back to type-only path selection.");
            }

            var typeKey = $"path:{highwayType}";
            if (_laneSpecificPrefabEntities.TryGetValue(typeKey, out var cachedType))
                return cachedType;

            var typeMatch = TryFindPrefabForHighwayType(highwayType, 0, false, IsSuitablePathPrefabName, preferredNames, candidates);
            if (typeMatch != Entity.Null)
            {
                _laneSpecificPrefabEntities[typeKey] = typeMatch;
                Mod.log.Info(
                    $"Selected path prefab '{DescribePrefab(typeMatch)}' for highway type '{highwayType}'.");
                return typeMatch;
            }

            return Entity.Null;
        }

        private Entity TryGetTrackPrefabForHighwayType(string highwayType, int lanes)
        {
            var preferredNames = GetPreferredPrefabNames(highwayType, lanes, false);
            var candidates = GetTrackPrefabCandidates().ToList();

            if (lanes <= 0)
            {
                var defaultLaneCount = GetDefaultLaneCountForHighwayType(highwayType);
                if (defaultLaneCount > 0)
                {
                    var defaultLaneKey = $"track:{highwayType}:{defaultLaneCount}";
                    if (_laneSpecificPrefabEntities.TryGetValue(defaultLaneKey, out var cachedDefaultLane))
                        return cachedDefaultLane;

                    var defaultLaneMatch = TryFindPrefabForHighwayType(highwayType, defaultLaneCount, false, IsSuitableRoadPrefabName, preferredNames, candidates);
                    if (defaultLaneMatch != Entity.Null)
                    {
                        _laneSpecificPrefabEntities[defaultLaneKey] = defaultLaneMatch;
                        Mod.log.Info(
                            $"Selected default lane track prefab '{DescribePrefab(defaultLaneMatch)}' for highway type '{highwayType}' with {defaultLaneCount} lanes.");
                        return defaultLaneMatch;
                    }
                }
            }

            if (lanes > 0)
            {
                var laneKey = $"track:{highwayType}:{lanes}";
                if (_laneSpecificPrefabEntities.TryGetValue(laneKey, out var laneCached))
                    return laneCached;

                var laneMatch = TryFindPrefabForHighwayType(highwayType, lanes, false, IsSuitableRoadPrefabName, preferredNames, candidates);
                if (laneMatch != Entity.Null)
                {
                    _laneSpecificPrefabEntities[laneKey] = laneMatch;
                    Mod.log.Info(
                        $"Selected track prefab '{DescribePrefab(laneMatch)}' for highway type '{highwayType}' with {lanes} lanes.");
                    return laneMatch;
                }

                Mod.log.Info(
                    $"No lane-specific track prefab match for highway type '{highwayType}' with {lanes} lanes. " +
                    "Falling back to type-only track selection.");
            }

            var typeKey = $"track:{highwayType}";
            if (_laneSpecificPrefabEntities.TryGetValue(typeKey, out var cachedType))
                return cachedType;

            var typeMatch = TryFindPrefabForHighwayType(highwayType, 0, false, IsSuitableRoadPrefabName, preferredNames, candidates);
            if (typeMatch != Entity.Null)
            {
                _laneSpecificPrefabEntities[typeKey] = typeMatch;
                Mod.log.Info($"Selected track prefab '{DescribePrefab(typeMatch)}' for highway type '{highwayType}'.");
                return typeMatch;
            }

            return Entity.Null;
        }

        private Entity TryGetRoundaboutPrefabForWay(OsmWay way)
        {
            return Entity.Null;
        }

        private bool TryGetManualRoundaboutPrefabForWay(OsmWay way, out Entity prefabEntity)
        {
            prefabEntity = Entity.Null;

            EnsurePrefabRulesLoaded();
            if (way == null || DefaultPrefabRulesByRoadType == null)
                return false;

            if (!DefaultPrefabRulesByRoadType.TryGetValue("roundabout", out var rules) || rules == null || rules.Count == 0)
                return false;

            var candidateSource = GetAnyRoundaboutPrefabCandidates().ToList();
            var matchedRule = rules.FirstOrDefault(rule => rule != null && rule.Matches(way, _activeGenerateOptions));
            if (matchedRule == null)
                matchedRule = rules.FirstOrDefault(rule => rule != null && !string.IsNullOrWhiteSpace(rule.PrefabName));

            if (matchedRule == null || string.IsNullOrWhiteSpace(matchedRule.PrefabName))
                return false;

            if (TryResolveRulePrefab(matchedRule.PrefabName, candidateSource, out prefabEntity) ||
                TryResolvePrefabByNameAcrossAllPrefabs(matchedRule.PrefabName, IsSuitableRoundaboutPrefabName, out prefabEntity))
            {
                if (!HasRoundaboutComposition(prefabEntity))
                {
                    Mod.log.Warn($"Resolved roundabout prefab '{DescribePrefab(prefabEntity)}' does not expose roundabout composition; rejecting.");
                    prefabEntity = Entity.Null;
                    return false;
                }

                Mod.log.Info($"Selected roundabout prefab '{DescribePrefab(prefabEntity)}' from prefab-mappings.json for highway type '{way.Highway}'.");
                return true;
            }

            if (candidateSource.Count > 0 && _prefabSystem.TryGetEntity(candidateSource[0], out prefabEntity))
            {
                if (!HasRoundaboutComposition(prefabEntity))
                {
                    Mod.log.Warn($"Roundabout mapping '{matchedRule.PrefabName}' resolved to non-roundabout prefab '{DescribePrefab(prefabEntity)}'; rejecting.");
                    prefabEntity = Entity.Null;
                    return false;
                }

                Mod.log.Warn($"Roundabout mapping '{matchedRule.PrefabName}' could not be resolved; using first available roundabout prefab '{DescribePrefab(prefabEntity)}'.");
                return true;
            }

            LogRoundaboutPrefabLookupDiagnostics(way, matchedRule.PrefabName, candidateSource.Count);

            return false;
        }

        private Entity TryFindRoundaboutPrefabByNameFallback(OsmWay way)
        {
            if (_prefabSystem == null)
                return Entity.Null;

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetObjectData>());
            using var prefabs = query.ToEntityArray(Allocator.Temp);

            var matches = prefabs
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => new { Entity = e, Name = GetPrefabName(e), PrefabBase = GetPrefabBase(e) })
                .Where(x => IsSuitableRoundaboutPrefabName(x.Name) || HasRoundaboutComposition(x.Entity))
                .ToList();

            if (matches.Count == 0)
            {
                Mod.log.Warn($"Roundabout prefab fallback search found no usable candidates for highway type '{way.Highway}'.");
                LogRoundaboutPrefabLookupDiagnostics(way, string.Empty, 0);
                return Entity.Null;
            }

            Mod.log.Info($"Roundabout prefab fallback candidates: {string.Join(", ", matches.Take(12).Select(x => x.Name))}");

            var preferredNames = GetPreferredRoundaboutPrefabNames(way).ToArray();
            foreach (var preferredName in preferredNames)
            {
                var exact = matches.FirstOrDefault(x => x.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    Mod.log.Info($"Selected roundabout prefab by exact fallback name '{exact.Name}' for highway type '{way.Highway}'.");
                    return exact.Entity;
                }

                var fuzzy = matches.FirstOrDefault(x => PrefabNameMatches(x.Name, preferredName));
                if (fuzzy != null)
                {
                    Mod.log.Info($"Selected roundabout prefab by fuzzy fallback name '{fuzzy.Name}' for highway type '{way.Highway}'.");
                    return fuzzy.Entity;
                }
            }

            var first = matches[0];
            Mod.log.Info($"Selected first fallback roundabout prefab '{first.Name}' for highway type '{way.Highway}'.");
            return first.Entity;
        }

        private void LogRoundaboutPrefabLookupDiagnostics(OsmWay way, string requestedPrefabName, int candidateCount)
        {
            if (_loggedRoundaboutPrefabDiagnostics || _prefabSystem == null)
                return;

            _loggedRoundaboutPrefabDiagnostics = true;

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetObjectData>());
            using var prefabs = query.ToEntityArray(Allocator.Temp);

            var matches = prefabs
                .Select(e => new
                {
                    Entity = e,
                    Name = GetPrefabName(e),
                    PrefabBaseName = GetPrefabBase(e)?.name ?? string.Empty,
                    IsDlcGated = IsDlcGatedPrefab(e),
                    HasNetData = EntityManager.HasComponent<NetData>(e),
                    HasNetObjectData = EntityManager.HasComponent<NetObjectData>(e),
                    HasRoundabout = HasRoundaboutComposition(e),
                })
                .Where(x =>
                    (!string.IsNullOrWhiteSpace(x.Name) && x.Name.IndexOf("roundabout", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(x.PrefabBaseName) && x.PrefabBaseName.IndexOf("roundabout", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    x.HasRoundabout ||
                    x.HasNetObjectData)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Mod.log.Info(
                $"Roundabout prefab diagnostics for highway type '{way?.Highway ?? string.Empty}': requested='{requestedPrefabName}', usableCandidates={candidateCount}, matches={matches.Count}");

            foreach (var match in matches.Take(40))
            {
                using var componentTypes = EntityManager.GetComponentTypes(match.Entity, Allocator.Temp);
                var componentSummary = componentTypes.Length == 0
                    ? string.Empty
                    : string.Join(", ", componentTypes.Select(t => t.ToString()));

                Mod.log.Info(
                    $"  roundabout-candidate entity={match.Entity.Index}:{match.Entity.Version} name='{match.Name}' prefabBase='{match.PrefabBaseName}' dlcGated={match.IsDlcGated} netData={match.HasNetData} netObjectData={match.HasNetObjectData} roundaboutComponent={match.HasRoundabout} components=[{componentSummary}]");
            }
        }

        private Entity TryFindPrefabForHighwayType(
            string highwayType,
            int lanes,
            bool requiresSidewalk,
            Func<string, string, bool, bool> isSuitablePrefabName,
            IEnumerable<string> preferredNames,
            List<NetPrefab> candidates)
        {
            foreach (var preferredName in preferredNames)
            {
                    var match = candidates.FirstOrDefault(p =>
                        isSuitablePrefabName(p.name, highwayType, requiresSidewalk) &&
                        p.name.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (lanes <= 0 || ParsePrefabLaneCount(p.name) == lanes || ParsePrefabLaneCount(p.name) == 0));
                if (match != null && _prefabSystem.TryGetEntity(match, out var entity))
                {
                    if (lanes <= 0)
                        _highwayPrefabNames[highwayType] = match.name;
                    return entity;
                }
            }

            return Entity.Null;
        }

        private IEnumerable<PrefabBase> GetRoundaboutPrefabCandidates()
        {
            if (_prefabSystem == null)
                return Enumerable.Empty<PrefabBase>();

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetObjectData>());

            using var prefabs = query.ToEntityArray(Allocator.Temp);
            return prefabs
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => GetPrefabBase(e))
                .Where(p => p != null && IsSuitableRoundaboutPrefabName(p.name))
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<PrefabBase> GetAllRoundaboutPrefabCandidates()
        {
            if (_prefabSystem == null)
                return Enumerable.Empty<PrefabBase>();

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetObjectData>());
            using var prefabs = query.ToEntityArray(Allocator.Temp);
            return prefabs
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => GetPrefabBase(e))
                .Where(p => p != null)
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<PrefabBase> GetAnyRoundaboutPrefabCandidates()
        {
            return GetAllRoundaboutPrefabCandidates()
                .Where(p => p != null && IsSuitableRoundaboutPrefabName(p.name))
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool TryGetManualPrefabForWay(OsmWay way, out Entity prefabEntity)
        {
            prefabEntity = Entity.Null;

            EnsurePrefabRulesLoaded();
            var rulesByType = DefaultPrefabRulesByRoadType;
            if (way == null || rulesByType == null || rulesByType.Count == 0)
                return false;

            var roadType = way.Highway ?? string.Empty;
            if (!rulesByType.TryGetValue(roadType, out var rules) || rules == null || rules.Count == 0)
                return false;

            IEnumerable<PrefabBase> candidateSource;
            if (IsTrackLikeHighwayType(way.Highway))
                candidateSource = GetTrackPrefabCandidates();
            else if (IsPathLikeHighwayType(way.Highway))
                candidateSource = GetPathPrefabCandidates();
            else
                candidateSource = GetRoadPrefabCandidates();

            var roadCandidates = candidateSource.Where(p => p != null).ToList();

            PrefabSelectionRule bestRule = null;
            var bestRuleScore = -1;
            foreach (var rule in rules)
            {
                if (!rule.Matches(way, _activeGenerateOptions))
                    continue;

                var score = rule.SpecificityScore();
                if (score > bestRuleScore)
                {
                    bestRule = rule;
                    bestRuleScore = score;
                }
            }

            if (bestRule != null)
            {
                if (TryResolveRulePrefab(bestRule.PrefabName, roadCandidates, out prefabEntity))
                {
                    Mod.log.Info($"Selected manual prefab '{DescribePrefab(prefabEntity)}' for road type '{roadType}'.");
                    return true;
                }

                Mod.log.Warn($"Manual prefab rule for road type '{roadType}' matched but prefab '{bestRule.PrefabName}' was not found among usable candidates.");
            }

            return false;
        }

        private bool HasManualPrefabRulesForWay(OsmWay way)
        {
            EnsurePrefabRulesLoaded();
            if (way == null)
                return false;

            var roadType = way.Highway ?? string.Empty;
            return DefaultPrefabRulesByRoadType.TryGetValue(roadType, out var rules) && rules != null && rules.Count > 0;
        }

        private bool TryResolveRulePrefab(string prefabName, List<NetPrefab> candidates, out Entity prefabEntity)
            => TryResolveRulePrefab(prefabName, candidates?.Cast<PrefabBase>().ToList() ?? new List<PrefabBase>(), out prefabEntity);

        private bool TryResolveRulePrefab(string prefabName, List<PrefabBase> candidates, out Entity prefabEntity)
        {
            prefabEntity = Entity.Null;

            if (string.IsNullOrWhiteSpace(prefabName) || candidates == null || candidates.Count == 0 || _prefabSystem == null)
                return false;

            var match = candidates.FirstOrDefault(p => p != null && p.name.Equals(prefabName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                match = candidates.FirstOrDefault(p => p != null && PrefabNameMatches(p.name, prefabName));

            return match != null && _prefabSystem.TryGetEntity(match, out prefabEntity);
        }

        private bool TryResolvePrefabByNameAcrossAllPrefabs(string prefabName, Func<string, bool> isSuitableName, out Entity prefabEntity)
        {
            prefabEntity = Entity.Null;

            if (string.IsNullOrWhiteSpace(prefabName) || _prefabSystem == null)
                return false;

            using var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>());
            using var prefabs = query.ToEntityArray(Allocator.Temp);

            var match = prefabs
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => new { Entity = e, Name = GetPrefabName(e) })
                .FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.Name) &&
                    isSuitableName(x.Name) &&
                    (x.Name.Equals(prefabName, StringComparison.OrdinalIgnoreCase) || PrefabNameMatches(x.Name, prefabName)));

            if (match == null)
                return false;

            prefabEntity = match.Entity;
            return true;
        }

        private static bool PrefabNameMatches(string prefabName, string expectedName)
        {
            if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(expectedName))
                return false;

            var actual = NormalizePrefabName(prefabName);
            var expected = NormalizePrefabName(expectedName);
            if (actual == expected)
                return true;

            actual = NormalizePrefabName(prefabName, true);
            expected = NormalizePrefabName(expectedName, true);
            if (actual == expected)
                return true;

            return actual.Contains(expected) || expected.Contains(actual);
        }

        private static string NormalizePrefabName(string value, bool stripDigits = false)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value
                .Where(c => char.IsLetterOrDigit(c) && (!stripDigits || !char.IsDigit(c)))
                .Select(char.ToLowerInvariant)
                .ToArray();
            return new string(chars);
        }

        private string GetPrefabName(Entity entity)
        {
            if (entity == Entity.Null)
                return string.Empty;

            var name = EntityManager.GetName(entity);
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            if (_prefabSystem != null)
            {
                if (_prefabSystem.TryGetPrefab<PrefabBase>(entity, out var prefab) && prefab != null)
                    return prefab.name;

                if (_prefabSystem.TryGetPrefab<NetPrefab>(entity, out var netPrefab) && netPrefab != null)
                    return netPrefab.name;
            }

            return string.Empty;
        }

        private PrefabBase GetPrefabBase(Entity entity)
        {
            if (entity == Entity.Null || _prefabSystem == null)
                return null;

            return _prefabSystem.TryGetPrefab<PrefabBase>(entity, out var prefab) ? prefab : null;
        }

        private bool HasRoundaboutComposition(Entity entity)
        {
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<NetObjectData>(entity))
                return false;

            var netObjectData = EntityManager.GetComponentData<NetObjectData>(entity);
            return netObjectData.m_CompositionFlags.m_General.HasFlag(CompositionFlags.General.Roundabout);
        }

        private static PrefabLaneBucket ParseLaneBucket(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "1": return PrefabLaneBucket.One;
                case "2": return PrefabLaneBucket.Two;
                case "3": return PrefabLaneBucket.Three;
                case "4": return PrefabLaneBucket.Four;
                case "5": return PrefabLaneBucket.Five;
                case "6": return PrefabLaneBucket.Six;
                case "7+":
                case "7plus":
                case "7 plus":
                case "7_or_more":
                case "7 or more":
                    return PrefabLaneBucket.SevenPlus;
                case "6+":
                case "6plus":
                case "6 plus":
                case "6_or_more":
                case "6 or more":
                    return PrefabLaneBucket.Six;
                default:
                    return PrefabLaneBucket.Any;
            }
        }

        private static PrefabSidewalkBucket ParseSidewalkBucket(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "none": return PrefabSidewalkBucket.None;
                case "sidewalk":
                case "yes":
                    return PrefabSidewalkBucket.Sidewalk;
                case "wide":
                case "wide sidewalk":
                case "wide_sidewalk":
                    return PrefabSidewalkBucket.WideSidewalk;
                default:
                    return PrefabSidewalkBucket.Any;
            }
        }

        private static PrefabParkingSideBucket ParseParkingSideBucket(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "none":
                    return PrefabParkingSideBucket.None;
                case "left":
                    return PrefabParkingSideBucket.Left;
                case "right":
                    return PrefabParkingSideBucket.Right;
                case "both":
                    return PrefabParkingSideBucket.Both;
                default:
                    return PrefabParkingSideBucket.Any;
            }
        }

        private static PrefabParkingStyleBucket ParseParkingStyleBucket(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "none":
                    return PrefabParkingStyleBucket.None;
                case "parallel":
                    return PrefabParkingStyleBucket.Parallel;
                case "diagonal":
                    return PrefabParkingStyleBucket.Diagonal;
                case "perpendicular":
                    return PrefabParkingStyleBucket.Perpendicular;
                default:
                    return PrefabParkingStyleBucket.Any;
            }
        }

        private static TriState ParseTriState(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "yes":
                case "true":
                case "1":
                    return TriState.Yes;
                case "no":
                case "false":
                case "0":
                    return TriState.No;
                default:
                    return TriState.Any;
            }
        }

        internal enum TriState
        {
            Any = 0,
            No = 1,
            Yes = 2,
        }

        internal enum OneWayBucket
        {
            Any = 0,
            No = 1,
            AnyOneWay = 2,
            Forward = 3,
            Backward = 4,
        }

        internal enum PrefabLaneBucket
        {
            Any = 0,
            One = 1,
            Two = 2,
            Three = 3,
            Four = 4,
            Five = 5,
            Six = 6,
            SevenPlus = 7,
        }

        internal enum PrefabSidewalkBucket
        {
            Any = -1,
            None = 0,
            Sidewalk = 1,
            WideSidewalk = 2,
        }

        internal enum PrefabParkingSideBucket
        {
            Any = 0,
            None = 1,
            Left = 2,
            Right = 3,
            Both = 4,
        }

        internal enum PrefabParkingStyleBucket
        {
            Any = 0,
            None = 1,
            Parallel = 2,
            Diagonal = 3,
            Perpendicular = 4,
        }

        internal sealed class PrefabSelectionRule
        {
            public PrefabLaneBucket LaneBucket { get; set; }

            public PrefabLaneBucket ForwardLaneBucket { get; set; }

            public PrefabLaneBucket BackwardLaneBucket { get; set; }

            public PrefabLaneBucket BothWaysLaneBucket { get; set; }

            public PrefabSidewalkBucket Sidewalk { get; set; }

            public PrefabParkingSideBucket ParkingSide { get; set; }

            public PrefabParkingStyleBucket ParkingStyle { get; set; }

            public TriState Bridge { get; set; }

            public TriState Tunnel { get; set; }

            public OneWayBucket OneWay { get; set; }

            public TriState BikeLanes { get; set; }

            public TriState TurnRestrictions { get; set; }

            public TriState Crosswalks { get; set; }

            public TriState StopSigns { get; set; }

            public TriState TrafficLights { get; set; }

            public TriState RestrictedLanes { get; set; }

            public string PrefabName { get; set; } = string.Empty;

            public bool Matches(OsmWay way, OsmGenerateOptions options)
            {
                if (way == null)
                    return false;

                return MatchesLane(way.Lanes) &&
                       MatchesLaneBucket(ForwardLaneBucket, way.LanesForward) &&
                       MatchesLaneBucket(BackwardLaneBucket, way.LanesBackward) &&
                       MatchesLaneBucket(BothWaysLaneBucket, way.LanesBothWays) &&
                       MatchesSidewalk(way.Sidewalk) &&
                       MatchesParkingSide(way.ParkingSide) &&
                       MatchesParkingStyle(way.ParkingStyle) &&
                       MatchesTriState(Bridge, way.IsBridge) &&
                       MatchesTriState(Tunnel, way.IsTunnel) &&
                       MatchesOneWay(OneWay, way.OneWayDirection) &&
                       MatchesTriState(BikeLanes, (options == null || options.GenerateBikeLanes) && way.HasBikeLanes) &&
                        MatchesTriState(TurnRestrictions, (options == null || options.GenerateTurnRestrictions) && way.HasTurnRestrictions) &&
                       MatchesTriState(Crosswalks, (options == null || options.GenerateCrosswalks) && way.Crosswalks.Count > 0) &&
                       MatchesTriState(StopSigns, (options == null || options.GenerateStopSigns) && way.StopSigns.Count > 0) &&
                       MatchesTriState(TrafficLights, (options == null || options.GenerateTrafficLights) && way.TrafficLights.Count > 0) &&
                       MatchesTriState(RestrictedLanes, (options == null || options.GenerateRestrictedLanes) && way.HasRestrictedLanes);
            }

            private bool MatchesLane(int lanes)
            {
                return LaneBucket == PrefabLaneBucket.Any ||
                        (LaneBucket == PrefabLaneBucket.Six && lanes == 6) ||
                        (LaneBucket == PrefabLaneBucket.SevenPlus && lanes >= 7) ||
                        (LaneBucket == PrefabLaneBucket.One && lanes == 1) ||
                        (LaneBucket == PrefabLaneBucket.Two && lanes == 2) ||
                        (LaneBucket == PrefabLaneBucket.Three && lanes == 3) ||
                        (LaneBucket == PrefabLaneBucket.Four && lanes == 4) ||
                        (LaneBucket == PrefabLaneBucket.Five && lanes == 5);
            }

            private static bool MatchesLaneBucket(PrefabLaneBucket expected, int lanes)
            {
                return expected == PrefabLaneBucket.Any ||
                       (expected == PrefabLaneBucket.Six && lanes == 6) ||
                       (expected == PrefabLaneBucket.SevenPlus && lanes >= 7) ||
                       (expected == PrefabLaneBucket.One && lanes == 1) ||
                       (expected == PrefabLaneBucket.Two && lanes == 2) ||
                       (expected == PrefabLaneBucket.Three && lanes == 3) ||
                       (expected == PrefabLaneBucket.Four && lanes == 4) ||
                       (expected == PrefabLaneBucket.Five && lanes == 5);
            }

            public int SpecificityScore()
            {
                var score = 0;
                if (LaneBucket != PrefabLaneBucket.Any) score++;
                if (ForwardLaneBucket != PrefabLaneBucket.Any) score++;
                if (BackwardLaneBucket != PrefabLaneBucket.Any) score++;
                if (BothWaysLaneBucket != PrefabLaneBucket.Any) score++;
                if (Sidewalk != PrefabSidewalkBucket.Any) score++;
                if (ParkingSide != PrefabParkingSideBucket.Any) score++;
                if (ParkingStyle != PrefabParkingStyleBucket.Any) score++;
                if (Bridge != TriState.Any) score++;
                if (Tunnel != TriState.Any) score++;
                if (OneWay != OneWayBucket.Any) score++;
                if (BikeLanes != TriState.Any) score++;
                if (TurnRestrictions != TriState.Any) score++;
                if (Crosswalks != TriState.Any) score++;
                if (StopSigns != TriState.Any) score++;
                if (TrafficLights != TriState.Any) score++;
                if (RestrictedLanes != TriState.Any) score++;
                return score;
            }

            private bool MatchesSidewalk(OsmSidewalkType sidewalk)
            {
                return Sidewalk == PrefabSidewalkBucket.Any || (int)Sidewalk == (int)sidewalk;
            }

            private bool MatchesParkingSide(OsmParkingSideType parkingSide)
            {
                return ParkingSide == PrefabParkingSideBucket.Any || (int)ParkingSide == (int)parkingSide;
            }

            private bool MatchesParkingStyle(OsmParkingStyleType parkingStyle)
            {
                return ParkingStyle == PrefabParkingStyleBucket.Any || (int)ParkingStyle == (int)parkingStyle;
            }

            private static bool MatchesTriState(TriState expected, bool actual)
            {
                return expected == TriState.Any || (expected == TriState.Yes && actual) || (expected == TriState.No && !actual);
            }

            private static bool MatchesOneWay(OneWayBucket expected, int direction)
            {
                return expected == OneWayBucket.Any ||
                       (expected == OneWayBucket.No && direction == 0) ||
                       (expected == OneWayBucket.AnyOneWay && direction != 0) ||
                       (expected == OneWayBucket.Forward && direction > 0) ||
                       (expected == OneWayBucket.Backward && direction < 0);
            }
        }

        private static OneWayBucket ParseOneWayBucket(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "yes":
                case "true":
                case "1":
                case "any-oneway":
                case "oneway":
                    return OneWayBucket.AnyOneWay;
                case "no":
                case "false":
                case "0":
                case "none":
                    return OneWayBucket.No;
                case "forward":
                case "fwd":
                    return OneWayBucket.Forward;
                case "backward":
                case "reverse":
                case "rev":
                case "-1":
                    return OneWayBucket.Backward;
                default:
                    return OneWayBucket.Any;
            }
        }

        private static void EnsurePrefabRulesLoaded()
        {
            if (_prefabRulesLoaded)
                return;

            lock (_prefabRulesLock)
            {
                if (_prefabRulesLoaded)
                    return;

                try
                {
                    LoadPrefabRulesFromJson();
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Failed to load prefab mapping JSON: {ex.Message}");
                }

                _prefabRulesLoaded = true;
            }
        }

        private static void LoadPrefabRulesFromJson()
        {
            var path = GetPrefabRulesFilePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Mod.log.Warn($"Prefab mapping JSON not found at '{path}'. Using empty mappings.");
                return;
            }

            PrefabMappingsRoot root;
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(PrefabMappingsRoot));
                    root = serializer.ReadObject(stream) as PrefabMappingsRoot;
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Prefab mapping JSON at '{path}' could not be parsed: {ex.Message}");
                return;
            }

            if (root == null)
            {
                Mod.log.Warn($"Prefab mapping JSON at '{path}' was not a valid mapping root object.");
                return;
            }

            if (root.RoadTypes != null)
            {
                ApplyPrefabRulesDictionary(root.RoadTypes);
                return;
            }

            if (root.RoadTypeGroups != null)
            {
                ApplyPrefabRulesGroups(root.RoadTypeGroups);
                return;
            }
        }

        private static void ApplyPrefabRulesGroups(List<PrefabRoadTypeGroup> roadTypeGroups)
        {
            foreach (var key in DefaultPrefabRulesByRoadType.Keys.ToList())
                DefaultPrefabRulesByRoadType[key].Clear();

            if (roadTypeGroups == null)
                return;

            foreach (var group in roadTypeGroups)
            {
                if (group == null || group.RoadTypes == null || group.Rules == null)
                    continue;

                var rules = ParsePrefabRules(group.Rules.Cast<object>());
                if (rules.Count == 0 || group.RoadTypes.Count == 0)
                    continue;

                foreach (var roadType in group.RoadTypes)
                {
                    if (string.IsNullOrWhiteSpace(roadType))
                        continue;

                    if (!DefaultPrefabRulesByRoadType.TryGetValue(roadType, out var list))
                    {
                        list = new List<PrefabSelectionRule>();
                        DefaultPrefabRulesByRoadType[roadType] = list;
                    }

                    list.Clear();
                    list.AddRange(rules);
                }
            }
        }

        private static void ApplyPrefabRulesDictionary(Dictionary<string, List<PrefabRuleData>> roadTypes)
        {
            foreach (var key in DefaultPrefabRulesByRoadType.Keys.ToList())
                DefaultPrefabRulesByRoadType[key].Clear();

            foreach (var pair in roadTypes)
            {
                var roadType = pair.Key ?? string.Empty;
                var rules = ParsePrefabRules(pair.Value.Cast<object>());
                if (!DefaultPrefabRulesByRoadType.TryGetValue(roadType, out var list))
                {
                    list = new List<PrefabSelectionRule>();
                    DefaultPrefabRulesByRoadType[roadType] = list;
                }

                list.Clear();
                list.AddRange(rules);
            }
        }

        private static List<PrefabSelectionRule> ParsePrefabRules(System.Collections.IEnumerable value)
        {
            var rules = new List<PrefabSelectionRule>();
            if (value == null)
                return rules;

            foreach (var item in value)
            {
                var dict = item as PrefabRuleData;
                if (dict == null)
                    continue;

                var prefabName = dict.Prefab ?? string.Empty;
                if (string.IsNullOrWhiteSpace(prefabName))
                    continue;

                rules.Add(new PrefabSelectionRule
                {
                    LaneBucket = ParseLaneBucket(dict.Lane),
                    ForwardLaneBucket = ParseLaneBucket(dict.LanesForward),
                    BackwardLaneBucket = ParseLaneBucket(dict.LanesBackward),
                    BothWaysLaneBucket = ParseLaneBucket(dict.LanesBothWays),
                    Sidewalk = ParseSidewalkBucket(dict.Sidewalk),
                    ParkingSide = ParseParkingSideBucket(dict.ParkingSide),
                    ParkingStyle = ParseParkingStyleBucket(dict.ParkingStyle),
                    Bridge = ParseTriState(dict.Bridge),
                    Tunnel = ParseTriState(dict.Tunnel),
                    OneWay = ParseOneWayBucket(dict.OneWay),
                    BikeLanes = ParseTriState(dict.BikeLanes),
                    TurnRestrictions = ParseTriState(dict.TurnRestrictions),
                    Crosswalks = ParseTriState(dict.Crosswalks),
                    StopSigns = ParseTriState(dict.StopSigns),
                    TrafficLights = ParseTriState(dict.TrafficLights),
                    RestrictedLanes = ParseTriState(dict.RestrictedLanes),
                    PrefabName = prefabName,
                });
            }

            return rules;
        }

        [DataContract]
        private sealed class PrefabMappingsRoot
        {
            [DataMember(Name = "roadTypes")]
            public Dictionary<string, List<PrefabRuleData>> RoadTypes { get; set; }

            [DataMember(Name = "roadTypeGroups")]
            public List<PrefabRoadTypeGroup> RoadTypeGroups { get; set; }
        }

        [DataContract]
        private sealed class PrefabRoadTypeGroup
        {
            [DataMember(Name = "roadTypes")]
            public List<string> RoadTypes { get; set; }

            [DataMember(Name = "rules")]
            public List<PrefabRuleData> Rules { get; set; }
        }

        [DataContract]
        private sealed class PrefabRuleData
        {
            [DataMember(Name = "lane")]
            public string Lane { get; set; }

            [DataMember(Name = "lanesForward")]
            public string LanesForward { get; set; }

            [DataMember(Name = "lanesBackward")]
            public string LanesBackward { get; set; }

            [DataMember(Name = "lanesBothWays")]
            public string LanesBothWays { get; set; }

            [DataMember(Name = "sidewalk")]
            public string Sidewalk { get; set; }

            [DataMember(Name = "parkingSide")]
            public string ParkingSide { get; set; }

            [DataMember(Name = "parkingStyle")]
            public string ParkingStyle { get; set; }

            [DataMember(Name = "bridge")]
            public string Bridge { get; set; }

            [DataMember(Name = "tunnel")]
            public string Tunnel { get; set; }

            [DataMember(Name = "oneway")]
            public string OneWay { get; set; }

            [DataMember(Name = "bikeLanes")]
            public string BikeLanes { get; set; }

            [DataMember(Name = "turnRestrictions")]
            public string TurnRestrictions { get; set; }

            [DataMember(Name = "crosswalks")]
            public string Crosswalks { get; set; }

            [DataMember(Name = "stopSigns")]
            public string StopSigns { get; set; }

            [DataMember(Name = "trafficLights")]
            public string TrafficLights { get; set; }

            [DataMember(Name = "restrictedLanes")]
            public string RestrictedLanes { get; set; }

            [DataMember(Name = "prefab")]
            public string Prefab { get; set; }
        }

        private static string GetPrefabRulesFilePath()
        {
            if (!string.IsNullOrWhiteSpace(Mod.ModDirectory))
                return Path.Combine(Mod.ModDirectory, "prefab-mappings.json");

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? string.Empty, "prefab-mappings.json");
        }

        private static int ParsePrefabLaneCount(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return 0;

            var parts = prefabName.Split(new[] { ' ', '-', '_', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!parts[i + 1].Equals("lane", StringComparison.OrdinalIgnoreCase) &&
                    !parts[i + 1].Equals("lanes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var word = parts[i].Trim().ToLowerInvariant();
                switch (word)
                {
                    case "one": return 1;
                    case "two": return 2;
                    case "three": return 3;
                    case "four": return 4;
                    case "five": return 5;
                    case "six": return 6;
                    case "seven": return 7;
                    case "eight": return 8;
                    case "nine": return 9;
                    case "ten": return 10;
                }

                if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) && parsedCount > 0)
                    return parsedCount;
            }

            return 0;
        }

        private string DescribeQueuedLaneCounts()
        {
            if (_queuedSegmentInfos.Count == 0)
                return L("OsmImporter.Value.None", "none");

            var groups = _queuedSegmentInfos
                .GroupBy(info => info.Lanes)
                .OrderBy(g => g.Key <= 0 ? int.MaxValue : g.Key)
                .Select(g => g.Key > 0
                    ? LF("OsmImporter.Format.LaneCount", "{0}-lane: {1}", g.Key, g.Count())
                    : LF("OsmImporter.Format.UnknownLaneCount", "unknown: {0}", g.Count()));

            return string.Join(", ", groups);
        }

        private IEnumerable<NetPrefab> GetRoadPrefabCandidates()
        {
            if (_prefabSystem == null)
                return Enumerable.Empty<NetPrefab>();

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<RoadData>(),
                ComponentType.ReadOnly<NetData>());

            using var prefabs = query.ToEntityArray(Allocator.Temp);
            return prefabs
                // DLC-gated road prefabs must never be auto-selected for OSM generation.
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => _prefabSystem.GetPrefab<NetPrefab>(e))
                .Where(p => p != null)
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<NetPrefab> GetPathPrefabCandidates()
        {
            if (_prefabSystem == null)
                return Enumerable.Empty<NetPrefab>();

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetData>());

            using var prefabs = query.ToEntityArray(Allocator.Temp);
            return prefabs
                // DLC-gated prefabs must never be auto-selected for OSM generation.
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => _prefabSystem.GetPrefab<NetPrefab>(e))
                .Where(p => p != null)
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<NetPrefab> GetTrackPrefabCandidates()
        {
            if (_prefabSystem == null)
                return Enumerable.Empty<NetPrefab>();

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetData>());

            using var prefabs = query.ToEntityArray(Allocator.Temp);
            return prefabs
                // DLC-gated prefabs must never be auto-selected for OSM generation.
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => _prefabSystem.GetPrefab<NetPrefab>(e))
                .Where(p => p != null)
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string[] GetPreferredPrefabNames(string highwayType, int lanes, bool requiresSidewalk)
        {
            switch (highwayType.ToLowerInvariant())
            {
                case "motorway":
                case "motorway_link":
                    return new[] { "Highway", "Freeway" };
                case "trunk":
                case "trunk_link":
                    return new[] { "Highway", "Freeway", "Boulevard" };
                case "primary":
                case "primary_link":
                    return new[] { "Oneway", "Road", "Avenue", "Street" };
                case "secondary":
                case "secondary_link":
                    return new[] { "Oneway", "Road", "Avenue" };
                case "tertiary":
                case "tertiary_link":
                    return new[] { "Oneway", "Street", "Road" };
                case "unclassified":
                case "residential":
                case "living_street":
                case "road":
                    return new[] { "Street", "Road" };
                case "service":
                    return new[] { "Service", "Street", "Road" };
                case "footway":
                case "steps":
                case "pedestrian":
                    return new[] { "Pedestrian Path", "Pedestrian Walkway", "Walkway", "Footpath", "Path" };
                case "bridleway":
                    return new[] { "Pedestrian", "Path", "Street" };
                case "cycleway":
                    return new[] { "Cycleway", "Path", "Road", "Street" };
                case "track":
                    return new[] { "Gravel Road", "GravelRoad", "Gravel Oneway Road", "GravelOnewayRoad", "SmallRoad", "Small Road", "Road", "Street" };
                case "monorail":
                    return new[] { "Oneway Train Track", "OnewayTrainTrack", "Monorail Track", "MonorailTrack", "Monorail", "TwoWay Train Track", "TwoWayTrainTrack", "Train Track", "TrainTrack", "Double Train Track", "DoubleTrainTrack" };
                case "rail":
                case "subway":
                    return new[] { "Train Track", "TrainTrack", "Train", "Double Train Track", "DoubleTrainTrack", "Oneway Train Track", "OnewayTrainTrack", "TwoWay Train Track", "TwoWayTrainTrack" };
                case "light_rail":
                    return new[] { "Double Tram Track", "DoubleTramTrack", "Tram Track", "TramTrack", "Train Track", "TrainTrack", "Double Train Track", "DoubleTrainTrack" };
                case "tram":
                    return new[] { "Tram Track", "TramTrack", "Oneway Tram Track", "OnewayTramTrack", "Double Tram Track", "DoubleTramTrack" };
                default:
                    return new[] { "Street", "Road" };
            }
        }

        private static string[] GetPreferredPathPrefabNames(string highwayType)
        {
            switch (highwayType.ToLowerInvariant())
            {
                case "cycleway":
                    return new[] { "Cycleway", "Bike Path", "Bicycle Path", "Path", "Walkway" };
                case "bridleway":
                    return new[] { "Trail", "Bridleway", "Path", "Walkway", "Pedestrian Path" };
                case "footway":
                case "steps":
                case "pedestrian":
                    return new[]
                    {
                        "Pavement Path",
                        "PedestrianPathWide01",
                        "PedestrianPathwithBikeLanes",
                        "TwoWayBikePath",
                        "Pedestrian Path",
                        "Pedestrian Walkway",
                        "Walkway",
                        "Footpath",
                        "Path",
                        "Pedestrian"
                    };
                default:
                    return new[] { "Path", "Walkway", "Pedestrian Path" };
            }
        }

        private static bool IsSuitableRoadPrefabName(string prefabName, string highwayType, bool requiresSidewalk)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return false;

            var lower = prefabName.ToLowerInvariant();
            if (lower.Contains("pillar") ||
                lower.Contains("extrados") ||
                lower.Contains("cable stayed") ||
                lower.Contains("cable-stayed") ||
                lower.Contains("bridge pillar") ||
                lower.Contains("bridge support") ||
                lower.Contains("support") ||
                lower.Contains("tunnel") ||
                lower.Contains("interchange"))
            {
                return false;
            }

            // Gravel/dirt/unpaved prefabs are suitable for farm/forest tracks
            if (!(highwayType is "track") &&
                (lower.Contains("gravel") || lower.Contains("dirt") || lower.Contains("unpaved")))
            {
                return false;
            }

            // Monorail should use single track, not double
            if (highwayType is "monorail" && lower.Contains("double"))
                return false;

            // Railway prefabs naturally contain "track" in their names (e.g. TrainTrack)
            if (!(highwayType is "rail" or "monorail" or "light_rail" or "subway" or "tram") && lower.Contains("track"))
                return false;

            if (requiresSidewalk && lower.Contains("alley"))
                return false;

            if (!(highwayType is "footway" or "steps" or "bridleway" or "cycleway" or "pedestrian") &&
                (lower.Contains("pedestrianstreet") || lower.Contains("pedestrian street")))
            {
                return false;
            }

            // Path prefabs are suitable for pedestrian/cycle types
            if (!(highwayType is "footway" or "steps" or "bridleway" or "cycleway" or "pedestrian") && lower.Contains("path"))
                return false;

            if (highwayType.Equals("service", StringComparison.OrdinalIgnoreCase) &&
                (lower.Contains("highway") || lower.Contains("freeway")))
            {
                return false;
            }

            return true;
        }

        private bool IsDlcGatedPrefab(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null || !EntityManager.Exists(prefabEntity))
                return false;

            // Any prefab with ContentPrerequisiteData is DLC/mod-gated and must not be auto-selected.
            if (EntityManager.HasComponent<ContentPrerequisiteData>(prefabEntity))
                return true;

            if (_prefabSystem != null && _prefabSystem.TryGetPrefab<NetPrefab>(prefabEntity, out var prefab) && prefab != null)
                return IsKnownDlcRoadName(prefab.name);

            return false;
        }

        private static bool IsKnownDlcRoadName(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return false;

            var lower = prefabName.ToLowerInvariant();
            return lower.Contains("port gate") ||
                   lower.Contains("bridgesandports") ||
                   lower.Contains("bridges and ports");
        }

        private static bool IsSuitableRoadPrefabName(string prefabName) =>
            IsSuitableRoadPrefabName(prefabName, string.Empty, false);

        private static string GetRoadPrefabCacheKey(string highwayType, int lanes, OsmSidewalkType sidewalk)
            => $"{highwayType}:{lanes}:{(int)sidewalk}";

        private static bool IsSuitablePathPrefabName(string prefabName, string highwayType, bool requiresSidewalk)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return false;

            var lower = prefabName.ToLowerInvariant();
            if (lower.Contains("pillar") ||
                lower.Contains("extrados") ||
                lower.Contains("cable stayed") ||
                lower.Contains("cable-stayed") ||
                lower.Contains("bridge") ||
                lower.Contains("support") ||
                lower.Contains("tunnel") ||
                lower.Contains("interchange") ||
                lower.Contains("highway") ||
                lower.Contains("freeway") ||
                lower.Contains("boulevard") ||
                lower.Contains("street") ||
                lower.Contains("avenue") ||
                lower.Contains("road"))
            {
                return false;
            }

            return highwayType.ToLowerInvariant() switch
            {
                "cycleway" => lower.Contains("cycle") || lower.Contains("bike") || lower.Contains("bicycle") || lower.Contains("path") || lower.Contains("walkway"),
                "bridleway" => lower.Contains("trail") || lower.Contains("path") || lower.Contains("walkway") || lower.Contains("pedestrian"),
                "footway" or "steps" or "pedestrian" => lower.Contains("pedestrian") || lower.Contains("walkway") || lower.Contains("footpath") || lower.Contains("path"),
                _ => lower.Contains("path") || lower.Contains("walkway") || lower.Contains("trail"),
            };
        }

        private static bool IsSuitablePathPrefabName(string prefabName, string highwayType) =>
            IsSuitablePathPrefabName(prefabName, highwayType, false);

        private static bool IsPathLikeHighwayType(string highwayType)
        {
            switch ((highwayType ?? string.Empty).ToLowerInvariant())
            {
                case "footway":
                case "steps":
                case "pedestrian":
                case "bridleway":
                case "cycleway":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsSuitableRoundaboutPrefabName(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                return false;

            var lower = prefabName.ToLowerInvariant();
            return lower.Contains("roundabout") &&
                   !lower.Contains("interchange") &&
                   !lower.Contains("teardrop");
        }

        private static IEnumerable<string> GetPreferredRoundaboutPrefabNames(OsmWay way)
        {
            var diameter = GetWayDiameterMeters(way);
            if (diameter <= 0f)
                diameter = GetWayLengthEstimate(way) * 0.25f;

            if (diameter <= 28f)
                return new[] { "SmallRoundabout01", "SmallRoundabout02", "SmallRoundabout03", "SmallRoundabout04" };

            if (diameter <= 45f)
                return new[] { "MediumRoundabout01", "MediumRoundabout02", "MediumRoundabout03", "MediumRoundabout04" };

            if (diameter <= 70f)
                return new[] { "LargeRoundabout01", "LargeRoundabout02", "LargeRoundabout03", "LargeRoundabout04", "LargeRoundabout05", "LargeRoundabout06", "LargeRoundabout07", "LargeRoundabout08", "LargeRoundabout09", "LargeRoundabout10" };

            return new[] { "XLRoundabout01", "XLRoundabout02", "XLRoundabout03", "XLRoundabout04", "XLRoundabout05", "XLRoundabout06", "XLRoundabout07", "XLRoundabout08", "XLRoundabout09", "XLRoundabout10" };
        }

        private static float GetWayDiameterMeters(OsmWay way)
        {
            if (way == null || way.Points == null || way.Points.Count < 2)
                return 0f;

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

            return Math.Max(maxX - minX, maxZ - minZ);
        }

        private static float GetWayLengthEstimate(OsmWay way)
        {
            if (way == null || way.Points == null || way.Points.Count < 2)
                return 0f;

            var length = 0f;
            for (var i = 1; i < way.Points.Count; i++)
                length += math.distance(way.Points[i - 1], way.Points[i]);

            return length;
        }

        private static bool IsTrackLikeHighwayType(string highwayType)
        {
            switch ((highwayType ?? string.Empty).ToLowerInvariant())
            {
                case "monorail":
                case "rail":
                case "subway":
                case "light_rail":
                case "tram":
                    return true;
                default:
                    return false;
            }
        }

        private static int GetDefaultLaneCountForHighwayType(string highwayType)
        {
            switch ((highwayType ?? string.Empty).ToLowerInvariant())
            {
                case "motorway":
                case "motorway_link":
                    return 4;
                case "trunk":
                case "trunk_link":
                    return 4;
                case "primary":
                case "primary_link":
                    return 3;
                case "secondary":
                case "secondary_link":
                    return 2;
                case "tertiary":
                case "tertiary_link":
                    return 2;
                case "unclassified":
                case "residential":
                case "living_street":
                case "road":
                    return 2;
                case "service":
                    return 1;
                case "footway":
                case "steps":
                case "bridleway":
                case "track":
                case "cycleway":
                case "pedestrian":
                    return 1;
                case "monorail":
                case "rail":
                case "light_rail":
                case "subway":
                case "tram":
                    return 2;
                default:
                    return 0;
            }
        }

        private string DescribePrefab(Entity prefabEntity)
        {
            if (_prefabSystem == null)
                return $"{prefabEntity.Index}:{prefabEntity.Version}";

            if (_prefabSystem.TryGetPrefab<NetPrefab>(prefabEntity, out var prefab) && prefab != null)
                return prefab.name;

            if (_prefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out var basePrefab) && basePrefab != null)
                return basePrefab.name;

            return $"{prefabEntity.Index}:{prefabEntity.Version}";
        }

        private void LogPrefabCatalogOnce()
        {
            if (_loggedPrefabCatalog || _prefabSystem == null)
                return;

            using var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>());
            using var prefabs = query.ToEntityArray(Allocator.Temp);

            if (prefabs.Length == 0)
                return;

            var entries = new List<string>(prefabs.Length);
            foreach (var entity in prefabs)
            {
                var name = EntityManager.GetName(entity);
                if (string.IsNullOrWhiteSpace(name) && _prefabSystem.TryGetPrefab<NetPrefab>(entity, out var netPrefab) && netPrefab != null)
                    name = netPrefab.name;

                if (string.IsNullOrWhiteSpace(name))
                    name = $"{entity.Index}:{entity.Version}";

                using var componentTypes = EntityManager.GetComponentTypes(entity, Allocator.Temp);
                var typeNames = componentTypes.Length == 0
                    ? string.Empty
                    : string.Join(", ", componentTypes.Select(t => t.ToString()));

                entries.Add(string.IsNullOrWhiteSpace(typeNames)
                    ? name
                    : $"{name} [{typeNames}]");
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            Mod.log.Info($"Prefab catalog dump ({entries.Count} prefabs):");
            foreach (var entry in entries)
                Mod.log.Info($"  {entry}");

            _loggedPrefabCatalog = true;
        }

        private List<string> BuildGenerationTypeOrder()
        {
            if (_document == null || _document.Ways.Count == 0)
                return new List<string>();

            var selectedTypes = _activeGenerateOptions?.HighwayTypes;
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var way in _document.Ways)
            {
                if (string.IsNullOrWhiteSpace(way.Highway))
                    continue;

                if (selectedTypes != null && selectedTypes.Count > 0 && !selectedTypes.Contains(way.Highway))
                    continue;

                if (seen.Add(way.Highway))
                    order.Add(way.Highway);
            }

            return order;
        }

        private List<OsmWay> GetWaysForHighwayType(string highwayType, bool includeRoundabouts = true, bool includeRoundaboutConnectors = false)
        {
            if (_document == null || _document.Ways.Count == 0)
                return new List<OsmWay>();

            return _document.Ways
                .Where(way => string.Equals(way.Highway, highwayType, StringComparison.OrdinalIgnoreCase) &&
                              (includeRoundabouts || !way.IsRoundabout) &&
                              !(way.IsInsideParkingLot && (_activeGenerateOptions?.GenerateParkingLots ?? false)) &&
                              (includeRoundaboutConnectors || !_roundaboutConnectorWayIds.Contains(way.Id)))
                .ToList();
        }

        private int CountPendingDefinitions()
        {
            using var entities = GetEntityQuery(
                ComponentType.ReadOnly<OsmImportDefinition>(),
                ComponentType.ReadOnly<CreationDefinition>(),
                ComponentType.ReadOnly<NetCourse>()).ToEntityArray(Allocator.Temp);

            var count = 0;
            foreach (var entity in entities)
            {
                if (EntityManager.GetComponentData<OsmImportDefinition>(entity).SessionId == _sessionId)
                    count++;
            }

            return count;
        }

        private int CountAllNetEdges()
        {
            using var entities = GetCurrentNetEdgeArray(Allocator.Temp);

            return entities.Length;
        }

        /// <summary>
        /// Every network edge the importer can generate: roads and tracks (GetCurrentNetEdges) plus pedestrian
        /// pathways. Pathway edges carry no type-specific component like Road or TrainTrack, so they are found
        /// through their prefab's PathwayData. Without them, footways were missing from the baseline/new-edge
        /// tracking, so Delete Generated left them behind and the geometry report showed them as unbuilt.
        /// </summary>
        private NativeArray<Entity> GetCurrentNetEdgeArray(Allocator allocator)
        {
            using var roadEdges = GetCurrentNetEdges().ToEntityArray(Allocator.Temp);
            using var otherEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<TrainTrack>(),
                    ComponentType.ReadOnly<SubwayTrack>(), ComponentType.ReadOnly<TramTrack>()
                },
            }).ToEntityArray(Allocator.Temp);

            var result = new List<Entity>(roadEdges.Length + otherEdges.Length);
            result.AddRange(roadEdges);
            foreach (var entity in otherEdges)
            {
                var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (EntityManager.HasComponent<PathwayData>(prefab))
                    result.Add(entity);
            }

            return new NativeArray<Entity>(result.ToArray(), allocator);
        }

        private EntityQuery GetCurrentNetEdges()
        {
            return GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<PrefabRef>() },
                Any = new[] { ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<TrainTrack>(), ComponentType.ReadOnly<SubwayTrack>(), ComponentType.ReadOnly<TramTrack>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        private bool TryGetRoadPrefab(string name, out NetPrefab prefab, out Entity entity)
        {
            using var entities = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<RoadData>(),
                ComponentType.ReadOnly<NetData>()).ToEntityArray(Allocator.Temp);

            var candidates = entities
                // Never allow DLC-gated road prefabs here; they break generation for players without the DLC.
                .Where(e => !IsDlcGatedPrefab(e))
                .Select(e => _prefabSystem.GetPrefab<NetPrefab>(e))
                .Where(p => p != null && IsSuitableRoadPrefabName(p.name))
                .ToList();

            if (!string.IsNullOrWhiteSpace(name))
            {
                prefab = candidates.FirstOrDefault(p => p.name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (prefab != null && _prefabSystem.TryGetEntity(prefab, out entity))
                    return true;
            }

            prefab = candidates.FirstOrDefault(p => p.name.IndexOf("Two-Lane", StringComparison.OrdinalIgnoreCase) >= 0)
                     ?? candidates.FirstOrDefault();

            if (prefab != null && _prefabSystem.TryGetEntity(prefab, out entity))
                return true;

            entity = Entity.Null;
            return false;
        }

        private void LogImportedBounds(OsmDocument document)
        {
            if (!document.HasBounds)
                return;

            var size = document.Max - document.Min;
            Mod.log.Info(
                $"Projected OSM bounds: center lat/lon {document.CenterLat:F6}, {document.CenterLon:F6}; " +
                $"x {document.Min.x:F1}..{document.Max.x:F1}m, z {document.Min.z:F1}..{document.Max.z:F1}m, " +
                $"size {size.x:F1}m x {size.z:F1}m, elevation y={document.ElevationMeters:F1}m");
        }

        private string DocumentBoundsMessage() => LF("OsmImporter.Bounds.ProjectedImport", "Projected import bounds: {0}", ShortBounds(_document));

        private static string ShortBounds(OsmDocument document)
        {
            if (document == null || !document.HasBounds)
                return L("OsmImporter.Value.Unknown", "unknown");

            var size = document.Max - document.Min;
            return LF("OsmImporter.Bounds.Short", "{0:F0}m x {1:F0}m, y={2:F1}m", size.x, size.z, document.ElevationMeters);
        }

        private static string SelectedTypesLabel(OsmGenerateOptions options)
        {
            return options == null || options.HighwayTypes.Count == 0
                ? L("OsmImporter.Value.AllLoadedTypes", "all loaded types")
                : string.Join(", ", options.HighwayTypes.OrderBy(t => t).Select(DisplayHighwayType));
        }

        private static string BuildRoadTypeCountsJson(OsmDocument document)
        {
            if (document == null)
                return "{}";

            var entries = document.Ways
                .GroupBy(way => way.Highway)
                .OrderBy(group => group.Key)
                .Select(group => $"\"{EscapeJson(group.Key)}\":{group.Count()}");

            return "{" + string.Join(",", entries) + "}";
        }

        private static string EscapeJson(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static int ImportedHash(float3 start, float3 end) =>
            (int)math.hash(new float4(start.x + end.x, start.y + end.y, start.z + end.z, math.distance(start, end)));

        private static string FormatPoint(float3 point) =>
            $"({point.x:F1}, {point.y:F1}, {point.z:F1})";

        private float3 GetTerrainWorldOffset()
        {
            if (_groundHeightSystem == null)
                return float3.zero;

            try
            {
                if (!TryGetTerrainHeightData(out var terrainHeightData, out var sourceLabel))
                    return float3.zero;

                Mod.log.Info($"Terrain height source for offset: {sourceLabel}");

                var offsetProperty = terrainHeightData.GetType().GetProperty(
                    "offset",
                    BindingFlags.Instance | BindingFlags.Public);
                if (offsetProperty == null || offsetProperty.PropertyType != typeof(float3))
                    return float3.zero;

                return (float3)offsetProperty.GetValue(terrainHeightData);
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Unable to read terrain world offset: {ex.Message}");
                return float3.zero;
            }
        }

        private float SampleTerrainHeight(float3 position)
        {
            if (_hasTerrainHeightData)
            {
                if (!_loggedTerrainMethodProbe)
                {
                    _loggedTerrainMethodProbe = true;
                    LogTerrainMethods("TerrainSystem.GetHeightData(false)", _terrainHeightData);
                }

                return TerrainUtils.SampleHeight(ref _terrainHeightData, position);
            }

            if (TrySampleUnityTerrainHeight(position, out var unityHeight))
                return unityHeight;

            return 0f;
        }

        private void CacheTerrainHeightData()
        {
            _hasTerrainHeightData = false;

            if (_terrainSystem == null)
                return;

            try
            {
                _terrainHeightData = _terrainSystem.GetHeightData(false);
                _hasTerrainHeightData = _terrainHeightData.isCreated;

                if (!_hasTerrainHeightData)
                {
                    _terrainHeightData = _terrainSystem.GetHeightData(true);
                    _hasTerrainHeightData = _terrainHeightData.isCreated;
                }

                if (_hasTerrainHeightData)
                {
                    Mod.log.Info(
                        $"Cached terrain height data from TerrainSystem: worldOffset={_terrainSystem.worldOffset}, " +
                        $"positionOffset={_terrainSystem.positionOffset}, heightScaleOffset={_terrainSystem.heightScaleOffset}, " +
                        $"terrainOffset={_terrainHeightData.offset}");
                }
                else
                {
                    Mod.log.Warn("TerrainSystem returned no usable height data.");
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Unable to cache terrain height data: {ex.Message}");
            }
        }

        private static MethodInfo FindTerrainMethod(string name)
        {
            return typeof(TerrainUtils)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == 2);
        }

        private static bool TryInvokeTerrainPoint(MethodInfo method, object terrainHeightData, float3 position, out float3 result)
        {
            result = position;
            if (method == null)
                return false;

            var parameters = method.GetParameters();
            if (parameters.Length != 2)
                return false;

            object pointArgument = CreateTerrainPointArgument(parameters[1].ParameterType, position);
            if (pointArgument == null)
                return false;

            var arguments = new object[] { terrainHeightData, pointArgument };
            var returnValue = method.Invoke(null, arguments);
            if (returnValue is float3 returnedPosition)
            {
                result = returnedPosition;
                return true;
            }

            if (TryReadTerrainPoint(arguments[1], out var mutatedPosition))
            {
                result = mutatedPosition;
                return true;
            }

            return false;
        }

        private static bool TrySampleTerrainValue(string label, MethodInfo method, object terrainHeightData, float3 position, out float value)
        {
            value = 0f;
            if (method == null)
                return false;

            var parameters = method.GetParameters();
            if (parameters.Length != 2)
                return false;

            object pointArgument = CreateTerrainPointArgument(parameters[1].ParameterType, position);
            if (pointArgument == null)
                return false;

            var arguments = new object[] { terrainHeightData, pointArgument };
            var returnValue = method.Invoke(null, arguments);
            if (returnValue is float sampledValue)
            {
                value = sampledValue;
                Mod.log.Info($"Terrain {label} returned {sampledValue:F3} at {FormatPoint(position)}");
                return true;
            }

            if (TryReadTerrainFloat(arguments[1], out var mutatedValue))
            {
                value = mutatedValue;
                Mod.log.Info($"Terrain {label} mutated to {mutatedValue:F3} at {FormatPoint(position)}");
                return true;
            }

            return false;
        }

        private static object CreateTerrainPointArgument(Type type, float3 position)
        {
            if (type == typeof(float3))
                return position;

            if (type == typeof(float2))
                return new float2(position.x, position.z);

            if (type.IsByRef)
            {
                var elementType = type.GetElementType();
                if (elementType == typeof(float3))
                    return position;
                if (elementType == typeof(float2))
                    return new float2(position.x, position.z);
            }

            return null;
        }

        private static bool TryReadTerrainPoint(object argument, out float3 value)
        {
            value = default;
            if (argument is float3 point3)
            {
                value = point3;
                return true;
            }

            if (argument is float2 point2)
            {
                value = new float3(point2.x, 0f, point2.y);
                return true;
            }

            return false;
        }

        private static bool TryReadTerrainFloat(object argument, out float value)
        {
            value = 0f;
            if (argument is float sampledValue)
            {
                value = sampledValue;
                return true;
            }

            return false;
        }

        private static bool TrySampleUnityTerrainHeight(float3 position, out float height)
        {
            height = 0f;

            try
            {
                var terrainType = Type.GetType("UnityEngine.Terrain, UnityEngine.TerrainModule");
                var vector3Type = Type.GetType("UnityEngine.Vector3, UnityEngine.CoreModule");
                if (terrainType == null || vector3Type == null)
                    return false;

                var sampleHeightMethod = terrainType.GetMethod(
                    "SampleHeight",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { vector3Type },
                    null);
                if (sampleHeightMethod == null)
                    return false;

                var activeTerrainProperty = terrainType.GetProperty("activeTerrain", BindingFlags.Static | BindingFlags.Public);
                if (activeTerrainProperty != null)
                {
                    var activeTerrain = activeTerrainProperty.GetValue(null);
                    if (activeTerrain != null)
                    {
                        var vector3 = Activator.CreateInstance(vector3Type, new object[] { position.x, position.y, position.z });
                        var sample = sampleHeightMethod.Invoke(activeTerrain, new[] { vector3 });
                        if (sample is float sampleHeight)
                        {
                            height = sampleHeight;
                            Mod.log.Info($"Unity terrain SampleHeight returned {height:F3} at {FormatPoint(position)}");
                            return true;
                        }
                    }
                }

                var activeTerrainsProperty = terrainType.GetProperty("activeTerrains", BindingFlags.Static | BindingFlags.Public);
                if (activeTerrainsProperty?.GetValue(null) is Array activeTerrains)
                {
                    foreach (var terrain in activeTerrains)
                    {
                        if (terrain == null)
                            continue;

                        var vector3 = Activator.CreateInstance(vector3Type, new object[] { position.x, position.y, position.z });
                        var sample = sampleHeightMethod.Invoke(terrain, new[] { vector3 });
                        if (sample is float sampleHeight)
                        {
                            height = sampleHeight;
                            Mod.log.Info($"Unity terrain SampleHeight returned {height:F3} at {FormatPoint(position)}");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Unity terrain sampling failed: {ex.Message}");
            }

            return false;
        }

        private void LogTerrainMethods(string sourceLabel, object terrainHeightData)
        {
            try
            {
                var methods = typeof(TerrainUtils)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(method => method.Name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     method.Name.IndexOf("Heightmap", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(method => $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})")
                    .OrderBy(name => name)
                    .Take(25)
                    .ToArray();
                var dataType = terrainHeightData != null ? terrainHeightData.GetType().FullName : "null";

                Mod.log.Info(
                    $"TerrainUtils probe: source={sourceLabel}, dataType={dataType}; " +
                    $"methods={string.Join(" | ", methods)}");
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Unable to probe TerrainUtils methods: {ex.Message}");
            }
        }

        private bool TryGetTerrainHeightData(out object terrainHeightData, out string sourceLabel)
        {
            terrainHeightData = null;
            sourceLabel = string.Empty;

            if (_hasTerrainHeightData)
            {
                terrainHeightData = _terrainHeightData;
                sourceLabel = "TerrainSystem.GetHeightData(false)";
                return true;
            }

            if (_terrainSystem == null)
                return false;

            try
            {
                _terrainHeightData = _terrainSystem.GetHeightData(false);
                _hasTerrainHeightData = _terrainHeightData.isCreated;
                if (!_hasTerrainHeightData)
                {
                    _terrainHeightData = _terrainSystem.GetHeightData(true);
                    _hasTerrainHeightData = _terrainHeightData.isCreated;
                }

                if (_hasTerrainHeightData)
                {
                    terrainHeightData = _terrainHeightData;
                    sourceLabel = "TerrainSystem.GetHeightData(false)";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"TerrainSystem height data lookup failed: {ex.Message}");
            }

            return false;
        }

        private static bool TryFindTerrainHeightData(object instance, int depth, string path, out object terrainHeightData, out string sourceLabel)
        {
            terrainHeightData = null;
            sourceLabel = string.Empty;

            if (instance == null || depth > 3)
                return false;

            if (LooksLikeTerrainHeightData(instance))
            {
                terrainHeightData = instance;
                sourceLabel = path;
                return true;
            }

            var type = instance.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var propertyName in new[] { "heights", "Heights", "m_Heights", "height", "Height", "m_Height", "terrain", "Terrain", "m_Terrain", "terrainData", "TerrainData", "m_TerrainData" })
            {
                var property = type.GetProperty(propertyName, flags);
                if (property == null || property.GetIndexParameters().Length > 0)
                    continue;

                object value = null;
                try
                {
                    value = property.GetValue(instance);
                }
                catch
                {
                    continue;
                }

                if (value == null)
                    continue;

                if (LooksLikeTerrainHeightData(value))
                {
                    terrainHeightData = value;
                    sourceLabel = $"{path}.{propertyName}";
                    return true;
                }

                if (TryFindTerrainHeightData(value, depth + 1, $"{path}.{propertyName}", out terrainHeightData, out sourceLabel))
                    return true;
            }

            foreach (var fieldName in new[] { "heights", "Heights", "m_Heights", "height", "Height", "m_Height", "terrain", "Terrain", "m_Terrain", "terrainData", "TerrainData", "m_TerrainData" })
            {
                var field = type.GetField(fieldName, flags);
                if (field == null)
                    continue;

                var value = field.GetValue(instance);
                if (value == null)
                    continue;

                if (LooksLikeTerrainHeightData(value))
                {
                    terrainHeightData = value;
                    sourceLabel = $"{path}.{fieldName}";
                    return true;
                }

                if (TryFindTerrainHeightData(value, depth + 1, $"{path}.{fieldName}", out terrainHeightData, out sourceLabel))
                    return true;
            }

            return false;
        }

        private static bool LooksLikeTerrainHeightData(object value)
        {
            if (value == null)
                return false;

            var type = value.GetType();
            var name = type.FullName ?? type.Name;

            if (string.Equals(name, "Game.Simulation.TerrainHeightData", StringComparison.Ordinal) ||
                string.Equals(type.Name, "TerrainHeightData", StringComparison.Ordinal))
                return true;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            if (type.GetProperty("offset", flags) != null &&
                type.GetProperty("heights", flags) != null)
                return true;

            if (type.GetMethod("SampleHeight", flags) != null &&
                type.GetMethod("ToHeightmapSpace", flags) != null)
                return true;

            return false;
        }

    }
}
