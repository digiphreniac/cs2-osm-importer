using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// A divided street with rail in its median (the light rail on Martin Luther King Jr Way S) is mapped as two one-way
    /// carriageways about 6 m either side of the track. Built at CS2 widths, each carriageway's inner lane and sidewalk
    /// lay over the track. Such a carriageway's points are pushed outward, away from the track, until its built width
    /// clears it; every way sharing a moved point (the cross streets at each junction) moves with it, as do the
    /// junction's features (signals, stop signs, crosswalks, turn restrictions).
    /// </summary>
    internal static partial class OsmParser
    {
        // A carriageway point counts as beside a track within the road's half width plus this, running the same way
        // (angle at most RailYardMaxAngleDegrees) at the same level...
        private const float RailMedianReachExtraMeters = 2f;
        // ... and is pushed out to the road's half width plus this (half a CS2 double track and a little room).
        private const float RailMedianTrackHalfMeters = 4f;
        // Only carriageways running beside a track for this share of their length are moved, at most this far.
        private const float RailMedianShare = 0.5f;
        private const float RailMedianMaxShiftMeters = 5f;

        private static bool IsMedianTrack(OsmWay w)
        {
            var h = (w.Highway ?? string.Empty).ToLowerInvariant();
            return (h == "rail" || h == "light_rail") && !w.IsTunnel && !w.IsBridge;
        }

        private static bool IsCarriageway(OsmWay w) =>
            IsVehicleRoad(w) && w.IsOneWay && !w.IsBridge && !w.IsTunnel && w.Points.Count >= 2 &&
            !string.Equals(w.Highway, "service", System.StringComparison.OrdinalIgnoreCase);

        private static void PushCarriagewaysOffRail(List<OsmWay> ways, Dictionary<long, float2> sourcePositions)
        {
            const float cell = 32f;
            var grid = new Dictionary<(int, int), List<(OsmWay Track, int Seg)>>();
            foreach (var t in ways.Where(IsMedianTrack))
                for (var i = 1; i < t.Points.Count; i++)
                {
                    var lo = math.min(t.Points[i - 1].xz, t.Points[i].xz) - 12f;
                    var hi = math.max(t.Points[i - 1].xz, t.Points[i].xz) + 12f;
                    for (var x = (int)math.floor(lo.x / cell); x <= (int)math.floor(hi.x / cell); x++)
                        for (var z = (int)math.floor(lo.y / cell); z <= (int)math.floor(hi.y / cell); z++)
                        {
                            if (!grid.TryGetValue((x, z), out var l))
                                grid[(x, z)] = l = new List<(OsmWay, int)>();
                            l.Add((t, i));
                        }
                }
            if (grid.Count == 0)
                return;
            var minCos = math.cos(math.radians(RailYardMaxAngleDegrees));

            // The nearest track beside point p running along 'dir': its distance and the way out from it.
            bool Beside(float3 p, float2 dir, float reach, out float d, out float2 away)
            {
                d = float.MaxValue;
                away = default;
                if (!grid.TryGetValue(((int)math.floor(p.x / cell), (int)math.floor(p.z / cell)), out var cands))
                    return false;
                foreach (var (t, j) in cands)
                {
                    var ta = t.Points[j - 1];
                    var tb = t.Points[j];
                    var tl = math.distance(ta.xz, tb.xz);
                    if (tl < 0.01f)
                        continue;
                    var td = (tb.xz - ta.xz) / tl;
                    if (math.abs(math.dot(td, dir)) < minCos)
                        continue;
                    var q = math.lerp(ta, tb, math.saturate(math.dot(p.xz - ta.xz, td) / tl));
                    var dq = math.distance(q.xz, p.xz);
                    if (math.abs(q.y - p.y) > RailYardMaxHeightMeters || dq > reach || dq >= d || dq < 0.5f)
                        continue;
                    d = dq;
                    away = (p.xz - q.xz) / dq;
                }
                return d < float.MaxValue;
            }

            var moves = new Dictionary<(int, int), float2>();
            var roads = 0;
            foreach (var road in ways.Where(IsCarriageway))
            {
                var reach = HalfWidth(road) + RailMedianReachExtraMeters;
                var target = HalfWidth(road) + RailMedianTrackHalfMeters;
                float total = 0f, beside = 0f;
                for (var i = 1; i < road.Points.Count; i++)
                {
                    var a = road.Points[i - 1];
                    var b = road.Points[i];
                    var len = math.distance(a.xz, b.xz);
                    if (len < 0.01f)
                        continue;
                    var n = math.max(1, (int)math.ceil(len / RailYardSampleMeters));
                    for (var s = 0; s < n; s++)
                    {
                        total += len / n;
                        if (Beside(math.lerp(a, b, (s + 0.5f) / n), (b.xz - a.xz) / len, reach, out _, out _))
                            beside += len / n;
                    }
                }
                if (total <= 0f || beside < RailMedianShare * total)
                    continue;
                roads++;
                var pts = road.Points;
                for (var i = 0; i < pts.Count; i++)
                {
                    var dir = math.normalizesafe(pts[math.min(pts.Count - 1, i + 1)].xz - pts[math.max(0, i - 1)].xz);
                    if (!Beside(pts[i], dir, reach, out var d, out var away) || d >= target)
                        continue;
                    var key = PointKey(pts[i].xz);
                    var shift = away * math.min(target - d, RailMedianMaxShiftMeters);
                    moves[key] = moves.TryGetValue(key, out var m) && math.lengthsq(m) > math.lengthsq(shift) ? m : shift;
                }
            }
            if (moves.Count == 0)
                return;

            // Every way's points (and features) at a moved point move with it.
            float2 Moved(float2 p) => moves.TryGetValue(PointKey(p), out var m) ? p + m : p;
            float3 Moved3(float3 p) => moves.TryGetValue(PointKey(p.xz), out var m) ? new float3(p.x + m.x, p.y, p.z + m.y) : p;
            foreach (var w in ways)
            {
                for (var i = 0; i < w.Points.Count; i++)
                    w.Points[i] = Moved3(w.Points[i]);
                foreach (var list in new[] { w.Crosswalks, w.JunctionCrossings, w.StopSigns, w.TrafficLights })
                    foreach (var f in list)
                        f.Position = Moved3(f.Position);
                foreach (var r in w.TurnRestrictions)
                    r.ViaPosition = Moved3(r.ViaPosition);
            }
            foreach (var id in sourcePositions.Keys.ToList())
                sourcePositions[id] = Moved(sourcePositions[id]);
            var largest = moves.Values.Max(m => math.length(m));
            Mod.log.Info($"Rail medians: {roads} one-way carriageways beside a track pushed off it ({moves.Count} points moved up to {largest:F1} m, " +
                         $"to the road's half width + {RailMedianTrackHalfMeters:F0} m from the track; ways sharing those points moved with them).");
        }
    }
}
