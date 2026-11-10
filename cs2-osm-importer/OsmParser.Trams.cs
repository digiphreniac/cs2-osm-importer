using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// Streetcar lines are mapped as their own railway=tram ways running down the street (the First Hill Streetcar on
    /// South Jackson Street: 89% of Seattle's tram length lies inside a road's footprint). Built literally, the game laid
    /// a separate tram track over the road, split the road into 2 m pieces wherever the two touched, and drew rows of
    /// crosswalks on the tracks. In CS2 a street with tram tracks is the road itself with PrimaryTrack upgrades on both
    /// sides (GenerateEdgesSystem gives it a TramTrack; verified with the bridge's merge_test: the road then carries
    /// track lanes, and a separate tram track joining its end connects to them). So: the parts of a tram way running
    /// along a road are dropped and recorded on that road (OsmWay.TramStretches, applied at generation); the parts on
    /// their own right-of-way stay tram tracks, attached to the road where they leave it.
    /// </summary>
    internal static partial class OsmParser
    {
        // A tram point is on a road when it lies within the road's half width plus this, running along it.
        private const float TramOnRoadExtraMeters = 2f;
        // A street carrying a tram is built at least as a Small Road (about 6 m from its middle to its outer edge),
        // however narrow its OSM tags make it: a tram mapped down one side of it is still on it (14th Ave S, one lane).
        private const float TramRoadMinHalfWidthMeters = 6f;
        private const float TramOnRoadMaxAngleDegrees = 25f;
        // An off-road piece shorter than this between two on-road parts is part of the street run (a crossing, a stop).
        private const float TramMinOwnRightOfWayMeters = 15f;
        // Street-running segments are checked this often for other roads they run along.
        private const float TramSampleMeters = 2f;

        // Two streets more than this apart in direction make a turn.
        private static readonly float TurnCos = math.cos(math.radians(30f));
        // An off-street stretch longer than this between two streets is the tram's own right-of-way, not a turn.
        private const float TramMaxTurnMeters = 60f;
        // A turn's curve leaves each street this far from the junction (less when the next junction is closer)...
        private const float TramTurnMeters = 25f;
        // ... but not under this: a sharper curve than CS2's trams take.
        private const float TramMinTurnMeters = 12f;
        // A turn needs the tram to run at least this far along each of the two streets.
        private const float TramMinTurnRunMeters = 8f;
        // ... and at least this short of the street's next junction, so no sliver of road is left between them.
        private const float TramTurnJunctionClearMeters = 6f;

        /// <summary>The direction of the way's segment nearest to 'at'.</summary>
        private static float2 RoadDirection(OsmWay road, float3 at)
        {
            var best = float.MaxValue;
            var dir = new float2(1f, 0f);
            for (var i = 1; i < road.Points.Count; i++)
            {
                var a = road.Points[i - 1].xz;
                var ab = road.Points[i].xz - a;
                var len2 = math.lengthsq(ab);
                if (len2 < 1e-6f)
                    continue;
                var t = math.saturate(math.dot(at.xz - a, ab) / len2);
                var d = math.distancesq(a + ab * t, at.xz);
                if (d < best)
                {
                    best = d;
                    dir = ab / math.sqrt(len2);
                }
            }
            return dir;
        }

        /// <summary>
        /// A street's tram track stops where a turn curve meets it: track on the street between that point and the
        /// junction would reach the curve's end node alongside the curve, at almost the same angle, and CS2 then joins
        /// neither (a dead end at the South Lake Union line's turn off Westlake). The mapped tram often rejoins the street
        /// before the curve's end point, so stretches there are cut back to it, or dropped when wholly on the junction side.
        /// Returns how many stretches were changed.
        /// </summary>
        private static int TrimStretchesAtCurves(List<(OsmWay Road, float2 At, float2 Away, float ToJunction)> curveEnds, IEnumerable<OsmWay> tramRoads)
        {
            const float reach = TramMaxTurnMeters, slack = 0.5f, lineWidth = 3f;
            var changed = 0;
            // A curve end where tram track also runs on past the junction, on this street or the next way along it,
            // is a line going straight through the junction as well: its track is left alone there.
            var through = new HashSet<int>();
            var allEnds = tramRoads.SelectMany(r => r.TramStretches).SelectMany(t => new[] { t.A, t.B }).ToList();
            for (var z = 0; z < curveEnds.Count; z++)
            {
                var (_, at, away, toJ) = curveEnds[z];
                foreach (var p in allEnds)
                {
                    var s = math.dot(p - at, away);
                    var off = math.length(p - at - s * away);
                    if (off < lineWidth && s < -(toJ + 2f) && s > -(toJ + reach))
                    {
                        through.Add(z);
                        break;
                    }
                }
            }
            foreach (var road in curveEnds.Select(c => c.Road).Distinct())
            {
                var ends = curveEnds.Where((c, z) => c.Road == road && !through.Contains(z)).ToList();
                var kept = new List<(float2 A, float2 B)>();
                foreach (var (a0, b0) in road.TramStretches)
                {
                    var a = a0;
                    var b = b0;
                    var drop = false;
                    foreach (var (_, at, away, toJ) in ends)
                    {
                        if (math.distance(a, at) > reach && math.distance(b, at) > reach)
                            continue;
                        // An end between the curve's end and the junction (the curve leaves at most TramTurnMeters
                        // from it). An end beyond the junction is another line running straight through: left alone.
                        var sa = math.dot(a - at, away);
                        var sb = math.dot(b - at, away);
                        var inA = sa < -slack && sa > -(toJ + slack);
                        var inB = sb < -slack && sb > -(toJ + slack);
                        if (inA && inB) { drop = true; break; }
                        if (inA && sb >= -slack) a = at;
                        else if (inB && sa >= -slack) b = at;
                    }
                    if (drop || !a.Equals(a0) || !b.Equals(b0))
                        changed++;
                    if (!drop && math.distance(a, b) > 0.01f)
                        kept.Add((a, b));
                }
                road.TramStretches.Clear();
                road.TramStretches.AddRange(kept);
            }
            return changed;
        }

        /// <summary>
        /// The tram's curve from street A (where it runs at 'fromA') to street B (at 'toB'): the streets' shared junction
        /// J; a point on each street TramTurnMeters from J towards the tram (at most up to 6 m short of that street's next
        /// junction); and a curve between them tangent to both streets (a quadratic Bezier through J's corner). Both end
        /// points are inserted into their street. Null when the streets share no junction near the turn or there is no room.
        /// </summary>
        private static List<float3> BuildTramTurn(OsmWay a, float3 fromA, OsmWay b, float3 toB, Dictionary<(int, int), int> use)
        {
            var keysB = new HashSet<(int, int)>(b.Points.Select(q => PointKey(q.xz)));
            var mid = (fromA.xz + toB.xz) * 0.5f;
            var jIndex = -1;
            var best = 60f;
            for (var i = 0; i < a.Points.Count; i++)
            {
                if (!keysB.Contains(PointKey(a.Points[i].xz)))
                    continue;
                var d = math.distance(a.Points[i].xz, mid);
                if (d < best)
                {
                    best = d;
                    jIndex = i;
                }
            }
            if (jIndex < 0)
                return null;
            var j = a.Points[jIndex];
            if (!TryPointAlong(a, j, fromA, use, out var pa) || !TryPointAlong(b, j, toB, use, out var pb))
                return null;
            pa = InsertPointOnWay(a, pa, 1f);
            pb = InsertPointOnWay(b, pb, 1f);
            var curve = new List<float3>();
            const int steps = 8;
            for (var s = 0; s <= steps; s++)
            {
                var t = s / (float)steps;
                curve.Add((1f - t) * (1f - t) * pa + 2f * (1f - t) * t * j + t * t * pb);
            }
            curve[0] = pa;
            curve[steps] = pb;
            return curve;
        }

        /// <summary>The point on the way TramTurnMeters from its point j, walking towards 'towards'.</summary>
        private static bool TryPointAlong(OsmWay way, float3 j, float3 towards, Dictionary<(int, int), int> use, out float3 point)
        {
            point = default;
            var pts = way.Points;
            var ji = pts.FindIndex(q => math.distancesq(q.xz, j.xz) < 1e-4f);
            if (ji < 0)
                return false;
            var along = new float[pts.Count];
            for (var i = 1; i < pts.Count; i++)
                along[i] = along[i - 1] + math.distance(pts[i - 1].xz, pts[i].xz);
            var sT = AlongAt(pts, along, towards.xz);
            var step = sT >= along[ji] ? 1 : -1;
            // Room: up to the street's next junction (or its end) in that direction.
            var room = 0f;
            for (var k = ji + step; k >= 0 && k < pts.Count; k += step)
            {
                room = math.abs(along[k] - along[ji]);
                if (use.TryGetValue(PointKey(pts[k].xz), out var u) && u > 1)
                    break;
            }
            var d = math.min(TramTurnMeters, room - TramTurnJunctionClearMeters);
            if (d < TramMinTurnMeters)
                return false;
            var target = along[ji] + step * d;
            for (var k = 1; k < pts.Count; k++)
                if (target >= along[k - 1] && target <= along[k])
                {
                    var t = (target - along[k - 1]) / math.max(1e-3f, along[k] - along[k - 1]);
                    point = math.lerp(pts[k - 1], pts[k], t);
                    return true;
                }
            return false;
        }

        /// <summary>How far along the way (with cumulative lengths 'along') its nearest position to p lies.</summary>
        private static float AlongAt(List<float3> pts, float[] along, float2 p)
        {
            var s = 0f;
            var best = float.MaxValue;
            for (var i = 1; i < pts.Count; i++)
            {
                var ab = pts[i].xz - pts[i - 1].xz;
                var len2 = math.lengthsq(ab);
                var t = len2 > 1e-6f ? math.saturate(math.dot(p - pts[i - 1].xz, ab) / len2) : 0f;
                var d = math.distancesq(pts[i - 1].xz + ab * t, p);
                if (d < best)
                {
                    best = d;
                    s = along[i - 1] + t * math.sqrt(len2);
                }
            }
            return s;
        }

        private static bool CanCarryTram(OsmWay road)
        {
            if (!IsVehicleRoad(road))
                return false;
            switch ((road.Highway ?? string.Empty).ToLowerInvariant())
            {
                case "motorway": case "motorway_link": case "trunk": case "trunk_link":
                // Alleys and driveways lined up with a street at a junction took the tram off the street there.
                case "service":
                    return false;
                default:
                    return true;
            }
        }

        private static List<OsmWay> MergeStreetTrams(List<OsmWay> ways)
        {
            var trams = ways.Where(w => string.Equals(w.Highway, "tram", StringComparison.OrdinalIgnoreCase) && w.Points.Count >= 2).ToList();
            if (trams.Count == 0)
                return ways;
            var roads = ways.Where(CanCarryTram).ToList();
            const float cell = 32f;
            var grid = new Dictionary<(int, int), List<(OsmWay Road, int Seg)>>();
            foreach (var road in roads)
                for (var i = 1; i < road.Points.Count; i++)
                {
                    var reach = HalfWidth(road) + TramOnRoadExtraMeters;
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
            var minCos = math.cos(math.radians(TramOnRoadMaxAngleDegrees));

            // The road a point of a tram lies on (running along it), and where on that road.
            bool OnRoad(float3 p, float2 dir, out OsmWay onRoad, out float3 at)
            {
                onRoad = null;
                at = default;
                if (!grid.TryGetValue(((int)math.floor(p.x / cell), (int)math.floor(p.z / cell)), out var cands))
                    return false;
                var best = float.MaxValue;
                foreach (var (road, seg) in cands)
                {
                    var a = road.Points[seg - 1];
                    var b = road.Points[seg];
                    var len = math.distance(a.xz, b.xz);
                    if (len < 0.01f)
                        continue;
                    var rdir = (b.xz - a.xz) / len;
                    if (math.abs(math.dot(rdir, dir)) < minCos)
                        continue;
                    var t = math.saturate(math.dot(p.xz - a.xz, rdir) / len);
                    var q = math.lerp(a, b, t);
                    if (math.abs(q.y - p.y) > 3f)
                        continue;
                    var d = math.distance(q.xz, p.xz);
                    if (d > math.max(HalfWidth(road), TramRoadMinHalfWidthMeters) + TramOnRoadExtraMeters || d >= best)
                        continue;
                    best = d;
                    onRoad = road;
                    at = q;
                }
                return onRoad != null;
            }

            var dropped = new HashSet<OsmWay>();
            var added = new List<OsmWay>();
            var builtTurns = 0;
            var builtCurves = new HashSet<((int, int), (int, int))>();
            var failedTurns = 0;
            // Where each turn curve meets a street, and the direction along the street away from the junction.
            var curveEnds = new List<(OsmWay Road, float2 At, float2 Away, float ToJunction)>();
            // How many roads use each point: a point used by more than one is a junction (footway crossings are not).
            var use = new Dictionary<(int, int), int>();
            foreach (var w in ways.Where(IsVehicleRoad))
                foreach (var key in w.Points.Select(q => PointKey(q.xz)).Distinct())
                    use[key] = use.TryGetValue(key, out var c) ? c + 1 : 1;
            float onRoadLength = 0f, keptLength = 0f;
            var roadsWithTrams = new HashSet<OsmWay>();
            foreach (var tram in trams)
            {
                var pts = tram.Points;
                var n = pts.Count;
                var on = new bool[n];
                var roadAt = new OsmWay[n];
                var proj = new float3[n];
                for (var i = 0; i < n; i++)
                {
                    var dir = math.normalizesafe(pts[math.min(n - 1, i + 1)].xz - pts[math.max(0, i - 1)].xz);
                    on[i] = OnRoad(pts[i], dir, out roadAt[i], out proj[i]);
                }
                // Turns from one street onto another: CS2 links no track lanes round a junction corner (too tight for a
                // tram at any road size; checked with the bridge's tram_turn_test), so each turn gets a track of its own,
                // curving from one street to the other (BuildTramTurn). turn[k]: the tram's own points there are dropped.
                var turn = new bool[n];
                var turns = new List<(int Before, int After)>();
                // The tram's heading over about 15 m from point i, backwards (dirSign -1) or forwards (+1).
                float2 TramHeading(int i, int dirSign)
                {
                    var k = i;
                    while (k + dirSign >= 0 && k + dirSign < n && math.distance(pts[k].xz, pts[i].xz) < 15f)
                        k += dirSign;
                    return math.normalizesafe(dirSign > 0 ? pts[k].xz - pts[i].xz : pts[i].xz - pts[k].xz);
                }
                // Not a turn: the two streets run the same way, or the tram itself goes straight on (from one
                // carriageway of a divided street onto the other).
                bool Aligned(int a, int b) => math.abs(math.dot(RoadDirection(roadAt[a], proj[a]), RoadDirection(roadAt[b], proj[b]))) >= TurnCos
                                              || math.dot(TramHeading(a, -1), TramHeading(b, 1)) >= TurnCos;
                // How far the tram runs along the street at point i (or streets in line with it), backwards (-1) or
                // forwards (+1). A turn needs the tram to run along both streets: a tram mapped beside a street can
                // share one point with a side street's end (South Main Street at 14th Ave S), which is no turn.
                float RunAlong(int i, int dirSign)
                {
                    var dir = RoadDirection(roadAt[i], proj[i]);
                    var len = 0f;
                    for (var k = i + dirSign; k >= 0 && k < n && on[k]; k += dirSign)
                    {
                        if (math.abs(math.dot(RoadDirection(roadAt[k], proj[k]), dir)) < TurnCos)
                            break;
                        len += math.distance(pts[k].xz, pts[k - dirSign].xz);
                        if (len >= TramMinTurnRunMeters)
                            break;
                    }
                    return len;
                }
                bool RunsAlongBoth(int a, int b) => RunAlong(a, -1) >= TramMinTurnRunMeters && RunAlong(b, 1) >= TramMinTurnRunMeters;
                for (var i = 1; i < n; i++)
                    if (on[i - 1] && on[i] && roadAt[i - 1] != roadAt[i] && !Aligned(i - 1, i) && RunsAlongBoth(i - 1, i))
                        turns.Add((i - 1, i));
                // Short stretches off the road between two on-road parts: the street run, or a turn between two streets.
                for (var i = 0; i < n; i++)
                {
                    if (on[i])
                        continue;
                    var j = i;
                    var len = 0f;
                    while (j < n && !on[j])
                    {
                        if (j > i) len += math.distance(pts[j - 1].xz, pts[j].xz);
                        j++;
                    }
                    if (i > 0 && j < n)
                    {
                        len += math.distance(pts[i - 1].xz, pts[i].xz) + math.distance(pts[j - 1].xz, pts[j].xz);
                        if (roadAt[i - 1] != roadAt[j] && !Aligned(i - 1, j) && len < TramMaxTurnMeters && RunsAlongBoth(i - 1, j))
                        {
                            turns.Add((i - 1, j));
                            for (var k = i; k < j; k++)
                                turn[k] = true;
                        }
                        else if (len < TramMinOwnRightOfWayMeters)
                            for (var k = i; k < j; k++)
                            {
                                on[k] = true;
                                roadAt[k] = roadAt[i - 1];
                                proj[k] = ProjectOnWay(roadAt[k], pts[k]);
                            }
                    }
                    i = j;
                }
                if (!on.Any(b => b))
                    continue;
                // Each turn: a curve between the two streets; where none fits, the tram's own points stay as track.
                var turnSegments = new HashSet<int>();
                foreach (var (before, after) in turns)
                {
                    var curve = BuildTramTurn(roadAt[before], proj[before], roadAt[after], proj[after], use);
                    if (curve == null)
                    {
                        for (var k = before + 1; k < after; k++)
                            turn[k] = false;
                        failedTurns++;
                        continue;
                    }
                    // A line mapped as one track per direction turns twice at each corner: one curve carries both.
                    var k0 = PointKey(curve[0].xz);
                    var k1 = PointKey(curve[curve.Count - 1].xz);
                    if (builtCurves.Add(k0.CompareTo(k1) <= 0 ? (k0, k1) : (k1, k0)))
                    {
                        var w = CloneWayShape(tram);
                        w.Points.AddRange(curve);
                        w.TerrainHeights.AddRange(curve.Select(q => q.y));
                        added.Add(w);
                        builtTurns++;
                    }
                    // Each street keeps its tram track up to where the curve leaves it.
                    roadAt[before].TramStretches.Add((proj[before].xz, curve[0].xz));
                    roadAt[after].TramStretches.Add((curve[curve.Count - 1].xz, proj[after].xz));
                    // The junction: the quadratic curve's control point, from its middle point and ends.
                    var junction = 2f * curve[curve.Count / 2].xz - 0.5f * (curve[0].xz + curve[curve.Count - 1].xz);
                    curveEnds.Add((roadAt[before], curve[0].xz, math.normalizesafe(curve[0].xz - curve[1].xz), math.distance(curve[0].xz, junction)));
                    curveEnds.Add((roadAt[after], curve[curve.Count - 1].xz, math.normalizesafe(curve[curve.Count - 1].xz - curve[curve.Count - 2].xz), math.distance(curve[curve.Count - 1].xz, junction)));
                    roadsWithTrams.Add(roadAt[before]);
                    roadsWithTrams.Add(roadAt[after]);
                    for (var k = before + 1; k <= after; k++)
                        turnSegments.Add(k);
                }

                // Segments with both ends on the road are street running: record them on the road(s).
                for (var i = 1; i < n; i++)
                {
                    if (!on[i - 1] || !on[i] || turnSegments.Contains(i))
                        continue;
                    var len = math.distance(pts[i - 1].xz, pts[i].xz);
                    onRoadLength += len;
                    // Every road the segment runs along: a short way between its two ends (Broadway is split into
                    // a 5 m piece at one junction) would otherwise be left without track, cutting the line.
                    var along = new HashSet<OsmWay> { roadAt[i - 1], roadAt[i] };
                    if (roadAt[i - 1] != roadAt[i] || len > 2f * TramSampleMeters)
                    {
                        var segDir = math.normalizesafe(pts[i].xz - pts[i - 1].xz);
                        var samples = (int)math.ceil(len / TramSampleMeters);
                        for (var s = 1; s < samples; s++)
                            if (OnRoad(math.lerp(pts[i - 1], pts[i], s / (float)samples), segDir, out var r, out _))
                                along.Add(r);
                    }
                    foreach (var road in along)
                    {
                        road.TramStretches.Add((ProjectOnWay(road, pts[i - 1]).xz, ProjectOnWay(road, pts[i]).xz));
                        roadsWithTrams.Add(road);
                    }
                }

                // The rest stays a tram track, in pieces; a piece's end next to a street run is moved onto the road there.
                dropped.Add(tram);
                var piece = new List<float3>();
                var heights = new List<float>();
                void Flush()
                {
                    if (piece.Count >= 2)
                    {
                        var w = CloneWayShape(tram);
                        w.Points.AddRange(piece);
                        if (heights.Count == piece.Count)
                            w.TerrainHeights.AddRange(heights);
                        added.Add(w);
                        for (var k = 1; k < piece.Count; k++)
                            keptLength += math.distance(piece[k - 1].xz, piece[k].xz);
                    }
                    piece.Clear();
                    heights.Clear();
                }
                var hasHeights = tram.TerrainHeights.Count == n;
                for (var i = 0; i < n; i++)
                {
                    if (turn[i])
                    {
                        Flush();
                        continue;
                    }
                    if (on[i])
                    {
                        // An off-road piece ending here joins the road at this point.
                        if (piece.Count > 0)
                        {
                            var joint = InsertPointOnWay(roadAt[i], proj[i], 2f);
                            piece.Add(joint);
                            heights.Add(joint.y);
                            Flush();
                        }
                        continue;
                    }
                    if (piece.Count == 0 && i > 0 && on[i - 1])
                    {
                        var joint = InsertPointOnWay(roadAt[i - 1], proj[i - 1], 2f);
                        piece.Add(joint);
                        heights.Add(joint.y);
                    }
                    piece.Add(pts[i]);
                    heights.Add(hasHeights ? tram.TerrainHeights[i] : pts[i].y);
                }
                Flush();
            }
            if (dropped.Count == 0)
                return ways;
            var trimmed = TrimStretchesAtCurves(curveEnds, roadsWithTrams);
            Mod.log.Info($"Street trams: {trimmed} street stretches trimmed back to where a turn curve meets them.");
            Mod.log.Info($"Street trams: {onRoadLength / 1000f:F1} km of tram track running along {roadsWithTrams.Count} roads built as those roads' tram tracks; " +
                         $"{keptLength / 1000f:F1} km on its own right-of-way kept as tram track ({added.Count - builtTurns} pieces); " +
                         $"{builtTurns} turns from one street onto another given a curve of their own ({failedTurns} kept as mapped: no room).");
            return ways.Where(w => !dropped.Contains(w)).Concat(added).ToList();
        }

        /// <summary>The nearest position on the way to p, at any distance.</summary>
        private static float3 ProjectOnWay(OsmWay way, float3 p)
        {
            var pts = way.Points;
            var best = float.MaxValue;
            var result = p;
            for (var i = 1; i < pts.Count; i++)
            {
                var ab = pts[i].xz - pts[i - 1].xz;
                var len2 = math.lengthsq(ab);
                var tt = len2 > 1e-6f ? math.saturate(math.dot(p.xz - pts[i - 1].xz, ab) / len2) : 0f;
                var q = math.lerp(pts[i - 1], pts[i], tt);
                var d = math.distancesq(q.xz, p.xz);
                if (d < best)
                {
                    best = d;
                    result = q;
                }
            }
            return result;
        }

        /// <summary>A new way with every tag of <paramref name="way"/> and no points.</summary>
        private static OsmWay CloneWayShape(OsmWay way) => new OsmWay
        {
            Id = way.Id,
            Name = way.Name,
            Ref = way.Ref,
            Highway = way.Highway,
            Footway = way.Footway,
            CyclewayTag = way.CyclewayTag,
            SidewalkTag = way.SidewalkTag,
            Lanes = way.Lanes,
            LanesForward = way.LanesForward,
            LanesBackward = way.LanesBackward,
            LanesBothWays = way.LanesBothWays,
            HasExplicitLaneCount = way.HasExplicitLaneCount,
            UsesStructuralElevation = way.UsesStructuralElevation,
            IsBridge = way.IsBridge,
            IsPier = way.IsPier,
            IsTunnel = way.IsTunnel,
            Layer = way.Layer,
            IsIndoor = way.IsIndoor,
            IsOneWay = way.IsOneWay,
            OneWayDirection = way.OneWayDirection,
            BaseElevationOffsetMeters = way.BaseElevationOffsetMeters,
            InclineDirection = way.InclineDirection,
            InclinePercent = way.InclinePercent,
            Sidewalk = way.Sidewalk,
            ParkingSide = way.ParkingSide,
            ParkingStyle = way.ParkingStyle
        };
    }
}
