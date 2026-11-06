using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    // Developer-only geometry report: compares three layers of the import pipeline so that
    // small positional errors become measurable numbers instead of something to eyeball.
    //
    //   OSM reference  - raw projected OSM node positions (before interpolation/simplification)
    //   Queued         - the straight NetCourse segments the importer handed to the game
    //   Actual         - the edges/nodes the game created from those definitions
    //
    // Large actual-vs-queued error  => the game moved geometry (snapping, merging, splitting).
    // Large actual-vs-OSM error with small actual-vs-queued error => the importer's own pipeline
    // (projection, simplification, way merging, roundabout synthesis) produced the offset.
    public partial class OsmImportSystem
    {
        // Set by the settings button; consumed on the system's update loop.
        internal static bool GeometryReportRequested;

        private const float ReportSampleStepMeters = 2f;
        private const int ReportMaxSamplesPerEdge = 64;
        private const int ReportMaxSamplesPerWay = 2000;
        private const float ReportSearchRadiusMeters = 50f;
        private const float ReportCoverageToleranceMeters = 2f;
        private const float ReportIndexCellMeters = 16f;
        private const int ReportTopCount = 25;
        private static readonly float[] ReportHistogramBounds = { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f };


        internal string LastGeometryReportPath { get; private set; } = string.Empty;

        internal static string GeometryReportDirectory =>
            Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", Mod.Id, "geometry-reports");

        /// <summary>
        /// Writes the report when requested (settings button, or the agent bridge's geometry_report command). It no
        /// longer runs by itself after every generation in Developer Mode: it runs in a single frame and froze the game
        /// for ~3 minutes after a full-Seattle import, blocking everything queued behind it.
        /// </summary>
        private void UpdateGeometryReportRequests()
        {
            if (IsImporting || !GeometryReportRequested)
                return;

            GeometryReportRequested = false;
            WriteGeometryReport("manual");
        }

        private sealed class EdgeRecord
        {
            public Entity Entity;
            public string Prefab;
            public float Length;
            public int WayIndex = -1;
            public float3 Mid;
            public float MaxVsOsm = -1f;
            public float MeanVsOsm = -1f;
            public float MaxVsQueued = -1f;
            public float MeanVsQueued = -1f;
            public float MaxAbsDyVsQueued = -1f;
            public float StartNodeOffset = -1f;
            public float EndNodeOffset = -1f;
            public bool Unrelated;
        }

        private sealed class WayRecord
        {
            public OsmWay Way;
            public int EdgeCount;
            public float MaxVsOsm;
            public float MaxVsQueued;
            public float Coverage = 1f;
            public float LongestGapMeters;
            public float3 LongestGapStart;
            public bool UsedRawOsmReference;
        }

        private sealed class JunctionRecord
        {
            public long NodeId;
            public float2 Position;
            public int WayCount;
            public float Offset = -1f;
            public string WayNames;
        }

        private void WriteGeometryReport(string trigger)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (_document == null || _queuedSegmentInfos.Count == 0)
                {
                    const string nothing = "Geometry report skipped: no roads were generated in this game session.";
                    Mod.log.Info(nothing);
                    SetStatus(nothing);
                    return;
                }

                // ---- Ways + queued segments ----
                var ways = new List<WayRecord>();
                var wayLookup = new Dictionary<OsmWay, int>();
                var queued = new SegmentIndex(ReportIndexCellMeters);
                var queuedEndpoints = new SegmentIndex(ReportIndexCellMeters);
                for (var i = 0; i < _queuedSegmentInfos.Count; i++)
                {
                    var info = _queuedSegmentInfos[i];
                    if (info.Way == null)
                        continue;

                    if (!wayLookup.ContainsKey(info.Way))
                    {
                        wayLookup[info.Way] = ways.Count;
                        ways.Add(new WayRecord { Way = info.Way });
                    }

                    queued.Add(info.StartPos, info.EndPos, i);
                    queuedEndpoints.Add(info.StartPos, info.StartPos, i);
                    queuedEndpoints.Add(info.EndPos, info.EndPos, i);
                }

                // ---- OSM reference polylines ----
                var reference = new SegmentIndex(ReportIndexCellMeters);
                var referencePolylines = new List<List<float3>>(ways.Count);
                for (var w = 0; w < ways.Count; w++)
                {
                    var polyline = BuildReferencePolyline(ways[w].Way, out var usedRaw);
                    ways[w].UsedRawOsmReference = usedRaw;
                    referencePolylines.Add(polyline);
                    for (var p = 1; p < polyline.Count; p++)
                        reference.Add(polyline[p - 1], polyline[p], w);
                }

                // ---- Actual edges created since generation started ----
                var edges = new List<EdgeRecord>();
                var actual = new SegmentIndex(ReportIndexCellMeters);
                var gameNodes = new SegmentIndex(ReportIndexCellMeters);
                var seenNodes = new HashSet<Entity>();
                var samples = new List<float3>(ReportMaxSamplesPerEdge + 1);

                using (var edgeArray = GetCurrentNetEdgeArray(Allocator.Temp))
                {
                    foreach (var entity in edgeArray)
                    {
                        if (_baselineSegments.Contains(entity) || !EntityManager.HasComponent<Game.Net.Curve>(entity))
                            continue;

                        var curve = EntityManager.GetComponentData<Game.Net.Curve>(entity);
                        var record = new EdgeRecord
                        {
                            Entity = entity,
                            Length = curve.m_Length,
                            Prefab = EntityManager.HasComponent<PrefabRef>(entity)
                                ? GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab)
                                : string.Empty,
                            Mid = EvaluateBezier(curve.m_Bezier, 0.5f)
                        };

                        SampleBezier(curve.m_Bezier, curve.m_Length, samples);
                        MeasureEdgeSamples(record, samples, queued, reference);

                        if (queued.Nearest(record.Mid.xz, ReportSearchRadiusMeters, out _, out var midSegment, out _))
                        {
                            var way = _queuedSegmentInfos[queued.Owner[midSegment]].Way;
                            if (way != null && wayLookup.TryGetValue(way, out var wayIndex))
                                record.WayIndex = wayIndex;
                        }

                        if (EntityManager.HasComponent<Edge>(entity))
                        {
                            var edge = EntityManager.GetComponentData<Edge>(entity);
                            record.StartNodeOffset = NodeOffsetToQueuedEndpoint(edge.m_Start, queuedEndpoints, gameNodes, seenNodes);
                            record.EndNodeOffset = NodeOffsetToQueuedEndpoint(edge.m_End, queuedEndpoints, gameNodes, seenNodes);
                        }

                        var edgeIndex = edges.Count;
                        edges.Add(record);
                        if (!record.Unrelated)
                        {
                            for (var s = 1; s < samples.Count; s++)
                                actual.Add(samples[s - 1], samples[s], edgeIndex);
                        }
                    }
                }

                // ---- Per-way aggregation + coverage of the OSM reference by actual edges ----
                foreach (var edge in edges)
                {
                    if (edge.Unrelated || edge.WayIndex < 0)
                        continue;

                    var wayRecord = ways[edge.WayIndex];
                    wayRecord.EdgeCount++;
                    wayRecord.MaxVsOsm = math.max(wayRecord.MaxVsOsm, edge.MaxVsOsm);
                    wayRecord.MaxVsQueued = math.max(wayRecord.MaxVsQueued, edge.MaxVsQueued);
                }

                for (var w = 0; w < ways.Count; w++)
                    MeasureWayCoverage(ways[w], referencePolylines[w], actual);

                // ---- Junctions: OSM nodes shared by 2+ generated ways vs the nearest game node ----
                var junctions = BuildJunctionRecords(ways, gameNodes);

                stopwatch.Stop();
                var paths = WriteGeometryReportFiles(trigger, ways, edges, junctions, gameNodes.Count, stopwatch.ElapsedMilliseconds);
                LastGeometryReportPath = paths;
                Mod.log.Info($"Geometry report written in {stopwatch.ElapsedMilliseconds} ms: {paths}");
                SetStatus($"Geometry report written: {paths}");
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                SetStatus($"Geometry report failed: {ex.Message}");
            }
        }

        private List<float3> BuildReferencePolyline(OsmWay way, out bool usedRawOsm)
        {
            var result = new List<float3>();
            var sourcePositions = _document?.SourceNodePositions;
            if (sourcePositions != null && sourcePositions.Count > 0)
            {
                foreach (var nodeId in way.NodeIds)
                {
                    if (!sourcePositions.TryGetValue(nodeId, out var position))
                        continue;

                    var point = new float3(position.x, 0f, position.y);
                    if (result.Count == 0 || math.distancesq(result[result.Count - 1].xz, point.xz) > 1e-6f)
                        result.Add(point);
                }
            }

            usedRawOsm = result.Count >= 2;
            if (usedRawOsm)
                return result;

            // Fall back to the processed points (already interpolated/simplified).
            result.Clear();
            result.AddRange(way.Points);
            return result;
        }

        private void MeasureEdgeSamples(EdgeRecord record, List<float3> samples, SegmentIndex queued, SegmentIndex reference)
        {
            var queuedHits = 0;
            var sumQueued = 0f;
            var maxQueued = 0f;
            var maxDy = 0f;
            var referenceHits = 0;
            var sumReference = 0f;
            var maxReference = 0f;

            foreach (var sample in samples)
            {
                if (queued.Nearest(sample.xz, ReportSearchRadiusMeters, out var dq, out var segment, out var t))
                {
                    queuedHits++;
                    sumQueued += dq;
                    maxQueued = math.max(maxQueued, dq);
                    var queuedY = math.lerp(queued.A[segment].y, queued.B[segment].y, t);
                    maxDy = math.max(maxDy, math.abs(sample.y - queuedY));
                }

                if (reference.Nearest(sample.xz, ReportSearchRadiusMeters, out var dr, out _, out _))
                {
                    referenceHits++;
                    sumReference += dr;
                    maxReference = math.max(maxReference, dr);
                }
            }

            // Edges mostly far from anything we queued are pre-existing roads the game split,
            // or roads built by hand after the import; keep them out of the statistics.
            record.Unrelated = queuedHits * 2 < samples.Count;
            if (queuedHits > 0)
            {
                record.MaxVsQueued = maxQueued;
                record.MeanVsQueued = sumQueued / queuedHits;
                record.MaxAbsDyVsQueued = maxDy;
            }

            if (referenceHits > 0)
            {
                record.MaxVsOsm = maxReference;
                record.MeanVsOsm = sumReference / referenceHits;
            }
        }

        private float NodeOffsetToQueuedEndpoint(Entity node, SegmentIndex queuedEndpoints, SegmentIndex gameNodes, HashSet<Entity> seenNodes)
        {
            if (node == Entity.Null || !EntityManager.HasComponent<Node>(node))
                return -1f;

            var position = EntityManager.GetComponentData<Node>(node).m_Position;
            if (seenNodes.Add(node))
                gameNodes.Add(position, position, node.Index);

            return queuedEndpoints.Nearest(position.xz, ReportSearchRadiusMeters, out var distance, out _, out _)
                ? distance
                : -1f;
        }

        private static void MeasureWayCoverage(WayRecord record, List<float3> polyline, SegmentIndex actual)
        {
            var length = 0f;
            for (var i = 1; i < polyline.Count; i++)
                length += math.distance(polyline[i - 1].xz, polyline[i].xz);

            if (length <= 0f)
                return;

            var step = math.max(ReportSampleStepMeters, length / ReportMaxSamplesPerWay);
            var total = 0;
            var covered = 0;
            var gap = 0f;
            var gapStart = float3.zero;

            for (var i = 1; i < polyline.Count; i++)
            {
                var a = polyline[i - 1];
                var b = polyline[i];
                var segmentLength = math.distance(a.xz, b.xz);
                var count = math.max(1, (int)math.ceil(segmentLength / step));
                var sampleSpacing = segmentLength / count;
                for (var k = (i == 1 ? 0 : 1); k <= count; k++)
                {
                    var point = math.lerp(a, b, (float)k / count);
                    total++;
                    var hit = actual.Nearest(point.xz, ReportSearchRadiusMeters, out var distance, out _, out _) &&
                              distance <= ReportCoverageToleranceMeters;
                    if (hit)
                    {
                        covered++;
                        gap = 0f;
                        continue;
                    }

                    if (gap == 0f)
                        gapStart = point;

                    // Approximate gap length: one sample spacing per consecutive uncovered sample.
                    gap += sampleSpacing;
                    if (gap > record.LongestGapMeters)
                    {
                        record.LongestGapMeters = gap;
                        record.LongestGapStart = gapStart;
                    }
                }
            }

            record.Coverage = total > 0 ? (float)covered / total : 1f;
        }

        private List<JunctionRecord> BuildJunctionRecords(List<WayRecord> ways, SegmentIndex gameNodes)
        {
            var result = new List<JunctionRecord>();
            var sourcePositions = _document?.SourceNodePositions;
            if (sourcePositions == null || sourcePositions.Count == 0)
                return result;

            var occurrences = new Dictionary<long, List<OsmWay>>();
            foreach (var record in ways)
            {
                foreach (var nodeId in record.Way.NodeIds.Distinct())
                {
                    if (!occurrences.TryGetValue(nodeId, out var list))
                    {
                        list = new List<OsmWay>();
                        occurrences[nodeId] = list;
                    }

                    list.Add(record.Way);
                }
            }

            foreach (var pair in occurrences)
            {
                if (pair.Value.Count < 2 || !sourcePositions.TryGetValue(pair.Key, out var position))
                    continue;

                var junction = new JunctionRecord
                {
                    NodeId = pair.Key,
                    Position = position,
                    WayCount = pair.Value.Count,
                    WayNames = string.Join(" / ", pair.Value
                        .Select(way => string.IsNullOrWhiteSpace(way.Name) ? $"{way.Highway}#{way.Id}" : way.Name)
                        .Distinct()
                        .Take(4))
                };

                if (gameNodes.Nearest(position, ReportSearchRadiusMeters, out var distance, out _, out _))
                    junction.Offset = distance;

                result.Add(junction);
            }

            return result;
        }

        // ---------------------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------------------

        private string WriteGeometryReportFiles(
            string trigger,
            List<WayRecord> ways,
            List<EdgeRecord> edges,
            List<JunctionRecord> junctions,
            int gameNodeCount,
            long elapsedMs)
        {
            var directory = GeometryReportDirectory;
            Directory.CreateDirectory(directory);

            var related = edges.Where(e => !e.Unrelated).ToList();
            var vsOsm = related.Where(e => e.MaxVsOsm >= 0f).Select(e => e.MaxVsOsm).ToList();
            var vsQueued = related.Where(e => e.MaxVsQueued >= 0f).Select(e => e.MaxVsQueued).ToList();
            var dy = related.Where(e => e.MaxAbsDyVsQueued >= 0f).Select(e => e.MaxAbsDyVsQueued).ToList();
            var nodeOffsets = related
                .SelectMany(e => new[] { e.StartNodeOffset, e.EndNodeOffset })
                .Where(v => v >= 0f)
                .ToList();
            var junctionOffsets = junctions.Where(j => j.Offset >= 0f).Select(j => j.Offset).ToList();
            var waysWithGaps = ways.Where(w => w.LongestGapMeters > 0f).ToList();

            var json = new StringBuilder(64 * 1024);
            json.Append("{\n");
            json.Append("  \"trigger\": ").Append(Str(trigger)).Append(",\n");
            json.Append("  \"createdUtc\": ").Append(Str(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))).Append(",\n");
            json.Append("  \"elapsedMs\": ").Append(elapsedMs).Append(",\n");
            json.Append("  \"osmFile\": ").Append(Str(_activeOptions?.Path ?? string.Empty)).Append(",\n");
            json.Append("  \"settings\": {")
                .Append("\"simplifyToleranceMeters\": ").Append(Num(_activeOptions?.SimplifyToleranceMeters ?? -1f))
                .Append(", \"curveToleranceMeters\": ").Append(Num(_activeOptions?.CurveToleranceMeters ?? -1f))
                .Append(", \"scaleFactor\": ").Append(Num(_activeOptions?.ScaleFactor ?? -1f))
                .Append(", \"coverageToleranceMeters\": ").Append(Num(ReportCoverageToleranceMeters))
                .Append(", \"searchRadiusMeters\": ").Append(Num(ReportSearchRadiusMeters))
                .Append("},\n");
            json.Append("  \"counts\": {")
                .Append("\"ways\": ").Append(ways.Count)
                .Append(", \"waysUsingRawOsmReference\": ").Append(ways.Count(w => w.UsedRawOsmReference))
                .Append(", \"queuedSegments\": ").Append(_queuedSegmentInfos.Count)
                .Append(", \"newEdges\": ").Append(edges.Count)
                .Append(", \"analyzedEdges\": ").Append(related.Count)
                .Append(", \"unrelatedEdges\": ").Append(edges.Count - related.Count)
                .Append(", \"gameNodes\": ").Append(gameNodeCount)
                .Append(", \"junctions\": ").Append(junctions.Count)
                .Append(", \"junctionsWithoutGameNode\": ").Append(junctions.Count(j => j.Offset < 0f))
                .Append(", \"waysWithCoverageGaps\": ").Append(waysWithGaps.Count)
                .Append("},\n");
            json.Append("  \"edgeMaxDeviationVsOsmMeters\": ").Append(Distribution(vsOsm)).Append(",\n");
            json.Append("  \"edgeMaxDeviationVsQueuedMeters\": ").Append(Distribution(vsQueued)).Append(",\n");
            json.Append("  \"edgeMaxVerticalDeviationVsQueuedMeters\": ").Append(Distribution(dy)).Append(",\n");
            json.Append("  \"edgeNodeOffsetVsQueuedEndpointMeters\": ").Append(Distribution(nodeOffsets)).Append(",\n");
            json.Append("  \"junctionOffsetVsNearestGameNodeMeters\": ").Append(Distribution(junctionOffsets)).Append(",\n");

            json.Append("  \"worstEdgesVsQueued\": ");
            AppendEdges(json, related.Where(e => e.MaxVsQueued >= 0f).OrderByDescending(e => e.MaxVsQueued), ways);
            json.Append(",\n  \"worstEdgesVsOsm\": ");
            AppendEdges(json, related.Where(e => e.MaxVsOsm >= 0f).OrderByDescending(e => e.MaxVsOsm), ways);
            json.Append(",\n  \"worstEdgesVertical\": ");
            AppendEdges(json, related.Where(e => e.MaxAbsDyVsQueued >= 0f).OrderByDescending(e => e.MaxAbsDyVsQueued), ways);

            json.Append(",\n  \"worstJunctions\": [");
            var first = true;
            foreach (var junction in junctions.OrderByDescending(j => j.Offset < 0f ? float.MaxValue : j.Offset).Take(ReportTopCount))
            {
                json.Append(first ? "\n    " : ",\n    ");
                first = false;
                json.Append("{\"osmNodeId\": ").Append(junction.NodeId)
                    .Append(", \"x\": ").Append(Num(junction.Position.x))
                    .Append(", \"z\": ").Append(Num(junction.Position.y))
                    .Append(", \"ways\": ").Append(junction.WayCount)
                    .Append(", \"offsetMeters\": ").Append(junction.Offset < 0f ? "null" : Num(junction.Offset))
                    .Append(", \"names\": ").Append(Str(junction.WayNames))
                    .Append('}');
            }
            json.Append(first ? "]" : "\n  ]");

            json.Append(",\n  \"worstWayCoverageGaps\": [");
            first = true;
            foreach (var way in waysWithGaps.OrderByDescending(w => w.LongestGapMeters).Take(ReportTopCount))
            {
                json.Append(first ? "\n    " : ",\n    ");
                first = false;
                json.Append("{").Append(WayFields(way.Way))
                    .Append(", \"edges\": ").Append(way.EdgeCount)
                    .Append(", \"coverage\": ").Append(Num(way.Coverage))
                    .Append(", \"longestGapMeters\": ").Append(Num(way.LongestGapMeters))
                    .Append(", \"gapStartX\": ").Append(Num(way.LongestGapStart.x))
                    .Append(", \"gapStartZ\": ").Append(Num(way.LongestGapStart.z))
                    .Append('}');
            }
            json.Append(first ? "]" : "\n  ]");
            json.Append("\n}\n");

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var jsonText = json.ToString();
            var summaryText = BuildSummaryText(trigger, ways.Count, edges.Count, related.Count, vsOsm, vsQueued, dy, nodeOffsets, junctionOffsets, junctions, waysWithGaps, ways);
            var csvText = BuildEdgeCsv(edges, ways);

            File.WriteAllText(Path.Combine(directory, $"geometry-{stamp}.json"), jsonText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "latest.json"), jsonText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "latest.txt"), summaryText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "latest-edges.csv"), csvText, new UTF8Encoding(false));
            return Path.Combine(directory, "latest.txt");
        }

        private void AppendEdges(StringBuilder json, IEnumerable<EdgeRecord> ordered, List<WayRecord> ways)
        {
            json.Append('[');
            var first = true;
            foreach (var edge in ordered.Take(ReportTopCount))
            {
                json.Append(first ? "\n    " : ",\n    ");
                first = false;
                json.Append("{\"entity\": ").Append(Str($"{edge.Entity.Index}:{edge.Entity.Version}"))
                    .Append(", \"prefab\": ").Append(Str(edge.Prefab))
                    .Append(", \"length\": ").Append(Num(edge.Length))
                    .Append(", \"midX\": ").Append(Num(edge.Mid.x))
                    .Append(", \"midY\": ").Append(Num(edge.Mid.y))
                    .Append(", \"midZ\": ").Append(Num(edge.Mid.z))
                    .Append(", \"maxVsOsm\": ").Append(Opt(edge.MaxVsOsm))
                    .Append(", \"meanVsOsm\": ").Append(Opt(edge.MeanVsOsm))
                    .Append(", \"maxVsQueued\": ").Append(Opt(edge.MaxVsQueued))
                    .Append(", \"meanVsQueued\": ").Append(Opt(edge.MeanVsQueued))
                    .Append(", \"maxAbsDy\": ").Append(Opt(edge.MaxAbsDyVsQueued))
                    .Append(", \"startNodeOffset\": ").Append(Opt(edge.StartNodeOffset))
                    .Append(", \"endNodeOffset\": ").Append(Opt(edge.EndNodeOffset));
                if (edge.WayIndex >= 0)
                    json.Append(", ").Append(WayFields(ways[edge.WayIndex].Way));
                json.Append('}');
            }
            json.Append(first ? "]" : "\n  ]");
        }

        private static string WayFields(OsmWay way) =>
            $"\"wayId\": {way.Id}, \"name\": {Str(way.Name)}, \"highway\": {Str(way.Highway)}, " +
            $"\"roundabout\": {(way.IsRoundabout ? "true" : "false")}, \"structural\": {(way.UsesStructuralElevation ? "true" : "false")}";

        private string BuildSummaryText(
            string trigger,
            int wayCount,
            int edgeCount,
            int relatedCount,
            List<float> vsOsm,
            List<float> vsQueued,
            List<float> dy,
            List<float> nodeOffsets,
            List<float> junctionOffsets,
            List<JunctionRecord> junctions,
            List<WayRecord> waysWithGaps,
            List<WayRecord> ways)
        {
            var text = new StringBuilder();
            text.AppendLine($"OSM Importer geometry report ({trigger}) - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"OSM file: {_activeOptions?.Path}");
            text.AppendLine($"Simplify tolerance: {_activeOptions?.SimplifyToleranceMeters:0.##} m, scale: {_activeOptions?.ScaleFactor:0.##}");
            text.AppendLine($"Ways: {wayCount}, queued segments: {_queuedSegmentInfos.Count}, new edges: {edgeCount} ({relatedCount} analyzed, {edgeCount - relatedCount} unrelated/pre-existing)");
            text.AppendLine();
            text.AppendLine("Distributions (meters): p50 / p90 / p99 / max");
            text.AppendLine($"  Edge vs OSM reference (XZ):      {Brief(vsOsm)}");
            text.AppendLine($"  Edge vs queued segment (XZ):     {Brief(vsQueued)}");
            text.AppendLine($"  Edge vs queued segment (height): {Brief(dy)}");
            text.AppendLine($"  Edge node vs queued endpoint:    {Brief(nodeOffsets)}");
            text.AppendLine($"  OSM junction vs nearest node:    {Brief(junctionOffsets)}");
            text.AppendLine($"  Junctions with no game node within {ReportSearchRadiusMeters:0} m: {junctions.Count(j => j.Offset < 0f)} of {junctions.Count}");
            text.AppendLine($"  Ways with coverage gaps > {ReportCoverageToleranceMeters:0} m off: {waysWithGaps.Count} of {wayCount}");
            text.AppendLine();
            text.AppendLine("How to read this:");
            text.AppendLine("  - Large 'vs queued' => the game moved geometry after we queued it (snapping, merging, splitting).");
            text.AppendLine("  - Large 'vs OSM' but small 'vs queued' => the importer's pipeline (projection, simplification, way merging, roundabouts).");
            text.AppendLine("  - Coverage gaps => parts of an OSM way that no generated edge follows (missing, shortened or rerouted segments).");
            text.AppendLine();
            text.AppendLine("Details: latest.json (worst offenders with coordinates), latest-edges.csv (every new edge).");
            return text.ToString();
        }

        private static string BuildEdgeCsv(List<EdgeRecord> edges, List<WayRecord> ways)
        {
            var csv = new StringBuilder(edges.Count * 160 + 256);
            csv.AppendLine("entity,unrelated,prefab,wayId,highway,name,roundabout,length,midX,midY,midZ,maxVsOsm,meanVsOsm,maxVsQueued,meanVsQueued,maxAbsDy,startNodeOffset,endNodeOffset");
            foreach (var edge in edges)
            {
                var way = edge.WayIndex >= 0 ? ways[edge.WayIndex].Way : null;
                csv.Append(edge.Entity.Index).Append(':').Append(edge.Entity.Version).Append(',')
                    .Append(edge.Unrelated ? "1" : "0").Append(',')
                    .Append(Csv(edge.Prefab)).Append(',')
                    .Append(way != null ? way.Id.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(',')
                    .Append(Csv(way?.Highway)).Append(',')
                    .Append(Csv(way?.Name)).Append(',')
                    .Append(way != null && way.IsRoundabout ? "1" : "0").Append(',')
                    .Append(Num(edge.Length)).Append(',')
                    .Append(Num(edge.Mid.x)).Append(',').Append(Num(edge.Mid.y)).Append(',').Append(Num(edge.Mid.z)).Append(',')
                    .Append(CsvOpt(edge.MaxVsOsm)).Append(',').Append(CsvOpt(edge.MeanVsOsm)).Append(',')
                    .Append(CsvOpt(edge.MaxVsQueued)).Append(',').Append(CsvOpt(edge.MeanVsQueued)).Append(',')
                    .Append(CsvOpt(edge.MaxAbsDyVsQueued)).Append(',')
                    .Append(CsvOpt(edge.StartNodeOffset)).Append(',').Append(CsvOpt(edge.EndNodeOffset))
                    .AppendLine();
            }

            return csv.ToString();
        }

        private static string Distribution(List<float> values)
        {
            if (values.Count == 0)
                return "{\"count\": 0}";

            var sorted = values.OrderBy(v => v).ToList();
            var builder = new StringBuilder();
            builder.Append("{\"count\": ").Append(sorted.Count)
                .Append(", \"mean\": ").Append(Num(sorted.Average()))
                .Append(", \"p50\": ").Append(Num(Percentile(sorted, 0.5f)))
                .Append(", \"p90\": ").Append(Num(Percentile(sorted, 0.9f)))
                .Append(", \"p99\": ").Append(Num(Percentile(sorted, 0.99f)))
                .Append(", \"max\": ").Append(Num(sorted[sorted.Count - 1]))
                .Append(", \"histogram\": {");

            var lower = 0f;
            for (var i = 0; i <= ReportHistogramBounds.Length; i++)
            {
                var upper = i < ReportHistogramBounds.Length ? ReportHistogramBounds[i] : float.MaxValue;
                var count = sorted.Count(v => v >= lower && v < upper);
                var label = i < ReportHistogramBounds.Length ? $"<{Num(upper)}" : $">={Num(lower)}";
                builder.Append(i == 0 ? string.Empty : ", ").Append(Str(label)).Append(": ").Append(count);
                lower = upper;
            }

            builder.Append("}}");
            return builder.ToString();
        }

        private static string Brief(List<float> values)
        {
            if (values.Count == 0)
                return "n/a";

            var sorted = values.OrderBy(v => v).ToList();
            return $"{Percentile(sorted, 0.5f):0.00} / {Percentile(sorted, 0.9f):0.00} / {Percentile(sorted, 0.99f):0.00} / {sorted[sorted.Count - 1]:0.00}  (n={sorted.Count})";
        }

        private static float Percentile(List<float> sorted, float fraction)
        {
            var index = (int)math.clamp(math.round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[index];
        }

        private static string Num(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Num(double value) => Num((float)value);

        private static string Opt(float value) => value < 0f ? "null" : Num(value);

        private static string CsvOpt(float value) => value < 0f ? string.Empty : Num(value);

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        private static string Str(string value)
        {
            var builder = new StringBuilder((value?.Length ?? 0) + 2);
            builder.Append('"');
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        // ---------------------------------------------------------------------------------
        // Geometry helpers
        // ---------------------------------------------------------------------------------

        private static float3 EvaluateBezier(Bezier4x3 curve, float t)
        {
            var u = 1f - t;
            return u * u * u * curve.a + 3f * u * u * t * curve.b + 3f * u * t * t * curve.c + t * t * t * curve.d;
        }

        private static void SampleBezier(Bezier4x3 curve, float length, List<float3> samples)
        {
            samples.Clear();
            var count = math.clamp((int)math.ceil(length / ReportSampleStepMeters), 1, ReportMaxSamplesPerEdge);
            for (var k = 0; k <= count; k++)
                samples.Add(EvaluateBezier(curve, (float)k / count));
        }

        // Uniform-grid index of line segments (or points, when A == B) in the XZ plane.
        private sealed class SegmentIndex
        {
            private readonly float _cell;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly HashSet<long> _scratch = new HashSet<long>();
            private int[] _visitStamp = new int[256];
            private int _query;

            public readonly List<float3> A = new List<float3>();
            public readonly List<float3> B = new List<float3>();
            public readonly List<int> Owner = new List<int>();

            public SegmentIndex(float cell)
            {
                _cell = cell;
            }

            public int Count => A.Count;

            private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

            private int CellCoord(float value) => (int)math.floor(value / _cell);

            public void Add(float3 a, float3 b, int owner)
            {
                var id = A.Count;
                A.Add(a);
                B.Add(b);
                Owner.Add(owner);
                if (id >= _visitStamp.Length)
                    Array.Resize(ref _visitStamp, _visitStamp.Length * 2);

                // Walk the segment in quarter-cell steps and register each visited cell plus its
                // 8 neighbours; this guarantees every cell the segment touches is registered.
                _scratch.Clear();
                var length = math.distance(a.xz, b.xz);
                var steps = math.max(1, (int)math.ceil(length / (_cell * 0.25f)));
                for (var s = 0; s <= steps; s++)
                {
                    var p = math.lerp(a.xz, b.xz, (float)s / steps);
                    var cx = CellCoord(p.x);
                    var cz = CellCoord(p.y);
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        for (var dz = -1; dz <= 1; dz++)
                        {
                            var key = Key(cx + dx, cz + dz);
                            if (!_scratch.Add(key))
                                continue;

                            if (!_cells.TryGetValue(key, out var list))
                            {
                                list = new List<int>(4);
                                _cells[key] = list;
                            }

                            list.Add(id);
                        }
                    }
                }
            }

            public bool Nearest(float2 point, float maxDistance, out float distance, out int segment, out float t)
            {
                distance = float.MaxValue;
                segment = -1;
                t = 0f;
                if (A.Count == 0)
                    return false;

                _query++;
                if (_query == int.MaxValue)
                {
                    Array.Clear(_visitStamp, 0, _visitStamp.Length);
                    _query = 1;
                }

                var cx = CellCoord(point.x);
                var cz = CellCoord(point.y);
                var maxRing = (int)math.ceil(maxDistance / _cell) + 1;

                for (var ring = 0; ring <= maxRing; ring++)
                {
                    for (var dx = -ring; dx <= ring; dx++)
                    {
                        for (var dz = -ring; dz <= ring; dz++)
                        {
                            if (math.max(math.abs(dx), math.abs(dz)) != ring)
                                continue;

                            if (!_cells.TryGetValue(Key(cx + dx, cz + dz), out var list))
                                continue;

                            foreach (var id in list)
                            {
                                if (_visitStamp[id] == _query)
                                    continue;

                                _visitStamp[id] = _query;
                                var d = DistanceToSegment(point, A[id].xz, B[id].xz, out var segmentT);
                                if (d < distance)
                                {
                                    distance = d;
                                    segment = id;
                                    t = segmentT;
                                }
                            }
                        }
                    }

                    // Anything registered only in cells beyond this ring is at least ring * cell away.
                    if (segment >= 0 && distance <= ring * _cell)
                        break;
                }

                return segment >= 0 && distance <= maxDistance;
            }

            private static float DistanceToSegment(float2 p, float2 a, float2 b, out float t)
            {
                var ab = b - a;
                var lengthSq = math.lengthsq(ab);
                t = lengthSq > 1e-8f ? math.saturate(math.dot(p - a, ab) / lengthSq) : 0f;
                return math.distance(p, a + ab * t);
            }
        }
    }
}
