using Colossal.IO.AssetDatabase;
using Game.Input;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace cs2_osm_importer
{
    [FileLocation(Mod.Id)]
    [SettingsUITabOrder(SETTINGS)]
    [SettingsUIGroupOrder(DEVELOPER)]
    [SettingsUIShowGroupName(DEVELOPER)]
    public class OsmImporterSettings : ModSetting
    {
        public const string SETTINGS = "Settings";
        public const string DEVELOPER = "Developer";

        public OsmImporterSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(SETTINGS, DEVELOPER)]
        public bool DeveloperMode { get; set; }

        public override void SetDefaults()
        {
            DeveloperMode = false;
        }
    }
}
