using System;
using System.IO;
using System.Text.Json;
using Unity.Entities;

namespace cs2_osm_importer
{
    public partial class OsmZoningImportSystem : GameSystemBase
    {
        private OsmZoningDocument _lastPreview;
n        public void LoadZoningFromFile(string path)
        {
            Mod.log.Info($"OSM zoning: requested load for '{path}'");
            if (string.IsNullOrWhiteSpace(path))
            {
                Mod.log.Info("OSM zoning: no path provided");
                return;
            }
n            try
            {
                var doc = OsmZoningParser.Parse(path);
                _lastPreview = doc;
                Mod.log.Info($"OSM zoning: parsed {doc.Features.Count} features (preview stored in memory).");
n                // Optionally write a JSON preview to the mod directory or temp directory.
                var outDir = string.IsNullOrWhiteSpace(Mod.ModDirectory) ? Path.GetTempPath() : Mod.ModDirectory;
                var outPath = Path.Combine(outDir, "osm_zoning_preview.json");
                try
                {
                    var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(outPath, json);
                    Mod.log.Info($"OSM zoning: wrote preview JSON to {outPath}");
                }
                catch (Exception ex)
                {
                    Mod.log.Warning($"OSM zoning: failed to write preview JSON: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error($"OSM zoning: failed to parse file: {ex.Message}");
            }
        }

        // Apply zoning into the running game. This operation must be performed while the city is running (Play mode).
        // The real integration with the game's zoning APIs needs to run in a safe context; this method is a guarded entry point.
        public void ApplyZoning()
        {
            if (_lastPreview == null)
            {
                Mod.log.Info("OSM zoning: nothing to apply (no preview). Use LoadZoningFromFile first.");
                return;
            }
n            // Guard: Require Play mode to actually change game state. The exact check and API calls are left as TODOs.
            Mod.log.Info("OSM zoning: ApplyZoning called. This operation must be invoked while the game is running (Play mode). Implementation of actual game zoning application is pending.");
        }
    }
}