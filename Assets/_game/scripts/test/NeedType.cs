using System;

namespace CitySim
{
    /// <summary>
    /// 시민/유닛이 겪는 부정적 상태(스트레스/위협)의 종류.
    /// 64비트 플래그이므로 최대 64개 정의 가능.
    ///
    /// 구역:
    ///   0x_0000_0000_0000_00XX  — 시민 기본 생활 (배고픔, 주거, 직장 등)
    ///   0x_0000_0000_0000_XX00  — 도시 인프라 (교통, 의료, 교육 등)
    ///   0x_0000_0000_00XX_0000  — 치안/재난
    ///   0x_0000_0000_XX00_0000  — 전투: 지상 위협
    ///   0x_0000_00XX_0000_0000  — 전투: 공중/원거리 위협
    ///   0x_XXXX_XX00_0000_0000  — 예약 (DLC / 추후 확장)
    ///
    /// 건물이나 유닛은 ReliefMask(ulong)로 자신이 해소하는
    /// NeedType 조합을 보유한다.
    /// </summary>
    [Flags]
    public enum NeedType : ulong
    {
        None = 0,

        // ── 시민 기본 생활 (bits 0~7) ──────────────────────────────
        Hunger          = 1uL << 0,  // 배고픔
        Homeless        = 1uL << 1,  // 주거지 없음
        Unemployed      = 1uL << 2,  // 직장 없음
        LowEntertainment= 1uL << 3,  // 여가/오락 부족
        LowReligion     = 1uL << 4,  // 종교/정신적 안정 부족
        LowEducation    = 1uL << 5,  // 교육 부족
        // bits 6~7 예약

        // ── 도시 인프라 (bits 8~15) ────────────────────────────────
        PoorTransport   = 1uL << 8,  // 교통 불편
        PoorHealthcare  = 1uL << 9,  // 의료 부족
        PoorSanitation  = 1uL << 10, // 위생/쓰레기 처리 부족
        PoorWater       = 1uL << 11, // 수도 부족
        PoorPower       = 1uL << 12, // 전력 부족
        // bits 13~15 예약

        // ── 치안 / 재난 (bits 16~23) ───────────────────────────────
        HighCrime       = 1uL << 16, // 치안 불안
        Fire            = 1uL << 17, // 화재
        Flood           = 1uL << 18, // 홍수/자연재해
        Disease         = 1uL << 19, // 전염병
        // bits 20~23 예약

        // ── 전투: 지상 위협 (bits 24~31) ──────────────────────────
        HeavyGroundUnit = 1uL << 24, // 중장갑 지상유닛 위협
        LightGroundUnit = 1uL << 25, // 경보병 위협
        Siege           = 1uL << 26, // 공성 무기 위협
        Cavalry         = 1uL << 27, // 기병 위협
        // bits 28~31 예약

        // ── 전투: 공중 / 원거리 위협 (bits 32~39) ─────────────────
        HeavyAirUnit    = 1uL << 32, // 중형 공중유닛 위협
        LightAirUnit    = 1uL << 33, // 경형 공중유닛 위협
        Ranged          = 1uL << 34, // 원거리 유닛 위협
        NavalUnit       = 1uL << 35, // 해상 유닛 위협
        // bits 36~39 예약

        // ── DLC / 추후 확장 (bits 40~63) ──────────────────────────
        // 이 구역은 DLC별 FactionRegistry에서 추가 정의하거나
        // 새 partial enum 파일로 확장하세요.
    }

    /// <summary>
    /// NeedType 플래그 조합 헬퍼.
    /// </summary>
    public static class NeedTypeOps
    {
        /// <summary>mask가 target의 비트를 하나라도 포함하는가.</summary>
        public static bool HasAny(this NeedType mask, NeedType target)
            => (mask & target) != 0;

        /// <summary>mask가 target의 비트를 모두 포함하는가.</summary>
        public static bool HasAll(this NeedType mask, NeedType target)
            => (mask & target) == target;

        /// <summary>need를 mask에 추가.</summary>
        public static NeedType Add(this NeedType mask, NeedType need)
            => mask | need;

        /// <summary>need를 mask에서 제거.</summary>
        public static NeedType Remove(this NeedType mask, NeedType need)
            => mask & ~need;

        /// <summary>두 마스크의 겹치는 Need 반환.</summary>
        public static NeedType Overlap(NeedType a, NeedType b)
            => a & b;
    }
}
