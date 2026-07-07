using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DemandField — 미충족 욕구의 공간 집계 (욕구 주도 배치의 입력, 2026-07-07)
    // ──────────────────────────────────────────────────────────────────────────
    //  "어디에 무엇이 부족한가"를 저해상도 격자로 모은 수요 필드. 욕구 주도 배치
    //  (AI/유저가 무엇을·어디에 지을지 결정)의 입력이자 디버그 히트맵의 소스.
    //
    //  ★제네릭 설계(2026-07-07 확정): 특정 욕구(Hunger)에 묶지 않는다. key에 NeedType
    //    비트 인덱스를 포함 → 미래 욕구(Thirst·여가·의료…)가 자동으로 자기 레이어를 가진다.
    //    공통 집계기 하나가 모든 욕구를 처리(욕구별 코드 복제 없음).
    //
    //  key   = int4(owner, demandX, demandY, needBitIndex)
    //  value = 그 수요셀에 사는 '미충족'(추구 중인데 못 해소) 시민 수.
    //
    //  더블 버퍼: DemandAggregationSystem이 back에 재계산 → 완료 시 front와 스왑.
    //  독자(히트맵/배치)는 front만 읽는다(불변 스냅샷). Version = 렌더 캐시 키.
    // ══════════════════════════════════════════════════════════════════════════
    public struct DemandField : IComponentData
    {
        /// <summary>front(불변 스냅샷). key=int4(owner,dx,dy,needBit) → 미충족 시민 수.</summary>
        public NativeHashMap<int4, int> Counts;

        /// <summary>재계산마다 +1 — 렌더 캐시 무효화 키(히트맵이 버전 바뀔 때만 메시 재구축).</summary>
        public uint Version;
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
