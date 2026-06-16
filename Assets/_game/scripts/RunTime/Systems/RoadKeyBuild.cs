using Unity.Collections;
using Unity.Entities;

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
            var sizeByFaction = new NativeHashMap<int, byte>(
                baked.Length > 0 ? baked.Length : 1,
                Allocator.Persistent);

            for (int i = 0; i < baked.Length; i++)
            {
                var b   = baked[i];
                int key = RoadKeyLookup.Pack(b.FactionId, b.Dir);
                // 중복은 마지막 항목 우선(Validate에서 이미 경고). TryAdd 후 덮어쓰기.
                if (!table.TryAdd(key, b.MainKey))
                    table[key] = b.MainKey;

                // FactionId당 도로 크기(레지스트리 DefaultSize). 첫 항목 우선.
                sizeByFaction.TryAdd(b.FactionId, b.Size <= 0 ? (byte)1 : b.Size);
            }

            baked.Dispose();

            var e = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(e, new RoadKeyLookup
            {
                Table         = table,
                SizeByFaction = sizeByFaction,
            });

            state.Enabled = false; // 1회로 끝
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<RoadKeyLookup>()) return;
            var lookup = SystemAPI.GetSingleton<RoadKeyLookup>();
            if (lookup.Table.IsCreated)         lookup.Table.Dispose();
            if (lookup.SizeByFaction.IsCreated) lookup.SizeByFaction.Dispose();
        }
    }
}
