using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// Realistic heights for bridges and tunnels. OSM gives no heights, only bridge/tunnel and their order (layer); the
    /// importer used layer x 10 m above or below the line between each OSM piece's end terrain. That put the SR 99 tunnel
    /// (layer -8) 80-100 m deep, the Ship Canal Bridge (layer 3) 30 m above the hillsides it meets, and dipped the
    /// express lanes towards the water where two OSM pieces of one bridge meet mid-span.
    /// Here, per connected structure (bridge pieces joined end to end count as one):
    /// - the reference is the ground at the structure's outer ends, interpolated along it (inverse distance), so a valley
    ///   bridge runs from bank to bank;
    /// - a bridge is lifted above that only where it must clear something below (a road, path or rail, or a lower deck,
    ///   lower = smaller layer offset) by StructureClearanceMeters; a tunnel keeps TunnelCoverMeters of ground above it and
    ///   the same clearance below anything shallower over it.
    /// The result is a per-point reference and target offset that ApplyGradeLimitedElevations ramps between.
    /// </summary>
    internal static partial class OsmParser
    {
        private const float StructureClearanceMeters = 6.5f;
        private const float TunnelCoverMeters = 8f;
        // Two ways overlap in plan when their centre lines are closer than half their widths added (lanes x 3.5 m plus
        // shoulders); OSM draws the Ship Canal Bridge's express lanes 9-17 m beside the upper deck's carriageways.
        private const float StructureOverlapMeters = 20f; // grid reach
        private static float HalfWidth(OsmWay w) => math.max(2f, (w.Lanes > 0 ? w.Lanes : IsPathLike(w) ? 1 : 2) * 1.75f + 1.5f);
        // Near a structure's outer ends, what continues in line is its approach, not something below it.
        private const float StructureEndSkipMeters = 12f;
        private const float StructureSampleMeters = 10f;

        private sealed class StructurePlan
        {
            public float[] Reference;
            public float[] Target;
        }

        private static Dictionary<OsmWay, StructurePlan> PlanStructures(List<OsmWay> ways)
        {
            var plans = new Dictionary<OsmWay, StructurePlan>();
            (int, int) Key(float3 p) => ((int)math.round(p.x * 20f), (int)math.round(p.z * 20f));
            bool IsStructure(OsmWay w) => w.UsesStructuralElevation && !w.IsPier && w.Points.Count >= 2 &&
                                          w.TerrainHeights.Count == w.Points.Count && (w.IsBridge || w.IsTunnel);
            var structures = ways.Where(IsStructure).ToList();
            if (structures.Count == 0)
                return plans;

            // Which ways use each point.
            var waysAt = new Dictionary<(int, int), List<OsmWay>>();
            foreach (var w in ways)
                foreach (var p in w.Points)
                {
                    var k = Key(p);
                    if (!waysAt.TryGetValue(k, out var l))
                        waysAt[k] = l = new List<OsmWay>();
                    if (!l.Contains(w))
                        l.Add(w);
                }

            // Connected structures: structure ways of the same kind sharing a point.
            var component = new Dictionary<OsmWay, int>();
            var components = new List<List<OsmWay>>();
            foreach (var s in structures)
            {
                if (component.ContainsKey(s))
                    continue;
                var list = new List<OsmWay>();
                var stack = new Stack<OsmWay>();
                stack.Push(s);
                component[s] = components.Count;
                while (stack.Count > 0)
                {
                    var w = stack.Pop();
                    list.Add(w);
                    foreach (var p in w.Points)
                        foreach (var o in waysAt[Key(p)])
                            if (!component.ContainsKey(o) && IsStructure(o) && o.IsBridge == s.IsBridge)
                            {
                                component[o] = components.Count;
                                stack.Push(o);
                            }
                }
                components.Add(list);
            }

            // Spatial index of every way segment, for what passes under or over.
            var grid = new Dictionary<(int, int), List<(OsmWay Way, int Seg)>>();
            const float cell = 32f;
            foreach (var w in ways)
            {
                if (w.IsPier)
                    continue;
                for (var i = 1; i < w.Points.Count; i++)
                {
                    var a = w.Points[i - 1].xz;
                    var b = w.Points[i].xz;
                    var min = math.min(a, b) - StructureOverlapMeters;
                    var max = math.max(a, b) + StructureOverlapMeters;
                    for (var x = (int)math.floor(min.x / cell); x <= (int)math.floor(max.x / cell); x++)
                        for (var z = (int)math.floor(min.y / cell); z <= (int)math.floor(max.y / cell); z++)
                        {
                            if (!grid.TryGetValue((x, z), out var l))
                                grid[(x, z)] = l = new List<(OsmWay, int)>();
                            l.Add((w, i));
                        }
                }
            }

            // Final heights decided so far (structures processed lower layers first for bridges, shallower first for
            // tunnels), so a deck above another deck can clear it.
            var planned = new Dictionary<OsmWay, float[]>();
            float HeightOf(OsmWay w, int seg, float2 at)
            {
                var a = w.Points[seg - 1];
                var b = w.Points[seg];
                var ab = b.xz - a.xz;
                var t = math.lengthsq(ab) > 1e-6f ? math.saturate(math.dot(at - a.xz, ab) / math.lengthsq(ab)) : 0f;
                if (planned.TryGetValue(w, out var ys))
                    return math.lerp(ys[seg - 1], ys[seg], t);
                return math.lerp(a.y, b.y, t);
            }

            var order = Enumerable.Range(0, components.Count)
                .OrderBy(c => components[c][0].IsBridge ? 0 : 1)
                .ThenBy(c => components[c][0].IsBridge
                    ? components[c].Min(w => w.BaseElevationOffsetMeters)
                    : -components[c].Max(w => w.BaseElevationOffsetMeters))
                .ToList();

            int bridgesLifted = 0, tunnelsLowered = 0;
            foreach (var c in order)
            {
                var comp = components[c];
                var compSet = new HashSet<OsmWay>(comp);
                var isBridge = comp[0].IsBridge;
                var level = isBridge ? comp.Min(w => w.BaseElevationOffsetMeters) : comp.Max(w => w.BaseElevationOffsetMeters);
                // Ways connected to this structure (its approaches and anything joining it) are not "under" it.
                var connected = new HashSet<OsmWay>(compSet);
                foreach (var w in comp)
                    foreach (var p in w.Points)
                        foreach (var o in waysAt[Key(p)])
                            connected.Add(o);

                // Outer ends: points where the structure meets a non-structure way, or way ends used by nothing else.
                var ends = new List<(float3 P, float Terrain)>();
                foreach (var w in comp)
                    for (var i = 0; i < w.Points.Count; i++)
                    {
                        var users = waysAt[Key(w.Points[i])];
                        var meetsOther = users.Any(o => !compSet.Contains(o));
                        var isWayEnd = i == 0 || i == w.Points.Count - 1;
                        var deadEnd = isWayEnd && users.Count == 1;
                        if ((meetsOther && isWayEnd) || deadEnd)
                            ends.Add((w.Points[i], w.TerrainHeights[i]));
                    }
                if (ends.Count == 0)
                    ends.Add((comp[0].Points[0], comp[0].TerrainHeights[0]));

                foreach (var w in comp)
                {
                    var n = w.Points.Count;
                    var reference = new float[n];
                    for (var i = 0; i < n; i++)
                    {
                        // Inverse plan distance to the outer ends (straight-line: structures run roughly straight between them).
                        float num = 0f, den = 0f;
                        var exact = float.NaN;
                        foreach (var (p, t) in ends)
                        {
                            var d = math.distance(p.xz, w.Points[i].xz);
                            if (d < 0.5f)
                            {
                                exact = t;
                                break;
                            }
                            num += t / d;
                            den += 1f / d;
                        }
                        reference[i] = !float.IsNaN(exact) ? exact : num / den;
                    }

                    // Per segment, the most the structure must move off its reference to clear what is under (bridge)
                    // or over (tunnel) it, and for tunnels the ground cover.
                    var segNeed = new float[n];
                    for (var i = 1; i < n; i++)
                    {
                        var a = w.Points[i - 1];
                        var b = w.Points[i];
                        var len = math.distance(a.xz, b.xz);
                        var samples = math.max(1, (int)math.ceil(len / StructureSampleMeters));
                        var need = 0f;
                        for (var s = 0; s <= samples; s++)
                        {
                            var t = s / (float)samples;
                            var at = math.lerp(a.xz, b.xz, t);
                            var refHere = math.lerp(reference[i - 1], reference[i], t);
                            if (!isBridge)
                            {
                                var ground = math.lerp(w.TerrainHeights[i - 1], w.TerrainHeights[i], t);
                                need = math.min(need, ground - TunnelCoverMeters - refHere);
                            }
                            if (ends.Any(e => math.distance(e.P.xz, at) < StructureEndSkipMeters))
                                continue;
                            var key = ((int)math.floor(at.x / cell), (int)math.floor(at.y / cell));
                            if (!grid.TryGetValue(key, out var candidates))
                                continue;
                            foreach (var (o, seg) in candidates)
                            {
                                if (connected.Contains(o))
                                    continue;
                                var oa = o.Points[seg - 1].xz;
                                var ob = o.Points[seg].xz;
                                var oab = ob - oa;
                                var ot = math.lengthsq(oab) > 1e-6f ? math.saturate(math.dot(at - oa, oab) / math.lengthsq(oab)) : 0f;
                                if (math.distance(oa + oab * ot, at) > HalfWidth(w) + HalfWidth(o))
                                    continue;
                                var other = o.UsesStructuralElevation && (o.IsBridge || o.IsTunnel) ? o.BaseElevationOffsetMeters : 0f;
                                var y = HeightOf(o, seg, at);
                                if (isBridge && other < level)
                                    need = math.max(need, y + StructureClearanceMeters - refHere);
                                else if (!isBridge && other > level)
                                    need = math.min(need, y - StructureClearanceMeters - refHere);
                            }
                        }
                        segNeed[i] = need;
                    }
                    var target = new float[n];
                    for (var i = 0; i < n; i++)
                    {
                        var left = i > 0 ? segNeed[i] : 0f;
                        var right = i < n - 1 ? segNeed[i + 1] : 0f;
                        target[i] = isBridge ? math.max(left, right) : math.min(left, right);
                    }
                    if (isBridge && target.Any(t => t > 0.5f)) bridgesLifted++;
                    if (!isBridge && target.Any(t => t < -0.5f)) tunnelsLowered++;
                    plans[w] = new StructurePlan { Reference = reference, Target = target };
                    var ys = new float[n];
                    for (var i = 0; i < n; i++)
                        ys[i] = reference[i] + target[i];
                    planned[w] = ys;
                }
            }
            Mod.log.Info($"Structures: {components.Count} bridges/tunnels ({structures.Count} ways) placed from their ends' ground; " +
                         $"{bridgesLifted} bridge ways lifted to clear what passes under ({StructureClearanceMeters} m), " +
                         $"{tunnelsLowered} tunnel ways kept {TunnelCoverMeters} m under ground.");
            return plans;
        }
    }
}
