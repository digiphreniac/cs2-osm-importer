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
using NetNode = Game.Net.Node;
using SubNet = Game.Net.SubNet;
using SubObject = Game.Objects.SubObject;
using ObjectTransform = Game.Objects.Transform;

namespace cs2_osm_importer
{
    /// <summary>
    /// OSM surface parking lots as working CS2 lots built only from vanilla pieces (verified in game, 1.6.2f1):
    ///  - a pavement surface in the exact OSM outline,
    ///  - invisible two-way car paths for the aisles, joined to the nearest street (the entrance path ends just outside
    ///    the street's edge; the game connects it there by itself),
    ///  - ParkingLotDecal01 stall markings, whose prefab sub-lane is a real parking lane.
    /// The surface must OWN the paths (Owner + SubNet) and the stalls (Owner + SubObject): in the editor the game gives
    /// no lanes to unowned objects, and LaneConnectionSystem links a stall to a car lane by searching the sub-nets of
    /// its top owner. Ownership also makes the whole lot delete as one (deleting the surface cascades).
    /// </summary>
    public partial class OsmImportSystem
    {
        private const string ParkingSurfacePrefabName = "Pavement Surface 01";
        private const string ParkingPathPrefabName = "Invisible Road Path - 1xTwoway";
        private const string ParkingStallPrefabName = "ParkingLotDecal01";
        private const float ParkingEntranceSearchMeters = 40f;
        private const float ParkingStreetClearanceMeters = 0.5f;
        private const float ParkingStallScale = 0.9f;
        // Roads faster than this (highways) never get a lot entrance. Game units: RoadData.m_SpeedLimit is km/h / 1.8
        private const float ParkingMaxEntranceSpeed = 40f; // 72 km/h; Small Road = 22.2, Medium = 27.8, highways >= 44.4

        private enum ParkingStep { NotStarted, WaitingForApply, Done }

        private sealed class QueuedParkingLot
        {
            public OsmParkingArea Area;
            public List<(float2 A, float2 B)> Paths;
            public List<float2> Stalls;
            public Entity Surface;
        }

        private ParkingStep _parkingStep = ParkingStep.NotStarted;
        private int _parkingWaitFrames;
        private readonly List<QueuedParkingLot> _queuedParkingLots = new List<QueuedParkingLot>();
        private readonly List<Entity> _parkingDefinitions = new List<Entity>();
        private readonly List<ParkingStall> _pendingStalls = new List<ParkingStall>();
        private int _pendingStallIndex;
        private const int ParkingStallsPerFrame = 250;
        private readonly List<Entity> _generatedParkingSurfaces = new List<Entity>();
        private HashSet<Entity> _parkingBaselineSurfaces = new HashSet<Entity>();
        private HashSet<Entity> _parkingBaselinePaths = new HashSet<Entity>();
        private HashSet<Entity> _parkingBaselineStalls = new HashSet<Entity>();
        private Entity _parkingSurfacePrefab, _parkingPathPrefab, _parkingStallPrefab;

        private void ResetParkingLotState()
        {
            _parkingStep = ParkingStep.NotStarted;
            _parkingWaitFrames = 0;
            _queuedParkingLots.Clear();
            _parkingDefinitions.Clear();
            _pendingStalls.Clear();
            _pendingStallIndex = 0;
        }

        private IEnumerable<Entity> TakeGeneratedParkingLots()
        {
            var lots = _generatedParkingSurfaces.Where(e => e != Entity.Null && EntityManager.Exists(e)).ToList();
            _generatedParkingSurfaces.Clear();
            return lots;
        }

