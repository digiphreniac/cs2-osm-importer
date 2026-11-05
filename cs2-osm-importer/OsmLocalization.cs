using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Localization;
using Game.SceneFlow;

namespace cs2_osm_importer
{
    internal static class OsmLocalization
    {
        private static readonly Dictionary<string, string> EditorFallbacks = new Dictionary<string, string>
        {
            { $"Editor.TOOL[{Mod.ToolId}]", "OSM Importer Tool" },
            { $"Options.SECTION[{Mod.Id}.{Mod.Id}.Mod]", "OSM Importer" },
            { $"Options.TAB[{Mod.Id}.{Mod.Id}.Mod.Settings]", "Settings" },
            { $"Options.TAB[{Mod.Id}.{Mod.Id}.Mod.Defaults]", "Default values" },
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Settings info]", "Information" },
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Defaults info]", "About default values" },
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Developer]", "Developer" },
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Import defaults]", "Import defaults" },
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Structural elevation defaults]", "Structural elevation defaults" },
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Generate defaults]", "Generate defaults" },
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Reset all defaults]", "Reset all defaults" },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.SettingsDescription]", "These settings are experimental and not currently recommended for most users. Enable only if you understand the implications." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DeveloperMode]", "Developer Mode" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DeveloperMode]", "Enables debug UI bindings and developer-only features for the OSM Importer mod." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DevLoadAndGenerateInOneClick]", "Load & generate in one click" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DevLoadAndGenerateInOneClick]", "When enabled, adds a 'DEV: Load & Generate' button to the panel that loads OSM data and immediately starts road generation." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.WriteGeometryReport]", "Write geometry report" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.WriteGeometryReport]", "Compares the generated roads against the queued segments and the original OSM geometry, and writes the results to ModsData/cs2_osm_importer/geometry-reports. Developer Mode also writes this report automatically after each generation." },
            { $"Options.INPUT_ACTION[{Mod.Id}.{Mod.ButtonActionName}]", "Toggle OSM Importer Panel" },
            { "OsmImporter.Button.DevLoadAndGenerate", "DEV: Load & Generate" },
            // Defaults tab info
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultsDescription]", "These default values are pre-filled when you open the OSM Importer tool. Changing these defaults may be helpful if you rely on this mod a lot." },

            // Import defaults
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultOsmPath]", "Default OSM file path" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultOsmPath]", "Default path shown in the OSM file path field when the editor is opened." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultPriorityRoadNamesPath]", "Default priority roads file path" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultPriorityRoadNamesPath]", "Default path shown in the priority roads file field when the editor is opened." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultSimplifyTolerance]", "Default simplify tolerance (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultSimplifyTolerance]", "Default value for the simplify tolerance field (0-10m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultCurveTolerance]", "Default curve tolerance (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultCurveTolerance]", "Default value for the curve tolerance field (0-5m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultScaleFactor]", "Default scale factor" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultScaleFactor]", "Default value for the scale factor field (0.1-10x)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultMaxWays]", "Default max ways" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultMaxWays]", "Default value for the max ways field (1000-100000)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportMotorway]", "Import: motorway" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportMotorway]", "Default state for importing motorway roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTrunk]", "Import: trunk" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTrunk]", "Default state for importing trunk roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportPrimary]", "Import: primary" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportPrimary]", "Default state for importing primary roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSecondary]", "Import: secondary" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSecondary]", "Default state for importing secondary roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTertiary]", "Import: tertiary" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTertiary]", "Default state for importing tertiary roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportUnclassified]", "Import: unclassified" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportUnclassified]", "Default state for importing unclassified roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportResidential]", "Import: residential" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportResidential]", "Default state for importing residential roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportService]", "Import: service" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportService]", "Default state for importing service roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportLivingStreet]", "Import: living street" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportLivingStreet]", "Default state for importing living streets when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportRoad]", "Import: road" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportRoad]", "Default state for importing generic roads when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportMotorwayLink]", "Import: motorway link" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportMotorwayLink]", "Default state for importing motorway links when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTrunkLink]", "Import: trunk link" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTrunkLink]", "Default state for importing trunk links when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportPrimaryLink]", "Import: primary link" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportPrimaryLink]", "Default state for importing primary links when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSecondaryLink]", "Import: secondary link" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSecondaryLink]", "Default state for importing secondary links when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTertiaryLink]", "Import: tertiary link" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTertiaryLink]", "Default state for importing tertiary links when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportFootway]", "Import: footway" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportFootway]", "Default state for importing footways when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSteps]", "Import: steps" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSteps]", "Default state for importing steps (outdoor stairs) when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportBridleway]", "Import: bridleway" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportBridleway]", "Default state for importing bridleways when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportMonorail]", "Import: monorail" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportMonorail]", "Default state for importing monorails when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportRail]", "Import: rail" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportRail]", "Default state for importing railways when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportLightRail]", "Import: light rail" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportLightRail]", "Default state for importing light rail when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSubway]", "Import: subway" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportSubway]", "Default state for importing subways when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTram]", "Import: tram" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTram]", "Default state for importing trams when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTrack]", "Import: track" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportTrack]", "Default state for importing tracks when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportCycleway]", "Import: cycleway" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportCycleway]", "Default state for importing cycleways when loading OSM data." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportPedestrian]", "Import: pedestrian" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultImportPedestrian]", "Default state for importing pedestrian roads when loading OSM data." },
            // Structural elevation defaults
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultLayerElevationStep]", "Default layer step (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultLayerElevationStep]", "Default value for the layer step field (0-20m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultBridgeElevation]", "Default bridge height (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultBridgeElevation]", "Default value for the bridge height field (0-20m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultTunnelDepth]", "Default tunnel depth (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultTunnelDepth]", "Default value for the tunnel depth field (0-20m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultCoveredDepth]", "Default covered depth (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultCoveredDepth]", "Default value for the covered depth field (0-20m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultEmbankmentElevation]", "Default embankment height (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultEmbankmentElevation]", "Default value for the embankment height field (0-20m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultCuttingDepth]", "Default cutting depth (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultCuttingDepth]", "Default value for the cutting depth field (0-20m)." },
            // Generate defaults
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultIntersectionThreshold]", "Default intersection threshold (m)" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultIntersectionThreshold]", "Default value for the intersection threshold field (1-20m)." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateBikeLanes]", "Default: generate bike lanes" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateBikeLanes]", "Default state for the bike lanes toggle in the generate panel." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateTurnRestrictions]", "Default: generate turn restrictions" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateTurnRestrictions]", "Default state for the turn restrictions toggle in the generate panel." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateParkingLots]", "Default: generate parking lots" },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateParks]", "Default: generate parks" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateParks]", "Default state for the parks toggle in the generate panel." },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateParkingLots]", "Default state for the parking lots toggle in the generate panel." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateCrosswalks]", "Default: generate crosswalks" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateCrosswalks]", "Default state for the crosswalks toggle in the generate panel." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateStopSigns]", "Default: generate stop signs" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateStopSigns]", "Default state for the stop signs toggle in the generate panel." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateTrafficLights]", "Default: generate traffic lights" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateTrafficLights]", "Default state for the traffic lights toggle in the generate panel." },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateRestrictedLanes]", "Default: generate restricted lanes" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DefaultGenerateRestrictedLanes]", "Default state for the restricted lanes toggle in the generate panel." },
            // Reset import defaults button
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetImportDefaults]", "Reset import defaults" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetImportDefaults]", "Restores the import default values above to their factory settings." },
            { $"Options.WARNING[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetImportDefaults]", "This will reset the import default values above to their factory settings. Continue?" },
            // Reset structural defaults button
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetStructuralDefaults]", "Reset structural defaults" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetStructuralDefaults]", "Restores the structural elevation default values above to their factory settings." },
            { $"Options.WARNING[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetStructuralDefaults]", "This will reset the structural elevation default values above to their factory settings. Continue?" },
            // Reset generate defaults button
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetGenerateDefaults]", "Reset generate defaults" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetGenerateDefaults]", "Restores the generate default values above to their factory settings." },
            { $"Options.WARNING[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetGenerateDefaults]", "This will reset the generate default values above to their factory settings. Continue?" },
            // Reset all defaults button
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetAllDefaults]", "Reset all defaults" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetAllDefaults]", "Restores every default value on this tab to its factory setting." },
            { $"Options.WARNING[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.ResetAllDefaults]", "This will reset all default values to their factory settings. Continue?" },
        };

        internal static MemorySource CreateEditorFallbackSource()
        {
            return new MemorySource(new Dictionary<string, string>(EditorFallbacks));
        }

        internal static string T(string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key))
                return fallback;

            try
            {
                var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
                if (dictionary != null && dictionary.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch
            {
                // I18n EveryWhere hydrates the game's localization dictionary from lang/*.json.
                // If localization is not ready yet, keep the UI usable with the provided fallback.
            }

            return fallback;
        }

        internal static string F(string key, string fallback, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(key, fallback), args);
        }

        internal static string FormatCountedText(string key, string fallback, params object[] args)
        {
            return F(key, fallback, args);
        }

        internal static string GetRoadTypeName(string roadType, string fallback)
        {
            var normalized = NormalizeRoadTypeKey(roadType);
            return T($"OsmImporter.RoadType.{normalized}.Name", fallback);
        }

        internal static string GetRoadTypeDescription(string roadType, string fallback)
        {
            var normalized = NormalizeRoadTypeKey(roadType);
            return T($"OsmImporter.RoadType.{normalized}.Description", fallback);
        }

        private static string NormalizeRoadTypeKey(string roadType)
        {
            if (string.IsNullOrWhiteSpace(roadType))
                return "Road";

            var parts = roadType.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant()));
        }
    }
}
