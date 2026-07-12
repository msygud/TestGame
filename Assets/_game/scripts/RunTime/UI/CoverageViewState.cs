namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  CoverageView — 커버리지 표시 통합 상태 (정식 게임 UI 브리지, 2026-07-12)
    //
    //  "통합창길": 배치 프리뷰(PlacementCoverageOverlaySystem)가 커버 종류별 표시
    //  여부를 여기서 읽고, UI(GameHUD 커버리지 토글 — 씬 와이어링 선택, 미래의
    //  정식 설정 창)가 쓴다. 종류가 늘면(교육·의료 오라 등) 필드+마스크 비트 추가.
    //
    //  ECS 싱글톤이 아닌 정적 클래스인 이유: 읽는 쪽이 전부 매니지드 프레젠테이션
    //  (SystemBase/MonoBehaviour)이고, 표시 상태는 시뮬레이션 데이터가 아니다
    //  (ECS→UI 정적 미러 관례 — CoverageOverlaySystem.GlobalEnabled와 동형).
    // ══════════════════════════════════════════════════════════════
    public static class CoverageView
    {
        public static bool ShowWarehouse = true;   // 초록 — 창고 stamp 범위
        public static bool ShowSupply    = true;   // 주황 — 방문형(식당 등) stamp 범위
        public static bool ShowAura      = true;   // 보라 — 오라(치안 등) 범위

        /// <summary>변경 감지 키 — 프리뷰 메시 재구축 트리거.</summary>
        public static int Mask => (ShowWarehouse ? 1 : 0)
                                | (ShowSupply    ? 2 : 0)
                                | (ShowAura      ? 4 : 0);
    }
}
