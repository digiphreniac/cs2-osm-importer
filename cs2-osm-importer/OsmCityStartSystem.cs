using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
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
    /// </summary>
    public partial class OsmCityStartSystem : GameSystemBase
    {
        private EntityQuery _unconnectedBuildings;
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
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            _framesUntilRefresh = mode == GameMode.Game && purpose == Purpose.NewGame ? RefreshDelayFrames : -1;
        }

        protected override void OnUpdate()
        {
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
        }
    }
}
