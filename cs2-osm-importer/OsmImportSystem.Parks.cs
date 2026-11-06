using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;
using ObjectTransform = Game.Objects.Transform;

namespace cs2_osm_importer
{
    /// <summary>
    /// OSM parks as parks the city's systems use (verified in game, 1.6.2f1):
    ///  - grass in the exact outline (shape only; nothing in the simulation reads a surface),
    ///  - vanilla park buildings (CityPark / PocketPark) along the park's streets: they carry the park, leisure and
    ///    attraction data and need park maintenance. A building only gets access from a road within 8.4 m of the middle
    ///    of its front edge (footpaths never give access), so they face the streets bounding the park,
    ///  - trees in the woods mapped inside the park, NOT owned by anything: only unowned trees count for forest
    ///    ambience, which raises land value nearby.
    /// The interior of a large park stays open ground with its OSM trails.
    /// </summary>
    public partial class OsmImportSystem
    {
        private const string ParkSurfacePrefabName = "Grass Surface 01";
        private static readonly string[] ParkBuildingPrefabNames =
        {
            "CityPark01", "CityPark02", "CityPark03", "CityPark04", "CityPark05", "CityPark06", "CityPark07",
            "CityPark08", "CityPark09", "CityPark10", "CityPark11",
            "PocketPark01", "PocketPark02", "PocketPark03", "PocketPark04", "PocketPark05", "PocketPark06",
            "PocketPark07", "PocketPark08", "PocketPark09", "PocketPark10",
        };
        private static readonly string[] PlaygroundPrefabNames = { "Playground01", "Playground02", "Playground03", "Playground04", "Playground05" };
        private static readonly string[] DogParkPrefabNames = { "DogPark01", "DogPark02" };
        private const float ParkFeatureMaxMove = 150f;
        private readonly List<Entity> _playgroundPrefabs = new List<Entity>();
        private readonly List<Entity> _dogParkPrefabs = new List<Entity>();
        private int _featuresInPlace, _featuresMoved, _featuresSkipped;
        private float _featuresMovedTotal, _featuresMovedMax;
        private static readonly string[] ParkTreePrefabNames =
            { "OakTree01", "BirchTree01", "NA_LindenTree01", "NA_LondonPlaneTree01", "NA_HickoryTree01", "PineTree01", "SpruceTree01" };
        private const float ParkSquareMetersPerBuilding = 6000f;
        private const int ParkMaxBuildings = 12;
        private const float ParkBuildingSpacing = 4f;
        private const float ParkFrontReach = 6f; // front middle to street edge (the game allows 8.4 m)
        private const float ParkTreeSpacing = 9f;
        private const int ParkMaxTrees = 20000;
        private const int ParkObjectsPerFrame = 250;

        private enum ParkStep { NotStarted, Queueing, WaitingForApply, Done }

        private ParkStep _parkStep = ParkStep.NotStarted;
        private int _parkWaitFrames;
        private readonly List<Entity> _parkDefinitions = new List<Entity>();
        private readonly List<(Entity Prefab, float3 Position, quaternion Rotation, float Age)> _pendingParkTrees = new List<(Entity, float3, quaternion, float)>();
        private int _pendingParkTreeIndex;
        private readonly List<Entity> _generatedParkEntities = new List<Entity>();
        private HashSet<Entity> _parkBaselineSurfaces = new HashSet<Entity>();
        private HashSet<Entity> _parkBaselineBuildings = new HashSet<Entity>();
        private HashSet<Entity> _parkBaselineTrees = new HashSet<Entity>();
        private Entity _parkSurfacePrefab;
        private readonly List<Entity> _parkBuildingPrefabs = new List<Entity>();
        private readonly List<Entity> _parkTreePrefabs = new List<Entity>();
        private int _queuedParkBuildings, _queuedParkSurfaces;

        // Water and holes of the park being placed: buildings and trees keep ParkWaterMargin clear of them.
        private static List<List<float2>> _currentParkObstacles = new List<List<float2>>();
        private const float ParkWaterMargin = 3f;

