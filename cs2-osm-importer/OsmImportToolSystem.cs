using Game.Tools;
using Game.Prefabs;
using Unity.Entities;

namespace cs2_osm_importer
{
    public partial class OsmImportToolSystem : ToolBaseSystem
    {
        public override string toolID => Mod.ToolId;
        // public override string toolID => "OSM Import Tool";

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            base.OnCreate();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            Mod.log.Info("OSM Import Tool activated.");
        }

        protected override void OnStopRunning()
        {
            Mod.log.Info("OSM Import Tool deactivated.");
            base.OnStopRunning();
        }
    }
}