        /// <summary>One step per call. True while the parking work needs more frames.</summary>
        private bool ProcessParkingLotStep()
        {
            try
            {
                switch (_parkingStep)
                {
                    case ParkingStep.NotStarted:
                        _parkingStep = ParkingStep.Done;
                        if (!(_activeGenerateOptions?.GenerateParkingLots ?? false) || _document == null || _document.ParkingAreas.Count == 0)
                            return false;
                        if (!QueueParkingLots())
                            return false;
                        _parkingStep = ParkingStep.WaitingForApply;
                        _parkingWaitFrames = 0;
                        SetStatus(LF("OsmImporter.Status.BuildingParkingLots", "Building {0} parking lots...", _queuedParkingLots.Count));
                        return true;

                    case ParkingStep.WaitingForApply:
                        _parkingWaitFrames++;
                        // Stalls go in batches: one huge batch of objects makes the game crawl while it applies them.
                        if (_pendingStallIndex < _pendingStalls.Count)
                        {
                            var end = Math.Min(_pendingStalls.Count, _pendingStallIndex + ParkingStallsPerFrame);
                            for (; _pendingStallIndex < end; _pendingStallIndex++)
                                QueueParkingStall(_pendingStalls[_pendingStallIndex]);
                            return true;
                        }
                        if (_parkingWaitFrames % 60 == 1)
                            LogPendingDefinitions("parking", _parkingDefinitions, _parkingWaitFrames);
                        if (CountPendingParkingDefinitions() > 0 && _parkingWaitFrames < MaxApplyWaitFrames)
                            return true;
                        Mod.log.Info($"Parking definitions applied after {_parkingWaitFrames} frames ({CountPendingParkingDefinitions()} still pending).");
                        AssignParkingOwnership();
                        _parkingStep = ParkingStep.Done;
                        // One more round of frames so the stalls rebuild their parking lanes as owned objects.
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                _parkingStep = ParkingStep.Done;
                return false;
            }
        }

        private bool QueueParkingLots()
        {
            _parkingSurfacePrefab = FindPrefabByName(ParkingSurfacePrefabName);
            _parkingPathPrefab = FindPrefabByName(ParkingPathPrefabName);
            _parkingStallPrefab = FindPrefabByName(ParkingStallPrefabName);
            if (_parkingSurfacePrefab == Entity.Null || _parkingPathPrefab == Entity.Null || _parkingStallPrefab == Entity.Null)
            {
                Mod.log.Warn($"Parking lots skipped: prefab missing (surface={_parkingSurfacePrefab != Entity.Null}, path={_parkingPathPrefab != Entity.Null}, stall={_parkingStallPrefab != Entity.Null}).");
                return false;
            }

            _parkingBaselineSurfaces = QueryEntities(ComponentType.ReadOnly<Surface>(), ComponentType.ReadOnly<AreaNode>());
            _parkingBaselinePaths = QueryEntitiesWithPrefab(_parkingPathPrefab, ComponentType.ReadOnly<Edge>());
            _parkingBaselineStalls = QueryEntitiesWithPrefab(_parkingStallPrefab, ComponentType.ReadOnly<ObjectTransform>());

            var streets = new ParkingStreetIndex(this);
            var skipped = new Dictionary<string, int>();
            var lots = _document.ParkingAreas.Where(a => a.Kind == OsmParkingKind.Surface).ToList();
            var stallCount = 0;
            var loggedNoStreet = 0;
            Mod.log.Info($"Parking street index: {streets.EntranceRoads} road edges can take a lot entrance, {streets.OtherRoads} cannot (highways, pedestrian streets, dead-end stubs).");
            foreach (var area in lots)
            {
                var ring = area.Ring;
                float2? streetPoint = null;
                var streetApproach = float2.zero;
                var entranceInside = ParkingGeometry.Centroid(ring);
                if (streets.TryFindEntrance(ring, out var insidePoint, out var outsidePoint, out var approachPoint))
                {
                    streetPoint = outsidePoint;
                    streetApproach = approachPoint;
                    entranceInside = insidePoint;
                }

                if (!streetPoint.HasValue && loggedNoStreet++ < 8)
                {
                    var c0 = ParkingGeometry.Centroid(ring);
                    Mod.log.Info($"Parking lot {area.Id} at ({c0.x:F0}, {c0.y:F0}): no entrance street within {ParkingEntranceSearchMeters} m; nearest road of any kind {streets.NearestAnyRoadGap(ring):F1} m.");
                }
                var plan = ParkingLotLayout.Plan(ring, entranceInside, streetPoint, streetApproach, streets.IsNearStreet, streets.IsInStreetConnectRange);
                if (streetPoint.HasValue && plan.Stalls.Count > 0)
                {
                    var c1 = ParkingGeometry.Centroid(ring);
                    var ee = streets.LastEntranceEdge;
                    Mod.log.Info($"DIAG parking lot {area.Id} at ({c1.x:F1}, {c1.y:F1}): entrance to edge {ee.Index}:{ee.Version} ({(ee == Entity.Null ? "?" : _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(ee).m_Prefab))}) at ({streetPoint.Value.x:F1}, {streetPoint.Value.y:F1}), approach ({streetApproach.x:F1}, {streetApproach.y:F1}).");
                }
                if (plan.Stalls.Count == 0 && !string.IsNullOrEmpty(plan.SkipReason))
                    skipped[plan.SkipReason] = skipped.TryGetValue(plan.SkipReason, out var c) ? c + 1 : 1;

                QueueParkingSurface(ring);
                var queued = new QueuedParkingLot { Area = area, Paths = new List<(float2, float2)>(), Stalls = new List<float2>() };
                if (plan.Stalls.Count > 0)
                {
                    foreach (var path in plan.Paths)
                    {
                        var course = CreateNetCourseDefinition(Lift(path.A), Lift(path.B), _parkingPathPrefab);
                        if (course == Entity.Null)
                            continue;
                        _parkingDefinitions.Add(course);
                        queued.Paths.Add(path);
                    }
                    foreach (var stall in plan.Stalls)
                    {
                        _pendingStalls.Add(stall);
                        queued.Stalls.Add(stall.Center);
                    }
                    stallCount += plan.Stalls.Count;
                }
                _queuedParkingLots.Add(queued);
            }

            QueueParkingGarages(streets);

            Mod.log.Info($"Queued {lots.Count} surface parking lots with {stallCount} stalls. Pavement only: " +
                         (skipped.Count == 0 ? "none" : string.Join(", ", skipped.Select(s => $"{s.Value} ({s.Key})"))) +
                         $". Not built as surface lots: {_document.ParkingAreas.Count - lots.Count} (" +
                         string.Join(", ", _document.ParkingAreas.Where(a => a.Kind != OsmParkingKind.Surface).GroupBy(a => a.Kind).Select(g => $"{g.Key}={g.Count()}")) + ").");
            return _queuedParkingLots.Count > 0 || _queuedGarages > 0;
        }

        // Vanilla parking buildings for parking=multi-storey (footprints from ObjectGeometryData, game 1.6.2f1):
        // ParkingHall04 23.6 x 47.6, ParkingHall03 63.6 x 47.6, ParkingHall01 95.6 x 111.6. ParkingHall02 (47.6 x 79.6,
        // a low deck) is left out: placed by definition it stayed in an error state in the editor (seen twice, Seattle).
        private static readonly string[] ParkingGaragePrefabNames = { "ParkingHall04", "ParkingHall03", "ParkingHall01" };
        private readonly List<Entity> _parkingGaragePrefabs = new List<Entity>();
        private HashSet<Entity> _parkingBaselineGarages = new HashSet<Entity>();
        private int _queuedGarages;

        /// <summary>
        /// One vanilla parking building per multi-storey outline: the footprint closest to the outline's minimum-area
        /// rectangle, its front (local +z) facing the nearest street, moved up to that street when the gap is small so it
        /// gets road access.
        /// </summary>
        private void QueueParkingGarages(ParkingStreetIndex streets)
        {
            _queuedGarages = 0;
            _parkingGaragePrefabs.Clear();
            foreach (var name in ParkingGaragePrefabNames)
            {
                var p = FindPrefabByName(name);
                if (p != Entity.Null && EntityManager.HasComponent<ObjectGeometryData>(p))
                    _parkingGaragePrefabs.Add(p);
            }
            _parkingBaselineGarages = new HashSet<Entity>();
            foreach (var p in _parkingGaragePrefabs)
                _parkingBaselineGarages.UnionWith(QueryEntitiesWithPrefab(p, ComponentType.ReadOnly<ObjectTransform>()));
            if (_parkingGaragePrefabs.Count == 0)
                return;

            foreach (var area in _document.ParkingAreas.Where(a => a.Kind == OsmParkingKind.MultiStorey))
            {
                var rect = ParkingGeometry.MinAreaRectangle(area.Ring);
                // Every side of the outline that faces a street, nearest first; the long side faces +v without one.
                var frontages = streets.FindFrontages(rect);
                var front = frontages.Count > 0 ? frontages[0].Front : rect.AxisV;
                // For each street-facing side (nearest first) and each footprint (best fit first): the front edge on that
                // street, centred on the nearest street point. A footprint standing on a street, or whose front is within the game's
                // 8.4 m connection reach of a pedestrian street or highway (it would link to that instead), is skipped.
                // A garage with no usable side is not built: without road access it would never be used.
                var best = Entity.Null;
                float2 bestSize = default;
                var center = rect.Center;
                foreach (var (sideFront, outside, _) in frontages)
                {
                    var frontIsU = math.abs(math.dot(sideFront, rect.AxisU)) > 0.5f;
                    var frontage = frontIsU ? rect.SizeV : rect.SizeU; // building width runs along the street
                    var depth = frontIsU ? rect.SizeU : rect.SizeV;
                    var side = new float2(sideFront.y, -sideFront.x);
                    foreach (var p in _parkingGaragePrefabs.OrderBy(p =>
                             {
                                 var sz = EntityManager.GetComponentData<ObjectGeometryData>(p).m_Size.xz;
                                 return math.abs(math.log(sz.x / math.max(1f, frontage))) + math.abs(math.log(sz.y / math.max(1f, depth)));
                             }))
                    {
                        var size = EntityManager.GetComponentData<ObjectGeometryData>(p).m_Size.xz;
                        // Slide along the side from the outline's middle towards the street point: the game measures its 8.4 m
                        // reach from the middle of the front edge, so that point must end up next to the street.
                        var latMiddle = math.dot(rect.Center - outside, side);
                        var placed = false;
                        foreach (var f in new[] { 1f, 0.75f, 0.5f, 0.25f, 0f })
                        {
                            var c = outside + side * (latMiddle * f) - sideFront * (size.y * 0.5f);
                            var frontMiddle = c + sideFront * (size.y * 0.5f);
                            if (streets.EntranceRoadGap(frontMiddle) > 6f || FootprintOnStreet(streets, c, sideFront, size) || streets.NonEntranceRoadNear(frontMiddle, 8.4f))
                                continue;
                            center = c;
                            placed = true;
                            break;
                        }
                        if (!placed)
                            continue;
                        best = p;
                        bestSize = size;
                        front = sideFront;
                        break;
                    }
                    if (best != Entity.Null)
                        break;
                }
                if (best == Entity.Null)
                {
                    Mod.log.Info($"Parking garage {area.Id}: no vanilla parking building fits facing a street without standing on one; skipped.");
                    continue;
                }

                var position = Lift(center);
                var rotation = quaternion.RotateY(math.atan2(front.x, front.y));
                // With its internal ramps, parking and sub-areas, as the game's own tool would place it.
                QueueBuildingWithSubElements(best, position, rotation, (int)(area.Id & 0x7fffffff), _parkingDefinitions);
                _queuedGarages++;
                Mod.log.Info($"Parking garage {area.Id} at ({center.x:F0}, {center.y:F0}): outline {rect.SizeU:F0} x {rect.SizeV:F0} m -> {_prefabSystem.GetPrefabName(best)} ({bestSize.x:F0} x {bestSize.y:F0} m).");
            }
        }

        /// <summary>Samples the footprint (front along +front, width across) on a 4 m grid against the street index.</summary>
        private static bool FootprintOnStreet(ParkingStreetIndex streets, float2 center, float2 front, float2 size)
        {
            var side = new float2(front.y, -front.x);
            for (var w = -size.x * 0.5f + 1f; w <= size.x * 0.5f - 1f; w += 4f)
            for (var d = -size.y * 0.5f + 1f; d <= size.y * 0.5f - 1f; d += 4f)
                if (streets.IsNearStreet(center + side * w + front * d))
                    return true;
            return false;
        }

        private int TrackGeneratedGarages()
        {
            var count = 0;
            foreach (var p in _parkingGaragePrefabs)
                foreach (var e in QueryEntitiesWithPrefab(p, ComponentType.ReadOnly<ObjectTransform>()))
                    if (!_parkingBaselineGarages.Contains(e) && !EntityManager.HasComponent<Owner>(e))
                    {
                        _generatedParkingSurfaces.Add(e);
                        count++;
                    }
            return count;
        }

        // DIAG: which definition kinds are still waiting for the game.
        private void LogPendingDefinitions(string label, List<Entity> definitions, int frames)
        {
            int net = 0, obj = 0, area = 0, owned = 0;
            foreach (var e in definitions)
            {
                if (!EntityManager.Exists(e) || !EntityManager.HasComponent<CreationDefinition>(e)) continue;
                if (EntityManager.HasComponent<NetCourse>(e)) net++;
                else if (EntityManager.HasComponent<Game.Tools.ObjectDefinition>(e)) obj++;
                else area++;
                if (EntityManager.HasComponent<OwnerDefinition>(e)) owned++;
            }
            Mod.log.Info($"DIAG {label} pending after {frames} frames: {net} courses, {obj} objects, {area} areas ({owned} with an owner definition).");
        }

        private float3 Lift(float2 p) => new float3(p.x, SampleTerrainHeight(new float3(p.x, 0f, p.y)), p.y);

        private void QueueParkingSurface(List<float2> ring)
        {
            var entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition { m_Prefab = _parkingSurfacePrefab, m_Flags = CreationFlags.Permanent });
            EntityManager.AddComponentData(entity, new Updated());
            EntityManager.AddComponentData(entity, new OsmImportDefinition { SessionId = _sessionId });
            _parkingDefinitions.Add(entity);
            var nodes = EntityManager.AddBuffer<AreaNode>(entity);
            foreach (var p in ring)
                nodes.Add(new AreaNode(Lift(p), float.MinValue)); // MinValue elevation: on the ground (vanilla area tool)
            nodes.Add(nodes[0]); // the closing node must equal the first, or the area stays an open polyline
        }