        private static bool OnObstacle(float2 p, float margin)
        {
            foreach (var o in _currentParkObstacles)
            {
                if (ParkingGeometry.Contains(o, p) || ParkingGeometry.DistanceToRing(o, p) < margin)
                    return true;
            }
            return false;
        }

        private void ResetParkState()
        {
            _parkStep = ParkStep.NotStarted;
            _parkWaitFrames = 0;
            _parkDefinitions.Clear();
            _pendingParkTrees.Clear();
            _pendingParkTreeIndex = 0;
        }

        private IEnumerable<Entity> TakeGeneratedParks()
        {
            var entities = _generatedParkEntities.Where(e => e != Entity.Null && EntityManager.Exists(e)).ToList();
            _generatedParkEntities.Clear();
            return entities;
        }

        /// <summary>One step per call. True while the park work needs more frames.</summary>
        private bool ProcessParkStep()
        {
            try
            {
                switch (_parkStep)
                {
                    case ParkStep.NotStarted:
                        _parkStep = ParkStep.Done;
                        if (!(_activeGenerateOptions?.GenerateParks ?? false) || _document == null || _document.Parks.Count == 0)
                            return false;
                        if (!QueueParks())
                            return false;
                        _parkStep = ParkStep.Queueing;
                        SetStatus(LF("OsmImporter.Status.BuildingParks", "Building {0} parks...", _document.Parks.Count));
                        return true;

                    case ParkStep.Queueing:
                        // Trees in batches: one huge batch of objects makes the game crawl while it applies them.
                        var end = Math.Min(_pendingParkTrees.Count, _pendingParkTreeIndex + ParkObjectsPerFrame);
                        for (; _pendingParkTreeIndex < end; _pendingParkTreeIndex++)
                        {
                            var t = _pendingParkTrees[_pendingParkTreeIndex];
                            QueueParkTree(t.Prefab, t.Position, t.Rotation, t.Age);
                        }
                        if (_pendingParkTreeIndex >= _pendingParkTrees.Count)
                        {
                            _parkStep = ParkStep.WaitingForApply;
                            _parkWaitFrames = 0;
                        }
                        return true;

                    case ParkStep.WaitingForApply:
                        _parkWaitFrames++;
                        if (_parkWaitFrames % 60 == 1)
                            LogPendingDefinitions("parks", _parkDefinitions, _parkWaitFrames);
                        if (_parkDefinitions.Any(e => EntityManager.Exists(e) && EntityManager.HasComponent<CreationDefinition>(e)) && _parkWaitFrames < MaxApplyWaitFrames)
                            return true;
                        TrackGeneratedParks();
                        _parkStep = ParkStep.Done;
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                _parkStep = ParkStep.Done;
                return false;
            }
        }

        private bool QueueParks()
        {
            _parkSurfacePrefab = FindPrefabByName(ParkSurfacePrefabName);
            _parkBuildingPrefabs.Clear();
            foreach (var name in ParkBuildingPrefabNames)
            {
                var p = FindPrefabByName(name);
                if (p != Entity.Null && EntityManager.HasComponent<ObjectGeometryData>(p) && EntityManager.HasComponent<ParkData>(p))
                    _parkBuildingPrefabs.Add(p);
            }
            LoadPrefabs(PlaygroundPrefabNames, _playgroundPrefabs);
            LoadPrefabs(DogParkPrefabNames, _dogParkPrefabs);
            _parkTreePrefabs.Clear();
            foreach (var name in ParkTreePrefabNames)
            {
                var p = FindPrefabByName(name);
                if (p != Entity.Null)
                    _parkTreePrefabs.Add(p);
            }
            if (_parkSurfacePrefab == Entity.Null)
            {
                Mod.log.Warn("Parks skipped: grass surface prefab missing.");
                return false;
            }

            _parkBaselineSurfaces = QueryEntitiesWithPrefab(_parkSurfacePrefab, ComponentType.ReadOnly<AreaNode>());
            _parkBaselineBuildings = new HashSet<Entity>();
            foreach (var p in _parkBuildingPrefabs.Concat(_playgroundPrefabs).Concat(_dogParkPrefabs))
                _parkBaselineBuildings.UnionWith(QueryEntitiesWithPrefab(p, ComponentType.ReadOnly<ObjectTransform>()));
            _parkBaselineTrees = new HashSet<Entity>();
            foreach (var p in _parkTreePrefabs)
                _parkBaselineTrees.UnionWith(QueryEntitiesWithPrefab(p, ComponentType.ReadOnly<ObjectTransform>()));

            var streets = new ParkingStreetIndex(this);
            var paths = new ParkNetIndex(this);
            var random = new Unity.Mathematics.Random(0x5eed1234u);
            _queuedParkBuildings = 0;
            _featuresInPlace = _featuresMoved = _featuresSkipped = 0;
            _featuresMovedTotal = _featuresMovedMax = 0f;
            _queuedParkSurfaces = 0;
            var parksWithBuildings = 0;
            foreach (var park in _document.Parks)
            {
                QueueParkSurface(park.Ring);
                _currentParkObstacles = park.Obstacles;
                var footprints = new List<(float2 Center, float2 Front, float2 Size)>();
                var features = PlaceParkFeatures(park, streets, paths, footprints, ref random);
                var placed = features + PlaceParkBuildings(park, streets, paths, footprints, ref random, features);
                if (placed > 0)
                    parksWithBuildings++;
                PlanParkTrees(park, streets, paths, footprints, ref random);
            }

            var movedAverage = _featuresMoved > 0 ? _featuresMovedTotal / _featuresMoved : 0f;
            Mod.log.Info($"Mapped playgrounds and dog parks: {_featuresInPlace} built where mapped, {_featuresMoved} moved to the park's nearest street side " +
                         $"(average {movedAverage:F0} m, at most {_featuresMovedMax:F0} m), {_featuresSkipped} not built (no street side within {ParkFeatureMaxMove:F0} m a vanilla one fits on).");
            Mod.log.Info($"Queued {_document.Parks.Count} parks: {_queuedParkSurfaces} grass areas, {_queuedParkBuildings} park buildings in " +
                         $"{parksWithBuildings} parks (the rest have no street side a park building fits on), {_pendingParkTrees.Count} trees in mapped woods.");
            return true;
        }

        private void QueueParkSurface(List<float2> ring)
        {
            var entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition { m_Prefab = _parkSurfacePrefab, m_Flags = CreationFlags.Permanent });
            EntityManager.AddComponentData(entity, new Updated());
            EntityManager.AddComponentData(entity, new OsmImportDefinition { SessionId = _sessionId });
            var nodes = EntityManager.AddBuffer<AreaNode>(entity);
            foreach (var p in ring)
                nodes.Add(new AreaNode(Lift(p), float.MinValue));
            nodes.Add(nodes[0]);
            _parkDefinitions.Add(entity);
            _queuedParkSurfaces++;
        }

