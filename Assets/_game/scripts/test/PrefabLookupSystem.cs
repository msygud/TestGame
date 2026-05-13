using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  PrefabLookupInitSystem
    //
    //  서브씬 로드 시 도착하는 PrefabRegistryEntry 버퍼들을
    //  싱글톤 NativeHashMap에 합쳐 빠른 조회 제공.
    //
    //  동작:
    //    - 첫 프레임에 PrefabLookup 싱글톤 생성
    //    - 미처리 버퍼 발견 시 룩업에 추가 (RegistryProcessed로 마크)
    //    - 서브씬 언로드 감지 시 룩업 재빌드
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct PrefabLookupInitSystem : ISystem
    {
        EntityQuery unprocessedQuery;
        EntityQuery allBuffersQuery;
        int         lastBufferCount;

        public void OnCreate(ref SystemState state)
        {
            unprocessedQuery = SystemAPI.QueryBuilder()
                .WithAll<PrefabRegistryEntry>()
                .WithNone<RegistryProcessed>()
                .Build();

            allBuffersQuery = SystemAPI.QueryBuilder()
                .WithAll<PrefabRegistryEntry>()
                .Build();

            // PrefabLookup 싱글톤
            var lookupE = state.EntityManager.CreateEntity(typeof(PrefabLookup));
            state.EntityManager.SetComponentData(lookupE, new PrefabLookup
            {
                Map       = new NativeHashMap<int2, Entity>(256, Allocator.Persistent),
                DlcLookup = new NativeHashMap<int2, int>   (256, Allocator.Persistent),
            });

            // PrefabMetaLookup 싱글톤
            var metaE = state.EntityManager.CreateEntity(typeof(PrefabMetaLookup));
            state.EntityManager.SetComponentData(metaE, new PrefabMetaLookup
            {
                Map = new NativeHashMap<int2, PrefabMetaEntry>(256, Allocator.Persistent),
            });

            lastBufferCount = 0;
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<PrefabLookup>())
            {
                var lookup = SystemAPI.GetSingleton<PrefabLookup>();
                if (lookup.Map.IsCreated)       lookup.Map.Dispose();
                if (lookup.DlcLookup.IsCreated) lookup.DlcLookup.Dispose();
            }
            if (SystemAPI.HasSingleton<PrefabMetaLookup>())
            {
                var meta = SystemAPI.GetSingleton<PrefabMetaLookup>();
                if (meta.Map.IsCreated) meta.Map.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            int currentBufferCount = allBuffersQuery.CalculateEntityCount();

            // 버퍼 수가 줄어듦 = 서브씬 언로드 → 전체 재빌드
            if (currentBufferCount < lastBufferCount)
            {
                RebuildAll(ref state);
            }
            // 신규 버퍼 발견 → 추가만
            else if (!unprocessedQuery.IsEmpty)
            {
                AddNew(ref state);
            }

            lastBufferCount = currentBufferCount;
        }

        // ══════════════════════════════════════════════════════════
        //  추가만 (정상 흐름)
        // ══════════════════════════════════════════════════════════
        void AddNew(ref SystemState state)
        {
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (buf, e) in
                SystemAPI.Query<DynamicBuffer<PrefabRegistryEntry>>()
                         .WithNone<RegistryProcessed>()
                         .WithEntityAccess())
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    var key = new int2(buf[i].MainKey, buf[i].VariantKey);

                    if (!lookup.Map.TryAdd(key, buf[i].Prefab))
                    {
                        Debug.LogWarning(
                            $"[PrefabLookup] Duplicate key: ({key.x}, {key.y}) — ignored.");
                        continue;
                    }

                    lookup.DlcLookup.TryAdd(key, buf[i].DlcKey);
                }

                ecb.AddComponent<RegistryProcessed>(e);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        // ══════════════════════════════════════════════════════════
        //  전체 재빌드 (서브씬 언로드 후)
        // ══════════════════════════════════════════════════════════
        void RebuildAll(ref SystemState state)
        {
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            lookup.Map.Clear();
            lookup.DlcLookup.Clear();

            // 모든 RegistryProcessed 마크 해제
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (buf, e) in
                SystemAPI.Query<DynamicBuffer<PrefabRegistryEntry>>().WithEntityAccess())
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    var key = new int2(buf[i].MainKey, buf[i].VariantKey);

                    if (!lookup.Map.TryAdd(key, buf[i].Prefab))
                    {
                        Debug.LogWarning(
                            $"[PrefabLookup] Duplicate key: ({key.x}, {key.y}) — ignored.");
                        continue;
                    }

                    lookup.DlcLookup.TryAdd(key, buf[i].DlcKey);
                }

                if (state.EntityManager.HasComponent<RegistryProcessed>(e))
                    ecb.RemoveComponent<RegistryProcessed>(e);
                ecb.AddComponent<RegistryProcessed>(e);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
