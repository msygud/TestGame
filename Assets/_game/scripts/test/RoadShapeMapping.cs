using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  RoadShapeMapping
    //
    //  RoadDir 비트마스크 ↔ (RoadShape, RotationY) 양방향 매핑.
    //
    //  비트 정의 (RoadDirection.cs와 일치):
    //    N = 1 << 0  (북, +Z)
    //    E = 1 << 1  (동, +X)
    //    S = 1 << 2  (남, -Z)
    //    W = 1 << 3  (서, -X)
    //
    //  기본 회전 (Rotation = 0):
    //    Straight: N+S (수직 직선)
    //    DeadEnd:  N (북쪽 한 방향)
    //    Corner:   N+E (ㄱ자)
    //    T:        N+E+S (서쪽 막힌 T)
    //    Cross:    N+E+S+W
    //
    //  회전 90도 단위로 16가지 비트마스크 모두 표현.
    // ══════════════════════════════════════════════════════════════
    public static class RoadShapeMapping
    {
        const byte N = 1 << 0;
        const byte E = 1 << 1;
        const byte S = 1 << 2;
        const byte W = 1 << 3;

        // ══════════════════════════════════════════════════════════
        //  비트마스크 → (Shape, Rotation)
        // ══════════════════════════════════════════════════════════
        public static bool TryGetShape(byte mask, out RoadShape shape, out int rotation90)
        {
            shape      = RoadShape.NotRoad;
            rotation90 = 0;

            int popCount = math.countbits((int)mask);

            switch (popCount)
            {
                case 0:
                    return false;   // 도로 없음

                case 1:
                    // DeadEnd: 한 비트만 켜짐
                    shape = RoadShape.DeadEnd;
                    if      ((mask & N) != 0) rotation90 = 0;
                    else if ((mask & E) != 0) rotation90 = 1;
                    else if ((mask & S) != 0) rotation90 = 2;
                    else if ((mask & W) != 0) rotation90 = 3;
                    return true;

                case 2:
                    // Straight 또는 Corner
                    if (mask == (N | S))      { shape = RoadShape.Straight; rotation90 = 0; return true; }
                    if (mask == (E | W))      { shape = RoadShape.Straight; rotation90 = 1; return true; }

                    // Corner: 두 인접 방향
                    if (mask == (N | E))      { shape = RoadShape.Corner; rotation90 = 0; return true; }
                    if (mask == (E | S))      { shape = RoadShape.Corner; rotation90 = 1; return true; }
                    if (mask == (S | W))      { shape = RoadShape.Corner; rotation90 = 2; return true; }
                    if (mask == (W | N))      { shape = RoadShape.Corner; rotation90 = 3; return true; }
                    return false;

                case 3:
                    // T자: 한 방향이 빠짐
                    if ((mask & W) == 0)      { shape = RoadShape.T; rotation90 = 0; return true; }   // N+E+S
                    if ((mask & N) == 0)      { shape = RoadShape.T; rotation90 = 1; return true; }   // E+S+W
                    if ((mask & E) == 0)      { shape = RoadShape.T; rotation90 = 2; return true; }   // S+W+N
                    if ((mask & S) == 0)      { shape = RoadShape.T; rotation90 = 3; return true; }   // W+N+E
                    return false;

                case 4:
                    shape = RoadShape.Cross;
                    rotation90 = 0;
                    return true;

                default:
                    return false;
            }
        }

        public static float RotationDegrees(int rotation90) => rotation90 * 90f;

        // ══════════════════════════════════════════════════════════
        //  (Shape, Rotation) → 비트마스크
        //  사용자가 도로 선택 + Alt+휠로 회전했을 때 결과 비트마스크 계산
        // ══════════════════════════════════════════════════════════
        public static byte ToBitmask(RoadShape shape, int rotation90)
        {
            byte basemask = shape switch
            {
                RoadShape.Straight => (byte)(N | S),
                RoadShape.DeadEnd  => N,
                RoadShape.Corner   => (byte)(N | E),
                RoadShape.T        => (byte)(N | E | S),
                RoadShape.Cross    => (byte)(N | E | S | W),
                _                  => 0,
            };

            // 0~3 회전 (90도씩)
            rotation90 = ((rotation90 % 4) + 4) % 4;
            return RotateMask(basemask, rotation90);
        }

        /// <summary>비트마스크 회전 (시계방향 90도 단위).</summary>
        public static byte RotateMask(byte mask, int rotation90)
        {
            for (int i = 0; i < rotation90; i++)
            {
                // N → E → S → W → N
                byte rotated = 0;
                if ((mask & N) != 0) rotated |= E;
                if ((mask & E) != 0) rotated |= S;
                if ((mask & S) != 0) rotated |= W;
                if ((mask & W) != 0) rotated |= N;
                mask = rotated;
            }
            return mask;
        }

        // ══════════════════════════════════════════════════════════
        //  비트 검사 헬퍼
        // ══════════════════════════════════════════════════════════
        public static bool HasBit(byte mask, int dirIndex)
            => (mask & (1 << dirIndex)) != 0;

        public static byte SetBit(byte mask, int dirIndex)
            => (byte)(mask | (1 << dirIndex));

        public static byte ClearBit(byte mask, int dirIndex)
            => (byte)(mask & ~(1 << dirIndex));

        /// <summary>방향 인덱스의 반대 방향.</summary>
        public static int OppositeDir(int dirIndex)
            => (dirIndex + 2) & 3;

        /// <summary>방향 인덱스의 셀 오프셋 (X, Z).</summary>
        public static int2 DirOffset(int dirIndex) => dirIndex switch
        {
            0 => new int2( 0,  1),  // N
            1 => new int2( 1,  0),  // E
            2 => new int2( 0, -1),  // S
            3 => new int2(-1,  0),  // W
            _ => int2.zero,
        };
    }
}
