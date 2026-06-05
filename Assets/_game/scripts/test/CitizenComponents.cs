using Unity.Entities;
using Unity.Mathematics;
using Game.Unit;

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
    //    [핫]   CitizenConditions, CitizenNeeds(or NeedElement buffer), CitizenState
    //    [콜드] CitizenAttributes, JobData
    //    [소속] CitizenResidence(집·직장), CitizenTeam(SharedComponent)
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
    //  핫: 욕구 (Needs)
    //  기존 NeedType(64bit Flags) 재사용.
    //
    //  모델 (Stage B 확정):
    //    - 부정 방향 게이지: 0 = 만족, 1 = 최악. 증가가 나빠짐(불만 누적).
    //    - 매 틱 Rate만큼 증가. 해결 행동(건물 방문)이 게이지를 감소시킴(Stage F).
    //    - 게이지 > Threshold → ActiveMask에 비트 ON("이제 해결 필요"·불만 표출·AI 신호).
    //    - 욕구 종류별 게이지가 필요하므로 DynamicBuffer<NeedElement> 사용.
    //
    //  게이지(연속) + 플래그(이산) 2층:
    //    게이지는 부드럽게 누적, 플래그는 행동/집계 트리거.
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenNeeds : IComponentData
    {
        /// <summary>임계치를 넘어 "미충족"으로 활성화된 욕구 비트(불만).</summary>
        public NeedType ActiveMask;

        /// <summary>이번 의사결정에서 추구 중인 욕구(단일 선택). None이면 미정.</summary>
        public NeedType Pursuing;
    }

    /// <summary>
    /// 욕구 종류별 게이지(부정 방향).
    /// Level 0 = 만족, 1 = 최악. 매 틱 Rate만큼 증가.
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct NeedElement : IBufferElementData
    {
        public NeedType Type;
        public float    Level;       // 0(만족) ~ 1(최악). 증가 = 나빠짐.
        public float    Rate;        // 틱당(초당) 증가 속도.
        public float    Threshold;   // 이 값 초과 시 ActiveMask 비트 ON.

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
    //  팀 소속: SharedComponentData (변하지/사라지지 않음 → 청크 분리)
    //  기존 TeamMask(Game.Unit) 재사용. 팀별 처리·필터가 거의 공짜.
    //  주의: SharedComponent는 1~2개로 절제(조합 폭발 방지).
    // ──────────────────────────────────────────────────────────────────────────
    public struct CitizenTeam : ISharedComponentData
    {
        public TeamMask Team;   // 슬롯 비트(+IsPlayer/IsPlayerTeam 플래그)

        public CitizenTeam(int teamIndex, bool isPlayer = false, bool isPlayerTeam = false)
        {
            Team = TeamInfoData.CreateTeamMask(teamIndex, isPlayerTeam, isPlayer);
        }
    }
}
