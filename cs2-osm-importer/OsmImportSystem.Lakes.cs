using System;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using ObjectTransform = Game.Objects.Transform;

namespace cs2_osm_importer
{
    /// <summary>
    /// OSM lakes, ponds and reservoirs filled with water. Each lake gets constant-level water sources (the editor's
    /// water tool sources: the game keeps the water at Transform.y + height inside the source's radius) at the height
    /// of the ground just outside its outline. The heightmap has to have the lake bed below that shore; lakes without
    /// one are skipped. Verified in game on Green Lake (bed 8.5 m, shore ~53 m): a source fills the basin and holds the
    /// level without spilling.
    /// </summary>
    public partial class OsmImportSystem
    {
        private const float LakeMinSquareMeters = 1000f;
        private const float LakeShoreProbeOffset = 8f;
        private const float LakeShoreProbeSpacing = 10f;
        private const float LakeMinSourceRadius = 15f;
        private const float LakeMaxSourceRadius = 150f;
        private const int LakeMaxSourcesPerLake = 48;
        private const float LakeLevelBelowShore = 0.3f;
        private const float LakeMinDepth = 1f;

        // Lakes fill gradually: a source set straight to the full level pours tens of meters of water into the bed
        // at once, and the surge splashes over low spots of the shore into hollows nearby (seen at Green Lake's
        // south end: a pond 2.5 m deep in the pitch-and-putt hollow, above the lake level). Raising the level by
        // this much per frame keeps the water calm (~0.18 m/s at 60 fps; Green Lake's 41 m in about 4 minutes).
        private const float LakeFillMetersPerFrame = 0.003f;

        private sealed class LakeFill
        {
            public Entity Source;
            public float Ground, Radius, Target, Level;
        }

        private readonly List<Entity> _generatedLakeSources = new List<Entity>();
        private readonly List<LakeFill> _lakeFills = new List<LakeFill>();
        private bool _lakesDone;

        private void ResetLakeState() => _lakesDone = false;

        /// <summary>True while lakes are still filling up to their level.</summary>
        private bool LakesFilling => _lakeFills.Count > 0;

        private IEnumerable<Entity> TakeGeneratedLakes()
        {
            var entities = _generatedLakeSources.Where(e => e != Entity.Null && EntityManager.Exists(e)).ToList();
            _generatedLakeSources.Clear();
            _lakeFills.Clear();
            return entities;
        }

        /// <summary>Every frame: raise the filling lakes' sources a little. A source whose own spot is still above the
        /// rising level stays off (radius 0; a negative height would make it drain).</summary>
        private void UpdateLakeFill()
        {
            if (_lakeFills.Count == 0)
                return;
            try
            {
                for (var i = _lakeFills.Count - 1; i >= 0; i--)
                {
                    var f = _lakeFills[i];
                    if (!EntityManager.Exists(f.Source) || EntityManager.HasComponent<Deleted>(f.Source))
                    {
                        _lakeFills.RemoveAt(i);
                        continue;
                    }
                    f.Level = math.min(f.Target, f.Level + LakeFillMetersPerFrame);
                    var data = EntityManager.GetComponentData<WaterSourceData>(f.Source);
                    var active = f.Level > f.Ground + 0.5f;
                    data.m_Radius = active ? f.Radius : 0f;
                    data.m_Height = active ? f.Level - f.Ground : 0f;
                    EntityManager.SetComponentData(f.Source, data);
                    if (f.Level >= f.Target)
                        _lakeFills.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
                _lakeFills.Clear();
            }
        }

        /// <summary>Runs once per generation, when it starts: the lakes fill while the roads are built.</summary>
        private bool ProcessLakeStep()
        {
            if (_lakesDone)
                return false;
            _lakesDone = true;
            if (!(_activeGenerateOptions?.GenerateLakes ?? false) || _document == null || _document.Lakes.Count == 0)
                return false;
            try
            {
                QueueLakes();
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex);
            }
            return false;
        }

        private const float PierDeckAboveWater = 1f;
        private const float PierLakeReach = 50f;

