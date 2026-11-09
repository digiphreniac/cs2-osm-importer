using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// Protected bike lanes are often mapped as their own highway=cycleway beside the street (Eastlake Avenue's, one
    /// per side). Built literally they were separate bike paths lying along the road's edges, crossing each other and
    /// the road at every junction (a junction at Roosevelt Way with eight bike path pieces in it). CS2 roads carry bike
    /// lanes of their own, so such a track goes the way of a mapped sidewalk: the road gets bike lanes on that side and
    /// the track is dropped. Bike paths continuing from it are extended onto the road like paths that ended on a
    /// sidewalk. A track that leaves the road for most of its length stays a bike path.
    /// </summary>
    internal static partial class OsmParser
    {
        // A track counts as beside a road when its samples are within the road's half width plus this...
        private const float SidepathExtraMeters = 4f;
        // ... running along it (angle between them at most this)...
        private const float SidepathMaxAngleDegrees = 25f;
        // ... for at least this share of its length.
        private const float SidepathShare = 0.8f;
        private const float SidepathSampleMeters = 4f;

        private static bool CanHaveBikeLanes(OsmWay road)
        {
            if (!IsVehicleRoad(road))
                return false;
            switch ((road.Highway ?? string.Empty).ToLowerInvariant())
            {
                case "motorway": case "motorway_link":
                    return false;
                default:
                    return true;
            }
        }

        private static List<OsmWay> FindSidepathCycleways(List<OsmWay> ways)
        {
            var found = new List<OsmWay>();
            var roads = ways.Where(CanHaveBikeLanes).ToList();
            if (roads.Count == 0)
                return found;
            const float cell = 32f;
            var grid = new Dictionary<(int, int), List<(OsmWay Road, int Seg)>>();
            foreach (var road in roads)
                for (var i = 1; i < road.Points.Count; i++)
                {
                    var reach = HalfWidth(road) + SidepathExtraMeters;
                    var lo = math.min(road.Points[i - 1].xz, road.Points[i].xz) - reach;
                    var hi = math.max(road.Points[i - 1].xz, road.Points[i].xz) + reach;
                    for (var x = (int)math.floor(lo.x / cell); x <= (int)math.floor(hi.x / cell); x++)
                        for (var z = (int)math.floor(lo.y / cell); z <= (int)math.floor(hi.y / cell); z++)
                        {
                            if (!grid.TryGetValue((x, z), out var l))
                                grid[(x, z)] = l = new List<(OsmWay, int)>();
                            l.Add((road, i));
                        }
                }

            var minCos = math.cos(math.radians(SidepathMaxAngleDegrees));
            var lanesAdded = 0;
            foreach (var track in ways)
            {
                if (!string.Equals(track.Highway, "cycleway", StringComparison.OrdinalIgnoreCase) || IsBikeCrossing(track) ||
                    track.IsBridge || track.IsTunnel || track.Points.Count < 2)
                    continue;
                var total = 0f;
                var beside = 0f;
                var sides = new Dictionary<OsmWay, (float Left, float Right)>();
                for (var i = 1; i < track.Points.Count; i++)
                {
                    var a = track.Points[i - 1].xz;
                    var b = track.Points[i].xz;
                    var len = math.distance(a, b);
                    if (len < 0.01f)
                        continue;
                    var dir = (b - a) / len;
                    var n = math.max(1, (int)math.ceil(len / SidepathSampleMeters));
                    for (var s = 0; s < n; s++)
                    {
                        var p = math.lerp(a, b, (s + 0.5f) / n);
                        var piece = len / n;
                        total += piece;
                        if (!grid.TryGetValue(((int)math.floor(p.x / cell), (int)math.floor(p.y / cell)), out var cands))
                            continue;
                        OsmWay best = null;
                        var bestD = float.MaxValue;
                        var bestCross = 0f;
                        foreach (var (road, seg) in cands)
                        {
                            var ra = road.Points[seg - 1].xz;
                            var rb = road.Points[seg].xz;
                            var rlen = math.distance(ra, rb);
                            if (rlen < 0.01f)
                                continue;
                            var rdir = (rb - ra) / rlen;
                            if (math.abs(math.dot(rdir, dir)) < minCos)
                                continue;
                            var t = math.saturate(math.dot(p - ra, rdir) / rlen);
                            var q = math.lerp(ra, rb, t);
                            var d = math.distance(p, q);
                            if (d > HalfWidth(road) + SidepathExtraMeters || d >= bestD)
                                continue;
                            bestD = d;
                            best = road;
                            var v = p - q;
                            bestCross = rdir.x * v.y - rdir.y * v.x;
                        }
                        if (best == null)
                            continue;
                        beside += piece;
                        sides.TryGetValue(best, out var sd);
                        sides[best] = bestCross > 0f ? (sd.Left + piece, sd.Right) : (sd.Left, sd.Right + piece);
                    }
                }
                if (total <= 0f || beside < SidepathShare * total)
                    continue;
                found.Add(track);
                foreach (var kv in sides)
                {
                    var road = kv.Key;
                    var side = kv.Value.Left > 0f && kv.Value.Right > 0f ? OsmRoadFeatureSide.Both
                        : kv.Value.Left > 0f ? OsmRoadFeatureSide.Left : OsmRoadFeatureSide.Right;
                    if (!road.HasBikeLanes)
                        lanesAdded++;
                    road.HasBikeLanes = true;
                    road.BikeLaneSide = MergeRoadFeatureSide(road.BikeLaneSide, side);
                }
            }
            Mod.log.Info($"Bike tracks: {found.Count} cycleways mapped beside a road dropped (within the road's half width + {SidepathExtraMeters:F0} m for " +
                         $"{SidepathShare * 100f:F0}% of their length); {lanesAdded} roads given bike lanes for them. Longest: " +
                         string.Join(", ", found.OrderByDescending(GetWayLength).Take(12).Select(w => $"{(string.IsNullOrEmpty(w.Name) ? w.Id.ToString() : w.Name)} {GetWayLength(w):F0} m")) + ".");
            return found;
        }
    }
}
