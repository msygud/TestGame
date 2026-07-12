using Unity.Entities;

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

            foreach (var (job, entity) in
                     SystemAPI.Query<RefRW<ProductionJob>>().WithEntityAccess())
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
                    // ── 대기: 일꾼이 있고(staff>0) 재료가 충분하면 제작 시작 ──
                    //   무인(노동력 집계 0)이면 시작하지 않음 — 안 하면 재료만 차감된 채
                    //   진행 0에서 동결(재료 잠김). 진행 중 무인화는 동결 후 재개(허용).
                    if (staff > 0f
                        && DrawInput(ref stock, recipe.Input, recipe.InputAmount))
                        j.Progress = 0f;
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
