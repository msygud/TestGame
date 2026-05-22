using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct PrefabLookupInitSystem : ISystem
    {
        EntityQuery allRegistriesQuery;
        EntityQuery unprocessedRegistriesQuery;
        int lastRegistryCount;

        public void OnCreate(ref SystemState state)
        {
            allRegistriesQuery = SystemAPI.QueryBuilder()
                .WithAll<PrefabRegistryEntry>()
                .Build();

            unprocessedRegistriesQuery = SystemAPI.QueryBuilder()
                .WithAll<PrefabRegistryEntry>()
                .WithNone<RegistryProcessed>()
                .Build();

            var lookupEntity = state.EntityManager.CreateEntity(typeof(PrefabLookup));
            state.EntityManager.SetComponentData(lookupEntity, new PrefabLookup
            {
                Map = new NativeHashMap<int2, Entity>(256, Allocator.Persistent),
                DlcLookup = new NativeHashMap<int2, int>(256, Allocator.Persistent),
                RoadMap = new NativeHashMap<int2, Entity>(256, Allocator.Persistent),
            });

            var metaEntity = state.EntityManager.CreateEntity(typeof(PrefabMetaLookup));
            state.EntityManager.SetComponentData(metaEntity, new PrefabMetaLookup
            {
                Map = new NativeHashMap<int2, PrefabMetaEntry>(256, Allocator.Persistent),
            });
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<PrefabLookup>())
            {
                var lookup = SystemAPI.GetSingleton<PrefabLookup>();
                if (lookup.Map.IsCreated) lookup.Map.Dispose();
                if (lookup.DlcLookup.IsCreated) lookup.DlcLookup.Dispose();
                if (lookup.RoadMap.IsCreated) lookup.RoadMap.Dispose();
            }

            if (SystemAPI.HasSingleton<PrefabMetaLookup>())
            {
                var metaLookup = SystemAPI.GetSingleton<PrefabMetaLookup>();
                if (metaLookup.Map.IsCreated) metaLookup.Map.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            int currentRegistryCount = allRegistriesQuery.CalculateEntityCount();
            bool registrySetChanged = currentRegistryCount != lastRegistryCount;
            bool hasNewRegistry = !unprocessedRegistriesQuery.IsEmpty;

            if (registrySetChanged || hasNewRegistry)
                RebuildAll(ref state);

            lastRegistryCount = currentRegistryCount;
        }

        void RebuildAll(ref SystemState state)
        {
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            var metaLookup = SystemAPI.GetSingleton<PrefabMetaLookup>();

            int totalEntries = CountRegistryEntries(ref state);
            EnsureCapacity(ref lookup, ref metaLookup, totalEntries);

            lookup.Map.Clear();
            lookup.DlcLookup.Clear();
            lookup.RoadMap.Clear();
            metaLookup.Map.Clear();

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (registryBuffer, registryEntity) in
                SystemAPI.Query<DynamicBuffer<PrefabRegistryEntry>>().WithEntityAccess())
            {
                for (int i = 0; i < registryBuffer.Length; i++)
                    AddRegistryEntry(lookup, registryBuffer[i]);

                AddMetaEntries(ref state, metaLookup, registryEntity);

                if (!state.EntityManager.HasComponent<RegistryProcessed>(registryEntity))
                    ecb.AddComponent<RegistryProcessed>(registryEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            LogLookupSummary(lookup, totalEntries, allRegistriesQuery.CalculateEntityCount());
        }

        int CountRegistryEntries(ref SystemState state)
        {
            int count = 0;
            foreach (var registryBuffer in SystemAPI.Query<DynamicBuffer<PrefabRegistryEntry>>())
                count += registryBuffer.Length;
            return count;
        }

        static void EnsureCapacity(
            ref PrefabLookup lookup,
            ref PrefabMetaLookup metaLookup,
            int totalEntries)
        {
            int capacity = math.max(256, math.ceilpow2(math.max(1, totalEntries * 2)));

            if (lookup.Map.IsCreated && lookup.Map.Capacity < capacity)
                lookup.Map.Capacity = capacity;

            if (lookup.DlcLookup.IsCreated && lookup.DlcLookup.Capacity < capacity)
                lookup.DlcLookup.Capacity = capacity;

            if (lookup.RoadMap.IsCreated && lookup.RoadMap.Capacity < capacity)
                lookup.RoadMap.Capacity = capacity;

            if (metaLookup.Map.IsCreated && metaLookup.Map.Capacity < capacity)
                metaLookup.Map.Capacity = capacity;
        }

        static void AddRegistryEntry(PrefabLookup lookup, PrefabRegistryEntry entry)
        {
            var key = new int2(entry.MainKey, entry.VariantKey);

            if (!lookup.Map.TryAdd(key, entry.Prefab))
            {
                Debug.LogWarning(
                    $"[PrefabLookup] Duplicate key ({key.x}, {key.y}) ignored. " +
                    "Content modules must use globally unique MainKey/VariantKey pairs.");
            }
            else
            {
                lookup.DlcLookup.TryAdd(key, entry.DlcKey);
            }

            if (entry.RoadDirectionMask == 0)
                return;

            var roadKey = new int2(entry.RoadDirectionMask, entry.VariantKey);
            if (!lookup.RoadMap.TryAdd(roadKey, entry.Prefab))
            {
                Debug.LogWarning(
                    $"[PrefabLookup] Duplicate road key directions={roadKey.x}, variant={roadKey.y} ignored.");
            }
        }

        static void AddMetaEntries(
            ref SystemState state,
            PrefabMetaLookup metaLookup,
            Entity registryEntity)
        {
            if (!state.EntityManager.HasBuffer<PrefabMetaEntry>(registryEntity))
                return;

            var metaBuffer = state.EntityManager.GetBuffer<PrefabMetaEntry>(registryEntity);
            for (int i = 0; i < metaBuffer.Length; i++)
            {
                var entry = metaBuffer[i];
                var key = new int2(entry.MainKey, entry.VariantKey);

                if (!metaLookup.Map.TryAdd(key, entry))
                {
                    Debug.LogWarning(
                        $"[PrefabMetaLookup] Duplicate key ({key.x}, {key.y}) ignored.");
                }
            }
        }

        static void LogLookupSummary(
            PrefabLookup lookup,
            int totalEntries,
            int registryEntityCount)
        {
            var dlcIds = new NativeHashSet<int>(16, Allocator.Temp);
            foreach (var kv in lookup.DlcLookup)
                dlcIds.Add(kv.Value);

            var dlcList = new FixedString512Bytes();
            foreach (int dlcId in dlcIds)
            {
                if (dlcList.Length > 0)
                    dlcList.Append(", ");
                dlcList.Append(dlcId);
            }

            Debug.Log(
                $"[PrefabLookup] Rebuilt from {registryEntityCount} registry entity/entities, " +
                $"entries={totalEntries}, map={lookup.Map.Count}, roads={lookup.RoadMap.Count}, dlcs=[{dlcList}].");

            dlcIds.Dispose();
        }
    }
}
