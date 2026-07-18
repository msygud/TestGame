using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  채취(Extraction) — 자원 접근 1단계 골격 (2026-07-19)
    //
    //  "무입력 생산 변형": 채취 건물은 레시피 Input 대신 footprint 아래
    //  ResourceLayer 셀(ResourceCell)을 소모한다.
    //    · 사이클 시작 게이트: footprint 아래 매칭 TypeId 잔량(라이브 − pending)
    //      ≥ AmountPerCycle. 부족(고갈) = 유휴(Progress<0 유지, 재도색 시 자동 재개).
    //    · 소모 기록: 완료 시 ResourceExtractLedger.Pending에 셀 단위 적립(메인 전용).
    //    · 라이브 반영: **게임 시간당 1회** ProductionSystem이 CompleteAllTrackedJobs
    //      후 ResourceLayer.Amount에서 일괄 차감(확립 패턴: 폴링 스냅샷 잡과의 충돌은
    //      희소 쓰기 전 전체 동기화로 회피 — AiCityGrowth 재개발·AuraCoverage 발행 선례).
    //      독자(AI 스냅샷·배치 검증)에게 소모는 최대 1게임시간 지연 반영 = "결과 지연 허용".
    //
    //  물류 경로:
    //    · 육상(광산): 일반 건물 — 입구 도로 + 창고 stamp 커버리지로 풀 접속(기존 push).
    //    · 해상(시추): OffshoreSupplier — 도로/stamp 없음. 항만 창고(WarehouseTag.SeaRange)
    //      유클리드 반경으로 풀 접속(OffshorePushSystem). 전역 풀 = 1단계 합의(텔레포트,
    //      3단계 군집 분리·실체 호송 때 물리화).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 채취 능력(per-MainKey, 프리팹 컴포넌트 — BuildingAuthoring 베이크).
    /// "무엇을 캐고 사이클당 얼마나 소모하나"의 사실. 결정(어디 지을지)·해석(키→프리팹)과
    /// 분리 — 배치 검증·생산이 이 컴포넌트를 읽는다.
    /// </summary>
    public struct ResourceExtractor : IComponentData
    {
        /// <summary>맵 자원 타입(ResourceCatalog TypeId — Iron=2, Oil=7 등).
        /// ResourceCell.TypeId와 일치하는 셀만 채취 대상.</summary>
        public int ResourceTypeId;

        /// <summary>생산 1사이클당 footprint 아래에서 소모하는 자원량.</summary>
        public int AmountPerCycle;
    }

    /// <summary>
    /// 해상 공급자 태그(per-MainKey, 프리팹 컴포넌트). 도로 입구·stamp 커버리지가 불가능한
    /// 수상 건물 — 출력 배출은 항만 창고(WarehouseTag.SeaRange) 반경으로 풀 접속
    /// (OffshorePushSystem 소관). 입구 기반 LogisticsPushSystem 쿼리(BuildingEntrance)에는
    /// 애초에 안 걸린다(입구 없음).
    /// </summary>
    public struct OffshoreSupplier : IComponentData { }

    // ══════════════════════════════════════════════════════════════════════════
    //  유조선(Tanker) — 해상 실체 운송 (2026-07-19, "석유는 배로")
    //
    //  육상 풀이 아직 텔레포트(캐리어=비주얼)인 것과 달리, 해상은 처음부터 실체 운송:
    //  배가 실제 재고를 싣고 시추↔항만을 왕복한다(격침 = 화물 소실 — 보급선 습격의 기반).
    //  이동은 유닛 이동 골격 재사용(NavalUnit + MoveOrderRequest → 물 도메인 A*) —
    //  군함과 같은 기계를 공유한다(전투 재사용 요구).
    //  유조선 프리팹 미제작 시 OffshorePushSystem의 텔레포트 폴백이 유지된다(레거시 스텁 규약).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>유조선 운항 단계.</summary>
    public enum TankerPhase : byte
    {
        Docked = 0,   // 항만 대기(빈 배) — 일감(출력 쌓인 시추) 탐색
        ToRig,        // 시추로 항해 중
        ToPort,       // 화물 싣고 항만으로 항해 중(하역 재시도 포함)
    }

    /// <summary>
    /// 유조선 상태(인스턴스). TankerSystem이 스폰 시 부착·구동(메인, 저빈도 게이트).
    /// 화물은 실체 — 시추 Output에서 차감해 싣고, 항만 도착 시 풀에 하역.
    /// </summary>
    public struct OilTanker : IComponentData
    {
        public Entity      HomePort;        // 소속 항만 창고(SeaRange>0)
        public Entity      TargetRig;       // 현재 목표 시추(ToRig 단계)
        public TankerPhase Phase;
        public Commodity   CargoCommodity;  // 싣고 있는 품목(None = 빈 배)
        public int         Cargo;           // 현재 적재량
        public int         Capacity;        // 최대 적재량
    }

    /// <summary>
    /// 유조선 프리팹 싱글톤(TankerPrefabAuthoring 베이크). 프리팹은 UnitAuthoring +
    /// NavalUnitAuthoring 기반 수상 유닛이어야 이동·전투 골격이 작동한다.
    /// 없으면 해상 물류는 텔레포트 폴백(OffshorePushSystem) — 기능 저하 없이 동작.
    /// </summary>
    public struct TankerPrefabSingleton : IComponentData
    {
        public Entity Prefab;
        public int    Capacity;   // 적재량(프리팹당 정책 — authoring 값)
    }

    /// <summary>
    /// 채취 소모 원장(싱글톤, 메인 전용 — StockInheritance 패턴). 셀 → 미반영 소모량.
    /// 쓰기: ProductionSystem(사이클 완료). 읽기: ProductionSystem(가용량 게이트 = 라이브
    /// − pending). 반영·클리어: ProductionSystem 시간 경계(CompleteAllTrackedJobs 후).
    /// 수명주기: ProductionSystem OnCreate/OnDestroy.
    /// </summary>
    public struct ResourceExtractLedger : IComponentData
    {
        public NativeHashMap<int2, int> Pending;

        /// <summary>이 셀의 미반영 소모량(없으면 0). 메인 전용.</summary>
        public readonly int PendingAt(int2 cell)
            => Pending.IsCreated && Pending.TryGetValue(cell, out int v) ? v : 0;

        /// <summary>셀에 소모 적립. 메인 전용.</summary>
        public void Add(int2 cell, int amount)
        {
            if (amount <= 0 || !Pending.IsCreated) return;
            Pending.TryGetValue(cell, out int have);
            Pending[cell] = have + amount;
        }
    }
}
