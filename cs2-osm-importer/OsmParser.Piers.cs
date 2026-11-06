using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    internal static partial class OsmParser
    {
        private const float PierShoreSnapMeters = 12f;
        private static readonly HashSet<string> PierJoinable = new HashSet<string>
        {
            "footway", "path", "pedestrian", "cycleway", "steps", "bridleway", "track", "service", "living_street", "residential", "unclassified",
        };
        // A pier joined to no path gets a footway this long at most from its point nearest a path (OSM often leaves
        // the last meters between a pier and the shore path unmapped, and an unreachable pier is useless).
        private const float PierConnectorMeters = 35f;
        // A free pier end this close to another pier's walkway joins it (finger docks on a main dock).
        private const float PierJoinMeters = 4f;
        // Joints closer than this on one walkway share a node: CS2 leaves a very short piece between two joints
        // unbuilt (two finger docks 1.2 m apart on a main dock stayed unconnected).
        private const float PierJointMergeMeters = 3f;
        // Two pier lines closer than this along most of one's length are one dock.
        private const float PierParallelLineMeters = 3.5f;
        private const float PierSimplifyMeters = 1f;
        private const float PierRectangleFill = 0.7f;
        private const float PierMinLengthMeters = 4f;
        private const double MetersPerDegreeLat = 110574.0;

        private static long _nextSyntheticPierNodeId = -1_000_000_000_000L;
        private static long _nextSyntheticPierWayId = -1_000_000_000_000L;

        /// <summary>
        /// man_made=pier as walkways over the water. A pier line without a highway tag becomes a footway; a pier area
        /// (rectangle or L) gets a footway down its middle; footways already mapped on a pier area are kept as they are.
        /// All of them carry man_made=pier, which makes TryReadRoadWay build them as a flat deck (OsmWay.IsPier; the
        /// deck height is set from the water level at generation). Synthetic ways are added to supportedWays; their
        /// ends snap to a path node within PierShoreSnapMeters so they connect to the shore.
        /// </summary>
        private static void AddPierWays(XmlDocument xml, XmlNodeList wayElements, Dictionary<long, GeoNode> nodes, List<XmlElement> supportedWays, HashSet<long> roadNodeIds, bool footwaysAllowed)
        {
            var pierElements = new List<XmlElement>();
            foreach (XmlElement w in wayElements)
            {
                if (ReadTags(w).TryGetValue("man_made", out var mm) && mm == "pier")
                    pierElements.Add(w);
            }
            if (pierElements.Count == 0)
                return;

            // Local metric frame around the piers, for geometry only (the real projection happens later).
            var anyNode = pierElements.SelectMany(ReadNodeRefs).Select(id => nodes.TryGetValue(id, out var n) ? n : null).FirstOrDefault(n => n != null);
            if (anyNode == null)
                return;
            var lat0 = anyNode.Lat;
            var lon0 = anyNode.Lon;
            var metersPerDegreeLon = Math.Cos(lat0 * Math.PI / 180.0) * 111320.0;
            float2 Local(GeoNode n) => new float2((float)((n.Lon - lon0) * metersPerDegreeLon), (float)((n.Lat - lat0) * MetersPerDegreeLat));

            // Supported highway ways with their local node positions (shore snapping, footways on pier areas).
            var highwayWays = new List<(XmlElement Element, List<(long Id, float2 P)> Nodes)>();
            foreach (var w in supportedWays)
            {
                // Piers join walkways and quiet streets, never a main road.
                if (!(ReadTags(w).TryGetValue("highway", out var hw) && PierJoinable.Contains(hw)))
                    continue;
                var list = new List<(long, float2)>();
                foreach (var id in ReadNodeRefs(w))
                {
                    if (nodes.TryGetValue(id, out var n))
                        list.Add((id, Local(n)));
                }
                highwayWays.Add((w, list));
            }
            var highwayIndex = new PolylineIndex(highwayWays.Select(h => h.Nodes).ToList(), 0.01f);

            int lines = 0, areas = 0, mappedOnArea = 0, skipped = 0, snapped = 0, connectors = 0, unreachable = 0, pierJoins = 0;
            // Every pier walkway (lines and areas), so a pier end can join another pier (finger docks are often mapped
            // as their own areas touching the main dock). targetPier[i] = the pier the walkway belongs to.
            var pierTargets = new List<(XmlElement Element, List<(long Id, float2 P)> Nodes)>();
            var targetPier = new List<int>();
            var builds = new List<PierBuild>();
            // How many ways use each node (roads and paths, and the pier lines), to tell a pier line's free ends. Pier
            // area outlines don't count: a finger line ending on the main dock's outline still has to join its walkway.
            var nodeUse = new Dictionary<long, int>();
            foreach (var w in supportedWays.Concat(pierElements.Where(p => !ReadTags(p).ContainsKey("highway"))))
            {
                var refs = ReadNodeRefs(w);
                if (refs.Count >= 4 && refs[0] == refs[refs.Count - 1] && !ReadTags(w).ContainsKey("highway"))
                    continue;
                foreach (var id in refs.Distinct())
                    nodeUse[id] = nodeUse.TryGetValue(id, out var c) ? c + 1 : 1;
            }

            // Pier lines (open man_made=pier ways without a highway tag): a pier area with one drawn on it is the same
            // dock mapped twice, and the line is its walkway.
            var pierLineNodes = new List<List<float2>>();
            var pierLineElements = new List<XmlElement>();
            foreach (var p in pierElements)
            {
                var refs = ReadNodeRefs(p);
                if (ReadTags(p).ContainsKey("highway") || refs.Count < 2 || (refs.Count >= 4 && refs[0] == refs[refs.Count - 1]))
                    continue;
                pierLineNodes.Add(refs.Where(nodes.ContainsKey).Select(id => Local(nodes[id])).ToList());
                pierLineElements.Add(p);
            }

            // A pier line running alongside another one, a few meters off, for most of its length is the same dock
            // mapped twice (or a boom beside it): two walkways that close are useless. The one with fewer connections
            // (nodes shared with other ways), then the shorter, is dropped.
            var droppedLines = new HashSet<XmlElement>();
            int Connections(int i) => ReadNodeRefs(pierLineElements[i]).Count(id => nodeUse.TryGetValue(id, out var u) && u > 1);
            float LineLength(List<float2> l)
            {
                var len = 0f;
                for (var k = 1; k < l.Count; k++)
                    len += math.distance(l[k - 1], l[k]);
                return len;
            }
            // Bounding boxes of the pier lines: lines whose boxes are farther apart than a test distance can't pass it.
            var pierLineBoxes = pierLineNodes.Select(BoundsOf).ToList();
            for (var i = 0; i < pierLineNodes.Count; i++)
            {
                // Short lines (finger stubs off a main dock) lie mostly near it without being a double.
                if (pierLineNodes[i].Count < 2 || droppedLines.Contains(pierLineElements[i]) || LineLength(pierLineNodes[i]) < 3f * PierParallelLineMeters)
                    continue;
                var samples = Resample(pierLineNodes[i], 1f);
                for (var k = 0; k < pierLineNodes.Count; k++)
                {
                    if (k == i || pierLineNodes[k].Count < 2 || droppedLines.Contains(pierLineElements[k]))
                        continue;
                    // No sample of i lies within PierParallelLineMeters of k: near would be 0.
                    if (!BoxesTouch(pierLineBoxes[i], pierLineBoxes[k], PierParallelLineMeters + 0.01f))
                        continue;
                    var near = samples.Count(p => DistanceToPolyline(pierLineNodes[k], p) < PierParallelLineMeters);
                    if (near < samples.Count * 0.7f)
                        continue;
                    // i runs alongside k: drop the weaker of the two.
                    var ci = Connections(i);
                    var ck = Connections(k);
                    var dropI = ci < ck || (ci == ck && LineLength(pierLineNodes[i]) <= LineLength(pierLineNodes[k]));
                    droppedLines.Add(pierLineElements[dropI ? i : k]);
                    if (dropI)
                        break;
                }
            }

            // Pass 1: walkways of every pier; free ends join a shore path within PierShoreSnapMeters.
            foreach (var pier in pierElements)
            {
                var tags = ReadTags(pier);
                var ids = ReadNodeRefs(pier);
                if (tags.ContainsKey("highway"))
                    continue; // a mapped footway/path that is a pier: read as itself, with the pier tag
                var closed = ids.Count >= 4 && ids[0] == ids[ids.Count - 1];
                if (!closed)
                {
                    if (!footwaysAllowed || ids.Count < 2 || droppedLines.Contains(pier))
                        continue;
                    var lineWay = CreatePierWay(xml, ids);
                    supportedWays.Add(lineWay);
                    foreach (var id in ids)
                    {
                        if (nodes.ContainsKey(id))
                            roadNodeIds.Add(id);
                    }
                    // Lines are kept as they are, and other piers may join them. An end no other way uses is a free end:
                    // marinas often map a finger dock ending on the main dock without sharing its node.
                    var lineBuild = new PierBuild { Joined = true, Kept = true };
                    builds.Add(lineBuild);
                    var lineNodes = ids.Where(nodes.ContainsKey).Select(id => (id, Local(nodes[id]))).ToList();
                    pierTargets.Add((lineWay, lineNodes));
                    targetPier.Add(builds.Count - 1);
                    if (lineNodes.Count == ids.Count)
                    {
                        foreach (var endIndex in new[] { 0, ids.Count - 1 })
                        {
                            if (nodeUse.TryGetValue(ids[endIndex], out var uses) && uses <= 1)
                                lineBuild.FreeEnds.Add((lineWay, pierTargets.Count - 1, endIndex, lineNodes[endIndex].Item2));
                        }
                    }
                    lines++;
                    continue;
                }

                var ring = ids.Take(ids.Count - 1).Select(id => nodes.TryGetValue(id, out var n) ? (float2?)Local(n) : null).Where(p => p.HasValue).Select(p => p.Value).ToList();
                if (ring.Count < 3)
                    continue;
                if (ParkingGeometry.SignedArea(ring) < 0f)
                    ring.Reverse();

                // Footways mapped on the pier: those are the walkway; mark them as pier decks. Only paths with a
                // segment box touching the outline's box can have nodes inside it.
                var ringBox = BoundsOf(ring);
                var onIt = highwayIndex.Query(ringBox.Min - 0.01f, ringBox.Max + 0.01f)
                    .Select(w => highwayWays[w])
                    .Where(h => h.Nodes.Count(n => ParkingGeometry.Contains(ring, n.P)) >= 2).ToList();
                // The same dock when at least half the line's length runs inside the outline (a long main dock
                // crossing a small finger area is not).
                var lineOnIt = pierLineNodes.Where((l, k) => BoxesTouch(pierLineBoxes[k], ringBox, 0.01f)).Any(l =>
                {
                    if (l.Count < 2)
                        return false;
                    var samples = Resample(l, 1f);
                    return samples.Count(p => ParkingGeometry.Contains(ring, p)) * 2 >= samples.Count;
                });
                if (onIt.Count > 0 || lineOnIt)
                {
                    foreach (var h in onIt)
                        MarkAsPier(xml, h.Element);
                    mappedOnArea++;
                    continue;
                }
                if (!footwaysAllowed)
                    continue;

                List<List<float2>> walkways;
                try
                {
                    walkways = PierWalkways(ring);
                }
                catch (Exception ex)
                {
                    // One odd outline must never stop the import: skip that pier.
                    Mod.log.Warn($"Pier {ReadLong(pier, "id")} skipped: walkway shape failed ({ex.Message}).");
                    walkways = null;
                }
                if (walkways == null || walkways.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var build = new PierBuild();
                builds.Add(build);
                var buildIndex = builds.Count - 1;
                // Polylines of one pier share their junction points: same point, same node.
                var pointIds = new Dictionary<(int, int), long>();
                var endCounts = new Dictionary<(int, int), int>();
                (int, int) Key(float2 p) => ((int)math.round(p.x * 10f), (int)math.round(p.y * 10f));
                foreach (var w in walkways)
                {
                    foreach (var end in new[] { w[0], w[w.Count - 1] })
                        endCounts[Key(end)] = endCounts.TryGetValue(Key(end), out var c) ? c + 1 : 1;
                }
                foreach (var line in walkways)
                {
                    var refs = new List<long>();
                    var freeEnds = new List<(int Index, float2 P)>();
                    for (var i = 0; i < line.Count; i++)
                    {
                        if (pointIds.TryGetValue(Key(line[i]), out var known))
                        {
                            refs.Add(known);
                            continue;
                        }
                        long id = 0;
                        var isFreeEnd = (i == 0 || i == line.Count - 1) && endCounts[Key(line[i])] == 1;
                        // A free end reaches for the shore: onto the nearest path, at a node of it or a new one in it.
                        if (isFreeEnd && TryNearestPathPoint(highwayWays, highwayIndex, line[i], PierShoreSnapMeters, out var hit, null))
                        {
                            id = AttachToPath(xml, highwayWays, hit, nodes, lat0, lon0, metersPerDegreeLon, roadNodeIds);
                            snapped++;
                            build.Joined = true;
                            isFreeEnd = false;
                        }
                        if (id == 0)
                        {
                            id = _nextSyntheticPierNodeId--;
                            nodes[id] = new GeoNode { Lat = lat0 + line[i].y / MetersPerDegreeLat, Lon = lon0 + line[i].x / metersPerDegreeLon };
                        }
                        if (isFreeEnd)
                            freeEnds.Add((refs.Count, line[i]));
                        pointIds[Key(line[i])] = id;
                        refs.Add(id);
                        roadNodeIds.Add(id);
                    }
                    var way = CreatePierWay(xml, refs);
                    build.Ways.Add(way);
                    var targetNodes = refs.Select((id, k) => (id, line[k])).ToList();
                    pierTargets.Add((way, targetNodes));
                    targetPier.Add(buildIndex);
                    foreach (var fe in freeEnds)
                        build.FreeEnds.Add((way, pierTargets.Count - 1, fe.Index, fe.P));
                }
            }

            // Pass 2: a free pier end within PierJoinMeters of another pier's walkway joins it there.
            var group = Enumerable.Range(0, builds.Count).ToArray();
            int Find(int i)
            {
                while (group[i] != i)
                    i = group[i] = group[group[i]];
                return i;
            }
            // Ends move up to PierJoinMeters onto a joint during this pass: the boxes allow for it.
            var targetIndex = new PolylineIndex(pierTargets.Select(t => t.Nodes).ToList(), PierJoinMeters + 0.01f);
            for (var b = 0; b < builds.Count; b++)
            {
                foreach (var end in builds[b].FreeEnds)
                {
                    // Walkways of the other piers (hit.Way indexes pierTargets).
                    var self = b;
                    var onPier = TryNearestPathPoint(pierTargets, targetIndex, end.P, PierJoinMeters, out var hit, t => targetPier[t] != self);
                    // A path or street just as close (a pier line ending on the shore path without sharing its node).
                    var onPath = TryNearestPathPoint(highwayWays, highwayIndex, end.P, onPier ? hit.Distance : PierJoinMeters, out var pathHit, null);
                    if (!onPier && !onPath)
                        continue;
                    var id = onPath
                        ? AttachToPath(xml, highwayWays, pathHit, nodes, lat0, lon0, metersPerDegreeLon, roadNodeIds)
                        : AttachToPath(xml, pierTargets, hit, nodes, lat0, lon0, metersPerDegreeLon, roadNodeIds);
                    var point = onPath ? pathHit.Point : hit.Point;
                    // Move this end onto the joint.
                    var nds = end.Way.SelectNodes("nd").Cast<XmlElement>().ToList();
                    nds[end.NdIndex].SetAttribute("ref", id.ToString());
                    pierTargets[end.Target].Nodes[end.NdIndex] = (id, point);
                    if (onPath)
                    {
                        builds[b].Joined = true;
                        snapped++;
                    }
                    else
                    {
                        group[Find(b)] = Find(targetPier[hit.Way]);
                        pierJoins++;
                    }
                }
            }

            // Pass 3: a group of joined piers with no way to the shore gets one short connector footway from its point
            // nearest a path; with none in reach (a swimming raft out in the lake) the group is left out.
            var groupJoined = new Dictionary<int, bool>();
            for (var b = 0; b < builds.Count; b++)
                groupJoined[Find(b)] = (groupJoined.TryGetValue(Find(b), out var j) && j) || builds[b].Joined;
            foreach (var root in groupJoined.Keys.ToList())
            {
                if (groupJoined[root])
                    continue;
                var bestDistance = PierConnectorMeters;
                long from = 0;
                PathHit best = default;
                for (var t = 0; t < pierTargets.Count; t++)
                {
                    if (Find(targetPier[t]) != root)
                        continue;
                    foreach (var p in pierTargets[t].Nodes)
                    {
                        if (TryNearestPathPoint(highwayWays, highwayIndex, p.P, bestDistance, out var hit, null))
                        {
                            bestDistance = hit.Distance;
                            best = hit;
                            from = p.Id;
                        }
                    }
                }
                if (from == 0)
                    continue;
                var to = AttachToPath(xml, highwayWays, best, nodes, lat0, lon0, metersPerDegreeLon, roadNodeIds);
                // Tagged as pier too: it runs at deck height where it is over the water or the lake's edge.
                supportedWays.Add(CreatePierWay(xml, new List<long> { from, to }));
                connectors++;
                groupJoined[root] = true;
            }
            for (var b = 0; b < builds.Count; b++)
            {
                if (builds[b].Kept)
                    continue; // pier lines, already added
                if (!groupJoined[Find(b)])
                {
                    unreachable++;
                    continue;
                }
                supportedWays.AddRange(builds[b].Ways);
                areas++;
            }

            Mod.log.Info($"Piers: {lines} pier lines and {areas} pier areas as walkways ({snapped} ends joined to a shore path, {pierJoins} to another pier, {connectors} groups joined by a short added footway), {mappedOnArea} pier areas with their own mapped footways; {droppedLines.Count} pier lines dropped as doubles of a pier beside them, {skipped} pier areas skipped (too small or no walkway found), {unreachable} with no path within reach (swimming rafts and the like).");
        }

        private static (float2 Min, float2 Max) BoundsOf(List<float2> points)
        {
            if (points.Count == 0)
                return (new float2(float.MaxValue), new float2(float.MinValue));
            var min = points[0];
            var max = points[0];
            foreach (var p in points)
            {
                min = math.min(min, p);
                max = math.max(max, p);
            }
            return (min, max);
        }

        private static bool BoxesTouch((float2 Min, float2 Max) a, (float2 Min, float2 Max) b, float margin)
        {
            return a.Min.x - margin <= b.Max.x && b.Min.x - margin <= a.Max.x &&
                   a.Min.y - margin <= b.Max.y && b.Min.y - margin <= a.Max.y;
        }

        private sealed class PierBuild
        {
            public readonly List<XmlElement> Ways = new List<XmlElement>();
            public readonly List<(XmlElement Way, int Target, int NdIndex, float2 P)> FreeEnds = new List<(XmlElement, int, int, float2)>();
            public bool Joined;
            public bool Kept;
        }

        private struct PathHit
        {
            public int Way;       // index in the path list
            public int Segment;   // between node Segment and Segment + 1
            public float2 Point;
            public float Distance;
            public long NodeId;   // an existing node near the point (within PierJointMergeMeters), else 0
        }

        /// <summary>
        /// Segment bounding boxes of a list of polylines in a coarse grid. Only a prefilter for searches over thousands
        /// of paths (city-sized imports spent most of the pier phase scanning every path for every pier end): callers
        /// run the exact test on the returned polylines in list order, so results are the same as a full scan.
        /// Boxes are inflated by <c>slack</c> to cover points that later move that far (a pier end moved onto a joint).
        /// Points inserted into a segment stay inside its box.
        /// </summary>
        private sealed class PolylineIndex
        {
            private const float CellSize = 64f;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly int[] _seen;
            private int _stamp;

            public PolylineIndex(IReadOnlyList<List<(long Id, float2 P)>> lines, float slack)
                : this(lines.Count, w => lines[w].Count, (w, i) => lines[w][i].P, slack)
            {
            }

            /// <summary>Polyline w has pointCount(w) points; point(w, i) is its i-th point.</summary>
            public PolylineIndex(int count, Func<int, int> pointCount, Func<int, int, float2> point, float slack)
            {
                _seen = new int[count];
                for (var w = 0; w < count; w++)
                {
                    var n = pointCount(w);
                    for (var s = 0; s < n; s++)
                    {
                        // Single points too, so a one-node line is still found.
                        if (s + 1 >= n && n > 1)
                            break;
                        var a = point(w, s);
                        var b = s + 1 < n ? point(w, s + 1) : a;
                        Add(w, math.min(a, b) - slack, math.max(a, b) + slack);
                    }
                }
            }

            private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

            private void Add(int w, float2 min, float2 max)
            {
                var x0 = (int)math.floor(min.x / CellSize);
                var y0 = (int)math.floor(min.y / CellSize);
                var x1 = (int)math.floor(max.x / CellSize);
                var y1 = (int)math.floor(max.y / CellSize);
                for (var x = x0; x <= x1; x++)
                    for (var y = y0; y <= y1; y++)
                    {
                        var key = Key(x, y);
                        if (!_cells.TryGetValue(key, out var list))
                            _cells[key] = list = new List<int>();
                        if (list.Count == 0 || list[list.Count - 1] != w)
                            list.Add(w);
                    }
            }

            /// <summary>Indices (ascending) of polylines with a segment box touching [min, max].</summary>
            public List<int> Query(float2 min, float2 max)
            {
                _stamp++;
                var result = new List<int>();
                var x0 = (int)math.floor(min.x / CellSize);
                var y0 = (int)math.floor(min.y / CellSize);
                var x1 = (int)math.floor(max.x / CellSize);
                var y1 = (int)math.floor(max.y / CellSize);
                for (var x = x0; x <= x1; x++)
                    for (var y = y0; y <= y1; y++)
                    {
                        if (!_cells.TryGetValue(Key(x, y), out var list))
                            continue;
                        foreach (var w in list)
                        {
                            if (_seen[w] == _stamp)
                                continue;
                            _seen[w] = _stamp;
                            result.Add(w);
                        }
                    }
                result.Sort();
                return result;
            }
        }

        /// <summary>The nearest point on any path within maxDistance of p.</summary>
        private static bool TryNearestPathPoint(List<(XmlElement Element, List<(long Id, float2 P)> Nodes)> paths, float2 p, float maxDistance, out PathHit hit)
        {
            return TryNearestPathPoint(paths, null, p, maxDistance, out hit, null);
        }

        /// <summary>
        /// The nearest point on any path within maxDistance of p, among the paths the index returns near p (all paths
        /// without an index) and that <c>include</c> accepts. Same scan order and tie-breaking as a full scan.
        /// </summary>
        private static bool TryNearestPathPoint(List<(XmlElement Element, List<(long Id, float2 P)> Nodes)> paths, PolylineIndex index, float2 p, float maxDistance, out PathHit hit, Func<int, bool> include)
        {
            hit = default;
            var found = false;
            var candidates = index?.Query(p - maxDistance, p + maxDistance);
            var count = candidates?.Count ?? paths.Count;
            for (var c = 0; c < count; c++)
            {
                var w = candidates != null ? candidates[c] : c;
                if (include != null && !include(w))
                    continue;
                var pts = paths[w].Nodes;
                for (var s = 0; s + 1 < pts.Count; s++)
                {
                    var q = ParkingGeometry.ClosestOnSegment(p, pts[s].P, pts[s + 1].P);
                    var d = math.distance(p, q);
                    if (d >= maxDistance)
                        continue;
                    maxDistance = d;
                    found = true;
                    long node = 0;
                    var at = q;
                    if (math.distance(q, pts[s].P) < PierJointMergeMeters && math.distance(q, pts[s].P) <= math.distance(q, pts[s + 1].P))
                    {
                        node = pts[s].Id;
                        at = pts[s].P;
                    }
                    else if (math.distance(q, pts[s + 1].P) < PierJointMergeMeters)
                    {
                        node = pts[s + 1].Id;
                        at = pts[s + 1].P;
                    }
                    hit = new PathHit { Way = w, Segment = s, Point = at, Distance = d, NodeId = node };
                }
            }
            return found;
        }

        /// <summary>The node to join at a path point: the existing one, or a new node inserted into the path way.</summary>
        private static long AttachToPath(XmlDocument xml, List<(XmlElement Element, List<(long Id, float2 P)> Nodes)> paths, PathHit hit, Dictionary<long, GeoNode> nodes, double lat0, double lon0, double metersPerDegreeLon, HashSet<long> roadNodeIds)
        {
            if (hit.NodeId != 0)
                return hit.NodeId;
            var id = _nextSyntheticPierNodeId--;
            nodes[id] = new GeoNode { Lat = lat0 + hit.Point.y / MetersPerDegreeLat, Lon = lon0 + hit.Point.x / metersPerDegreeLon };
            roadNodeIds.Add(id);
            var (element, list) = paths[hit.Way];
            var after = list[hit.Segment].Id;
            var before = list[hit.Segment + 1].Id;
            // Insert between the two nodes of that segment (the nd pair in the way, matched by ref).
            var nds = element.SelectNodes("nd").Cast<XmlElement>().ToList();
            for (var i = 0; i + 1 < nds.Count; i++)
            {
                if (ReadLong(nds[i], "ref") == after && ReadLong(nds[i + 1], "ref") == before)
                {
                    var nd = xml.CreateElement("nd");
                    nd.SetAttribute("ref", id.ToString());
                    element.InsertAfter(nd, nds[i]);
                    break;
                }
            }
            list.Insert(hit.Segment + 1, (id, hit.Point));
            return id;
        }

        private static XmlElement CreatePierWay(XmlDocument xml, List<long> refs)
        {
            var way = xml.CreateElement("way");
            way.SetAttribute("id", (_nextSyntheticPierWayId--).ToString());
            foreach (var id in refs)
            {
                var nd = xml.CreateElement("nd");
                nd.SetAttribute("ref", id.ToString());
                way.AppendChild(nd);
            }
            AddTag(xml, way, "highway", "footway");
            AddTag(xml, way, "man_made", "pier");
            return way;
        }

        private static void MarkAsPier(XmlDocument xml, XmlElement way)
        {
            if (!(ReadTags(way).TryGetValue("man_made", out var mm) && mm == "pier"))
                AddTag(xml, way, "man_made", "pier");
        }

        private static void AddTag(XmlDocument xml, XmlElement way, string key, string value)
        {
            var tag = xml.CreateElement("tag");
            tag.SetAttribute("k", key);
            tag.SetAttribute("v", value);
            way.AppendChild(tag);
        }

        /// <summary>
        /// The walkways of a pier area: the long axis for a rectangle-like pier, otherwise its skeleton (L, T, U and
        /// finger-dock shapes). Null when none is found.
        /// </summary>
        private static List<List<float2>> PierWalkways(List<float2> ring)
        {
            var simple = SimplifyRing(ring, PierSimplifyMeters);
            if (simple.Count < 3)
                return null;
            var line = RectangleAxis(simple);
            if (line != null)
                return new List<List<float2>> { line };
            return PierSkeleton(ring);
        }

        private static List<float2> RectangleAxis(List<float2> ring)
        {
            var r = ParkingGeometry.MinAreaRectangle(ring);
            var rectArea = r.SizeU * r.SizeV;
            if (rectArea <= 0f || ParkingGeometry.SignedArea(ring) / rectArea < PierRectangleFill)
                return null;
            var (axis, length, width) = r.SizeU >= r.SizeV ? (r.AxisU, r.SizeU, r.SizeV) : (r.AxisV, r.SizeV, r.SizeU);
            if (length < PierMinLengthMeters)
                return null;
            // End half a deck width short of each end, so the path stays on the pier.
            var half = math.max(length * 0.5f - math.min(width * 0.5f, 1.5f), length * 0.25f);
            return new List<float2> { r.Center - axis * half, r.Center + axis * half };
        }

        /// <summary>Douglas-Peucker on a closed ring (split at the two farthest-apart vertices).</summary>
        private static List<float2> SimplifyRing(List<float2> ring, float tolerance)
        {
            if (ring.Count <= 4)
                return new List<float2>(ring);
            int a = 0, b = 0;
            var far = -1f;
            for (var i = 0; i < ring.Count; i++)
            {
                var d = math.distance(ring[0], ring[i]);
                if (d > far)
                {
                    far = d;
                    b = i;
                }
            }
            var first = ring.Skip(a).Take(b - a + 1).ToList();
            var second = ring.Skip(b).Concat(new[] { ring[0] }).ToList();
            var s1 = SimplifyLine(first, tolerance);
            var s2 = SimplifyLine(second, tolerance);
            var result = new List<float2>(s1);
            result.AddRange(s2.Skip(1).Take(s2.Count - 2));
            return result;
        }

        private static List<float2> SimplifyLine(List<float2> pts, float tolerance)
        {
            if (pts.Count <= 2)
                return new List<float2>(pts);
            var maxD = 0f;
            var index = 0;
            for (var i = 1; i < pts.Count - 1; i++)
            {
                var d = ParkingGeometry.DistanceToSegment(pts[i], pts[0], pts[pts.Count - 1]);
                if (d > maxD)
                {
                    maxD = d;
                    index = i;
                }
            }
            if (maxD <= tolerance)
                return new List<float2> { pts[0], pts[pts.Count - 1] };
            var left = SimplifyLine(pts.Take(index + 1).ToList(), tolerance);
            var right = SimplifyLine(pts.Skip(index).ToList(), tolerance);
            left.AddRange(right.Skip(1));
            return left;
        }
    }
}
