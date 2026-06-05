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
    //  ※ 게이팅: 매 프레임 도는 기본 그룹에 두면 안 된다. 저빈도(예: DayChanged
    //    또는 N프레임마다) 갱신이 의도. 시스템 그룹/주기 배치는 후속 결정.
    // ══════════════════════════════════════════════════════════════════════
    public partial struct StampRebuildSystem : ISystem
    {
        // 라운드로빈 커서: 다음에 검사 시작할 플레이어 슬롯.
        int _cursor;

        public void OnCreate(ref SystemState state)
        {
            _cursor = 0;
            // 맵 alloc은 StampInitSystem 담당. 여기선 의존성만 건다.
            state.RequireForUpdate<StampLayers>();
            state.RequireForUpdate<GridLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
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

                StampOne(in supplier.ValueRO, in footprint.ValueRO, in bEntrance.ValueRO,
                         entity, target, ref map, in roadLayer, ref queue, ref visited);
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
            in StampSupplier    s,
            in BuildingFootprint fp,
            in BuildingEntrance  be,
            Entity              supplierEntity,
            int                 owner,
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