        /// <summary>
        /// Walks the outline's sides that run along a street and packs park buildings there, front on the street,
        /// inside the outline, off streets, paths and each other. The number scales with the park's area.
        /// </summary>
        private int PlaceParkBuildings(OsmParkArea park, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> footprints, ref Unity.Mathematics.Random random, int alreadyPlaced)
        {
            if (_parkBuildingPrefabs.Count == 0)
                return 0;
            // Mapped playgrounds and dog parks count towards the budget; a standalone dog park gets nothing else.
            if (park.IsStandaloneFeature)
                return 0;
            var budget = (int)math.clamp(math.round(park.AreaSquareMeters / ParkSquareMetersPerBuilding), 1f, ParkMaxBuildings) - alreadyPlaced;
            if (budget <= 0)
                return 0;
            var ring = park.Ring;
            // Bigger parks get the bigger buildings first; small ones the small.
            var bySize = _parkBuildingPrefabs
                .Select(p => (Prefab: p, Size: EntityManager.GetComponentData<ObjectGeometryData>(p).m_Size.xz))
                .OrderByDescending(p => p.Size.x * p.Size.y)
                .ToList();
            var budgetArea = park.AreaSquareMeters / budget;
            var preferred = bySize.Where(p => p.Size.x * p.Size.y <= budgetArea * 0.6f).ToList();
            if (preferred.Count == 0)
                preferred = bySize.OrderBy(p => p.Size.x * p.Size.y).ToList();

            var placed = 0;
            // Longest sides first, so the main streets get the buildings.
            var sides = Enumerable.Range(0, ring.Count)
                .Select(i => (A: ring[i], B: ring[(i + 1) % ring.Count]))
                .OrderByDescending(s => math.distance(s.A, s.B))
                .ToList();
            var start = random.NextInt(preferred.Count);
            foreach (var side in sides)
            {
                if (placed >= budget)
                    break;
                var length = math.distance(side.A, side.B);
                if (length < 10f)
                    continue;
                var dir = (side.B - side.A) / length;
                var front = new float2(dir.y, -dir.x); // outward: the ring is counter-clockwise
                for (var s = 2f; s < length - 6f && placed < budget;)
                {
                    var advanced = false;
                    for (var k = 0; k < preferred.Count; k++)
                    {
                        var (prefab, size) = preferred[(start + k) % preferred.Count];
                        if (s + size.x > length - 1f)
                            continue;
                        var edgePoint = side.A + dir * (s + size.x * 0.5f);
                        if (streets.EntranceRoadGap(edgePoint) > ParkFrontReach)
                            continue;
                        if (!TryFitBehindEdge(ring, edgePoint, front, size, streets, paths, footprints, out var center))
                            continue;
                        var rotation = quaternion.RotateY(math.atan2(front.x, front.y));
                        QueueBuildingWithSubElements(prefab, Lift(center), rotation, random.NextInt(), _parkDefinitions);
                        footprints.Add((center, front, size));
                        _queuedParkBuildings++;
                        placed++;
                        start++;
                        s += size.x + ParkBuildingSpacing;
                        advanced = true;
                        break;
                    }
                    if (!advanced)
                        s += 4f;
                }
            }
            return placed;
        }

