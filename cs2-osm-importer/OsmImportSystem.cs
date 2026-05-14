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
using Game.Prefabs;
using Game.Simulation;
using Game.UI;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    public partial class OsmImportSystem : GameSystemBase
    {
        private const int MaxSegmentsPerFrame = 128;
        private const int MaxApplyWaitFrames = 3600;
        private const float MinSegmentLengthMeters = 1f;

        private enum WorkPhase
        {
            Idle,
            LoadData,
            QueueDefinitions,
            WaitForGameApply,
            DeleteRoads
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
        private int _wayIndex;
        private int _pointIndex;
        private int _typeIndex;
        private int _importedSegments;
        private int _lastLoggedSegments;
        private int _sessionId;
        private int _applyWaitFrames;
        private int _baselineEdgeCount;
        private HashSet<Entity> _baselineRoadEdges = new HashSet<Entity>();
        private int _lastPendingDefinitions = -1;
        private int _lastWaitLogFrame;
        private bool _loggedFirstQueuedSegment;
        private bool _loggedTerrainMethodProbe;
        private bool _hasTerrainHeightData;
        private TerrainHeightData _terrainHeightData;
        private struct QueuedSegmentInfo
        {
            public float3 StartPos;
            public float3 EndPos;
            public float StartTerrainHeight;
            public float EndTerrainHeight;
            public float ElevationOffset;
            public string Name;
            public int Lanes;
            public float3 MidPoint => (StartPos + EndPos) * 0.5f;
        }

        private List<QueuedSegmentInfo> _queuedSegmentInfos = new List<QueuedSegmentInfo>();
        private List<string> _generationTypeOrder = new List<string>();
        private bool _roadNamesApplied;
        private WorkPhase _phase = WorkPhase.Idle;

        internal bool IsImporting { get; private set; }
        private string _status = "Ready.";
        internal string Status => _status;
        internal bool HasGeneratedAssets { get; private set; }
        internal bool HasLoadedData => _document != null && _document.Ways.Count > 0;
        internal string RoadTypeCountsJson { get; private set; } = "{}";

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _nameSystem = World.GetOrCreateSystemManaged<NameSystem>();

            _groundHeightSystem = World.GetOrCreateSystemManaged<GroundHeightSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            Enabled = false;
        }

        protected override void OnUpdate()
        {
            if (!IsImporting)
            {
                Enabled = false;
                return;
            }

            switch (_phase)
            {
                case WorkPhase.LoadData:
                    LoadData();
                    break;
                case WorkPhase.QueueDefinitions:
                    ProcessGenerationBatch();
                    break;
                case WorkPhase.WaitForGameApply:
                    WaitForGeneratedRoads();
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

            if (string.IsNullOrWhiteSpace(options.Path) || !File.Exists(options.Path))
            {
                _document = null;
                RoadTypeCountsJson = "{}";
                SetStatus("Enter a valid OSM file path.");
                return true;
            }

            _pendingLoadOptions = options;
            IsImporting = true;
            _phase = WorkPhase.LoadData;
            SetStatus("OSM file path accepted. Loading...");
            Enabled = true;
            Mod.log.Info($"Queued OSM load for '{options.Path}'");
            return true;
        }

        internal bool GenerateRoads(OsmGenerateOptions options)
        {
            if (IsImporting)
                return false;

            if (_document == null || _document.Ways.Count == 0)
            {
                SetStatus("Load OSM data before generating roads.");
                Enabled = true;
                return true;
            }

            _pendingGenerateOptions = options;
            IsImporting = true;
            _phase = WorkPhase.QueueDefinitions;
            SetStatus("Preparing road generation...");
            Enabled = true;
            Mod.log.Info($"Queued OSM road generation for selected types: {SelectedTypesLabel(options)}");
            PrepareGeneration();
            return true;
        }

        internal bool DeleteAllRoads()
        {
            if (IsImporting)
                return false;

            IsImporting = true;
            _phase = WorkPhase.DeleteRoads;
            SetStatus("Deleting roads...");
            Enabled = true;
            return true;
        }

        private void LoadData()
        {
            try
            {
                _activeOptions = _pendingLoadOptions;
                _pendingLoadOptions = null;
                SetStatus("Parsing OSM file...");
                Mod.log.Info($"Parsing OSM file '{_activeOptions.Path}'");

                if (string.IsNullOrWhiteSpace(_activeOptions.Path) || !File.Exists(_activeOptions.Path))
                {
                    _document = null;
                    RoadTypeCountsJson = "{}";
                    Complete("Enter a valid OSM file path.");
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
                LogImportedBounds(_document);

                if (_document.Ways.Count == 0)
                {
                    Complete("No supported OSM highway ways were found.");
                    return;
                }

                var typeSummary = string.Join(", ", _document.Ways
                    .GroupBy(w => w.Highway)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}: {g.Count()}"));
                Mod.log.Info($"Loaded OSM highway type counts: {typeSummary}");
                RoadTypeCountsJson = BuildRoadTypeCountsJson(_document);

                Complete($"Loaded {_document.Ways.Count} OSM ways. Select road types and click Generate.");
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                _document = null;
                RoadTypeCountsJson = "{}";
                Complete($"Failed to load OSM data: {ex.Message}");
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
                    Complete("No usable road prefab was found. Open a city and try again.");
                    return;
                }

                _prefabName = prefab.name;
                _highwayPrefabEntities.Clear();
                _highwayPrefabNames.Clear();
                _laneSpecificPrefabEntities.Clear();
                if (!_prefabSystem.TryGetComponentData(prefab, out _netData))
                {
                    Complete($"Road prefab '{prefab.name}' has no NetData.");
                    return;
                }

                _wayIndex = 0;
                _pointIndex = 0;
                _typeIndex = 0;
                _importedSegments = 0;
                _lastLoggedSegments = 0;
                _sessionId++;
                _applyWaitFrames = 0;
                _lastPendingDefinitions = -1;
                _lastWaitLogFrame = 0;
                _baselineEdgeCount = CountAllRoadEdges();
                using (var edgeArray = GetCurrentRoadEdges().ToEntityArray(Allocator.Temp))
                {
                    _baselineRoadEdges = edgeArray.ToHashSet();
                }
                _queuedSegmentInfos.Clear();
                _generationTypeOrder = BuildGenerationTypeOrder();
                _roadNamesApplied = false;
                Mod.log.Info($"Baseline road edge count before OSM road generation: {_baselineEdgeCount}");
                SetStatus(_generationTypeOrder.Count > 0
                    ? $"Generating \"{_generationTypeOrder[0]}\" roads (1/{_generationTypeOrder.Count})"
                    : "Generating selected OSM roads...");
                _loggedFirstQueuedSegment = false;
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete($"Failed to prepare road generation: {ex.Message}");
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
                    var typeWays = GetWaysForHighwayType(highwayType);
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
                        var typePrefab = GetPrefabForHighwayType(highwayType, way.Lanes);

                        while (_pointIndex < way.Points.Count - 1)
                        {
                            var segStart = way.Points[_pointIndex];
                            var segEnd = way.Points[_pointIndex + 1];
                            var terrainStart = _pointIndex < way.TerrainHeights.Count
                                ? way.TerrainHeights[_pointIndex] : segStart.y;
                            var terrainEnd = (_pointIndex + 1) < way.TerrainHeights.Count
                                ? way.TerrainHeights[_pointIndex + 1] : segEnd.y;
                            var created = CreateNetCourseDefinition(
                                segStart, segEnd, typePrefab, way.BaseElevationOffsetMeters);
                            _pointIndex++;
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
                                    Lanes = way.Lanes
                                });
                            }
                            segmentsThisFrame++;

                            if (segmentsThisFrame >= MaxSegmentsPerFrame)
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

                var currentTypeLabel = _typeIndex < _generationTypeOrder.Count
                    ? _generationTypeOrder[_typeIndex]
                    : string.Empty;
                SetStatus(!string.IsNullOrWhiteSpace(currentTypeLabel)
                    ? $"Generating \"{currentTypeLabel}\" roads ({Math.Min(_typeIndex + 1, _generationTypeOrder.Count)}/{_generationTypeOrder.Count})"
                    : "Generating selected OSM roads...");
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
                    _phase = WorkPhase.WaitForGameApply;
                    SetStatus($"Queued {_importedSegments} road definitions. Waiting for CS2 to generate roads...");
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete($"Failed to generate roads: {ex.Message}");
            }
        }

        private void WaitForGeneratedRoads()
        {
            try
            {
                _applyWaitFrames++;
                var pendingDefinitions = CountPendingDefinitions();
                var generatedRoadEdges = Math.Max(0, CountAllRoadEdges() - _baselineEdgeCount);
                var consumedDefinitions = Math.Max(0, _importedSegments - pendingDefinitions);

                SetStatus(pendingDefinitions > 0
                    ? $"Waiting for CS2 road generation... {consumedDefinitions}/{_importedSegments} definitions consumed"
                    : $"CS2 consumed {_importedSegments} OSM definitions. Verifying generated roads...");

                if (pendingDefinitions != _lastPendingDefinitions && _applyWaitFrames - _lastWaitLogFrame >= 300)
                {
                    _lastPendingDefinitions = pendingDefinitions;
                    _lastWaitLogFrame = _applyWaitFrames;
                    Mod.log.Info($"OSM road generation wait: {consumedDefinitions}/{_importedSegments} definitions consumed, {generatedRoadEdges} new road edges detected");
                }

                if (_applyWaitFrames >= 30 && (pendingDefinitions == 0 || generatedRoadEdges > 0))
                {
                    if (pendingDefinitions > 0)
                    {
                    Mod.log.Info($"CS2 generated {generatedRoadEdges} road edges but {pendingDefinitions} definitions remain pending. Proceeding with road names.");
                    }
                    ApplyQueuedRoadNames();
                    var uniqueNames = _queuedSegmentInfos
                        .Select(i => i.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n)
                        .ToList();
                    var namesPreview = uniqueNames.Count > 0
                        ? string.Join(", ", uniqueNames.Take(50).Select(n => $"\"{EscapeJson(n)}\""))
                        : "(none)";
                    var message = generatedRoadEdges > 0
                        ? $"CS2 consumed {_importedSegments} road definitions; detected {generatedRoadEdges} new road edges. Import bounds: {ShortBounds(_document)}. Names: {namesPreview}"
                        : $"CS2 consumed {_importedSegments} road definitions. Generated road count could not be verified. Names: {namesPreview}";

                    Mod.log.Info($"{message} Session {_sessionId}");
                    HasGeneratedAssets = true;
                    Complete(message);
                    return;
                }

                if (_applyWaitFrames >= MaxApplyWaitFrames)
                {
                    var message = pendingDefinitions > 0
                        ? $"Queued {_importedSegments} road definitions; CS2 consumed {consumedDefinitions} so far. Leave the editor running or try fewer road types."
                        : $"CS2 consumed {_importedSegments} road definitions. Generated road count could not be verified.";

                    Mod.log.Info($"{message} Session {_sessionId}, pending definitions {pendingDefinitions}");
                    HasGeneratedAssets = true;
                    Complete(message);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete($"Failed while waiting for generated roads: {ex.Message}");
            }
        }

        private void DeleteRoads()
        {
            try
            {
                var deletedEdges = MarkRoadEdgesDeleted();
                var deletedNodes = MarkRoadNodesDeleted();
                HasGeneratedAssets = false;
                Complete($"Marked {deletedEdges} road edges and {deletedNodes} road nodes for deletion.");
                Mod.log.Info(Status);
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                Complete($"Failed to delete roads: {ex.Message}");
            }
        }

        private void Complete(string message)
        {
            SetStatus(message);
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

        private void ApplyQueuedRoadNames()
        {
            if (_roadNamesApplied || _nameSystem == null || _queuedSegmentInfos.Count == 0)
                return;

            using var edgeArray = GetCurrentRoadEdges().ToEntityArray(Allocator.Temp);
            if (edgeArray.Length == 0)
                return;

            var newEdges = new List<Entity>();
            foreach (var entity in edgeArray)
            {
                if (!_baselineRoadEdges.Contains(entity))
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
                var cellKey = SpatialCellKey(_queuedSegmentInfos[i].MidPoint, cellSize);
                spatialIndex.Add(cellKey, i);
            }

            var matchedSegments = new NativeParallelHashSet<int>(_queuedSegmentInfos.Count, Allocator.Temp);
            var namedTargets = new HashSet<Entity>();
            var namedCount = 0;
            var proxCheckCount = 0;
            var proxMissCount = 0;
            var dirMismatchCount = 0;

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

                var cellKey = SpatialCellKey(edgeMid, cellSize);
                var bestDist = float.MaxValue;
                var bestIndex = -1;

                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var neighborPos = edgeMid + new float3(dx * cellSize, 0f, dz * cellSize);
                        var neighborKey = SpatialCellKey(neighborPos, cellSize);
                        if (spatialIndex.TryGetFirstValue(neighborKey, out var segIndex, out var iterator))
                        {
                            do
                            {
                                if (matchedSegments.Contains(segIndex))
                                    continue;

                                var queuedInfo = _queuedSegmentInfos[segIndex];
                                var queuedDir = math.normalizesafe(
                                    queuedInfo.EndPos - queuedInfo.StartPos, new float3(0f, 0f, 1f));
                                var dirDot = math.abs(math.dot(edgeDir, queuedDir));

                                const float minDirDot = 0.5f;
                                if (dirDot < minDirDot)
                                {
                                    dirMismatchCount++;
                                    continue;
                                }

                                var dist = math.distancesq(queuedInfo.MidPoint, edgeMid);
                                if (dist < bestDist)
                                {
                                    bestDist = dist;
                                    bestIndex = segIndex;
                                }
                            }
                            while (spatialIndex.TryGetNextValue(out segIndex, ref iterator));
                        }
                    }
                }

                if (bestIndex < 0)
                {
                    proxMissCount++;
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
            }

            _roadNamesApplied = true;
            Mod.log.Info(
                $"Deferred {proxCheckCount} road names for SetCustomName on aggregates (UIUpdate). " +
                $"{namedCount} total edge matches, {proxMissCount} unmatched edges, " +
                $"{dirMismatchCount} direction mismatches skipped. " +
                $"{newEdges.Count} new edges, {_queuedSegmentInfos.Count} queued segments.");

            spatialIndex.Dispose();
            matchedSegments.Dispose();
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
                return "Validation: names not yet applied.";

            var edgeArray = GetCurrentRoadEdges().ToEntityArray(Allocator.Temp);
            var newEdges = new List<Entity>();
            foreach (var entity in edgeArray)
            {
                if (!_baselineRoadEdges.Contains(entity))
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
            var baselineEdgeCount = _baselineRoadEdges.Count;

            var result = $"Validation: {totalNewEdges} new edges ({baselineEdgeCount} baseline), " +
                         $"{namedCount} named, {unnamedCount} unnamed. " +
                         $"Queued: {queuedTotal} total, {queuedNonEmpty} non-empty. " +
                         $"Samples: {sampleStr}";
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

        private Entity GetPrefabForHighwayType(string highwayType, int lanes)
        {
            if (string.IsNullOrWhiteSpace(highwayType))
                return _prefabEntity;

            if (lanes > 0)
            {
                var laneKey = $"{highwayType}:{lanes}";
                if (_laneSpecificPrefabEntities.TryGetValue(laneKey, out var laneCached))
                    return laneCached;

                var laneMatch = TryFindPrefabForHighwayType(highwayType, lanes);
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

            if (_highwayPrefabEntities.TryGetValue(highwayType, out var cached))
                return cached;

            var typeMatch = TryFindPrefabForHighwayType(highwayType, 0);
            if (typeMatch != Entity.Null)
            {
                _highwayPrefabEntities[highwayType] = typeMatch;
                return typeMatch;
            }

            _highwayPrefabEntities[highwayType] = _prefabEntity;
            _highwayPrefabNames[highwayType] = _prefabName;
            Mod.log.Warn($"No dedicated prefab found for highway type '{highwayType}'. Falling back to '{_prefabName}'.");
            return _prefabEntity;
        }

        private Entity TryFindPrefabForHighwayType(string highwayType, int lanes)
        {
            var candidates = GetRoadPrefabCandidates().ToList();
            var preferredNames = GetPreferredPrefabNames(highwayType);

            foreach (var preferredName in preferredNames)
            {
                var match = candidates.FirstOrDefault(p =>
                    IsSuitableRoadPrefabName(p.name, highwayType) &&
                    p.name.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (lanes <= 0 || ParsePrefabLaneCount(p.name) == lanes));
                if (match != null && _prefabSystem.TryGetEntity(match, out var entity))
                {
                    if (lanes <= 0)
                        _highwayPrefabNames[highwayType] = match.name;
                    return entity;
                }
            }

            return Entity.Null;
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
                return "none";

            var groups = _queuedSegmentInfos
                .GroupBy(info => info.Lanes)
                .OrderBy(g => g.Key <= 0 ? int.MaxValue : g.Key)
                .Select(g => g.Key > 0 ? $"{g.Key}-lane: {g.Count()}" : $"unknown: {g.Count()}");

            return string.Join(", ", groups);
        }

        private IEnumerable<NetPrefab> GetRoadPrefabCandidates()
        {
            if (_prefabSystem == null)
                return Enumerable.Empty<NetPrefab>();

            using var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetData>());

            using var prefabs = query.ToEntityArray(Allocator.Temp);
            return prefabs
                .Select(e => _prefabSystem.GetPrefab<NetPrefab>(e))
                .Where(p => p != null && IsSuitableRoadPrefabName(p.name))
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string[] GetPreferredPrefabNames(string highwayType)
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
                    return new[] { "Road", "Avenue", "Street" };
                case "secondary":
                case "secondary_link":
                    return new[] { "Road", "Avenue" };
                case "tertiary":
                case "tertiary_link":
                    return new[] { "Street", "Road" };
                case "unclassified":
                case "residential":
                case "living_street":
                case "road":
                    return new[] { "Street", "Road" };
                case "service":
                    return new[] { "Service", "Street", "Road" };
                default:
                    return new[] { "Street", "Road" };
            }
        }

        private static bool IsSuitableRoadPrefabName(string prefabName, string highwayType)
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

            if (highwayType.Equals("service", StringComparison.OrdinalIgnoreCase) &&
                (lower.Contains("highway") || lower.Contains("freeway")))
            {
                return false;
            }

            return true;
        }

        private static bool IsSuitableRoadPrefabName(string prefabName) =>
            IsSuitableRoadPrefabName(prefabName, string.Empty);

        private string DescribePrefab(Entity prefabEntity)
        {
            if (_prefabSystem == null)
                return $"{prefabEntity.Index}:{prefabEntity.Version}";

            if (_prefabSystem.TryGetPrefab<NetPrefab>(prefabEntity, out var prefab) && prefab != null)
                return prefab.name;

            return $"{prefabEntity.Index}:{prefabEntity.Version}";
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

        private List<OsmWay> GetWaysForHighwayType(string highwayType)
        {
            if (_document == null || _document.Ways.Count == 0)
                return new List<OsmWay>();

            return _document.Ways
                .Where(way => string.Equals(way.Highway, highwayType, StringComparison.OrdinalIgnoreCase))
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

        private int CountAllRoadEdges()
        {
            using var entities = GetCurrentRoadEdges().ToEntityArray(Allocator.Temp);

            return entities.Length;
        }

        private EntityQuery GetCurrentRoadEdges()
        {
            return GetEntityQuery(
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Road>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());
        }

        private int MarkRoadEdgesDeleted()
        {
            using var entities = GetEntityQuery(
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Road>(),
                ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp);

            foreach (var entity in entities)
            {
                EntityManager.AddComponentData(entity, new Deleted());
                EntityManager.AddComponentData(entity, new Updated());
            }

            return entities.Length;
        }

        private int MarkRoadNodesDeleted()
        {
            using var entities = GetEntityQuery(
                ComponentType.ReadOnly<Node>(),
                ComponentType.ReadOnly<Road>(),
                ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp);

            foreach (var entity in entities)
            {
                EntityManager.AddComponentData(entity, new Deleted());
                EntityManager.AddComponentData(entity, new Updated());
            }

            return entities.Length;
        }

        private bool TryGetRoadPrefab(string name, out NetPrefab prefab, out Entity entity)
        {
            using var entities = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<RoadData>(),
                ComponentType.ReadOnly<NetData>()).ToEntityArray(Allocator.Temp);

            var candidates = entities
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

        private string DocumentBoundsMessage() => $"Projected import bounds: {ShortBounds(_document)}";

        private static string ShortBounds(OsmDocument document)
        {
            if (document == null || !document.HasBounds)
                return "unknown";

            var size = document.Max - document.Min;
            return $"{size.x:F0}m x {size.z:F0}m, y={document.ElevationMeters:F1}m";
        }

        private static string SelectedTypesLabel(OsmGenerateOptions options)
        {
            return options == null || options.HighwayTypes.Count == 0
                ? "all loaded types"
                : string.Join(", ", options.HighwayTypes.OrderBy(t => t));
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
