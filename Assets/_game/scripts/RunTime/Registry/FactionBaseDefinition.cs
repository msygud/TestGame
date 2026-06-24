using System;
using System.Collections.Generic;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  BaseSpawnEntry  — 팩션 초기 베이스 배치 항목 1개
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class BaseSpawnEntry
    {
        [Tooltip("배치할 프리팹의 MainKey (GamePrefabRegistry 기준).")]
        public int MainKey;

        [Tooltip("0 = VariantProfile에서 해결 (유저·AI 설정 따름).\n" +
                 "1 이상 = 이 값으로 고정 (팩션 고유 외형 강제).")]
        public int VariantKeyOverride;

        [Tooltip("팀 스타트포인트 셀 기준 상대 오프셋 (XZ 그리드).")]
        public Vector2Int CellOffset;

        [Tooltip("Y축 회전 (도).")]
        public float RotationY;
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionBaseDefinition  (ScriptableObject)
    //
    //  팩션 하나의 게임 시작 초기 베이스 배치 정의.
    //  FactionId 하나당 하나의 SO를 생성한다.
    //
    //  FactionBaseAuthoring에 이 SO 목록을 등록하면
    //  Baker가 BakedFactionBase 버퍼로 굽는다.
    //
    //  메뉴: Assets > Create > CitySim > Faction Base Definition
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Faction Base Definition",
        fileName = "FactionBaseDef_New")]
    public class FactionBaseDefinition : ScriptableObject
    {
        [Tooltip("FactionDefinition의 FactionId와 반드시 일치해야 한다.")]
        public int FactionId;

        [Tooltip("초기 베이스캠프 영역 크기 (N×N 셀). 스타트포인트를 좌하단 기준으로 N×N 외곽 도로를 생성한다.")]
        [Min(3)]
        public int BaseCampSize = 8;

        [Header("Road Maintenance")]
        [Tooltip("도로 관리시설 건물의 MainKey. 이 프리팹은 GamePrefabRegistry에서 IsRoadMaintenance=true + " +
                 "입구(Entrances) 정의가 있어야 한다.\n" +
                 "0 = 베이스에 관리시설 자동 배치 안 함 ⚠ 그러면 이 팩션의 비-영구 도로가 coverage 밖이 되어 " +
                 "RoadDecaySystem이 며칠 뒤 철거한다(베이스 외곽 링만 영구 보존).")]
        public int MaintenanceMainKey;

        [Tooltip("관리시설 배치 위치 — 건물 영역 원점(buildOrigin) 기준 상대 셀.\n" +
                 "RequireRoadAccess=true이므로 입구가 외곽 링 도로에 닿는 자리여야 한다(보통 영역 가장자리).")]
        public Vector2Int MaintenanceCellOffset;

        [Tooltip("게임 시작 시 팀 스타트포인트 기준으로 배치될 건물·유닛 목록.")]
        public List<BaseSpawnEntry> BaseEntries = new();
    }
}