        /// <summary>
        /// Pier decks: 1 m above the water they stand in (the level of the lake around them, else the sea level);
        /// where a pier (or its added shore connector) is over land, it follows the ground.
        /// </summary>
        private void SetPierDeckHeights()
        {
            if (_document == null)
                return;
            var piers = _document.Ways.Where(w => w.IsPier && w.Points.Count >= 2).ToList();
            var fillLakes = _activeGenerateOptions?.GenerateLakes ?? false;
            if (piers.Count == 0 && !fillLakes)
                return;
            CacheTerrainHeightData();
            var levels = new List<(OsmLake Lake, float Level)>();
            foreach (var lake in _document.Lakes)
            {
                if (lake.AreaSquareMeters >= LakeMinSquareMeters && TryLakeLevel(lake, out var level, out _))
                    levels.Add((lake, level));
            }
            var sea = World.GetExistingSystemManaged<WaterSystem>()?.SeaLevel ?? 0f;
            int onLakes = 0, onSea = 0;
            // Heights of the other ways' points: where a pier joins a path or street it takes that point's height
            // (a fresh terrain sample differs from the parser's smoothed profile by a few decimeters, and CS2 then
            // builds two unconnected nodes at the joint).
            var otherHeights = new Dictionary<(int, int), float>();
            foreach (var way in _document.Ways)
            {
                if (way.IsPier)
                    continue;
                foreach (var p in way.Points)
                    otherHeights[PointKey(p)] = p.y;
            }
            foreach (var pier in piers)
            {
                float? water = null;
                var bestDistance = PierLakeReach;
                foreach (var (lake, level) in levels)
                {
                    foreach (var p in pier.Points)
                    {
                        var d = ParkingGeometry.Contains(lake.Ring, p.xz) ? 0f : ParkingGeometry.DistanceToRing(lake.Ring, p.xz);
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            water = level;
                        }
                    }
                }
                if (water.HasValue)
                    onLakes++;
                else
                {
                    water = sea;
                    onSea++;
                }

                var terrain = new float[pier.Points.Count];
                for (var i = 0; i < pier.Points.Count; i++)
                    terrain[i] = SampleTerrainHeight(new float3(pier.Points[i].x, 0f, pier.Points[i].z));
                // Over the water the deck, on land the ground: the same rule on every pier way, so the points they
                // share (and the shore paths they join) always get the same height.
                var deck = water.Value + PierDeckAboveWater;
                for (var i = 0; i < pier.Points.Count; i++)
                {
                    var p = pier.Points[i];
                    var ground = otherHeights.TryGetValue(PointKey(p), out var shared) ? shared : terrain[i];
                    p.y = math.max(ground, deck);
                    pier.Points[i] = p;
                }
            }
            Mod.log.Info($"Pier decks: {onLakes} over lakes, {onSea} at the sea level {sea:F1} m.");

            // And a path point a pier deck joins above its own height comes up to the deck.
            var pierHeights = new Dictionary<(int, int), float>();
            foreach (var pier in piers)
                foreach (var p in pier.Points)
                    pierHeights[PointKey(p)] = p.y;
            foreach (var way in _document.Ways)
            {
                if (way.IsPier)
                    continue;
                for (var i = 0; i < way.Points.Count; i++)
                {
                    var p = way.Points[i];
                    if (pierHeights.TryGetValue(PointKey(p), out var deckY) && deckY > p.y)
                    {
                        p.y = deckY;
                        way.Points[i] = p;
                    }
                }
            }

            if (fillLakes)
                RaiseShoreWays(levels);
        }

        private static (int, int) PointKey(float3 p) => ((int)math.round(p.x * 20f), (int)math.round(p.z * 20f));

        private const float ShoreWayReach = 15f;

        /// <summary>
        /// The heightmap's lake bed can reach a few meters past the mapped shore, and the paths along the shore then
        /// sit on its slope, under the water (seen on Green Lake's west shore: a shore footway at 45-48 m, the lake at
        /// 49.5 m). Every way point in a lake or within ShoreWayReach of its shore is kept at least as high as a pier
        /// deck, the same rule the piers use, so the points piers and shore paths share stay at one height.
        /// </summary>
        private void RaiseShoreWays(List<(OsmLake Lake, float Level)> levels)
        {
            if (levels.Count == 0)
                return;
            var boxes = levels.Select(l => ParkingGeometry.Bounds(l.Lake.Ring) + new float4(-ShoreWayReach, -ShoreWayReach, ShoreWayReach, ShoreWayReach)).ToList();
            int raisedWays = 0, raisedPoints = 0;
            foreach (var way in _document.Ways)
            {
                if (way.IsPier)
                    continue;
                var raised = false;
                for (var i = 0; i < way.Points.Count; i++)
                {
                    var p = way.Points[i];
                    for (var k = 0; k < levels.Count; k++)
                    {
                        var b = boxes[k];
                        if (p.x < b.x || p.z < b.y || p.x > b.z || p.z > b.w)
                            continue;
                        var minY = levels[k].Level + PierDeckAboveWater;
                        if (p.y >= minY)
                            continue;
                        var ring = levels[k].Lake.Ring;
                        if (!ParkingGeometry.Contains(ring, p.xz) && ParkingGeometry.DistanceToRing(ring, p.xz) > ShoreWayReach)
                            continue;
                        p.y = minY;
                        way.Points[i] = p;
                        raised = true;
                        raisedPoints++;
                    }
                }
                if (raised)
                    raisedWays++;
            }
            if (raisedPoints > 0)
                Mod.log.Info($"Shore ways: {raisedPoints} points of {raisedWays} ways by the lakes raised above the water (the heightmap's lake bed reaches under them).");
        }

