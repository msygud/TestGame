using System;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  RoadDir  — 도로 방향 비트마스크
    //
    //  4비트, 0x01~0x0F 의 15가지 조합.
    //  각 조합이 독립적인 프리팹에 1:1 매핑된다 (회전 없음).
    //
    //  비트 배치:
    //    N = 1 << 0  (북, +Z)
    //    E = 1 << 1  (동, +X)
    //    S = 1 << 2  (남, -Z)
    //    W = 1 << 3  (서, -X)
    // ══════════════════════════════════════════════════════════════
    [Flags]
    public enum RoadDir : byte
    {
        None = 0,
        N    = 1 << 0,
        E    = 1 << 1,
        S    = 1 << 2,
        W    = 1 << 3,
    }

    // ══════════════════════════════════════════════════════════════
    //  RoadDirOps  — 방향 비트 관련 정적 유틸리티
    // ══════════════════════════════════════════════════════════════
    public static class RoadDirOps
    {
        /// <summary>방향 인덱스 0~3 → 인접 셀 오프셋 (N, E, S, W 순).</summary>
        public static readonly int2[] Offsets =
        {
            new int2( 0,  1),   // 0: N  (+Z)
            new int2( 1,  0),   // 1: E  (+X)
            new int2( 0, -1),   // 2: S  (-Z)
            new int2(-1,  0),   // 3: W  (-X)
        };

        /// <summary>방향 인덱스 0~3 → RoadDir 단일 비트.</summary>
        public static RoadDir FromIndex(int index) => (RoadDir)(1 << index);

        /// <summary>반대 방향 인덱스 반환 (0↔2, 1↔3).</summary>
        public static int OppositeIndex(int index) => (index + 2) & 3;

        /// <summary>켜진 비트 수 (이웃 연결 수).</summary>
        public static int PopCount(RoadDir dirs) => math.countbits((int)dirs);

        /// <summary>단일 비트인지 (막힌 끝, Dead-end).</summary>
        public static bool IsDeadEnd(RoadDir dirs) => PopCount(dirs) == 1;

        /// <summary>4방향 모두 열려있는지 (교차로, Cross).</summary>
        public static bool IsCross(RoadDir dirs) => dirs == (RoadDir.N | RoadDir.E | RoadDir.S | RoadDir.W);
    }
}
