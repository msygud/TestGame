using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  환경물(나무/바위 등) 컴포넌트
    //
    //  설계:
    //    - 환경물 비주얼 인스턴스마다 EnvironmentInstance(소속 셀)를 붙인다.
    //    - 도로/건물 배치로 셀을 덮으면 그 셀의 환경물을 "치운다":
    //      직접 destroy하지 않고 EnvironmentClearRequest(셀)를 발행한다.
    //      EnvironmentClearSystem이 해당 셀의 EnvironmentInstance를 일괄 destroy.
    //
    //  이전엔 한 셀의 다중 비주얼을 LinkedEntityGroup으로 묶어 부모를 ecb로
    //  destroy했으나, ECB로 구성한 LEG의 지연 엔티티 처리에서 playback 예외가
    //  나 배치 루프가 중단되는 문제가 있었다(도로가 환경물 직전까지만 깔림).
    //  → 셀 기준 쿼리 destroy로 단순·견고하게 대체.
    // ══════════════════════════════════════════════════════════════

    /// <summary>환경물 비주얼 인스턴스. 소속 셀 기준으로 일괄 제거된다.</summary>
    public struct EnvironmentInstance : IComponentData
    {
        public int2 Cell;
    }

    /// <summary>환경물 제거 요청(단발성). 해당 셀의 모든 EnvironmentInstance를 destroy.</summary>
    public struct EnvironmentClearRequest : IComponentData
    {
        public int2 Cell;
    }
}
