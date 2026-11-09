using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace cs2_osm_importer
{
    /// <summary>
    /// Bare-earth heightmaps fill the ground under a removed bridge up to near its deck: the I-5 trench under Yesler
    /// Way is a bank in the Seattle heightmap. A way passing under such a bridge dips into a cut (OsmParser.Grades), but
    /// the deck still stood only a few metres above the filled ground there, and CS2 gives a road pillars only where
    /// it stands at least twice its prefab's elevation step above the ground (NetCompositionHelpers.GetElevationFlags;
    /// 8 m for a Small Road): below that it builds walls. Before generation reads ground heights, the ground in each
    /// recorded trench (OsmDocument.Trenches) is lowered to the height of the way below, with 45-degree sides, in the
    /// map's own heightmap (TerrainSystem.ReplaceHeightmap, as the editor's heightmap import does).
    /// </summary>
    public partial class OsmImportSystem
    {
        // The sides of a trench rise one metre per metre out from its edge.
        private const float TrenchSideSlope = 1f;
        // The trench floor sits this far below the way, so the way is laid on it rather than clipped into it.
        private const float TrenchFloorBelowWay = 0.2f;
        // Frames to wait after replacing the heightmap before the game's CPU copy of the heights is read again.
        private const int TrenchSettleFrames = 30;

        private WorkPhase _phaseAfterTrenches;
        private int _trenchWaitFrames;

        /// <summary>Lowers the ground in every recorded trench. Returns false when there was nothing to lower.</summary>
        private bool CarveTrenches()
        {
            var trenches = _document?.Trenches;
            if (trenches == null || trenches.Count == 0 || _terrainSystem == null)
                return false;
            var texture = _terrainSystem.heightmap;
            if (texture == null)
                return false;
            var width = texture.width;
            var height = texture.height;
            var heights = new NativeArray<ushort>(width * height, Allocator.Persistent);
            try
            {
                var request = AsyncGPUReadback.RequestIntoNativeArray(ref heights, texture);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    Mod.log.Warn("Trenches: could not read the heightmap; the ground under bridges is left as it is.");
                    return false;
                }
                var scale = math.max(1f, _terrainSystem.heightScaleOffset.x);
                var offset = _terrainSystem.heightScaleOffset.y;
                var origin = _terrainSystem.playableOffset;
                var cell = _terrainSystem.playableArea / new float2(width, height);
                var lowered = 0;
                var maxDepth = 0f;
                foreach (var t in trenches)
                {
                    var side = new float2(-t.Direction.y, t.Direction.x);
                    var reach = math.max(t.HalfLength, t.HalfWidth) + 30f;
                    var lo = (int2)math.floor((t.Center - reach - origin) / cell);
                    var hi = (int2)math.ceil((t.Center + reach - origin) / cell);
                    lo = math.clamp(lo, 0, new int2(width - 1, height - 1));
                    hi = math.clamp(hi, 0, new int2(width - 1, height - 1));
                    for (var z = lo.y; z <= hi.y; z++)
                        for (var x = lo.x; x <= hi.x; x++)
                        {
                            var p = origin + (new float2(x, z) + 0.5f) * cell - t.Center;
                            var outside = math.max(math.abs(math.dot(p, t.Direction)) - t.HalfLength, math.abs(math.dot(p, side)) - t.HalfWidth);
                            var allowed = t.Floor - TrenchFloorBelowWay + math.max(0f, outside) * TrenchSideSlope;
                            var i = z * width + x;
                            var current = heights[i] / 65535f * scale + offset;
                            if (current <= allowed)
                                continue;
                            heights[i] = (ushort)math.clamp(math.round((allowed - offset) / scale * 65535f), 0f, 65535f);
                            lowered++;
                            maxDepth = math.max(maxDepth, current - allowed);
                        }
                }
                if (lowered == 0)
                    return false;
                var map = new Texture2D(width, height, GraphicsFormat.R16_UNorm, TextureCreationFlags.DontInitializePixels);
                map.SetPixelData(heights, 0);
                map.Apply();
                _terrainSystem.ReplaceHeightmap(map);
                UnityEngine.Object.Destroy(map);
                Mod.log.Info($"Trenches: ground lowered under {trenches.Count} bridges where the way below dips ({lowered} heightmap cells, at most {maxDepth:F1} m).");
                return true;
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Trenches: lowering the ground failed ({ex.Message}); the ground under bridges is left as it is.");
                return false;
            }
            finally
            {
                heights.Dispose();
            }
        }

        /// <summary>Waits for the game to take the lowered heightmap, reads the ground heights again and goes on.</summary>
        private void ProcessTrenchWait()
        {
            if (++_trenchWaitFrames < TrenchSettleFrames)
                return;
            _terrainSystem.GetHeightData(true);
            CacheTerrainHeightData();
            _phase = SeaWaitOr(_phaseAfterTrenches);
        }
    }
}
