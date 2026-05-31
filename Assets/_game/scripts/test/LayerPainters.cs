#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  지형 / 자원 타입 정의 (에디터 전용, 런타임 불필요)
    //  MapData.TerrainCellData.TypeId / ResourceCellData.TypeId 는
    //  단순 int 로 저장되므로 이 enum 은 에디터에서만 필요.
    // ══════════════════════════════════════════════════════════════

    public enum MapTerrainKind : int
    {
        Land = 0,
        Water = 1,
    }

    public enum MapResourceKind : int
    {
        None = 0,
        Coal = 10,
        Iron = 11,
        Gold = 12,
        Oil = 13,
        Crystal = 14,
    }

    // ── 지형 색상·이름 헬퍼 ────────────────────────────────────────
    public sealed class MapTerrainDefs
    {
        MapTerrainDefs() { }

        public static readonly MapTerrainKind[] All =
            { MapTerrainKind.Land, MapTerrainKind.Water };

        public static string NameOf(MapTerrainKind t) => t switch
        {
            MapTerrainKind.Land => "땅",
            MapTerrainKind.Water => "물",
            _ => t.ToString(),
        };

        public static Color ColorOf(MapTerrainKind t) => t switch
        {
            MapTerrainKind.Land => new Color(0.45f, 0.32f, 0.16f, 0.75f),
            MapTerrainKind.Water => new Color(0.15f, 0.45f, 0.90f, 0.75f),
            _ => Color.grey,
        };

        public static Color ColorWithHeight(MapTerrainKind t, byte height)
        {
            var c = ColorOf(t);
            float b = 0.55f + height / 15f * 0.45f;
            return new Color(c.r * b, c.g * b, c.b * b, c.a);
        }

        public static int TypeIdOf(MapTerrainKind t) => (int)t;
        public static MapTerrainKind FromId(int id) => (MapTerrainKind)id;
    }

    // ── 자원 색상·이름 헬퍼 ────────────────────────────────────────
    public sealed class MapResourceDefs
    {
        MapResourceDefs() { }

        public static readonly MapResourceKind[] All =
        {
            MapResourceKind.Coal, MapResourceKind.Iron, MapResourceKind.Gold,
            MapResourceKind.Oil,  MapResourceKind.Crystal,
        };

        public static string NameOf(MapResourceKind r) => r switch
        {
            MapResourceKind.Coal => "석탄",
            MapResourceKind.Iron => "철광석",
            MapResourceKind.Gold => "금",
            MapResourceKind.Oil => "석유",
            MapResourceKind.Crystal => "수정",
            _ => r.ToString(),
        };

        public static Color ColorOf(MapResourceKind r) => r switch
        {
            MapResourceKind.Coal => new Color(0.20f, 0.20f, 0.20f, 0.80f),
            MapResourceKind.Iron => new Color(0.45f, 0.55f, 0.70f, 0.80f),
            MapResourceKind.Gold => new Color(1.00f, 0.82f, 0.10f, 0.80f),
            MapResourceKind.Oil => new Color(0.35f, 0.10f, 0.45f, 0.80f),
            MapResourceKind.Crystal => new Color(0.30f, 0.90f, 0.95f, 0.80f),
            _ => Color.grey,
        };

        public static Color ColorWithAmount(MapResourceKind r, int amount)
        {
            var c = ColorOf(r);
            float b = 0.40f + Mathf.Clamp01(amount / 1000f) * 0.60f;
            return new Color(c.r * b, c.g * b, c.b * b, c.a);
        }

        public static int TypeIdOf(MapResourceKind r) => (int)r;
        public static MapResourceKind FromId(int id) => (MapResourceKind)id;
    }

    // ══════════════════════════════════════════════════════════════
    //  ILayerPainter
    // ══════════════════════════════════════════════════════════════
    public interface ILayerPainter
    {
        string LayerName { get; }
        void DrawToolbar();
        void Paint(Vector2Int cell, MapData map, BrushSettings brush);
        void Erase(Vector2Int cell, MapData map);
        bool TryGetCellColor(Vector2Int cell, MapData map, out Color color);
        bool WantsHeightOverlay { get; }
        bool TryGetHeightLabel(Vector2Int cell, MapData map, out string label);
        bool HandleScroll(Vector2Int cell, MapData map, float delta);
    }

    // ══════════════════════════════════════════════════════════════
    //  BrushSettings
    // ══════════════════════════════════════════════════════════════
    public class BrushSettings
    {
        public BrushShape Shape = BrushShape.Single;
        public int Size = 1;
        public BrushMode Mode = BrushMode.Paint;

        public IEnumerable<Vector2Int> GetCells(Vector2Int center, MapSettings s)
        {
            switch (Shape)
            {
                case BrushShape.Single:
                    if (InBounds(center, s)) yield return center;
                    break;

                case BrushShape.Square:
                    {
                        // Size = 한 변의 셀 수
                        // lo~hi 범위가 정확히 Size 칸이 되도록 계산
                        // 예) Size=2: lo=-1, hi=0 → 2칸  Size=3: lo=-1, hi=1 → 3칸
                        int lo = -(Size / 2);
                        int hi = (Size - 1) / 2;
                        for (int dx = lo; dx <= hi; dx++)
                            for (int dz = lo; dz <= hi; dz++)
                            {
                                var c = new Vector2Int(center.x + dx, center.y + dz);
                                if (InBounds(c, s)) yield return c;
                            }
                        break;
                    }

                case BrushShape.Circle:
                    {
                        // 지름 = Size 칸, 반지름 = Size/2
                        // 짝수 크기는 중심을 0.5 오프셋해 대칭 유지
                        float radius = Size / 2f;
                        int ext = Mathf.CeilToInt(radius);
                        float offset = (Size % 2 == 0) ? 0.5f : 0f;
                        for (int dx = -ext; dx <= ext; dx++)
                            for (int dz = -ext; dz <= ext; dz++)
                            {
                                float cx = dx + offset;
                                float cz = dz + offset;
                                if (cx * cx + cz * cz < radius * radius + 1e-5f)
                                {
                                    var c = new Vector2Int(center.x + dx, center.y + dz);
                                    if (InBounds(c, s)) yield return c;
                                }
                            }
                        break;
                    }
            }
        }

        static bool InBounds(Vector2Int c, MapSettings s)
            => c.x >= 0 && c.y >= 0 && c.x < s.Width && c.y < s.Height;

        public void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("브러시", GUILayout.Width(36));
            Shape = (BrushShape)EditorGUILayout.EnumPopup(Shape, GUILayout.Width(64));
            if (Shape != BrushShape.Single)
                Size = EditorGUILayout.IntSlider(Size, 1, 10);
            Mode = (BrushMode)EditorGUILayout.EnumPopup(Mode, GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();
        }
    }

    public enum BrushShape { Single, Square, Circle }
    public enum BrushMode { Paint, Erase }

    // ══════════════════════════════════════════════════════════════
    //  TerrainLayerPainter
    //
    //  서브모드:
    //    Type   — 타입(땅/물) + 높이로 셀 칠하기
    //    Height — 타입 유지, 높이만 변경
    //             · 슬라이더 또는 Ctrl+스크롤로 ±1 조절
    //             · 오버레이에 높이 숫자 표시
    // ══════════════════════════════════════════════════════════════
    public class TerrainLayerPainter : ILayerPainter
    {
        public string LayerName => "Terrain";

        enum SubMode { Type, Height }
        SubMode _sub = SubMode.Type;

        MapTerrainKind _selectedType = MapTerrainKind.Land;
        byte _selectedHeight = 0;

        public bool WantsHeightOverlay => _sub == SubMode.Height;

        public void DrawToolbar()
        {
            _sub = (SubMode)GUILayout.Toolbar((int)_sub,
                new[] { "타입 페인팅", "높이 편집" }, GUILayout.Height(22));
            EditorGUILayout.Space(6);

            if (_sub == SubMode.Type) DrawTypePalette();
            else DrawHeightPanel();
        }

        void DrawTypePalette()
        {
            EditorGUILayout.LabelField("지형 타입 선택", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            foreach (var t in MapTerrainDefs.All)
            {
                var col = MapTerrainDefs.ColorOf(t);
                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = _selectedType == t
                    ? new Color(col.r * 1.4f, col.g * 1.4f, col.b * 1.4f)
                    : new Color(col.r * 0.85f, col.g * 0.85f, col.b * 0.85f);
                if (GUILayout.Button(MapTerrainDefs.NameOf(t),
                        GUILayout.Width(80), GUILayout.Height(34)))
                    _selectedType = t;
                GUI.backgroundColor = prevBg;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            _selectedHeight = (byte)EditorGUILayout.IntSlider(
                $"함께 칠할 높이 ({_selectedHeight})", _selectedHeight, 0, 15);
        }

        void DrawHeightPanel()
        {
            EditorGUILayout.LabelField("높이 편집", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("▼", GUILayout.Width(28), GUILayout.Height(28))
                && _selectedHeight > 0) _selectedHeight--;
            _selectedHeight = (byte)EditorGUILayout.IntSlider(
                _selectedHeight, 0, 15, GUILayout.Height(28));
            if (GUILayout.Button("▲", GUILayout.Width(28), GUILayout.Height(28))
                && _selectedHeight < 15) _selectedHeight++;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "• 클릭/드래그: 높이 적용 (타입 유지)\n" +
                "• Ctrl + 스크롤: 마우스 아래 셀 ±1\n" +
                "• 오버레이: 높이 숫자 표시", MessageType.None);
        }

        public void Paint(Vector2Int cell, MapData map, BrushSettings brush)
        {
            if (_sub == SubMode.Type)
            {
                foreach (var c in brush.GetCells(cell, map.Settings))
                    map.TerrainDict[c] = new TerrainCellData
                    {
                        X = c.x,
                        Y = c.y,
                        TypeId = MapTerrainDefs.TypeIdOf(_selectedType),
                        Height = _selectedHeight,
                    };
            }
            else
            {
                foreach (var c in brush.GetCells(cell, map.Settings))
                {
                    if (map.TerrainDict.TryGetValue(c, out var ex))
                    { ex.Height = _selectedHeight; map.TerrainDict[c] = ex; }
                    else
                        map.TerrainDict[c] = new TerrainCellData
                        {
                            X = c.x,
                            Y = c.y,
                            TypeId = MapTerrainDefs.TypeIdOf(MapTerrainKind.Land),
                            Height = _selectedHeight,
                        };
                }
            }
        }

        public void Erase(Vector2Int cell, MapData map)
            => map.TerrainDict.Remove(cell);

        public bool HandleScroll(Vector2Int cell, MapData map, float delta)
        {
            if (_sub != SubMode.Height) return false;
            if (!map.TerrainDict.TryGetValue(cell, out var data)) return false;
            int nh = Mathf.Clamp(data.Height + (delta > 0 ? 1 : -1), 0, 15);
            data.Height = (byte)nh;
            _selectedHeight = (byte)nh;
            map.TerrainDict[cell] = data;
            return true;
        }

        public bool TryGetCellColor(Vector2Int cell, MapData map, out Color color)
        {
            if (map.TerrainDict.TryGetValue(cell, out var data))
            {
                color = MapTerrainDefs.ColorWithHeight(
                    MapTerrainDefs.FromId(data.TypeId), data.Height);
                return true;
            }
            color = Color.clear;
            return false;
        }

        public bool TryGetHeightLabel(Vector2Int cell, MapData map, out string label)
        {
            if (map.TerrainDict.TryGetValue(cell, out var data))
            { label = data.Height.ToString(); return true; }
            label = null;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  ResourceLayerPainter
    // ══════════════════════════════════════════════════════════════
    public class ResourceLayerPainter : ILayerPainter
    {
        public string LayerName => "Resource";
        public bool WantsHeightOverlay => false;

        MapResourceKind _selectedType = MapResourceKind.Coal;
        int _selectedAmount = 300;

        public void DrawToolbar()
        {
            EditorGUILayout.LabelField("지하 자원 선택", EditorStyles.miniBoldLabel);
            int cols = 3;
            var all = MapResourceDefs.All;
            for (int i = 0; i < all.Length; i += cols)
            {
                EditorGUILayout.BeginHorizontal();
                for (int c = 0; c < cols && i + c < all.Length; c++)
                {
                    var r = all[i + c];
                    var col = MapResourceDefs.ColorOf(r);
                    var prevBg = GUI.backgroundColor;
                    GUI.backgroundColor = _selectedType == r
                        ? new Color(col.r * 1.5f, col.g * 1.5f, col.b * 1.5f)
                        : new Color(col.r * 0.75f, col.g * 0.75f, col.b * 0.75f);
                    if (GUILayout.Button(MapResourceDefs.NameOf(r), GUILayout.Height(32)))
                        _selectedType = r;
                    GUI.backgroundColor = prevBg;
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"매장량 ({_selectedAmount})", GUILayout.Width(90));
            _selectedAmount = EditorGUILayout.IntSlider(_selectedAmount, 1, 1000);
            EditorGUILayout.EndHorizontal();
        }

        public void Paint(Vector2Int cell, MapData map, BrushSettings brush)
        {
            foreach (var c in brush.GetCells(cell, map.Settings))
                map.ResourceDict[c] = new ResourceCellData
                {
                    X = c.x,
                    Y = c.y,
                    TypeId = MapResourceDefs.TypeIdOf(_selectedType),
                    Amount = _selectedAmount,
                };
        }

        public void Erase(Vector2Int cell, MapData map)
            => map.ResourceDict.Remove(cell);

        public bool HandleScroll(Vector2Int cell, MapData map, float delta) => false;

        public bool TryGetCellColor(Vector2Int cell, MapData map, out Color color)
        {
            if (map.ResourceDict.TryGetValue(cell, out var data))
            {
                color = MapResourceDefs.ColorWithAmount(
                    MapResourceDefs.FromId(data.TypeId), data.Amount);
                return true;
            }
            color = Color.clear;
            return false;
        }

        public bool TryGetHeightLabel(Vector2Int cell, MapData map, out string label)
        { label = null; return false; }
    }
}
#endif
