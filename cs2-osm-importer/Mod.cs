using Colossal.IO.AssetDatabase;
using Colossal.Logging;

using Game;
using Game.Modding;
using Game.SceneFlow;


namespace cs2_osm_importer
{
    public class Mod : IMod
    {
        public const string Id = "cs2-osm-importer";
        public const string ToolId = "OsmImportTool";

        public static ILog log = LogManager.GetLogger($"{nameof(cs2_osm_importer)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(true);

        public const string ButtonActionName = "ToggleOSMImporterTool";
        
        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            updateSystem.UpdateAt<OsmImporterUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<OsmImportToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<OsmImportSystem>(SystemUpdatePhase.ToolUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}
