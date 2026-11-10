using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// Rail yards are mapped with service roads running between and beside the tracks (Interbay, SODO). Built as roads
    /// they lie on top of the tracks next to them, since a CS2 road and track are wider than the gap mapped between
    /// them. Such a road is dropped when it runs along rail for most of its length, unless dropping it would cut other
    /// roads off from the rest of the network (then it stays, overlap and all).
    /// </summary>
    internal static partial class OsmParser
    {
        // A road sample counts as along rail when a track runs within the road's half width plus this...
        private const float RailYardExtraMeters = 2f;
        // ... in the same direction (angle at most this) and at the same level...
        private const float RailYardMaxAngleDegrees = 20f;
        private const float RailYardMaxHeightMeters = 3f;
        // ... and the road is dropped when this share of its length is.
        private const float RailYardShare = 0.6f;
        private const float RailYardSampleMeters = 5f;

        private static bool IsRailTrack(OsmWay w)
        {
            switch ((w.Highway ?? string.Empty).ToLowerInvariant())
            {
                case "rail": case "light_rail": case "tram": case "narrow_gauge":
                    return !w.IsTunnel;
                default:
                    return false;
            }
        }

        private static List<OsmWay> DropServiceRoadsAlongRail(List<OsmWay> ways)
        {
            const float cell = 32f;
            var grid = new Dictionary<(int, int), List<(OsmWay Track, int Seg)>>();
            foreach (var t in ways.Where(IsRailTrack))
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
                return ways;

            var minCos = math.cos(math.radians(RailYardMaxAngleDegrees));
            var candidates = new HashSet<OsmWay>();
            foreach (var road in ways)
            {
                if (!string.Equals(road.Highway, "service", StringComparison.OrdinalIgnoreCase) || !IsVehicleRoad(road) ||
                    road.IsBridge || road.IsTunnel || road.Points.Count < 2)
                    continue;
                var reach = HalfWidth(road) + RailYardExtraMeters;
                float total = 0f, along = 0f;
                for (var i = 1; i < road.Points.Count; i++)
                {
                    var a = road.Points[i - 1];
                    var b = road.Points[i];
                    var len = math.distance(a.xz, b.xz);
                    if (len < 0.01f)
                        continue;
                    var dir = (b.xz - a.xz) / len;
                    var n = math.max(1, (int)math.ceil(len / RailYardSampleMeters));
                    for (var s = 0; s < n; s++)
                    {
                        var p = math.lerp(a, b, (s + 0.5f) / n);
                        var piece = len / n;
                        total += piece;
                        if (!grid.TryGetValue(((int)math.floor(p.x / cell), (int)math.floor(p.z / cell)), out var cands))
                            continue;
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
                            if (math.abs(q.y - p.y) > RailYardMaxHeightMeters || math.distance(q.xz, p.xz) > reach)
                                continue;
                            along += piece;
                            break;
                        }
                    }
                }
                if (total > 0f && along >= RailYardShare * total)
                    candidates.Add(road);
            }
            if (candidates.Count == 0)
                return ways;

            // Roads cut off from the main network by the drop: a group of only service roads (the rest of the yard's
            // roads) goes too; a group with any other road keeps the dropped roads it touches, so it stays reachable.
            var vehicle = ways.Where(IsVehicleRoad).ToList();
            var fullMain = Groups(vehicle, null)[0];
            var kept = new HashSet<OsmWay>();
            var yardRest = new HashSet<OsmWay>();
            for (var round = 0; round < 20; round++)
            {
                var drop = new HashSet<OsmWay>(candidates.Where(c => !kept.Contains(c)));
                var groups = Groups(vehicle, drop);
                yardRest.Clear();
                var added = new List<OsmWay>();
                foreach (var g in groups.Skip(1))
                {
                    if (!g.Any(fullMain.Contains))
                        continue;
                    if (g.All(w => string.Equals(w.Highway, "service", StringComparison.OrdinalIgnoreCase)))
                    {
                        yardRest.UnionWith(g);
                        continue;
                    }
                    var keys = new HashSet<(int, int)>(g.SelectMany(w => w.Points).Select(p => PointKey(p.xz)));
                    added.AddRange(drop.Where(c => c.Points.Any(p => keys.Contains(PointKey(p.xz)))));
                }
                if (added.Count == 0)
                    break;
                kept.UnionWith(added);
            }
            var dropped = new HashSet<OsmWay>(candidates.Where(c => !kept.Contains(c)));
            dropped.UnionWith(yardRest);
            var length = dropped.Sum(w => Enumerable.Range(1, w.Points.Count - 1).Sum(i => math.distance(w.Points[i - 1].xz, w.Points[i].xz)));
            Mod.log.Info($"Rail yards: {dropped.Count} service roads running along rail tracks dropped ({length / 1000f:F1} km; " +
                         $"{RailYardShare:P0} or more of their length inside the road's width of a track, plus {yardRest.Count} yard service roads only reached through them); {kept.Count} kept because other roads are only reached through them.");
            return ways.Where(w => !dropped.Contains(w)).ToList();
        }

        // The connected groups of roads (joined at shared points), leaving out 'without'; the largest first.
        private static List<HashSet<OsmWay>> Groups(List<OsmWay> roads, HashSet<OsmWay> without)
        {
            var parent = new Dictionary<(int, int), (int, int)>();
            (int, int) Find((int, int) k)
            {
                while (parent.TryGetValue(k, out var p) && !p.Equals(k))
                {
                    parent.TryGetValue(p, out var gp);
                    parent[k] = gp;
                    k = gp;
                }
                return k;
            }
            var use = roads.Where(r => without == null || !without.Contains(r)).ToList();
            foreach (var r in use)
            {
                var first = PointKey(r.Points[0].xz);
                if (!parent.ContainsKey(first)) parent[first] = first;
                foreach (var p in r.Points)
                {
                    var k = PointKey(p.xz);
                    if (!parent.ContainsKey(k)) parent[k] = k;
                    var a = Find(first);
                    var b = Find(k);
                    if (!a.Equals(b)) parent[b] = a;
                }
            }
            var groups = new Dictionary<(int, int), HashSet<OsmWay>>();
            foreach (var r in use)
            {
                var root = Find(PointKey(r.Points[0].xz));
                if (!groups.TryGetValue(root, out var g))
                    groups[root] = g = new HashSet<OsmWay>();
                g.Add(r);
            }
            var list = groups.Values.OrderByDescending(g => g.Count).ToList();
            if (list.Count == 0)
                list.Add(new HashSet<OsmWay>());
            return list;
        }
    }
}