        private void QueueParkingStall(ParkingStall stall)
        {
            var position = Lift(stall.Center);
            var rotation = quaternion.RotateY(math.atan2(stall.Facing.x, stall.Facing.y));
            var entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition
            {
                m_Prefab = _parkingStallPrefab,
                m_Flags = CreationFlags.Permanent,
                m_RandomSeed = ImportedHash(position, position + new float3(1f, 0f, 0f))
            });
            EntityManager.AddComponentData(entity, new Game.Tools.ObjectDefinition
            {
                m_Position = position,
                m_LocalPosition = position,
                m_Rotation = rotation,
                m_LocalRotation = rotation,
                // The marking's collision box (3.2 x 6.2 m) is larger than its 2.9 x 5.9 m stall, so neighbours would
                // overlap; scaled to fit inside the stall (as Placemaker does).
                m_Scale = new float3(ParkingStallScale),
                m_Intensity = 1f,
                m_Probability = 100,
                m_ParentMesh = -1,
                m_PrefabSubIndex = -1
            });
            EntityManager.AddComponentData(entity, new Updated());
            EntityManager.AddComponentData(entity, new OsmImportDefinition { SessionId = _sessionId });
            _parkingDefinitions.Add(entity);
        }

        // Only the parking definitions: unconsumed road definitions from earlier phases must not hold this step up.
        private int CountPendingParkingDefinitions()
        {
            var count = 0;
            foreach (var e in _parkingDefinitions)
                if (EntityManager.Exists(e) && EntityManager.HasComponent<CreationDefinition>(e))
                    count++;
            return count;
        }

