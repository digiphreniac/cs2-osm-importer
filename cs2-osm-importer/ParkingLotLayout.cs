using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal static class ParkingGeometry
    {
        public static float SignedArea(IReadOnlyList<float2> ring)
        {
            var sum = 0f;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                sum += ring[j].x * ring[i].y - ring[i].x * ring[j].y;
            return sum * 0.5f;
        }

        /// <summary>(minX, minZ, maxX, maxZ)</summary>
        public static float4 Bounds(IReadOnlyList<float2> ring)
        {
            var b = new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            foreach (var p in ring)
                b = new float4(math.min(b.x, p.x), math.min(b.y, p.y), math.max(b.z, p.x), math.max(b.w, p.y));
            return b;
        }

        public static bool Contains(IReadOnlyList<float2> ring, float2 p)
        {
            var inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        public static float DistanceToSegment(float2 p, float2 a, float2 b)
        {
            var ab = b - a;
            var len2 = math.lengthsq(ab);
            var t = len2 > 1e-9f ? math.saturate(math.dot(p - a, ab) / len2) : 0f;
            return math.distance(p, a + ab * t);
        }

        public static float2 ClosestOnSegment(float2 p, float2 a, float2 b)
        {
            var ab = b - a;
            var len2 = math.lengthsq(ab);
            var t = len2 > 1e-9f ? math.saturate(math.dot(p - a, ab) / len2) : 0f;
            return a + ab * t;
        }

        public static float DistanceToRing(IReadOnlyList<float2> ring, float2 p)
        {
            var best = float.MaxValue;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                best = math.min(best, DistanceToSegment(p, ring[j], ring[i]));
            return best;
        }

        public struct OrientedRect
        {
            public float2 Center, AxisU, AxisV;
            public float SizeU, SizeV;
        }

        /// <summary>Smallest-area rectangle around the outline (one side is always along a convex hull edge).</summary>
        public static OrientedRect MinAreaRectangle(IReadOnlyList<float2> ring)
        {
            var hull = ConvexHull(ring);
            var best = new OrientedRect { Center = Centroid(ring), AxisU = new float2(1f, 0f), AxisV = new float2(0f, 1f) };
            var bestArea = float.MaxValue;
            for (var i = 0; i < hull.Count; i++)
            {
                var d = hull[(i + 1) % hull.Count] - hull[i];
                if (math.lengthsq(d) < 1e-6f)
                    continue;
                var u = math.normalize(d);
                var v = new float2(-u.y, u.x);
                float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                foreach (var p in hull)
                {
                    var pu = math.dot(p, u);
                    var pv = math.dot(p, v);
                    u0 = math.min(u0, pu); u1 = math.max(u1, pu);
                    v0 = math.min(v0, pv); v1 = math.max(v1, pv);
                }
                var area = (u1 - u0) * (v1 - v0);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = new OrientedRect
                    {
                        Center = u * ((u0 + u1) * 0.5f) + v * ((v0 + v1) * 0.5f),
                        AxisU = u,
                        AxisV = v,
                        SizeU = u1 - u0,
                        SizeV = v1 - v0,
                    };
                }
            }
            return best;
        }

        public static List<float2> ConvexHull(IReadOnlyList<float2> points)
        {
            var pts = points.Distinct().OrderBy(p => p.x).ThenBy(p => p.y).ToList();
            if (pts.Count < 3)
                return pts;
            float Cross(float2 o, float2 a, float2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var hull = new List<float2>();
            foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
            {
                var start = hull.Count;
                foreach (var p in pass)
                {
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        public static float2 Centroid(IReadOnlyList<float2> ring)
        {
            var c = float2.zero;
            foreach (var p in ring)
                c += p;
            return c / ring.Count;
        }
    }

    internal struct ParkingStall
    {
        public float2 Center;
        // Direction the decal's local +z points: towards the aisle it is reached from.
        public float2 Facing;
    }

    internal sealed class ParkingLotPlan
    {
        // Drive paths as straight segments; consecutive segments share bit-identical end points.
        public List<(float2 A, float2 B)> Paths { get; } = new List<(float2, float2)>();
        public List<ParkingStall> Stalls { get; } = new List<ParkingStall>();
        public bool HasEntrance;
        public string SkipReason = string.Empty;
    }

    /// <summary>
    /// Fills a parking outline with rows of perpendicular stalls (2.9 x 5.9 m, the vanilla stall size) along parallel
    /// aisles laid out along the outline's longest edge, joins the aisles with one connector, and runs an entrance from
    /// the connector to the street. Not a copy of any real lot: the goal is a working lot in the real outline.
    /// </summary>
    internal static class ParkingLotLayout
    {
        public const float StallWidth = 2.9f;
        public const float StallLength = 5.9f;
        public const float AisleHalfWidth = 3f;
        private const float EdgeMargin = 0.5f;
        private const float AisleEndMargin = 1.5f;
        private const float MinAisleLength = 8f;
        private const float MaxPathPieceLength = 40f;

        /// <param name="ring">Outline, counter-clockwise, no repeated closing point.</param>
        /// <param name="entranceInside">Point just inside the outline where the entrance should leave the lot.</param>
        /// <param name="streetPoint">Where the entrance path ends (just outside the street's edge), or null for no entrance.</param>
        /// <param name="streetApproach">A point a few metres back from streetPoint, square to the street.</param>
        /// <param name="blocked">True where a stall corner must not go (on or next to a street).</param>
        public static ParkingLotPlan Plan(IReadOnlyList<float2> ring, float2 entranceInside, float2? streetPoint, float2 streetApproach, Func<float2, bool> blocked)
        {
            var plan = new ParkingLotPlan();
            if (!streetPoint.HasValue)
            {
                plan.SkipReason = "no street within reach";
                return plan;
            }

            // Frame: u along the longest edge, v across it.
            var longest = 0;
            var longestLen = 0f;
            for (var i = 0; i < ring.Count; i++)
            {
                var len = math.distance(ring[i], ring[(i + 1) % ring.Count]);
                if (len > longestLen) { longestLen = len; longest = i; }
            }
            var d = math.normalize(ring[(longest + 1) % ring.Count] - ring[longest]);
            var n = new float2(-d.y, d.x);
            float U(float2 p) => math.dot(p, d);
            float V(float2 p) => math.dot(p, n);
            float2 W(float u, float v) => d * u + n * v;

            var vMin = ring.Min(V) + EdgeMargin;
            var vMax = ring.Max(V) - EdgeMargin;
            var width = vMax - vMin;
            const float module = 2f * StallLength + 2f * AisleHalfWidth;
            const float single = StallLength + 2f * AisleHalfWidth;

            // Aisles: (v, stall sides below/above).
            var aisles = new List<(float V, bool Below, bool Above)>();
            var doubles = (int)math.floor(width / module);
            var rest = width - doubles * module;
            var hasSingle = rest >= single;
            var used = doubles * module + (hasSingle ? single : 0f);
            if (used <= 0f)
            {
                plan.SkipReason = "too narrow for a stall row";
                return plan;
            }
            var v0 = vMin + (width - used) * 0.5f;
            for (var i = 0; i < doubles; i++)
                aisles.Add((v0 + i * module + StallLength + AisleHalfWidth, true, true));
            if (hasSingle)
                aisles.Add((v0 + doubles * module + StallLength + AisleHalfWidth, true, false));

            // Each aisle runs along the longest inside interval of its line.
            var aisleSpans = new List<(float V, float U0, float U1, bool Below, bool Above)>();
            foreach (var a in aisles)
            {
                if (!TryInsideInterval(ring, d, n, a.V, out var u0, out var u1))
                    continue;
                u0 += AisleEndMargin;
                u1 -= AisleEndMargin;
                if (u1 - u0 >= MinAisleLength)
                    aisleSpans.Add((a.V, u0, u1, a.Below, a.Above));
            }
            if (aisleSpans.Count == 0)
            {
                plan.SkipReason = "no aisle fits";
                return plan;
            }

            // Connector across the aisles at the u closest to the entrance that most aisles contain.
            var ue = U(entranceInside);
            var uc = ChooseConnectorU(aisleSpans.Select(s => (s.U0, s.U1)).ToList(), ue);
            var reachable = aisleSpans.Where(s => uc >= s.U0 - 0.01f && uc <= s.U1 + 0.01f).ToList();
            if (reachable.Count == 0)
            {
                plan.SkipReason = "aisles cannot be joined";
                return plan;
            }

            // Connector points: every aisle crossing, plus where it meets the entrance.
            var ve = math.clamp(V(entranceInside), vMin, vMax);
            var connectorVs = reachable.Select(s => s.V).ToList();
            var vLow = math.min(connectorVs.Min(), ve);
            var vHigh = math.max(connectorVs.Max(), ve);
            // Keep the connector inside the outline at the entrance end.
            if (!ParkingGeometry.Contains(ring, W(uc, ve)))
                ve = math.abs(ve - connectorVs.Min()) < math.abs(ve - connectorVs.Max()) ? connectorVs.Min() : connectorVs.Max();
            connectorVs.Add(ve);
            connectorVs = connectorVs.Distinct().OrderBy(v => v).ToList();
            if (vLow < connectorVs[0] || vHigh > connectorVs[connectorVs.Count - 1])
                connectorVs = connectorVs.Where(v => v >= vLow - 0.01f && v <= vHigh + 0.01f).ToList();

            var points = new Dictionary<(int, int), float2>();
            float2 Pt(float u, float v)
            {
                var key = ((int)math.round(u * 100f), (int)math.round(v * 100f));
                if (!points.TryGetValue(key, out var p))
                    points[key] = p = W(u, v);
                return p;
            }

            var drive = new List<(float2 A, float2 B)>();
            foreach (var s in reachable)
            {
                AddPolyline(drive, new[] { Pt(s.U0, s.V), Pt(uc, s.V), Pt(s.U1, s.V) });
            }
            AddPolyline(drive, connectorVs.Select(v => Pt(uc, v)).ToArray());
            var entranceStart = Pt(uc, ve);
            // The last metres run straight at the street: the game only links a car lane to a path that meets it squarely.
            AddPolyline(drive, new[] { entranceStart, streetApproach, streetPoint.Value });
            plan.HasEntrance = true;

            foreach (var seg in drive)
                AddPieces(plan.Paths, seg.A, seg.B);

            // Stalls along both sides of each reachable aisle.
            foreach (var s in reachable)
            {
                for (var side = -1; side <= 1; side += 2)
                {
                    if ((side < 0 && !s.Below) || (side > 0 && !s.Above))
                        continue;
                    var cv = s.V + side * (AisleHalfWidth + StallLength * 0.5f);
                    var facing = n * -side;
                    var count = (int)math.floor((s.U1 - s.U0) / StallWidth);
                    var start = s.U0 + ((s.U1 - s.U0) - count * StallWidth) * 0.5f + StallWidth * 0.5f;
                    for (var k = 0; k < count; k++)
                    {
                        var cu = start + k * StallWidth;
                        var center = W(cu, cv);
                        if (!StallFits(ring, d, n, center, blocked))
                            continue;
                        if (TooCloseToOtherDrive(center, d, n, drive, s.V, s.U0, s.U1, Pt))
                            continue;
                        plan.Stalls.Add(new ParkingStall { Center = center, Facing = facing });
                    }
                }
            }
            if (plan.Stalls.Count == 0)
                plan.SkipReason = "no stall clear of streets and the outline";
            return plan;
        }

        private static float ChooseConnectorU(List<(float U0, float U1)> spans, float ue)
        {
            var overlap0 = spans.Max(s => s.U0);
            var overlap1 = spans.Min(s => s.U1);
            if (overlap0 <= overlap1)
                return math.clamp(ue, overlap0 + StallWidth, math.max(overlap0 + StallWidth, overlap1 - StallWidth));
            // No common overlap: the u inside the most spans, nearest the entrance.
            var best = ue;
            var bestCount = -1;
            foreach (var c in spans.SelectMany(s => new[] { s.U0 + StallWidth, s.U1 - StallWidth, math.clamp(ue, s.U0, s.U1) }))
            {
                var count = spans.Count(s => c >= s.U0 && c <= s.U1);
                if (count > bestCount || (count == bestCount && math.abs(c - ue) < math.abs(best - ue)))
                {
                    best = c;
                    bestCount = count;
                }
            }
            return best;
        }

        private static bool TryInsideInterval(IReadOnlyList<float2> ring, float2 d, float2 n, float v, out float u0, out float u1)
        {
            var crossings = new List<float>();
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var va = math.dot(ring[j], n);
                var vb = math.dot(ring[i], n);
                if ((va > v) == (vb > v))
                    continue;
                var t = (v - va) / (vb - va);
                crossings.Add(math.lerp(math.dot(ring[j], d), math.dot(ring[i], d), t));
            }
            crossings.Sort();
            u0 = u1 = 0f;
            var bestLen = 0f;
            for (var k = 0; k + 1 < crossings.Count; k += 2)
            {
                if (crossings[k + 1] - crossings[k] > bestLen)
                {
                    bestLen = crossings[k + 1] - crossings[k];
                    u0 = crossings[k];
                    u1 = crossings[k + 1];
                }
            }
            return bestLen > 0f;
        }

        private static bool StallFits(IReadOnlyList<float2> ring, float2 d, float2 n, float2 center, Func<float2, bool> blocked)
        {
            var hu = d * (StallWidth * 0.5f);
            var hv = n * (StallLength * 0.5f);
            foreach (var corner in new[] { center - hu - hv, center + hu - hv, center + hu + hv, center - hu + hv })
            {
                if (!ParkingGeometry.Contains(ring, corner) || ParkingGeometry.DistanceToRing(ring, corner) < 0.2f)
                    return false;
                if (blocked != null && blocked(corner))
                    return false;
            }
            return true;
        }

        // A stall must not stand on the connector or the entrance (its own aisle is fine).
        private static bool TooCloseToOtherDrive(float2 center, float2 d, float2 n, List<(float2 A, float2 B)> drive, float aisleV, float u0, float u1, Func<float, float, float2> pt)
        {
            var hu = d * (StallWidth * 0.5f);
            var hv = n * (StallLength * 0.5f);
            var corners = new[] { center - hu - hv, center + hu - hv, center + hu + hv, center - hu + hv, center };
            foreach (var seg in drive)
            {
                // Skip the stall's own aisle (horizontal at aisleV).
                if (math.abs(math.dot(seg.A, n) - aisleV) < 0.01f && math.abs(math.dot(seg.B, n) - aisleV) < 0.01f)
                    continue;
                foreach (var c in corners)
                    if (ParkingGeometry.DistanceToSegment(c, seg.A, seg.B) < AisleHalfWidth)
                        return true;
            }
            return false;
        }

        private static void AddPolyline(List<(float2 A, float2 B)> segments, IReadOnlyList<float2> pts)
        {
            for (var i = 0; i + 1 < pts.Count; i++)
                if (math.distance(pts[i], pts[i + 1]) > 0.5f)
                    segments.Add((pts[i], pts[i + 1]));
        }

        private static void AddPieces(List<(float2 A, float2 B)> paths, float2 a, float2 b)
        {
            var pieces = (int)math.ceil(math.distance(a, b) / MaxPathPieceLength);
            var prev = a;
            for (var i = 1; i <= pieces; i++)
            {
                var next = i == pieces ? b : math.lerp(a, b, (float)i / pieces);
                paths.Add((prev, next));
                prev = next;
            }
        }
    }
}
