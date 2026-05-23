using Game.UI.Editor;
using Unity.Entities;

namespace cs2_osm_importer
{
    // Small editor tool entry for the OSM Zoning importer. Panel and tool systems are scaffolds.
    public class OsmZoningTool : EditorTool
    {
        public OsmZoningTool(World world)
            : base(world)
        {
            id = Mod.ToolId + " - Zoning";
            icon = "coui://ui-mods/images/Openstreetmap_logo.png";
            uiTag = "UITagPrefab:OSMZoningButton";
            panel = world.GetOrCreateSystemManaged<OsmZoningPanelSystem>();
            tool = world.GetOrCreateSystemManaged<OsmZoningImportSystem>();
        }
    }
}