        /// <summary>Finds what the game built for each queued lot and makes the surface own its paths and stalls.</summary>
        private void AssignParkingOwnership()
        {
            // Surfaces by their first outline point.
            var surfaceByKey = new Dictionary<(int, int), Entity>();
            foreach (var s in QueryEntities(ComponentType.ReadOnly<Surface>(), ComponentType.ReadOnly<AreaNode>()))
            {
                if (_parkingBaselineSurfaces.Contains(s) || EntityManager.GetComponentData<PrefabRef>(s).m_Prefab != _parkingSurfacePrefab)
                    continue;
                var nodes = EntityManager.GetBuffer<AreaNode>(s, true);
                if (nodes.Length > 0)
                    surfaceByKey[PointKey(nodes[0].m_Position.xz, 10f)] = s;
            }

            // Lot index by queued path segment and stall position, on a coarse grid.
            const float cell = 16f;
            var pathCells = new Dictionary<(int, int), List<(int Lot, float2 A, float2 B)>>();
            var stallByKey = new Dictionary<(int, int), int>();
            for (var i = 0; i < _queuedParkingLots.Count; i++)
            {
                var lot = _queuedParkingLots[i];
                lot.Surface = surfaceByKey.TryGetValue(PointKey(lot.Area.Ring[0], 10f), out var s) ? s : Entity.Null;
                foreach (var p in lot.Paths)
                {
                    var key = PointKey((p.A + p.B) * 0.5f, 1f / cell);
                    if (!pathCells.TryGetValue(key, out var list))
                        pathCells[key] = list = new List<(int, float2, float2)>();
                    list.Add((i, p.A, p.B));
                }
                foreach (var c in lot.Stalls)
                    stallByKey[PointKey(c, 20f)] = i;
            }

            var pathsByLot = new Dictionary<int, List<Entity>>();
            foreach (var e in QueryEntitiesWithPrefab(_parkingPathPrefab, ComponentType.ReadOnly<Edge>()))
            {
                if (_parkingBaselinePaths.Contains(e))
                    continue;
                var curve = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                var mid = MathUtils.Position(curve, 0.5f).xz;
                var key = PointKey(mid, 1f / cell);
                var best = -1;
                var bestDistance = 0.75f;
                for (var dx = -1; dx <= 1; dx++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!pathCells.TryGetValue((key.Item1 + dx, key.Item2 + dz), out var list))
                        continue;
                    foreach (var q in list)
                    {
                        var dist = ParkingGeometry.DistanceToSegment(mid, q.A, q.B);
                        if (dist < bestDistance) { bestDistance = dist; best = q.Lot; }
                    }
                }
                if (best < 0)
                    continue;
                if (!pathsByLot.TryGetValue(best, out var paths))
                    pathsByLot[best] = paths = new List<Entity>();
                paths.Add(e);
            }

