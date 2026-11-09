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
