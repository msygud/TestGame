using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  PrefabLookupBuildSystem
    //
    //  SubScene이 로드될 때마다 베이킹 버퍼를 수집하여 룩업에 반영:
    //    BakedPrefabEntry    → PrefabLookup + PrefabMetaLookup
    //    BakedEntranceEntry  → EntranceLookup
    //
    //  - 처리 완료 엔티티는 PrefabRegistryProcessed 태그로 구분.
    //  - 두 버퍼는 같은 엔티티(GamePrefabRegistryAuthoring)에 함께 존재.
    //
    //  실행 순서:
    //    InitializationSystemGroup 내 GridInitSystem 이후.
    //    이후 MapLoadSystem 등이 룩업을 사용하므로 반드시 앞서야 함.
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(GridInitSystem))]
    public partial struct PrefabLookupBuildSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var em = state.EntityManager;

            // PrefabLookup 싱글톤
            var lookupEntity = em.CreateEntity();
            em.AddComponentData(lookupEntity, new PrefabLookup
            {
                Table        = new NativeHashMap<int2, Entity>(256, Allocator.Persistent),
                LoadedDlcIds = new NativeHashSet<int>(16, Allocator.Persistent),
            });

            // PrefabMetaLookup 싱글톤
            var metaEntity = em.CreateEntity();
            em.AddComponentData(metaEntity, new PrefabMetaLookup
            {
                Table = new NativeHashMap<int2, PrefabMeta>(256, Allocator.Persistent),
            });

            // EntranceLookup 싱글톤
            var entranceEntity = em.CreateEntity();
            em.AddComponentData(entranceEntity, new EntranceLookup
            {
                Table = new NativeHashMap<int, FixedList64Bytes<int2>>(64, Allocator.Persistent),
            });
        }

        public void OnUpdate(ref SystemState state)
        {
            var unprocessedQuery = SystemAPI.QueryBuilder()
                .WithAll<BakedPrefabEntry>()
                .WithNone<PrefabRegistryProcessed>()
                .Build();

            if (unprocessedQuery.IsEmpty) return;

            var lookup         = SystemAPI.GetSingletonRW<PrefabLookup>();
            var metaLookup     = SystemAPI.GetSingletonRW<PrefabMetaLookup>();
            var entranceLookup = SystemAPI.GetSingletonRW<EntranceLookup>();
            var ecb            = new EntityCommandBuffer(Allocator.Temp);

            int added    = 0;
            int conflict = 0;
            int entrances = 0;

            foreach (var (entries, entity) in
                SystemAPI.Query<DynamicBuffer<BakedPrefabEntry>>()
                    .WithNone<PrefabRegistryProcessed>()
                    .WithEntityAccess())
            {
                // ── 프리팹 + 메타 등록 ────────────────────────────
                for (int i = 0; i < entries.Length; i++)
                {
                    var e   = entries[i];
                    var key = new int2(e.MainKey, e.VariantKey);

                    if (lookup.ValueRW.Table.TryAdd(key, e.Prefab))
                    {
                        added++;
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[PrefabLookupBuildSystem] 키 충돌 " +
                            $"MainKey={e.MainKey}, VariantKey={e.VariantKey}. 기존 항목 유지.");
                        conflict++;
                    }

                    // PrefabMeta 등록 (Category·BuildableOn 포함 — 이전 누락 수정)
                    metaLookup.ValueRW.Table.TryAdd(key, new PrefabMeta
                    {
                        Size          = e.Size,
                        Offset        = e.Offset,
                        RoadMask      = e.RoadMask,
                        MultiCount    = e.MultiCount,
                        MultiItemSize = e.MultiItemSize,
                        BuildableOn   = e.BuildableOn,
                        Category      = e.Category,
                    });

                    lookup.ValueRW.LoadedDlcIds.Add(e.DlcId);
                }

                // ── 입구 등록 (같은 엔티티의 BakedEntranceEntry) ──
                if (SystemAPI.HasBuffer<BakedEntranceEntry>(entity))
                {
                    var entranceEntries = SystemAPI.GetBuffer<BakedEntranceEntry>(entity);
                    for (int i = 0; i < entranceEntries.Length; i++)
                    {
                        var en = entranceEntries[i];

                        entranceLookup.ValueRW.Table.TryGetValue(en.MainKey, out var list);
                        if (list.Length < list.Capacity)
                        {
                            list.Add(en.Offset);
                            entranceLookup.ValueRW.Table[en.MainKey] = list;
                            entrances++;
                        }
                        else
                        {
                            UnityEngine.Debug.LogWarning(
                                $"[PrefabLookupBuildSystem] 입구 초과(MainKey={en.MainKey}). " +
                                $"최대 {list.Capacity}개까지만 등록.");
                        }
                    }
                }

                ecb.AddComponent<PrefabRegistryProcessed>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            if (added > 0)
                UnityEngine.Debug.Log(
                    $"[PrefabLookupBuildSystem] 프리팹 {added}개 등록" +
                    (conflict  > 0 ? $", {conflict}개 충돌 스킵" : "") +
                    (entrances > 0 ? $", 입구 {entrances}개 등록" : ""));
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<PrefabLookup>())
            {
                var l = SystemAPI.GetSingleton<PrefabLookup>();
                if (l.Table.IsCreated)        l.Table.Dispose();
                if (l.LoadedDlcIds.IsCreated) l.LoadedDlcIds.Dispose();
            }

            if (SystemAPI.HasSingleton<PrefabMetaLookup>())
            {
                var m = SystemAPI.GetSingleton<PrefabMetaLookup>();
                if (m.Table.IsCreated) m.Table.Dispose();
            }

            if (SystemAPI.HasSingleton<EntranceLookup>())
            {
                var en = SystemAPI.GetSingleton<EntranceLookup>();
                if (en.Table.IsCreated) en.Table.Dispose();
            }
        }
    }
}
