using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    /// <summary>
    /// Two junctions mapped a metre or two apart (a side street meeting each carriageway of a road a step apart, a
    /// road split where a footway joins next to a junction) leave a road piece shorter than CS2's junctions are wide.
    /// The game then draws two overlapping junctions joined by a sliver of road (0.2 m on SR 99). Such a piece is
    /// collapsed: both its ends become one junction at their middle, and every way using either end meets there.
    /// </summary>
    internal static partial class OsmParser
    {
        // Road pieces shorter than this between two junctions are collapsed...
        private const float TinyPieceMeters = 3f;
        // ... when their ends are at about the same height, and no merged junction spreads further than this.
        private const float TinyPieceMaxHeightMeters = 1f;
        private const float TinyPieceMaxSpreadMeters = 4f;

        private static List<OsmWay> CollapseTinyRoadPieces(List<OsmWay> ways, Dictionary<long, float2> sourcePositions)
        {
            var use = new Dictionary<(int, int), int>();
            foreach (var w in ways)
                foreach (var k in w.Points.Select(p => PointKey(p.xz)).Distinct())
                    use[k] = use.TryGetValue(k, out var c) ? c + 1 : 1;
            bool Junction((int, int) k) => use.TryGetValue(k, out var c) && c > 1;

            var parent = new Dictionary<(int, int), (int, int)>();
            (int, int) Find((int, int) k)
            {
                while (parent.TryGetValue(k, out var p) && !p.Equals(k))
                    k = p;
                return k;
            }
            var position = new Dictionary<(int, int), float3>();
            var pieces = 0;
            foreach (var w in ways)
            {
                if (!IsVehicleRoad(w) || w.IsRoundabout)
                    continue;
                for (var i = 1; i < w.Points.Count; i++)
                {
                    var a = w.Points[i - 1];
                    var b = w.Points[i];
                    if (math.distance(a.xz, b.xz) >= TinyPieceMeters || math.abs(a.y - b.y) > TinyPieceMaxHeightMeters)
                        continue;
                    var ka = PointKey(a.xz);
                    var kb = PointKey(b.xz);
                    if (ka.Equals(kb) || !Junction(ka) || !Junction(kb))
                        continue;
                    foreach (var (k, p) in new[] { (ka, a), (kb, b) })
                        if (!parent.ContainsKey(k)) { parent[k] = k; position[k] = p; }
                    var ra = Find(ka);
                    var rb = Find(kb);
                    if (!ra.Equals(rb))
                        parent[rb] = ra;
                    pieces++;
                }
            }
            if (pieces == 0)
                return ways;

            // Each group of joined ends becomes one point at their middle, unless the group spreads too far.
            var target = new Dictionary<(int, int), float3>();
            foreach (var g in parent.Keys.GroupBy(Find))
            {
                var members = g.ToList();
                var mid = members.Aggregate(float3.zero, (s, k) => s + position[k]) / members.Count;
                if (members.Any(k => math.distance(position[k].xz, mid.xz) > TinyPieceMaxSpreadMeters))
                    continue;
                foreach (var k in members)
                    target[k] = mid;
            }
            if (target.Count == 0)
                return ways;

            float3 Moved(float3 p) => target.TryGetValue(PointKey(p.xz), out var t) ? t : p;
            var kept = new List<OsmWay>(ways.Count);
            var removedWays = 0;
            foreach (var w in ways)
            {
                var pts = w.Points;
                var heights = w.TerrainHeights.Count == pts.Count ? w.TerrainHeights : null;
                var touched = false;
                for (var i = 0; i < pts.Count; i++)
                {
                    var m = Moved(pts[i]);
                    if (m.Equals(pts[i]))
                        continue;
                    pts[i] = m;
                    if (heights != null)
                        heights[i] = m.y;
                    touched = true;
                }
                if (touched)
                    for (var i = pts.Count - 1; i > 0; i--)
                        if (pts[i].Equals(pts[i - 1]))
                        {
                            pts.RemoveAt(i);
                            heights?.RemoveAt(i);
                        }
                foreach (var list in new[] { w.Crosswalks, w.JunctionCrossings, w.StopSigns, w.TrafficLights })
                    foreach (var f in list)
                        f.Position = Moved(f.Position);
                foreach (var r in w.TurnRestrictions)
                    r.ViaPosition = Moved(r.ViaPosition);
                if (pts.Count < 2)
                {
                    removedWays++;
                    continue;
                }
                kept.Add(w);
            }
            foreach (var id in sourcePositions.Keys.ToList())
                if (target.TryGetValue(PointKey(sourcePositions[id]), out var t))
                    sourcePositions[id] = t.xz;
            Mod.log.Info($"Tiny road pieces: {pieces} road pieces under {TinyPieceMeters:F0} m between two junctions collapsed " +
                         $"({target.Count} junction points merged into {target.Values.Distinct().Count()}; {removedWays} ways left with no length dropped).");
            return kept;
        }
    }
}