        private void LoadPrefabs(string[] names, List<Entity> into)
        {
            into.Clear();
            foreach (var name in names)
            {
                var p = FindPrefabByName(name);
                if (p != Entity.Null && EntityManager.HasComponent<ObjectGeometryData>(p))
                    into.Add(p);
            }
        }

        /// <summary>
        /// Each mapped playground / dog park becomes the vanilla prefab closest to its size: at the mapped spot when it
        /// can face a street there, otherwise on the park's street side nearest to it (up to ParkFeatureMaxMove away),
        /// otherwise not at all (a park building without road access is never used).
        /// </summary>
        private int PlaceParkFeatures(OsmParkArea park, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> footprints, ref Unity.Mathematics.Random random)
        {
            var placed = 0;
            foreach (var feature in park.Features)
            {
                var pool = feature.Kind == OsmParkFeatureKind.Playground ? _playgroundPrefabs : _dogParkPrefabs;
                if (pool.Count == 0)
                    continue;
                var mapped = math.max(50f, feature.AreaSquareMeters);
                var bySize = pool.Select(p => (Prefab: p, Size: EntityManager.GetComponentData<ObjectGeometryData>(p).m_Size.xz))
                    .OrderBy(p => math.abs(math.log(p.Size.x * p.Size.y / mapped)))
                    .ToList();
                // A standalone dog park's own outline may be smaller than the vanilla one: allow the footprint past it.
                var bounds = park.IsStandaloneFeature ? Expanded(park.Ring, 15f) : park.Ring;
                if (TryPlaceFeatureInPlace(bounds, feature, bySize, streets, paths, footprints, ref random)
                    || TryPlaceFeatureOnStreetSide(park.Ring, bounds, feature, bySize, streets, paths, footprints, ref random))
                {
                    placed++;
                    continue;
                }
                _featuresSkipped++;
                LogSkippedFeature(park, feature, bySize, streets, paths, footprints);
            }
            return placed;
        }

