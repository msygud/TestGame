using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Stage A — 시민 엔티티 골격
    //
    //  설계 원칙(§0):
    //    - 변하는 데이터(핫)가 시스템 주쿼리가 된다.
    //    - 핫(매 틱 변함, 자주 읽힘)은 작고 조밀하게, 콜드(불변/희소)는 분리.
    //    - 팀 소속은 변하지도 사라지지도 않으므로 SharedComponentData.
    //
    //  한 시민 엔티티 구성:
    //    [핫]   CitizenConditions, 욕구 컴포넌트(Hunger 등), CitizenNeeds, CitizenState
    //    [콜드] CitizenAttributes, JobData
    //    [소속] CitizenResidence(집·직장), OwnerShared(SharedComponent, LocalId — OwnerComponents.cs)
    //    [동적] CitizenLocation(현재 건물 / 이동중)
    //    + LocalTransform (Unity.Transforms)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>시민 식별 태그.</summary>
    public struct CitizenTag : IComponentData { }

    // ──────────────────────────────────────────────────────────────────────────
    //  콜드: 능력치 (Attributes)
    //  타고난 기질. 불변(또는 매우 느린 변화). 숙련 성장속도·욕구발생·컨디션저항.
    //  0~255 정규화(byte)로 캐시 절약. 필요시 계산에서 /255f.
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenAttributes : IComponentData
    {
        public byte Physique;      // 체력   — 노동강도 견딤, 컨디션 하락 저항
        public byte Intelligence;  // 지능   — 기술직 숙련 성장속도
        public byte Dexterity;     // 손재주 — 생산직 숙련 성장속도
        public byte Sociability;   // 사교성 — 사회적 욕구 충족 효율
        public byte Diligence;     // 성실성 — 숙련 성장 전반, 결근율
        public byte Resilience;    // 인내심 — 욕구 미충족 시 컨디션 하락 둔화
        public byte Creativity;    // 창의성 — 고급 직업·연구 보정

        public readonly float PhysiqueN     => Physique     / 255f;
        public readonly float IntelligenceN => Intelligence / 255f;
        public readonly float DexterityN    => Dexterity    / 255f;
        public readonly float SociabilityN  => Sociability  / 255f;
        public readonly float DiligenceN    => Diligence    / 255f;
        public readonly float ResilienceN   => Resilience   / 255f;
        public readonly float CreativityN   => Creativity   / 255f;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  핫: 컨디션 (Conditions)
    //  욕구 미충족의 결과. 생산 아웃풋·행동에 직접 영향. 매 틱 갱신.
    //  0~1 float. 작게 유지(주쿼리 핫 데이터).
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenConditions : IComponentData
    {
        public float Satiety;   // 포만도 — 배고픔/갈증 반영(투영: 1−Hunger.Level)
        public float Energy;    // 활력   — 수면 반영, 노동효율 핵심
        public float Morale;    // 사기   — 사회·자아 욕구 반영
        public float Stress;    // 스트레스 — 누적 미충족(높을수록 나쁨)
        public float Health;    // 신체건강 — 질병·노동 누적
        public float Loyalty;   // 충성도 — 도시 전반 만족(이주 결정)
        public float Safety;    // 안심도 — 치안 반영(투영: 1−CitizenSafety.Level, 미보유=1)

        public static CitizenConditions Healthy => new CitizenConditions
        {
            Satiety = 1f, Energy = 1f, Morale = 1f,
            Stress  = 0f, Health = 1f, Loyalty = 1f, Safety = 1f,
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  핫: 욕구 (Needs) — 개별 컴포넌트 모델
    //
    //  설계 전환 (버퍼 → 개별 컴포넌트):
    //    - 욕구는 종류별 개별 IComponentData. 팩션이 가진 욕구 컴포넌트 "조합"이
    //      곧 그 팩션을 정의한다(예: 휴먼 {Hunger, ...}, 메카닉 {EnergyLevel}).
    //    - 정적 부착: 스폰 시 팩션 조합대로 붙고, 게임 중 떼지 않는다. 해소는
    //      컴포넌트 제거가 아니라 Level 값 감소(구조 변경 0). 필요 시 추후
    //      IEnableableComponent로 일시 비활성 가능.
    //    - 욕구별 시스템: 각 욕구의 증가/해소를 전담(확장성). 한 시스템에 모든
    //      욕구 분기를 모으지 않는다(helper=사실, system=결정).
    //    - 부정 방향 게이지: 0=만족, 1=최악. 매 틱 Rate만큼 증가, 해소가 감소.
    //
    //  ※ 욕구 종류는 재조정될 수 있음 — 특정 6종에 묶지 않는다. 골격(필드 형태)을
    //    먼저 세우고(A-1: Hunger), 종류/팩션 조합은 이후 단계에서 확정.
    //  ※ ActiveMask(비트 집계) 제거: 결정 시스템이 욕구 컴포넌트를 직접 읽어
    //    추구 욕구를 ServiceTarget으로 넘긴다. 비트 집계 불필요.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>시민 의사결정 보조 — 현재 추구 중인 욕구(단일).
    /// 최종적으로 ServiceTarget.Relief로 일원화 예정(결정 시스템 도입 시).</summary>
    public struct CitizenNeeds : IComponentData
    {
        /// <summary>이번 의사결정에서 추구 중인 욕구. None이면 미정.</summary>
        public NeedType Pursuing;

        // ── 2패스 결정 후보(잡 간 통신, 2026-07-06) ─────────────────────────
        //  욕구별 긴급도 잡(Hunger 등, Burst 청크 순회)이 "자기 욕구가 더 급하면"
        //  아래 두 필드를 갱신 → 공통 선택 잡이 소비(Pursuing set) 후 리셋.
        //  구 메인스레드 HasComponent 랜덤 액세스 결정(인구 1.8만에서 정체 실측)의 대체.
        /// <summary>이번 프레임 최대 긴급도 욕구 후보.</summary>
        public NeedType CandidateNeed;
        /// <summary>후보의 긴급도(Level − Threshold). 욕구별 잡이 max 갱신.</summary>
        public float    CandidateUrgency;
    }

    /// <summary>
    /// 배고픔 욕구 게이지(부정 방향). Level 0=만족, 1=최악. 매 틱 Rate만큼 증가.
    /// 개별 욕구 컴포넌트의 첫 사례 — 다른 욕구도 같은 {Level,Rate,Threshold} 골격.
    /// (욕구마다 필드가 달라질 수 있으므로 공통 base 없이 개별 정의.)
    /// </summary>
    public struct Hunger : IComponentData
    {
        public float Level;       // 0(만족) ~ 1(최악).
        public float Rate;        // 틱당(초당) 증가 속도.
        public float Threshold;   // 이 값 초과 시 "활성"(해결 필요).

        public readonly bool IsActive => Level > Threshold;
    }

    /// <summary>
    /// 치안 불안 욕구(NeedType.HighCrime) — **커버형 욕구의 첫 사례**(2026-07-12).
    /// 방문·추구(Pursuing) 없음: 집이 오라(경찰서류 AuraSupplier) 커버 안이면 감소,
    /// 밖이면 증가(SafetySystem). NeedDecision/ServiceSearch 파이프라인을 전혀 안 탄다 —
    /// 수요는 "미커버 + 불안 시민"을 DemandAggregation이 직접 샘플(NoCoverage 채널).
    /// v1 효과: 수요 신호 전용(AI가 경찰서류를 짓게 함). 사기·이민·생산성 연결은 후속.
    /// 모양 규약(Level/Rate/Threshold + IsActive) 준수.
    /// </summary>
    public struct CitizenSafety : IComponentData
    {
        public float Level;       // 0(안심) ~ 1(최악).
        public float Rate;        // 게임초당 증가 속도(미커버 시). 커버 시 4배속 회복.
        public float Threshold;   // 초과 = 불안(수요 샘플 대상).

        public readonly bool IsActive => Level > Threshold;
    }

    /// <summary>
    /// 따분함 욕구(NeedType.LowEntertainment) — **체류형 욕구의 첫 사례**(2026-07-12).
    /// 방문은 하되 재화 소비가 없다: 공원류(StampSupplier + 재고 0)에 도착해 머무는 동안
    /// 시간 비례로 해소(BoredomSystem 적분 — 식당의 일괄 Level=0과 대비: 식당은 재화가
    /// 양자(quantum), 공원은 시간이 양자). 발견·이동·좌석은 공통 파이프라인 그대로
    /// (stamp 탐색 + VisitorOccupancy = 용량). 다 풀리면(Level 0) 머무름 타이머를 당겨
    /// 조기 퇴장 — 심심한 만큼 머물러 좌석 회전율이 수요를 반영한다.
    /// 모양 규약(Level/Rate/Threshold + IsActive) 준수.
    /// </summary>
    public struct CitizenBoredom : IComponentData
    {
        public float Level;       // 0(즐거움) ~ 1(최악).
        public float Rate;        // 게임초당 증가 속도. 체류 해소 = Rate × ReliefFactor(8배).
        public float Threshold;   // 초과 = 따분(추구 후보).

        public readonly bool IsActive => Level > Threshold;
    }

    /// <summary>
    /// 나이(콜드, 2026-07-12 생애주기 v1) — 불사 세계의 마찰 축: 무한 숙련의 루즈함을
    /// "관리 비용"으로 상쇄. AgingSystem이 게임 일 단위로 증가. 효과: ① 피로 증가 가산
    /// (EnergyTickJob — 40세부터 드레인 가중) ② 질병 싸움 불리(DiseaseFightSystem).
    /// </summary>
    public struct CitizenAge : IComponentData
    {
        /// <summary>나이(세, float — 게임 일마다 YearsPerGameDay씩 증가).</summary>
        public float Years;
    }

    /// <summary>
    /// 질병(NeedType.Disease) — 커버형(치안)·체류형(공원) 패턴의 합성(2026-07-12 생애주기 v1).
    ///   · 발병 = DiseaseFightSystem의 주기 롤(시민당 게임 1일 1회, 위상 분산): 체력·인내 저항,
    ///     나이 불리, 현재 위치가 병원 오라 커버면 유리. 패배 = Level 일괄 상승(앓아누움).
    ///   · 치료 = 병원 방문·입원(체류 적분, 공원 동형 — 완치 시 조기 퇴원) + 자연 회복(극저속,
    ///     병원 없는 초기 도시 붕괴 방지 — 불사 세계라 죽지 않고 오래 앓을 뿐).
    ///   · 노동 = 병세 중 결근(CollectWorkerProd가 cond.Health로 게이트 — 공통 시스템 무지 유지).
    /// 모양 규약(Level/Rate/Threshold + IsActive) 준수. Rate = 자연 회복률(음수 방향으로 사용).
    /// </summary>
    public struct CitizenSickness : IComponentData
    {
        public float Level;       // 0(건강) ~ 1(중병).
        public float Rate;        // 자연 회복률(게임초당 — 입원의 1/6 수준, 하루+ 소요).
        public float Threshold;   // 미사용(상태화 후) — Level>0이면 앓는 중. 하위호환 잔존.

        public readonly bool IsActive => Level > 0f;
    }

    /// <summary>
    /// 질병 = **상태**(2026-07-13 유저 재설계) — 욕구가 아니라 "일반 시민과 다른 생활로직"의
    /// 모드 전환. 발병(SicknessSystem 3게임시간 체크) 시 enable → 별도 쿼리(WithAll)가
    /// 다른 생활로직 수행(모든 것 중단·병원 직행, 막히면 귀가+재요청+수요 분출). 완치 시 disable.
    /// IEnableableComponent 토글 = 구조 변경 없는 상태 전환(청크 안정). 정상 욕구·근무 시스템은
    /// WithDisabled로 상태 시민을 자동 제외 → "다른 생활로직"이 자연스럽게 격리된다.
    /// (불사 원칙의 무력화→병원행·입원 중도 같은 상태 계열로 확장 예정.)
    /// </summary>
    public struct DiseasedTag : IComponentData, IEnableableComponent { }

    /// <summary>
    /// 헬스케어 커버 값(2026-07-13 유저 설계) — 병원 오라(NeedType.PoorHealthcare) 커버 여부에서
    /// 파생되는 **고정 상수·전원 동일** 값. 유일한 용도 = **질병 상태 진입 판정의 저항 입력**
    /// (컨디션·사기·생산성과 무연결). 오라 맵(AuraCoverage, (owner,셀) 키)이 "건물이 보유한 값"의
    /// 원천이고, 시민은 질병 체크 시점에 **현재 건물 셀의 커버로 통째 교체**(머티리얼라이즈) —
    /// 가감(±)이 아니라 replace라 "안에 있는 동안 값이 바뀌어도 퇴실 시 뺄 값" 드리프트가 없다.
    /// </summary>
    public struct CitizenHealthcare : IComponentData
    {
        public float Value;   // 0(미커버) ~ CoverBonus(커버). 질병 저항에 가산.
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  콜드: 직업 (Job)
    //  직업은 거의 안 바뀜. 숙련도 성장은 가끔(콜드 취급, 생산 시 결과만 핫쿼리).
    // ──────────────────────────────────────────────────────────────────────────
    public enum JobType : byte
    {
        Unemployed = 0,
        Farmer, Miner, Builder, Engineer, Merchant,
        Doctor, Teacher, Researcher, Artist, Administrator, Soldier,
        Officer,   // 경찰(치안 오라) — 관리형 서비스 전문직(2026-07-15). 새 서비스는 여기 append(서수 안정).
    }

    public struct JobData : IComponentData
    {
        public JobType Job;
        // Skill 필드 은퇴(2026-07-06, 고용 2차): 숙련은 직업별 개별 보유 —
        //   CitizenSkills[(int)Job] 단일 소스(이중 소스 금지).

        /// <summary>교대 슬롯(0~ShiftCount-1). 고용 시 라운드로빈 배정 — 건물이
        /// 운영시간 내내 staffed 되도록 근로자를 서브-근무창에 분산(2026-07-07).</summary>
        public byte Shift;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  콜드: 직업별 숙련(고용 2차, 2026-07-06) — 직업이 바뀌어도 각 숙련 보존.
    //  욕구처럼 종류별 개별 컴포넌트가 아닌 이유: 직업은 런타임 가변 선택(구조 변경
    //  회피) + 종류별 전담 시스템 불필요 → 고정 배열 1개(JobType 서수 인덱스).
    //  성장은 교대 종료 시 일괄(CitizenMoveJob 퇴근 분기 — 근무시간×적성).
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenSkills : IComponentData
    {
        /// <summary>JobType 값 수(Unemployed 포함) — enum과 일치 유지. Officer 추가(2026-07-15) → 13.
        /// FixedList64Bytes&lt;float&gt; 용량 15+라 여유. 구세이브(12칸) 시민은 Get(Officer=12)이 bounds
        /// 밖→0 반환(신참 base 0.5 기여), Add도 no-op — 크래시 없음(숙련만 미성장).</summary>
        public const int JobCount = 13;

        /// <summary>인덱스 = (int)JobType, 값 0~100.</summary>
        public FixedList64Bytes<float> Values;

        public readonly float Get(JobType job)
        {
            int i = (int)job;
            return (uint)i < (uint)Values.Length ? Values[i] : 0f;
        }

        /// <summary>숙련 가산(0~100 클램프).</summary>
        public void Add(JobType job, float delta)
        {
            int i = (int)job;
            if ((uint)i >= (uint)Values.Length) return;
            Values[i] = math.clamp(Values[i] + delta, 0f, 100f);
        }

        public static CitizenSkills Empty
        {
            get
            {
                var s = new CitizenSkills();
                for (int i = 0; i < JobCount; i++) s.Values.Add(0f);
                return s;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  직업 적성 테이블(정적 스위치, Burst-safe — RecipeDefs 패턴의 결정 테이블).
    //  인과 사슬(2026-07-12 재확정 — "물고 물리는 관계 해소", 유저):
    //      능력치(적성 2종+성실) + 근무시간 → 숙련  →  산출 ← 욕구(컨디션 가중)
    //  능력치는 **숙련 성장속도로만** 산출에 도달한다(직접 반영 없음 — 같은 날 도입했던
    //  ±15% 직접 항은 이중 경로(능력→숙련→산출 ∥ 능력→산출)라 철회). 재능은 "더 빨리
    //  커서 결국 더 많이 내는" 유일 경로 하나만 가진다. 유닛 스탯 머티리얼라이즈(미래)는
    //  Aptitude()를 복사 시점에 직접 읽는다 — 그건 산출 경로가 아니라 스폰 해석.
    // ──────────────────────────────────────────────────────────────────────────
    // ──────────────────────────────────────────────────────────────────────────
    //  직업별 근무 프로파일 + 교대(2026-07-07 유저 설계) — 결정 테이블.
    //  각 직업 = 운영시간 [Open,Close) + 교대수. 고용 시 근로자를 교대 슬롯에
    //  라운드로빈 분산 → 건물이 운영시간 내내 최소 1명 staffed(서비스 상시 이용가능).
    //    · 생산(농장·제분소): 기본 창(config WorkStart/End), 1교대.
    //    · 서비스(식당):      8~24, 2교대(8~16 / 16~24) — 조식~심야, 소비자 시간대 커버.
    //    · 물류(창고):        0~24, 3교대(0~8/8~16/16~24) — 24시간(미래 서비스 대비).
    //  ※ 무인 시 폐점(decision 1a): 서비스는 staffed(StaffEffect.Factor>0)일 때만 이용가능.
    //  ※ Close=24는 wrap 없음(Hour<24). 자정 넘는 창이 필요하면 wrap 추가.
    // ──────────────────────────────────────────────────────────────────────────
    public static class JobSchedule
    {
        public struct Window { public int Open; public int Close; public int Shifts; }

        /// <summary>직업 운영 프로파일. 생산 기본창은 config(defOpen/defClose) 사용.</summary>
        public static Window Profile(JobType job, int defOpen, int defClose) => job switch
        {
            JobType.Merchant      => new Window { Open = 8,  Close = 24, Shifts = 2 },  // 식당
            JobType.Administrator => new Window { Open = 0,  Close = 24, Shifts = 3 },  // 창고 24h
            JobType.Doctor        => new Window { Open = 0,  Close = 24, Shifts = 3 },  // 병원 24h(2026-07-13)
            JobType.Officer       => new Window { Open = 0,  Close = 24, Shifts = 3 },  // 경찰 24h(2026-07-15)
            _                     => new Window { Open = defOpen, Close = defClose, Shifts = 1 },
        };

        /// <summary>교대수(운영창과 무관 — 고용 시 슬롯 배정용).</summary>
        public static int ShiftCount(JobType job) => job switch
        {
            JobType.Merchant      => 2,
            JobType.Administrator => 3,
            JobType.Doctor        => 3,   // 병원 24h(2026-07-13)
            JobType.Officer       => 3,   // 경찰 24h(2026-07-15)
            _                     => 1,
        };
    }

    public static class JobAptitude
    {
        // ──────────────────────────────────────────────────────────────────
        //  직업별 컨디션 가중(2026-07-07 유저 설계 / 2026-07-12 치안 축 추가) — "당일
        //  효율"의 직업 차. 컨디션 계수 = 0.5 + 0.5 × Σ(가중치 × 욕구 만족도).
        //  육체직은 포만에 민감, 지식직은 피로·치안에 민감 → 노동 구성에 따라 어떤
        //  욕구를 채울지가 전략이 된다. 욕구 추가 = 여기 가중치 열 확장(합 1 유지,
        //  공통 시스템 무수정). Energy(피로)는 "휴식 욕구의 만족도"로 취급 — 수면
        //  욕구 도입 시 자연 통합.
        // ──────────────────────────────────────────────────────────────────
        /// <summary>컨디션 계수(0.5~1.0): energy=피로 회복도, satiety=포만도(1−Hunger),
        /// safety=안심도(1−CitizenSafety.Level, 미보유 팩션/구세이브 = 중립 1).</summary>
        public static float ConditionFactor(JobType job, float energy, float satiety, float safety)
        {
            // ⚠ 치안 가중 테스트 과장값(2026-07-12 유저 요청 "크게 차이 나도록") —
            //   커버/미커버가 staff에 최대 ±20%로 보이게 wSafety 0.40대. 밸런싱 #1에서
            //   본값(육체 .45/.45/.10, 혼합 .55/.35/.10, 지식 .60/.25/.15)으로 복원.
            float wEnergy, wSatiety, wSafety;
            switch (job)
            {
                // 육체직 — 밥이 반이다.
                case JobType.Farmer:
                case JobType.Miner:
                case JobType.Builder:
                case JobType.Soldier:
                    wEnergy = 0.30f; wSatiety = 0.30f; wSafety = 0.40f; break;

                // 혼합직.
                case JobType.Engineer:
                case JobType.Merchant:
                    wEnergy = 0.35f; wSatiety = 0.25f; wSafety = 0.40f; break;

                // 지식직 — 피로·치안 민감(여가·교육 욕구가 생기면 그쪽으로 가중 분배).
                default:
                    wEnergy = 0.35f; wSatiety = 0.20f; wSafety = 0.45f; break;
            }
            float satisfaction = wEnergy * energy + wSatiety * satiety + wSafety * safety;
            return 0.5f + 0.5f * math.saturate(satisfaction);
        }

        // ──────────────────────────────────────────────────────────────────
        //  직업 적성(능력치 2종 블렌드, 0~1) — 단일 소스(2026-07-12 통합).
        //  숙련 성장(GrowthFactor)·산출 계수(AptitudeOutputFactor)·(미래) 전투 유닛
        //  스탯 머티리얼라이즈가 전부 이 한 표를 읽는다(직업↔능력치 조합 drift 0).
        // ──────────────────────────────────────────────────────────────────
        public static float Aptitude(JobType job, in CitizenAttributes a) => job switch
        {
            JobType.Farmer        => 0.7f * a.PhysiqueN     + 0.3f * a.DexterityN,
            JobType.Miner         => 0.8f * a.PhysiqueN     + 0.2f * a.ResilienceN,
            JobType.Builder       => 0.5f * a.PhysiqueN     + 0.5f * a.DexterityN,
            JobType.Engineer      => 0.6f * a.IntelligenceN + 0.4f * a.DexterityN,
            JobType.Merchant      => 0.7f * a.SociabilityN  + 0.3f * a.IntelligenceN,
            JobType.Doctor        => 0.6f * a.IntelligenceN + 0.4f * a.DexterityN,
            JobType.Teacher       => 0.5f * a.IntelligenceN + 0.5f * a.SociabilityN,
            JobType.Researcher    => 0.7f * a.IntelligenceN + 0.3f * a.CreativityN,
            JobType.Artist        => 0.8f * a.CreativityN   + 0.2f * a.DexterityN,
            JobType.Administrator => 0.5f * a.IntelligenceN + 0.5f * a.SociabilityN,
            JobType.Soldier       => 0.6f * a.PhysiqueN     + 0.4f * a.ResilienceN,
            JobType.Officer       => 0.5f * a.PhysiqueN     + 0.5f * a.ResilienceN,   // 경찰(2026-07-15, 튜닝 가능)
            _                     => 0.5f,
        };

        /// <summary>숙련 성장 배율(~0.4×—1.9×): (0.5+적성) × 성실 보정(0.75~1.25).
        /// 능력치가 산출에 이르는 **유일한 경로**(성장속도) — 인과 사슬 주석 참조.</summary>
        public static float GrowthFactor(JobType job, in CitizenAttributes a)
            => (0.5f + Aptitude(job, in a)) * (0.75f + 0.5f * a.DiligenceN);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  소속: 정적 위치 (집·직장)
    //  "이 시민의 집/직장은 어디" — 시민의 속성. 잘 안 바뀜(콜드).
    //  건물이 거주자 명단을 들지 않음(§2.3, [A] 안티패턴 회피).
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenResidence : IComponentData
    {
        public Entity Home;   // 집 건물. Null이면 노숙(Homeless).
        public Entity Work;   // 직장 건물. Null이면 무직(Unemployed).
    }

    /// <summary>
    /// 집이 배정되지 않은 시민 태그(= 주거 대기 큐, 2026-07-06 재정의).
    /// 배정 시스템은 이 태그를 가진 시민만 쿼리한다(매 프레임 전체 스캔 회피, §0-1).
    /// 집 배정 시 CitizenAssignmentSystem이 제거. 집 소실 시 DeadReferenceReclaim이 재부착.
    /// </summary>
    public struct UnassignedTag : IComponentData { }

    /// <summary>
    /// 직장이 없는(구직) 시민 태그(= 고용 대기 큐 — 주거 큐와 분리, 2026-07-06).
    /// EmploymentAssignmentSystem이 소비: owner 일치 직장 배정 시 제거.
    /// 직장 소실 시 DeadReferenceReclaim이 재부착(재고용 큐 복귀).
    /// </summary>
    public struct JobSeekerTag : IComponentData { }

    // ──────────────────────────────────────────────────────────────────────────
    //  동적 위치 + 상태머신
    //  "지금 어디서 무엇을 하나". 핫.
    // ──────────────────────────────────────────────────────────────────────────
    public enum CitizenActivity : byte
    {
        Idle = 0,        // 대기(다음 행동 미정)
        AtHome,          // 집에 있음(휴식 — 시간 예산 모델의 기본 활동)
        AtWork,          // 직장에서 행동(생산). 종료 = 근무 시간대 이탈(절대 시각)
        AtDestination,   // 목적지(욕구해소 등)에서 행동
        Traveling,       // 이동 중 — 목적은 CitizenState.Purpose
        Stuck,           // 예약 실패 등으로 머묾(욕구 증가)
    }

    /// <summary>Traveling의 목적 — 도착 시 어떤 상태로 전이할지 결정(2026-07-06, 시간 예산 모델).</summary>
    public enum TravelPurpose : byte
    {
        None = 0,     // 미지정(레거시 — Service로 취급)
        Service,      // 욕구 공급자 방문(ServiceTarget)
        Home,         // 귀가(휴식)
        Work,         // 출근
    }

    public struct CitizenState : IComponentData
    {
        public CitizenActivity Activity;

        /// <summary>현재 머무는/행동하는 건물. Traveling이면 Entity.Null.</summary>
        public Entity CurrentBuilding;

        /// <summary>지속 행동(이동·식사 등) 종료 시각. 게임시간 기준. AtWork는 미사용(절대 시각 퇴근).</summary>
        public double ActionEndTime;

        /// <summary>Traveling의 목적(도착 전이 분기). 이동 외 상태에선 무의미.</summary>
        public TravelPurpose Purpose;
    }

    // 소유: OwnerShared(ISharedComponentData) — 모든 소유 엔티티 공통 골격이라
    //   CitizenComponents가 아니라 OwnerComponents.cs에 정의(시민 전용 아님).

    // ──────────────────────────────────────────────────────────────────────────
    //  CitizenConfig — 시민 밸런스 (싱글톤, 없으면 Default). Test.cs 통합 패널이 push.
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenConfig : IComponentData
    {
        /// <summary>게임-시간당 owner별 이민 유입 상한. 0 = 유입 정지.
        /// 실제 유입 = min(이 값, 빈 거주 정원 − 집 미배정 대기자) — CitizenImmigrationSystem.</summary>
        public int ImmigrantsPerHourPerPlayer;

        /// <summary>허기 증가율(게임초당, 스폰 시 ±15% 변주 적용). 끼니 주기 = 임계(0.6)/이 값.
        /// 0.010 = 게임 1분에 1끼(테스트 과속) / 0.0013 ≈ 하루 2~3끼(권장 현실 페이스).
        /// ⚠ 스폰 시점에 베이킹 — 기존 시민에겐 소급 안 됨(새 플레이/새 이민부터).</summary>
        public float HungerRatePerGameSec;

        /// <summary>근무 시작 시(게임 시간, 0~23). 출근 게이트 — 활동 우선순위 트리 ②.</summary>
        public int WorkStartHour;

        /// <summary>근무 종료 시(게임 시간, 0~24, Start보다 커야 함). 이 시각에 퇴근.</summary>
        public int WorkEndHour;

        /// <summary>근무 1게임시간당 숙련 기본 성장량(적성 배율 전). 10시간 근무 × 배율 ~1 = +10/일.</summary>
        public float SkillGrowthPerWorkHour;

        /// <summary>점심시간 시작 시(게임 시간). LunchGameHours가 0이면 무시.</summary>
        public int LunchStartHour;

        /// <summary>점심시간 길이(게임 시간). 0 = 점심 없음(논스톱 근무 — 기본).
        /// 켜면 그 창 동안 근무 게이트가 풀려 활동 트리가 "식당行→복귀"를 창발
        /// (직장 근처 식당 = 시간 예산 최적화).</summary>
        public int LunchGameHours;

        public static CitizenConfig Default => new CitizenConfig
        {
            ImmigrantsPerHourPerPlayer = 4,
            HungerRatePerGameSec       = 0.010f,
            WorkStartHour              = 8,
            WorkEndHour                = 18,
            SkillGrowthPerWorkHour     = 1.0f,
            LunchStartHour             = 12,
            LunchGameHours             = 0,
        };
    }
}
