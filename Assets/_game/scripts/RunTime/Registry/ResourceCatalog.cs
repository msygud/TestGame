using System.Collections.Generic;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  ResourceCatalog — 지하 자원 종류 정의 (ScriptableObject)
    //
    //  자원의 "종류 / 이름 / 에디터 색"을 데이터로 정의한다.
    //  · 맵에디터(ResourceLayerPainter): 이 카탈로그에서 팔레트·색을 읽고,
    //    셀에는 "양(Amount)"만 칠한다.
    //  · ResourceDebugVisualizer(런타임 디버그): 같은 색을 사용.
    //
    //  TypeId 는 MapData.ResourceCellData.TypeId 로 저장되는 정수 키.
    //  새 자원을 추가하려면 항목을 하나 추가하고 고유 TypeId 만 주면 된다.
    //
    //  생성: Assets > Create > CitySim > Resource Catalog
    //  런타임 디버그에서도 색을 쓰려면 에셋을 Resources 폴더에 두고
    //  이름을 "ResourceCatalog" 로 하면 자동 로드된다.
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(menuName = "CitySim/Resource Catalog", fileName = "ResourceCatalog")]
    public sealed class ResourceCatalog : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            public int    TypeId;        // MapData 에 저장되는 정수 키 (고유)
            public string DisplayName;   // 에디터 팔레트 표시 이름
            public Color  EditorColor;   // 에디터/디버그 오버레이 색
        }

        [Tooltip("자원 종류 목록. TypeId 는 고유해야 한다.")]
        public List<Entry> Entries = new();

        // ── 조회 헬퍼 ────────────────────────────────────────────
        public bool TryGet(int typeId, out Entry entry)
        {
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].TypeId == typeId) { entry = Entries[i]; return true; }
            entry = default;
            return false;
        }

        public string NameOf(int typeId)
            => TryGet(typeId, out var e) && !string.IsNullOrEmpty(e.DisplayName)
               ? e.DisplayName : $"#{typeId}";

        public Color ColorOf(int typeId)
            => TryGet(typeId, out var e) ? e.EditorColor : Color.grey;

        // ── 런타임 디버그용 자동 로드 (Resources/ResourceCatalog) ──
        static ResourceCatalog _runtimeCached;
        public static ResourceCatalog LoadRuntime()
        {
            if (_runtimeCached != null) return _runtimeCached;
            _runtimeCached = Resources.Load<ResourceCatalog>("ResourceCatalog");
            return _runtimeCached;
        }
    }
}
