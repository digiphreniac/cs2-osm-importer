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

        private sealed class GradeGraph
        {
            public readonly Dictionary<(int, int), int> Index = new Dictionary<(int, int), int>();
            public readonly List<List<(int To, float Cost)>> Edges = new List<List<(int, float)>>();
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
                    Edges.Add(new List<(int, float)>());
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
            var plans = PlanStructures(ways);
            var wayNodes = new List<(OsmWay Way, int[] Nodes)>();
            foreach (var way in ways)
            {
                var pts = way.Points;
                if (pts.Count < 2 || way.IsPier)
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
                for (var i = 1; i < pts.Count; i++)
                {
                    var cost = math.distance(pts[i - 1].xz, pts[i].xz) * grade;
                    graph.Edges[nodes[i - 1]].Add((nodes[i], cost));
                    graph.Edges[nodes[i]].Add((nodes[i - 1], cost));
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

        /// <summary>
        /// Multi-source spread on the grade graph: value(i) = max over sources s of (start(s) - cost(s, i)), cut off at
        /// floor. Returns NegativeInfinity where nothing reached.
        /// </summary>
        private static float[] Propagate(GradeGraph graph, int n, Func<int, float> start, bool decreasing, float floor)
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
                foreach (var (to, cost) in graph.Edges[u])
                {
                    var w = v - cost;
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
