using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Localization;
using Game;
using Game.SceneFlow;

namespace cs2_osm_importer
{
    internal static class OsmLocalization
    {
        private static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            { "Editor.TOOL[OSM Importer Tool]", "OSM Importer Tool" },
            { "Editor.FILTERS", "Road types" },

            { "OsmImporter.Panel.Title", "OSM Importer Tool" },
            { "OsmImporter.Label.ImportHeader", "OSM Import Tool" },
            { "OsmImporter.Label.Path", "OSM file path" },
            { "OsmImporter.Label.SimplifyTolerance", "Simplify tolerance (m)" },
            { "OsmImporter.Label.CurveTolerance", "Curve tolerance (m)" },
            { "OsmImporter.Label.ScaleFactor", "Scale factor" },
            { "OsmImporter.Label.MaxWays", "Max ways" },
            { "OsmImporter.Label.RoadTypesToImport", "Road types to import" },
            { "OsmImporter.Label.ImportRoadTypes", "Import filter" },
            { "OsmImporter.Label.ImportRoadTypesInstructions", "Choose which road types should be loaded from the OSM file." },
            { "OsmImporter.Label.GenerateRoadTypesInstructions", "Choose which of the imported road types should be generated." },
            { "OsmImporter.Label.StructuralElevation", "Structural elevation" },
            { "OsmImporter.Label.LayerElevationStep", "Layer step (m)" },
            { "OsmImporter.Label.BridgeElevation", "Bridge height (m)" },
            { "OsmImporter.Label.TunnelDepth", "Tunnel depth (m)" },
            { "OsmImporter.Label.CoveredDepth", "Covered depth (m)" },
            { "OsmImporter.Label.EmbankmentElevation", "Embankment height (m)" },
            { "OsmImporter.Label.CuttingDepth", "Cutting depth (m)" },
            { "OsmImporter.Tooltip.SimplifyTolerance", "Simplifies road geometry before import." },
            { "OsmImporter.Tooltip.CurveTolerance", "Controls how tightly curves are followed." },
            { "OsmImporter.Tooltip.ScaleFactor", "Scales the imported map size." },
            { "OsmImporter.Tooltip.MaxWays", "Limits how many OSM ways are imported." },
            { "OsmImporter.Tooltip.LayerElevationStep", "Height added for each OSM layer level." },
            { "OsmImporter.Tooltip.BridgeElevation", "Extra height added to roads tagged as bridges." },
            { "OsmImporter.Tooltip.TunnelDepth", "Depth subtracted from roads tagged as tunnels." },
            { "OsmImporter.Tooltip.CoveredDepth", "Depth subtracted from roads tagged as covered." },
            { "OsmImporter.Tooltip.EmbankmentElevation", "Height added to roads tagged as embankments." },
            { "OsmImporter.Tooltip.CuttingDepth", "Depth subtracted from roads tagged as cuttings." },
            { "OsmImporter.Label.AdvancedSettings", "Advanced settings" },
            { "OsmImporter.Label.ImportData", "Import data" },
            { "OsmImporter.Label.ConfirmImport", "Confirm OSM import" },
            { "OsmImporter.Label.RoadTypes", "Road types" },
            { "OsmImporter.Label.GenerateRoads", "Generate Roads" },
            { "OsmImporter.Label.SupportedTypes", "Supported types: {0}" },
            { "OsmImporter.Label.LoadWarning", "Loading OSM data may temporarily freeze the game while the file is parsed and road definitions are generated." },
            { "OsmImporter.Label.ImportInstructions", "Pick an OSM input file and configure how the data is parsed before loading it into the game." },
            { "OsmImporter.Label.GenerateInstructions", "Choose which loaded road types should be generated." },

            { "OsmImporter.Button.LoadOSMData", "Load OSM Data" },
            { "OsmImporter.Button.Continue", "Continue" },
            { "OsmImporter.Button.Cancel", "Cancel" },
            { "OsmImporter.Button.ResetDefaults", "Reset to defaults" },
            { "OsmImporter.Button.GenerateRoads", "Generate Roads" },
            { "OsmImporter.Button.DeleteAllSegments", "Delete all segments" },
{ "OsmImporter.Format.RoadTypeCount", "{0} ({1})" },
            { "OsmImporter.Format.SelectedCount", "{0} selected" },
            { "OsmImporter.Format.LaneCount", "{0}-lane: {1}" },
            { "OsmImporter.Format.UnknownLaneCount", "unknown: {0}" },
            { "OsmImporter.Value.None", "none" },
            { "OsmImporter.Value.AllLoadedTypes", "all loaded types" },
            { "OsmImporter.Value.AllSupportedTypes", "all supported types" },
            { "OsmImporter.Value.Unknown", "unknown" },