        /// <summary>
        /// DIAG: why a mapped playground / dog park got no building. Counts, over every 4 m spot along the outline, how
        /// far the nearest street-side spot is and what rules out the spots within reach.
        /// </summary>
        private void LogSkippedFeature(OsmParkArea park, OsmParkFeature feature, List<(Entity Prefab, float2 Size)> bySize, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> footprints)
        {
            var ring = park.Ring;
            var bounds = park.IsStandaloneFeature ? Expanded(park.Ring, 15f) : park.Ring;
            float nearestStreetSpot = float.MaxValue, nearestAnySpot = float.MaxValue;
            int spots = 0, outside = 0, onStreet = 0, onPath = 0, overlap = 0, nonEntrance = 0;
            var smallest = bySize.OrderBy(p => p.Size.x * p.Size.y).First();
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                var length = math.distance(a, b);
                if (length < 1f)
                    continue;
                var dir = (b - a) / length;
                var front = new float2(dir.y, -dir.x);
                for (var s = 2f; s <= length - 2f; s += 4f)
                {
                    var p = a + dir * s;
                    var d = math.distance(p, feature.Center);
                    nearestAnySpot = math.min(nearestAnySpot, d);
                    if (streets.EntranceRoadGap(p) > ParkFrontReach)
                        continue;
                    nearestStreetSpot = math.min(nearestStreetSpot, d);
                    if (d > ParkFeatureMaxMove)
                        continue;
                    spots++;
                    if (streets.NonEntranceRoadNear(p, 8.4f)) { nonEntrance++; continue; }
                    var center = p - front * (smallest.Size.y * 0.5f + 0.5f);
                    switch (FootprintProblem(bounds, center, front, smallest.Size, streets, paths, footprints))
                    {
                        case 1: outside++; break;
                        case 2: onStreet++; break;
                        case 3: onPath++; break;
                        case 4: overlap++; break;
                    }
                }
            }
            var name = string.IsNullOrEmpty(park.Name) ? $"park {park.Id}" : park.Name;
            Mod.log.Info($"DIAG {feature.Kind} {feature.Id} in {name} ({feature.AreaSquareMeters:F0} m2, {park.AreaSquareMeters / 10000f:F1} ha park) not built: " +
                         $"nearest outline spot {nearestAnySpot:F0} m, nearest street-side spot {(nearestStreetSpot == float.MaxValue ? "none" : nearestStreetSpot.ToString("F0") + " m")}; " +
                         $"{spots} street-side spots within {ParkFeatureMaxMove:F0} m, smallest {_prefabSystem.GetPrefabName(smallest.Prefab)} ({smallest.Size.x:F0}x{smallest.Size.y:F0} m) ruled out by: " +
                         $"outside the park {outside}, on a street {onStreet}, on a footpath {onPath}, overlapping a placed building {overlap}, pedestrian street/highway in reach {nonEntrance}.");
        }

        /// <summary>0 = fits; 1 outside the outline, 2 on a street, 3 on a path, 4 overlapping a placed footprint.</summary>
        private static int FootprintProblem(List<float2> ring, float2 center, float2 front, float2 size, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> placed)
        {
            var side = new float2(front.y, -front.x);
            for (var w = -size.x * 0.5f; w <= size.x * 0.5f + 0.01f; w += math.max(1f, math.min(3f, size.x * 0.5f)))
            for (var d = -size.y * 0.5f; d <= size.y * 0.5f + 0.01f; d += math.max(1f, math.min(3f, size.y * 0.5f)))
            {
                var p = center + side * w + front * d;
                if (!ParkingGeometry.Contains(ring, p) && ParkingGeometry.DistanceToRing(ring, p) > 1f)
                    return 1;
                if (OnObstacle(p, ParkWaterMargin))
                    return 1;
                if (streets.IsNearStreet(p))
                    return 2;
                if (paths.IsNear(p, 0.5f))
                    return 3;
                foreach (var o in placed)
                {
                    var rel = p - o.Center;
                    var oSide = new float2(o.Front.y, -o.Front.x);
                    if (math.abs(math.dot(rel, oSide)) < o.Size.x * 0.5f + 1f && math.abs(math.dot(rel, o.Front)) < o.Size.y * 0.5f + 1f)
                        return 4;
                }
            }
            return 0;
        }

        private static List<float2> Expanded(List<float2> ring, float by)
        {
            var c = ParkingGeometry.Centroid(ring);
            return ring.Select(p => p + math.normalizesafe(p - c) * by).ToList();
        }

