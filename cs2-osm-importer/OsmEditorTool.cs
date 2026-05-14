using Game.UI.Editor;
using Unity.Entities;

namespace cs2_osm_importer
{
    public class OsmEditorTool : EditorTool
    {
        public OsmEditorTool(World world)
            : base(world)
        {
            id = Mod.ToolId;
            icon = "coui://ui-mods/images/Openstreetmap_logo.png";
            uiTag = "UITagPrefab:OSMImportButton";
            panel = world.GetOrCreateSystemManaged<OsmImportPanelSystem>();
            tool = world.GetOrCreateSystemManaged<OsmImportToolSystem>();
        }
    }
}
