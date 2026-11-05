using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;
using PrefabSubArea = Game.Prefabs.SubArea;
using PrefabSubNet = Game.Prefabs.SubNet;

namespace cs2_osm_importer
{
    /// <summary>
    /// Placing a vanilla building from code the way the game's object tool does. A bare CreationDefinition +
    /// ObjectDefinition creates the building and its props only: the prefab's internal nets (garage ramps and parking,
    /// park paths) and sub-areas are created by the tool as separate definitions (ObjectToolBaseSystem.UpdateSubNets /
    /// UpdateSubAreas), linked to the building through an OwnerDefinition that FindOwnersSystem matches by prefab and
    /// exact transform. Without them a garage has no parking and a park reports "No Pedestrian Access".
    /// </summary>
    public partial class OsmImportSystem
    {
        /// <summary>Queues the building and its sub-nets and sub-areas in this frame; every definition is added to <paramref name="definitions"/>.</summary>
        private Entity QueueBuildingWithSubElements(Entity prefab, float3 position, quaternion rotation, int seed, List<Entity> definitions)
        {
            var editor = Game.SceneFlow.GameManager.instance != null && Game.SceneFlow.GameManager.instance.gameMode == Game.GameMode.Editor;
            var building = EntityManager.CreateEntity();
            EntityManager.AddComponentData(building, new CreationDefinition { m_Prefab = prefab, m_Flags = CreationFlags.Permanent, m_RandomSeed = seed });
            EntityManager.AddComponentData(building, new ObjectDefinition
            {
                m_Position = position,
                m_LocalPosition = position,
                m_Rotation = rotation,
                m_LocalRotation = rotation,
                m_Scale = new float3(1f),
                m_Intensity = 1f,
                m_Probability = 100,
                m_ParentMesh = -1,
                m_PrefabSubIndex = -1
            });
            EntityManager.AddComponentData(building, new Updated());
            EntityManager.AddComponentData(building, new OsmImportDefinition { SessionId = _sessionId });
            definitions.Add(building);

            var owner = new OwnerDefinition { m_Prefab = prefab, m_Position = position, m_Rotation = rotation };
            var random = new Unity.Mathematics.Random((uint)math.max(1, seed));
            QueueSubNets(prefab, position, rotation, owner, editor, ref random, definitions);
            QueueSubAreas(prefab, position, rotation, owner, editor, ref random, definitions);
            return building;
        }

        private void QueueSubNets(Entity prefab, float3 position, quaternion rotation, OwnerDefinition owner, bool editor, ref Unity.Mathematics.Random random, List<Entity> definitions)
        {
            if (!EntityManager.HasBuffer<PrefabSubNet>(prefab))
                return;
            var subNets = EntityManager.GetBuffer<PrefabSubNet>(prefab, true);

            // Shared node positions: the average of every sub-net end that names the same node index (as the tool does).
            var nodePositions = new List<float4>();
            for (var i = 0; i < subNets.Length; i++)
            {
                void AddNode(int index, float3 p)
                {
                    if (index < 0) return;
                    while (nodePositions.Count <= index) nodePositions.Add(float4.zero);
                    nodePositions[index] += new float4(p, 1f);
                }
                AddNode(subNets[i].m_NodeIndex.x, subNets[i].m_Curve.a);
                AddNode(subNets[i].m_NodeIndex.y, subNets[i].m_Curve.d);
            }
            for (var j = 0; j < nodePositions.Count; j++)
                nodePositions[j] /= math.max(1f, nodePositions[j].w);

            var leftHand = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>().leftHandTraffic;
            var geometryLookup = GetComponentLookup<NetGeometryData>(true);
            var transform = new Transform(position, rotation);
            for (var k = 0; k < subNets.Length; k++)
            {
                var subNet = NetUtils.GetSubNet(subNets, k, leftHand, ref geometryLookup);
                var local = subNet.m_Curve;
                var geometry = EntityManager.HasComponent<NetGeometryData>(subNet.m_Prefab) ? EntityManager.GetComponentData<NetGeometryData>(subNet.m_Prefab) : default;
                var onParentMesh = subNet.m_ParentMesh.x >= 0 && subNet.m_ParentMesh.y >= 0;
                Bezier4x3 world;
                var worldCurve = ObjectUtils.LocalToWorld(position, rotation, local);
                if (onParentMesh || !_hasTerrainHeightData || (geometry.m_Flags & Game.Net.GeometryFlags.FlattenTerrain) != 0)
                {
                    // On the building's own mesh (floors, ramps), or flattened into its lot: exactly where the prefab puts it.
                    world = worldCurve;
                }
                else
                {
                    var adjusted = NetUtils.AdjustPosition(new Curve { m_Bezier = worldCurve }, subNet.m_ParentMesh.x >= 0, subNet.m_ParentMesh.x >= 0 || subNet.m_ParentMesh.y >= 0, subNet.m_ParentMesh.y >= 0, ref _terrainHeightData).m_Bezier;
                    adjusted.a.y += local.a.y;
                    adjusted.b.y += local.b.y;
                    adjusted.c.y += local.c.y;
                    adjusted.d.y += local.d.y;
                    world = adjusted;
                }

                var course = new NetCourse
                {
                    m_Curve = world,
                    m_StartPosition = new CoursePos
                    {
                        m_Position = subNet.m_NodeIndex.x >= 0 ? ObjectUtils.LocalToWorld(transform, nodePositions[subNet.m_NodeIndex.x].xyz) : world.a,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(world), rotation),
                        m_CourseDelta = 0f,
                        m_Elevation = local.a.y,
                        m_ParentMesh = subNet.m_ParentMesh.x,
                        m_Flags = CoursePosFlags.IsFirst,
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Position = subNet.m_NodeIndex.y >= 0 ? ObjectUtils.LocalToWorld(transform, nodePositions[subNet.m_NodeIndex.y].xyz) : world.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(world), rotation),
                        m_CourseDelta = 1f,
                        m_Elevation = local.d.y,
                        m_ParentMesh = subNet.m_ParentMesh.y,
                        m_Flags = CoursePosFlags.IsLast,
                    },
                    m_Length = MathUtils.Length(world),
                    m_FixedIndex = -1,
                };
                if (course.m_StartPosition.m_Position.Equals(course.m_EndPosition.m_Position))
                {
                    course.m_StartPosition.m_Flags |= CoursePosFlags.IsLast;
                    course.m_EndPosition.m_Flags |= CoursePosFlags.IsFirst;
                }

