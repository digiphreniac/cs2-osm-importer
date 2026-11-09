using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// Grade-limited height transitions. A bridge or tunnel used to reach its height in at most 60 m from each end
    /// (HarmonizeGroupElevations): 30 m in 60 m, 50-180% slopes measured on SR 99 and I-5, ramps CS2 traffic struggles
    /// on. Here every bridge or tunnel point aims for its planned offset, every junction where an ordinary road meets
    /// keeps its height, and between them the offset may change only at the way's maximum grade, along the road network
    /// in both directions: a structure ramps over its own length and on into its approach roads, never past a junction.
    /// Where a junction is too close, the structure stays lower near it.
    /// </summary>
    internal static partial class OsmParser
    {
        private static float MaxGrade(OsmWay way)
        {
            switch ((way.Highway ?? string.Empty).ToLowerInvariant())
            {
                case "motorway": case "motorway_link": case "trunk": case "trunk_link":
                case "primary": case "primary_link":
                    return 0.06f;
                case "secondary": case "secondary_link": case "tertiary": case "tertiary_link":
                    return 0.08f;
                case "footway": case "path": case "pedestrian": case "cycleway": case "bridleway": case "steps":
                    return 0.12f;
                case "rail": case "light_rail": case "subway": case "tram": case "monorail": case "narrow_gauge":
                case "funicular": case "preserved":
                    return 0.03f;
                default:
                    return 0.10f;
            }
        }

        /// <summary>
        /// The steeper grade allowed where a bridge must clear what it crosses (or a tunnel pass under it) and the
        /// normal grade leaves it too low: real interchange ramps and footbridge approaches are steeper than the
        /// gentle rise a long viaduct gets.
        /// </summary>
        private static float MaxClearanceGrade(OsmWay way)
        {
            switch ((way.Highway ?? string.Empty).ToLowerInvariant())
            {
                case "motorway": case "motorway_link": case "trunk": case "trunk_link":
                case "primary": case "primary_link":
                    return 0.08f;
                case "secondary": case "secondary_link": case "tertiary": case "tertiary_link":
                    return 0.10f;
                case "steps":
                    return 0.5f;
                case "footway": case "path": case "pedestrian": case "cycleway": case "bridleway":
                    return 0.15f;
                case "rail": case "light_rail": case "subway": case "tram": case "monorail": case "narrow_gauge":
                case "funicular": case "preserved":
                    return 0.04f;
                default:
                    return 0.12f;
            }
        }

        private sealed class GradeGraph
        {
            public readonly Dictionary<(int, int), int> Index = new Dictionary<(int, int), int>();
            public readonly List<List<(int To, float Cost, float Relaxed)>> Edges = new List<List<(int, float, float)>>();
            public readonly List<float> Target = new List<float>();     // planned offset (0 = ground)
            public readonly List<float> Reference = new List<float>();  // height the offset is measured from
            public readonly List<bool> HasStructuralReference = new List<bool>();
            public readonly List<int> Degree = new List<int>();          // segment ends meeting here
            public readonly List<float> PinOffset = new List<float>();   // see Node
            public readonly List<bool> PinFromThrough = new List<bool>();

            /// <summary>
            /// The pin offset of a junction: the offset of a way passing through it (a path ending on a bridge deck must
            /// not pull the deck down), else of the way closest to the ground.
            /// </summary>
            public int Node(float3 p, float reference, bool structuralReference, float target, bool through)
            {
                var key = ((int)math.round(p.x * 20f), (int)math.round(p.z * 20f));
                if (!Index.TryGetValue(key, out var i))
                {
                    i = Edges.Count;
                    Index[key] = i;
                    Edges.Add(new List<(int, float, float)>());
                    Target.Add(target);
                    Reference.Add(reference);
                    HasStructuralReference.Add(structuralReference);
                    Degree.Add(0);
                    PinOffset.Add(target);
                    PinFromThrough.Add(through);
                    return i;
                }
                // A structure's reference (the line between its ends) wins over terrain at a shared point.
                if (structuralReference && !HasStructuralReference[i])
                {
                    Reference[i] = reference;
                    HasStructuralReference[i] = true;
                }
                if (math.abs(target) > math.abs(Target[i]))
                    Target[i] = target;
                if (through && !PinFromThrough[i])
                {
                    PinOffset[i] = target;
                    PinFromThrough[i] = true;
                }
                else if (through == PinFromThrough[i] && math.abs(target) < math.abs(PinOffset[i]))
                    PinOffset[i] = target;
                return i;
            }
        }

        private static void ApplyGradeLimitedElevations(List<OsmWay> ways)
        {
            var graph = new GradeGraph();
            var levelCrossings = FindLevelCrossings(ways);
            var plans = PlanStructures(ways);
            var wayNodes = new List<(OsmWay Way, int[] Nodes)>();
            foreach (var way in ways)
            {
                var pts = way.Points;
                if (pts.Count < 2 || way.IsPier || IsPedestrianDetailFootway(way))
                    continue;
                var heights = way.TerrainHeights.Count == pts.Count ? way.TerrainHeights : null;
                var structural = way.UsesStructuralElevation && heights != null;
                var target = way.BaseElevationOffsetMeters;
                var nodes = new int[pts.Count];
                var total = 0f;
                for (var i = 1; i < pts.Count; i++)
                    total += math.distance(pts[i - 1].xz, pts[i].xz);
                var along = 0f;
                for (var i = 0; i < pts.Count; i++)
                {
                    if (i > 0)
                        along += math.distance(pts[i - 1].xz, pts[i].xz);
                    float reference;
                    var pointTarget = target;
                    if (plans.TryGetValue(way, out var plan))
                    {
                        reference = plan.Reference[i];
                        pointTarget = plan.Target[i];
                    }
                    else if (structural)
                        reference = math.lerp(heights[0], heights[pts.Count - 1], total > 0.01f ? along / total : 0f);
                    else
                        reference = heights != null ? heights[i] : pts[i].y - target;
                    nodes[i] = graph.Node(pts[i], reference, structural, pointTarget, i > 0 && i < pts.Count - 1);
                }
                var grade = MaxGrade(way);
                var relaxedGrade = MaxClearanceGrade(way);
                for (var i = 1; i < pts.Count; i++)
                {
                    var length = math.distance(pts[i - 1].xz, pts[i].xz);
                    var cost = length * grade;
                    var relaxed = length * relaxedGrade;
                    graph.Edges[nodes[i - 1]].Add((nodes[i], cost, relaxed));
                    graph.Edges[nodes[i]].Add((nodes[i - 1], cost, relaxed));
                    graph.Degree[nodes[i - 1]]++;
                    graph.Degree[nodes[i]]++;
                }
                wayNodes.Add((way, nodes));
            }

            var n = graph.Edges.Count;
            // Bridges pull up, tunnels pull down, each fading out at the grade.
            var up = Propagate(graph, n, i => graph.Target[i] > 0f ? graph.Target[i] : float.NegativeInfinity, decreasing: true, floor: 0f);
            var down = Propagate(graph, n, i => graph.Target[i] < 0f ? -graph.Target[i] : float.NegativeInfinity, decreasing: true, floor: 0f);
            // Junctions of 3+ segment ends keep the offset of their way closest to the ground; around them the offset may
            // differ from it only by the grade.
            var maxAbs = graph.Target.Count > 0 ? graph.Target.Max(t => math.abs(t)) : 0f;
            var hi = Propagate(graph, n, i => graph.Degree[i] >= 3 ? -graph.PinOffset[i] : float.NegativeInfinity, decreasing: true, floor: -maxAbs - 1f);
            var lo = Propagate(graph, n, i => graph.Degree[i] >= 3 ? graph.PinOffset[i] : float.NegativeInfinity, decreasing: true, floor: -maxAbs - 1f);

            var offset = new float[n];
            var changedNodes = 0;
            for (var i = 0; i < n; i++)
            {
                var o = math.max(0f, up[i]) - math.max(0f, down[i]);
                if (!float.IsNegativeInfinity(hi[i]))
                    o = math.min(o, -hi[i]);
                if (!float.IsNegativeInfinity(lo[i]))
                    o = math.max(o, lo[i]);
                offset[i] = o;
            }
            ResolveLevelCrossings(graph, n, wayNodes, offset, levelCrossings);

            var maxSlopeBefore = 0f;
            var maxSlopeAfter = 0f;
            foreach (var (way, nodes) in wayNodes)
            {
                var pts = way.Points;
                for (var i = 0; i < pts.Count; i++)
                {
                    var y = graph.Reference[nodes[i]] + offset[nodes[i]];
                    if (i > 0)
                    {
                        var len = math.max(0.5f, math.distance(pts[i - 1].xz, pts[i].xz));
                        maxSlopeBefore = math.max(maxSlopeBefore, math.abs(pts[i].y - pts[i - 1].y) / len);
                    }
                    if (math.abs(y - pts[i].y) > 0.01f)
                    {
                        var p = pts[i];
                        p.y = y;
                        pts[i] = p;
                        changedNodes++;
                    }
                }
                for (var i = 1; i < pts.Count; i++)
                    maxSlopeAfter = math.max(maxSlopeAfter, math.abs(pts[i].y - pts[i - 1].y) / math.max(0.5f, math.distance(pts[i - 1].xz, pts[i].xz)));
            }
            Mod.log.Info($"Grade-limited elevations: {changedNodes} way points re-levelled over {n} network points " +
                         $"(bridges and tunnels ramp at most 6-12% by road type, junctions keep their height).");
        }

        private const int LevelCrossingPasses = 4;

        /// <summary>
        /// Structure planning asks each bridge for clearance over what it crosses, but the grade limits then decide:
        /// a bridge whose crossing lies 10-40 m from where it lands, or from a junction, stays as low as the normal
        /// grade allows, and a footbridge 2 m over a street or a ramp through the road below were common (578 crossings
        /// under 4.5 m on full Seattle, 75% of them grade-limited or near the bridge's end). Here each crossing between
        /// ways at different OSM levels is checked after grading: where the gap is short, the moving way (bridge up,
        /// tunnel down) gets a height source at the crossing that spreads at the steeper clearance grade, still bounded
        /// by every junction's height at that grade. A few passes, so a deck lifted over one road can lift the deck
        /// above it in turn. What still lacks room (a crossing right at a junction) is counted and left.
        /// </summary>
        private static void ResolveLevelCrossings(GradeGraph graph, int n, List<(OsmWay Way, int[] Nodes)> wayNodes, float[] offset,
            List<LevelCrossing> crossings)
        {
            if (crossings.Count == 0 || n == 0)
                return;
            var nodesOf = new Dictionary<OsmWay, int[]>();
            foreach (var (w, ns) in wayNodes)
                nodesOf[w] = ns;
            var items = new List<(LevelCrossing C, int Node, int[] Other, int Seg, float T, int Partner)>();
            foreach (var c in crossings)
            {
                if (!nodesOf.TryGetValue(c.Mover, out var moverNodes) || !nodesOf.TryGetValue(c.Other, out var otherNodes))
                    continue;
                var mi = c.Mover.Points.FindIndex(p => math.distancesq(p.xz, c.At) < 1e-4f);
                if (mi < 0 || !TryProjectOnWay(c.Other.Points, c.At, out var seg, out var t))
                    continue;
                var pi = c.HasPartner ? c.Other.Points.FindIndex(p => math.distancesq(p.xz, c.Partner) < 1e-4f) : -1;
                items.Add((c, moverNodes[mi], otherNodes, seg, t, pi >= 0 ? otherNodes[pi] : -1));
            }

            float Gap((LevelCrossing C, int Node, int[] Other, int Seg, float T, int Partner) it, float[] off)
            {
                var mover = graph.Reference[it.Node] + off[it.Node];
                var other = math.lerp(graph.Reference[it.Other[it.Seg - 1]] + off[it.Other[it.Seg - 1]],
                    graph.Reference[it.Other[it.Seg]] + off[it.Other[it.Seg]], it.T);
                return it.C.Up ? mover - other : other - mover;
            }

            var reach = offset.Max(o => math.abs(o)) + 40f;
            var hi = Propagate(graph, n, i => graph.Degree[i] >= 3 ? -graph.PinOffset[i] : float.NegativeInfinity, true, -reach, relaxed: true);
            var lo = Propagate(graph, n, i => graph.Degree[i] >= 3 ? graph.PinOffset[i] : float.NegativeInfinity, true, -reach, relaxed: true);
            float Ceiling(int i) => float.IsNegativeInfinity(hi[i]) ? float.PositiveInfinity : -hi[i];
            float Floor(int i) => float.IsNegativeInfinity(lo[i]) ? float.NegativeInfinity : lo[i];
            var up = new float[n];
            var down = new float[n];
            for (var i = 0; i < n; i++)
                up[i] = down[i] = float.NegativeInfinity;

            var final = (float[])offset.Clone();
            var shortBefore = items.Count(it => Gap(it, final) < it.C.Clearance - 0.05f);
            for (var pass = 0; pass < LevelCrossingPasses; pass++)
            {
                var added = false;
                bool Raise(float[] sources, int node, float want)
                {
                    if (want <= sources[node] + 0.01f)
                        return false;
                    sources[node] = want;
                    return true;
                }
                foreach (var it in items)
                {
                    var deficit = it.C.Clearance - Gap(it, final);
                    if (deficit <= 0.05f)
                        continue;
                    if (it.C.Up)
                    {
                        // The bridge rises as far as its junctions allow; a ground way below dips for the rest.
                        var rise = math.min(deficit, math.max(0f, Ceiling(it.Node) - final[it.Node]));
                        if (rise > 0.05f)
                            added |= Raise(up, it.Node, final[it.Node] + rise);
                        var rest = deficit - rise;
                        if (rest > 0.05f && it.Partner >= 0)
                            added |= Raise(down, it.Partner, -(final[it.Partner] - rest));
                    }
                    else
                        added |= Raise(down, it.Node, -(final[it.Node] - deficit));
                }
                if (!added)
                    break;
                var upSpread = Propagate(graph, n, i => up[i], true, -reach, relaxed: true);
                var downSpread = Propagate(graph, n, i => down[i], true, -reach, relaxed: true);
                for (var i = 0; i < n; i++)
                {
                    var o = offset[i];
                    if (!float.IsNegativeInfinity(upSpread[i]))
                        o = math.max(o, math.min(upSpread[i], Ceiling(i)));
                    if (!float.IsNegativeInfinity(downSpread[i]))
                        o = math.min(o, math.max(-downSpread[i], Floor(i)));
                    final[i] = o;
                }
            }

            var shortAfter = 0;
            var dipped = 0;
            foreach (var it in items)
            {
                if (it.Partner >= 0 && final[it.Partner] < offset[it.Partner] - 0.5f)
                    dipped++;
                if (it.C.Clearance - Gap(it, final) > 0.05f)
                    shortAfter++;
            }
            Array.Copy(final, offset, n);
            Mod.log.Info($"Level crossings: {items.Count} bridges/tunnels crossing a way at another OSM level, {shortBefore} short of clearance " +
                         $"({LevelClearanceOverRoadMeters} m over roads/rail, {LevelClearanceOverPathMeters} m over paths) after grading; " +
                         $"{shortBefore - shortAfter} given room at up to the clearance grade ({dipped} with the way below dipping under), " +
                         $"{shortAfter} still short (junctions too close on both).");
        }

        private static bool TryProjectOnWay(List<float3> pts, float2 at, out int seg, out float t)
        {
            seg = -1;
            t = 0f;
            var best = float.MaxValue;
            for (var i = 1; i < pts.Count; i++)
            {
                var ab = pts[i].xz - pts[i - 1].xz;
                var len2 = math.lengthsq(ab);
                var tt = len2 > 1e-6f ? math.saturate(math.dot(at - pts[i - 1].xz, ab) / len2) : 0f;
                var d = math.distancesq(at, pts[i - 1].xz + ab * tt);
                if (d < best)
                {
                    best = d;
                    seg = i;
                    t = tt;
                }
            }
            return seg > 0 && best < 1f;
        }

        /// <summary>
        /// Multi-source spread on the grade graph: value(i) = max over sources s of (start(s) - cost(s, i)), cut off at
        /// floor. Returns NegativeInfinity where nothing reached.
        /// </summary>
        private static float[] Propagate(GradeGraph graph, int n, Func<int, float> start, bool decreasing, float floor, bool relaxed = false)
        {
            var value = new float[n];
            var heap = new List<(float Value, int Node)>();
            for (var i = 0; i < n; i++)
            {
                value[i] = start(i);
                if (!float.IsNegativeInfinity(value[i]))
                    HeapPush(heap, (value[i], i));
            }
            while (heap.Count > 0)
            {
                var (v, u) = HeapPop(heap);
                if (v < value[u])
                    continue;
                foreach (var (to, cost, relaxedCost) in graph.Edges[u])
                {
                    var w = v - (relaxed ? relaxedCost : cost);
                    if (w <= floor || w <= value[to])
                        continue;
                    value[to] = w;
                    HeapPush(heap, (w, to));
                }
            }
            return value;
        }

        // Max-heap on Value.
        private static void HeapPush(List<(float Value, int Node)> heap, (float Value, int Node) item)
        {
            heap.Add(item);
            var i = heap.Count - 1;
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (heap[parent].Value >= heap[i].Value)
                    break;
                (heap[parent], heap[i]) = (heap[i], heap[parent]);
                i = parent;
            }
        }

        private static (float Value, int Node) HeapPop(List<(float Value, int Node)> heap)
        {
            var top = heap[0];
            var last = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count > 0)
            {
                heap[0] = last;
                var i = 0;
                while (true)
                {
                    var l = 2 * i + 1;
                    var r = l + 1;
                    var m = i;
                    if (l < heap.Count && heap[l].Value > heap[m].Value) m = l;
                    if (r < heap.Count && heap[r].Value > heap[m].Value) m = r;
                    if (m == i)
                        break;
                    (heap[m], heap[i]) = (heap[i], heap[m]);
                    i = m;
                }
            }
            return top;
        }
    }
}
