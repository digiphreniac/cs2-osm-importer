using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Entities;

namespace cs2_osm_importer
{
    public partial class OsmZoningImportSystem : GameSystemBase
    {
        private OsmZoningDocument _lastPreview;
        public void LoadZoningFromFile(string path)
        {
            Mod.log.Info($"OSM zoning: requested load for '{path}'");
            if (string.IsNullOrWhiteSpace(path))
            {
                Mod.log.Info("OSM zoning: no path provided");
                return;
            }

            try
            {
                var doc = OsmZoningParser.Parse(path);
                _lastPreview = doc;
                Mod.log.Info($"OSM zoning: parsed {doc.Features.Count} features (preview stored in memory).");

                // Optionally write a JSON preview to the mod directory or temp directory.
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

        // Return number of parsed features in last preview.
        public int GetPreviewFeatureCount()
        {
            return _lastPreview?.Features?.Count ?? 0;
        }

        // Return shallow copy of features for UI consumption. Avoid direct mutation in UI.
        public List<OsmZoningFeature> GetPreviewFeatures()
        {
            if (_lastPreview?.Features == null)
                return new List<OsmZoningFeature>();
            return _lastPreview.Features.Select(f => f).ToList();
        }

        // Set or clear density override for a feature by id.
        public void SetFeatureDensityOverride(long featureId, DensityLevel? overrideValue)
        {
            if (_lastPreview?.Features == null)
                return;
            var f = _lastPreview.Features.FirstOrDefault(x => x.Id == featureId);
            if (f != null)
            {
                f.DensityOverride = overrideValue;
                Mod.log.Info($"OSM zoning: set density override for feature {featureId} = {(overrideValue.HasValue ? overrideValue.Value.ToString() : "Auto")}");
                // persist preview JSON with overrides
                PersistPreviewJson();
            }
        }

        public DensityLevel? GetFeatureDensityOverride(long featureId)
        {
            if (_lastPreview?.Features == null)
                return null;
            var f = _lastPreview.Features.FirstOrDefault(x => x.Id == featureId);
            return f?.DensityOverride;
        }

        private void PersistPreviewJson()
        {
            try
            {
                var outDir = string.IsNullOrWhiteSpace(Mod.ModDirectory) ? Path.GetTempPath() : Mod.ModDirectory;
                var outPath = Path.Combine(outDir, "osm_zoning_preview.json");
                var json = JsonSerializer.Serialize(_lastPreview, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(outPath, json);
                Mod.log.Info($"OSM zoning: updated preview JSON with overrides at {outPath}");
            }
            catch (Exception ex)
            {
                Mod.log.Warning($"OSM zoning: failed to persist preview JSON: {ex.Message}");
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

            // Guard: Use reflection to detect a SceneFlow/Play-mode indicator to avoid compile-time dependency on game internals.
            bool isPlayMode = false;
            try
            {
                var t = Type.GetType("Game.SceneFlow.SceneFlowManager, Assembly-CSharp") ?? Type.GetType("SceneFlowManager, Assembly-CSharp");
                if (t != null)
                {
                    var m = t.GetMethod("IsInGame", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null)
                        isPlayMode = (bool)m.Invoke(null, null);
                }
            }
            catch { }

            if (!isPlayMode)
            {
                Mod.log.Info("OSM zoning: ApplyZoning aborted: not in Play mode.");
                return;
            }

            // Iterate features and apply zoning. Actual game API calls are TODO; log intended actions for now.
            foreach (var f in _lastPreview.Features)
            {
                var effectiveDensity = f.DensityOverride ?? f.Density;
                Mod.log.Info($"OSM zoning: would apply {f.Type} at ({f.CentroidLat:F6},{f.CentroidLon:F6}) with density {effectiveDensity} (feature {f.Id})");
                // TODO: translate lat/lon centroid to game world coordinates and call zoning API to set zone and density.
            }

            Mod.log.Info("OSM zoning: ApplyZoning completed (logged). Implement actual game zoning API calls in code.");
        }
    }
}