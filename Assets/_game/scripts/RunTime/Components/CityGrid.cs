using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  CityGrid  — 팀별 도시 블록 그리드 파라미터 (IComponentData)
    //
    //  AI 도시 성장(AiCityGrowthSystem)이 블록식으로 자라기 위한 격자 정의.
    //  FactionBaseSpawnSystem이 베이스캠프 생성 시 팀 엔티티에 부착한다
    //  (Anchor=베이스 외곽 원점, Block=campSize, Road=roadSize → 베이스=블록(0,0)).
    //
    //  격자 규약 (Anchor에서 +X/+Z 방향으로만 확장 — 셀 음수 금지 규약과 일치):
    //    · 주기 P = Block + Road.
    //    · 도로 그리드 선: x = Anchor.x + k*P (Road 폭), z 동일.
    //    · 블록(bx,bz) 내부(건물 영역) 원점 = Anchor + (Road,Road) + (bx*P, bz*P), 크기 Block×Block.
    //    · 베이스(블록 0,0) 외곽 링이 그리드 선 0 / P 와 정렬되므로 새 블록 도로가 베이스와 연결된다.
    //      (그러려면 Block = campSize 여야 함 — 더 큰 블록을 원하면 BaseCampSize를 키운다.)
    // ══════════════════════════════════════════════════════════════
    public struct CityGrid : IComponentData
    {
        /// <summary>그리드 원점(베이스 외곽 링 좌하단 = originCell).</summary>
        public int2 Anchor;
        /// <summary>블록 내부 한 변 셀 수(=campSize). 건물 영역.</summary>
        public int  Block;
        /// <summary>도로 footprint 한 변 셀 수(=roadSize).</summary>
        public int  Road;
        /// <summary>이 팀의 팩션 ID(도로 배치 PlaceRoadCommand에 필요).</summary>
        public int  FactionId;

        /// <summary>세션별 랜덤 시드(성장 RNG에 섞음). 게임 시작마다 달라 패턴이 매번 다름.</summary>
        public uint Seed;

        /// <summary>그리드 주기(블록+도로).</summary>
        public int  Period => Block + Road;
    }
}
