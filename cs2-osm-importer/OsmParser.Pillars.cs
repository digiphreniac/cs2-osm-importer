using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// CS2 stands an elevated road on a pillar under each of its nodes. A bridge point that happens to lie over a road
    /// below (an OSM shape point of Yesler Way over the I-5 ramps) put a pillar in the middle of that road. Each such
    /// point of a bridge is slid along the bridge to the nearest place clear of everything passing under it (the gap
    /// between carriageways, a verge), at most PillarShiftMeters, keeping its height. Junction points and way ends stay.
    /// </summary>
    internal static partial class OsmParser
    {
        // A pillar stands this far from the edge of a way below at least.
        private const float PillarClearMeters = 1.5f;
        private const float PillarShiftMeters = 15f;
        // A way this much lower than the bridge point passes under it.
        private const float PillarUnderMeters = 3f;
        private const float PillarMinPieceMeters = 4f;
        private const float PillarMaxGrade = 0.15f;

        private static void MovePillarPointsOffWaysBelow(List<OsmWay> ways)
        {
            var use = new Dictionary<(int, int), int>();
            foreach (var w in ways)
                foreach (var key in w.Points.Select(p => PointKey(p.xz)).Distinct())
                    use[key] = use.TryGetValue(key, out var c) ? c + 1 : 1;

            const float cell = 32f;
            var grid = new Dictionary<(int, int), List<(OsmWay Way, int Seg)>>();
            foreach (var w in ways)
            {
                if (w.IsIndoor || w.Points.Count < 2)
                    continue;
                for (var i = 1; i < w.Points.Count; i++)
                {
                    var reach = HalfWidth(w) + PillarClearMeters;
                    var lo = math.min(w.Points[i - 1].xz, w.Points[i].xz) - reach;
                    var hi = math.max(w.Points[i - 1].xz, w.Points[i].xz) + reach;
                    for (var x = (int)math.floor(lo.x / cell); x <= (int)math.floor(hi.x / cell); x++)
                        for (var z = (int)math.floor(lo.y / cell); z <= (int)math.floor(hi.y / cell); z++)
                        {
                            if (!grid.TryGetValue((x, z), out var l))
                                grid[(x, z)] = l = new List<(OsmWay, int)>();
                            l.Add((w, i));
                        }
                }
            }

            // Is a pillar at p (under a deck at height y, on way self) inside the footprint of a way below?
            bool OverWayBelow(float2 p, float y, OsmWay self)
            {
                if (!grid.TryGetValue(((int)math.floor(p.x / cell), (int)math.floor(p.y / cell)), out var cands))
                    return false;
                foreach (var (o, seg) in cands)
                {
                    if (o == self)
                        continue;
                    var a = o.Points[seg - 1];
                    var b = o.Points[seg];
                    var ab = b.xz - a.xz;
                    var t = math.lengthsq(ab) > 1e-6f ? math.saturate(math.dot(p - a.xz, ab) / math.lengthsq(ab)) : 0f;
                    var q = math.lerp(a, b, t);
                    if (q.y > y - PillarUnderMeters)
                        continue;
                    if (math.distance(q.xz, p) < HalfWidth(o) + PillarClearMeters)
                        return true;
                }
                return false;
            }

            int moved = 0, stuck = 0;
            foreach (var w in ways)
            {
                if (!(w.IsBridge && w.UsesStructuralElevation) || w.IsPier || w.Points.Count < 3)
                    continue;
                var pts = w.Points;
                for (var i = 1; i < pts.Count - 1; i++)
                {
                    var p = pts[i];
                    if (use.TryGetValue(PointKey(p.xz), out var uses) && uses > 1)
                        continue; // a junction
                    if (!OverWayBelow(p.xz, p.y, w))
                        continue;
                    // Candidates along the bridge both ways, nearest first. The point keeps its height, so it must stay
                    // far enough from the neighbour it moves towards for the piece between them to stay at most
                    // PillarMaxGrade (moved to within 1-2 m it left 50-140% slopes on ramp pieces).
                    float Room(float3 n) => math.distance(n.xz, p.xz) - math.max(PillarMinPieceMeters, math.abs(n.y - p.y) / PillarMaxGrade);
                    var back = Room(pts[i - 1]);
                    var ahead = Room(pts[i + 1]);
                    var dirBack = math.normalizesafe(pts[i - 1].xz - p.xz);
                    var dirAhead = math.normalizesafe(pts[i + 1].xz - p.xz);
                    float2? found = null;
                    for (var d = 0.5f; d <= PillarShiftMeters && found == null; d += 0.5f)
                    {
                        if (d <= ahead && !OverWayBelow(p.xz + dirAhead * d, p.y, w))
                            found = p.xz + dirAhead * d;
                        else if (d <= back && !OverWayBelow(p.xz + dirBack * d, p.y, w))
                            found = p.xz + dirBack * d;
                    }
                    if (found == null)
                    {
                        stuck++;
                        continue;
                    }
                    var oldKey = PointKey(p.xz);
                    pts[i] = new float3(found.Value.x, p.y, found.Value.y);
                    // Features recorded at the old position follow the point.
                    foreach (var list in new[] { w.Crosswalks, w.StopSigns, w.TrafficLights })
                        foreach (var f in list)
                            if (PointKey(f.Position.xz) == oldKey)
                                f.Position = pts[i];
                    moved++;
                }
            }
            Mod.log.Info($"Bridge pillars: {moved} bridge points moved up to {PillarShiftMeters:F0} m along their bridge off a way passing under " +
                         $"(CS2 puts a pillar under each), {stuck} left (no clear spot nearby).");
        }
    }
}
