using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// The importer moves and adds path pieces of its own: a path end extended onto the nearest road, joins slid along
    /// a road, a last piece squared to the road. Each new piece was laid without looking at what it crossed, and 94% of
    /// the same-level crossings without a junction on full Seattle were these (a pier walkway extended onto a service
    /// road straight across the Westlake bike lane; a squared approach through a neighbouring path): CS2 built both
    /// ways through each other with no junction, so neither people nor bikes could turn there. This index answers
    /// "does this new piece cross a way at its own level?" so those steps can join the crossed way or leave the path as
    /// mapped instead.
    /// </summary>
    internal static partial class OsmParser
    {
        // A crossing this close to an end of the new piece is the piece arriving at a junction, not a crossing.
        private const float CrossingEndToleranceMeters = 0.1f;
        // A path end extended across a way joins it there only when the two are this close in height.
        private const float CrossingJoinHeightMeters = 3f;

        /// <summary>OSM level: layer=*, or 1 for an untagged bridge and -1 for an untagged tunnel.</summary>
        private static int OsmLevel(OsmWay way) => way.Layer != 0 ? way.Layer : way.IsBridge ? 1 : way.IsTunnel ? -1 : 0;

        private sealed class WayCrossingIndex
        {
            private const float CellSize = 32f;

            private sealed class Seg
            {
                public OsmWay Way;
                public float3 A, B;
            }

            private readonly Dictionary<(int, int), List<Seg>> _cells = new Dictionary<(int, int), List<Seg>>();
            private readonly Dictionary<OsmWay, List<(List<Seg> Cell, Seg Seg)>> _entries = new Dictionary<OsmWay, List<(List<Seg>, Seg)>>();

            public WayCrossingIndex(IEnumerable<OsmWay> ways)
            {
                foreach (var w in ways)
                    Add(w);
            }

            public void Add(OsmWay way)
            {
                if (way.IsIndoor || way.IsNodeRoundabout || way.Points.Count < 2)
                    return;
                if (!_entries.TryGetValue(way, out var entries))
                    _entries[way] = entries = new List<(List<Seg>, Seg)>();
                for (var i = 1; i < way.Points.Count; i++)
                {
                    var seg = new Seg { Way = way, A = way.Points[i - 1], B = way.Points[i] };
                    var lo = math.min(seg.A.xz, seg.B.xz);
                    var hi = math.max(seg.A.xz, seg.B.xz);
                    for (var x = (int)math.floor(lo.x / CellSize); x <= (int)math.floor(hi.x / CellSize); x++)
                        for (var z = (int)math.floor(lo.y / CellSize); z <= (int)math.floor(hi.y / CellSize); z++)
                        {
                            if (!_cells.TryGetValue((x, z), out var cell))
                                _cells[(x, z)] = cell = new List<Seg>();
                            cell.Add(seg);
                            entries.Add((cell, seg));
                        }
                }
            }

            /// <summary>Re-reads the way's geometry after its points changed.</summary>
            public void Update(OsmWay way)
            {
                if (_entries.TryGetValue(way, out var entries))
                {
                    foreach (var (cell, seg) in entries)
                        cell.Remove(seg);
                    entries.Clear();
                }
                Add(way);
            }

            /// <summary>
            /// Every crossing of the piece a-b with another way at any level (the position on the other way), except
            /// where the other way has a point at a or b (a junction).
            /// </summary>
            public IEnumerable<(OsmWay Way, float3 At)> Crossings(float3 a, float3 b, OsmWay self)
            {
                var length = math.distance(a.xz, b.xz);
                if (length < 1e-3f)
                    yield break;
                var lo = math.min(a.xz, b.xz);
                var hi = math.max(a.xz, b.xz);
                var seen = new HashSet<Seg>();
                for (var x = (int)math.floor(lo.x / CellSize); x <= (int)math.floor(hi.x / CellSize); x++)
                    for (var z = (int)math.floor(lo.y / CellSize); z <= (int)math.floor(hi.y / CellSize); z++)
                    {
                        if (!_cells.TryGetValue((x, z), out var cell))
                            continue;
                        foreach (var seg in cell)
                        {
                            if (seg.Way == self || !seen.Add(seg))
                                continue;
                            if (!SegmentsCross(a.xz, b.xz, seg.A.xz, seg.B.xz, out var u, out var v))
                                continue;
                            var along = u * length;
                            if ((along < CrossingEndToleranceMeters || along > length - CrossingEndToleranceMeters) &&
                                HasPointNear(seg.Way, along < CrossingEndToleranceMeters ? a.xz : b.xz))
                                continue;
                            yield return (seg.Way, math.lerp(seg.A, seg.B, v));
                        }
                    }
            }

            /// <summary>
            /// The crossing nearest a along the new piece a-b of <paramref name="self"/> with another way at self's
            /// OSM level, if any (whatever their heights there: tagged as one level, they are meant to meet). Touching
            /// another way at a or b is a junction and does not count. <paramref name="heightGap"/> is how far apart
            /// the two are in height at the crossing.
            /// </summary>
            public bool TryFirstCrossing(float3 a, float3 b, OsmWay self, out OsmWay crossed, out float3 at) =>
                TryFirstCrossing(a, b, self, out crossed, out at, out _);

            public bool TryFirstCrossing(float3 a, float3 b, OsmWay self, out OsmWay crossed, out float3 at, out float heightGap)
            {
                crossed = null;
                at = default;
                heightGap = 0f;
                var best = float.MaxValue;
                var length = math.distance(a.xz, b.xz);
                if (length < 1e-3f)
                    return false;
                var level = OsmLevel(self);
                var lo = math.min(a.xz, b.xz);
                var hi = math.max(a.xz, b.xz);
                for (var x = (int)math.floor(lo.x / CellSize); x <= (int)math.floor(hi.x / CellSize); x++)
                    for (var z = (int)math.floor(lo.y / CellSize); z <= (int)math.floor(hi.y / CellSize); z++)
                    {
                        if (!_cells.TryGetValue((x, z), out var cell))
                            continue;
                        foreach (var seg in cell)
                        {
                            if (seg.Way == self || OsmLevel(seg.Way) != level)
                                continue;
                            if (!SegmentsCross(a.xz, b.xz, seg.A.xz, seg.B.xz, out var u, out var v))
                                continue;
                            var along = u * length;
                            if (along >= best)
                                continue;
                            // At an end of the new piece: a junction if the other way has a point there (a squared
                            // path end put on the middle of a neighbour's segment is still a crossing).
                            if (along < CrossingEndToleranceMeters || along > length - CrossingEndToleranceMeters)
                            {
                                var end = along < CrossingEndToleranceMeters ? a.xz : b.xz;
                                if (HasPointNear(seg.Way, end))
                                    continue;
                            }
                            var mine = math.lerp(a, b, u);
                            var theirs = math.lerp(seg.A, seg.B, v);
                            best = along;
                            crossed = seg.Way;
                            at = theirs;
                            heightGap = math.abs(mine.y - theirs.y);
                        }
                    }
                return crossed != null;
            }
        }

        // Same-level ways still crossing at the end are joined there when this close in height.
        private const float CrossingJoinMaxGapMeters = 1f;
        // An existing point of the crossed way this close to the crossing is used as the junction.
        private const float CrossingJoinReuseMeters = 0.5f;

        /// <summary>
        /// What is left after the importer's own steps stopped making crossings: OSM ways at one level crossing without
        /// a shared node (a footway drawn across a trunk road's kerb line, two trails mapped over each other, pier
        /// walkways generated across one another). Built as they are, CS2 lays them through each other with no
        /// junction. Roads and paths are given a shared point there; rail and tram are left alone (a path or road
        /// cannot share a node with a track that way), as are crossings more than CrossingJoinMaxGapMeters apart in
        /// height (one probably passes over the other, tags or not).
        /// </summary>
        private static void JoinSameLevelCrossings(List<OsmWay> ways)
        {
            // Piers are left out: their heights are set from the water at generation, and a point a pier shared with a
            // footway came apart there.
            var candidates = ways.Where(w => !w.IsIndoor && !w.IsPier && w.Points.Count > 1 && (IsVehicleRoad(w) || IsPathLike(w))).ToList();
            var index = new WayCrossingIndex(candidates);
            var joined = 0;
            var apart = 0;
            var bothNear = 0;
            foreach (var w in candidates)
            {
                for (var i = 1; i < w.Points.Count; i++)
                {
                    for (var guard = 0; guard < 4; guard++)
                    {
                        if (!index.TryFirstCrossing(w.Points[i - 1], w.Points[i], w, out var other, out var at, out var gap))
                            break;
                        if (gap > CrossingJoinMaxGapMeters)
                        {
                            apart++;
                            break;
                        }
                        // A point of either way next to the crossing becomes the junction: a new point 0.1 m beside
                        // an existing one left an edge CS2 does not build (a cycleway broken where a service road
                        // crossed it). Both ways having one there would mean moving a point; left.
                        var nearStart = math.distance(w.Points[i - 1].xz, at.xz) < CrossingJoinReuseMeters;
                        var nearEnd = math.distance(w.Points[i].xz, at.xz) < CrossingJoinReuseMeters;
                        if (nearStart || nearEnd)
                        {
                            if (HasPointWithin(other, at.xz, CrossingJoinReuseMeters))
                            {
                                bothNear++;
                                break;
                            }
                            InsertPointOnWay(other, nearStart ? w.Points[i - 1] : w.Points[i], 0f, exact: true);
                            index.Update(other);
                            joined++;
                            continue;
                        }
                        var t = math.saturate(math.distance(w.Points[i - 1].xz, at.xz) / math.max(1e-3f, math.distance(w.Points[i - 1].xz, w.Points[i].xz)));
                        var mine = math.lerp(w.Points[i - 1], w.Points[i], t);
                        var shared = InsertPointOnWay(other, new float3(at.x, (at.y + mine.y) * 0.5f, at.z), CrossingJoinReuseMeters, exact: true);
                        if (w.TerrainHeights.Count == w.Points.Count)
                            w.TerrainHeights.Insert(i, math.lerp(w.TerrainHeights[i - 1], w.TerrainHeights[i], t));
                        w.Points.Insert(i, shared);
                        index.Update(w);
                        index.Update(other);
                        joined++;
                    }
                }
            }
            Mod.log.Info($"Same-level crossings: {joined} roads/paths crossing at one level without a shared node joined there; " +
                         $"{apart} left (more than {CrossingJoinMaxGapMeters:F0} m apart in height), {bothNear} left (both ways have a point beside the crossing).");
        }

        /// <summary>
        /// Where two ways at different OSM levels cross: <see cref="Mover"/> is the one whose height can change to
        /// make room (the bridge over a street, the tunnel under one), <see cref="Up"/> says which way it moves, and
        /// <see cref="Other"/> stays put. Mover has a point of its own at <see cref="At"/>.
        /// </summary>
        private sealed class LevelCrossing
        {
            public OsmWay Mover;
            public OsmWay Other;
            public bool Up;
            public float2 At;
            public float Clearance;
            // A ground way below that may dip under Mover: its own point beside the crossing.
            public bool HasPartner;
            public float2 Partner;
        }

        private static bool IsBridgeStructure(OsmWay w) => w.UsesStructuralElevation && w.IsBridge && !w.IsPier;
        private static bool IsTunnelStructure(OsmWay w) => w.UsesStructuralElevation && w.IsTunnel;

        // Height between centre lines where a way passes over another: a road or track below needs room for trucks
        // and trains plus the deck; a path below needs less. Over a road it is also what CS2 needs to build the deck on
        // pillars: a piece of road gets pillars only where it stands at least twice its prefab's elevation step (4 m
        // for a Small Road) above the ground, and at 6.5 m Yesler Way over the I-5 ramps came out as a walled ramp.
        private const float LevelClearanceOverRoadMeters = 8.5f;
        private const float LevelClearanceOverPathMeters = 4.5f;
        // Crossings of the same two ways closer than this are one crossing (a curve drawn twice through the grid).
        private const float LevelCrossingMergeMeters = 1f;

        /// <summary>
        /// Every crossing between ways at different OSM levels, with a point inserted into the way that moves. Only
        /// a bridge (moves up) or a tunnel (moves down) moves; a way at another level only by layer=* stays on the
        /// ground (a covered walkway, a floor of a passage), so where it crosses a ground way nothing can move and
        /// the crossing is left out. Indoor ways and piers are left out too.
        /// </summary>
        private static List<LevelCrossing> FindLevelCrossings(List<OsmWay> ways)
        {
            var usable = ways.Where(w => !w.IsIndoor && !w.IsPier && !IsPedestrianDetailFootway(w) && !w.IsNodeRoundabout && w.Points.Count > 1).ToList();
            var index = new WayCrossingIndex(usable);
            var found = new List<LevelCrossing>();
            var seen = new HashSet<(OsmWay, OsmWay, int, int)>();
            foreach (var w in usable)
            {
                var level = OsmLevel(w);
                if (level == 0)
                    continue; // a crossing at different levels always involves a way off level 0; found from that side
                for (var i = 1; i < w.Points.Count; i++)
                {
                    foreach (var (other, at) in index.Crossings(w.Points[i - 1], w.Points[i], w))
                    {
                        var otherLevel = OsmLevel(other);
                        if (otherLevel == level)
                            continue;
                        var upper = level > otherLevel ? w : other;
                        var lower = level > otherLevel ? other : w;
                        OsmWay mover;
                        bool up;
                        if (IsBridgeStructure(upper))
                        {
                            mover = upper;
                            up = true;
                        }
                        else if (IsTunnelStructure(lower))
                        {
                            mover = lower;
                            up = false;
                        }
                        else
                            continue;
                        var a = mover.GetHashCode() < (mover == upper ? lower : upper).GetHashCode();
                        var cell = ((int)math.floor(at.x / LevelCrossingMergeMeters), (int)math.floor(at.z / LevelCrossingMergeMeters));
                        var key = a ? (upper, lower, cell.Item1, cell.Item2) : (lower, upper, cell.Item1, cell.Item2);
                        if (!seen.Add(key))
                            continue;
                        found.Add(new LevelCrossing
                        {
                            Mover = mover,
                            Other = mover == upper ? lower : upper,
                            Up = up,
                            At = at.xz,
                            Clearance = IsPathLike(lower) ? LevelClearanceOverPathMeters : LevelClearanceOverRoadMeters
                        });
                    }
                }
            }
            // Points go in after the search, so no crossing was looked for on a half-changed way. No new point may sit
            // where the other way has one: grading keys points by plan position, and the two would become one junction.
            foreach (var c in found)
            {
                var target = HasPointWithin(c.Other, c.At, 0.1f) ? AlongWay(c.Mover, c.At, 0.15f) : new float3(c.At.x, 0f, c.At.y);
                var p = InsertPointOnWay(c.Mover, target, 0.5f);
                if (HasPointWithin(c.Other, p.xz, 0.05f))
                {
                    c.Mover = null;
                    continue;
                }
                c.At = p.xz;
                // A ground way below may dip to pass under (a ramp in its trench under Yesler Way, where the heightmap
                // has filled the trench up to the deck): it gets a point of its own just beside the crossing.
                if (c.Up && CanDipUnder(c.Other))
                {
                    var q = InsertPointOnWay(c.Other, AlongWay(c.Other, c.At, 0.1f));
                    if (!HasPointWithin(c.Mover, q.xz, 0.05f))
                    {
                        c.Partner = q.xz;
                        c.HasPartner = true;
                    }
                }
            }
            found.RemoveAll(c => c.Mover == null);
            return found;
        }

        private static bool CanDipUnder(OsmWay w)
        {
            if (w.UsesStructuralElevation || w.IsPier || w.IsIndoor || w.IsNodeRoundabout)
                return false;
            if (IsVehicleRoad(w) || IsPathLike(w))
                return true;
            switch ((w.Highway ?? string.Empty).ToLowerInvariant())
            {
                case "rail": case "light_rail": case "subway": case "narrow_gauge":
                    return true;
                default:
                    return false; // a tram lies in its street; the street does not dip with it
            }
        }

        /// <summary>The position <paramref name="meters"/> along the way's segment nearest <paramref name="at"/>.</summary>
        private static float3 AlongWay(OsmWay way, float2 at, float meters)
        {
            if (!TryProjectOnWay(way.Points, at, out var seg, out var t))
                return new float3(at.x, 0f, at.y);
            var a = way.Points[seg - 1].xz;
            var b = way.Points[seg].xz;
            var len = math.max(1e-3f, math.distance(a, b));
            var dir = (b - a) / len;
            var p = (1f - t) * len >= meters + 0.06f ? at + dir * meters : at - dir * meters;
            return new float3(p.x, 0f, p.y);
        }

        private static bool HasPointNear(OsmWay way, float2 p) => HasPointWithin(way, p, 0.05f);

        private static bool HasPointWithin(OsmWay way, float2 p, float meters)
        {
            foreach (var q in way.Points)
                if (math.distancesq(q.xz, p) < meters * meters)
                    return true;
            return false;
        }

        private static bool SegmentsCross(float2 p1, float2 p2, float2 p3, float2 p4, out float u, out float v)
        {
            u = v = 0f;
            var d = (p2.x - p1.x) * (p4.y - p3.y) - (p2.y - p1.y) * (p4.x - p3.x);
            if (math.abs(d) < 1e-6f)
                return false;
            u = ((p3.x - p1.x) * (p4.y - p3.y) - (p3.y - p1.y) * (p4.x - p3.x)) / d;
            v = ((p3.x - p1.x) * (p2.y - p1.y) - (p3.y - p1.y) * (p2.x - p1.x)) / d;
            return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
        }

        /// <summary>
        /// Puts <paramref name="at"/> (a position on one of the way's segments) into the way as a point of its own,
        /// or returns the way's existing point within <paramref name="reuseMeters"/>. Returns the exact point the way
        /// now has: <paramref name="at"/> itself when <paramref name="exact"/>, else the position on the segment.
        /// </summary>
        private static float3 InsertPointOnWay(OsmWay way, float3 at, float reuseMeters = 0.05f, bool exact = false)
        {
            var pts = way.Points;
            var bestSeg = -1;
            var bestT = 0f;
            var bestD = float.MaxValue;
            for (var i = 1; i < pts.Count; i++)
            {
                var ab = pts[i].xz - pts[i - 1].xz;
                var len2 = math.lengthsq(ab);
                var t = len2 > 1e-6f ? math.saturate(math.dot(at.xz - pts[i - 1].xz, ab) / len2) : 0f;
                var d = math.distance(at.xz, pts[i - 1].xz + ab * t);
                if (d < bestD)
                {
                    bestD = d;
                    bestSeg = i;
                    bestT = t;
                }
            }
            if (bestSeg < 0)
                return at;
            var len = math.distance(pts[bestSeg - 1].xz, pts[bestSeg].xz);
            if (bestT * len < reuseMeters)
                return pts[bestSeg - 1];
            if ((1f - bestT) * len < reuseMeters)
                return pts[bestSeg];
            var point = exact ? at : math.lerp(pts[bestSeg - 1], pts[bestSeg], bestT);
            if (way.TerrainHeights.Count == pts.Count)
                way.TerrainHeights.Insert(bestSeg, math.lerp(way.TerrainHeights[bestSeg - 1], way.TerrainHeights[bestSeg], bestT));
            pts.Insert(bestSeg, point);
            return point;
        }
    }
}
