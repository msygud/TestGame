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
    //    [소속] CitizenResidence(집·직장), CitizenOwner(SharedComponent, LocalId)
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
        public float Satiety;   // 포만도 — 배고픔/갈증 반영
        public float Energy;    // 활력   — 수면 반영, 노동효율 핵심
        public float Morale;    // 사기   — 사회·자아 욕구 반영
        public float Stress;    // 스트레스 — 누적 미충족(높을수록 나쁨)
        public float Health;    // 신체건강 — 질병·노동 누적
        public float Loyalty;   // 충성도 — 도시 전반 만족(이주 결정)

        public static CitizenConditions Healthy => new CitizenConditions
        {
            Satiety = 1f, Energy = 1f, Morale = 1f,
            Stress  = 0f, Health = 1f, Loyalty = 1f,
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

    // ──────────────────────────────────────────────────────────────────────────
    //  콜드: 직업 (Job)
    //  직업은 거의 안 바뀜. 숙련도 성장은 가끔(콜드 취급, 생산 시 결과만 핫쿼리).
    // ──────────────────────────────────────────────────────────────────────────
    public enum JobType : byte
    {
        Unemployed = 0,
        Farmer, Miner, Builder, Engineer, Merchant,
        Doctor, Teacher, Researcher, Artist, Administrator, Soldier,
    }

    public struct JobData : IComponentData
    {
        public JobType Job;
        public float   Skill;   // 숙련도 0~100. 능력치가 성장속도 결정.
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
    /// 아직 집 또는 직장이 배정되지 않은 시민에 붙는 태그.
    /// 배정 시스템은 이 태그를 가진 시민만 쿼리한다(매 프레임 전체 스캔 회피, §0-1).
    /// 집·직장이 모두 배정되면 CitizenAssignmentSystem이 이 태그를 제거.
    /// </summary>
    public struct UnassignedTag : IComponentData { }

    // ──────────────────────────────────────────────────────────────────────────
    //  동적 위치 + 상태머신
    //  "지금 어디서 무엇을 하나". 핫.
    // ──────────────────────────────────────────────────────────────────────────
    public enum CitizenActivity : byte
    {
        Idle = 0,        // 대기(다음 행동 미정)
        AtHome,          // 집에 있음
        AtWork,          // 직장에서 행동(생산)
        AtDestination,   // 목적지(욕구해소 등)에서 행동
        Traveling,       // 이동 중(예약 완료, 도착 타이머 대기)
        Stuck,           // 예약 실패 등으로 머묾(욕구 증가)
    }

    public struct CitizenState : IComponentData
    {
        public CitizenActivity Activity;

        /// <summary>현재 머무는/행동하는 건물. Traveling이면 Entity.Null.</summary>
        public Entity CurrentBuilding;

        /// <summary>지속 행동(근무·식사·수면 등) 종료 시각. 게임시간 기준.</summary>
        public double ActionEndTime;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  소유: 개별 플레이어 단위 (SharedComponentData — 변하지/사라지지 않음 → 청크 분리)
    //
    //  소유 단위는 "개별 플레이어(LocalId 0~7)" 하나로 통일한다. 팀 개념을 쓰지
    //  않음 — 건물·도로·stamp 슬롯·시민이 모두 같은 LocalId 축으로 정렬되어,
    //  교차 참조(시민→자기 stamp 슬롯, 도로 소유 검사 등)가 한 키로 떨어진다.
    //
    //  SharedComponent인 이유: LocalId는 생성 후 바뀌지 않으므로 청크가 플레이어
    //  별로 갈린다 → WithSharedComponentFilter(LocalId)로 한 플레이어 시민만 묶어
    //  그 플레이어의 stamp 슬롯으로 일괄 처리(거의 공짜). 단일 SharedComponent라
    //  조합 폭발 없음.
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenOwner : ISharedComponentData
    {
        public int LocalId;   // 소유 플레이어 슬롯 (0~7). stamp[LocalId] 등에 직접 사용.

        public CitizenOwner(int localId)
        {
            LocalId = localId;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  CitizenConfig — 시민 밸런스 (싱글톤, 없으면 Default). Test.cs 통합 패널이 push.
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenConfig : IComponentData
    {
        /// <summary>게임-시간당 owner별 이민 유입 상한. 0 = 유입 정지.
        /// 실제 유입 = min(이 값, 빈 거주 정원 − 집 미배정 대기자) — CitizenImmigrationSystem.</summary>
        public int ImmigrantsPerHourPerPlayer;

        public static CitizenConfig Default => new CitizenConfig
        {
            ImmigrantsPerHourPerPlayer = 4,
        };
    }
}
