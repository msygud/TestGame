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

            // RW 선언(확립 기법, 2026-07-06) — stamp 맵을 [ReadOnly]로 읽는 잡
            //   (ServiceSearchJob)이 있으면 완료를 기다린 뒤 mutate(Clear/Add).
            //   RO 선언(GetSingleton)이면 리더 잡과 컨테이너 안전성 충돌.
            var stamp = SystemAPI.GetSingletonRW<StampLayers>().ValueRO;

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

            // ── ③ 그 플레이어 소유 공급자 → 각자 BFS ──────────────────────
            //   OwnerShared 청크 필터(2026-07-09): 대상 플레이어 청크만 순회 →
            //   타 플레이어 공급자 전수 스캔 + 값 비교(구 if OwnerLocalId!=target continue) 제거.
            //   footprint/입구는 BuildingFootprint/BuildingEntrance에서 읽는다.
            //   BuildingEntrance를 쿼리에 포함 → 입구 없는 공급자는 자동 제외
            //   (BFS 시작점=입구 도로셀이 없으므로 도달 범위를 그릴 수 없음).
            var queue = new NativeQueue<int2>(Allocator.Temp);
            var visited = new NativeHashMap<int2, int>(1024, Allocator.Temp);

            foreach (var (supplier, footprint, bEntrance, entity) in
                     SystemAPI.Query<RefRO<StampSupplier>, RefRO<BuildingFootprint>,
                                     RefRO<BuildingEntrance>>()
                         .WithSharedComponentFilter(new OwnerShared(target))
                         .WithEntityAccess())
            {
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
                                     RefRO<BuildingEntrance>>()
                         .WithSharedComponentFilter(new OwnerShared(target))
                         .WithEntityAccess())
            {
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
        //  시설 1개 도장 — 입구 도로셀에서 owner 도로망을 maxDist까지 확산.
        //
        //  BFS는 공용 fact RoadCoverageOps.Flood에 위임한다(공급자/창고 공통 규칙).
        //  · 입구 도로셀이 owner 도로가 아니면 Flood가 빈 결과(런타임 도로 철거 방어).
        //  · 도달 셀마다 SupplierRef(시설, Relief, dist, Kind) 도장.
        //  · 같은 셀에 다른 시설이 이미 있어도 무관 (MultiHashMap = 누적).
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

            // 입구 도로셀에서 owner 도로망을 maxDist까지 확산(공용 fact).
            //   queue/visited는 Flood 진입 시 Clear된다.
            RoadCoverageOps.Flood(start, owner, maxDist, in roadLayer, ref queue, ref visited);

            // 도달한 도로셀마다 도장(거리는 visited에 기록됨). 추가 순서 무관(소비처가 Dist로 정렬).
            var cells = visited.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < cells.Length; i++)
            {
                int2 cell = cells[i];
                map.Add(cell, new SupplierRef
                {
                    Supplier = facilityEntity,
                    Relief   = relief,
                    Dist     = visited[cell],
                    Kind     = kind,
                });
            }
            cells.Dispose();
        }
    }
}
