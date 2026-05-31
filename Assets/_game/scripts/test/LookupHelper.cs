using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  LookupHelper
    //
    //  L2 → L1(PrefabLookup) 두 단계 조회를 하나의 호출로 제공하는 정적 유틸리티.
    //
    //  호출 측은 L1/L2 구분을 몰라도 된다:
    //    bool ok = LookupHelper.TryGetPrefab(needMask, factionL2, l1, variantKey, out prefab);
    //
    //  생성/파괴가 잦은 프리팹 스폰 시스템에서 NeedMask를 인수로 직접 넘기는 패턴:
    //    SpawnByNeedSystem → LookupHelper.TryGetPrefab(entity.NeedMask, ...) → Instantiate
    // ══════════════════════════════════════════════════════════════════════════
    public static class LookupHelper
    {
        // ── 기본 조회 ───────────────────────────────────────────────────────

        /// <summary>
        /// NeedMask → MainKey (L2) → Prefab Entity (L1 = PrefabLookup) 두 단계 조회.
        /// </summary>
        /// <param name="needMask">조회할 니드 비트 조합</param>
        /// <param name="factionL2">해당 팩션의 NeedLookupL2 컴포넌트</param>
        /// <param name="l1">전역 PrefabLookup 싱글톤</param>
        /// <param name="variantKey">플레이어가 선택한 베리언트 키</param>
        /// <param name="prefab">결과 프리팹 엔티티</param>
        public static bool TryGetPrefab(
            uint needMask,
            in NeedLookupL2 factionL2,
            in PrefabLookup l1,
            int variantKey,
            out Entity prefab)
        {
            prefab = Entity.Null;

            // L2: NeedMask → MainKey
            if (!factionL2.Table.TryGetValue(needMask, out int mainKey))
                return false;

            // L1: (MainKey, VariantKey) → Prefab
            prefab = l1.Get(mainKey, variantKey);
            if (prefab != Entity.Null) return true;

            // 선택한 베리언트가 없으면 V0(기본)으로 폴백
            prefab = l1.Get(mainKey, 0);
            return prefab != Entity.Null;
        }

        // ── MainKey 직접 조회 (L2만 필요한 경우) ──────────────────────────

        public static bool TryGetMainKey(
            uint needMask,
            in NeedLookupL2 factionL2,
            out int mainKey)
            => factionL2.Table.TryGetValue(needMask, out mainKey);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  UpgradeNeedMappingCommand — 업그레이드 명령 (단발성 이벤트 엔티티)
    // ══════════════════════════════════════════════════════════════════════════
    public struct UpgradeNeedMappingCommand : IComponentData
    {
        public int FactionId;   // 어떤 팩션의 L2를 바꿀 것인가
        public uint OldNeedMask; // 기존 키 (0이면 신규 추가)
        public uint NewNeedMask; // 새 키
        public int MainKey;     // 연결할 MainKey
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  UpgradePatchSystem — L2 테이블 업그레이드 적용
    //
    //  UpgradeNeedMappingCommand 이벤트를 받아 특정 팩션의 L2만 수정.
    //  L1(PrefabLookup)은 건드리지 않는다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct UpgradePatchSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder().WithAll<UpgradeNeedMappingCommand>().Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<UpgradeNeedMappingCommand>>().WithEntityAccess())
            {
                var c = cmd.ValueRO;

                foreach (var (fid, lookup) in
                         SystemAPI.Query<RefRO<FactionId>, RefRW<NeedLookupL2>>())
                {
                    if (fid.ValueRO.Value != c.FactionId) continue;

                    ref var table = ref lookup.ValueRW.Table;

                    if (c.OldNeedMask != 0)
                        table.Remove(c.OldNeedMask);

                    if (table.ContainsKey(c.NewNeedMask))
                        table[c.NewNeedMask] = c.MainKey;
                    else
                        table.Add(c.NewNeedMask, c.MainKey);

                    break;
                }

                ecb.DestroyEntity(cmdEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SpawnByNeedCommand / SpawnByNeedSystem — NeedMask 기반 즉시 스폰
    //
    //  생성/파괴가 잦은 유닛/이펙트에 적합한 패턴.
    // ══════════════════════════════════════════════════════════════════════════
    public struct SpawnByNeedCommand : IComponentData
    {
        public uint NeedMask;
        public int FactionId;
        public Unity.Mathematics.float3 Position;
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct SpawnByNeedSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PrefabLookup>();
            state.RequireForUpdate(
                SystemAPI.QueryBuilder().WithAll<SpawnByNeedCommand>().Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var l1 = SystemAPI.GetSingleton<PrefabLookup>();
            var variantKey = SystemAPI.GetSingleton<PlayerVariantSetting>().VariantKey;

            // 팩션별 L2 캐시
            var factionL2Cache = new NativeHashMap<int, NeedLookupL2>(8, Allocator.Temp);
            foreach (var (fid, lookup) in
                     SystemAPI.Query<RefRO<FactionId>, RefRO<NeedLookupL2>>())
                factionL2Cache.TryAdd(fid.ValueRO.Value, lookup.ValueRO);

            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<SpawnByNeedCommand>>().WithEntityAccess())
            {
                var c = cmd.ValueRO;

                if (!factionL2Cache.TryGetValue(c.FactionId, out var l2))
                {
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }

                if (LookupHelper.TryGetPrefab(c.NeedMask, l2, l1, variantKey, out var prefab))
                {
                    var spawned = ecb.Instantiate(prefab);
                    ecb.SetComponent(spawned, new Unity.Transforms.LocalTransform
                    {
                        Position = c.Position,
                        Rotation = Unity.Mathematics.quaternion.identity,
                        Scale = 1f,
                    });
                }

                ecb.DestroyEntity(cmdEntity);
            }

            factionL2Cache.Dispose();
            ecb.Playback(em);
            ecb.Dispose();
        }
    }
}