        private bool TryPlaceFeatureInPlace(List<float2> bounds, OsmParkFeature feature, List<(Entity Prefab, float2 Size)> bySize, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> footprints, ref Unity.Mathematics.Random random)
        {
            var rect = ParkingGeometry.MinAreaRectangle(feature.Ring);
            foreach (var (front, _, _) in streets.FindFrontages(rect))
            {
                foreach (var (prefab, size) in bySize)
                {
                    // At the mapped spot, nudged at most 10 m towards the street so its front reaches it.
                    var center = feature.Center;
                    var frontMiddle = center + front * (size.y * 0.5f);
                    var reach = streets.EntranceRoadGap(frontMiddle);
                    if (reach > ParkFrontReach)
                    {
                        var nudge = reach - ParkFrontReach + 1f;
                        if (nudge > 10f)
                            continue;
                        center += front * nudge;
                        frontMiddle = center + front * (size.y * 0.5f);
                        if (streets.EntranceRoadGap(frontMiddle) > ParkFrontReach)
                            continue;
                    }
                    if (streets.NonEntranceRoadNear(frontMiddle, 8.4f) || !FootprintFits(bounds, center, front, size, streets, paths, footprints))
                        continue;
                    QueueParkFeatureBuilding(prefab, center, front, size, footprints, ref random);
                    _featuresInPlace++;
                    return true;
                }
            }
            return false;
        }

