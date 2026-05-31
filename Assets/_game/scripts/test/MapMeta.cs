using System;
using System.Collections.Generic;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  MapMeta  — 경량 메타데이터 (맵 선택 화면용)
    //
    //  맵 전체 데이터(MapData)를 로드하기 전에
    //  목록 표시 및 DLC 접근 여부 판단에 사용.
    //
    //  Addressables:
    //    주소 = DlcAddressConfig.MapMeta(MapId)
    //    라벨 = DlcAddressConfig.LabelMapMeta
    //
    //  MapSettings, MapData, 배치 타입 등은
    //  CitySim.MapEditor 네임스페이스(MapData.cs)에 정의.
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class MapMeta
    {
        public string MapId;
        public string DisplayName;
        public string Description;
        public string ThumbnailAddress;

        // 맵 선택 화면 미리보기용 크기 정보
        public int   Width;
        public int   Height;
        public float CellSize;

        /// <summary>이 맵을 플레이하기 위해 필요한 DLC ID 목록.</summary>
        public List<int> RequiredDlcIds = new();

        // ── 접근 판단 헬퍼 ────────────────────────────────────────

        /// <summary>현재 플레이어가 이 맵에 접근 가능한가.</summary>
        public bool CanAccess()
            => DlcOwnershipService.CanAccess(RequiredDlcIds);

        /// <summary>미보유 DLC 목록 반환 (UI 잠금 표시용).</summary>
        public List<int> MissingDlcs()
            => DlcOwnershipService.MissingDlcs(RequiredDlcIds);
    }
}
