using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace cs2_osm_importer
{
    /// <summary>
    /// When a new city starts from a map, buildings placed in the editor (the importer's parking garages and parks) come
    /// in without their road link (Building.m_RoadEdge) until something updates them, so they show "No Road Access" and
    /// citizens cannot reach them. Marking them Updated once lets the game's RoadConnectionSystem connect them again
    /// (verified in game, 1.6.2f1).
    /// Junctions come in with their lanes as saved, and those lack the crosswalk lanes the junction's composition
    /// calls for: in a new city from an imported Seattle map, pedestrians on Capitol Hill could reach 9% of the area's
    /// walkways (85% in the editor on the same map), and a 4-way junction had no crosswalk lane until something updated
    /// it. Every network node joining two or more edges is marked Updated once too, so the game rebuilds its lanes.
    /// </summary>
    public partial class OsmCityStartSystem : GameSystemBase
    {
        private EntityQuery _unconnectedBuildings;
        private EntityQuery _networkNodes;
        // Right at load completion the roads are not ready to take building connections yet; a refresh then is lost.
        private const int RefreshDelayFrames = 180;
        private int _framesUntilRefresh = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            _unconnectedBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>() }
            });
            _networkNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<SubLane>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>() }
            });
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            _framesUntilRefresh = mode == GameMode.Game && purpose == Purpose.NewGame ? RefreshDelayFrames : -1;
        }

        protected override void OnUpdate()
        {
            if (_framesUntilTramRestore >= 0 && --_framesUntilTramRestore <= 0)
            {
                _framesUntilTramRestore = -1;
                RestoreTramEdges();
            }
            if (_framesUntilRefresh < 0 || --_framesUntilRefresh > 0)
                return;
            _framesUntilRefresh = -1;
            using var buildings = _unconnectedBuildings.ToEntityArray(Allocator.Temp);
            var touched = 0;
            foreach (var e in buildings)
            {
                if (EntityManager.GetComponentData<Building>(e).m_RoadEdge != Entity.Null)
                    continue;
                EntityManager.AddComponent<Updated>(e);
                touched++;
            }
            if (touched > 0)
                Mod.log.Info($"New city: refreshed {touched} map buildings without a road link so the game reconnects them.");
            using var nodes = _networkNodes.ToEntityArray(Allocator.Temp);
            var nodesTouched = 0;
            foreach (var n in nodes)
            {
                if (EntityManager.GetBuffer<ConnectedEdge>(n, true).Length < 2)
                    continue;
                EntityManager.AddComponent<Updated>(n);
                nodesTouched++;
            }
            if (nodesTouched > 0)
                Mod.log.Info($"New city: refreshed {nodesTouched} network junctions so the game rebuilds their lanes (crosswalks were missing).");
            StartTramLaneRebuild();
        }

        // Street tram edges come in from the map with lanes the game's pathfinder cannot route a tram along at some
        // junctions (on Broadway in Seattle the tram lanes kept ForbidPassing and MergingLane flags from when the edge was
        // built; marking the edge Updated keeps them). A real change of the edge's composition rebuilds them: verified in
        // game by adding bike lanes and taking them off again. Taking the tram track itself off and back does not work
        // (it left every junction without tram lanes), so the bike-lane flag is flipped for a few frames and restored.
        private const int TramRestoreFrames = 30;
        private readonly System.Collections.Generic.List<(Entity Edge, Upgraded Original)> _tramEdges = new System.Collections.Generic.List<(Entity Edge, Upgraded Original)>();
        private int _framesUntilTramRestore = -1;

        private void StartTramLaneRebuild()
        {
            const Game.Prefabs.CompositionFlags.Side track = Game.Prefabs.CompositionFlags.Side.PrimaryTrack;
            const Game.Prefabs.CompositionFlags.Side bike = Game.Prefabs.CompositionFlags.Side.SecondaryLane;
            using var edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Upgraded>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            }).ToEntityArray(Allocator.Temp);
            _tramEdges.Clear();
            foreach (var e in edges)
            {
                var up = EntityManager.GetComponentData<Upgraded>(e);
                if (((up.m_Flags.m_Left | up.m_Flags.m_Right) & track) == 0)
                    continue;
                _tramEdges.Add((e, up));
                up.m_Flags.m_Left ^= bike;
                up.m_Flags.m_Right ^= bike;
                EntityManager.SetComponentData(e, up);
                MarkEdgeAndNodesUpdated(e);
            }
            _framesUntilTramRestore = _tramEdges.Count > 0 ? TramRestoreFrames : -1;
        }

        private void RestoreTramEdges()
        {
            var restored = 0;
            foreach (var (e, original) in _tramEdges)
            {
                if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e) || !EntityManager.HasComponent<Upgraded>(e))
                    continue;
                EntityManager.SetComponentData(e, original);
                MarkEdgeAndNodesUpdated(e);
                restored++;
            }
            _tramEdges.Clear();
            Mod.log.Info($"New city: rebuilt the lanes of {restored} street tram edges so trams can be routed along them.");
        }

        private void MarkEdgeAndNodesUpdated(Entity e)
        {
            var edge = EntityManager.GetComponentData<Edge>(e);
            foreach (var t in new[] { e, edge.m_Start, edge.m_End })
                if (EntityManager.Exists(t) && !EntityManager.HasComponent<Updated>(t))
                    EntityManager.AddComponent<Updated>(t);
        }
    }
}
