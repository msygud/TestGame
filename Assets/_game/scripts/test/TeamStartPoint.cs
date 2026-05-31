using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  TeamStartPoint  (ECS 컴포넌트)
    //
    //  맵에 찍어두는 팀 시작 위치.
    //  베이스 건물/유닛 배치는 로비에서 팩션이 결정된 후
    //  런타임에 FactionRegistry를 참조해 스폰한다.
    //
    //  보유 정보:
    //    Cell      — 그리드 시작 좌표
    //    TeamIndex — 포지션 번호 (0~7), 로비/인게임 UI에 표시
    // ══════════════════════════════════════════════════════════════
    public struct TeamStartPoint : IComponentData
    {
        /// <summary>시작 위치 그리드 셀.</summary>
        public int2 Cell;

        /// <summary>
        /// 포지션 번호 (0~7).
        /// 로비에서 플레이어/AI가 이 번호로 자리를 선택하고
        /// 팩션을 배정받는다.
        /// </summary>
        public int TeamIndex;
    }

    // ══════════════════════════════════════════════════════════════
    //  StartPointBuffer  (맵 저장용 버퍼)
    //
    //  GridMap 싱글톤 또는 MapData에서 모든 스타트 포인트를
    //  순회할 때 사용.
    // ══════════════════════════════════════════════════════════════
    [InternalBufferCapacity(8)]
    public struct StartPointElement : IBufferElementData
    {
        public int2 Cell;
        public int  TeamIndex;
    }
}
