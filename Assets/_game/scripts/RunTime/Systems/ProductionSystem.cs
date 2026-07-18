using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ProductionSystem — 건물 생산 사이클 처리
    //
    //  매 틱(게임초 기준):
    //    [대기] Progress < 0
    //      → Input 재고가 InputAmount 이상이면:
    //           Input 차감, Progress = 0  (제작 시작)
    //      → 아니면 패스 (다음 틱 재시도)
    //
    //    [진행] Progress ≥ 0
    //      → Progress += dt × StaffEffect.Factor(무인 설계 = 1)
    //      → Progress ≥ BaseDuration 이면 완료:
    //           Output 재고에 OutputAmount 추가
    //           Progress = -1 (다음 사이클 대기)
    //
    //  재고 룰:
    //    - Input 차감: StockRole.Input, 품목 = recipe.Input
    //    - Output 추가:
    //        Final 티어 → StockRole.LocalFinal (창고 안 거침)
    //        그 외       → StockRole.Output     (Push 대상)
    //    - 출력 칸이 꽉 찼으면(Free < OutputAmount) 완료를 미룬다(출력 포화 대기).
    //      → Progress를 BaseDuration에 클램프해 두고, 공간 생길 때 완료.
    //
    //  ※ 메인스레드: Pull/Push와 동일한 이유(버퍼 alias 회피, 저빈도 보장).
    //  ※ RequireForUpdate<GameClock>: dt 소스. GameClock 없으면 시스템 비활성.
    //  ※ RequireForUpdate<ProductionJob>: 생산 건물 없으면 조기 종료.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(LogisticsPullSystem))]
    public partial struct ProductionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<ProductionJob>();

            // 채취 소모 원장(2026-07-19) — 수명주기 이 시스템 소유(StockInheritance 패턴).
            if (!SystemAPI.HasSingleton<ResourceExtractLedger>())
            {
                var e = state.EntityManager.CreateEntity(typeof(ResourceExtractLedger));
                state.EntityManager.SetComponentData(e, new ResourceExtractLedger
                {
                    Pending = new NativeHashMap<int2, int>(256, Allocator.Persistent),
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<ResourceExtractLedger>()) return;
            var ledger = SystemAPI.GetSingleton<ResourceExtractLedger>();
            if (ledger.Pending.IsCreated) ledger.Pending.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!_initialized)
            {
                _prevTotalSeconds = clock.TotalSeconds;
                _initialized = true;
                return;
            }
            float dt = (float)(clock.TotalSeconds - _prevTotalSeconds);
            _prevTotalSeconds = clock.TotalSeconds;
            if (dt <= 0f) return;

            // 인구 비례 비축 게이트 입력(P2, 2026-07-17) — 풀 셀 Target(LogisticsPoolSystem이
            //   시간당 재계산). 풀 없음(부트스트랩)이면 게이트 없음.
            bool havePool = SystemAPI.TryGetSingleton<LogisticsPool>(out var pool)
                            && pool.Cells.IsCreated;

            // ── 채취(2026-07-19): 원장 + ResourceLayer(메인 읽기 = 안전) ──
            //   시간 경계에 pending을 라이브 레이어로 일괄 반영 — 폴링 스냅샷 잡(AI)과의
            //   충돌은 쓰기 전 CompleteAllTrackedJobs(확립 패턴, 시간당 1회라 비용 무시).
            bool haveExtract = SystemAPI.TryGetSingleton<ResourceExtractLedger>(out var ledger)
                               && ledger.Pending.IsCreated
                               && SystemAPI.HasSingleton<GridLayers>();
            if (haveExtract && clock.HourChanged && ledger.Pending.Count > 0)
            {
                state.EntityManager.CompleteAllTrackedJobs();
                ref var layers = ref SystemAPI.GetSingletonRW<GridLayers>().ValueRW;
                foreach (var kv in ledger.Pending)
                {
                    if (!layers.ResourceLayer.TryGetValue(kv.Key, out var rc)) continue;
                    rc.Amount = math.max(0, rc.Amount - kv.Value);
                    layers.ResourceLayer[kv.Key] = rc;
                }
                ledger.Pending.Clear();
            }
            var extractorLookup = SystemAPI.GetComponentLookup<ResourceExtractor>(true);
            bool haveLayers = SystemAPI.TryGetSingleton<GridLayers>(out var gridLayers);

            foreach (var (job, fp, entity) in
                     SystemAPI.Query<RefRW<ProductionJob>, RefRO<BuildingFootprint>>()
                              .WithEntityAccess())
            {
                var recipe = RecipeDefs.Get(job.ValueRO.RecipeOutput);
                if (recipe.BaseDuration <= 0f) continue; // 레시피 없음

                if (!SystemAPI.HasBuffer<StockEntry>(entity)) continue;
                var stock = SystemAPI.GetBuffer<StockEntry>(entity);

                ref var j = ref job.ValueRW;

                // 직무 효과(StaffEffect, 2026-07-12 일반화 — 구 ProductionJob.SkillFactor 은퇴):
                //   유인 직장 = 노동력 스칼라 / 무인 설계 생산 건물(컴포넌트 없음) = 1(상시 가동).
                float staff = SystemAPI.HasComponent<StaffEffect>(entity)
                    ? SystemAPI.GetComponent<StaffEffect>(entity).Factor : 1f;

                if (j.Progress < 0f)
                {
                    // ── 인구 비례 비축 게이트(P2, 2026-07-17): 풀 재고 ≥ Target이면 새 사이클
                    //   시작 안 함(유휴). 소비로 재고가 빠지거나 인구가 늘어 Target이 오르면 자동
                    //   재개 — 비축의 앵커를 용량(창고 수·래칫)에서 인구로 교정(무한 생산 함정 차단).
                    //   풀 셀 없음(창고 전무 부트스트랩·Final 품목 = 풀 미경유) → 게이트 없음
                    //   (로컬 수급은 기존 출력 포화 클램프가 전담).
                    if (havePool && pool.Cells.TryGetValue(
                            LogisticsPool.Key(fp.ValueRO.OwnerLocalId, recipe.Output), out var pc)
                        && pc.Stored >= pc.Target)
                        continue;

                    // ── 대기: 일꾼이 있고(staff>0) 재료가 충분하면 제작 시작 ──
                    //   무인(노동력 집계 0)이면 시작하지 않음 — 안 하면 재료만 차감된 채
                    //   진행 0에서 동결(재료 잠김). 진행 중 무인화는 동결 후 재개(허용).
                    //   채취(ResourceExtractor): 재료 = footprint 아래 매칭 자원 셀.
                    //   DrawInput(레시피 Input=None = 자연 통과)과 대칭으로 시작 시점에
                    //   소모 확정(pending 적립) — 부족(고갈)이면 유휴, 재도색 시 자동 재개.
                    if (staff > 0f
                        && DrawInput(ref stock, recipe.Input, recipe.InputAmount))
                    {
                        if (extractorLookup.HasComponent(entity))
                        {
                            if (!haveExtract || !haveLayers
                                || !TryConsumeResource(fp.ValueRO, extractorLookup[entity],
                                                       in gridLayers, ref ledger))
                                continue;   // 고갈/미초기화 = 유휴 (레시피 Input=None이라 차감 잠김 없음)
                        }
                        j.Progress = 0f;
                    }
                }
                else
                {
                    // ── 진행: 시간 누적 ────────────────────────────────────────
                    j.Progress += dt * staff;

                    if (j.Progress >= recipe.BaseDuration)
                    {
                        // 출력 포화 체크: 공간 없으면 완료 미룸(클램프).
                        if (!HasOutputRoom(in stock, recipe.Output, recipe.OutputAmount))
                        {
                            j.Progress = recipe.BaseDuration; // 클램프 (무한 대기)
                        }
                        else
                        {
                            AddOutput(ref stock, recipe.Output, recipe.OutputAmount);
                            j.Progress = -1f; // 다음 사이클 대기
                        }
                    }
                }
            }
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 채취 소모(2026-07-19): footprint 아래 매칭 TypeId 셀의 가용량(라이브 − pending)이
        /// AmountPerCycle 이상이면 pending에 적립하고 true. 부족(고갈)이면 아무것도 안 하고
        /// false. 라이브 레이어는 읽기만(쓰기는 시간 경계 일괄 반영) — 메인 전용.
        /// </summary>
        static bool TryConsumeResource(in BuildingFootprint fp, in ResourceExtractor ext,
                                       in GridLayers layers, ref ResourceExtractLedger ledger)
        {
            if (!layers.ResourceLayer.IsCreated) return false;
            int  need = math.max(1, ext.AmountPerCycle);
            int2 size = EntranceOps.RotateSize(fp.Size, fp.RotSteps);

            // 1패스: 가용 확인(부족하면 무변경 — DrawInput과 동일 계약).
            int avail = 0;
            for (int dx = 0; dx < size.x && avail < need; dx++)
            for (int dz = 0; dz < size.y && avail < need; dz++)
            {
                var cell = fp.Origin + new int2(dx, dz);
                if (layers.ResourceLayer.TryGetValue(cell, out var rc)
                    && rc.TypeId == ext.ResourceTypeId)
                    avail += math.max(0, rc.Amount - ledger.PendingAt(cell));
            }
            if (avail < need) return false;

            // 2패스: pending 적립(셀 순서 고정 = 결정적).
            int remain = need;
            for (int dx = 0; dx < size.x && remain > 0; dx++)
            for (int dz = 0; dz < size.y && remain > 0; dz++)
            {
                var cell = fp.Origin + new int2(dx, dz);
                if (!layers.ResourceLayer.TryGetValue(cell, out var rc)
                    || rc.TypeId != ext.ResourceTypeId) continue;
                int take = math.min(remain, math.max(0, rc.Amount - ledger.PendingAt(cell)));
                if (take <= 0) continue;
                ledger.Add(cell, take);
                remain -= take;
            }
            return remain <= 0;
        }

        /// <summary>
        /// Input 칸에서 amount 차감. 충분하면 true, 부족하면 false(아무것도 안 건드림).
        /// </summary>
        static bool DrawInput(ref DynamicBuffer<StockEntry> stock,
                              Commodity c, int amount)
        {
            // 먼저 총량 확인.
            int total = 0;
            for (int i = 0; i < stock.Length; i++)
            {
                var e = stock[i];
                if (e.Role == StockRole.Input && e.Commodity == c)
                    total += e.Current;
            }
            if (total < amount) return false;

            // 충분 → 차감.
            int remain = amount;
            for (int i = 0; i < stock.Length && remain > 0; i++)
            {
                var e = stock[i];
                if (e.Role != StockRole.Input || e.Commodity != c) continue;
                int take = remain < e.Current ? remain : e.Current;
                e.Current -= take;
                stock[i]   = e;
                remain    -= take;
            }
            return true;
        }

        /// <summary>Output or LocalFinal 칸에 amount 추가할 여유가 있는가.</summary>
        static bool HasOutputRoom(in DynamicBuffer<StockEntry> stock,
                                  Commodity c, int amount)
        {
            bool isFinal = CommodityDefs.TierOf(c) == CommodityTier.Final;
            StockRole role = isFinal ? StockRole.LocalFinal : StockRole.Output;

            int free = 0;
            for (int i = 0; i < stock.Length; i++)
            {
                var e = stock[i];
                if (e.Role == role && e.Commodity == c)
                    free += e.Free;
            }
            return free >= amount;
        }

        /// <summary>Output or LocalFinal 칸에 amount 추가(여유만큼).</summary>
        static void AddOutput(ref DynamicBuffer<StockEntry> stock,
                              Commodity c, int amount)
        {
            bool isFinal = CommodityDefs.TierOf(c) == CommodityTier.Final;
            StockRole role = isFinal ? StockRole.LocalFinal : StockRole.Output;

            int remain = amount;
            for (int i = 0; i < stock.Length && remain > 0; i++)
            {
                var e = stock[i];
                if (e.Role != role || e.Commodity != c) continue;
                int spare = e.Free;
                if (spare <= 0) continue;
                int add = remain < spare ? remain : spare;
                e.Current += add;
                stock[i]   = e;
                remain    -= add;
            }
        }

        // GameClock.TotalSeconds 이전 프레임 값(dt 계산용).
        // false인 첫 틱에 기준점만 잡고 처리 건너뜀(로드 직후 TotalSeconds 점프 방어).
        double _prevTotalSeconds;
        bool   _initialized;
    }
}
