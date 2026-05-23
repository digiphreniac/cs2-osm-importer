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
            { $"Options.GROUP[{Mod.Id}.{Mod.Id}.Mod.Developer]", "Developer" },
            { $"Options.OPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DeveloperMode]", "Developer Mode" },
            { $"Options.OPTION_DESCRIPTION[{Mod.Id}.{Mod.Id}.Mod.OsmImporterSettings.DeveloperMode]", "Enables debug UI bindings and developer-only features for the OSM Importer mod." }
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