        private bool TryPlaceFeatureOnStreetSide(List<float2> ring, List<float2> bounds, OsmParkFeature feature, List<(Entity Prefab, float2 Size)> bySize, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> footprints, ref Unity.Mathematics.Random random)
        {
            // Candidate front-middle points every 4 m along the outline, nearest to the mapped spot first.
            var candidates = new List<(float2 Point, float2 Front, float Distance)>();
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                var length = math.distance(a, b);
                if (length < 1f)
                    continue;
                var dir = (b - a) / length;
                var front = new float2(dir.y, -dir.x);
                for (var s = 2f; s <= length - 2f; s += 4f)
                {
                    var p = a + dir * s;
                    var d = math.distance(p, feature.Center);
                    if (d <= ParkFeatureMaxMove && streets.EntranceRoadGap(p) <= ParkFrontReach)
                        candidates.Add((p, front, d));
                }
            }
            foreach (var c in candidates.OrderBy(c => c.Distance))
            {
                foreach (var (prefab, size) in bySize)
                {
                    if (!TryFitBehindEdge(bounds, c.Point, c.Front, size, streets, paths, footprints, out var center))
                        continue;
                    QueueParkFeatureBuilding(prefab, center, c.Front, size, footprints, ref random);
                    _featuresMoved++;
                    _featuresMovedTotal += c.Distance;
                    _featuresMovedMax = math.max(_featuresMovedMax, c.Distance);
                    return true;
                }
            }
            return false;
        }

        private void QueueParkFeatureBuilding(Entity prefab, float2 center, float2 front, float2 size, List<(float2 Center, float2 Front, float2 Size)> footprints, ref Unity.Mathematics.Random random)
        {
            QueueBuildingWithSubElements(prefab, Lift(center), quaternion.RotateY(math.atan2(front.x, front.y)), random.NextInt(), _parkDefinitions);
            footprints.Add((center, front, size));
            _queuedParkBuildings++;
        }

        // The game links a building to a road within 8.4 m of the middle of its front edge; keep a little margin.
        private const float ParkFrontMaxReach = 7.5f;
        private static readonly float[] ParkSetbacks = { 0f, 1.5f, 3f, 4.5f, 6f };

        /// <summary>
        /// A footprint with its front on the park edge at edgePoint, stepped back from the edge (behind a sidewalk or
        /// footpath along it) as long as its front middle stays within the game's reach of the street, and never where
        /// a pedestrian street or highway would take the connection.
        /// </summary>
        private static bool TryFitBehindEdge(List<float2> ring, float2 edgePoint, float2 front, float2 size, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> footprints, out float2 center)
        {
            foreach (var setback in ParkSetbacks)
            {
                center = edgePoint - front * (size.y * 0.5f + 0.5f + setback);
                var frontMiddle = center + front * (size.y * 0.5f);
                if (streets.EntranceRoadGap(frontMiddle) > ParkFrontMaxReach)
                    break; // further back only gets further from the street
                if (streets.NonEntranceRoadNear(frontMiddle, 8.4f))
                    continue;
                if (FootprintFits(ring, center, front, size, streets, paths, footprints))
                    return true;
            }
            center = default;
            return false;
        }

        /// <summary>Footprint sampled on a 3 m grid: inside the outline, off streets and paths, clear of placed ones.</summary>
        private static bool FootprintFits(List<float2> ring, float2 center, float2 front, float2 size, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> placed)
        {
            var side = new float2(front.y, -front.x);
            for (var w = -size.x * 0.5f; w <= size.x * 0.5f + 0.01f; w += math.max(1f, math.min(3f, size.x * 0.5f)))
            for (var d = -size.y * 0.5f; d <= size.y * 0.5f + 0.01f; d += math.max(1f, math.min(3f, size.y * 0.5f)))
            {
                var p = center + side * w + front * d;
                if (!ParkingGeometry.Contains(ring, p) && ParkingGeometry.DistanceToRing(ring, p) > 1f)
                    return false;
                if (OnObstacle(p, ParkWaterMargin))
                    return false;
                if (streets.IsNearStreet(p) || paths.IsNear(p, 0.5f))
                    return false;
                foreach (var o in placed)
                {
                    var rel = p - o.Center;
                    var oSide = new float2(o.Front.y, -o.Front.x);
                    if (math.abs(math.dot(rel, oSide)) < o.Size.x * 0.5f + 1f && math.abs(math.dot(rel, o.Front)) < o.Size.y * 0.5f + 1f)
                        return false;
                }
            }
            return true;
        }

        /// <summary>Jittered grid of trees in the woods mapped inside the park, clear of streets, paths and buildings.</summary>
        private void PlanParkTrees(OsmParkArea park, ParkingStreetIndex streets, ParkNetIndex paths, List<(float2 Center, float2 Front, float2 Size)> footprints, ref Unity.Mathematics.Random random)
        {
            if (_parkTreePrefabs.Count == 0)
                return;
            foreach (var wood in park.Woods)
            {
                var b = ParkingGeometry.Bounds(wood);
                for (var x = b.x + ParkTreeSpacing * 0.5f; x < b.z; x += ParkTreeSpacing)
                for (var z = b.y + ParkTreeSpacing * 0.5f; z < b.w; z += ParkTreeSpacing)
                {
                    if (_pendingParkTrees.Count >= ParkMaxTrees)
                        return;
                    var p = new float2(x, z) + random.NextFloat2(-0.35f, 0.35f) * ParkTreeSpacing;
                    if (!ParkingGeometry.Contains(wood, p) || !ParkingGeometry.Contains(park.Ring, p) || OnObstacle(p, 1f))
                        continue;
                    if (streets.IsNearStreet(p) || paths.IsNear(p, 2.5f))
                        continue;
                    if (footprints.Any(o =>
                        {
                            var rel = p - o.Center;
                            var oSide = new float2(o.Front.y, -o.Front.x);
                            return math.abs(math.dot(rel, oSide)) < o.Size.x * 0.5f + 2f && math.abs(math.dot(rel, o.Front)) < o.Size.y * 0.5f + 2f;
                        }))
                        continue;
                    var prefab = _parkTreePrefabs[random.NextInt(_parkTreePrefabs.Count)];
                    _pendingParkTrees.Add((prefab, Lift(p), quaternion.RotateY(random.NextFloat(0f, 2f * math.PI)), random.NextBool() ? 0.425f : 0.775f));
                }
            }
        }

        private void QueueParkTree(Entity prefab, float3 position, quaternion rotation, float age)
        {
            var entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition
            {
                m_Prefab = prefab,
                m_Flags = CreationFlags.Permanent,
                m_RandomSeed = ImportedHash(position, position + new float3(0f, 0f, 1f))
            });
            EntityManager.AddComponentData(entity, new ObjectDefinition
            {
                m_Position = position,
                m_LocalPosition = position,
                m_Rotation = rotation,
                m_LocalRotation = rotation,
                m_Scale = new float3(1f),
                m_Intensity = 1f,
                m_Age = age,
                m_Probability = 100,
                m_ParentMesh = -1,
                m_PrefabSubIndex = -1
            });
            EntityManager.AddComponentData(entity, new Updated());
            EntityManager.AddComponentData(entity, new OsmImportDefinition { SessionId = _sessionId });
            _parkDefinitions.Add(entity);
        }

        /// <summary>Everything the parks created, for Delete Generated (buildings take their own sub-elements with them).</summary>
        private void TrackGeneratedParks()
        {
            int surfaces = 0, buildings = 0, trees = 0;
            foreach (var e in QueryEntitiesWithPrefab(_parkSurfacePrefab, ComponentType.ReadOnly<AreaNode>()))
                if (!_parkBaselineSurfaces.Contains(e) && !EntityManager.HasComponent<Owner>(e)) { _generatedParkEntities.Add(e); surfaces++; }
            foreach (var p in _parkBuildingPrefabs.Concat(_playgroundPrefabs).Concat(_dogParkPrefabs))
                foreach (var e in QueryEntitiesWithPrefab(p, ComponentType.ReadOnly<ObjectTransform>()))
                    if (!_parkBaselineBuildings.Contains(e) && !EntityManager.HasComponent<Owner>(e)) { _generatedParkEntities.Add(e); buildings++; }
            foreach (var p in _parkTreePrefabs)
                foreach (var e in QueryEntitiesWithPrefab(p, ComponentType.ReadOnly<ObjectTransform>()))
                    if (!_parkBaselineTrees.Contains(e) && !EntityManager.HasComponent<Owner>(e)) { _generatedParkEntities.Add(e); trees++; }
            HasGeneratedAssets |= surfaces + buildings + trees > 0;
            Mod.log.Info($"Parks built: {surfaces}/{_queuedParkSurfaces} grass areas, {buildings}/{_queuedParkBuildings} park buildings, {trees}/{_pendingParkTrees.Count} trees (after {_parkWaitFrames} frames).");
        }

        /// <summary>Unowned footpaths, cycleways and other pathways on a grid (park buildings and trees keep off them).</summary>
        private sealed class ParkNetIndex
        {
            private const float Cell = 32f;
            private readonly Dictionary<(int, int), List<(float2 A, float2 B, float Half)>> _cells = new Dictionary<(int, int), List<(float2, float2, float)>>();

            public ParkNetIndex(OsmImportSystem system)
            {
                var em = system.EntityManager;
                foreach (var e in system.QueryEntities(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>()))
                {
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (em.HasComponent<RoadData>(prefab))
                        continue; // streets are in the street index
                    if (!em.HasComponent<NetGeometryData>(prefab))
                        continue;
                    var half = em.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth * 0.5f;
                    var curve = em.GetComponentData<Curve>(e).m_Bezier;
                    var prev = curve.a.xz;
                    for (var i = 1; i <= 4; i++)
                    {
                        var next = MathUtils.Position(curve, i / 4f).xz;
                        var min = math.min(prev, next) - half;
                        var max = math.max(prev, next) + half;
                        for (var x = (int)math.floor(min.x / Cell); x <= (int)math.floor(max.x / Cell); x++)
                        for (var z = (int)math.floor(min.y / Cell); z <= (int)math.floor(max.y / Cell); z++)
                        {
                            if (!_cells.TryGetValue((x, z), out var list))
                                _cells[(x, z)] = list = new List<(float2, float2, float)>();
                            list.Add((prev, next, half));
                        }
                        prev = next;
                    }
                }
            }

            public bool IsNear(float2 p, float clearance)
            {
                if (!_cells.TryGetValue(((int)math.floor(p.x / Cell), (int)math.floor(p.y / Cell)), out var list))
                    return false;
                foreach (var s in list)
                    if (ParkingGeometry.DistanceToSegment(p, s.A, s.B) < s.Half + clearance)
                        return true;
                return false;
            }
        }
    }
}
