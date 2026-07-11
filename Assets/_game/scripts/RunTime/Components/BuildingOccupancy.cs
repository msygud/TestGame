using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Stage A — 건물 점유 (Occupancy)
    //
    //  설계(§2.1, §7):
    //    - 건물은 "누가 있나"(명단)를 들지 않는다. 카운트만 든다(흐름량).
    //    - 명단이 꼭 필요한 순간(UI)엔 시민 CurrentBuilding 역쿼리로 그때 생성.
    //    - 예약 기반: 출발 시점에 목적지 Current++ (자리 맡기),
    //      도착은 상태만 변경(카운트는 이미 됨), 떠남/회수 시 Current--.
    //
    //  집·직장·공급자(식당 등) 모두 이 메커니즘 공유.
    //  주의: 이것은 인게임 건물 인스턴스(스폰된 엔티티)에 붙는다.
    //        GridLayers.OccupancyLayer(셀 점유)와는 다른 층 — 이쪽은 "정원/예약".
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 건물의 수용 정원과 현재 점유(예약 포함) 수.
    /// 예약 가능 여부 판정에 사용. 시민 명단이 아니라 카운트.
    /// </summary>
    public struct BuildingOccupancy : IComponentData
    {
        /// <summary>현재 점유 + 예약된 인원.</summary>
        public int Current;

        /// <summary>수용 정원.</summary>
        public int Capacity;

        public readonly bool HasRoom => Current < Capacity;
        public readonly int  Free    => Capacity - Current;

        /// <summary>예약 시도. 자리가 있으면 Current++ 하고 true.</summary>
        public bool TryReserve()
        {
            if (Current >= Capacity) return false;
            Current++;
            return true;
        }

        /// <summary>점유/예약 해제. 0 미만으로 내려가지 않음(정합성 가드).</summary>
        public void Release()
        {
            if (Current > 0) Current--;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  건물 분류 태그
    //  Intent 라우팅(§8) 및 집·직장·공급자 구분에 사용.
    //  ReliefMask(NeedType)는 별도 컴포넌트(추후 Stage C/F에서 연결).
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>거주 가능 건물(집). 시민 Home 배정 대상.</summary>
    public struct ResidenceBuilding : IComponentData { }

    /// <summary>일자리를 제공하는 건물(직장). 시민 Work 배정 대상.</summary>
    public struct WorkplaceBuilding : IComponentData
    {
        public JobType ProvidedJob;   // 이 직장이 제공하는 직업
    }

    /// <summary>
    /// 욕구를 해소하는 공급자 건물(식당·광장 등).
    /// 어떤 욕구를 해소하는지는 ReliefMask로(추후 연결).
    /// </summary>
    public struct ServiceBuilding : IComponentData
    {
        /// <summary>이 건물이 해소하는 NeedType 조합(ulong 비트).</summary>
        public NeedType ReliefMask;

        /// <summary>공급 영향력(도로 칸수). 반경 BFS 깊이(§4.2).</summary>
        public int Influence;
    }

    /// <summary>
    /// 방문(서비스 이용) 정원 — 예약 기반(2026-07-07): 출발 시 TryReserve(자리 맡기),
    /// 떠남/취소/거절 시 Release. 고용·거주 정원(BuildingOccupancy)과 분리 —
    /// 식당은 둘 다 가진다(일자리 4 + 좌석 N). 없으면 방문 무제한(하위호환).
    /// 원자성: 예약/해제는 서비스 데스크(단일 잡)와 복구 패스에서만 쓴다.
    /// </summary>
    public struct VisitorOccupancy : IComponentData
    {
        /// <summary>현재 방문(예약 포함) 수.</summary>
        public int Current;

        /// <summary>동시 수용 정원(좌석).</summary>
        public int Capacity;

        public readonly bool Full => Current >= Capacity;

        /// <summary>자리 예약 시도. 여유 있으면 Current++ 하고 true.</summary>
        public bool TryReserve()
        {
            if (Current >= Capacity) return false;
            Current++;
            return true;
        }

        /// <summary>자리 해제. 0 미만 방지(정합성 가드).</summary>
        public void Release()
        {
            if (Current > 0) Current--;
        }
    }

    /// <summary>
    /// 서비스 건물의 손님 누적 통계(2026-07-07) — 오늘/어제 접객 수.
    /// ServiceDeskJob이 서빙 성공마다 TodayServed++, DayChanged에 롤오버
    /// (Yesterday=Today, Today=0). 처리량·수요 추이 관찰용(인스펙터 표시).
    /// </summary>
    public struct ServiceStats : IComponentData
    {
        public int TodayServed;
        public int YesterdayServed;
    }

    /// <summary>
    /// 건물 내구 능력(다지기 ①, 2026-07-11) — BuildingAuthoring이 굽는 per-MainKey 값.
    /// 전투 컴포넌트 **부착 골격**(CombatTargetable/CombatHealth/CombatDestroyOnDeath)은
    /// 건물 공통이라 SpawnSystem이 계속 담당하고, CombatHealth **값**만 이 컴포넌트에서
    /// 읽는다(없으면 SpawnConfig.BuildingDefaultHealth 폴백). 전투 의미론(장갑·상성)은
    /// 유닛 재작성 때 — 그쪽은 이 값을 읽기만 한다(읽기/쓰기 분리).
    /// </summary>
    public struct BuildingDurability : IComponentData
    {
        public float MaxHealth;
    }

    /// <summary>
    /// 오라형 욕구 공급 능력(2026-07-11 합의 — 커버형 욕구: 경찰서·관공서·광장류).
    /// 방문·좌석·재고·물류 없음: 반경 안 시민의 해당 욕구가 수동적으로 해소된다.
    /// 판정 = footprint 최근접 유클리드 제곱(dx²+dz² ≤ Radius², 정수 — float/√ 없음).
    /// 소비: AuraCoverageSystem(맵 재빌드) → SafetySystem 등 욕구별 시스템(해소) +
    /// DemandAggregation(미커버 수요 수집) + AI 배치(지구 슬롯 선호).
    /// </summary>
    public struct AuraSupplier : IComponentData
    {
        /// <summary>오라가 해소하는 욕구 비트.</summary>
        public NeedType Relief;

        /// <summary>오라 반경(셀).</summary>
        public int Radius;
    }

    /// <summary>
    /// 오라 커버 맵 front 싱글톤(커버형 욕구 v1, 2026-07-12) — AuraCoverageSystem이
    /// 게임 시간당 1회 백그라운드 잡으로 재구축(무효화 회피: Clear 후 전체 재그리기,
    /// stamp 독트린과 동일) 후 발행. key = (owner, x, y) 실셀 — 소유자별 독립("공용
    /// 공간 + owner별 상태" 관례). value = 그 셀에 닿는 오라 relief 비트합.
    /// 독자(욕구 해소·수요 수집·배치 프리뷰)는 GetSingleton(RO)으로만 읽는다.
    /// </summary>
    public struct AuraCoverage : IComponentData
    {
        public NativeHashMap<int3, ulong> Map;
        public uint Version;
    }
}