                var e = EntityManager.CreateEntity();
                EntityManager.AddComponentData(e, new CreationDefinition { m_Prefab = subNet.m_Prefab, m_Flags = CreationFlags.Permanent, m_RandomSeed = random.NextInt() });
                EntityManager.AddComponentData(e, owner);
                EntityManager.AddComponentData(e, course);
                EntityManager.AddComponentData(e, new Updated());
                EntityManager.AddComponentData(e, new OsmImportDefinition { SessionId = _sessionId });
                if (subNet.m_Upgrades != default(CompositionFlags))
                    EntityManager.AddComponentData(e, new Upgraded { m_Flags = subNet.m_Upgrades });
                if (editor)
                    EntityManager.AddComponentData(e, new LocalCurveCache { m_Curve = local });
                definitions.Add(e);
            }
        }

        private void QueueSubAreas(Entity prefab, float3 position, quaternion rotation, OwnerDefinition owner, bool editor, ref Unity.Mathematics.Random random, List<Entity> definitions)
        {
            if (!EntityManager.HasBuffer<PrefabSubArea>(prefab) || !EntityManager.HasBuffer<SubAreaNode>(prefab))
                return;
            var subAreas = EntityManager.GetBuffer<PrefabSubArea>(prefab, true);
            var areaNodes = EntityManager.GetBuffer<SubAreaNode>(prefab, true);
            var transform = new Transform(position, rotation);
            for (var i = 0; i < subAreas.Length; i++)
            {
                var subArea = subAreas[i];
                if (!EntityManager.HasComponent<AreaGeometryData>(subArea.m_Prefab))
                    continue;
                var areaGeometry = EntityManager.GetComponentData<AreaGeometryData>(subArea.m_Prefab);
                var e = EntityManager.CreateEntity();
                var flags = CreationFlags.Permanent;
                if (areaGeometry.m_Type != 0)
                    flags |= CreationFlags.Hidden;
                EntityManager.AddComponentData(e, new CreationDefinition { m_Prefab = subArea.m_Prefab, m_Flags = flags, m_RandomSeed = random.NextInt() });
                EntityManager.AddComponentData(e, owner);
                EntityManager.AddComponentData(e, new Updated());
                EntityManager.AddComponentData(e, new OsmImportDefinition { SessionId = _sessionId });
                var count = subArea.m_NodeRange.y - subArea.m_NodeRange.x;
                var nodes = EntityManager.AddBuffer<AreaNode>(e);
                var caches = new List<LocalNodeCache>();
                var index = ObjectToolBaseSystem.GetFirstNodeIndex(areaNodes, subArea.m_NodeRange);
                for (var j = 0; j <= count; j++)
                {
                    var node = areaNodes[index];
                    var elevation = node.m_ParentMesh >= 0 ? node.m_Position.y : float.MinValue;
                    nodes = EntityManager.GetBuffer<AreaNode>(e);
                    nodes.Add(new AreaNode(ObjectUtils.LocalToWorld(transform, node.m_Position), elevation));
                    caches.Add(new LocalNodeCache { m_Position = node.m_Position, m_ParentMesh = node.m_ParentMesh });
                    if (++index == subArea.m_NodeRange.y)
                        index = subArea.m_NodeRange.x;
                }
                if (editor)
                {
                    var cache = EntityManager.AddBuffer<LocalNodeCache>(e);
                    foreach (var c in caches)
                        cache.Add(c);
                }
                definitions.Add(e);
            }
        }
    }
}
