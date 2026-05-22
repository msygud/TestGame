using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  SpawnSystem — Single 인스턴싱
    //
    //  SpawnRequest 처리:
    //    - PrefabLookup으로 (MainKey, VariantKey) → Entity 조회
    //    - 인스턴싱 + LocalTransform 적용
    //    - MapLoaded 태그 부여 (맵 정리 시 사용)
    //    - SpawnRequest 엔티티 파괴
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoaderSystem))]
    public partial struct SpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<PrefabLookup>()) return;
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, reqEntity) in
                SystemAPI.Query<RefRO<SpawnRequest>>().WithEntityAccess())
            {
                int mk = req.ValueRO.MainKey;
                int vk = req.ValueRO.VariantKey;

                if (!lookup.TryGet(mk, vk, out Entity prefab))
                {
                    Debug.LogWarning(
                        $"[SpawnSystem] Prefab not found: ({mk}, {vk}).");
                    ecb.DestroyEntity(reqEntity);
                    continue;
                }

                var instance = ecb.Instantiate(prefab);
                ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(
                    req.ValueRO.Position,
                    req.ValueRO.Rotation,
                    req.ValueRO.Scale));

                // 맵 정리용 태그
                ecb.AddComponent<MapLoaded>(instance);
                ecb.AddComponent(instance, new MapPlacementIdentity
                {
                    MainKey = req.ValueRO.MainKey,
                    VariantKey = req.ValueRO.VariantKey,
                    Kind = MapPlacementKind.Single,
                    OwnerLocalId = req.ValueRO.OwnerLocalId,
                });
                ecb.AddComponent(instance, new MapFootprint
                {
                    Cell = req.ValueRO.Cell,
                    Size = math.max(req.ValueRO.Size, new int2(1, 1)),
                    Height = req.ValueRO.HeightIndex,
                    OccupancyType = req.ValueRO.OccupancyType,
                });

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  MultiSpawnSystem — Multi 결정적 랜덤 배치
    //
    //  MultiSpawnRequest 처리:
    //    - Seed 기반 결정적 랜덤 (Unity.Mathematics.Random)
    //    - 1셀 영역 안에 Count개 랜덤 위치 + 랜덤 Y회전
    //    - 개별 아이템 크기(ItemSize) 감안한 최소 간격 보장
    //      (단순화: 간격 보장 없이 순수 랜덤, 추후 포아송 디스크로 개선 가능)
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoaderSystem))]
    public partial struct MultiSpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<PrefabLookup>()) return;
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, reqEntity) in
                SystemAPI.Query<RefRO<MultiSpawnRequest>>().WithEntityAccess())
            {
                int mk = req.ValueRO.MainKey;
                int vk = req.ValueRO.VariantKey;

                if (!lookup.TryGet(mk, vk, out Entity prefab))
                {
                    Debug.LogWarning(
                        $"[MultiSpawnSystem] Prefab not found: ({mk}, {vk}).");
                    ecb.DestroyEntity(reqEntity);
                    continue;
                }

                float3 center = req.ValueRO.Position;
                float cs      = req.ValueRO.CellSize;
                float h       = req.ValueRO.Height;

                // 결정적 랜덤 (Seed 기반)
                var rng = new Unity.Mathematics.Random((uint)(req.ValueRO.Seed + 1));

                for (int i = 0; i < req.ValueRO.Count; i++)
                {
                    float localX = rng.NextFloat(0f, cs);
                    float localZ = rng.NextFloat(0f, cs);
                    float rotY   = rng.NextFloat(0f, 360f);

                    float3 pos = new float3(
                        center.x - cs * 0.5f + localX,
                        h,
                        center.z - cs * 0.5f + localZ);

                    var instance = ecb.Instantiate(prefab);
                    ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(
                        pos,
                        quaternion.RotateY(math.radians(rotY)),
                        req.ValueRO.Scale));

                    ecb.AddComponent<MapLoaded>(instance);
                    ecb.AddComponent(instance, new MapPlacementIdentity
                    {
                        MainKey = req.ValueRO.MainKey,
                        VariantKey = req.ValueRO.VariantKey,
                        Kind = MapPlacementKind.Multi,
                        OwnerLocalId = req.ValueRO.OwnerLocalId,
                    });

                    if (i == 0)
                    {
                        ecb.AddComponent(instance, new MapFootprint
                        {
                            Cell = req.ValueRO.Cell,
                            Size = math.max(req.ValueRO.Size, new int2(1, 1)),
                            Height = req.ValueRO.HeightIndex,
                            OccupancyType = req.ValueRO.OccupancyType,
                        });
                    }
                }

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
