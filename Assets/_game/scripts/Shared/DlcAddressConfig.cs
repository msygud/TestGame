namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  DlcAddressConfig
    //
    //  Addressables 주소 규칙을 한 곳에서 관리.
    //  런타임/에디터 모두 이 클래스를 통해 주소를 생성한다.
    //
    //  규칙 요약:
    //    SubScene   : "{DlcName}/SubScene"
    //    MapMeta    : "Maps/{MapId}/Meta"
    //    MapData    : "Maps/{MapId}/Data"
    //    Registry SO: "{DlcName}/Registry"   label="registry"
    //
    //  프리팹은 SubScene 베이킹을 통해 ECS Entity로 제공되므로
    //  개별 프리팹 주소는 사용하지 않는다.
    //  (Addressables로 직접 GameObject를 로드하는 방식 ❌)
    // ══════════════════════════════════════════════════════════════
    public static class DlcAddressConfig
    {
        // ── 그룹 이름 ────────────────────────────────────────────────

        /// <summary>본편 그룹 이름. Local Build.</summary>
        public const string OriginGroupName = "Origin";

        /// <summary>DLC 그룹 이름 패턴. Remote Build.</summary>
        public static string DlcGroupName(string dlcName) => dlcName;

        // ── 주소 생성 ────────────────────────────────────────────────

        /// <summary>
        /// DLC SubScene 주소.
        /// 예: "Origin/SubScene", "DLC1/SubScene"
        /// </summary>
        public static string SubScene(string dlcName)
            => $"{dlcName}/SubScene";

        /// <summary>
        /// 맵 메타 파일 주소 (경량, 선택 화면에서 항상 로드).
        /// 예: "Maps/Forest01/Meta"
        /// </summary>
        public static string MapMeta(string mapId)
            => $"Maps/{mapId}/Meta";

        /// <summary>
        /// 맵 데이터 파일 주소 (전체, 실제 게임 진입 시 로드).
        /// 예: "Maps/Forest01/Data"
        /// </summary>
        public static string MapData(string mapId)
            => $"Maps/{mapId}/Data";

        /// <summary>
        /// GamePrefabRegistry SO 주소.
        /// 예: "Origin/Registry", "DLC1/Registry"
        /// label = "registry" 도 함께 부여.
        /// </summary>
        public static string Registry(string dlcName)
            => $"{dlcName}/Registry";

        // ── 라벨 ─────────────────────────────────────────────────────

        /// <summary>모든 GamePrefabRegistry SO에 붙이는 라벨.</summary>
        public const string LabelRegistry = "registry";

        /// <summary>모든 MapMeta 파일에 붙이는 라벨.</summary>
        public const string LabelMapMeta  = "map-meta";

        /// <summary>모든 MapData 파일에 붙이는 라벨.</summary>
        public const string LabelMapData  = "map-data";

        /// <summary>DLC 콘텐츠에 붙이는 라벨 (필터링용).</summary>
        public static string LabelDlc(string dlcName)
            => $"dlc:{dlcName.ToLower()}";
    }
}
