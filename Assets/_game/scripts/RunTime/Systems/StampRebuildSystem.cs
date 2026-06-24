using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════
    //  StampSupplier  — "이 건물은 공급자다" 표식 (축소판)
    // ──────────────────────────────────────────────────────────────────────
    //  footprint/입구는 BuildingFootprint/BuildingEntrance에서 읽으므로
    //  여기엔 공급자 고유 의미만 남긴다(중복 제거).
    //  BFS 시작점 = EntranceOps.EntranceRoadCell(footprint.Origin,
    //    footprint.Size, entrance.Entrance, footprint.RotSteps).
    //
    //  부착: SpawnSystem이 SpawnRequest.IsSupplier일 때 건물 인스턴스에 부착.
    //  ※ 임시: Relief를 직접 박는다. 나중에 PrefabMeta/ResourceType 테이블로 이관.
    //  ※ 입구 없는 공급자(BuildingEntrance 미부착)는 BFS 시작점을 못 구하므로
    //    재빌드에서 건너뛴다 (입구가 도달 진입점이라는 설계 전제).
    // ══════════════════════════════════════════════════════════════════════
    public struct StampSupplier : IComponentData
    {
        /// <summary>이 공급자를 소유한 플레이어 (0~7). 자기 도로망에만 도장.</summary>
        public int OwnerLocalId;

        /// <summary>이 공급자가 해소하는 Need 조합 (임시 직접 값).</summary>
        public NeedType Relief;

        /// <summary>도달 범위 상한 (BFS 최대 거리, 도로 칸 수). 0 이하면 무제한.</summary>
        public int MaxDist;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  RoadMaintenanceDepot  — "이 건물은 도로 관리시설이다" 표식
    // ──────────────────────────────────────────────────────────────────────
    //  StampSupplier와 형제(둘 다 입구 도로셀에서 도로망 BFS로 도장을 찍는
    //  시설). 차이는 Relief가 없고 도장 Kind가 RoadMaintenance라는 점뿐.
    //  도달 범위 안의 도로셀에 StampKind.RoadMaintenance 도장이 찍히고,
    //  RoadDecaySystem이 도장 없는 도로를 미관리로 보고 decay시킨다.
    //
    //  부착: SpawnSystem이 SpawnRequest.IsRoadMaintenance일 때 건물에 부착.
    //  BFS 시작점 = StampSupplier와 동일(입구 도로셀, BuildingEntrance 필요).
    // ══════════════════════════════════════════════════════════════════════
    public struct RoadMaintenanceDepot : IComponentData
    {
        /// <summary>이 관리시설을 소유한 플레이어 (0~7). 자기 도로망에만 도장.</summary>
        public int OwnerLocalId;

        /// <summary>관리 도달 거리 (BFS 최대 거리, 도로 칸 수). 0 이하면 무제한.</summary>
        public int MaxDist;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  StampRebuildSystem  — stamp 재빌드 (라운드로빈 + DirtyMask)
    // ──────────────────────────────────────────────────────────────────────
    //  Stamp 인프라 3시스템 중 마지막:
    //    StampInitSystem         — StampLayers 싱글톤 alloc/dispose
    //    StampDirtyCollectSystem — StampDirtyEvent 모아 DirtyMask 반영
    //    StampRebuildSystem (이것) — DirtyMask 보고 그 슬롯만 재BFS
    //
    //  매 (저빈도) 틱:
    //    ① DirtyMask에서 dirty한 플레이어를 라운드로빈으로 1명 선택.
    //    ② stamp.ClearSlot(p) — 그 슬롯 맵 비움 (도달 범위 다시 그리기).
    //    ③ 그 플레이어 소유 StampSupplier 전수 → 각자 입구 도로셀에서 BFS.
    //       도로셀마다 SupplierRef{공급자, Relief, dist} 도장(Add).
    //    ④ stamp.ClearDirty(p) → SetSingleton (DirtyMask는 값 필드).
    //
    //  메모리 ⑤/⑤-보충 준수:
    //    · 연결 아님, 도달 범위 다시 그리기 (매번 Clear 후 재BFS = 무효화 회피).
    //    · 다중 소스 확산: 한 플레이어의 모든 공급자가 같은 맵에 겹쳐 찍는다.
    //    · 가까운 공급자 우선 = SupplierRef.Dist에 BFS 거리 기록 (수급자가 정렬).
    //    · Capacity는 stamp에 안 박음 (예약 시 BuildingOccupancy 직접 조회).
    //
    //  ※ Burst 미적용: NativeParallelMultiHashMap 인덱서 get/set과 SetSingleton
    //    경로가 얽혀 있고, 재빌드는 한 틱에 한 플레이어(저빈도)라 비용이 작다.
    //    병목 시 BFS 본체만 IJob으로 분리 가능.
    //
    //  ※ 게이팅: GameClock.HourChanged일 때만 진입(매 게임 시간 1회).
    //    DirtyMask = 0이면 BFS 없이 즉시 탈출하므로, dirty 없는 시간대 비용 = 0.
    //    새 건물·도로 변경의 stamp 반영 지연 ≤ 1 게임 시간(도시 건설 페이스에 적합).
    //    GameClock 없으면 시스템 비활성(RequireForUpdate).
    // ══════════════════════════════════════════════════════════════════════
    public partial struct StampRebuildSystem : ISystem
    {
        // 라운드로빈 커서: 다음에 검사 시작할 플레이어 슬롯.
        int _cursor;

        public void OnCreate(ref SystemState state)
        {
            _cursor = 0;
            state.RequireForUpdate<StampLayers>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GameClock>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 게이트: 게임 시간이 바뀐 프레임에만 진입.
            if (!SystemAPI.GetSingleton<GameClock>().HourChanged)
                return;

            var stamp = SystemAPI.GetSingleton<StampLayers>();

            // ── ① 라운드로빈으로 dirty한 플레이어 1명 선택 ──────────────
            int target = -1;
            for (int i = 0; i < StampLayers.MaxPlayers; i++)
            {
                int p = (_cursor + i) % StampLayers.MaxPlayers;
                if (stamp.IsDirty(p))
                {
                    target = p;
                    _cursor = (p + 1) % StampLayers.MaxPlayers; // 다음 틱은 그 다음부터.
                    break;
                }
            }
            if (target < 0)
                return; // 재빌드할 플레이어 없음.

            var roadLayer = SystemAPI.GetSingleton<GridLayers>().RoadLayer;

            // ── ② 그 슬롯 맵 Clear (도달 범위 다시 그리기) ──────────────
            var map = stamp[target]; // 핸들 값 복사 — 같은 버퍼 가리킴.
            map.Clear();

            // ── ③ 그 플레이어 소유 공급자 전수 → 각자 BFS ──────────────
            //   (공급자 수는 시민보다 압도적으로 적어 전수 스캔 + 값 비교로 충분 — 메모리 원칙.)
            //   footprint/입구는 BuildingFootprint/BuildingEntrance에서 읽는다.
            //   BuildingEntrance를 쿼리에 포함 → 입구 없는 공급자는 자동 제외
            //   (BFS 시작점=입구 도로셀이 없으므로 도달 범위를 그릴 수 없음).
            var queue = new NativeQueue<int2>(Allocator.Temp);
            var visited = new NativeHashMap<int2, int>(1024, Allocator.Temp);

            foreach (var (supplier, footprint, bEntrance, entity) in
                     SystemAPI.Query<RefRO<StampSupplier>, RefRO<BuildingFootprint>,
                                     RefRO<BuildingEntrance>>().WithEntityAccess())
            {
                if (supplier.ValueRO.OwnerLocalId != target)
                    continue;

                StampOne(in footprint.ValueRO, in bEntrance.ValueRO, entity, target,
                         supplier.ValueRO.Relief, supplier.ValueRO.MaxDist, StampKind.Supplier,
                         ref map, in roadLayer, ref queue, ref visited);
            }

            // ── ③-b 같은 플레이어 소유 창고도 동일 BFS로 도장(Kind=Warehouse) ──
            //   commodity는 need 비트가 아니므로 Relief=None. 어떤 품목을 보유/수용
            //   하는지는 stamp에 안 싣고, pull/push가 전송 시점에 창고 stock에서 직접
            //   읽는다(capacity 직접읽기 원칙). ServiceSearch는 Relief=None이라 무시.
            //   입구 없는 창고(BuildingEntrance 미부착)는 쿼리에서 자동 제외.
            foreach (var (warehouse, footprint, bEntrance, entity) in
                     SystemAPI.Query<RefRO<WarehouseTag>, RefRO<BuildingFootprint>,
                                     RefRO<BuildingEntrance>>().WithEntityAccess())
            {
                if (warehouse.ValueRO.OwnerLocalId != target)
                    continue;

                StampOne(in footprint.ValueRO, in bEntrance.ValueRO, entity, target,
                         NeedType.None, warehouse.ValueRO.MaxDist, StampKind.Warehouse,
                         ref map, in roadLayer, ref queue, ref visited);
            }

            visited.Dispose();
            queue.Dispose();

            // ── ④ dirty 해제 (DirtyMask는 값 필드 → 다시 써야 반영) ────
            stamp.ClearDirty(target);
            SystemAPI.SetSingleton(stamp);
        }

        // ──────────────────────────────────────────────────────────────────
        //  공급자 1개 BFS — 입구 도로셀에서 시작해 자기 소유 도로를 4방 확산.
        //
        //  · 시작점이 도로가 아니거나 내 소유가 아니면 즉시 종료 — 배치 검증을
        //    통과했어야 정상이나, 런타임에 도로가 헐릴 수 있으므로 방어.
        //  · 방문 셀마다 SupplierRef(공급자, Relief, dist) 도장.
        //  · 같은 셀에 다른 공급자가 이미 있어도 무관 (MultiHashMap = 누적).
        //  · 같은 공급자의 같은 셀 재방문은 visited로 차단 (최단거리 먼저 도달).
        // ──────────────────────────────────────────────────────────────────
        static void StampOne(
            in BuildingFootprint fp,
            in BuildingEntrance  be,
            Entity              facilityEntity,
            int                 owner,
            NeedType            relief,
            int                 maxDist,
            StampKind           kind,
            ref NativeParallelMultiHashMap<int2, SupplierRef> map,
            in NativeHashMap<int2, RoadCell> roadLayer,
            ref NativeQueue<int2> queue,
            ref NativeHashMap<int2, int> visited)
        {
            int2 start = EntranceOps.EntranceRoadCell(fp.Origin, fp.Size, in be.Entrance, fp.RotSteps);

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
                    Supplier = facilityEntity,
                    Relief   = relief,
                    Dist     = dist,
                    Kind     = kind,
                });

                // 거리 상한 도달 시 더 확장 안 함.
                if (maxDist > 0 && dist >= maxDist)
                    continue;

                // 현재 셀의 연결 비트(Directions)를 따라서만 확산.
                //   보행/시각과 동일 권위 데이터를 사용 → 평행 도로는 물류도 안 건너감,
                //   교차 셀에서만 가로질러 전파(축-AND 결과 그대로 반영).
                if (!roadLayer.TryGetValue(cell, out var curRc))
                    continue;

                for (int d = 0; d < 4; d++)
                {
                    if ((curRc.Directions & RoadDirOps.FromIndex(d)) == 0)
                        continue;                                  // 이 방향으로 안 이어짐
                    int2 next = cell + dirs[d];
                    if (visited.ContainsKey(next))
                        continue;
                    if (!roadLayer.TryGetValue(next, out var nextRc) || nextRc.OwnerLocalId != owner)
                        continue;
                    // 이웃이 반대 방향으로 되받아 연결돼야 함(양방향 일치).
                    if ((nextRc.Directions & RoadDirOps.FromIndex((d + 2) & 3)) == 0)
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
