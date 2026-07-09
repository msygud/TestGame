using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ServiceOutcome — 서비스 검색 결과의 실패 사유(2026-07-08, 욕구 주도 배치 통계)
    // ──────────────────────────────────────────────────────────────────────────
    //  "지어서 고칠 수 있는 실패"의 분류. WHERE의 의미가 사유마다 다르다:
    //    · NoCoverage : 이 욕구를 푸는 공급자가 사거리(현재 건물 입구 stamp) 내 아예 없음
    //                   → 그 자리에 공급 건물 신설이 정답(WHERE = 시민 위치).
    //    · Reached    : 공급자는 도달 가능하나 서빙 불가(재고 0·만석·무인 폐점)
    //                   → 상류 보강/용량 증설/노동력(WHERE = 그 공급자). 건물 난사 억제.
    //  검색 잡이 후보 순회 중 확정(필터가 이미 계산 — 재계산 0). 잠금 중(요청 없음)
    //  시민은 애초에 검색 자체를 안 하므로 통계에 안 들어감(시도 게이트 = 오염 필터).
    // ══════════════════════════════════════════════════════════════════════════
    public enum ServiceOutcome : byte
    {
        None       = 0,   // 미검색 / 성공(Has=true) — 실패 아님
        NoCoverage = 1,   // 사거리 내 해당 욕구 공급자 없음
        Reached    = 2,   // 도달했으나 서빙 실패(재고·만석·폐점)
    }

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

        /// <summary>이번 검색의 실패 사유(욕구 주도 배치 입력). Has=true면 None,
        /// 실패면 NoCoverage/Reached. DemandAggregation이 1초마다 이 값을 샘플한다
        /// (검색은 매 프레임 덮어쓰기 — 계측 비용 ~0, 카운팅은 저빈도 집계가 담당).</summary>
        public ServiceOutcome LastOutcome;

        public readonly bool Has => Supplier != Entity.Null;

        public static ServiceTarget None => new ServiceTarget
        {
            Supplier   = Entity.Null,
            Relief     = NeedType.None,
            Dist       = int.MaxValue,
            LastOutcome = ServiceOutcome.None,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ServiceSearchSystem — 수급자 읽기 (stamp 실사용처)
    // ──────────────────────────────────────────────────────────────────────────
    //  합의된 인적이동 모델:
    //    · 출발점은 "시민이 지금 있는 건물"(CitizenState.CurrentBuilding).
    //    · 시민은 전수조사하지 않는다. 자기 현재 건물의 입구 도로셀에 찍힌
    //      stamp 도장만 읽는다("입구만 보면 다 있다").
    //    · 욕구 ↔ 공급자 매칭 = NeedType 직접 플래그 비교.
    //    · 같은 셀의 여러 공급자 중 가장 가까운(최단 Dist) 것을 고른다.
    //
    //  실행 모델 — Burst 잡(2026-07-06, 구 메인스레드 전수 순회가 인구 1.8만에서
    //  최상위 실측 → 교체):
    //    · IJobEntity 병렬 — 대다수 시민(추구 없음/이동 중/목적지 보유)은 첫 분기에서
    //      탈락하는 청크 선형 순회.
    //    · 플레이어 슬롯 해석: OwnerShared(SharedComponent) 대신 **현재 건물
    //      footprint의 OwnerLocalId**를 사용 — 배정이 owner-일치(2026-07-05)라 동치이고,
    //      잡에서 공유 컴포넌트 접근/8중 필터 루프가 불필요해진다.
    //    · StampLayers는 [ReadOnly] 캡처. 쓰기자(StampRebuildSystem)는 RW 선언으로
    //      이 잡의 완료를 대기(확립 기법) — 재빌드는 HourChanged 저빈도라 대기 ~0.
    // ══════════════════════════════════════════════════════════════════════════
    public partial struct ServiceSearchSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<StampLayers>();
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new ServiceSearchJob
            {
                Stamp         = SystemAPI.GetSingleton<StampLayers>(),
                FpLookup      = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                EntLookup     = SystemAPI.GetComponentLookup<BuildingEntrance>(true),
                StockLookup   = SystemAPI.GetBufferLookup<StockEntry>(true),
                VisitorLookup = SystemAPI.GetComponentLookup<VisitorOccupancy>(true),
                ProdLookup    = SystemAPI.GetComponentLookup<ProductionJob>(true),
            }.ScheduleParallel(state.Dependency);
        }
    }

    // ── 시민 1명 — 현재 건물 입구 도로셀의 stamp에서 매칭 공급자 찾기 ──────────
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct ServiceSearchJob : IJobEntity
    {
        [ReadOnly] public StampLayers Stamp;
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public ComponentLookup<BuildingEntrance>  EntLookup;
        [ReadOnly] public BufferLookup<StockEntry>           StockLookup;
        [ReadOnly] public ComponentLookup<VisitorOccupancy>  VisitorLookup;
        [ReadOnly] public ComponentLookup<ProductionJob>     ProdLookup;

        void Execute(ref ServiceTarget target, in CitizenNeeds needs, in CitizenState st)
        {
            // 검색은 "목적지를 새로 찾아야 하는" 시민만.
            //  · 이미 목적지가 있으면(Has) 이동/도착이 인계 중 → 건드리지 않음.
            //  · Idle/AtHome일 때만 검색(이동 중 CurrentBuilding=Null 상태에서
            //    덮어쓰면 이동이 깨진다). 도착·이동 상태는 보존.
            if (target.Has) return;
            var act = st.Activity;
            if (act != CitizenActivity.Idle && act != CitizenActivity.AtHome) return;

            NeedType want = needs.Pursuing;
            if (want == NeedType.None) return;          // 추구 중인 욕구 없음.

            Entity building = st.CurrentBuilding;
            if (building == Entity.Null) return;        // 기준 셀 없음(이동 중 등).
            if (!FpLookup.HasComponent(building) || !EntLookup.HasComponent(building))
                return;                                  // 입구 없는 건물 → 기준 셀 없음.

            var fp  = FpLookup[building];
            var ent = EntLookup[building];

            // 시민 owner = 현재 건물 owner (배정 owner-일치 전제 — 헤더 참조).
            int owner = fp.OwnerLocalId;
            if ((uint)owner >= StampLayers.MaxPlayers) return;
            var map = Stamp[owner];
            if (!map.IsCreated) return;

            int2 roadCell = EntranceOps.EntranceRoadCell(fp.Origin, fp.Size, in ent.Entrance, fp.RotSteps);

            // 그 도로셀 도장들 중, 욕구와 매칭되는(Relief & want) 가장 가까운 공급자.
            //   재고 인지(2026-07-06): 빈 공급자(LocalFinal 재고 0)는 후보 제외 — 안 하면
            //   빈 식당이 target으로 계속 잡혀 "순례 루프"(거절→귀가→재탐색)가 출근을
            //   영원히 이긴다(실측: 고용 24 중 출근 11). 제외되면 target=None → UNMET으로
            //   정직하게 잡히고, 근무시간이면 출근 규칙이 발동. 재고가 차면 자동 복귀.
            var best = ServiceTarget.None;
            bool reached = false;   // 이 욕구를 푸는 공급자가 사거리 내 존재(필터 전) — 실패 사유 분류용
            if (map.TryGetFirstValue(roadCell, out var sr, out var it))
            {
                do
                {
                    if ((sr.Relief & want) == NeedType.None)
                        continue;
                    // Relief 일치 = 이 욕구 공급자가 도달 가능 → 실패해도 NoCoverage 아닌 Reached.
                    reached = true;
                    // 무인 폐점(2026-07-07 decision 1a) — 직원 미출근(SkillFactor<=0)이면 영업 안 함.
                    //   ProductionJob 없는 공급자(광장 등)는 게이트 없음(항상 열림).
                    if (ProdLookup.HasComponent(sr.Supplier)
                        && ProdLookup[sr.Supplier].SkillFactor <= 0f)
                        continue;
                    if (!SupplierHasGoods(sr.Supplier))
                        continue;
                    // 만석 제외 — 대안탐색의 본체: 최근접이 만석이면 자연히 차선(다음 Dist) 선택.
                    if (VisitorLookup.HasComponent(sr.Supplier)
                        && VisitorLookup[sr.Supplier].Full)
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

            // 실패 사유 기록(욕구 주도 배치 입력) — 성공이면 None, 실패면 도달여부로 2분류.
            //   reached=false → NoCoverage(공급자 자체가 없음) / true → Reached(있으나 못 서빙).
            best.LastOutcome = best.Has ? ServiceOutcome.None
                             : (reached ? ServiceOutcome.Reached : ServiceOutcome.NoCoverage);
            target = best;
        }

        // 공급자에게 내줄 물건이 있는가 — LocalFinal(완성품 로컬) 칸 기준.
        //   재고 버퍼 없음 = 무한 공급(하위호환, ServeMealsJob과 동일 규칙).
        //   LocalFinal 칸이 아예 없으면 재고 개념이 없는 공급자(광장 등) → 통과.
        //   ※ 욕구→품목 매핑 테이블은 후속(현재는 "아무 LocalFinal>0" — 현 콘텐츠에선 Meal뿐).
        bool SupplierHasGoods(Entity supplier)
        {
            if (!StockLookup.HasBuffer(supplier)) return true;
            var stock = StockLookup[supplier];
            bool anyLocalFinal = false;
            for (int i = 0; i < stock.Length; i++)
            {
                if (stock[i].Role != StockRole.LocalFinal) continue;
                anyLocalFinal = true;
                if (stock[i].Current > 0) return true;
            }
            return !anyLocalFinal;
        }
    }
}
