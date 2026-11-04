using Colossal.IO.AssetDatabase;
using Game.Input;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace cs2_osm_importer
{
    [FileLocation(Mod.Id)]
    [SettingsUITabOrder(SETTINGS, DEFAULTS)]
    [SettingsUIGroupOrder(SETTINGS_INFO, DEVELOPER, DEFAULTS_INFO, DEFAULTS_IMPORT, DEFAULTS_STRUCTURAL, DEFAULTS_GENERATE, DEFAULTS_RESET)]
    [SettingsUIShowGroupName(SETTINGS_INFO, DEVELOPER, DEFAULTS_INFO, DEFAULTS_IMPORT, DEFAULTS_STRUCTURAL, DEFAULTS_GENERATE, DEFAULTS_RESET)]
    public class OsmImporterSettings : ModSetting
    {
        public const string SETTINGS = "Settings";
        public const string DEFAULTS = "Defaults";
        public const string SETTINGS_INFO = "Settings info";
        public const string DEVELOPER = "Developer";
        public const string DEFAULTS_INFO = "Defaults info";
        public const string DEFAULTS_IMPORT = "Import defaults";
        public const string DEFAULTS_STRUCTURAL = "Structural elevation defaults";
        public const string DEFAULTS_GENERATE = "Generate defaults";
        public const string DEFAULTS_RESET = "Reset all defaults";

        public OsmImporterSettings(IMod mod) : base(mod)
        {
        }

        // ---- Settings tab ----

        [SettingsUISection(SETTINGS, SETTINGS_INFO)]
        [SettingsUIMultilineText]
        public string SettingsDescription => string.Empty;

        [SettingsUISection(SETTINGS, DEVELOPER)]
        public bool DeveloperMode
        {
            get => _developerMode;
            set
            {
                _developerMode = value;
                if (!value)
                    DevLoadAndGenerateInOneClick = false;
            }
        }

        [SettingsUISection(SETTINGS, DEVELOPER)]
        [SettingsUIHideByCondition(typeof(OsmImporterSettings), nameof(IsDeveloperModeOff))]
        public bool DevLoadAndGenerateInOneClick
        {
            get => _developerMode && _devLoadAndGenerateInOneClick;
            set => _devLoadAndGenerateInOneClick = _developerMode && value;
        }

        [SettingsUIButton]
        [SettingsUISection(SETTINGS, DEVELOPER)]
        [SettingsUIHideByCondition(typeof(OsmImporterSettings), nameof(IsDeveloperModeOff))]
        public bool WriteGeometryReport
        {
            set => OsmImportSystem.GeometryReportRequested = true;
        }

        [SettingsUIKeyboardBinding(actionName: Mod.ButtonActionName)]
        [SettingsUIHidden]
        public ProxyBinding TogglePanel { get; set; }

        private bool _developerMode;
        private bool _devLoadAndGenerateInOneClick;

        // ---- Defaults tab ----

        [SettingsUISection(DEFAULTS, DEFAULTS_INFO)]
        [SettingsUIMultilineText]
        public string DefaultsDescription => string.Empty;

        // Import defaults group
        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        [SettingsUITextInput]
        public string DefaultOsmPath { get; set; } = string.Empty;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        [SettingsUITextInput]
        public string DefaultPriorityRoadNamesPath { get; set; } = string.Empty;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        [SettingsUISlider(min = 0, max = 10, step = 1f)]
        public float DefaultSimplifyTolerance { get; set; } = 1f;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        [SettingsUISlider(min = 0, max = 5, step = 1f)]
        public float DefaultCurveTolerance { get; set; } = 0.5f;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        [SettingsUISlider(min = 0.1f, max = 10, step = 1f)]
        public float DefaultScaleFactor { get; set; } = 1f;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        [SettingsUISlider(min = 1000, max = 100000, step = 1000)]
        public int DefaultMaxWays { get; set; } = 10000;

        // Road type selection defaults (each is a checkbox toggle)
        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportMotorway { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportTrunk { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportPrimary { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportSecondary { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportTertiary { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportUnclassified { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportResidential { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportService { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportLivingStreet { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportRoad { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportMotorwayLink { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportTrunkLink { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportPrimaryLink { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportSecondaryLink { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportTertiaryLink { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportFootway { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportSteps { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportBridleway { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportMonorail { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportRail { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportLightRail { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportSubway { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportTram { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportTrack { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportCycleway { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool DefaultImportPedestrian { get; set; } = true;

        // Reset import defaults button (rendered inline at the bottom of the import defaults group)
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(DEFAULTS, DEFAULTS_IMPORT)]
        public bool ResetImportDefaults
        {
            set
            {
                ResetImportDefaultsValues();
            }
        }

        // Structural elevation defaults group
        [SettingsUISection(DEFAULTS, DEFAULTS_STRUCTURAL)]
        [SettingsUISlider(min = 0, max = 20, step = 0.1f)]
        public float DefaultLayerElevationStep { get; set; } = 10f;

        [SettingsUISection(DEFAULTS, DEFAULTS_STRUCTURAL)]
        [SettingsUISlider(min = 0, max = 20, step = 0.1f)]
        public float DefaultBridgeElevation { get; set; } = 10f;

        [SettingsUISection(DEFAULTS, DEFAULTS_STRUCTURAL)]
        [SettingsUISlider(min = 0, max = 20, step = 0.1f)]
        public float DefaultTunnelDepth { get; set; } = 10f;

        [SettingsUISection(DEFAULTS, DEFAULTS_STRUCTURAL)]
        [SettingsUISlider(min = 0, max = 20, step = 0.1f)]
        public float DefaultCoveredDepth { get; set; } = 5f;

        [SettingsUISection(DEFAULTS, DEFAULTS_STRUCTURAL)]
        [SettingsUISlider(min = 0, max = 20, step = 0.1f)]
        public float DefaultEmbankmentElevation { get; set; } = 3f;

        [SettingsUISection(DEFAULTS, DEFAULTS_STRUCTURAL)]
        [SettingsUISlider(min = 0, max = 20, step = 0.1f)]
        public float DefaultCuttingDepth { get; set; } = 3f;

        // Reset structural defaults button (rendered inline at the bottom of the structural defaults group)
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(DEFAULTS, DEFAULTS_STRUCTURAL)]
        public bool ResetStructuralDefaults
        {
            set
            {
                ResetStructuralDefaultsValues();
            }
        }

        // Generate defaults group
        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        [SettingsUISlider(min = 1, max = 20, step = 0.5f)]
        public float DefaultIntersectionThreshold { get; set; } = 8f;

        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool DefaultGenerateBikeLanes { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool DefaultGenerateTurnRestrictions { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool DefaultGenerateCrosswalks { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool DefaultGenerateStopSigns { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool DefaultGenerateTrafficLights { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool DefaultGenerateRestrictedLanes { get; set; } = true;

        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool DefaultGenerateParkingLots { get; set; } = true;

        // Reset generate defaults button (rendered inline at the bottom of the generate defaults group)
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(DEFAULTS, DEFAULTS_GENERATE)]
        public bool ResetGenerateDefaults
        {
            set
            {
                ResetGenerateDefaultsValues();
            }
        }

        // Reset all defaults button (rendered in its own group at the bottom of the tab)
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(DEFAULTS, DEFAULTS_RESET)]
        public bool ResetAllDefaults
        {
            set
            {
                SetDefaults();
            }
        }

        public override void SetDefaults()
        {
            _developerMode = false;
            DevLoadAndGenerateInOneClick = false;
            ResetImportDefaultsValues();
            ResetStructuralDefaultsValues();
            ResetGenerateDefaultsValues();
        }

        private bool IsDeveloperModeOff() => !DeveloperMode;

        private void ResetImportDefaultsValues()
        {
            DefaultOsmPath = GetDefaultOsmPath();
            DefaultPriorityRoadNamesPath = GetDefaultOsmPath();
            DefaultImportMotorway = true;
            DefaultImportTrunk = true;
            DefaultImportPrimary = true;
            DefaultImportSecondary = true;
            DefaultImportTertiary = true;
            DefaultImportUnclassified = true;
            DefaultImportResidential = true;
            DefaultImportService = true;
            DefaultImportLivingStreet = true;
            DefaultImportRoad = true;
            DefaultImportMotorwayLink = true;
            DefaultImportTrunkLink = true;
            DefaultImportPrimaryLink = true;
            DefaultImportSecondaryLink = true;
            DefaultImportTertiaryLink = true;
            DefaultImportFootway = true;
            DefaultImportSteps = true;
            DefaultImportBridleway = true;
            DefaultImportMonorail = true;
            DefaultImportRail = true;
            DefaultImportLightRail = true;
            DefaultImportSubway = true;
            DefaultImportTram = true;
            DefaultImportTrack = true;
            DefaultImportCycleway = true;
            DefaultImportPedestrian = true;
            DefaultSimplifyTolerance = 1f;
            DefaultCurveTolerance = 0.5f;
            DefaultScaleFactor = 1f;
            DefaultMaxWays = 10000;
        }

        private void ResetStructuralDefaultsValues()
        {
            DefaultLayerElevationStep = 10f;
            DefaultBridgeElevation = 10f;
            DefaultTunnelDepth = 10f;
            DefaultCoveredDepth = 5f;
            DefaultEmbankmentElevation = 3f;
            DefaultCuttingDepth = 3f;
        }

        private void ResetGenerateDefaultsValues()
        {
            DefaultIntersectionThreshold = 8f;
            DefaultGenerateBikeLanes = true;
            DefaultGenerateTurnRestrictions = true;
            DefaultGenerateCrosswalks = true;
            DefaultGenerateStopSigns = true;
            DefaultGenerateTrafficLights = true;
            DefaultGenerateRestrictedLanes = true;
            DefaultGenerateParkingLots = true;
        }

        private static string GetDefaultOsmPath()
        {
            var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                return string.Empty;

            return System.IO.Path.Combine(userProfile, "Downloads") + System.IO.Path.DirectorySeparatorChar;
        }
    }
}
