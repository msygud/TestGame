using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadKeyAuthoring — 도로 키 매핑 베이킹
    //
    //  서브씬에 하나만 배치.
    //  등록된 RoadPrefabRegistry SO들의 Entries를 BakedRoadKey 버퍼로 구워 넣는다.
    //  이후 RoadKeyBuildSystem이 이 버퍼를 읽어 RoadKeyLookup 싱글톤을 구성.
    //
    //  SO가 추가/변경되면 이 Authoring에 레퍼런스를 추가하고 SubScene Re-bake.
    //  (NeedMappingAuthoring과 동일한 패턴.)
    // ══════════════════════════════════════════════════════════════════════════
    public class RoadKeyAuthoring : MonoBehaviour
    {
        [Tooltip("도로 (팩션,방향)→MainKey 매핑을 가진 RoadPrefabRegistry SO들")]
        public List<RoadPrefabRegistry> Registries = new();

        class Baker : Baker<RoadKeyAuthoring>
        {
            public override void Bake(RoadKeyAuthoring a)
            {
                var e   = GetEntity(TransformUsageFlags.None);
                var buf = AddBuffer<BakedRoadKey>(e);

                foreach (var reg in a.Registries)
                {
                    if (reg == null) continue;
                    foreach (var entry in reg.Entries)
                    {
                        if (entry == null) continue;
                        if (entry.Dir == RoadDir.None) continue;       // 0은 도로 아님
                        if (entry.MainKey == MainKeyRange.NullKey) continue; // 무효 키 스킵

                        buf.Add(new BakedRoadKey
                        {
                            FactionId = entry.FactionId,
                            Dir       = entry.Dir,
                            MainKey   = entry.MainKey,
                        });
                    }
                }
            }
        }
    }
}

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadKeyBuildSystem — BakedRoadKey 버퍼 → RoadKeyLookup 싱글톤 구성
    //
    //  게임 시작 시 1회. 베이크된 도로 매핑을 NativeHashMap으로 펼쳐
    //  RoadKeyLookup 싱글톤을 만든다. 이후 도로 시스템이 이걸 참조해
    //  (FactionId, dirMask) → MainKey 를 조회.
    //
    //  팩션은 게임 시작 시 고정 → 이 테이블도 시작 시 1회만 구성.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct RoadKeyBuildSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // BakedRoadKey 버퍼가 존재할 때만 동작
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<BakedRoadKey>()
                    .Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 이미 구성됐으면 재실행 안 함
            if (SystemAPI.HasSingleton<RoadKeyLookup>())
            {
                state.Enabled = false;
                return;
            }

            // 베이크 버퍼 수집
            var baked = new NativeList<BakedRoadKey>(Allocator.Temp);
            foreach (var buf in SystemAPI.Query<DynamicBuffer<BakedRoadKey>>())
            {
                for (int i = 0; i < buf.Length; i++)
                    baked.Add(buf[i]);
            }

            var table = new NativeHashMap<int, int>(
                baked.Length > 0 ? baked.Length : 1,
                Allocator.Persistent);

            for (int i = 0; i < baked.Length; i++)
            {
                var b   = baked[i];
                int key = RoadKeyLookup.Pack(b.FactionId, b.Dir);
                // 중복은 마지막 항목 우선(Validate에서 이미 경고). TryAdd 후 덮어쓰기.
                if (!table.TryAdd(key, b.MainKey))
                    table[key] = b.MainKey;
            }

            baked.Dispose();

            var e = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(e, new RoadKeyLookup { Table = table });

            state.Enabled = false; // 1회로 끝
        }
    }
}
