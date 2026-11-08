using System.Collections.Generic;
using System.Linq;
using Game.Simulation;
using Unity.Jobs;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// Waits for the sea to reach the map's sea level before any road is queued. The water rises over minutes after
    /// the sea level is set; CS2 puts the nodes it adds when it splits a long pier piece on the water surface of the
    /// moment, not at the deck height. Generated right after setting the sea level to 2 m, 47 pier nodes on the Lake
    /// Union crop sat at 1.4-1.5 m and the piers dipped under the water once it had risen; generated after the water
    /// had settled, none did.
    /// </summary>
    public partial class OsmImportSystem
    {
        // The water counts as settled within this of the sea level (it levels off a few centimetres below it).
        private const float SeaSettledMargin = 0.1f;
        // Never wait longer than this; generation then goes ahead with a warning.
        private const double SeaWaitTimeoutSeconds = 600;
        private const int SeaWaitProbeCount = 24;

        private readonly List<float3> _seaProbes = new List<float3>();
        private readonly System.Diagnostics.Stopwatch _seaWaitClock = new System.Diagnostics.Stopwatch();
        private WorkPhase _phaseAfterSeaWait;
        private int _seaWaitFrame;

        /// <summary>
        /// Called at the end of PrepareGeneration with the phase generation would start in. Goes to WaitForSeaLevel when
        /// piers stand where the sea should be and the water there is still below the sea level.
        /// </summary>
        private WorkPhase SeaWaitOr(WorkPhase next)
        {
            _seaProbes.Clear();
            var sea = MapSeaLevel;
            if (sea <= 0f || _document == null)
                return next;
            // Probe points: pier points whose ground is clearly below the sea level (that water comes from the sea).
            var candidates = _document.Ways.Where(w => w.IsPier).SelectMany(w => w.Points)
                .Where(p => SampleTerrainHeight(new float3(p.x, 0f, p.z)) < sea - 0.5f)
                .ToList();
            if (candidates.Count == 0)
                return next;
            var step = math.max(1, candidates.Count / SeaWaitProbeCount);
            for (var i = 0; i < candidates.Count && _seaProbes.Count < SeaWaitProbeCount; i += step)
                _seaProbes.Add(candidates[i]);
            var level = MedianWaterSurface();
            if (level >= sea - SeaSettledMargin)
                return next;
            _phaseAfterSeaWait = next;
            _seaWaitClock.Restart();
            _seaWaitFrame = 0;
            Mod.log.Info($"Sea level: the water where piers stand is at {level:F2} m, the sea level is {sea:F2} m; waiting for it to rise before building.");
            StatusSummary = LF("OsmImporter.Status.WaitingForSea", "Waiting for the sea to rise ({0:F1} of {1:F1} m)...", level, sea);
            return WorkPhase.WaitForSeaLevel;
        }

        private void ProcessSeaWait()
        {
            if (++_seaWaitFrame % 30 != 0)
                return;
            var sea = MapSeaLevel;
            var level = MedianWaterSurface();
            var timedOut = _seaWaitClock.Elapsed.TotalSeconds > SeaWaitTimeoutSeconds;
            if (level >= sea - SeaSettledMargin || timedOut)
            {
                if (timedOut)
                    Mod.log.Warn($"Sea level: still {level:F2} m of {sea:F2} m after {SeaWaitTimeoutSeconds:F0} s; building anyway (piers may end up below the water).");
                else
                    Mod.log.Info($"Sea level: the water reached {level:F2} m after {_seaWaitClock.Elapsed.TotalSeconds:F0} s.");
                _seaWaitClock.Stop();
                StatusSummary = L("OsmImporter.Status.GeneratingRoads", "generating...");
                _phase = _phaseAfterSeaWait;
                return;
            }
            StatusSummary = LF("OsmImporter.Status.WaitingForSea", "Waiting for the sea to rise ({0:F1} of {1:F1} m)...", level, sea);
        }

        /// <summary>Median water surface height (ground + water depth) at the probe points.</summary>
        private float MedianWaterSurface()
        {
            var water = World.GetExistingSystemManaged<WaterSystem>();
            if (water == null || _seaProbes.Count == 0)
                return float.MaxValue;
            CacheTerrainHeightData();
            var surface = water.GetSurfaceData(out JobHandle deps);
            deps.Complete();
            var levels = new List<float>(_seaProbes.Count);
            foreach (var p in _seaProbes)
            {
                var at = new float3(p.x, 0f, p.z);
                levels.Add(SampleTerrainHeight(at) + WaterUtils.SampleDepth(ref surface, at));
            }
            levels.Sort();
            return levels[levels.Count / 2];
        }
    }
}