        private void QueueLakes()
        {
            CacheTerrainHeightData();
            int filled = 0, small = 0, flat = 0, sources = 0, lowShore = 0;
            var filledLakes = new List<(OsmLake Lake, float Level)>();
            foreach (var lake in _document.Lakes)
            {
                if (lake.AreaSquareMeters < LakeMinSquareMeters)
                {
                    small++;
                    continue;
                }

                if (!TryLakeLevel(lake, out var level, out var hasLowSpot))
                {
                    flat++;
                    continue;
                }
                if (hasLowSpot)
                    lowShore++;

                var placed = PlaceLakeSources(lake, level);
                if (placed == 0)
                {
                    flat++;
                    Mod.log.Info($"Lake {lake.Id} '{lake.Name}' not filled: no lake bed below the shore level {level:F1} m in the heightmap.");
                    continue;
                }
                filled++;
                filledLakes.Add((lake, level));
                sources += placed;
            }

            var trees = RemoveTreesInLakes(filledLakes);
            Mod.log.Info($"Lakes: {filled} filling with {sources} water sources ({trees} map trees in them removed); skipped {small} under {LakeMinSquareMeters:F0} m2 and {flat} without a lake bed below their shore in the heightmap" +
                         (lowShore > 0 ? $"; {lowShore} have a low spot on the shore (outlet or cut), kept to the usual shore height there." : "."));
        }

