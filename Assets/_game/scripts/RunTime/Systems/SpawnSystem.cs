using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Game.Unit;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  SpawnSystem — Single 인스턴싱
    //
    //  SpawnRequest 처리:
    //    - PrefabLookup.Get(MainKey, VariantKey) → Entity 조회
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

            // 건물 기본 체력(균일, 임시) — 전투로 파괴 가능하게 부여. SpawnConfig 싱글톤(밸런스).
            //   TODO: 프리팹별 값이 필요하면 BuildingAuthoring 베이킹으로 이전(능력=컴포넌트 원칙).
            float buildingDefaultHealth = (SystemAPI.TryGetSingleton<SpawnConfig>(out var spawnCfg)
                ? spawnCfg : SpawnConfig.Default).BuildingDefaultHealth;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var teamsByLocalId = new NativeArray<TeamInfoData>(8, Allocator.Temp);
            var hasTeamByLocalId = new NativeArray<byte>(8, Allocator.Temp);

            foreach (var team in SystemAPI.Query<RefRO<TeamInfoData>>())
            {
                int localId = math.clamp(team.ValueRO.LocalID, 0, 7);
                teamsByLocalId[localId] = team.ValueRO;
                hasTeamByLocalId[localId] = 1;
            }

            foreach (var (req, reqEntity) in
                SystemAPI.Query<RefRO<SpawnRequest>>().WithEntityAccess())
            {
                int mk     = req.ValueRO.MainKey;
                int vk     = req.ValueRO.VariantKey;
                var prefab = lookup.Get(mk, vk);

                if (prefab == Entity.Null)
                {
                    Debug.LogWarning($"[SpawnSystem] Prefab not found: ({mk}, {vk}).");
                    ecb.DestroyEntity(reqEntity);
                    continue;
                }

                var instance = ecb.Instantiate(prefab);
                ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(
                    req.ValueRO.Position,
                    req.ValueRO.Rotation,
                    req.ValueRO.Scale));

                ApplySpawnTeam(
                    ecb,
                    state.EntityManager,
                    prefab,
                    instance,
                    req.ValueRO.OwnerLocalId,
                    teamsByLocalId,
                    hasTeamByLocalId);

                ecb.AddComponent<MapLoaded>(instance);

                // ── footprint/입구/공급자 승격 (인게임 배치 경로만) ──
                //   HasFootprint=false인 경로(맵 로더 등)는 아래를 건너뛴다.
                if (req.ValueRO.HasFootprint)
                {
                    ecb.AddComponent(instance, new BuildingFootprint
                    {
                        Origin       = req.ValueRO.FootprintOrigin,
                        Size         = req.ValueRO.FootprintSize,
                        RotSteps     = req.ValueRO.RotSteps,
                        OwnerLocalId = req.ValueRO.OwnerLocalId,
                    });

                    if (req.ValueRO.HasEntrance)
                        ecb.AddComponent(instance, new BuildingEntrance
                        {
                            Entrance = req.ValueRO.Entrance,
                        });

                    if (req.ValueRO.IsSupplier)
                        ecb.AddComponent(instance, new StampSupplier
                        {
                            OwnerLocalId = req.ValueRO.OwnerLocalId,
                            Relief       = req.ValueRO.Relief,
                            MaxDist      = req.ValueRO.SupplyMaxDist,
                        });

                    // ── 건물 전투 타겟화: 공격으로 파괴 가능(캡처 후 적 건물 제거의 토대) ──
                    //   타겟 쿼리 요건: CombatTargetable + CombatHealth + TeamInfoData + LocalTransform(스폰 시 부여).
                    //   CombatDestroyOnDeath → 사망 시 CombatDeathSystem이 destroy.
                    //   CombatTargetBounds는 선택(없으면 ResolveAimPosition이 transform 위치로 폴백) → 1차 생략.
                    // 영토 전환 파괴 면제(베이스/HQ) — TerritoryCaptureSystem이 건너뜀.
                    if (req.ValueRO.CaptureExempt)
                        ecb.AddComponent<CaptureExempt>(instance);

                    int ownerLid = math.clamp(req.ValueRO.OwnerLocalId, 0, 7);
                    ecb.AddComponent(instance, new CombatTargetable { TargetType = CombatTargetMask.Building });
                    ecb.AddComponent(instance, new CombatHealth { Health = buildingDefaultHealth, MaxHealth = buildingDefaultHealth });
                    ecb.AddComponent<CombatDestroyOnDeath>(instance);
                    // friend/foe 판정용 팀 — 프리팹에 TeamInfoData 없을 때만 owner 팀으로 부여
                    //   (있으면 위 ApplySpawnTeam이 이미 set).
                    if (hasTeamByLocalId[ownerLid] == 1 && !state.EntityManager.HasComponent<TeamInfoData>(prefab))
                        ecb.AddComponent(instance, teamsByLocalId[ownerLid]);
                }

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            hasTeamByLocalId.Dispose();
            teamsByLocalId.Dispose();
        }

        static void ApplySpawnTeam(
            EntityCommandBuffer ecb,
            EntityManager entityManager,
            Entity prefab,
            Entity instance,
            int ownerLocalId,
            NativeArray<TeamInfoData> teamsByLocalId,
            NativeArray<byte> hasTeamByLocalId)
        {
            if (!entityManager.HasComponent<TeamInfoData>(prefab))
                return;

            int localId = math.clamp(ownerLocalId, 0, 7);
            if (hasTeamByLocalId[localId] == 0)
                return;

            ecb.SetComponent(instance, teamsByLocalId[localId]);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  MultiSpawnSystem — Multi 결정적 랜덤 배치
    //
    //  MultiSpawnRequest 처리:
    //    - Seed 기반 결정적 랜덤 (Unity.Mathematics.Random)
    //    - 1셀 영역 안에 Count개 랜덤 위치 + 랜덤 Y회전
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
                int mk     = req.ValueRO.MainKey;
                int vk     = req.ValueRO.VariantKey;
                var prefab = lookup.Get(mk, vk);

                if (prefab == Entity.Null)
                {
                    Debug.LogWarning($"[MultiSpawnSystem] Prefab not found: ({mk}, {vk}).");
                    ecb.DestroyEntity(reqEntity);
                    continue;
                }

                float cs   = req.ValueRO.CellSize;
                float orgX = req.ValueRO.Cell.x * cs;
                float orgZ = req.ValueRO.Cell.y * cs;
                float h    = req.ValueRO.Height;

                var rng = new Unity.Mathematics.Random((uint)(req.ValueRO.Seed + 1));

                for (int i = 0; i < req.ValueRO.Count; i++)
                {
                    float localX = rng.NextFloat(0f, cs);
                    float localZ = rng.NextFloat(0f, cs);
                    float rotY   = rng.NextFloat(0f, 360f);

                    float3 pos = new float3(orgX + localX, h, orgZ + localZ);

                    var instance = ecb.Instantiate(prefab);
                    ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(
                        pos,
                        quaternion.RotateY(math.radians(rotY)),
                        req.ValueRO.Scale));

                    ecb.AddComponent<MapLoaded>(instance);
                }

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