            { "OsmImporter.Status.Ready", "Ready." },
            { "OsmImporter.Status.EnterValidPath", "Enter a valid OSM file path." },
            { "OsmImporter.Status.InvalidPath", "The selected file path is not a valid OSM file." },
            { "OsmImporter.Status.LoadStarted", "OSM file path accepted. Loading..." },
            { "OsmImporter.Status.LoadBeforeGenerate", "Load OSM data before generating roads." },
            { "OsmImporter.Status.GeneratingPrepared", "Preparing road generation..." },
            { "OsmImporter.Status.GeneratingRoads", "generating..." },
            { "OsmImporter.Status.LoadingWithWarning", "loading... (game might be frozen for some time)" },
            { "OsmImporter.Status.DeletingSegments", "Deleting all segments..." },
            { "OsmImporter.Status.ParsingFile", "Parsing OSM file..." },
            { "OsmImporter.Status.NoSupportedWays", "No supported OSM highway ways were found." },
            { "OsmImporter.Status.LoadComplete", "Loaded {0} OSM ways. Select road types and click Generate." },
            { "OsmImporter.Status.LoadFailed", "Failed to load OSM data: {0}" },
            { "OsmImporter.Status.NoUsablePrefab", "No usable road prefab was found. Open a city and try again." },
            { "OsmImporter.Status.PrefabMissingNetData", "Road prefab '{0}' has no NetData." },
            { "OsmImporter.Status.PrepareGenerationFailed", "Failed to prepare road generation: {0}" },
            { "OsmImporter.Status.GenerateFailed", "Failed to generate roads: {0}" },
            { "OsmImporter.Status.WaitingFailed", "Failed while waiting for generated roads: {0}" },
            { "OsmImporter.Status.DeleteFailed", "Failed to delete segments: {0}" },
            { "OsmImporter.Status.LoadInProgress", "An OSM import is already running." },
            { "OsmImporter.Status.OperationInProgress", "An OSM operation is already running." },
            { "OsmImporter.Status.DefaultsRestored", "Defaults restored." },
            { "OsmImporter.Status.AllRoadTypesSelected", "All supported road types selected." },
            { "OsmImporter.Status.AllRoadTypesDeselected", "All supported road types deselected." },
            { "OsmImporter.Status.QueuedGeneration", "Queued {0} road definitions. Waiting for CS2 to generate roads..." },
            { "OsmImporter.Status.GenerationProgress", "Generating \"{0}\" roads ({1}/{2})" },
            { "OsmImporter.Status.GenerationSelected", "Generating selected OSM roads..." },
            { "OsmImporter.Status.WaitingForCS2", "Waiting for CS2 road generation... {0}/{1} definitions consumed" },
            { "OsmImporter.Status.CS2Verifying", "CS2 consumed {0} OSM definitions. Verifying generated roads..." },
            { "OsmImporter.Status.GeneratedRoads", "CS2 consumed {0} road definitions; detected {1} new road edges. Import bounds: {2}. Names: {3}" },
            { "OsmImporter.Status.GeneratedRoadsUnknown", "CS2 consumed {0} road definitions. Generated road count could not be verified. Names: {1}" },
            { "OsmImporter.Status.GeneratedRoadsUnknownSimple", "CS2 consumed {0} road definitions. Generated road count could not be verified." },
            { "OsmImporter.Status.WaitingContinue", "Queued {0} road definitions; CS2 consumed {1} so far. Leave the editor running or try fewer road types." },
            { "OsmImporter.Status.DeleteComplete", "Marked {0} edges for deletion." },
            { "OsmImporter.Status.ValidationNotApplied", "Validation: names not yet applied." },
            { "OsmImporter.Status.ValidationSummary", "Validation: {0} new edges ({1} baseline), {2} named, {3} unnamed. Queued: {4} total, {5} non-empty. Samples: {6}" },
            { "OsmImporter.Bounds.ProjectedImport", "Projected import bounds: {0}" },
            { "OsmImporter.Bounds.Short", "{0:F0}m x {1:F0}m, y={2:F1}m" },
            { "OsmImporter.Bounds.ProjectedOSM", "Projected OSM bounds: center lat/lon {0:F6}, {1:F6}; x {2:F1}..{3:F1}m, z {4:F1}..{5:F1}m, size {6:F1}m x {7:F1}m, elevation y={8:F1}m" },

