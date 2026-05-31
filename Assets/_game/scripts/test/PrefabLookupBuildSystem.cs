using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  PrefabLookupBuildSystem
    //
    //  SubScene이 로드될 때마다 BakedPrefabEntry 버퍼를 수집하여
    //  PrefabLookup / PrefabMetaLookup NativeHashMap에 반영한다.
    //
    //  - 이미 처리된 엔티티는 PrefabRegistryProcessed 태그로 구분.
    //  - 싱글플레이에서는 세션 내 SubScene 언로드가 없으므로
    //    처리 후 항목은 게임 종료까지 유지.
    //
    //  실행 순서:
    //    InitializationSystemGroup 내 GridInitSystem 이후.
    //    이후 MapLoadSystem이 PrefabLookup을 사용하므로 반드시 앞서야 함.
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(GridInitSystem))]
    public partial struct PrefabLookupBuildSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // PrefabLookup 싱글톤 생성 (빈 테이블)
            var lookupEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(lookupEntity, new PrefabLookup
            {
                Table        = new NativeHashMap<int2, Entity>(256, Allocator.Persistent),
                LoadedDlcIds = new NativeHashSet<int>(16, Allocator.Persistent),
            });

            // PrefabMetaLookup 싱글톤 생성
            var metaEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(metaEntity, new PrefabMetaLookup
            {
                Table = new NativeHashMap<int2, PrefabMeta>(256, Allocator.Persistent),
            });
        }

        public void OnUpdate(ref SystemState state)
        {
            var unprocessedQuery = SystemAPI.QueryBuilder()
                .WithAll<BakedPrefabEntry>()
                .WithNone<PrefabRegistryProcessed>()
                .Build();

            if (unprocessedQuery.IsEmpty) return;

            var lookup     = SystemAPI.GetSingletonRW<PrefabLookup>();
            var metaLookup = SystemAPI.GetSingletonRW<PrefabMetaLookup>();
            var ecb        = new EntityCommandBuffer(Allocator.Temp);

            int added    = 0;
            int conflict = 0;

            foreach (var (entries, entity) in
                SystemAPI.Query<DynamicBuffer<BakedPrefabEntry>>()
                    .WithNone<PrefabRegistryProcessed>()
                    .WithEntityAccess())
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    var e   = entries[i];
                    var key = new int2(e.MainKey, e.VariantKey);

                    // PrefabLookup 등록
                    if (lookup.ValueRW.Table.TryAdd(key, e.Prefab))
                    {
                        added++;
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[PrefabLookupBuildSystem] 키 충돌 " +
                            $"MainKey={e.MainKey}, VariantKey={e.VariantKey}. " +
                            $"기존 항목 유지.");
                        conflict++;
                    }

                    // PrefabMetaLookup 등록
                    metaLookup.ValueRW.Table.TryAdd(key, new PrefabMeta
                    {
                        Size         = e.Size,
                        Offset       = e.Offset,
                        RoadMask     = e.RoadMask,
                        MultiCount   = e.MultiCount,
                        MultiItemSize = e.MultiItemSize,
                    });

                    // DLC ID 기록
                    lookup.ValueRW.LoadedDlcIds.Add(e.DlcId);
                }

                ecb.AddComponent<PrefabRegistryProcessed>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            if (added > 0)
                UnityEngine.Debug.Log(
                    $"[PrefabLookupBuildSystem] {added}개 등록" +
                    (conflict > 0 ? $", {conflict}개 충돌 스킵" : ""));
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
        }
    }
}
