using System;
using Unity.Entities;

namespace cs2_osm_importer
{
    // GameSystem responsible for parsing OSM zoning data and applying it in Play mode.
    // Implementation is scaffolded; parsing and mapping heuristics to be added next.
    public partial class OsmZoningImportSystem : GameSystemBase
    {
        public void LoadZoningFromFile(string path)
        {
            Mod.log.Info($"OSM zoning: requested load for '{path}'");
            if (string.IsNullOrWhiteSpace(path))
            {
                Mod.log.Info("OSM zoning: no path provided");
                return;
            }
n            // The importer is designed to apply zoning in Play mode only. The actual "apply" step
            // must be guarded by a runtime check and invoked while the city is running. For now
            // just log and parse later.
            Mod.log.Info("OSM zoning: parsing and preview (apply in Play mode only). Implementation pending.");
        }
    }
}