            var stallsByLot = new Dictionary<int, List<Entity>>();
            foreach (var e in QueryEntitiesWithPrefab(_parkingStallPrefab, ComponentType.ReadOnly<ObjectTransform>()))
            {
                if (_parkingBaselineStalls.Contains(e) || EntityManager.HasComponent<Owner>(e))
                    continue;
                var pos = EntityManager.GetComponentData<ObjectTransform>(e).m_Position.xz;
                if (!stallByKey.TryGetValue(PointKey(pos, 20f), out var lot))
                    continue;
                if (!stallsByLot.TryGetValue(lot, out var stalls))
                    stallsByLot[lot] = stalls = new List<Entity>();
                stalls.Add(e);
            }

            var built = 0;
            var noSurface = 0;
            var ownedStalls = 0;
            for (var i = 0; i < _queuedParkingLots.Count; i++)
            {
                var lot = _queuedParkingLots[i];
                pathsByLot.TryGetValue(i, out var paths);
                stallsByLot.TryGetValue(i, out var stalls);
                if (lot.Surface == Entity.Null)
                {
                    // The game rejected the outline: do not leave unowned (unusable) aisles and stalls behind.
                    noSurface++;
                    foreach (var e in (paths ?? new List<Entity>()).Concat(stalls ?? new List<Entity>()))
                    {
                        EntityManager.AddComponentData(e, new Deleted());
                        EntityManager.AddComponentData(e, new Updated());
                    }
                    continue;
                }

                _generatedParkingSurfaces.Add(lot.Surface);
                built++;
                if (paths != null)
                {
                    var ownPaths = new HashSet<Entity>(paths);
                    var subNets = EntityManager.HasBuffer<SubNet>(lot.Surface) ? EntityManager.GetBuffer<SubNet>(lot.Surface) : EntityManager.AddBuffer<SubNet>(lot.Surface);
                    var ownedNodes = new HashSet<Entity>();
                    foreach (var e in paths)
                    {
                        if (!EntityManager.HasComponent<Owner>(e))
                            EntityManager.AddComponentData(e, new Owner { m_Owner = lot.Surface });
                        EntityManager.GetBuffer<SubNet>(lot.Surface).Add(new SubNet { m_SubNet = e });
                        var edge = EntityManager.GetComponentData<Edge>(e);
                        foreach (var n in new[] { edge.m_Start, edge.m_End })
                        {
                            if (!ownedNodes.Add(n) || EntityManager.HasComponent<Owner>(n))
                                continue;
                            // A node shared with a street stays the street's.
                            var connected = EntityManager.GetBuffer<ConnectedEdge>(n, true);
                            var onlyOurs = true;
                            for (var k = 0; k < connected.Length; k++)
                                onlyOurs &= ownPaths.Contains(connected[k].m_Edge);
                            if (!onlyOurs)
                                continue;
                            EntityManager.AddComponentData(n, new Owner { m_Owner = lot.Surface });
                            EntityManager.GetBuffer<SubNet>(lot.Surface).Add(new SubNet { m_SubNet = n });
                        }
                    }
                }
                if (stalls != null)
                {
                    if (!EntityManager.HasBuffer<SubObject>(lot.Surface))
                        EntityManager.AddBuffer<SubObject>(lot.Surface);
                    foreach (var e in stalls)
                    {
                        EntityManager.AddComponentData(e, new Owner { m_Owner = lot.Surface });
                        EntityManager.GetBuffer<SubObject>(lot.Surface).Add(new SubObject { m_SubObject = e });
                        // Owned now: let the game rebuild its parking lane (the editor gives none to unowned objects).
                        if (!EntityManager.HasComponent<Updated>(e))
                            EntityManager.AddComponent<Updated>(e);
                        ownedStalls++;
                    }
                }
            }

