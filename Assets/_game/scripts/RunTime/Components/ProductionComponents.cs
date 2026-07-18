using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  생산 — 레시피 & 건물 생산 상태
    //
    //  흐름:
    //    Input 재고(StockRole.Input) 차감
    //    → Progress 누적(게임초 × StaffEffect.Factor — 무인 설계는 1)
    //    → 완료 시 Output 재고(StockRole.Output or LocalFinal) 추가
    //
    //  레시피 키 = Output 품목. 건물은 ProductionJob.RecipeOutput으로 레시피를 고른다.
    //  직무 효과(StaffEffect): 제작 진행 속도 승수 — 노동자 숙련·컨디션·적성이 속도를 정한다.
    //
    //  stub 체인: Grain→Flour(중간재), Flour→Meal(완성품).
    //  품목 추가 시 RecipeDefs.Get() 스위치와 Commodity enum만 확장.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>레시피 1개의 정적 메타.</summary>
    public struct RecipeDef
    {
        public Commodity Input;        // 소비 품목
        public int       InputAmount;  // 1회 소비량
        public Commodity Output;       // 생산 품목
        public int       OutputAmount; // 1회 생산량
        public float     BaseDuration; // 기본 제작 시간(게임초, SkillFactor 보정 전)
    }

    /// <summary>
    /// Output 품목 → 레시피 조회(stub, Burst-safe 정적 스위치).
    /// 레시피가 없는 품목(원재료 등)은 RecipeDef.BaseDuration = 0 반환.
    /// </summary>
    public static class RecipeDefs
    {
        public static RecipeDef Get(Commodity output)
        {
            switch (output)
            {
                case Commodity.Grain:
                    // 무입력 생산(원재료 산지 stub) — Input=None/0이면 ProductionSystem의
                    //   DrawInput이 자연 통과(차감 없이 시작). 추후 채취(자원 레이어 소모)로 대체.
                    return new RecipeDef
                    {
                        Input        = Commodity.None,
                        InputAmount  = 0,
                        Output       = Commodity.Grain,
                        OutputAmount = 2,
                        BaseDuration = 10f,  // 게임초 — 밀 레시피(2Grain/10s 소비)와 1:1 균형
                    };
                case Commodity.Flour:
                    return new RecipeDef
                    {
                        Input        = Commodity.Grain,
                        InputAmount  = 2,
                        Output       = Commodity.Flour,
                        OutputAmount = 1,
                        BaseDuration = 10f,  // 게임초
                    };
                case Commodity.Meal:
                    // 처리량 수정(2026-07-07): 구 Flour1→Meal2/8s는 수요(인구×허기) 대비 ~10배
                    //   부족(식당 아무리 지어도 Meal 즉시 소진, 허기 0.5 고착 — 유저 실측).
                    //   Flour1→Meal10/8s로 상향(한 주방이 다수 급식). 스텁 노브 — 밸런스 시 재조정.
                    return new RecipeDef
                    {
                        Input        = Commodity.Flour,
                        InputAmount  = 1,
                        Output       = Commodity.Meal,
                        OutputAmount = 10,
                        BaseDuration = 8f,
                    };
                case Commodity.IronOre:
                    // 무입력 채취(2026-07-19) — 재료는 footprint 아래 ResourceCell(Iron).
                    //   가용량 게이트·소모는 ProductionSystem의 ResourceExtractor 분기가 담당.
                    return new RecipeDef
                    {
                        Input        = Commodity.None,
                        InputAmount  = 0,
                        Output       = Commodity.IronOre,
                        OutputAmount = 2,
                        BaseDuration = 10f,  // ⚠ v1 스텁 — 제련 레시피 도입 때 균형
                    };
                case Commodity.Oil:
                    // 무입력 채취(해상 시추) — 재료는 footprint 아래 ResourceCell(Oil).
                    return new RecipeDef
                    {
                        Input        = Commodity.None,
                        InputAmount  = 0,
                        Output       = Commodity.Oil,
                        OutputAmount = 2,
                        BaseDuration = 10f,  // ⚠ v1 스텁 — 정제 레시피 도입 때 균형
                    };
                default:
                    return default;  // BaseDuration = 0 → 레시피 없음
            }
        }

        /// <summary>해당 품목의 레시피가 존재하는가.</summary>
        public static bool HasRecipe(Commodity output)
            => Get(output).BaseDuration > 0f;
    }

    /// <summary>
    /// 건물 하나의 생산 상태. 레시피는 RecipeOutput 품목으로 조회.
    ///
    /// Progress 의미:
    ///   &lt; 0  → 대기(재료 부족 또는 출력 포화). 다음 틱에 재시도.
    ///   ≥ 0  → 진행 중. Progress 가 EffectiveDuration 에 도달하면 완료.
    ///
    /// 완료 시 출력 재고에 추가하고 Progress = -1 로 리셋(다음 사이클 재시도).
    /// Output이 Final 티어면 StockRole.LocalFinal 칸, 아니면 StockRole.Output 칸에 넣는다.
    /// </summary>
    public struct ProductionJob : IComponentData
    {
        /// <summary>이 건물이 만드는 품목. RecipeDefs.Get() 키.</summary>
        public Commodity RecipeOutput;

        /// <summary>
        /// (은퇴 2026-07-12 — 직무 효과는 범용 **StaffEffect** 컴포넌트로 이동: "산출 =
        /// 그 직무의 긍정 효과" 일반화. 진행 = dt × StaffEffect.Factor(무인 설계 = 1 폴백).
        /// 세이브 레이아웃 호환을 위해 필드만 유지 — 어떤 시스템도 읽고 쓰지 않는다.
        /// 다음 세이브 마이그레이션 때 삭제.)
        /// </summary>
        public float SkillFactor;

        /// <summary>현재 제작 진행(게임초). 음수면 대기. 반드시 -1f로 초기화할 것.</summary>
        public float Progress;

        /// <summary>대기 상태로 초기화된 기본값. SpawnSystem 등에서 사용.</summary>
        public static ProductionJob Make(Commodity output, float skillFactor = 1f)
            => new ProductionJob { RecipeOutput = output, SkillFactor = skillFactor, Progress = -1f };
    }
}
