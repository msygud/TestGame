using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════
    //  StampSupplier  — 공급자 식별용 임시 컴포넌트.
    //
    //  임시 단계: Relief(NeedType)와 입구 정보를 엔티티에 직접 박아 BFS가
    //  바로 읽게 한다. 나중에 생산/소비 레이어 컴포넌트
    //  (ServiceRelief / EntranceInfo 캐시 등)로 흡수하면서 제거 예정.
    //
    //  BFS 시작점 = EntranceOps.EntranceRoadCell(Origin, Size, Entrance, RotSteps).
    // ══════════════════════════════════════════════════════════════════
    public struct StampSupplier : IComponentData
    {
        /// <summary>이 공급자를 소유한 플레이어 (0~7). 자기 도로망에만 도장.</summary>
        public int OwnerLocalId;

        /// <summary>footprint 원점(좌하단 실셀).</summary>
        public int2 Origin;

        /// <summary>footprint 원본 크기 (회전 정규화용).</summary>
        public int2 Size;

        /// <summary>입구 정보 (Offset 셀 + Dir 방향).</summary>
        public EntranceInfo Entrance;

        /// <summary>현재 회전 스텝 (0~3). 배치 시 확정된 값.</summary>
        public int RotSteps;

        /// <summary>이 공급자가 해소하는 Need 조합 (임시 직접 값).</summary>
        public NeedType Relief;

        /// <summary>도달 범위 상한 (BFS 최대 거리, 도로 칸 수). 0 이하면 무제한.</summary>
        public int MaxDist;
    }

    // ══════════════════════════════════════════════════════════════════
    //  SupplierStampSystem  — stamp 재빌드 (라운드로빈 + dirty).
    //
    //  매 저빈도 틱:
    //    ① dirty한 플레이어를 라운드로빈으로 하나(또는 소수) 고른다.
    //    ② 그 플레이어 맵을 Clear.
    //    ③ 그 플레이어 소유 공급자들을 모아, 각자 입구 도로셀에서 BFS.
    //       도장(SupplierRef{공급자, Relief, dist})을 도로셀마다 Add.
    //    ④ dirty 비트 해제.
    //
    //  메모리 ⑤/⑤-보충 준수:
    //    · 연결 아님, 도달 범위 다시 그리기 (매번 Clear 후 재BFS = 무효화 회피).
    //    · 다중 소스 확산: 한 플레이어의 모든 공급자가 같은 맵에 겹쳐 찍는다.
    //    · 가까운 공급자 우선 = SupplierRef.Dist에 BFS 거리 기록 (수급자가 정렬).
    //    · Capacity는 stamp에 안 박음 (예약 시 BuildingOccupancy 직접 조회).
    //
    //  현재는 메인스레드 BFS (저빈도라 충분). 병목 시 IJob 분할 가능.
    // ══════════════════════════════════════════════════════════════════
    [BurstCompile]
    public partial struct SupplierStampSystem : ISystem
    {
        // 라운드로빈 커서: 다음에 검사 시작할 플레이어 슬롯.
        int _cursor;

        public void OnCreate(ref SystemState state)
        {
            // 골격 싱글톤 생성 (한 번).
            var maps = new SupplierStampMaps();
            maps.AllocateAll(StampConfig.InitialCapacity, Allocator.Persistent);

            var mapsEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(mapsEntity, maps);

            // 초기엔 전 플레이어 dirty (첫 틱에 1회 빌드).
            state.EntityManager.AddComponentData(mapsEntity, new StampDirty { Mask = 0xFF });

            _cursor = 0;

            // 공급자/맵이 있어야 의미 있음.
            state.RequireForUpdate<SupplierStampMaps>();
            state.RequireForUpdate<GridLayers>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonRW<SupplierStampMaps>(out var mapsRW))
                mapsRW.ValueRW.DisposeAll();
        }

        public void OnUpdate(ref SystemState state)
        {
            ref var dirty = ref SystemAPI.GetSingletonRW<StampDirty>().ValueRW;
            if (!dirty.AnyDirty)
                return; // 재빌드할 플레이어 없음.

            // ── ① 라운드로빈으로 dirty한 플레이어 1명 선택 ──────────────
            int target = -1;
            for (int i = 0; i < StampConfig.MaxPlayers; i++)
            {
                int p = (_cursor + i) % StampConfig.MaxPlayers;
                if (dirty.IsDirty(p))
                {
                    target = p;
                    _cursor = (p + 1) % StampConfig.MaxPlayers; // 다음 틱은 그 다음부터.
                    break;
                }
            }
            if (target < 0)
                return;

            var maps = SystemAPI.GetSingleton<SupplierStampMaps>();
            var map = maps.Get(target);   // 핸들 값 복사 — 같은 버퍼 가리킴.
            var roadLayer = SystemAPI.GetSingleton<GridLayers>().RoadLayer;

            // ── ② 그 플레이어 맵 Clear (도달 범위 다시 그리기) ──────────
            map.Clear();

            // ── ③ 그 플레이어 소유 공급자 전수 → 각자 BFS ──────────────
            //   (공급자 수는 시민보다 압도적으로 적어 전수 스캔 + 값 비교로 충분 — 메모리 원칙.)
            //   재사용 버퍼: BFS 프런티어 큐 + 방문 집합.
            var queue = new NativeQueue<int2>(Allocator.Temp);
            var visited = new NativeHashMap<int2, int>(StampConfig.InitialCapacity, Allocator.Temp);

            foreach (var (supplier, entity) in
                     SystemAPI.Query<RefRO<StampSupplier>>().WithEntityAccess())
            {
                if (supplier.ValueRO.OwnerLocalId != target)
                    continue;

                StampOne(in supplier.ValueRO, entity, target, ref map, in roadLayer,
                         ref queue, ref visited);
            }

            visited.Dispose();
            queue.Dispose();

            // ── ④ dirty 해제 ──────────────────────────────────────────
            dirty.ClearDirty(target);
        }

        // ──────────────────────────────────────────────────────────────
        //  공급자 1개 BFS — 입구 도로셀에서 시작해 자기 소유 도로를 4방 확산.
        //
        //  · 시작점이 도로가 아니면(입구가 도로에 안 닿음) 즉시 종료 — 배치
        //    검증을 통과했어야 정상이나, 런타임에 도로가 헐릴 수 있으므로 방어.
        //  · 방문 셀마다 SupplierRef(공급자, Relief, dist) 도장.
        //  · 같은 셀에 다른 공급자가 이미 있어도 무관 (MultiHashMap = 누적).
        //  · 같은 공급자가 같은 셀 재방문은 visited로 차단 (최단거리 먼저 도달).
        // ──────────────────────────────────────────────────────────────
        static void StampOne(
            in StampSupplier s,
            Entity supplierEntity,
            int owner,
            ref NativeParallelMultiHashMap<int2, SupplierRef> map,
            in NativeHashMap<int2, RoadCell> roadLayer,
            ref NativeQueue<int2> queue,
            ref NativeHashMap<int2, int> visited)
        {
            int2 start = EntranceOps.EntranceRoadCell(s.Origin, s.Size, in s.Entrance, s.RotSteps);

            // 시작 도로셀이 없거나 내 소유가 아니면 도장 불가.
            if (!IsOwnedRoad(start, owner, in roadLayer))
                return;

            queue.Clear();
            visited.Clear();

            queue.Enqueue(start);
            visited[start] = 0;

            // 4방 오프셋.
            var dirs = new NativeArray<int2>(4, Allocator.Temp);
            dirs[0] = new int2(0, 1);   // N
            dirs[1] = new int2(1, 0);   // E
            dirs[2] = new int2(0, -1);  // S
            dirs[3] = new int2(-1, 0);  // W

            while (queue.TryDequeue(out int2 cell))
            {
                int dist = visited[cell];

                // 도장 찍기.
                map.Add(cell, new SupplierRef
                {
                    Supplier = supplierEntity,
                    Relief   = s.Relief,
                    Dist     = dist,
                });

                // 거리 상한 도달 시 더 확장 안 함.
                if (s.MaxDist > 0 && dist >= s.MaxDist)
                    continue;

                for (int d = 0; d < 4; d++)
                {
                    int2 next = cell + dirs[d];
                    if (visited.ContainsKey(next))
                        continue;
                    if (!IsOwnedRoad(next, owner, in roadLayer))
                        continue;

                    visited[next] = dist + 1;
                    queue.Enqueue(next);
                }
            }

            dirs.Dispose();
        }

        /// <summary>그 셀이 도로 레이어에 있고, owner 소유인가.</summary>
        static bool IsOwnedRoad(int2 cell, int owner,
                                in NativeHashMap<int2, RoadCell> roadLayer)
        {
            return roadLayer.TryGetValue(cell, out var rc) && rc.OwnerLocalId == owner;
        }
    }
}
