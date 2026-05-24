using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Entities;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    public partial class OsmZoningImportSystem : GameSystemBase
    {
        private OsmZoningDocument _lastPreview;
        private double _anchorLat = 0.0;
        private double _anchorLon = 0.0;

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
                _anchorLat = doc.Features.Count > 0 ? doc.Features.Average(f => f.CentroidLat) : 0.0;
                _anchorLon = doc.Features.Count > 0 ? doc.Features.Average(f => f.CentroidLon) : 0.0;
                
                Mod.log.Info($"OSM zoning: parsed {doc.Features.Count} features (preview stored in memory). Map anchor: ({_anchorLat:F6}, {_anchorLon:F6})");

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
        // Uses coordinate mapping and reflection-based API calls to avoid compile-time coupling.
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

            // Iterate features, map coordinates, and apply zoning.
            int appliedCount = 0;
            int failedCount = 0;
            foreach (var f in _lastPreview.Features)
            {
                try
                {
                    var effectiveDensity = f.DensityOverride ?? f.Density;
                    
                    // Map lat/lon centroid to game world coordinates
                    var worldPos = ZoningCoordinateMapper.LatLonToWorldCoords(f.CentroidLat, f.CentroidLon, _anchorLat, _anchorLon);
                    
                    // Apply zoning via reflection-based API
                    ZoningApplier.ApplyZoneAtPosition(worldPos, f.Type, effectiveDensity);
                    appliedCount++;
                }
                catch (Exception ex)
                {
                    Mod.log.Warning($"OSM zoning: failed to apply zone for feature {f.Id}: {ex.Message}");
                    failedCount++;
                }
            }

            Mod.log.Info($"OSM zoning: ApplyZoning completed. Applied: {appliedCount}, Failed: {failedCount}");
        }
    }
}