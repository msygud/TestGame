using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ServiceTarget — 시민이 찾아낸 "이번에 갈 공급자" (찾기 결과)
    // ──────────────────────────────────────────────────────────────────────────
    //  ServiceSearchSystem이 채운다. 이동/해소는 후속 단계가 이 값을 소비.
    //  공급자를 못 찾으면 Supplier = Entity.Null (Has=false).
    // ══════════════════════════════════════════════════════════════════════════
    public struct ServiceTarget : IComponentData
    {
        public Entity   Supplier;  // 찾은 공급자 건물. Null이면 못 찾음.
        public NeedType Relief;    // 그 공급자가 해소하는(이번에 추구한) 욕구 비트.
        public int      Dist;      // 시민 현재 건물 입구 도로셀에서의 거리(도로 칸 수).

        public readonly bool Has => Supplier != Entity.Null;

        public static ServiceTarget None => new ServiceTarget
        {
            Supplier = Entity.Null,
            Relief   = NeedType.None,
            Dist     = int.MaxValue,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ServiceSearchSystem — 수급자 읽기 (stamp 첫 실사용처)
    // ──────────────────────────────────────────────────────────────────────────
    //  합의된 인적이동 모델:
    //    · 출발점은 "시민이 지금 있는 건물"(CitizenState.CurrentBuilding).
    //      집/직장이 검색 조건이 아니다 — 현재 건물 기준.
    //    · 시민은 전수조사하지 않는다. 자기 현재 건물의 입구 도로셀에 찍힌
    //      stamp 도장만 읽는다("입구만 보면 다 있다").
    //    · 욕구 ↔ 공급자 매칭 = NeedType 직접 플래그 비교
    //      (SupplierRef.Relief & 추구욕구) != 0.
    //    · 같은 셀의 여러 공급자 중 가장 가까운(최단 Dist) 것을 고른다.
    //
    //  이번 범위(찾기까지):
    //    Pursuing(추구 욕구)으로 → 현재 건물 입구 도로셀의 stamp 조회 →
    //    매칭되는 가장 가까운 공급자를 ServiceTarget에 기록. 이동/루트/예약/
    //    교통통계/수용량 대안탐색은 후속 단계.
    //
    //  플레이어 분리:
    //    CitizenOwner(SharedComponent, LocalId)로 청크가 플레이어별로 갈린다.
    //    WithSharedComponentFilter(LocalId)로 한 플레이어 시민만 묶어, 그
    //    플레이어의 stamp 슬롯(stamp[LocalId])으로 일괄 처리.
    //
    //  주쿼리 = 변하는 데이터:
    //    CitizenNeeds(Pursuing) + CitizenState(CurrentBuilding) = 핫.
    //    건물 footprint/입구는 ComponentLookup(RO)로 콜드 조회.
    //
    //  ※ 게이팅: 매 프레임 전 시민을 도는 건 과하다. 현재는 단순화를 위해
    //    매 프레임 + "Pursuing 있고 건물에 있는" 시민만(쿼리로 1차 필터).
    //    후속에 스태거링/이벤트 트리거로 빈도 낮춘다(§10).
    // ══════════════════════════════════════════════════════════════════════════
    public partial struct ServiceSearchSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<StampLayers>();
            // 시민이 하나라도 있어야 의미. ServiceTarget 부착된 시민 대상.
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var stamp = SystemAPI.GetSingleton<StampLayers>();

            // 건물 footprint/입구 콜드 조회용 (RO).
            var fpLookup  = SystemAPI.GetComponentLookup<BuildingFootprint>(true);
            var entLookup = SystemAPI.GetComponentLookup<BuildingEntrance>(true);

            // 어느 플레이어 슬롯이 존재하는지 모르므로 0~7 전부 시도.
            // 각 슬롯마다 그 플레이어 시민만 필터해 처리.
            for (int localId = 0; localId < StampLayers.MaxPlayers; localId++)
            {
                var map = stamp[localId];
                if (!map.IsCreated)
                    continue;

                foreach (var (needs, st, target) in
                         SystemAPI.Query<RefRO<CitizenNeeds>, RefRO<CitizenState>,
                                         RefRW<ServiceTarget>>()
                             .WithAll<CitizenTag>()
                             .WithSharedComponentFilter(new CitizenOwner(localId)))
                {
                    // 검색은 "목적지를 새로 찾아야 하는" 시민만.
                    //  · 이미 목적지가 있으면(Has) 이동/도착이 인계 중 → 건드리지 않음.
                    //  · Idle/AtHome일 때만 검색(이동 중 CurrentBuilding=Null 상태에서
                    //    덮어쓰면 이동이 깨진다). 도착·이동 상태는 보존.
                    if (target.ValueRO.Has)
                        continue;
                    var act = st.ValueRO.Activity;
                    if (act != CitizenActivity.Idle && act != CitizenActivity.AtHome)
                        continue;

                    target.ValueRW = SearchOne(
                        needs.ValueRO, st.ValueRO, map, in fpLookup, in entLookup);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  시민 1명 — 현재 건물 입구 도로셀의 stamp에서 매칭 공급자 찾기.
        // ──────────────────────────────────────────────────────────────────────
        static ServiceTarget SearchOne(
            in CitizenNeeds needs,
            in CitizenState st,
            in NativeParallelMultiHashMap<int2, SupplierRef> map,
            in ComponentLookup<BuildingFootprint> fpLookup,
            in ComponentLookup<BuildingEntrance>  entLookup)
        {
            // 추구할 욕구 = Pursuing(결정 시스템이 설정). None이면 미정 → 스킵.
            // (ActiveMask 비트 집계는 폐기됨. 활성 판정은 각 욕구 컴포넌트의
            //  IsActive로, 추구 선택은 결정 시스템이 Pursuing에 set.)
            NeedType want = needs.Pursuing;
            if (want == NeedType.None)
                return ServiceTarget.None; // 추구 중인 욕구 없음.

            // 현재 건물에 있어야 검색(이동 중이면 기준 셀 없음 → 스킵).
            Entity building = st.CurrentBuilding;
            if (building == Entity.Null)
                return ServiceTarget.None;

            // 현재 건물의 footprint/입구로 입구 도로셀 계산.
            if (!fpLookup.HasComponent(building) || !entLookup.HasComponent(building))
                return ServiceTarget.None; // 입구 없는 건물(또는 footprint 미부착) → 기준 셀 없음.

            var fp  = fpLookup[building];
            var ent = entLookup[building];
            int2 roadCell = EntranceOps.EntranceRoadCell(fp.Origin, fp.Size, in ent.Entrance, fp.RotSteps);

            // 그 도로셀 도장들 중, 욕구와 매칭되는(Relief & want) 가장 가까운 공급자.
            var best = ServiceTarget.None;
            if (map.TryGetFirstValue(roadCell, out var sr, out var it))
            {
                do
                {
                    // 직접 플래그 매칭: 이 공급자의 Relief에 추구 욕구 비트가 포함되는가.
                    if ((sr.Relief & want) == NeedType.None)
                        continue;

                    if (sr.Dist < best.Dist)
                    {
                        best.Supplier = sr.Supplier;
                        best.Relief   = sr.Relief;
                        best.Dist     = sr.Dist;
                    }
                }
                while (map.TryGetNextValue(out sr, ref it));
            }

            return best;
        }
    }
}