            { "IntersectionThreshold", "Intersection threshold (m)" },
            { "IntersectionThreshold.Description", "Maximum XZ distance from a road for a pillar to be considered intersecting (1-20m)." },
            { "OsmImporter.Status.PillarCleanupComplete", "Pillar cleanup: {0}/{1} intersecting pillars removed." },
            { "OsmImporter.Status.PillarCleanupFailed", "Failed during pillar intersection cleanup: {0}" },
            { "OsmImporter.Status.RoadGenerationComplete", "Road generation complete." },
            { "OsmImporter.RoadType.Motorway.Name", "Motorway" },
            { "OsmImporter.RoadType.Motorway.Description", "High-capacity, access-controlled road for long-distance traffic." },
            { "OsmImporter.RoadType.Trunk.Name", "Trunk" },
            { "OsmImporter.RoadType.Trunk.Description", "Major intercity road that carries important through traffic." },
            { "OsmImporter.RoadType.Primary.Name", "Primary" },
            { "OsmImporter.RoadType.Primary.Description", "Main road connecting important towns, districts, and hubs." },
            { "OsmImporter.RoadType.Secondary.Name", "Secondary" },
            { "OsmImporter.RoadType.Secondary.Description", "Significant connector between smaller towns and city districts." },
            { "OsmImporter.RoadType.Tertiary.Name", "Tertiary" },
            { "OsmImporter.RoadType.Tertiary.Description", "Local connector that feeds traffic into higher-order roads." },
            { "OsmImporter.RoadType.Unclassified.Name", "Unclassified" },
            { "OsmImporter.RoadType.Unclassified.Description", "Minor public road used for general local access." },
            { "OsmImporter.RoadType.Residential.Name", "Residential" },
            { "OsmImporter.RoadType.Residential.Description", "Neighborhood street serving homes and local access." },
            { "OsmImporter.RoadType.Service.Name", "Service" },
            { "OsmImporter.RoadType.Service.Description", "Access road for buildings, parking, depots, and utilities." },
            { "OsmImporter.RoadType.LivingStreet.Name", "Living street" },
            { "OsmImporter.RoadType.LivingStreet.Description", "Low-speed shared street where pedestrians have priority." },
            { "OsmImporter.RoadType.Road.Name", "Road" },
            { "OsmImporter.RoadType.Road.Description", "Generic road tag used when no more specific type is known." },
            { "OsmImporter.RoadType.MotorwayLink.Name", "Motorway link" },
            { "OsmImporter.RoadType.MotorwayLink.Description", "Ramp or connector linking a motorway to nearby roads." },
            { "OsmImporter.RoadType.TrunkLink.Name", "Trunk link" },
            { "OsmImporter.RoadType.TrunkLink.Description", "Connector road feeding traffic into or out of a trunk route." },
            { "OsmImporter.RoadType.PrimaryLink.Name", "Primary link" },
            { "OsmImporter.RoadType.PrimaryLink.Description", "Connector between primary roads and the wider network." },
            { "OsmImporter.RoadType.SecondaryLink.Name", "Secondary link" },
            { "OsmImporter.RoadType.SecondaryLink.Description", "Connector between secondary roads and local streets." },
            { "OsmImporter.RoadType.TertiaryLink.Name", "Tertiary link" },
            { "OsmImporter.RoadType.TertiaryLink.Description", "Connector between tertiary roads and nearby local access." },
            { "OsmImporter.RoadType.Footway.Name", "Footway" },
            { "OsmImporter.RoadType.Footway.Description", "Path designed for pedestrians, often separate from the road network." },
            { "OsmImporter.RoadType.Bridleway.Name", "Bridleway" },
            { "OsmImporter.RoadType.Bridleway.Description", "Path intended for horse riding, also open to pedestrians." },
            { "OsmImporter.RoadType.Monorail.Name", "Monorail" },
            { "OsmImporter.RoadType.Monorail.Description", "Single-rail railway system for passenger transport." },
            { "OsmImporter.RoadType.Rail.Name", "Rail" },
            { "OsmImporter.RoadType.Rail.Description", "Standard railway track for passenger and freight trains." },
            { "OsmImporter.RoadType.LightRail.Name", "Light rail" },
            { "OsmImporter.RoadType.LightRail.Description", "Urban rail system that combines tram-like flexibility with higher capacity." },
            { "OsmImporter.RoadType.Subway.Name", "Subway" },
            { "OsmImporter.RoadType.Subway.Description", "Underground metro system for high-capacity urban transit." },
            { "OsmImporter.RoadType.Tram.Name", "Tram" },
            { "OsmImporter.RoadType.Tram.Description", "Street-running light rail system for city transport." },
            { "OsmImporter.RoadType.Track.Name", "Track" },
            { "OsmImporter.RoadType.Track.Description", "Unpaved or rough rural road used for agriculture and forestry." },
            { "OsmImporter.RoadType.Cycleway.Name", "Cycleway" },
            { "OsmImporter.RoadType.Cycleway.Description", "Dedicated path or lane for bicycle traffic." },
            { "OsmImporter.RoadType.Pedestrian.Name", "Pedestrian" },
            { "OsmImporter.RoadType.Pedestrian.Description", "Street or zone reserved for pedestrian use." },
        };

        internal static string T(string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key))
                return fallback;

            try
            {
                var entries = GameManager.instance?.localizationManager?.activeDictionary?.entries;
                if (entries != null)
                {
                    var match = entries.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
                    if (!string.IsNullOrWhiteSpace(match.Value))
                        return match.Value;
                }
            }
            catch
            {
                // Fall back to the provided string.
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

        internal static MemorySource CreateEnglishSource()
        {
            return new MemorySource(new Dictionary<string, string>(English));
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
