using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  LookupBuildSystem
    //
    //  게임 진입 시 1회 실행. 이후 비활성화.
    //
    //  처리 순서:
    //  1) BakedNeedMapping 버퍼 → 공통/팩션별 매핑 분류
    //  2) 팩션 엔티티마다 NeedLookupL2 부착
    //     - FactionFlags==0 항목 → 모든 팩션에 복사
    //     - FactionFlags!=0 항목 → 해당 팩션 비트가 켜진 팩션에만 복사
    //
    //  L1은 PrefabLookup 싱글톤(PrefabLookupBuildSystem이 이미 구성)을
    //  그대로 공유한다. 별도 PrefabLookupL1 불필요.
    //
    //  L1과 L2는 서로를 전혀 모른다 — 조합은 LookupHelper만 안다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(PrefabLookupBuildSystem))]
    public partial struct LookupBuildSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder().WithAll<BakedNeedMapping>().Build());
            // PrefabLookup이 준비된 뒤 실행
            state.RequireForUpdate<PrefabLookup>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var em  = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // ── 1) BakedNeedMapping 수집 ───────────────────────────────────
            var commonEntries  = new NativeList<BakedNeedMapping>(32, Allocator.Temp);
            var factionEntries = new NativeList<BakedNeedMapping>(64, Allocator.Temp);

            foreach (var buf in SystemAPI.Query<DynamicBuffer<BakedNeedMapping>>())
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    var entry = buf[i];
                    if (entry.FactionFlags == 0) commonEntries.Add(entry);
                    else                          factionEntries.Add(entry);
                }
            }

            // ── 2) 팩션 엔티티마다 L2 구성 ────────────────────────────────
            foreach (var (fid, e) in
                     SystemAPI.Query<RefRO<FactionId>>().WithEntityAccess())
            {
                var table = new NativeHashMap<uint, int>(64, Allocator.Persistent);

                // 공통 항목 — 모든 팩션에 복사
                for (int i = 0; i < commonEntries.Length; i++)
                    TryAddOrWarn(ref table, commonEntries[i].NeedMask, commonEntries[i].MainKey);

                // 팩션 전용 항목 — 내 비트가 켜진 것만
                uint myBit = FactionBit(fid.ValueRO.Value);
                for (int i = 0; i < factionEntries.Length; i++)
                {
                    var entry = factionEntries[i];
                    if ((entry.FactionFlags & myBit) != 0)
                        TryAddOrWarn(ref table, entry.NeedMask, entry.MainKey);
                }

                ecb.AddComponent(e, new NeedLookupL2 { Table = table });
            }

            ecb.Playback(em);
            ecb.Dispose();
            commonEntries.Dispose();
            factionEntries.Dispose();

            // 1회 실행 후 비활성화
            state.Enabled = false;
        }

        public void OnDestroy(ref SystemState state)
        {
            // L2 해제 — 팩션 엔티티별
            foreach (var lookup in SystemAPI.Query<RefRW<NeedLookupL2>>())
            {
                if (lookup.ValueRO.Table.IsCreated)
                    lookup.ValueRW.Table.Dispose();
            }
        }

        // ── 헬퍼 ───────────────────────────────────────────────────────────

        static uint FactionBit(int factionId) =>
            factionId > 0 ? (1u << (factionId - 1)) : 0u;

        static void TryAddOrWarn(ref NativeHashMap<uint, int> table, uint needMask, int mainKey)
        {
            if (!table.TryAdd(needMask, mainKey))
            {
                UnityEngine.Debug.LogWarning(
                    $"[LookupBuildSystem] NeedMask {needMask:X8} 중복 등록. " +
                    $"기존 MainKey={table[needMask]}, 신규 MainKey={mainKey} 무시됨.");
            }
        }
    }
}
