using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal static partial class OsmParser
    {
        private const int PierSkeletonMaxCells = 300000;
        private const float PierSkeletonMinEdgeMeters = 2f;

        private sealed class SkeletonEdge
        {
            public int A, B; // node clusters (B = -1: ran out)
            public List<float2> Points = new List<float2>();
            public float Length
            {
                get
                {
                    var l = 0f;
                    for (var i = 1; i < Points.Count; i++)
                        l += math.distance(Points[i - 1], Points[i]);
                    return l;
                }
            }
        }

        /// <summary>
        /// The middle lines of a pier outline: rasterize it, thin it to a one-cell skeleton (Zhang-Suen), turn the
        /// skeleton into polylines between its ends and junctions, and drop the short side spurs that thinning leaves
        /// at corners (shorter than a few times the pier's half width there).
        /// </summary>
        private static List<List<float2>> PierSkeleton(List<float2> ring)
        {
            var b = ParkingGeometry.Bounds(ring);
            var size = b.zw - b.xy;
            var cell = 0.5f;
            while ((size.x / cell) * (size.y / cell) > PierSkeletonMaxCells)
                cell *= 1.5f;
            var w = (int)math.ceil(size.x / cell) + 3;
            var h = (int)math.ceil(size.y / cell) + 3;
            var origin = b.xy - cell;
            float2 Pos(int x, int y) => origin + new float2(x + 0.5f, y + 0.5f) * cell;

            var g = new bool[w, h];
            var filled = 0;
            for (var x = 1; x < w - 1; x++)
            {
                for (var y = 1; y < h - 1; y++)
                {
                    if (ParkingGeometry.Contains(ring, Pos(x, y)))
                    {
                        g[x, y] = true;
                        filled++;
                    }
                }
            }
            if (filled < 4)
                return null;

            Thin(g, w, h);

            // Skeleton cells and their degree (8-neighbourhood).
            int Degree(int x, int y)
            {
                var d = 0;
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        if ((dx != 0 || dy != 0) && g[x + dx, y + dy])
                            d++;
                return d;
            }

            // Node cells (ends and junctions), clustered when adjacent.
            var cluster = new int[w, h];
            for (var x = 0; x < w; x++)
                for (var y = 0; y < h; y++)
                    cluster[x, y] = -1;
            var clusterCells = new List<List<(int X, int Y)>>();
            for (var x = 1; x < w - 1; x++)
            {
                for (var y = 1; y < h - 1; y++)
                {
                    if (!g[x, y] || Degree(x, y) == 2 || cluster[x, y] >= 0)
                        continue;
                    var id = clusterCells.Count;
                    var cells = new List<(int, int)>();
                    var stack = new Stack<(int X, int Y)>();
                    stack.Push((x, y));
                    cluster[x, y] = id;
                    while (stack.Count > 0)
                    {
                        var c = stack.Pop();
                        cells.Add(c);
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            for (var dy = -1; dy <= 1; dy++)
                            {
                                int nx = c.X + dx, ny = c.Y + dy;
                                if (g[nx, ny] && cluster[nx, ny] < 0 && Degree(nx, ny) != 2)
                                {
                                    cluster[nx, ny] = id;
                                    stack.Push((nx, ny));
                                }
                            }
                        }
                    }
                    clusterCells.Add(cells);
                }
            }
            if (clusterCells.Count == 0)
                return null; // a closed loop with no ends: not a pier walkway shape we handle
            var clusterPos = clusterCells.Select(cs => cs.Aggregate(float2.zero, (s, c) => s + Pos(c.X, c.Y)) / cs.Count).ToList();

            // Trace the edges between node clusters.
            var visited = new bool[w, h];
            var edges = new List<SkeletonEdge>();
            var direct = new HashSet<(int, int)>();
            for (var ci = 0; ci < clusterCells.Count; ci++)
            {
                foreach (var start in clusterCells[ci])
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;
                            int qx = start.X + dx, qy = start.Y + dy;
                            if (!g[qx, qy])
                                continue;
                            var qc = cluster[qx, qy];
                            if (qc >= 0)
                            {
                                if (qc != ci && direct.Add((math.min(ci, qc), math.max(ci, qc))))
                                    edges.Add(new SkeletonEdge { A = ci, B = qc, Points = { clusterPos[ci], clusterPos[qc] } });
                                continue;
                            }
                            if (visited[qx, qy])
                                continue;
                            var edge = new SkeletonEdge { A = ci, B = -1 };
                            edge.Points.Add(clusterPos[ci]);
                            int px = start.X, py = start.Y, cx = qx, cy = qy;
                            while (true)
                            {
                                if (cluster[cx, cy] >= 0)
                                {
                                    edge.B = cluster[cx, cy];
                                    edge.Points.Add(clusterPos[edge.B]);
                                    break;
                                }
                                visited[cx, cy] = true;
                                edge.Points.Add(Pos(cx, cy));
                                var moved = false;
                                for (var ex = -1; ex <= 1 && !moved; ex++)
                                {
                                    for (var ey = -1; ey <= 1 && !moved; ey++)
                                    {
                                        if (ex == 0 && ey == 0)
                                            continue;
                                        int nx = cx + ex, ny = cy + ey;
                                        if (!g[nx, ny] || (nx == px && ny == py) || (cluster[nx, ny] < 0 && visited[nx, ny]))
                                            continue;
                                        if (cluster[nx, ny] == ci && edge.Points.Count <= 2)
                                            continue; // don't step straight back into the start
                                        px = cx; py = cy; cx = nx; cy = ny;
                                        moved = true;
                                    }
                                }
                                if (!moved)
                                    break;
                            }
                            edges.Add(edge);
                        }
                    }
                }
            }

            // Prune the corner spurs, a few rounds (removing one can expose another).
            for (var round = 0; round < 3; round++)
            {
                var degree = Degrees(edges, clusterCells.Count);
                var removed = edges.RemoveAll(e =>
                {
                    // An end that ran out (-1) is a free end too.
                    var dA = e.A >= 0 ? degree[e.A] : 1;
                    var dB = e.B >= 0 ? degree[e.B] : 1;
                    int junction;
                    if (dB == 1 && dA >= 3 && e.A >= 0) junction = e.A;
                    else if (dA == 1 && dB >= 3 && e.B >= 0) junction = e.B;
                    else return false;
                    var halfWidth = ParkingGeometry.DistanceToRing(ring, clusterPos[junction]);
                    return e.Length < math.max(3f, 2.5f * halfWidth);
                });
                if (removed == 0)
                    break;
            }

            // Join edges through clusters that now have just two.
            while (true)
            {
                var degree = Degrees(edges, clusterCells.Count);
                var c = Enumerable.Range(0, clusterCells.Count).FirstOrDefault(i => degree[i] == 2 && edges.Count(e => e.A == i || e.B == i) == 2 && !edges.Any(e => e.A == i && e.B == i));
                if (degree.Length == 0 || degree[c] != 2 || edges.Count(e => e.A == c || e.B == c) != 2 || edges.Any(e => e.A == c && e.B == c))
                    break;
                var pair = edges.Where(e => e.A == c || e.B == c).ToList();
                var first = Oriented(pair[0], c, atEnd: true);
                var second = Oriented(pair[1], c, atEnd: false);
                var joined = new SkeletonEdge { A = first.A, B = second.B };
                joined.Points.AddRange(first.Points);
                joined.Points.AddRange(second.Points.Skip(1));
                edges.Remove(pair[0]);
                edges.Remove(pair[1]);
                edges.Add(joined);
            }

            var result = new List<List<float2>>();
            foreach (var e in edges)
            {
                if (e.Length < PierSkeletonMinEdgeMeters)
                    continue;
                result.Add(SimplifyLine(e.Points, 0.75f));
            }
            DropParallelSpurs(result);
            return result.Count > 0 ? result : null;
        }

        private const float PierParallelMeters = 2.5f;

        /// <summary>
        /// Thinning can leave a second line beside the real middle line where a pier widens (seen on Green Lake's L
        /// pier: an 12 m strip 1.5 m beside the main walkway). A line with a free end that runs alongside another line
        /// for most of its length is such a double: drop it.
        /// </summary>
        private static void DropParallelSpurs(List<List<float2>> lines)
        {
            bool removed;
            do
            {
                removed = false;
                for (var i = 0; i < lines.Count && !removed; i++)
                {
                    var line = lines[i];
                    var others = lines.Where((_, j) => j != i).ToList();
                    if (others.Count == 0)
                        return;
                    bool Shared(float2 p) => others.Any(o => math.distance(o[0], p) < 0.05f || math.distance(o[o.Count - 1], p) < 0.05f);
                    if (Shared(line[0]) && Shared(line[line.Count - 1]))
                        continue; // joins two lines: part of the walkway network
                    var samples = Resample(line, 1f);
                    var near = samples.Count(p => others.Any(o => DistanceToPolyline(o, p) < PierParallelMeters));
                    if (near >= samples.Count * 0.7f)
                    {
                        lines.RemoveAt(i);
                        removed = true;
                    }
                }
            }
            while (removed);
        }

        private static List<float2> Resample(List<float2> line, float step)
        {
            var result = new List<float2> { line[0] };
            for (var i = 1; i < line.Count; i++)
            {
                var a = line[i - 1];
                var b = line[i];
                var n = Math.Max(1, (int)math.ceil(math.distance(a, b) / step));
                for (var k = 1; k <= n; k++)
                    result.Add(math.lerp(a, b, k / (float)n));
            }
            return result;
        }

        private static float DistanceToPolyline(List<float2> line, float2 p)
        {
            var best = float.MaxValue;
            for (var i = 1; i < line.Count; i++)
                best = math.min(best, ParkingGeometry.DistanceToSegment(p, line[i - 1], line[i]));
            return best;
        }

        private static int[] Degrees(List<SkeletonEdge> edges, int clusters)
        {
            var degree = new int[clusters];
            foreach (var e in edges)
            {
                if (e.A >= 0)
                    degree[e.A]++;
                if (e.B >= 0)
                    degree[e.B]++;
            }
            return degree;
        }

        /// <summary>The edge with cluster c at its end (atEnd) or at its start.</summary>
        private static SkeletonEdge Oriented(SkeletonEdge e, int c, bool atEnd)
        {
            var cAtEnd = e.B == c;
            if (cAtEnd == atEnd)
                return e;
            var r = new SkeletonEdge { A = e.B, B = e.A };
            r.Points.AddRange(Enumerable.Reverse(e.Points));
            return r;
        }

        /// <summary>Zhang-Suen thinning, in place (the grid has a one-cell empty border).</summary>
        private static void Thin(bool[,] g, int w, int h)
        {
            var remove = new List<(int, int)>();
            bool changed;
            do
            {
                changed = false;
                for (var step = 0; step < 2; step++)
                {
                    remove.Clear();
                    for (var x = 1; x < w - 1; x++)
                    {
                        for (var y = 1; y < h - 1; y++)
                        {
                            if (!g[x, y])
                                continue;
                            // p2..p9 clockwise from north.
                            bool p2 = g[x, y + 1], p3 = g[x + 1, y + 1], p4 = g[x + 1, y], p5 = g[x + 1, y - 1];
                            bool p6 = g[x, y - 1], p7 = g[x - 1, y - 1], p8 = g[x - 1, y], p9 = g[x - 1, y + 1];
                            var n = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                            if (n < 2 || n > 6)
                                continue;
                            var seq = new[] { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
                            var transitions = 0;
                            for (var i = 0; i < 8; i++)
                                if (!seq[i] && seq[i + 1])
                                    transitions++;
                            if (transitions != 1)
                                continue;
                            if (step == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8))
                                continue;
                            remove.Add((x, y));
                        }
                    }
                    foreach (var (x, y) in remove)
                        g[x, y] = false;
                    changed |= remove.Count > 0;
                }
            }
            while (changed);
        }
    }
}
