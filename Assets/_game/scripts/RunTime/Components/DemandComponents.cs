using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DemandField — 미충족 욕구의 공간 집계 (욕구 주도 배치의 입력, 2026-07-08 개정)
    // ──────────────────────────────────────────────────────────────────────────
    //  "어디에 무엇이 왜 부족한가"를 저해상도 격자로 모은 수요 필드. 욕구 주도 배치
    //  (AI/유저가 무엇을·어디에 지을지 결정)의 입력이자 디버그 히트맵의 소스.
    //
    //  ★재정의(2026-07-08): "미충족 시민 수(재고)" → "시도 실패의 사유별 누적(흐름)".
    //    · 집계 단위 = 집이 아니라 **시도 위치**(추구를 시작한 현재 건물 셀 = 노출된 실수요).
    //    · 값 = 사유별 실패 누적(존재하는 동안 계속 누적 — 즉시성 불필요, 느슨한 집계).
    //    · 사유(ServiceOutcome)가 remedy를 가른다: NoCoverage=신설(WHERE=시민 위치) /
    //      Reached=상류·용량·노동(WHERE=공급자). "지어서 고칠 수 있는 실패"만 담긴다
    //      (잠금 중 시민은 검색 안 함 → 미포함, 영업시간 외는 게이트로 제외 = 오염 필터).
    //    · v1은 실패 강도만 — 요청/비율(성공 계측)은 후속 슬라이스.
    //
    //  ★제네릭 설계: 특정 욕구(Hunger)에 안 묶임. key에 NeedType 비트 인덱스 포함 →
    //    미래 욕구(Thirst·여가·의료…)가 자동으로 자기 레이어를 가진다.
    //
    //  key   = int4(owner, demandX, demandY, needBitIndex)
    //  value = DemandStat(사유별 실패 누적).
    //
    //  발행: DemandAggregationSystem이 누적 back에 접고(fold) 메인에서 front로 복사한다.
    //  독자(히트맵/배치)는 front만 읽는다(메인만 쓰므로 안전). Version = 렌더 캐시 키.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>수요셀·욕구별 실패 누적 통계(사유 2분류, 2026-07-08).</summary>
    public struct DemandStat
    {
        /// <summary>사거리 내 공급자 자체가 없음 → 신설(WHERE = 시민 위치).</summary>
        public int FailNoCoverage;
        /// <summary>도달했으나 서빙 실패(재고·만석·폐점) → 상류/용량/노동(WHERE = 공급자).</summary>
        public int FailReached;

        public readonly int Failures => FailNoCoverage + FailReached;
    }

    public struct DemandField : IComponentData
    {
        /// <summary>front(발행 스냅샷). key=int4(owner,dx,dy,needBit) → 사유별 실패 누적.</summary>
        public NativeHashMap<int4, DemandStat> Stats;

        /// <summary>발행마다 +1 — 렌더 캐시 무효화 키(히트맵이 버전 바뀔 때만 메시 재구축).</summary>
        public uint Version;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  DemandResource — DemandField 키의 4번째(resource id) 규약 (2026-07-08, B 공급망).
    //   시민 욕구와 건물 입력 부족을 한 축으로 통일:
    //     0 ~ 63  = 욕구 비트(NeedType tzcnt) — need→relief 결정(NeedLookupL2)
    //     64 +    = commodity(= id − 64)        — commodity→producer 결정
    //   AI는 이 id로 분기해 같은 배치 루프(델타)로 수렴 — "결정은 여럿, 해석은 하나".
    // ──────────────────────────────────────────────────────────────────────────
    public static class DemandResource
    {
        public const int CommodityBase = 64;
        public const int WarehouseId   = 128;   // 창고 라우팅 수요(commodity 무관 — 창고가 전 품목 경유)

        public static int       ForCommodity(Commodity c) => CommodityBase + (int)c;
        public static bool      IsCommodity(int resId)     => resId >= CommodityBase && resId < WarehouseId;
        public static bool      IsWarehouse(int resId)     => resId == WarehouseId;
        public static Commodity ToCommodity(int resId)     => (Commodity)(resId - CommodityBase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  수요셀 격자 — 실셀 CellSize×CellSize를 한 수요셀로(배치 구역 해상도, 노이즈 완화).
    //  그리드 규약(항상 0,0에서 시작·비음수)과 일관되게 floor 나눗셈.
    // ──────────────────────────────────────────────────────────────────────────
    public static class DemandGrid
    {
        public const int CellSize = 8;   // 8×8 실셀 = 1 수요셀

        public static int2 ToCell(int2 realCell)
            => new int2(FloorDiv(realCell.x, CellSize), FloorDiv(realCell.y, CellSize));

        /// <summary>수요셀 → 실셀 원점(좌하단).</summary>
        public static int2 ToRealOrigin(int2 demandCell)
            => new int2(demandCell.x * CellSize, demandCell.y * CellSize);

        static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }
    }
}
