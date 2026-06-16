using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  FactionDefinition  (ScriptableObject)
    //
    //  팩션 메타 정보 (이름/색상).
    //  프리팹 목록은 NeedMappingEntry(FactionFlags)로 관리.
    //  베리언트 설정은 VariantSettings SO + VariantProfile로 분리.
    //
    //  메뉴: Assets > Create > CitySim > Faction Definition
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Faction Definition",
        fileName = "FactionDef_New")]
    public class FactionDefinition : ScriptableObject
    {
        [Tooltip("전역 고유 팩션 ID.\n" +
                 "NeedMappingEntry.FactionFlags 및\n" +
                 "FactionBaseDefinition.FactionId와 반드시 일치.")]
        public int FactionId;

        [Tooltip("이 팩션이 속한 DLC 식별자.")]
        public int DlcId = 0;

        [Tooltip("팩션 이름 (UI 표시용).")]
        public string FactionName = "New Faction";

        [Tooltip("팩션 대표 색상 (미니맵/팀 표시).")]
        public Color FactionColor = Color.white;

        // VariantKey는 VariantSettings SO + VariantProfile로 이관.
        // (VariantSelectionWindow에서 유닛별 User/AI 독립 설정)
    }
}