            var garages = TrackGeneratedGarages();
            HasGeneratedAssets |= built > 0 || garages > 0;
            Mod.log.Info($"Parking lots built: {built} surfaces, {ownedStalls} stalls owned, {pathsByLot.Values.Sum(p => p.Count)} aisle edges, " +
                         $"{garages}/{_queuedGarages} garages; {noSurface} outlines rejected by the game (aisles/stalls removed).");
        }

        private static (int, int) PointKey(float2 p, float scale) => ((int)math.round(p.x * scale), (int)math.round(p.y * scale));

        private Entity FindPrefabByName(string name)
        {
            using var prefabs = GetEntityQuery(ComponentType.ReadOnly<PrefabData>()).ToEntityArray(Allocator.Temp);
            foreach (var p in prefabs)
                if (_prefabSystem.GetPrefabName(p) == name)
                    return p;
            return Entity.Null;
        }

        private HashSet<Entity> QueryEntities(params ComponentType[] all)
        {
            using var q = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = all,
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            });
            using var arr = q.ToEntityArray(Allocator.Temp);
            return new HashSet<Entity>(arr);
        }

        private HashSet<Entity> QueryEntitiesWithPrefab(Entity prefab, ComponentType component)
        {
            var result = new HashSet<Entity>();
            foreach (var e in QueryEntities(component, ComponentType.ReadOnly<PrefabRef>()))
                if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == prefab)
                    result.Add(e);
            return result;
        }

        /// <summary>Street centrelines (roads with car lanes, not motorways) on a grid, for lot entrances and stall clearance.</summary>
        private sealed class ParkingStreetIndex
        {
            private const float Cell = 32f;
            private readonly Dictionary<(int, int), List<(float2 A, float2 B, float Half)>> _cells = new Dictionary<(int, int), List<(float2, float2, float)>>();
            public int EntranceRoads, OtherRoads;
            public readonly Dictionary<string, float> SpeedByPrefab = new Dictionary<string, float>();

            /// <summary>Diagnostics: gap from the outline to the nearest road of any kind (-1 if none within 200 m).</summary>
            public float NearestAnyRoadGap(IReadOnlyList<float2> ring)
            {
                var b = ParkingGeometry.Bounds(ring);
                var best = float.MaxValue;
                foreach (var s in Near(new float2(b.x, b.y) - 200f, new float2(b.z, b.w) + 200f))
                    foreach (var p in ring)
                        best = math.min(best, ParkingGeometry.DistanceToSegment(p, s.A, s.B) - math.abs(s.Half));
                return best == float.MaxValue ? -1f : best;
            }

            public ParkingStreetIndex(OsmImportSystem system)
            {
                var em = system.EntityManager;
                foreach (var e in system.QueryEntities(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>()))
                {
                    if (em.HasComponent<Owner>(e))
                        continue;
                    var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!em.HasComponent<RoadData>(prefab))
                        continue;
                    var speed = em.GetComponentData<RoadData>(prefab).m_SpeedLimit;
                    // Only streets cars can drive on take an entrance (a Pedestrian Street has RoadData but no car lane).
                    var hasCarLane = false;
                    if (em.HasBuffer<Game.Net.SubLane>(e))
                    {
                        var lanes = em.GetBuffer<Game.Net.SubLane>(e, true);
                        for (var k = 0; k < lanes.Length && !hasCarLane; k++)
                            hasCarLane = em.HasComponent<Game.Net.CarLane>(lanes[k].m_SubLane) && !em.HasComponent<Game.Net.ParkingLane>(lanes[k].m_SubLane)
                                         && (em.GetComponentData<Game.Net.CarLane>(lanes[k].m_SubLane).m_Flags & Game.Net.CarLaneFlags.Forbidden) == 0;
                    }
                    var prefabNameForEntrance = system._prefabSystem.GetPrefabName(prefab);
                    // Pedestrian streets only let service vehicles in; highways take no entrance.
                    var isHighway = speed > ParkingMaxEntranceSpeed || (em.GetComponentData<RoadData>(prefab).m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
                    var isEntranceRoad = hasCarLane && speed <= ParkingMaxEntranceSpeed
                                         && (em.GetComponentData<RoadData>(prefab).m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) == 0
                                         && prefabNameForEntrance.IndexOf("Pedestrian", StringComparison.OrdinalIgnoreCase) < 0;
                    if (isEntranceRoad) EntranceRoads++; else OtherRoads++;
                    var prefabName = system._prefabSystem.GetPrefabName(prefab);
                    SpeedByPrefab[prefabName] = speed;
                    var half = em.HasComponent<NetGeometryData>(prefab) ? em.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth * 0.5f : 4f;
                    var curve = em.GetComponentData<Curve>(e).m_Bezier;
                    // Bridges and overpasses neither block stalls underneath nor take an entrance.
                    var mid = MathUtils.Position(curve, 0.5f);
                    if (mid.y - system.SampleTerrainHeight(mid) > 4f)
                        continue;
                    var edgeNodes = em.GetComponentData<Edge>(e);
                    foreach (var node in new[] { edgeNodes.m_Start, edgeNodes.m_End })
                    {
                        // Junctions and dead ends: their node area covers the nearby street, which then builds no
                        // connection lanes for a path end there.
                        if (em.HasBuffer<ConnectedEdge>(node) && em.GetBuffer<ConnectedEdge>(node, true).Length != 2)
                        {
                            var np = em.GetComponentData<Game.Net.Node>(node).m_Position.xz;
                            var nk = ((int)math.floor(np.x / Cell), (int)math.floor(np.y / Cell));
                            if (!_junctions.TryGetValue(nk, out var jl))
                                _junctions[nk] = jl = new List<float2>();
                            jl.Add(np);
                        }
                    }
                    // The last edge of a dead end (turning circle) never links a path end.
                    if (IsDeadEnd(em, edgeNodes.m_Start) || IsDeadEnd(em, edgeNodes.m_End))
                        isEntranceRoad = false;
                    if (isEntranceRoad)
                    {
                        var midPoint = MathUtils.Position(curve, 0.5f).xz;
                        var midDir = math.normalizesafe(MathUtils.Tangent(curve, 0.5f).xz);
                        var length = MathUtils.Length(curve.xz);
                        var key = ((int)math.floor(midPoint.x / Cell), (int)math.floor(midPoint.y / Cell));
                        if (!_edgeMids.TryGetValue(key, out var mids))
                            _edgeMids[key] = mids = new List<(Entity, float2, float2, float, float)>();
                        mids.Add((e, midPoint, midDir, half, length));
                    }
                    var prev = curve.a.xz;
                    for (var i = 1; i <= 8; i++)
                    {
                        var next = MathUtils.Position(curve, i / 8f).xz;
                        // Negative half width marks a road that may not take an entrance (still kept clear of stalls).
                        Add(prev, next, isEntranceRoad ? half : -half);
                        if (isHighway)
                            AddHighway(prev, next, half);
                        prev = next;
                    }
                }
            }

            private readonly List<(float2 A, float2 B, float Half)> _highways = new List<(float2, float2, float)>();
            private readonly Dictionary<(int, int), List<int>> _highwayCells = new Dictionary<(int, int), List<int>>();

            private void AddHighway(float2 a, float2 b, float half)
            {
                var index = _highways.Count;
                _highways.Add((a, b, half));
                var min = math.min(a, b) - half;
                var max = math.max(a, b) + half;
                for (var x = (int)math.floor(min.x / Cell); x <= (int)math.floor(max.x / Cell); x++)
                for (var z = (int)math.floor(min.y / Cell); z <= (int)math.floor(max.y / Cell); z++)
                {
                    if (!_highwayCells.TryGetValue((x, z), out var list))
                        _highwayCells[(x, z)] = list = new List<int>();
                    list.Add(index);
                }
            }

            private void Add(float2 a, float2 b, float half)
            {
                var min = math.min(a, b) - math.abs(half);
                var max = math.max(a, b) + math.abs(half);
                for (var x = (int)math.floor(min.x / Cell); x <= (int)math.floor(max.x / Cell); x++)
                for (var z = (int)math.floor(min.y / Cell); z <= (int)math.floor(max.y / Cell); z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list))
                        _cells[(x, z)] = list = new List<(float2, float2, float)>();
                    list.Add((a, b, half));
                }
            }

            private IEnumerable<(float2 A, float2 B, float Half)> Near(float2 min, float2 max)
            {
                var seen = new HashSet<(float2, float2)>();
                for (var x = (int)math.floor(min.x / Cell); x <= (int)math.floor(max.x / Cell); x++)
                for (var z = (int)math.floor(min.y / Cell); z <= (int)math.floor(max.y / Cell); z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list))
                        continue;
                    foreach (var s in list)
                        if (seen.Add((s.A, s.B)))
                            yield return s;
                }
            }

            public bool IsNearStreet(float2 p)
            {
                foreach (var s in Near(p, p))
                    if (ParkingGeometry.DistanceToSegment(p, s.A, s.B) < math.abs(s.Half) + ParkingStreetClearanceMeters)
                        return true;
                return false;
            }

            /// <summary>
            /// Nearest entrance street to the outline: insidePoint is 1.5 m inside the outline where the lot is closest to
            /// that street, outsidePoint is just beyond the street's edge on the lot's side (where the path ends).
            /// </summary>
            public Entity LastEntranceEdge;

            /// <summary>
            /// For a building: the side of the rectangle closest to a street with car lanes (front = that side's outward
            /// direction) and the point just off that street's edge where the front edge should go.
            /// </summary>
            public List<(float2 Front, float2 Outside, float Gap)> FindFrontages(ParkingGeometry.OrientedRect rect)
            {
                var result = new List<(float2, float2, float)>();
                foreach (var dir in new[] { rect.AxisU, -rect.AxisU, rect.AxisV, -rect.AxisV })
                {
                    var reachOut = math.abs(math.dot(dir, rect.AxisU)) > 0.5f ? rect.SizeU * 0.5f : rect.SizeV * 0.5f;
                    var along = math.abs(math.dot(dir, rect.AxisU)) > 0.5f ? rect.SizeV * 0.5f : rect.SizeU * 0.5f;
                    var side = new float2(dir.y, -dir.x);
                    var mid = rect.Center + dir * reachOut;
                    var best = ParkingEntranceSearchMeters;
                    var bestOutside = float2.zero;
                    var found = false;
                    for (var k = -2; k <= 2; k++)
                    {
                        var p = mid + side * (along * k / 2f);
                        foreach (var s in Near(p - ParkingEntranceSearchMeters, p + ParkingEntranceSearchMeters))
                        {
                            if (s.Half <= 0f)
                                continue;
                            var onRoad = ParkingGeometry.ClosestOnSegment(p, s.A, s.B);
                            if (math.dot(onRoad - mid, dir) < -1f)
                                continue; // only streets in front of this side
                            var gap = math.distance(p, onRoad) - s.Half;
                            if (gap < best)
                            {
                                best = gap;
                                bestOutside = onRoad - dir * (s.Half + 0.3f);
                                found = true;
                            }
                        }
                    }
                    if (found)
                        result.Add((dir, bestOutside, best));
                }
                result.Sort((x, y) => x.Item3.CompareTo(y.Item3));
                return result;
            }

            /// <summary>True when a road that cannot take an entrance (pedestrian street, highway) lies within the
            /// game's building-connection reach (8.4 m) of a point: the building would link to it instead.</summary>
            /// <summary>Distance from a point to the edge of the nearest street that can take an entrance.</summary>
            public float EntranceRoadGap(float2 p)
            {
                var best = float.MaxValue;
                foreach (var s in Near(p - 30f, p + 30f))
                    if (s.Half > 0f)
                        best = math.min(best, ParkingGeometry.DistanceToSegment(p, s.A, s.B) - s.Half);
                return best;
            }

            public bool NonEntranceRoadNear(float2 p, float reach)
            {
                foreach (var s in Near(p - reach - 20f, p + reach + 20f))
                    if (s.Half < 0f && ParkingGeometry.DistanceToSegment(p, s.A, s.B) + s.Half < reach)
                        return true;
                return false;
            }

            public bool TryFindEntrance(IReadOnlyList<float2> ring, out float2 insidePoint, out float2 outsidePoint, out float2 approachPoint)
            {
                insidePoint = outsidePoint = approachPoint = default;
                var b = ParkingGeometry.Bounds(ring);
                var reach = ParkingEntranceSearchMeters;
                // Aim at the middle of a street edge: a path end beside an edge's end is handed to the street node
                // there (LaneSystem.CheckConnectPosition) and gets no car connection. Importer edges are ~14 m long.
                var best = float.MaxValue;
                var bestRing = float2.zero;
                (Entity Edge, float2 Mid, float2 Dir, float Half, float Length) bestEdge = default;
                for (var x = (int)math.floor((b.x - reach) / Cell); x <= (int)math.floor((b.z + reach) / Cell); x++)
                for (var z = (int)math.floor((b.y - reach) / Cell); z <= (int)math.floor((b.w + reach) / Cell); z++)
                {
                    if (!_edgeMids.TryGetValue((x, z), out var mids))
                        continue;
                    foreach (var m in mids)
                    {
                        if (m.Length < EntranceMinEdgeLength || NearJunction(m.Mid))
                            continue;
                        var onRing = ClosestOnRing(ring, m.Mid);
                        var gap = math.distance(onRing, m.Mid) - m.Half;
                        if (gap < best)
                        {
                            best = gap;
                            bestRing = onRing;
                            bestEdge = m;
                        }
                    }
                }
                if (best > reach)
                    return false;
                LastEntranceEdge = bestEdge.Edge;

                var centroid = ParkingGeometry.Centroid(ring);
                var square = new float2(-bestEdge.Dir.y, bestEdge.Dir.x);
                var towardLot = math.lengthsq(bestRing - bestEdge.Mid) > 1f ? bestRing - bestEdge.Mid : centroid - bestEdge.Mid;
                if (math.dot(square, towardLot) < 0f)
                    square = -square;
                outsidePoint = bestEdge.Mid + square * (bestEdge.Half + 0.3f);
                approachPoint = outsidePoint + square * 5f;
                insidePoint = bestRing + math.normalizesafe(centroid - bestRing) * 1.5f;
                if (!ParkingGeometry.Contains(ring, insidePoint))
                    insidePoint = math.lerp(bestRing, centroid, 0.2f);
                return true;
            }

            private const float EntranceMinEdgeLength = 8f;
            // Invisible Road Path local connect: path half width 2 + search distance 4 beyond the street's edge.
            private const float LocalConnectReach = 6f;
            private readonly Dictionary<(int, int), List<(Entity Edge, float2 Mid, float2 Dir, float Half, float Length)>> _edgeMids =
                new Dictionary<(int, int), List<(Entity, float2, float2, float, float)>>();

            private const float EntranceJunctionClearance = 15f;
            private readonly Dictionary<(int, int), List<float2>> _junctions = new Dictionary<(int, int), List<float2>>();

            private bool NearJunction(float2 p)
            {
                for (var x = (int)math.floor((p.x - EntranceJunctionClearance) / Cell); x <= (int)math.floor((p.x + EntranceJunctionClearance) / Cell); x++)
                for (var z = (int)math.floor((p.y - EntranceJunctionClearance) / Cell); z <= (int)math.floor((p.y + EntranceJunctionClearance) / Cell); z++)
                    if (_junctions.TryGetValue((x, z), out var list))
                        foreach (var j in list)
                            if (math.distance(j, p) < EntranceJunctionClearance)
                                return true;
                return false;
            }

            /// <summary>
            /// True where a path dead end would link by itself to a highway. Extra exits onto ordinary streets are
            /// allowed, as in real lots.
            /// </summary>
            public bool IsInStreetConnectRange(float2 p)
            {
                for (var x = (int)math.floor((p.x - 20f) / Cell); x <= (int)math.floor((p.x + 20f) / Cell); x++)
                for (var z = (int)math.floor((p.y - 20f) / Cell); z <= (int)math.floor((p.y + 20f) / Cell); z++)
                    if (_highwayCells.TryGetValue((x, z), out var list))
                        foreach (var i in list)
                        {
                            var h = _highways[i];
                            if (ParkingGeometry.DistanceToSegment(p, h.A, h.B) < h.Half + LocalConnectReach + 1f)
                                return true;
                        }
                return false;
            }

            private static float2 ClosestOnRing(IReadOnlyList<float2> ring, float2 p)
            {
                var best = ring[0];
                var bestDistance = float.MaxValue;
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    var c = ParkingGeometry.ClosestOnSegment(p, ring[j], ring[i]);
                    var d = math.distancesq(c, p);
                    if (d < bestDistance) { bestDistance = d; best = c; }
                }
                return best;
            }

            private static bool IsDeadEnd(EntityManager em, Entity node) =>
                em.HasBuffer<ConnectedEdge>(node) && em.GetBuffer<ConnectedEdge>(node, true).Length == 1;
        }
    }
}
