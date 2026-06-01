using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  LookupHelper
    //
    //  L2(NeedLookupL2) → L1(PrefabLookup) 두 단계 조회를 하나의 호출로 제공.
    //
    //  오버로드 두 가지:
    //    ① int variantKey        — VariantKey를 직접 지정 (레거시 호환)
    //    ② VariantProfile + who  — SlotController(User/AI) 기반 자동 해결
    //
    //  호출 측은 L1/L2 구분을 몰라도 된다.
    // ══════════════════════════════════════════════════════════════════════════
    public static class LookupHelper
    {
        // ── ① 직접 지정 오버로드 (레거시 호환) ────────────────────────────

        /// <summary>
        /// NeedMask → MainKey (L2) → Prefab Entity (L1) 두 단계 조회.
        /// VariantKey를 직접 지정. 없으면 V0 폴백.
        /// </summary>
        public static bool TryGetPrefab(
            uint needMask,
            in NeedLookupL2 factionL2,
            in PrefabLookup l1,
            int variantKey,
            out Entity prefab)
        {
            prefab = Entity.Null;

            if (!factionL2.Table.TryGetValue(needMask, out int mainKey))
                return false;

            prefab = l1.Get(mainKey, variantKey);
            if (prefab != Entity.Null) return true;

            // 선택한 베리언트 없으면 V0(기본)으로 폴백
            prefab = l1.Get(mainKey, 0);
            return prefab != Entity.Null;
        }

        // ── ② VariantProfile 오버로드 ─────────────────────────────────────

        /// <summary>
        /// NeedMask → MainKey (L2) → Prefab Entity (L1) 두 단계 조회.
        /// VariantKey는 VariantProfile.Resolve(mainKey, who) 로 자동 결정.
        /// 해결된 베리언트가 없으면 V0 폴백.
        /// </summary>
        /// <param name="needMask">조회할 니드 비트 조합.</param>
        /// <param name="factionL2">해당 팩션의 NeedLookupL2.</param>
        /// <param name="l1">전역 PrefabLookup 싱글톤.</param>
        /// <param name="profile">세션 베리언트 설정 (VariantProfile 싱글톤).</param>
        /// <param name="who">User 또는 AI — VariantProfile 조회 키.</param>
        /// <param name="prefab">결과 프리팹 엔티티.</param>
        public static bool TryGetPrefab(
            uint needMask,
            in NeedLookupL2 factionL2,
            in PrefabLookup l1,
            in VariantProfile profile,
            SlotController who,
            out Entity prefab)
        {
            prefab = Entity.Null;

            if (!factionL2.Table.TryGetValue(needMask, out int mainKey))
                return false;

            int vk = profile.Resolve(mainKey, who);

            prefab = l1.Get(mainKey, vk);
            if (prefab != Entity.Null) return true;

            // V0 폴백
            prefab = l1.Get(mainKey, 0);
            return prefab != Entity.Null;
        }

        // ── MainKey 직접 조회 ──────────────────────────────────────────────

        /// <summary>NeedMask → MainKey 조회 (L2만 필요한 경우).</summary>
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
        public int  FactionId;   // 어떤 팩션의 L2를 바꿀 것인가
        public uint OldNeedMask; // 기존 키 (0이면 신규 추가)
        public uint NewNeedMask; // 새 키
        public int  MainKey;     // 연결할 MainKey
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
                SystemAPI.QueryBuilder()
                    .WithAll<UpgradeNeedMappingCommand>()
                    .Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<UpgradeNeedMappingCommand>>()
                         .WithEntityAccess())
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
    //  SpawnByNeedCommand — NeedMask 기반 즉시 스폰 명령
    //
    //  생성/파괴가 잦은 유닛·이펙트 스폰에 적합.
    //  Who 필드로 User/AI를 구분해 VariantProfile에서 올바른 베리언트 해결.
    // ══════════════════════════════════════════════════════════════════════════
    public struct SpawnByNeedCommand : IComponentData
    {
        public uint          NeedMask;
        public int           FactionId;
        public float3        Position;
        /// <summary>
        /// 이 스폰이 유저 팀 유닛인지 AI 팀 유닛인지.
        /// VariantProfile.Resolve(mainKey, Who) 호출에 사용.
        /// </summary>
        public SlotController Who;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SpawnByNeedSystem — SpawnByNeedCommand 처리
    //
    //  VariantProfile 싱글톤에서 Who(User/AI)에 맞는 VariantKey를 해결.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct SpawnByNeedSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PrefabLookup>();
            state.RequireForUpdate<VariantProfile>();
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<SpawnByNeedCommand>()
                    .Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            var em      = state.EntityManager;
            var ecb     = new EntityCommandBuffer(Allocator.Temp);
            var l1      = SystemAPI.GetSingleton<PrefabLookup>();
            var profile = SystemAPI.GetSingleton<VariantProfile>();

            // 팩션별 L2 캐시
            var factionL2Cache =
                new NativeHashMap<int, NeedLookupL2>(8, Allocator.Temp);
            foreach (var (fid, lookup) in
                     SystemAPI.Query<RefRO<FactionId>, RefRO<NeedLookupL2>>())
                factionL2Cache.TryAdd(fid.ValueRO.Value, lookup.ValueRO);

            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<SpawnByNeedCommand>>()
                         .WithEntityAccess())
            {
                var c = cmd.ValueRO;

                if (!factionL2Cache.TryGetValue(c.FactionId, out var l2))
                {
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }

                // VariantProfile + SlotController(Who) 기반 해결
                if (LookupHelper.TryGetPrefab(
                    c.NeedMask, l2, l1, profile, c.Who, out var prefab))
                {
                    var spawned = ecb.Instantiate(prefab);
                    ecb.SetComponent(spawned,
                        new Unity.Transforms.LocalTransform
                        {
                            Position = c.Position,
                            Rotation = quaternion.identity,
                            Scale    = 1f,
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