        /// <summary>
        /// Water level: the lowest ground just outside the outline, minus a little. A few low samples far under the
        /// rest (an outlet stream, a road cut) would drain the whole lake level, so those fall back to the 10th
        /// percentile of the shore.
        /// </summary>
        private bool TryLakeLevel(OsmLake lake, out float level, out bool hasLowSpot)
        {
            level = 0f;
            hasLowSpot = false;
            var ring = lake.Ring;
            var shore = new List<float>();
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                var d = b - a;
                var len = math.length(d);
                if (len < 0.01f)
                    continue;
                // Rings are counter-clockwise (positive signed area): outward is to the right of the edge direction.
                var outward = new float2(d.y, -d.x) / len;
                var steps = Math.Max(1, (int)math.ceil(len / LakeShoreProbeSpacing));
                for (var s = 0; s < steps; s++)
                {
                    var p = a + d * ((s + 0.5f) / steps) + outward * LakeShoreProbeOffset;
                    if (ParkingGeometry.Contains(ring, p))
                        continue;
                    shore.Add(SampleTerrainHeight(new float3(p.x, 0f, p.y)));
                }
            }
            if (shore.Count == 0)
                return false;
            shore.Sort();
            var low = shore[0];
            var p10 = shore[(int)(shore.Count * 0.1f)];
            if (p10 - low > 3f)
            {
                hasLowSpot = true;
                low = p10;
            }
            level = low - LakeLevelBelowShore;
            return true;
        }

        private int PlaceLakeSources(OsmLake lake, float level)
        {
            var ring = lake.Ring;
            var bounds = ParkingGeometry.Bounds(ring);
            var radius = math.clamp(math.sqrt(lake.AreaSquareMeters) / 3f, LakeMinSourceRadius, LakeMaxSourceRadius);
            var spacing = radius * 1.2f;
            var size = bounds.zw - bounds.xy;
            while ((size.x / spacing + 1) * (size.y / spacing + 1) > LakeMaxSourcesPerLake * 4)
                spacing *= 1.25f;

            var candidates = new List<(float2 P, float R, float Depth)>();
            for (var x = bounds.x + spacing * 0.5f; x < bounds.z; x += spacing)
            {
                for (var z = bounds.y + spacing * 0.5f; z < bounds.w; z += spacing)
                {
                    var p = new float2(x, z);
                    if (TryLakeSourceSpot(lake, p, level, radius, out var r, out var depth))
                        candidates.Add((p, r, depth));
                }
            }
            if (candidates.Count == 0)
            {
                // Small or narrow lake: one source at the centroid, if that is on the lake.
                var c = ParkingGeometry.Centroid(ring);
                if (TryLakeSourceSpot(lake, c, level, radius, out var r, out var depth))
                    candidates.Add((c, r, depth));
            }

            // Keep the biggest ones when a long, thin lake produced too many.
            var chosen = candidates.OrderByDescending(c => c.R).Take(LakeMaxSourcesPerLake).ToList();
            if (chosen.Count == 0)
                return 0;
            // The whole lake rises together from just above its deepest source spot.
            var start = chosen.Min(c => level - c.Depth) + 0.5f;
            foreach (var c in chosen)
            {
                var ground = SampleTerrainHeight(new float3(c.P.x, 0f, c.P.y));
                var e = CreateLakeSource(c.P, ground);
                _generatedLakeSources.Add(e);
                _lakeFills.Add(new LakeFill { Source = e, Ground = ground, Radius = c.R, Target = level, Level = start });
            }
            return chosen.Count;
        }

        /// <summary>
        /// The map's own trees standing in the lakes (a map's vegetation is unowned trees; the importer's park trees
        /// are left alone, they already keep off water) would end up under water: delete them. That is every one inside
        /// the outline, and those just outside it whose ground is below the water (the heightmap's lake bed can reach
        /// a little past the mapped shore).
        /// </summary>
        private const float LakeTreeShoreReach = 40f;

        private int RemoveTreesInLakes(List<(OsmLake Lake, float Level)> lakes)
        {
            if (lakes.Count == 0)
                return 0;
            var ours = new HashSet<Entity>(_generatedParkEntities);
            var removed = 0;
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Tree>(), ComponentType.ReadOnly<ObjectTransform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Owner>() },
            }))
            using (var trees = query.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                var bounds = lakes.Select(l => ParkingGeometry.Bounds(l.Lake.Ring) + new float4(-LakeTreeShoreReach, -LakeTreeShoreReach, LakeTreeShoreReach, LakeTreeShoreReach)).ToList();
                foreach (var tree in trees)
                {
                    if (ours.Contains(tree))
                        continue;
                    var p = EntityManager.GetComponentData<ObjectTransform>(tree).m_Position.xz;
                    for (var i = 0; i < lakes.Count; i++)
                    {
                        var b = bounds[i];
                        if (p.x < b.x || p.y < b.y || p.x > b.z || p.y > b.w)
                            continue;
                        var lake = lakes[i].Lake;
                        if (lake.Holes.Any(h => ParkingGeometry.Contains(h, p)))
                            continue;
                        if (!ParkingGeometry.Contains(lake.Ring, p) &&
                            (ParkingGeometry.DistanceToRing(lake.Ring, p) > LakeTreeShoreReach || SampleTerrainHeight(new float3(p.x, 0f, p.y)) > lakes[i].Level - 0.1f))
                            continue;
                        EntityManager.AddComponent<Deleted>(tree);
                        EntityManager.AddComponent<Updated>(tree);
                        removed++;
                        break;
                    }
                }
            }
            return removed;
        }

        private bool TryLakeSourceSpot(OsmLake lake, float2 p, float level, float maxRadius, out float radius, out float depth)
        {
            radius = 0f;
            depth = 0f;
            if (!ParkingGeometry.Contains(lake.Ring, p))
                return false;
            var clearance = ParkingGeometry.DistanceToRing(lake.Ring, p);
            foreach (var hole in lake.Holes)
            {
                if (ParkingGeometry.Contains(hole, p))
                    return false;
                clearance = math.min(clearance, ParkingGeometry.DistanceToRing(hole, p));
            }
            depth = level - SampleTerrainHeight(new float3(p.x, 0f, p.y));
            if (depth < LakeMinDepth)
                return false;
            // The source fills everything below the level within its radius, so it must not reach past the shore
            // into another hollow.
            radius = math.min(maxRadius, clearance + 2f);
            return radius >= 5f;
        }

        /// <summary>A source that starts off (radius 0); UpdateLakeFill turns it on and raises it.</summary>
        private Entity CreateLakeSource(float2 p, float ground)
        {
            var e = EntityManager.CreateEntity(typeof(WaterSourceData), typeof(ObjectTransform), typeof(Created), typeof(Updated));
            EntityManager.SetComponentData(e, new WaterSourceData
            {
                m_ConstantDepth = 0,
                m_Radius = 0f,
                m_Height = 0f,
                m_Multiplier = 1f,
                m_Polluted = 0f,
                m_Id = -1,
                m_Modifier = 1f,
            });
            EntityManager.SetComponentData(e, new ObjectTransform { m_Position = new float3(p.x, ground, p.y), m_Rotation = quaternion.identity });
            return e;
        }
    }
}
