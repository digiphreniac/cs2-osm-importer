using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Colossal.Logging;

using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using System.IO;


namespace cs2_osm_importer
{
    public class Mod : IMod
    {
        public const string Id = "cs2_osm_importer";
        public const string ToolId = "OSM Importer Tool";
        public static string ModDirectory { get; private set; } = string.Empty;

        public static ILog log = LogManager.GetLogger($"{nameof(cs2_osm_importer)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(true);

        public const string ButtonActionName = "ToggleOSMImporterTool";

        public static OsmImporterSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            GameManager.instance.localizationManager.AddSource("en-US", OsmLocalization.CreateEditorFallbackSource());

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
                ModDirectory = Path.GetDirectoryName(asset.path) ?? string.Empty;
            }

            Settings = new OsmImporterSettings(this);
            Settings.RegisterInOptionsUI();
            Settings.RegisterKeyBindings();
            AssetDatabase.global.LoadSettings(Mod.Id, Settings, new OsmImporterSettings(this));

            updateSystem.UpdateAt<OsmImporterUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<OsmImportToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<OsmImportSystem>(SystemUpdatePhase.ToolUpdate);

            // Register zoning UI + tool systems (scaffolding for new feature)
            updateSystem.UpdateAt<OsmZoningPanelSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<OsmZoningImportSystem>(SystemUpdatePhase.ToolUpdate);
        }
n        public void OnDispose()
        {
            log.Info(nameof(OnDispose));

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
