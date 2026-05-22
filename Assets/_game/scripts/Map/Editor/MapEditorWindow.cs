using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Rendering;
using Game.Utility;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  MapEditorWindow
    //
    //  메뉴: Tools > CitySim > Map Editor
    //
    //  배치 조작:
    //    좌클릭         = 배치 (Single/Multi)
    //    Shift + 드래그 = 셀 영역 다를 때만 배치
    //    Ctrl + 휠      = 높이 조정 (셀 0.5 단위)
    //    Alt + 휠       = Y축 90도 회전
    //    + / - / 0      = 스케일 조정
    //    Esc            = 선택 해제
    //
    //  씬 인스턴스 선택/편집:
    //    클릭            = 단일 선택
    //    Shift + 클릭    = 추가 선택 (다중)
    //    Delete          = 선택된 모두 삭제
    //
    //  도로는 단계 C에서 추가됨.
    // ══════════════════════════════════════════════════════════════
    public class MapEditorWindow : EditorWindow
    {
        // ── 맵 데이터 ──────────────────────────────────────────────
        MapData currentMap;
        string  mapName     = "NewMap";
        string  mapSavePath = "";

        // ── 레지스트리 SO ──────────────────────────────────────────
        List<GamePrefabRegistry> registries = new();

        // ── 선택 상태 (배치할 프리팹) ───────────────────────────────
        int   selectedMainKey    = int.MinValue;
        int   selectedVariantKey = 0;

        // ── 배치 파라미터 ──────────────────────────────────────────
        float instanceScale     = 1f;
        float instanceRotationY = 0f;
        float previewHeight     = 0f;
        RoadPlacementMode roadPlacementMode = RoadPlacementMode.Continuous;

        // ── 점유 셀 데이터 (맵에디터 전용) ─────────────────────────
        // (cell) → (placement type, placement index)
        Dictionary<OccupancyKey, OccupancyEntry> occupiedCells = new();

        // ── Shift 드래그 상태 ─────────────────────────────────────
        int2 lastPlacedCellOrigin = new int2(int.MinValue, int.MinValue);
        bool hasRoadContinuousStart;
        int2 roadContinuousLastCell;
        int  roadContinuousSurfaceHeight;
        int  roadContinuousHeight;

        // ── 씬 인스턴스 ────────────────────────────────────────────
        List<ScenePlacement> scenePlacements = new();
        GameObject previewInstance;

        // 다중 선택 (씬 배치 인덱스들)
        HashSet<int> selectedSinglePlacements = new();
        HashSet<int> selectedMultiPlacements  = new();
        HashSet<int> selectedRoadPlacements   = new();

        // ── 도로 모드 베리언트 (일괄 적용) ─────────────────────────
        int selectedRoadVariant = 0;

        // ── 스타트포인트 모드 ──────────────────────────────────────
        bool isStartPointMode    = false;
        int  editingTeamNumber   = 1;        // 1~8
        bool isSettingPosition   = false;    // 클릭으로 위치 지정 중

        // 씬 스타트포인트 시각화 (팀별 구체 + 레이블)
        static readonly Color[] TeamColors = new Color[]
        {
            Color.clear,                        // 0 (미사용)
            new Color(1.0f, 0.2f, 0.2f, 0.9f), // 1 빨강
            new Color(0.2f, 0.4f, 1.0f, 0.9f), // 2 파랑
            new Color(0.2f, 0.8f, 0.2f, 0.9f), // 3 초록
            new Color(1.0f, 0.9f, 0.1f, 0.9f), // 4 노랑
            new Color(0.7f, 0.2f, 1.0f, 0.9f), // 5 보라
            new Color(1.0f, 0.5f, 0.1f, 0.9f), // 6 주황
            new Color(0.2f, 0.9f, 1.0f, 0.9f), // 7 하늘
            new Color(1.0f, 0.4f, 0.8f, 0.9f), // 8 분홍
        };

        bool foldStartPoint = true;

        // ── 카테고리 가시성 ────────────────────────────────────────
        Dictionary<PrefabObjectType, bool> objectTypeVisibility = new()
        {
            { PrefabObjectType.Terrain,    true },
            { PrefabObjectType.Road,       true },
            { PrefabObjectType.Building,   true },
            { PrefabObjectType.Unit,       true },
            { PrefabObjectType.Resource,   true },
            { PrefabObjectType.Decoration, true },
            { PrefabObjectType.Projectile, true },
            { PrefabObjectType.Vfx,        true },
            { PrefabObjectType.Other,      true },
        };

        // ── 그리드 스타일 ──────────────────────────────────────────
        Color gridColor      = new Color(1f, 1f, 1f, 0.3f);
        float gridLineWidth  = 1.5f;
        bool  drawGrid       = true;
        bool  drawGridLabels = true;
        int   gridLabelStep  = 5;
        float boundaryGridHeight = 0f;

        // ── UI 상태 ────────────────────────────────────────────────
        Vector2 scrollPos;
        bool showBoundary = true;
        bool showThumbnails = true;
        bool showPrefabVisuals = true;
        bool showPaintVisuals = true;

        bool foldSettings   = true;
        bool foldVariant    = true;
        bool foldInstance   = true;
        bool foldRegistries = false;
        bool foldPrefabList = true;
        bool foldVisibility = false;
        bool foldGrid       = false;
        bool foldTerrain    = true;
        bool foldMapValidation = true;
        bool foldDebug      = false;

        MapEditorTool activeTool = MapEditorTool.PrefabPlacement;
        MapTerrainType terrainPaintType = MapTerrainType.Land;
        int terrainPaintHeight = 0;
        Vector2Int terrainBrushSize = Vector2Int.one;
        bool terrainSnapToBrushSize = true;
        Vector2Int terrainRectMin = Vector2Int.zero;
        Vector2Int terrainRectMax = new Vector2Int(15, 15);
        bool drawTerrainCells = true;
        bool drawTerrainHeightBlocks = true;
        GameObject terrainOverlayGo;
        MeshFilter terrainOverlayFilter;
        MeshRenderer terrainOverlayRenderer;
        Mesh terrainOverlayMesh;
        Material terrainOverlayMaterial;
        bool terrainOverlayDirty = true;
        readonly List<int> placementHeightCandidates = new();
        readonly List<ValidationIssue> mapValidationIssues = new();
        bool mapValidationHasRun;

        Dictionary<(int dlcId, PrefabObjectType type), bool> objectTypeFolds = new();
        Dictionary<int, bool> dlcFolds = new();

        // ── 내부 ──────────────────────────────────────────────────
        struct ScenePlacement
        {
            public GameObject Go;
            public int        Index;          // MapData.Singles 또는 Multis 인덱스
            public PlacementKind Kind;
        }

        enum PlacementKind { Single, Multi, Road }
        enum RoadPlacementMode { Single, Continuous }
        enum MapEditorTool { PrefabPlacement, TerrainPaint }

        struct OccupancyEntry
        {
            public PlacementKind Kind;
            public int           Index;
        }

        struct PlacementSnap
        {
            public int2    Cell;
            public int     SurfaceHeight;
            public int     Height;          // Occupancy / placement height layer.
            public Vector3 Origin;
        }

        readonly struct OccupancyKey : System.IEquatable<OccupancyKey>
        {
            public readonly int2 Cell;
            public readonly int  Height;

            public OccupancyKey(int2 cell, int height)
            {
                Cell = cell;
                Height = height;
            }

            public bool Equals(OccupancyKey other)
                => Cell.Equals(other.Cell) && Height == other.Height;

            public override bool Equals(object obj)
                => obj is OccupancyKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + Cell.x;
                    hash = hash * 31 + Cell.y;
                    hash = hash * 31 + Height;
                    return hash;
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        //  창 열기 / 생명주기
        // ══════════════════════════════════════════════════════════
        [MenuItem("Tools/CitySim/Map Editor")]
        public static void ShowWindow()
        {
            var w = GetWindow<MapEditorWindow>("Map Editor");
            w.minSize = new Vector2(380, 600);
            w.Show();
        }

        void OnEnable()
        {
            if (currentMap == null)
                currentMap = new MapData
                {
                    MapName  = mapName,
                    Settings = new MapSettings { CellSize = 2f, Width = 50, Height = 50 },
                };
            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            DestroyPreview();
            ClearScenePlacements();
            DestroyTerrainOverlay();
        }

        // ══════════════════════════════════════════════════════════
        //  메인 GUI
        // ══════════════════════════════════════════════════════════
        void OnGUI()
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            DrawHeader();
            EditorGUILayout.Space(10);
            DrawMapSettings();
            EditorGUILayout.Space(8);
            DrawTerrainSection();
            EditorGUILayout.Space(8);
            DrawVariantSelector();
            EditorGUILayout.Space(8);
            DrawInstanceSettings();
            EditorGUILayout.Space(10);
            DrawRegistries();
            EditorGUILayout.Space(10);
            DrawPrefabList();
            EditorGUILayout.Space(10);
            DrawStartPointSection();
            EditorGUILayout.Space(8);
            DrawMapValidationSection();
            EditorGUILayout.Space(8);
            DrawVisibilitySection();
            EditorGUILayout.Space(8);
            DrawGridSection();
            EditorGUILayout.Space(8);
            DrawDebugSection();

            EditorGUILayout.EndScrollView();
        }

        // ══════════════════════════════════════════════════════════
        //  헤더 + 저장/로드
        // ══════════════════════════════════════════════════════════
        void DrawHeader()
        {
            EditorGUILayout.LabelField("Map Editor", EditorStyles.boldLabel);

            mapName = EditorGUILayout.TextField("Map Name", mapName);
            if (currentMap != null) currentMap.MapName = mapName;

            EditorGUILayout.BeginHorizontal();
            mapSavePath = EditorGUILayout.TextField("Save Path", mapSavePath);
            if (GUILayout.Button("...", GUILayout.Width(28)))
            {
                string path = EditorUtility.SaveFilePanel(
                    "Save Map", string.IsNullOrEmpty(mapSavePath)
                        ? Application.dataPath : System.IO.Path.GetDirectoryName(mapSavePath),
                    $"{mapName}.json", "json");
                if (!string.IsNullOrEmpty(path)) mapSavePath = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save Map"))  SaveMap();
            if (GUILayout.Button("Load Map"))  LoadMap();
            if (GUILayout.Button("Clear Map")) ClearMap();
            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════
        //  Map Settings
        // ══════════════════════════════════════════════════════════
        void DrawMapSettings()
        {
            foldSettings = EditorGUILayout.Foldout(
                foldSettings, "Map Settings", true, EditorStyles.foldoutHeader);
            if (!foldSettings) return;

            EditorGUI.indentLevel++;
            var s = currentMap.Settings;
            float newCs = Mathf.Max(0.1f, EditorGUILayout.FloatField("Cell Size", s.CellSize));
            int   newW  = Mathf.Max(1, EditorGUILayout.IntField("Width (cells)", s.Width));
            int   newH  = Mathf.Max(1, EditorGUILayout.IntField("Height (cells)", s.Height));

            if (newCs != s.CellSize || newW != s.Width || newH != s.Height)
            {
                s.CellSize = newCs; s.Width = newW; s.Height = newH;
                currentMap.Settings = s;
                SetTerrainOverlayDirty();
                SceneView.RepaintAll();
            }

            EditorGUILayout.LabelField(
                $"Total: {s.Width * s.Height} cells, " +
                $"{s.Width * s.CellSize} x {s.Height * s.CellSize} units");
            EditorGUI.indentLevel--;
        }

        void DrawTerrainSection()
        {
            foldTerrain = EditorGUILayout.Foldout(
                foldTerrain, "Terrain Cells", true, EditorStyles.foldoutHeader);
            if (!foldTerrain) return;

            EditorGUI.indentLevel++;

            var newTool = (MapEditorTool)GUILayout.Toolbar(
                (int)activeTool,
                new[] { "Prefab", "Terrain" });
            if (newTool != activeTool)
                SetActiveTool(newTool);

            bool newShowPaintVisuals = EditorGUILayout.Toggle("Show Paint Visuals", showPaintVisuals);
            if (newShowPaintVisuals != showPaintVisuals)
            {
                showPaintVisuals = newShowPaintVisuals;
                SetTerrainOverlayDirty();
                SceneView.RepaintAll();
            }

            EditorGUI.BeginDisabledGroup(!showPaintVisuals);
            bool newDrawTerrainCells = EditorGUILayout.Toggle("Show Terrain Layer", drawTerrainCells);
            if (newDrawTerrainCells != drawTerrainCells)
            {
                drawTerrainCells = newDrawTerrainCells;
                SetTerrainOverlayDirty();
                SceneView.RepaintAll();
            }
            bool newDrawHeightBlocks = EditorGUILayout.Toggle("Show Height Blocks", drawTerrainHeightBlocks);
            if (newDrawHeightBlocks != drawTerrainHeightBlocks)
            {
                drawTerrainHeightBlocks = newDrawHeightBlocks;
                SetTerrainOverlayDirty();
                SceneView.RepaintAll();
            }
            EditorGUI.EndDisabledGroup();

            terrainPaintType = (MapTerrainType)EditorGUILayout.EnumPopup("Terrain", terrainPaintType);
            terrainPaintHeight = Mathf.Clamp(EditorGUILayout.IntField("Height Step", terrainPaintHeight), 0, 255);
            EditorGUILayout.LabelField("Height Unit", "0.5 cell", EditorStyles.miniLabel);
            terrainBrushSize = Vector2Int.Max(
                Vector2Int.one,
                EditorGUILayout.Vector2IntField("Brush Size", terrainBrushSize));
            terrainSnapToBrushSize = EditorGUILayout.Toggle("Snap Brush To Size", terrainSnapToBrushSize);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Land"))
            {
                terrainPaintType = MapTerrainType.Land;
                terrainPaintHeight = 0;
            }
            if (GUILayout.Button("Water"))
            {
                terrainPaintType = MapTerrainType.Water;
                terrainPaintHeight = 0;
            }
            if (GUILayout.Button("Mountain"))
            {
                terrainPaintType = MapTerrainType.Mountain;
                terrainPaintHeight = 1;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Rectangle Edit", EditorStyles.miniBoldLabel);
            terrainRectMin = EditorGUILayout.Vector2IntField("Min Cell", terrainRectMin);
            terrainRectMax = EditorGUILayout.Vector2IntField("Max Cell", terrainRectMax);

            var normalizedMin = new int2(
                Mathf.Min(terrainRectMin.x, terrainRectMax.x),
                Mathf.Min(terrainRectMin.y, terrainRectMax.y));
            var normalizedMax = new int2(
                Mathf.Max(terrainRectMin.x, terrainRectMax.x),
                Mathf.Max(terrainRectMin.y, terrainRectMax.y));
            int rectWidth = math.max(0, normalizedMax.x - normalizedMin.x + 1);
            int rectHeight = math.max(0, normalizedMax.y - normalizedMin.y + 1);
            EditorGUILayout.LabelField(
                $"Rect: ({normalizedMin.x}, {normalizedMin.y}) ~ ({normalizedMax.x}, {normalizedMax.y})  {rectWidth} x {rectHeight}",
                EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Fill Rect"))
            {
                FillTerrainRect(normalizedMin, normalizedMax, terrainPaintHeight, terrainPaintType);
            }
            if (GUILayout.Button("Remove Rect"))
            {
                RemoveTerrainRect(normalizedMin, normalizedMax);
            }
            EditorGUILayout.EndHorizontal();

            int count = currentMap?.TerrainCells?.Count ?? 0;
            EditorGUILayout.LabelField($"Stored overrides: {count}", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear Terrain Overrides"))
            {
                if (EditorUtility.DisplayDialog("Clear Terrain Overrides",
                    "Clear all stored terrain cell overrides?", "Clear", "Cancel"))
                {
                    EnsureTerrainCells();
                    currentMap.TerrainCells.Clear();
                    SetTerrainOverlayDirty();
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Variant Selector
        // ══════════════════════════════════════════════════════════
        void SetActiveTool(MapEditorTool tool)
        {
            activeTool = tool;
            hasRoadContinuousStart = false;

            if (activeTool == MapEditorTool.TerrainPaint)
            {
                selectedMainKey = int.MinValue;
                DestroyPreview();
                ClearAllSelections();
            }

            if (activeTool == MapEditorTool.TerrainPaint)
                drawTerrainCells = true;

            SetTerrainOverlayDirty();
            Repaint();
            SceneView.RepaintAll();
        }

        void DrawVariantSelector()
        {
            // 도로 모드인지 확인
            var selItem = GetSelectedItem();
            bool isRoadMode = IsRoadItem(selItem);

            string title = isRoadMode
                ? "Road Variant (Apply to All)"
                : "Variant Selector";

            foldVariant = EditorGUILayout.Foldout(
                foldVariant, title, true, EditorStyles.foldoutHeader);
            if (!foldVariant) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (selectedMainKey == int.MinValue)
            {
                EditorGUILayout.LabelField(" ", GUILayout.Height(70));
                EditorGUILayout.EndVertical();
                return;
            }

            if (isRoadMode)
            {
                roadPlacementMode = (RoadPlacementMode)GUILayout.Toolbar(
                    (int)roadPlacementMode,
                    new[] { "Single", "Continuous" });
                EditorGUILayout.Space(4);
                DrawRoadVariantSelector();
            }
            else
            {
                var variants = new List<(RegistryItem item, GamePrefabRegistry source)>();
                foreach (var reg in registries)
                {
                    if (reg == null) continue;
                    foreach (var it in reg.Items)
                    {
                        if (it.IsDeleted || it.MainKey != selectedMainKey) continue;
                        variants.Add((it, reg));
                    }
                }
                variants.Sort((a, b) => a.item.VariantKey.CompareTo(b.item.VariantKey));

                EditorGUILayout.BeginHorizontal();
                foreach (var (item, source) in variants)
                    DrawVariantTile(item, source);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════
        //  도로 베리언트 일괄 셀렉터
        //
        //  모든 도로 RegistryItem(Straight/DeadEnd/Corner/T/Cross)이
        //  가진 VariantKey 합집합을 표시.
        //  사용자가 V를 선택하면 모든 RoadPlacement.VariantKey 일괄 변경
        //  → 씬 인스턴스 모두 재생성.
        // ══════════════════════════════════════════════════════════
        void DrawRoadVariantSelector()
        {
            // 모든 도로 항목의 VariantKey 합집합
            var roadVariants = new SortedSet<int>();
            foreach (var reg in registries)
            {
                if (reg == null) continue;
                foreach (var it in reg.Items)
                {
                    if (it.IsDeleted) continue;
                    if (!IsRoadItem(it)) continue;
                    roadVariants.Add(it.VariantKey);
                }
            }

            if (roadVariants.Count == 0)
            {
                EditorGUILayout.LabelField("(no road items)", EditorStyles.miniLabel);
                return;
            }

            const float tileSize = 70f;
            EditorGUILayout.BeginHorizontal();
            foreach (int vk in roadVariants)
            {
                bool isSel = (selectedRoadVariant == vk);
                var prevBg = GUI.backgroundColor;
                if (isSel) GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f);

                if (GUILayout.Button($"V{vk}", GUILayout.Width(tileSize), GUILayout.Height(tileSize/2)))
                {
                    if (selectedRoadVariant != vk)
                    {
                        selectedRoadVariant = vk;
                        ApplyRoadVariantToAll(vk);
                    }
                }

                GUI.backgroundColor = prevBg;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 모든 도로 placement의 VariantKey를 일괄 변경하고 씬 인스턴스 재생성.
        /// </summary>
        void ApplyRoadVariantToAll(int variantKey)
        {
            for (int i = 0; i < currentMap.Roads.Count; i++)
            {
                var p = currentMap.Roads[i];
                p.VariantKey = variantKey;
                currentMap.Roads[i] = p;
            }

            // 씬 인스턴스 모두 재생성 (도로만)
            for (int i = scenePlacements.Count - 1; i >= 0; i--)
            {
                if (scenePlacements[i].Kind != PlacementKind.Road) continue;
                if (scenePlacements[i].Go != null)
                    DestroyImmediate(scenePlacements[i].Go);
                scenePlacements.RemoveAt(i);
            }

            for (int i = 0; i < currentMap.Roads.Count; i++)
                InstantiateRoadAt(i);

            ApplyCategoryVisibility();
            SceneView.RepaintAll();
        }

        void DrawVariantTile(RegistryItem item, GamePrefabRegistry source)
        {
            const float tileSize = 70f;
            bool isSelected = selectedVariantKey == item.VariantKey;

            var prevBg = GUI.backgroundColor;
            if (isSelected) GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f);

            EditorGUILayout.BeginVertical(GUILayout.Width(tileSize));
            var rect = GUILayoutUtility.GetRect(tileSize, tileSize,
                GUILayout.Width(tileSize), GUILayout.Height(tileSize));

            if (GUI.Button(rect, GUIContent.none))
            {
                selectedVariantKey = item.VariantKey;
                ResetInstanceSettings(item);
                if (selectedMainKey != int.MinValue)
                    ResetInstanceSettings(GetSelectedItem());
                DestroyPreview();
                Repaint();
            }

            Texture2D preview = item.Prefab != null
                ? AssetPreview.GetAssetPreview(item.Prefab) : null;
            if (preview == null && item.Prefab != null
                && AssetPreview.IsLoadingAssetPreview(item.Prefab.GetInstanceID()))
                Repaint();

            if (preview != null)
                GUI.DrawTexture(new Rect(rect.x+2, rect.y+2, rect.width-4, rect.height-4),
                    preview, ScaleMode.ScaleToFit);

            var dlcRect = new Rect(rect.x+2, rect.yMax-14, rect.width-4, 12);
            EditorGUI.DrawRect(dlcRect, new Color(0,0,0,0.6f));
            GUI.Label(dlcRect, " " + (source?.DlcName ?? "?"),
                new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.LowerLeft,
                    normal    = { textColor = Color.white },
                    fontSize  = 9,
                });

            GUI.backgroundColor = prevBg;

            string label = string.IsNullOrEmpty(item.Name)
                ? $"V{item.VariantKey}" : item.Name;
            EditorGUILayout.LabelField(label,
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter },
                GUILayout.Width(tileSize));

            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════
        //  Instance Settings
        // ══════════════════════════════════════════════════════════
        void DrawInstanceSettings()
        {
            foldInstance = EditorGUILayout.Foldout(
                foldInstance, "Instance Settings", true, EditorStyles.foldoutHeader);
            if (!foldInstance) return;

            EditorGUI.indentLevel++;

            // 스케일
            EditorGUILayout.BeginHorizontal();
            float newScale = EditorGUILayout.FloatField("Scale (uniform)", instanceScale);
            if (GUILayout.Button("Reset", GUILayout.Width(50)))
            {
                // 프리팹 원본 스케일로 (균등 가정 시 첫 축)
                var p = GetSelectedPrefab();
                newScale = GetPrefabDefaultScale(p);
            }
            EditorGUILayout.EndHorizontal();
            instanceScale = Mathf.Max(0.01f, newScale);

            // 회전
            bool selectedIsRoad = IsRoadItem(GetSelectedItem());
            if (selectedIsRoad)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Rotation Y", "Road uses direction flags", EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                float newRot = EditorGUILayout.FloatField("Rotation Y", instanceRotationY);
                if (GUILayout.Button("0", GUILayout.Width(24)))   newRot = 0f;
                if (GUILayout.Button("90", GUILayout.Width(28)))  newRot = 90f;
                if (GUILayout.Button("180", GUILayout.Width(34))) newRot = 180f;
                if (GUILayout.Button("270", GUILayout.Width(34))) newRot = 270f;
                EditorGUILayout.EndHorizontal();
                instanceRotationY = ((newRot % 360f) + 360f) % 360f;
            }

            // 높이
            EditorGUILayout.BeginHorizontal();
            float newHeight = EditorGUILayout.FloatField("Height (Y)", previewHeight);
            if (GUILayout.Button("Reset", GUILayout.Width(50))) newHeight = 0f;
            EditorGUILayout.EndHorizontal();
            previewHeight = Mathf.Max(0f, newHeight);
            EditorGUILayout.LabelField("Placement Height", "Occupancy layer offset from terrain surface", EditorStyles.miniLabel);

            // 선택된 단일 배치 (단수일 때만 표시)
            if (selectedSinglePlacements.Count == 1 && selectedMultiPlacements.Count == 0
                && selectedRoadPlacements.Count == 0)
            {
                int idx = -1;
                foreach (var i in selectedSinglePlacements) { idx = i; break; }

                if (idx >= 0 && idx < currentMap.Singles.Count)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Selected Single Placement", EditorStyles.miniBoldLabel);

                    var p = currentMap.Singles[idx];
                    var selectedItem = FindRegistryItem(p.MainKey, p.VariantKey);
                    EditorGUI.BeginChangeCheck();
                    var newPos  = EditorGUILayout.Vector3Field("Position", p.Position);
                    var newRotY = EditorGUILayout.FloatField("Rotation Y", p.RotationY);
                    var newSc   = EditorGUILayout.FloatField("Scale", ResolveSavedScale(p.Scale, selectedItem?.Prefab));
                    if (EditorGUI.EndChangeCheck())
                    {
                        var u = p;
                        u.Position  = newPos;
                        u.Cell      = FootprintOriginFromPosition(newPos, GetFootprintSize(selectedItem));
                        int surfaceHeight = GetTerrainCellData(u.Cell).Height;
                        u.Height    = Mathf.Max(surfaceHeight, GetPlacementHeightIndex(newPos.y, selectedItem));
                        u.RotationY = newRotY;
                        u.Scale     = Mathf.Max(0.01f, newSc);
                        currentMap.Singles[idx] = u;
                        RebuildOccupancyAndRefs();
                    }

                    EditorGUILayout.Space(4);
                    GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                    if (GUILayout.Button("Delete Selected", GUILayout.Height(24)))
                    {
                        DeleteSelectedPlacements();
                        GUI.backgroundColor = Color.white;
                    }
                    GUI.backgroundColor = Color.white;
                }
            }
            else if (selectedSinglePlacements.Count + selectedMultiPlacements.Count
                   + selectedRoadPlacements.Count > 0)
            {
                int total = selectedSinglePlacements.Count
                          + selectedMultiPlacements.Count
                          + selectedRoadPlacements.Count;
                EditorGUILayout.LabelField(
                    $"Selected: {total} item(s)", EditorStyles.miniBoldLabel);

                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button("Delete Selected", GUILayout.Height(24)))
                    DeleteSelectedPlacements();
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.LabelField(
                "Ctrl+Wheel: Height | Alt+Wheel: Rot90 | +/-/0: Scale",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "Del: Delete Selected | Ctrl+RightClick: Delete Cell",
                EditorStyles.miniLabel);

            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Registries
        // ══════════════════════════════════════════════════════════
        void DrawRegistries()
        {
            foldRegistries = EditorGUILayout.Foldout(
                foldRegistries, "Registries", true, EditorStyles.foldoutHeader);
            if (!foldRegistries) return;

            EditorGUI.indentLevel++;
            for (int i = 0; i < registries.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                var newReg = (GamePrefabRegistry)EditorGUILayout.ObjectField(
                    registries[i], typeof(GamePrefabRegistry), false);
                if (newReg != registries[i]) { registries[i] = newReg; Repaint(); }
                if (GUILayout.Button("X", GUILayout.Width(24)))
                {
                    registries.RemoveAt(i);
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.indentLevel--;
                    return;
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ Add Registry")) registries.Add(null);

            EditorGUILayout.Space(6);
            DrawMergedValidation();
            EditorGUI.indentLevel--;
        }

        void DrawMergedValidation()
        {
            var issues = GamePrefabRegistry.ValidateMerged(registries);
            if (issues.Count == 0)
            {
                EditorGUILayout.LabelField("Validation: OK",
                    new GUIStyle(EditorStyles.miniLabel)
                        { normal = { textColor = new Color(0.3f, 0.8f, 0.3f) } });
                return;
            }
            foreach (var issue in issues)
            {
                MessageType mt = issue.Level == ValidationLevel.Error   ? MessageType.Error
                              :  issue.Level == ValidationLevel.Warning ? MessageType.Warning
                                                                        : MessageType.Info;
                EditorGUILayout.HelpBox(issue.Message, mt);
            }
        }

        void DrawMapValidationSection()
        {
            foldMapValidation = EditorGUILayout.Foldout(
                foldMapValidation, "Map Validation", true, EditorStyles.foldoutHeader);
            if (!foldMapValidation) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Run Map Validation"))
                ValidateCurrentMap();
            if (GUILayout.Button("Clear", GUILayout.Width(70)))
            {
                mapValidationIssues.Clear();
                mapValidationHasRun = false;
            }
            EditorGUILayout.EndHorizontal();

            if (!mapValidationHasRun)
            {
                EditorGUILayout.HelpBox("Run validation before in-game load testing.", MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            int errors = CountMapValidationIssues(ValidationLevel.Error);
            int warnings = CountMapValidationIssues(ValidationLevel.Warning);
            if (mapValidationIssues.Count == 0)
            {
                EditorGUILayout.LabelField("Map Validation: OK",
                    new GUIStyle(EditorStyles.miniLabel)
                        { normal = { textColor = new Color(0.3f, 0.8f, 0.3f) } });
                EditorGUI.indentLevel--;
                return;
            }

            EditorGUILayout.LabelField(
                $"Errors={errors}, Warnings={warnings}, Info={mapValidationIssues.Count - errors - warnings}",
                EditorStyles.miniLabel);

            foreach (var issue in mapValidationIssues)
            {
                MessageType mt = issue.Level == ValidationLevel.Error   ? MessageType.Error
                              : issue.Level == ValidationLevel.Warning ? MessageType.Warning
                                                                       : MessageType.Info;
                EditorGUILayout.HelpBox(issue.Message, mt);
            }

            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Prefab List
        // ══════════════════════════════════════════════════════════
        void DrawPrefabList()
        {
            foldPrefabList = EditorGUILayout.Foldout(
                foldPrefabList, "Prefabs (Click to Select)", true, EditorStyles.foldoutHeader);
            if (!foldPrefabList) return;

            var validRegs = new List<GamePrefabRegistry>();
            foreach (var r in registries) if (r != null) validRegs.Add(r);

            if (validRegs.Count == 0)
            {
                EditorGUILayout.HelpBox("No registries.", MessageType.Info);
                return;
            }

            showThumbnails = EditorGUILayout.Toggle("Show Thumbnails", showThumbnails);

            var byDlc = new SortedDictionary<int, List<(RegistryItem item, GamePrefabRegistry src)>>();
            foreach (var reg in validRegs)
                foreach (var it in reg.Items)
                {
                    if (it.IsDeleted) continue;
                    if (it.VariantKey != 0) continue;
                    if ((it.Usage & PrefabUsage.MapEditor) == 0) continue;

                    if (!byDlc.TryGetValue(reg.DlcId, out var l))
                        byDlc[reg.DlcId] = l = new();
                    l.Add((it, reg));
                }

            if (byDlc.Count == 0) return;

            foreach (var kv in byDlc)
            {
                int dlcId = kv.Key;
                string dlcName = kv.Value.Count > 0 ? kv.Value[0].src.DlcName : $"DLC {dlcId}";

                if (!dlcFolds.TryGetValue(dlcId, out bool dlcOpen)) dlcOpen = true;
                dlcOpen = EditorGUILayout.Foldout(dlcOpen,
                    $"{dlcName} ({kv.Value.Count})", true, EditorStyles.foldoutHeader);
                dlcFolds[dlcId] = dlcOpen;
                if (!dlcOpen) continue;

                EditorGUI.indentLevel++;
                DrawDlcCategories(dlcId, kv.Value);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(5);
            DrawSelectionInfo();
        }

        void DrawDlcCategories(int dlcId,
            List<(RegistryItem item, GamePrefabRegistry src)> items)
        {
            var byType = new SortedDictionary<PrefabObjectType,
                List<(RegistryItem item, GamePrefabRegistry src)>>();
            foreach (var pair in items)
            {
                if (!byType.TryGetValue(pair.item.ObjectType, out var l))
                    byType[pair.item.ObjectType] = l = new();
                l.Add(pair);
            }

            foreach (var kv in byType)
            {
                var key = (dlcId, kv.Key);
                if (!objectTypeFolds.TryGetValue(key, out bool open)) open = true;
                open = EditorGUILayout.Foldout(open, $"{kv.Key} ({kv.Value.Count})", true);
                objectTypeFolds[key] = open;
                if (!open) continue;

                var singles = new List<(RegistryItem, GamePrefabRegistry)>();
                var multis  = new List<(RegistryItem, GamePrefabRegistry)>();
                foreach (var pair in kv.Value)
                {
                    if (pair.item.SpawnMode == PrefabSpawnMode.Single) singles.Add(pair);
                    else multis.Add(pair);
                }

                if (singles.Count > 0) { EditorGUILayout.LabelField("  Single", EditorStyles.miniBoldLabel); DrawPrefabRow(singles); }
                if (multis.Count  > 0) { EditorGUILayout.LabelField("  Multi",  EditorStyles.miniBoldLabel); DrawPrefabRow(multis);  }
            }
        }

        void DrawPrefabRow(List<(RegistryItem item, GamePrefabRegistry src)> items)
        {
            const int cols = 3;
            for (int i = 0; i < items.Count; i += cols)
            {
                EditorGUILayout.BeginHorizontal();
                for (int c = 0; c < cols; c++)
                {
                    int idx = i + c;
                    if (idx >= items.Count) break;
                    DrawPrefabButton(items[idx].item, items[idx].src);
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
        }

        void DrawPrefabButton(RegistryItem item, GamePrefabRegistry source)
        {
            const float tileSize = 80f;
            bool isSelected = selectedMainKey == item.MainKey;

            EditorGUILayout.BeginVertical(GUILayout.Width(tileSize));
            var prevBg = GUI.backgroundColor;
            if (isSelected) GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f);

            var rect = GUILayoutUtility.GetRect(tileSize, tileSize,
                GUILayout.Width(tileSize), GUILayout.Height(tileSize));

            if (GUI.Button(rect, GUIContent.none))
            {
                if (isSelected) { selectedMainKey = int.MinValue; selectedVariantKey = 0; }
                else
                {
                    selectedMainKey = item.MainKey;
                    selectedVariantKey = 0;
                    // 프리팹 원본 스케일로 초기화
                    if (item.Prefab != null)
                        instanceScale = GetPrefabDefaultScale(item.Prefab);
                    ResetInstanceSettings(item);
                }
                DestroyPreview();
                Repaint();
            }

            Texture2D preview = null;
            if (showThumbnails && item.Prefab != null)
            {
                preview = AssetPreview.GetAssetPreview(item.Prefab);
                if (preview == null && AssetPreview.IsLoadingAssetPreview(item.Prefab.GetInstanceID()))
                    Repaint();
            }

            if (preview != null)
                GUI.DrawTexture(new Rect(rect.x+2, rect.y+2, rect.width-4, rect.height-4),
                    preview, ScaleMode.ScaleToFit);
            else
                GUI.Label(rect, $"M{item.MainKey}",
                    new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter });

            var dlcRect = new Rect(rect.x+2, rect.yMax-14, rect.width-4, 12);
            EditorGUI.DrawRect(dlcRect, new Color(0,0,0,0.6f));
            GUI.Label(dlcRect, " " + (source?.DlcName ?? "?"),
                new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.LowerLeft,
                    normal    = { textColor = Color.white },
                    fontSize  = 9,
                });

            GUI.backgroundColor = prevBg;

            string lbl = string.IsNullOrEmpty(item.Name) ? $"M{item.MainKey}" : item.Name;
            EditorGUILayout.LabelField(lbl,
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter },
                GUILayout.Width(tileSize));

            EditorGUILayout.EndVertical();
        }

        void DrawSelectionInfo()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (selectedMainKey == int.MinValue)
                EditorGUILayout.LabelField("Selected: (none)", EditorStyles.miniLabel);
            else
            {
                var selectedItem = GetSelectedItem();
                byte roadMask = GetRoadMask(selectedItem);
                EditorGUILayout.LabelField(
                    $"Main {selectedMainKey}, V{selectedVariantKey}, " +
                    (IsRoadItem(selectedItem)
                        ? $"Scale {instanceScale:0.00}, Road {roadPlacementMode}, 0x{roadMask:X}"
                        : $"Scale {instanceScale:0.00}, Rot {instanceRotationY:0}"),
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════
        //  StartPoint Mode
        // ══════════════════════════════════════════════════════════
        void DrawStartPointSection()
        {
            // 헤더 (모드 토글 포함)
            EditorGUILayout.BeginHorizontal();
            foldStartPoint = EditorGUILayout.Foldout(
                foldStartPoint, "Start Points", true, EditorStyles.foldoutHeader);

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = isStartPointMode
                ? new Color(1f, 0.7f, 0.2f) : Color.white;
            if (GUILayout.Button(isStartPointMode ? "● SP Mode ON" : "○ SP Mode",
                GUILayout.Width(110), GUILayout.Height(18)))
            {
                isStartPointMode = !isStartPointMode;
                isSettingPosition = false;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            if (!foldStartPoint) return;

            EditorGUI.indentLevel++;

            // 팀 선택
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Editing Team", GUILayout.Width(90));
            for (int t = 1; t <= 8; t++)
            {
                bool isCur = (editingTeamNumber == t);
                GUI.backgroundColor = isCur ? TeamColors[t] : new Color(0.6f,0.6f,0.6f);
                if (GUILayout.Button(t.ToString(), GUILayout.Width(26), GUILayout.Height(22)))
                {
                    editingTeamNumber = t;
                    isSettingPosition = false;
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // 현재 팀 스타트포인트 정보
            var sp = GetOrCreateStartPoint(editingTeamNumber);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Team {editingTeamNumber} Position", GUILayout.Width(120));
            var newPos = EditorGUILayout.Vector3Field("", sp.Position);
            if (newPos != sp.Position)
            {
                sp.Position = newPos;
                SetStartPoint(editingTeamNumber, sp);
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            // 씬 클릭으로 위치 지정
            GUI.backgroundColor = isSettingPosition
                ? new Color(1f, 0.7f, 0.2f) : Color.white;
            if (GUILayout.Button(isSettingPosition ? "Click Scene to Set..." : "Set Position (Click)",
                GUILayout.Height(22)))
            {
                isSettingPosition = !isSettingPosition;
                if (isSettingPosition) isStartPointMode = true;
            }
            GUI.backgroundColor = Color.white;

            // 팀 삭제
            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("Remove Team", GUILayout.Width(100), GUILayout.Height(22)))
            {
                if (EditorUtility.DisplayDialog("Remove StartPoint",
                    $"Remove Team {editingTeamNumber} and all its base placements?",
                    "Remove", "Cancel"))
                    RemoveStartPoint(editingTeamNumber);
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // 현재 팀 베이스 기지 현황
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"Base: {sp.BaseSingles?.Count ?? 0} singles, " +
                $"{sp.BaseMultis?.Count ?? 0} multis, " +
                $"{sp.BaseRoads?.Count ?? 0} roads",
                EditorStyles.miniLabel);
            if (GUILayout.Button("Clear Base", GUILayout.Width(80)))
            {
                if (EditorUtility.DisplayDialog("Clear Base",
                    $"Clear Team {editingTeamNumber} base placements?",
                    "Clear", "Cancel"))
                    ClearStartPointBase(editingTeamNumber);
            }
            EditorGUILayout.EndHorizontal();

            // 스타트포인트 목록
            if (currentMap.StartPoints != null && currentMap.StartPoints.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("All Start Points:", EditorStyles.miniBoldLabel);
                foreach (var spt in currentMap.StartPoints)
                {
                    var c = spt.Number >= 1 && spt.Number <= 8
                        ? TeamColors[spt.Number] : Color.white;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.DrawRect(
                        GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12)),
                        c);
                    EditorGUILayout.LabelField(
                        $"Team {spt.Number}:  pos {spt.Position}  " +
                        $"[{spt.BaseSingles?.Count ?? 0}S / " +
                        $"{spt.BaseMultis?.Count ?? 0}M / " +
                        $"{spt.BaseRoads?.Count ?? 0}R]",
                        EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Visibility — 카테고리별 숨김
        // ══════════════════════════════════════════════════════════
        void DrawVisibilitySection()
        {
            foldVisibility = EditorGUILayout.Foldout(
                foldVisibility, "Scene Visuals", true, EditorStyles.foldoutHeader);
            if (!foldVisibility) return;

            EditorGUI.indentLevel++;
            bool newShowPrefabVisuals = EditorGUILayout.Toggle("Show Prefab Visuals", showPrefabVisuals);
            if (newShowPrefabVisuals != showPrefabVisuals)
            {
                showPrefabVisuals = newShowPrefabVisuals;
                ApplyCategoryVisibility();
            }

            bool newShowPaintVisuals = EditorGUILayout.Toggle("Show Paint Visuals", showPaintVisuals);
            if (newShowPaintVisuals != showPaintVisuals)
            {
                showPaintVisuals = newShowPaintVisuals;
                SetTerrainOverlayDirty();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Prefab Object Types", EditorStyles.miniBoldLabel);
            foreach (var type in System.Enum.GetValues(typeof(PrefabObjectType)))
            {
                var t = (PrefabObjectType)type;
                bool current = objectTypeVisibility.TryGetValue(t, out bool v) && v;
                bool nv = EditorGUILayout.Toggle(t.ToString(), current);
                if (nv != current)
                {
                    objectTypeVisibility[t] = nv;
                    ApplyCategoryVisibility();
                }
            }
            EditorGUI.indentLevel--;
        }

        void ApplyCategoryVisibility()
        {
            foreach (var sp in scenePlacements)
            {
                if (sp.Go == null) continue;
                var item = FindItemByIndex(sp.Kind, sp.Index);
                if (item == null)
                {
                    sp.Go.SetActive(showPrefabVisuals);
                    continue;
                }

                bool visible = showPrefabVisuals
                    && objectTypeVisibility.TryGetValue(item.ObjectType, out bool v) && v;
                sp.Go.SetActive(visible);
            }
            SceneView.RepaintAll();
        }

        // ══════════════════════════════════════════════════════════
        //  Grid 표시
        // ══════════════════════════════════════════════════════════
        void DrawGridSection()
        {
            foldGrid = EditorGUILayout.Foldout(
                foldGrid, "Grid Display", true, EditorStyles.foldoutHeader);
            if (!foldGrid) return;

            EditorGUI.indentLevel++;
            bool nd = EditorGUILayout.Toggle("Draw Grid", drawGrid);
            if (nd != drawGrid) { drawGrid = nd; SceneView.RepaintAll(); }

            bool nl = EditorGUILayout.Toggle("Draw Axis Labels", drawGridLabels);
            if (nl != drawGridLabels) { drawGridLabels = nl; SceneView.RepaintAll(); }

            int newLabelStep = Mathf.Max(1, EditorGUILayout.IntField("Label Step", gridLabelStep));
            if (newLabelStep != gridLabelStep)
            {
                gridLabelStep = newLabelStep;
                SceneView.RepaintAll();
            }

            EditorGUILayout.BeginHorizontal();
            float newBoundaryGridHeight = EditorGUILayout.FloatField("Gizmo Height (Y)", boundaryGridHeight);
            if (GUILayout.Button("Reset", GUILayout.Width(50)))
                newBoundaryGridHeight = 0f;
            EditorGUILayout.EndHorizontal();

            newBoundaryGridHeight = Mathf.Max(0f, newBoundaryGridHeight);
            if (!Mathf.Approximately(newBoundaryGridHeight, boundaryGridHeight))
            {
                boundaryGridHeight = newBoundaryGridHeight;
                SceneView.RepaintAll();
            }

            Color nc = EditorGUILayout.ColorField("Line Color", gridColor);
            if (nc != gridColor) { gridColor = nc; SceneView.RepaintAll(); }

            float nw = EditorGUILayout.Slider("Line Width", gridLineWidth, 0.1f, 5f);
            if (!Mathf.Approximately(nw, gridLineWidth))
            { gridLineWidth = nw; SceneView.RepaintAll(); }

            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Debug
        // ══════════════════════════════════════════════════════════
        void DrawDebugSection()
        {
            foldDebug = EditorGUILayout.Foldout(foldDebug, "Debug", true, EditorStyles.foldoutHeader);
            if (!foldDebug) return;

            EditorGUI.indentLevel++;
            bool ns = EditorGUILayout.Toggle("Show Boundary", showBoundary);
            if (ns != showBoundary) { showBoundary = ns; SceneView.RepaintAll(); }

            EditorGUILayout.LabelField(
                $"Singles={currentMap.Singles.Count}, Multis={currentMap.Multis.Count}, " +
                $"Roads={currentMap.Roads.Count}, Occupied={occupiedCells.Count}",
                EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  SceneView
        // ══════════════════════════════════════════════════════════
        void OnSceneGUI(SceneView sceneView)
        {
            var e = Event.current;

            if (showBoundary && currentMap != null)
                MapBoundaryGizmo.Draw(currentMap.Settings, boundaryGridHeight, false);

            if (drawGrid && currentMap != null)
                DrawCellGrid(boundaryGridHeight);
            UpdateTerrainOverlay();

            DrawStartPointGizmos();
            if (activeTool == MapEditorTool.TerrainPaint)
            {
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    SetActiveTool(MapEditorTool.PrefabPlacement);
                    e.Use();
                    return;
                }

                DestroyPreview();
                bool handledPaint = HandleTerrainPaint(e);
                if (handledPaint)
                    HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
                    sceneView.Repaint();
                return;
            }

            HandleStartPointPositionClick(e);

            // 위치 지정 중이면 기본 컨트롤 막기
            if (isSettingPosition)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            HandleScaleShortcuts(e);

            // Esc = 선택 해제
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                selectedMainKey = int.MinValue; selectedVariantKey = 0;
                selectedSinglePlacements.Clear();
                selectedMultiPlacements.Clear();
                DestroyPreview();
                e.Use(); Repaint(); return;
            }

            // Delete = 선택 삭제
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Delete)
            {
                DeleteSelectedPlacements();
                e.Use(); Repaint(); return;
            }

            // Ctrl+Z = 마지막 배치 취소
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Z && e.control)
            {
                UndoLastPlacement();
                e.Use(); return;
            }

            // 배치 모드 vs 선택 모드
            if (selectedMainKey != int.MinValue)
            {
                if (e.type == EventType.ScrollWheel && (e.control || e.alt))
                    HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

                // 배치 모드에서도 우클릭으로 셀 삭제 가능
                if (e.type == EventType.MouseDown && e.button == 1 && e.control)
                {
                    var rray  = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    var rplane = new Plane(Vector3.up, Vector3.zero);
                    if (rplane.Raycast(rray, out float rt))
                    {
                        int2 rcell = WorldToCell(rray.GetPoint(rt));
                        DeletePlacementAt(rcell);
                    }
                    e.Use(); Repaint();
                }
                else
                {
                    HandleHeightAndRotation(e);

                    var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    var item = GetSelectedItem();
                    if (item != null && TryGetPlacementSnap(ray, item, out var snap))
                    {
                        UpdatePreview(snap);
                        HandlePlacement(e, snap);
                    }
                }
            }
            else
            {
                DestroyPreview();
                HandleSceneSelection(e);
            }

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
                sceneView.Repaint();
        }

        // ══════════════════════════════════════════════════════════
        //  셀 그리드 그리기
        // ══════════════════════════════════════════════════════════
        void DrawCellGrid(float height)
        {
            float cs = currentMap.Settings.CellSize;
            int   w  = currentMap.Settings.Width;
            int   h  = currentMap.Settings.Height;

            var prevColor = Handles.color;
            var prevZTest = Handles.zTest;
            Handles.color = gridColor;
            Handles.zTest = CompareFunction.LessEqual;

            // 세로선 (Z축 방향)
            for (int x = 0; x <= w; x++)
            {
                float wx = x * cs;
                var p1 = new Vector3(wx, height, 0);
                var p2 = new Vector3(wx, height, h * cs);
                Handles.DrawAAPolyLine(gridLineWidth, new[] { p1, p2 });
            }

            // 가로선 (X축 방향)
            for (int z = 0; z <= h; z++)
            {
                float wz = z * cs;
                var p1 = new Vector3(0, height, wz);
                var p2 = new Vector3(w * cs, height, wz);
                Handles.DrawAAPolyLine(gridLineWidth, new[] { p1, p2 });
            }

            if (drawGridLabels)
                DrawGridAxisLabels(cs, w, h, height);

            Handles.color = prevColor;
            Handles.zTest = prevZTest;
        }

        void DrawGridAxisLabels(float cellSize, int width, int heightCells, float y)
        {
            int step = Mathf.Max(1, gridLabelStep);
            float inset = cellSize * 0.15f;
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
            };
            style.normal.textColor = Color.white;

            var prevColor = Handles.color;
            Handles.color = Color.white;

            for (int x = 0; x <= width; x += step)
            {
                float wx = x * cellSize;
                Handles.Label(new Vector3(wx + inset, y, inset), x.ToString(), style);
            }

            for (int z = 0; z <= heightCells; z += step)
            {
                float wz = z * cellSize;
                Handles.Label(new Vector3(inset, y, wz + inset), z.ToString(), style);
            }

            Handles.color = prevColor;
        }

        // ══════════════════════════════════════════════════════════
        //  셀 스냅 (좌하단 기준)
        // ══════════════════════════════════════════════════════════
        void UpdateTerrainOverlay()
        {
            bool visible = showPaintVisuals
                && (drawTerrainCells || drawTerrainHeightBlocks)
                && currentMap?.TerrainCells != null
                && currentMap.TerrainCells.Count > 0;

            if (!visible)
            {
                if (terrainOverlayGo != null)
                    terrainOverlayGo.SetActive(false);
                return;
            }

            EnsureTerrainOverlay();
            terrainOverlayGo.SetActive(true);

            if (!terrainOverlayDirty) return;
            RebuildTerrainOverlayMesh();
        }

        void EnsureTerrainOverlay()
        {
            if (terrainOverlayGo != null) return;

            terrainOverlayGo = new GameObject("MapEditor_TerrainOverlay");
            terrainOverlayGo.hideFlags = HideFlags.HideAndDontSave;
            terrainOverlayFilter = terrainOverlayGo.AddComponent<MeshFilter>();
            terrainOverlayRenderer = terrainOverlayGo.AddComponent<MeshRenderer>();
            terrainOverlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            terrainOverlayRenderer.receiveShadows = false;

            terrainOverlayMesh = new Mesh
            {
                name = "MapEditor_TerrainOverlayMesh",
                hideFlags = HideFlags.HideAndDontSave,
            };
            terrainOverlayMesh.indexFormat = IndexFormat.UInt32;
            terrainOverlayFilter.sharedMesh = terrainOverlayMesh;

            var shader = Shader.Find("Hidden/Internal-Colored")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color");
            terrainOverlayMaterial = new Material(shader)
            {
                name = "MapEditor_TerrainOverlayMaterial",
                hideFlags = HideFlags.HideAndDontSave,
            };
            terrainOverlayMaterial.color = Color.white;
            terrainOverlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            terrainOverlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            terrainOverlayMaterial.SetInt("_Cull", (int)CullMode.Off);
            terrainOverlayMaterial.SetInt("_ZWrite", 0);
            terrainOverlayRenderer.sharedMaterial = terrainOverlayMaterial;
        }

        void DestroyTerrainOverlay()
        {
            if (terrainOverlayGo != null)
                DestroyImmediate(terrainOverlayGo);
            if (terrainOverlayMesh != null)
                DestroyImmediate(terrainOverlayMesh);
            if (terrainOverlayMaterial != null)
                DestroyImmediate(terrainOverlayMaterial);

            terrainOverlayGo = null;
            terrainOverlayFilter = null;
            terrainOverlayRenderer = null;
            terrainOverlayMesh = null;
            terrainOverlayMaterial = null;
            terrainOverlayDirty = true;
        }

        void SetTerrainOverlayDirty()
        {
            terrainOverlayDirty = true;
        }

        void RebuildTerrainOverlayMesh()
        {
            terrainOverlayDirty = false;
            if (terrainOverlayMesh == null || currentMap?.TerrainCells == null) return;

            float cs = currentMap.Settings.CellSize;
            var vertices = new List<Vector3>(currentMap.TerrainCells.Count * 8);
            var colors = new List<Color>(currentMap.TerrainCells.Count * 8);
            var triangles = new List<int>(currentMap.TerrainCells.Count * 12);

            foreach (var cell in currentMap.TerrainCells)
            {
                if (!IsCellInMap(cell.Cell)) continue;

                Color terrainColor = GetTerrainColor(cell.Terrain);
                float y = TerrainHeightToWorldY(cell.Height);

                if (drawTerrainCells)
                {
                    AddOverlayQuad(vertices, colors, triangles, cell.Cell, cs,
                        y + 0.025f,
                        new Color(terrainColor.r, terrainColor.g, terrainColor.b, 0.26f));
                }

                if (drawTerrainHeightBlocks && cell.Height > 0)
                {
                    AddOverlaySides(vertices, colors, triangles, cell.Cell, cs,
                        0f, y + 0.025f,
                        new Color(terrainColor.r, terrainColor.g, terrainColor.b, 0.12f));
                }
            }

            terrainOverlayMesh.Clear();
            terrainOverlayMesh.SetVertices(vertices);
            terrainOverlayMesh.SetColors(colors);
            terrainOverlayMesh.SetTriangles(triangles, 0);
            terrainOverlayMesh.RecalculateBounds();
        }

        void AddOverlayQuad(List<Vector3> vertices, List<Color> colors,
            List<int> triangles, int2 cell, float cs, float y, Color color)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(cell.x * cs, y, cell.y * cs));
            vertices.Add(new Vector3((cell.x + 1) * cs, y, cell.y * cs));
            vertices.Add(new Vector3((cell.x + 1) * cs, y, (cell.y + 1) * cs));
            vertices.Add(new Vector3(cell.x * cs, y, (cell.y + 1) * cs));
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }

        void AddOverlaySides(List<Vector3> vertices, List<Color> colors,
            List<int> triangles, int2 cell, float cs, float bottomY, float topY, Color color)
        {
            AddOverlaySide(vertices, colors, triangles,
                new Vector3(cell.x * cs, bottomY, cell.y * cs),
                new Vector3((cell.x + 1) * cs, bottomY, cell.y * cs),
                new Vector3((cell.x + 1) * cs, topY, cell.y * cs),
                new Vector3(cell.x * cs, topY, cell.y * cs),
                color);
            AddOverlaySide(vertices, colors, triangles,
                new Vector3((cell.x + 1) * cs, bottomY, cell.y * cs),
                new Vector3((cell.x + 1) * cs, bottomY, (cell.y + 1) * cs),
                new Vector3((cell.x + 1) * cs, topY, (cell.y + 1) * cs),
                new Vector3((cell.x + 1) * cs, topY, cell.y * cs),
                color);
            AddOverlaySide(vertices, colors, triangles,
                new Vector3((cell.x + 1) * cs, bottomY, (cell.y + 1) * cs),
                new Vector3(cell.x * cs, bottomY, (cell.y + 1) * cs),
                new Vector3(cell.x * cs, topY, (cell.y + 1) * cs),
                new Vector3((cell.x + 1) * cs, topY, (cell.y + 1) * cs),
                color);
            AddOverlaySide(vertices, colors, triangles,
                new Vector3(cell.x * cs, bottomY, (cell.y + 1) * cs),
                new Vector3(cell.x * cs, bottomY, cell.y * cs),
                new Vector3(cell.x * cs, topY, cell.y * cs),
                new Vector3(cell.x * cs, topY, (cell.y + 1) * cs),
                color);
        }

        void AddOverlaySide(List<Vector3> vertices, List<Color> colors,
            List<int> triangles, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color)
        {
            int start = vertices.Count;
            vertices.Add(p0);
            vertices.Add(p1);
            vertices.Add(p2);
            vertices.Add(p3);
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }

        bool HandleTerrainPaint(Event e)
        {
            bool paint = (e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
                && e.button == 0 && !e.alt;
            bool erase = (e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
                && e.button == 1 && e.control && !e.alt;
            if (!paint && !erase) return false;

            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (!TryPickTerrainSurfaceCell(ray, out int2 cell, out _)) return false;

            if (paint)
            {
                PaintTerrainLayer(cell);
            }
            else
            {
                ResetTerrainLayer(cell);
            }

            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return true;
        }

        bool TryPickTerrainSurfaceCell(Ray ray, out int2 cell, out int height)
        {
            CollectPlacementHeightCandidates();

            bool found = false;
            float bestDistance = float.MaxValue;
            cell = default;
            height = 0;

            for (int i = 0; i < placementHeightCandidates.Count; i++)
            {
                int candidateHeight = placementHeightCandidates[i];
                if (!TryGetCellOnHeightPlane(ray, candidateHeight, out var candidateCell, out var hitPos))
                    continue;
                if (!IsCellInMap(candidateCell))
                    continue;

                var data = GetTerrainCellData(candidateCell);
                if (data.TerrainLayer.Height != candidateHeight)
                    continue;

                float distance = Vector3.SqrMagnitude(hitPos - ray.origin);
                if (found && distance >= bestDistance)
                    continue;

                found = true;
                bestDistance = distance;
                cell = candidateCell;
                height = candidateHeight;
            }

            return found;
        }

        void PaintTerrainLayer(int2 cell)
        {
            GetTerrainBrushRect(cell, out var minCell, out var maxCell);
            FillTerrainRect(minCell, maxCell, terrainPaintHeight, terrainPaintType);
        }

        void ResetTerrainLayer(int2 cell)
        {
            GetTerrainBrushRect(cell, out var minCell, out var maxCell);
            RemoveTerrainRect(minCell, maxCell);
        }

        void GetTerrainBrushRect(int2 pickedCell, out int2 minCell, out int2 maxCell)
        {
            var size = new int2(
                Mathf.Max(1, terrainBrushSize.x),
                Mathf.Max(1, terrainBrushSize.y));

            if (terrainSnapToBrushSize)
            {
                minCell = new int2(
                    Mathf.FloorToInt(pickedCell.x / (float)size.x) * size.x,
                    Mathf.FloorToInt(pickedCell.y / (float)size.y) * size.y);
            }
            else
            {
                minCell = pickedCell;
            }

            maxCell = new int2(minCell.x + size.x - 1, minCell.y + size.y - 1);
        }

        void EnsureTerrainCells()
        {
            if (currentMap.TerrainCells == null)
                currentMap.TerrainCells = new List<TerrainCellData>();
        }

        void SetTerrainCell(int2 cell, TerrainCellData data)
        {
            EnsureTerrainCells();

            for (int i = 0; i < currentMap.TerrainCells.Count; i++)
            {
                if (!currentMap.TerrainCells[i].Cell.Equals(cell)) continue;
                currentMap.TerrainCells[i] = data;
                SetTerrainOverlayDirty();
                return;
            }

            currentMap.TerrainCells.Add(data);
            SetTerrainOverlayDirty();
        }

        void RemoveTerrainCell(int2 cell)
        {
            EnsureTerrainCells();
            bool removed = false;
            for (int i = currentMap.TerrainCells.Count - 1; i >= 0; i--)
            {
                if (currentMap.TerrainCells[i].Cell.Equals(cell))
                {
                    currentMap.TerrainCells.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed)
                SetTerrainOverlayDirty();
        }

        void FillTerrainRect(int2 minCell, int2 maxCell, int height, MapTerrainType terrain)
        {
            EnsureTerrainCells();

            if (!TryClampRectToMap(ref minCell, ref maxCell))
                return;

            var data = TerrainCellData.Create(
                int2.zero,
                (byte)Mathf.Clamp(height, 0, 255),
                terrain);

            RemoveTerrainOverridesInRect(minCell, maxCell);

            if (!IsDefaultTerrain(data))
            {
                int capacity = (maxCell.x - minCell.x + 1) * (maxCell.y - minCell.y + 1);
                if (currentMap.TerrainCells.Capacity < currentMap.TerrainCells.Count + capacity)
                    currentMap.TerrainCells.Capacity = currentMap.TerrainCells.Count + capacity;

                for (int y = minCell.y; y <= maxCell.y; y++)
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    data.Cell = new int2(x, y);
                    currentMap.TerrainCells.Add(data);
                }
            }

            SetTerrainOverlayDirty();
            Repaint();
            SceneView.RepaintAll();
        }

        void RemoveTerrainRect(int2 minCell, int2 maxCell)
        {
            EnsureTerrainCells();

            if (!TryClampRectToMap(ref minCell, ref maxCell))
                return;

            if (RemoveTerrainOverridesInRect(minCell, maxCell))
            {
                SetTerrainOverlayDirty();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        bool RemoveTerrainOverridesInRect(int2 minCell, int2 maxCell)
        {
            bool removed = false;
            for (int i = currentMap.TerrainCells.Count - 1; i >= 0; i--)
            {
                var cell = currentMap.TerrainCells[i].Cell;
                if (cell.x < minCell.x || cell.x > maxCell.x
                    || cell.y < minCell.y || cell.y > maxCell.y)
                    continue;

                currentMap.TerrainCells.RemoveAt(i);
                removed = true;
            }

            return removed;
        }

        bool TryClampRectToMap(ref int2 minCell, ref int2 maxCell)
        {
            if (currentMap == null)
                return false;

            var mapMax = new int2(currentMap.Settings.Width - 1, currentMap.Settings.Height - 1);
            if (mapMax.x < 0 || mapMax.y < 0)
                return false;

            minCell = math.clamp(minCell, int2.zero, mapMax);
            maxCell = math.clamp(maxCell, int2.zero, mapMax);
            return minCell.x <= maxCell.x && minCell.y <= maxCell.y;
        }

        bool IsDefaultTerrain(TerrainCellData data)
            => data.Height == 0
            && data.Terrain == MapTerrainType.Land;

        bool IsCellInMap(int2 cell)
            => cell.x >= 0
            && cell.y >= 0
            && cell.x < currentMap.Settings.Width
            && cell.y < currentMap.Settings.Height;

        float TerrainHeightToWorldY(int height)
            => height * currentMap.Settings.CellSize * 0.5f;

        Color GetTerrainColor(MapTerrainType terrain)
        {
            return terrain switch
            {
                MapTerrainType.Water => new Color(0.1f, 0.45f, 0.95f),
                MapTerrainType.Mountain => new Color(0.48f, 0.45f, 0.42f),
                _ => Color.white,
            };
        }

        Vector3 SnapToCell(Vector3 worldPos)
        {
            float cs = currentMap.Settings.CellSize;
            float x  = Mathf.Floor(worldPos.x / cs) * cs;
            float z  = Mathf.Floor(worldPos.z / cs) * cs;
            return new Vector3(x, previewHeight, z);
        }

        int2 WorldToCell(Vector3 pos)
        {
            float cs = currentMap.Settings.CellSize;
            return new int2(
                Mathf.FloorToInt(pos.x / cs),
                Mathf.FloorToInt(pos.z / cs));
        }

        int2 FootprintOriginFromPosition(Vector3 position, Vector2Int size)
        {
            float cs = currentMap.Settings.CellSize;
            int sx = Mathf.Max(1, size.x);
            int sz = Mathf.Max(1, size.y);
            return new int2(
                Mathf.FloorToInt(position.x / cs - sx * 0.5f),
                Mathf.FloorToInt(position.z / cs - sz * 0.5f));
        }

        Vector2Int GetFootprintSize(RegistryItem item)
        {
            if (item == null || IsRoadItem(item))
                return Vector2Int.one;

            return new Vector2Int(
                Mathf.Max(1, item.Size.x),
                Mathf.Max(1, item.Size.y));
        }

        bool TryGetCellOnHeightPlane(Ray ray, int height, out int2 cell, out Vector3 hitPos)
        {
            var plane = new Plane(Vector3.up, Vector3.up * GetHeightFromIndex(height));
            if (plane.Raycast(ray, out float t))
            {
                hitPos = ray.GetPoint(t);
                cell = WorldToCell(hitPos);
                return true;
            }

            hitPos = default;
            cell = default;
            return false;
        }

        void CollectPlacementHeightCandidates()
        {
            placementHeightCandidates.Clear();
            placementHeightCandidates.Add(0);

            if (currentMap?.TerrainCells == null) return;

            for (int i = 0; i < currentMap.TerrainCells.Count; i++)
            {
                int height = currentMap.TerrainCells[i].Height;
                if (!placementHeightCandidates.Contains(height))
                    placementHeightCandidates.Add(height);
            }
        }

        int GetHeightIndex(float worldHeight)
        {
            float cs = currentMap.Settings.CellSize;
            return Mathf.RoundToInt(worldHeight / (cs * 0.5f));
        }

        int GetPlacementHeightIndex(float worldY, RegistryItem item)
        {
            float yOffset = item != null ? item.Offset.y : 0f;
            return GetHeightIndex(worldY - yOffset);
        }

        float GetHeightFromIndex(int height)
        {
            return height * currentMap.Settings.CellSize * 0.5f;
        }

        int GetPlacementHeight(int surfaceHeight)
        {
            int offsetHeight = Mathf.Max(0, GetHeightIndex(previewHeight));
            return surfaceHeight + offsetHeight;
        }

        bool TryGetPlacementSnap(Ray ray, RegistryItem item, out PlacementSnap snap)
        {
            CollectPlacementHeightCandidates();

            float cs = currentMap.Settings.CellSize;
            bool found = false;
            float bestDistance = float.MaxValue;
            int2 bestCell = default;
            int bestSurfaceHeight = 0;
            int bestPlacementHeight = 0;
            Vector3 bestHitPos = default;

            for (int i = 0; i < placementHeightCandidates.Count; i++)
            {
                int candidateHeight = placementHeightCandidates[i];
                if (!TryGetCellOnHeightPlane(ray, candidateHeight, out var cell, out var hitPos))
                    continue;

                if (!TryResolvePlacementSurface(cell, item, out int surfaceHeight)
                    || surfaceHeight != candidateHeight)
                    continue;

                int placementHeight = GetPlacementHeight(surfaceHeight);
                if (!CanPlaceItemAt(cell, item, surfaceHeight, placementHeight))
                    continue;

                float distance = Vector3.SqrMagnitude(hitPos - ray.origin);
                if (found && distance >= bestDistance)
                    continue;

                found = true;
                bestDistance = distance;
                bestCell = cell;
                bestSurfaceHeight = surfaceHeight;
                bestPlacementHeight = placementHeight;
                bestHitPos = hitPos;
            }

            if (!found)
            {
                snap = default;
                return false;
            }

            snap = new PlacementSnap
            {
                Cell          = bestCell,
                SurfaceHeight = bestSurfaceHeight,
                Height        = bestPlacementHeight,
                Origin = new Vector3(
                    Mathf.Floor(bestHitPos.x / cs) * cs,
                    GetHeightFromIndex(bestPlacementHeight),
                    Mathf.Floor(bestHitPos.z / cs) * cs),
            };
            return true;
        }

        // ══════════════════════════════════════════════════════════
        //  프리뷰
        // ══════════════════════════════════════════════════════════
        void UpdatePreview(PlacementSnap snap)
        {
            var item = GetSelectedItem();
            if (item == null || item.Prefab == null) { DestroyPreview(); return; }

            var prefab = item.Prefab;

            if (previewInstance == null
                || previewInstance.name != prefab.name + "_Preview")
            {
                DestroyPreview();
                previewInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                previewInstance.name      = prefab.name + "_Preview";
                previewInstance.hideFlags = HideFlags.HideAndDontSave;
                foreach (var col in previewInstance.GetComponentsInChildren<Collider>())
                    col.enabled = false;
            }

            var pos = GetSinglePosition(snap.Cell, snap.Height, item);

            previewInstance.transform.position   = pos;
            if (!IsRoadItem(item))
                previewInstance.transform.rotation = Quaternion.Euler(0, instanceRotationY, 0);
            previewInstance.transform.localScale = Vector3.one * instanceScale;
        }

        void DestroyPreview()
        {
            if (previewInstance != null)
            {
                DestroyImmediate(previewInstance);
                previewInstance = null;
            }
        }

        // ══════════════════════════════════════════════════════════
        //  높이 + 회전 (Ctrl/Alt + 휠)
        // ══════════════════════════════════════════════════════════
        void HandleHeightAndRotation(Event e)
        {
            if (e.type != EventType.ScrollWheel) return;
            float cs = currentMap.Settings.CellSize;

            if (e.control && !e.alt)
            {
                float step = cs * 0.5f;
                previewHeight = Mathf.Max(0f,
                    previewHeight + (e.delta.y > 0 ? -step : step));
                e.Use(); Repaint(); SceneView.RepaintAll();
            }
            else if (e.alt && !e.control && !IsRoadItem(GetSelectedItem()))
            {
                instanceRotationY = (instanceRotationY + (e.delta.y > 0 ? -90f : 90f)) % 360f;
                if (instanceRotationY < 0) instanceRotationY += 360f;
                e.Use(); Repaint();
            }
        }

        // ══════════════════════════════════════════════════════════
        //  배치
        // ══════════════════════════════════════════════════════════
        void HandlePlacement(Event e, PlacementSnap snap)
        {
            bool isClick = e.type == EventType.MouseDown && e.button == 0 && !e.alt;
            bool isDrag  = e.type == EventType.MouseDrag && e.button == 0 && !e.alt
                && (e.shift || (IsRoadItem(GetSelectedItem()) && roadPlacementMode == RoadPlacementMode.Continuous));
            bool isMouseUp = e.type == EventType.MouseUp && e.button == 0;

            if (!isClick && !isDrag && !isMouseUp) return;

            var item = GetSelectedItem();
            if (item == null) return;

            int2 cellOrigin = snap.Cell;

            // Shift 드래그: 셀 영역 다를 때만 배치
            if (isDrag && cellOrigin.Equals(lastPlacedCellOrigin)) return;

            // 도로/일반 분기
            bool isRoad = IsRoadItem(item);

            if (isRoad)
            {
                if (roadPlacementMode == RoadPlacementMode.Continuous)
                {
                    HandleContinuousRoadPlacement(e, item, cellOrigin, snap.SurfaceHeight, snap.Height);
                }
                else if (isClick)
                {
                    hasRoadContinuousStart = false;
                    if (TryPlaceRoad(item, cellOrigin, snap.SurfaceHeight, snap.Height))
                        lastPlacedCellOrigin = cellOrigin;
                }
            }
            else if (!isMouseUp && item.SpawnMode == PrefabSpawnMode.Single)
            {
                if (TryPlaceSingle(item, cellOrigin, snap.SurfaceHeight, snap.Height))
                    lastPlacedCellOrigin = cellOrigin;
            }
            else if (!isMouseUp)
            {
                if (TryPlaceMulti(item, cellOrigin, snap.SurfaceHeight, snap.Height))
                    lastPlacedCellOrigin = cellOrigin;
            }

            e.Use();
        }

        // ── Single 배치 ────────────────────────────────────────────
        bool TryPlaceSingle(RegistryItem item, int2 cellOrigin, int surfaceHeight, int height)
        {
            if (!CanPlaceItemAt(cellOrigin, item, surfaceHeight, height)) return false;

            var placement = new SinglePlacement
            {
                MainKey    = item.MainKey,
                VariantKey = selectedVariantKey,
                Cell       = cellOrigin,
                Position   = GetSinglePosition(cellOrigin, height, item),
                Height     = height,
                RotationY  = instanceRotationY,
                Scale      = instanceScale,
            };

            // 스타트포인트 모드: BaseSingles에 저장
            if (isStartPointMode)
            {
                var sp = GetOrCreateStartPoint(editingTeamNumber);
                if (sp.BaseSingles == null) sp.BaseSingles = new();
                sp.BaseSingles.Add(placement);
                SetStartPoint(editingTeamNumber, sp);
            }
            else
            {
                currentMap.Singles.Add(placement);
            }

            int idx = isStartPointMode
                ? GetStartPoint(editingTeamNumber).BaseSingles.Count - 1
                : currentMap.Singles.Count - 1;

            // 점유 등록
            if (BlocksOccupancy(item))
                MarkArea(cellOrigin, GetFootprintSize(item), height, PlacementKind.Single, idx);

            // RequiredDlcs 자동 수집
            if (!currentMap.RequiredDlcs.Contains(item.DlcKey))
                currentMap.RequiredDlcs.Add(item.DlcKey);

            // 씬 인스턴싱
            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
            go.name      = $"{item.Name}_{idx}";
            go.hideFlags = HideFlags.DontSave;
            go.transform.position   = placement.Position;
            go.transform.rotation   = Quaternion.Euler(0, placement.RotationY, 0);
            go.transform.localScale = Vector3.one * placement.Scale;
            // 콜라이더 비활성 (선택 충돌 회피)
            foreach (var col in go.GetComponentsInChildren<Collider>())
                col.enabled = false;

            scenePlacements.Add(new ScenePlacement
            {
                Go    = go,
                Index = idx,
                Kind  = PlacementKind.Single,
            });

            Repaint();
            return true;
        }

        // ── Multi 배치 ─────────────────────────────────────────────
        bool TryPlaceMulti(RegistryItem item, int2 cellOrigin, int surfaceHeight, int height)
        {
            int seed = System.DateTime.Now.GetHashCode() ^ cellOrigin.GetHashCode();

            if (!CanPlaceItemAt(cellOrigin, item, surfaceHeight, height)) return false;

            var position = GetMultiPosition(cellOrigin, height, item);

            var placement = new MultiPlacement
            {
                MainKey    = item.MainKey,
                VariantKey = selectedVariantKey,
                Cell       = cellOrigin,
                Position   = position,
                Height     = height,
                Seed       = seed,
                Scale      = instanceScale,
            };

            if (isStartPointMode)
            {
                var sp = GetOrCreateStartPoint(editingTeamNumber);
                if (sp.BaseMultis == null) sp.BaseMultis = new();
                sp.BaseMultis.Add(placement);
                SetStartPoint(editingTeamNumber, sp);
            }
            else
            {
                currentMap.Multis.Add(placement);
            }

            int idx = isStartPointMode
                ? GetStartPoint(editingTeamNumber).BaseMultis.Count - 1
                : currentMap.Multis.Count - 1;

            // 점유 등록 (1셀)
            if (BlocksOccupancy(item))
                MarkArea(cellOrigin, GetFootprintSize(item), height, PlacementKind.Multi, idx);

            if (!currentMap.RequiredDlcs.Contains(item.DlcKey))
                currentMap.RequiredDlcs.Add(item.DlcKey);

            // 시각화: 1개만 (셀 중심에 대표)
            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
            go.name      = $"{item.Name}_Multi_{idx}";
            go.hideFlags = HideFlags.DontSave;
            go.transform.position = placement.Position;
            go.transform.rotation   = Quaternion.Euler(0, instanceRotationY, 0);
            go.transform.localScale = Vector3.one * placement.Scale;
            foreach (var col in go.GetComponentsInChildren<Collider>())
                col.enabled = false;

            scenePlacements.Add(new ScenePlacement
            {
                Go    = go,
                Index = idx,
                Kind  = PlacementKind.Multi,
            });

            Repaint();
            return true;
        }

        // ══════════════════════════════════════════════════════════
        //  Road 배치
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 도로 배치 핵심 진입점.
        ///
        /// 단일 클릭:
        ///   - 빈 셀: 사용자 의도 그대로 (Shape + Rotation)
        ///   - 중첩: 사용자가 켠 비트 방향만 인접 검사 → 연결 OR 폐쇄
        ///   - 인접 도로 갱신 ❌
        ///
        /// Shift 드래그 (isDrag=true):
        ///   - 완전 자동 (인접 4방향 검사)
        ///   - 인접 도로 자동 갱신
        /// </summary>
        void HandleContinuousRoadPlacement(Event e, RegistryItem item, int2 cell, int surfaceHeight, int height)
        {
            if (e.type == EventType.MouseUp)
            {
                hasRoadContinuousStart = false;
                return;
            }

            if (e.type == EventType.MouseDown)
            {
                hasRoadContinuousStart = true;
                roadContinuousLastCell = cell;
                roadContinuousSurfaceHeight = surfaceHeight;
                roadContinuousHeight = height;
                lastPlacedCellOrigin = cell;
                return;
            }

            if (e.type != EventType.MouseDrag) return;

            if (!hasRoadContinuousStart
                || surfaceHeight != roadContinuousSurfaceHeight
                || height != roadContinuousHeight)
            {
                hasRoadContinuousStart = true;
                roadContinuousLastCell = cell;
                roadContinuousSurfaceHeight = surfaceHeight;
                roadContinuousHeight = height;
                lastPlacedCellOrigin = cell;
                return;
            }

            if (cell.Equals(roadContinuousLastCell)) return;

            if (TryPlaceRoadPath(item, roadContinuousLastCell, cell, out var lastConnectedCell))
            {
                roadContinuousLastCell = lastConnectedCell;
                lastPlacedCellOrigin = lastConnectedCell;
            }
        }

        bool TryPlaceRoadPath(RegistryItem item, int2 fromCell, int2 toCell, out int2 lastConnectedCell)
        {
            lastConnectedCell = fromCell;
            if (fromCell.Equals(toCell))
                return false;

            bool placedAny = false;
            var current = fromCell;
            int guard = currentMap.Settings.Width + currentMap.Settings.Height + 8;

            while (!current.Equals(toCell) && guard-- > 0)
            {
                var next = StepTowardCell(current, toCell);
                if (!TryResolveRoadSurface(item, current, next, out int surfaceHeight, out int height))
                    break;

                if (!TryPlaceRoadSegment(item, current, next, surfaceHeight, height))
                    break;

                current = next;
                lastConnectedCell = current;
                placedAny = true;
            }

            return placedAny;
        }

        int2 StepTowardCell(int2 current, int2 target)
        {
            int dx = target.x - current.x;
            int dy = target.y - current.y;

            if (math.abs(dx) >= math.abs(dy) && dx != 0)
                return new int2(current.x + (dx > 0 ? 1 : -1), current.y);

            if (dy != 0)
                return new int2(current.x, current.y + (dy > 0 ? 1 : -1));

            return current;
        }

        bool TryResolveRoadSurface(
            RegistryItem item,
            int2 fromCell,
            int2 toCell,
            out int surfaceHeight,
            out int height)
        {
            surfaceHeight = 0;
            height = 0;

            if (!TryResolvePlacementSurface(fromCell, item, out int fromSurface)
                || !TryResolvePlacementSurface(toCell, item, out int toSurface)
                || fromSurface != toSurface)
                return false;

            surfaceHeight = fromSurface;
            height = GetPlacementHeight(surfaceHeight);
            return true;
        }

        bool TryPlaceRoadSegment(RegistryItem item, int2 fromCell, int2 toCell, int surfaceHeight, int height)
        {
            if (!TryGetDirectionIndex(fromCell, toCell, out int dirFromTo))
            {
                hasRoadContinuousStart = true;
                roadContinuousLastCell = toCell;
                return false;
            }

            int dirToFrom = RoadShapeMapping.OppositeDir(dirFromTo);
            byte fromBit = RoadShapeMapping.SetBit(0, dirFromTo);
            byte toBit = RoadShapeMapping.SetBit(0, dirToFrom);

            if (!CanPlaceItemAt(fromCell, item, surfaceHeight, height)
                || !CanPlaceItemAt(toCell, item, surfaceHeight, height))
                return false;

            byte fromMask = GetRoadMaskAfterOr(fromCell, height, fromBit);
            byte toMask = GetRoadMaskAfterOr(toCell, height, toBit);
            if (!CanResolveRoadItem(item, fromCell, height, fromMask)
                || !CanResolveRoadItem(item, toCell, height, toMask))
                return false;

            bool fromOk = UpsertRoadAt(item, fromCell, height, fromBit, true, out _);
            bool toOk = UpsertRoadAt(item, toCell, height, toBit, true, out _);

            if (fromOk || toOk)
            {
                Repaint();
                return true;
            }

            return false;
        }

        bool TryPlaceRoad(RegistryItem item, int2 cell, int surfaceHeight, int height)
        {
            // 빈 셀 / 중첩 구분
            if (!CanPlaceItemAt(cell, item, surfaceHeight, height)) return false;

            bool isOverlap = HasRoadAt(cell, height);

            // Multi/Single이 점유한 셀에는 도로 못 깜
            if (!isOverlap && occupiedCells.TryGetValue(new OccupancyKey(cell, height), out var occ))
            {
                if (occ.Kind != PlacementKind.Road) return false;
            }

            byte selectedMask = GetRoadMask(item);
            if (!RoadShapeMapping.IsValidMask(selectedMask)) return false;

            byte newMask = selectedMask;

            /* Single road placement keeps the selected mask as-is.
                // ── Shift 드래그: 완전 자동 ──────────────────────────
                newMask = ComputeAutoBitmask(cell, height);
                if (newMask == 0)
                {
                    // 인접 도로 0개 — 사용자 선택 그대로 (DeadEnd 등)
                    newMask = selectedMask;
                }
            */
            /* else if (!isOverlap)
            {
                // ── 빈 셀 단일 클릭: 사용자 의도 그대로 ────────────────
                newMask = selectedMask;
            }
            else
            {
                // ── 중첩 단일 클릭: 사용자 비트 중 연결 가능한 것만 유지 ──
                newMask = FilterMaskByConnectivity(cell, height, selectedMask);
            } */

            // 마스크 → Shape, Rotation
            if (!RoadShapeMapping.IsValidMask(newMask))
            {
                // 마스크 0 (모든 비트 폐쇄) — 도로 제거 시나리오
                if (isOverlap) RemoveRoadAt(cell, height);
                return false;
            }

            // 결과 Shape에 해당하는 RegistryItem 찾기
            var roadItem = FindRoadItemByDirections(newMask, item.VariantKey);
            if (roadItem == null)
            {
                // 같은 Variant에 없으면 V0으로 폴백
                roadItem = FindRoadItemByDirections(newMask, 0);
                if (roadItem == null) return false;
            }

            // 중첩이면 기존 도로 제거
            if (isOverlap) RemoveRoadAt(cell, height);

            // 새 도로 등록
            int newIdx = AddRoadPlacement(roadItem, cell, height, newMask);
            // 스타트포인트 모드일 때 BaseRoads에도 기록
            if (isStartPointMode && newIdx >= 0)
            {
                var sp = GetOrCreateStartPoint(editingTeamNumber);
                if (sp.BaseRoads == null) sp.BaseRoads = new();
                var rp = currentMap.Roads[newIdx];
                sp.BaseRoads.Add(rp);
                SetStartPoint(editingTeamNumber, sp);
            }

            // Shift 드래그: 인접 도로도 갱신
            Repaint();
            return true;
        }

        /// <summary>
        /// 어떤 셀에 새 도로를 놓을 때, 인접 4방향에 도로가 있고 그 도로가
        /// 우리 쪽 비트를 가졌으면 우리도 그 방향 비트 ON.
        /// </summary>
        byte ComputeAutoBitmask(int2 cell, int height)
        {
            byte mask = 0;
            for (int i = 0; i < 4; i++)
            {
                var off = RoadShapeMapping.DirOffset(i);
                var nCell = new int2(cell.x + off.x, cell.y + off.y);

                if (!TryGetRoadAt(nCell, height, out var rp)) continue;

                // 이웃이 우리 쪽 비트 켜져있으면 우리도 그 방향 ON
                int oppDir = RoadShapeMapping.OppositeDir(i);
                if (RoadShapeMapping.HasBit(rp.Directions, oppDir))
                    mask = RoadShapeMapping.SetBit(mask, i);
            }
            return mask;
        }

        /// <summary>
        /// 사용자 비트마스크에서 연결 가능한 비트만 살리고 나머지는 폐쇄.
        /// 중첩 단일 클릭 시 사용.
        /// </summary>
        byte FilterMaskByConnectivity(int2 cell, int height, byte userMask)
        {
            byte filtered = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!RoadShapeMapping.HasBit(userMask, i)) continue; // 사용자가 안 켠 비트 무시

                var off = RoadShapeMapping.DirOffset(i);
                var nCell = new int2(cell.x + off.x, cell.y + off.y);

                if (!TryGetRoadAt(nCell, height, out var rp)) continue;

                int oppDir = RoadShapeMapping.OppositeDir(i);
                if (RoadShapeMapping.HasBit(rp.Directions, oppDir))
                    filtered = RoadShapeMapping.SetBit(filtered, i);
                // 연결 안 되는 비트는 OFF (폐쇄)
            }

            // 단, 사용자가 켠 비트인데 인접 도로 자체가 없으면 그대로 살림
            // (도로의 다른 끝 — 외곽으로 뻗는 방향)
            for (int i = 0; i < 4; i++)
            {
                if (!RoadShapeMapping.HasBit(userMask, i)) continue;
                var off = RoadShapeMapping.DirOffset(i);
                var nCell = new int2(cell.x + off.x, cell.y + off.y);
                if (!HasRoadAt(nCell, height))
                    filtered = RoadShapeMapping.SetBit(filtered, i);
            }

            return filtered;
        }

        /// <summary>
        /// Shift 드래그 시 새 도로 배치 후 인접 도로 4개의 우리 쪽 비트를 ON
        /// 하고 모양/회전을 자동 갱신.
        /// </summary>
        void UpdateAdjacentRoads(int2 cell, int height)
        {
            for (int i = 0; i < 4; i++)
            {
                var off = RoadShapeMapping.DirOffset(i);
                var nCell = new int2(cell.x + off.x, cell.y + off.y);

                int adjIdx = FindRoadIndex(nCell, height);
                if (adjIdx < 0) continue;

                var p = currentMap.Roads[adjIdx];
                int oppDir = RoadShapeMapping.OppositeDir(i);
                byte newMask = RoadShapeMapping.SetBit(p.Directions, oppDir);

                if (newMask == p.Directions) continue; // 변화 없음

                if (!RoadShapeMapping.IsValidMask(newMask)) continue;

                var newItem = FindRoadItemByDirections(newMask, p.VariantKey)
                           ?? FindRoadItemByDirections(newMask, 0);
                if (newItem == null) continue;

                // 기존 인스턴스 제거 → 새 모양으로 재생성
                RemoveRoadInstance(adjIdx);

                p.MainKey    = newItem.MainKey;
                p.VariantKey = newItem.VariantKey;
                p.Directions = newMask;
                currentMap.Roads[adjIdx] = p;

                InstantiateRoadAt(adjIdx);
            }
        }

        // ── 도로 등록/제거 ─────────────────────────────────────────

        bool CanRoadOccupy(int2 cell, int height)
        {
            if (!IsFootprintInMap(cell, new Vector2Int(1, 1))) return false;

            if (!occupiedCells.TryGetValue(new OccupancyKey(cell, height), out var occ))
                return true;

            return occ.Kind == PlacementKind.Road;
        }

        byte GetRoadMaskAfterOr(int2 cell, int height, byte mask)
        {
            return TryGetRoadAt(cell, height, out var p)
                ? (byte)(p.Directions | mask)
                : mask;
        }

        bool CanResolveRoadItem(RegistryItem sourceItem, int2 cell, int height, byte mask)
        {
            int variantKey = TryGetRoadAt(cell, height, out var p)
                ? p.VariantKey
                : sourceItem.VariantKey;

            return RoadShapeMapping.IsValidMask(mask)
                && (FindRoadItemByDirections(mask, variantKey) != null
                    || FindRoadItemByDirections(mask, sourceItem.VariantKey) != null
                    || FindRoadItemByDirections(mask, 0) != null);
        }

        bool TryGetDirectionIndex(int2 fromCell, int2 toCell, out int dirIndex)
        {
            int2 delta = new int2(toCell.x - fromCell.x, toCell.y - fromCell.y);
            for (int i = 0; i < 4; i++)
            {
                if (RoadShapeMapping.DirOffset(i).Equals(delta))
                {
                    dirIndex = i;
                    return true;
                }
            }

            dirIndex = -1;
            return false;
        }

        bool UpsertRoadAt(
            RegistryItem sourceItem,
            int2 cell,
            int height,
            byte mask,
            bool preserveExistingBits,
            out int index)
        {
            index = FindRoadIndex(cell, height);
            if (index >= 0)
            {
                var p = currentMap.Roads[index];
                byte newMask = preserveExistingBits
                    ? (byte)(p.Directions | mask)
                    : mask;

                if (!RoadShapeMapping.IsValidMask(newMask)) return false;

                var newItem = FindRoadItemByDirections(newMask, p.VariantKey)
                           ?? FindRoadItemByDirections(newMask, sourceItem.VariantKey)
                           ?? FindRoadItemByDirections(newMask, 0);
                if (newItem == null) return false;

                if (p.Directions == newMask
                    && p.MainKey == newItem.MainKey
                    && p.VariantKey == newItem.VariantKey)
                    return true;

                RemoveRoadInstance(index);
                p.MainKey = newItem.MainKey;
                p.VariantKey = newItem.VariantKey;
                p.Directions = newMask;
                p.Position = GetRoadPosition(cell, height, newItem);
                p.Scale = ResolveSavedScale(p.Scale, newItem.Prefab);
                currentMap.Roads[index] = p;
                InstantiateRoadAt(index);
                return true;
            }

            if (!CanRoadOccupy(cell, height)) return false;
            if (!RoadShapeMapping.IsValidMask(mask)) return false;

            var roadItem = FindRoadItemByDirections(mask, sourceItem.VariantKey)
                        ?? FindRoadItemByDirections(mask, 0);
            if (roadItem == null) return false;

            index = AddRoadPlacement(roadItem, cell, height, mask);
            if (isStartPointMode && index >= 0)
            {
                var sp = GetOrCreateStartPoint(editingTeamNumber);
                if (sp.BaseRoads == null) sp.BaseRoads = new();
                sp.BaseRoads.Add(currentMap.Roads[index]);
                SetStartPoint(editingTeamNumber, sp);
            }

            return index >= 0;
        }

        int AddRoadPlacement(RegistryItem roadItem, int2 cell, int height, byte mask)
        {
            var placement = new RoadPlacement
            {
                MainKey    = roadItem.MainKey,
                VariantKey = roadItem.VariantKey,
                Cell       = cell,
                Position   = GetRoadPosition(cell, height, roadItem),
                Height     = height,
                Directions = mask,
                Scale      = instanceScale,
            };
            currentMap.Roads.Add(placement);
            int idx = currentMap.Roads.Count - 1;

            MarkArea(cell, new Vector2Int(1, 1), height, PlacementKind.Road, idx);

            if (!currentMap.RequiredDlcs.Contains(roadItem.DlcKey))
                currentMap.RequiredDlcs.Add(roadItem.DlcKey);

            InstantiateRoadAt(idx);
            return idx;
        }

        void InstantiateRoadAt(int roadIdx)
        {
            var p = currentMap.Roads[roadIdx];
            var item = FindRegistryItem(p.MainKey, p.VariantKey);
            if (item == null || item.Prefab == null) return;
            p.Scale = ResolveSavedScale(p.Scale, item.Prefab);
            p.Position = ResolveRoadPosition(p, item);
            currentMap.Roads[roadIdx] = p;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
            go.name      = $"{item.Name}_Road_{roadIdx}";
            go.hideFlags = HideFlags.DontSave;
            go.transform.position   = p.Position;
            go.transform.localScale = Vector3.one * ResolveSavedScale(p.Scale, item.Prefab);
            foreach (var col in go.GetComponentsInChildren<Collider>())
                col.enabled = false;

            scenePlacements.Add(new ScenePlacement
            {
                Go    = go,
                Index = roadIdx,
                Kind  = PlacementKind.Road,
            });
        }

        void RemoveRoadInstance(int roadIdx)
        {
            for (int i = scenePlacements.Count - 1; i >= 0; i--)
            {
                if (scenePlacements[i].Kind == PlacementKind.Road
                    && scenePlacements[i].Index == roadIdx)
                {
                    if (scenePlacements[i].Go != null)
                        DestroyImmediate(scenePlacements[i].Go);
                    scenePlacements.RemoveAt(i);
                    return;
                }
            }
        }

        void RemoveRoadAt(int2 cell, int height)
        {
            int idx = FindRoadIndex(cell, height);
            if (idx < 0) return;

            RemoveRoadInstance(idx);
            UnmarkArea(cell, new Vector2Int(1, 1), height);
            currentMap.Roads.RemoveAt(idx);

            // 인덱스 시프트로 다른 도로의 ScenePlacement.Index 갱신 필요
            for (int i = 0; i < scenePlacements.Count; i++)
            {
                if (scenePlacements[i].Kind == PlacementKind.Road
                    && scenePlacements[i].Index > idx)
                {
                    var sp = scenePlacements[i];
                    sp.Index--;
                    scenePlacements[i] = sp;
                }
            }

            // 점유 셀의 인덱스도 시프트
            var keys = new List<OccupancyKey>(occupiedCells.Keys);
            foreach (var k in keys)
            {
                var entry = occupiedCells[k];
                if (entry.Kind == PlacementKind.Road && entry.Index > idx)
                {
                    entry.Index--;
                    occupiedCells[k] = entry;
                }
            }
        }

        // ── 도로 조회 헬퍼 ─────────────────────────────────────────
        bool HasRoadAt(int2 cell, int height) => FindRoadIndex(cell, height) >= 0;

        bool TryGetRoadAt(int2 cell, int height, out RoadPlacement rp)
        {
            int idx = FindRoadIndex(cell, height);
            if (idx >= 0) { rp = currentMap.Roads[idx]; return true; }
            rp = default; return false;
        }

        int FindRoadIndex(int2 cell, int height)
        {
            // 점유 셀에서 빠르게 찾기
            if (occupiedCells.TryGetValue(new OccupancyKey(cell, height), out var entry)
                && entry.Kind == PlacementKind.Road)
                return entry.Index;
            return -1;
        }

        byte GetRoadMask(RegistryItem item)
        {
            return item != null ? item.GetRoadDirectionMask() : (byte)0;
        }

        bool IsRoadItem(RegistryItem item)
        {
            return item != null
                && (item.ObjectType == PrefabObjectType.Road
                    || item.Category == PrefabCategory.Road
                    || item.RoadShape != RoadShape.NotRoad
                    || GetRoadMask(item) != 0);
        }

        RegistryItem FindRoadItemByDirections(byte directions, int variantKey)
        {
            foreach (var reg in registries)
            {
                if (reg == null) continue;
                foreach (var it in reg.Items)
                {
                    if (it.IsDeleted) continue;
                    if (it.VariantKey == variantKey && GetRoadMask(it) == directions)
                        return it;
                }
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════
        //  점유 셀 관리
        // ══════════════════════════════════════════════════════════
        bool IsAreaFree(int2 origin, Vector2Int size, int height)
        {
            if (!IsFootprintInMap(origin, size)) return false;

            int sx = Mathf.Max(1, size.x);
            int sz = Mathf.Max(1, size.y);

            for (int dx = 0; dx < sx; dx++)
                for (int dz = 0; dz < sz; dz++)
                {
                    var c = new int2(origin.x + dx, origin.y + dz);
                    if (occupiedCells.ContainsKey(new OccupancyKey(c, height))) return false;
                }
            return true;
        }

        bool BlocksOccupancy(RegistryItem item)
            => item != null && item.ShouldBlockOccupancy();

        bool CanPlaceItemAt(int2 origin, RegistryItem item, int surfaceHeight, int height)
        {
            var footprint = GetFootprintSize(item);

            if (!TryResolvePlacementSurface(origin, item, out int resolvedSurfaceHeight)
                || resolvedSurfaceHeight != surfaceHeight
                || height < surfaceHeight)
                return false;

            if (IsRoadItem(item))
            {
                if (!CanRoadOccupy(origin, height)) return false;
            }
            else if (BlocksOccupancy(item) && !IsAreaFree(origin, footprint, height))
            {
                return false;
            }

            if (item.RequiresRoadAdjacency && !HasAdjacentRoad(origin, footprint, height))
                return false;

            return true;
        }

        bool HasAdjacentRoad(int2 origin, Vector2Int size, int height)
        {
            int sx = Mathf.Max(1, size.x);
            int sz = Mathf.Max(1, size.y);

            for (int x = origin.x; x < origin.x + sx; x++)
            {
                if (HasRoadAt(new int2(x, origin.y - 1), height)) return true;
                if (HasRoadAt(new int2(x, origin.y + sz), height)) return true;
            }

            for (int z = origin.y; z < origin.y + sz; z++)
            {
                if (HasRoadAt(new int2(origin.x - 1, z), height)) return true;
                if (HasRoadAt(new int2(origin.x + sx, z), height)) return true;
            }

            return false;
        }

        bool IsFootprintInMap(int2 origin, Vector2Int size)
        {
            if (currentMap == null) return false;
            return origin.x >= 0
                && origin.y >= 0
                && origin.x + Mathf.Max(1, size.x) <= currentMap.Settings.Width
                && origin.y + Mathf.Max(1, size.y) <= currentMap.Settings.Height;
        }

        bool TryResolvePlacementSurface(int2 origin, RegistryItem item, out int height)
        {
            height = 0;
            var footprint = GetFootprintSize(item);
            if (item == null || !IsFootprintInMap(origin, footprint)) return false;

            bool hasHeight = false;

            for (int dx = 0; dx < footprint.x; dx++)
                for (int dz = 0; dz < footprint.y; dz++)
                {
                    var cell = new int2(origin.x + dx, origin.y + dz);
                    var terrain = GetTerrainCellData(cell);

                    if (!AllowsTerrain(item, terrain.TerrainLayer.Terrain))
                        return false;

                    if (!hasHeight)
                    {
                        height = terrain.TerrainLayer.Height;
                        hasHeight = true;
                    }
                    else if (item.RequiresFlatFootprint && terrain.TerrainLayer.Height != height)
                    {
                        return false;
                    }
                }

            return hasHeight;
        }

        bool AllowsTerrain(RegistryItem item, MapTerrainType terrain)
            => item != null
            && (item.GetAllowedTerrainMask() & MapTerrainMaskUtility.ToMask(terrain)) != 0;

        bool TryGetTerrainCellData(int2 cell, out TerrainCellData terrain)
        {
            if (currentMap?.TerrainCells != null)
            {
                for (int i = 0; i < currentMap.TerrainCells.Count; i++)
                {
                    var data = currentMap.TerrainCells[i];
                    if (!data.Cell.Equals(cell)) continue;
                    terrain = data;
                    return true;
                }
            }

            terrain = default;
            return false;
        }

        TerrainCellData GetTerrainCellData(int2 cell)
        {
            if (TryGetTerrainCellData(cell, out var terrain))
                return terrain;

            return TerrainCellData.Create(cell, 0, MapTerrainType.Land);
        }

        void MarkArea(int2 origin, Vector2Int size, int height, PlacementKind kind, int index)
        {
            int sx = Mathf.Max(1, size.x);
            int sz = Mathf.Max(1, size.y);

            for (int dx = 0; dx < sx; dx++)
                for (int dz = 0; dz < sz; dz++)
                {
                    var c = new int2(origin.x + dx, origin.y + dz);
                    occupiedCells[new OccupancyKey(c, height)] =
                        new OccupancyEntry { Kind = kind, Index = index };
                }
        }

        void UnmarkArea(int2 origin, Vector2Int size, int height)
        {
            int sx = Mathf.Max(1, size.x);
            int sz = Mathf.Max(1, size.y);

            for (int dx = 0; dx < sx; dx++)
                for (int dz = 0; dz < sz; dz++)
                {
                    var c = new int2(origin.x + dx, origin.y + dz);
                    occupiedCells.Remove(new OccupancyKey(c, height));
                }
        }

        // ══════════════════════════════════════════════════════════
        //  씬 선택 / 다중 선택
        // ══════════════════════════════════════════════════════════
        void HandleSceneSelection(Event e)
        {
            bool isLeftDown  = e.type == EventType.MouseDown && e.button == 0;
            bool isRightDown = e.type == EventType.MouseDown && e.button == 1 && e.control;

            if (!isLeftDown && !isRightDown) return;

            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            float bestDist = float.MaxValue;
            int   bestSceneIdx = -1;

            for (int i = 0; i < scenePlacements.Count; i++)
            {
                var sp = scenePlacements[i];
                if (sp.Go == null || !sp.Go.activeInHierarchy) continue;

                var bounds = GetBounds(sp.Go);
                if (bounds.IntersectRay(ray, out float dist) && dist < bestDist)
                {
                    bestDist = dist;
                    bestSceneIdx = i;
                }
            }

            // ── 우클릭: 즉시 삭제 ──────────────────────────────────
            if (isRightDown)
            {
                if (bestSceneIdx >= 0)
                {
                    var sp = scenePlacements[bestSceneIdx];
                    DeleteByKindAndIndex(sp.Kind, sp.Index);
                }
                else
                {
                    // 셀 기준 삭제 (도로/Multi용 — 클릭 위치의 셀)
                    var plane = new Plane(Vector3.up, Vector3.zero);
                    if (plane.Raycast(ray, out float t))
                    {
                        var hitPos = ray.GetPoint(t);
                        int2 cell = WorldToCell(hitPos);
                        DeletePlacementAt(cell);
                    }
                }
                e.Use(); Repaint();
                return;
            }

            // ── 좌클릭: 선택 ──────────────────────────────────────
            if (bestSceneIdx >= 0)
            {
                var sp = scenePlacements[bestSceneIdx];
                bool isShift = e.shift;

                // Shift 없으면 전체 선택 초기화
                if (!isShift) ClearAllSelections();

                if (sp.Kind == PlacementKind.Single)
                {
                    if (selectedSinglePlacements.Contains(sp.Index))
                        selectedSinglePlacements.Remove(sp.Index);
                    else
                        selectedSinglePlacements.Add(sp.Index);
                }
                else if (sp.Kind == PlacementKind.Multi)
                {
                    if (selectedMultiPlacements.Contains(sp.Index))
                        selectedMultiPlacements.Remove(sp.Index);
                    else
                        selectedMultiPlacements.Add(sp.Index);
                }
                else if (sp.Kind == PlacementKind.Road)
                {
                    if (selectedRoadPlacements.Contains(sp.Index))
                        selectedRoadPlacements.Remove(sp.Index);
                    else
                        selectedRoadPlacements.Add(sp.Index);
                }

                e.Use(); Repaint();
            }
            else if (!e.shift)
            {
                ClearAllSelections();
                Repaint();
            }
        }

        void ClearAllSelections()
        {
            selectedSinglePlacements.Clear();
            selectedMultiPlacements.Clear();
            selectedRoadPlacements.Clear();
        }

        /// <summary>
        /// 종류 + 인덱스로 즉시 삭제.
        /// 우클릭 또는 Delete 키에서 사용.
        /// </summary>
        void DeleteByKindAndIndex(PlacementKind kind, int index)
        {
            switch (kind)
            {
                case PlacementKind.Single:
                    RemoveSinglePlacement(index);
                    selectedSinglePlacements.Remove(index);
                    RebuildOccupancyAndRefs();
                    break;

                case PlacementKind.Multi:
                    RemoveMultiPlacement(index);
                    selectedMultiPlacements.Remove(index);
                    RebuildOccupancyAndRefs();
                    break;

                case PlacementKind.Road:
                    // 도로 삭제 시 셀 찾아서 RemoveRoadAt 호출
                    if (index >= 0 && index < currentMap.Roads.Count)
                    {
                        var road = currentMap.Roads[index];
                        RemoveRoadAt(road.Cell, road.Height);
                        selectedRoadPlacements.Remove(index);
                    }
                    break;
            }
        }

        /// <summary>
        /// 셀 좌표 기준으로 해당 셀에 있는 것 즉시 삭제.
        /// 우클릭으로 빈 공간 클릭 시 셀에 있는 도로/Multi 삭제.
        /// </summary>
        void DeletePlacementAt(int2 cell)
        {
            int height = int.MinValue;
            OccupancyEntry entry = default;
            foreach (var kv in occupiedCells)
            {
                if (!kv.Key.Cell.Equals(cell) || kv.Key.Height < height) continue;
                height = kv.Key.Height;
                entry = kv.Value;
            }
            if (height == int.MinValue) return;

            switch (entry.Kind)
            {
                case PlacementKind.Road:
                    RemoveRoadAt(cell, height);
                    break;

                case PlacementKind.Multi:
                    RemoveMultiPlacement(entry.Index);
                    RebuildOccupancyAndRefs();
                    break;

                case PlacementKind.Single:
                    // Single은 선택 후 삭제 유도
                    // (우클릭으로 찾아지므로 bestSceneIdx에서 이미 처리됨)
                    RemoveSinglePlacement(entry.Index);
                    RebuildOccupancyAndRefs();
                    break;
            }
        }

        static Bounds GetBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        // ══════════════════════════════════════════════════════════
        //  삭제
        // ══════════════════════════════════════════════════════════
        void DeleteSelectedPlacements()
        {
            // 인덱스가 큰 것부터 삭제 (인덱스 시프트 방지)
            var singleSorted = new List<int>(selectedSinglePlacements);
            singleSorted.Sort((a, b) => b.CompareTo(a));
            foreach (int idx in singleSorted) RemoveSinglePlacement(idx);

            var multiSorted = new List<int>(selectedMultiPlacements);
            multiSorted.Sort((a, b) => b.CompareTo(a));
            foreach (int idx in multiSorted) RemoveMultiPlacement(idx);

            // 도로는 셀 기준 삭제 (인덱스 시프트 복잡 → 셀 직접 사용)
            var roadSorted = new List<int>(selectedRoadPlacements);
            roadSorted.Sort((a, b) => b.CompareTo(a));
            foreach (int idx in roadSorted)
            {
                if (idx >= 0 && idx < currentMap.Roads.Count)
                {
                    var road = currentMap.Roads[idx];
                    RemoveRoadAt(road.Cell, road.Height);
                }
            }

            ClearAllSelections();
            RebuildOccupancyAndRefs();
            Repaint();
        }

        void RemoveSinglePlacement(int singleIdx)
        {
            if (singleIdx < 0 || singleIdx >= currentMap.Singles.Count) return;

            // 씬 인스턴스 제거
            for (int i = scenePlacements.Count - 1; i >= 0; i--)
            {
                if (scenePlacements[i].Kind == PlacementKind.Single
                    && scenePlacements[i].Index == singleIdx)
                {
                    if (scenePlacements[i].Go != null)
                        DestroyImmediate(scenePlacements[i].Go);
                    scenePlacements.RemoveAt(i);
                    break;
                }
            }

            currentMap.Singles.RemoveAt(singleIdx);
        }

        void RemoveMultiPlacement(int multiIdx)
        {
            if (multiIdx < 0 || multiIdx >= currentMap.Multis.Count) return;

            for (int i = scenePlacements.Count - 1; i >= 0; i--)
            {
                if (scenePlacements[i].Kind == PlacementKind.Multi
                    && scenePlacements[i].Index == multiIdx)
                {
                    if (scenePlacements[i].Go != null)
                        DestroyImmediate(scenePlacements[i].Go);
                    scenePlacements.RemoveAt(i);
                    break;
                }
            }

            currentMap.Multis.RemoveAt(multiIdx);
            RebuildOccupancyAndRefs();
        }

        /// <summary>
        /// 인덱스 시프트 후 점유 셀과 scenePlacements의 Index 재매핑.
        /// 단순화 위해 전체 재구축.
        /// </summary>
        void RebuildOccupancyAndRefs()
        {
            // 모든 씬 인스턴스 제거 후 다시 생성하면 가장 안전
            ClearScenePlacements();
            occupiedCells.Clear();

            for (int i = 0; i < currentMap.Singles.Count; i++)
            {
                var p = currentMap.Singles[i];
                var item = FindRegistryItem(p.MainKey, p.VariantKey);
                if (item == null) continue;
                p.Scale = ResolveSavedScale(p.Scale, item.Prefab);

                NormalizeSinglePlacement(ref p, item);
                currentMap.Singles[i] = p;
                int2 cellOrigin = p.Cell;
                int height = p.Height;
                if (BlocksOccupancy(item))
                    MarkArea(cellOrigin, GetFootprintSize(item), height, PlacementKind.Single, i);

                var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
                go.name      = $"{item.Name}_{i}";
                go.hideFlags = HideFlags.DontSave;
                go.transform.position   = p.Position;
                go.transform.rotation   = Quaternion.Euler(0, p.RotationY, 0);
                go.transform.localScale = Vector3.one * ResolveSavedScale(p.Scale, item.Prefab);
                foreach (var col in go.GetComponentsInChildren<Collider>())
                    col.enabled = false;

                scenePlacements.Add(new ScenePlacement
                { Go = go, Index = i, Kind = PlacementKind.Single });
            }

            for (int i = 0; i < currentMap.Multis.Count; i++)
            {
                var p = currentMap.Multis[i];
                var item = FindRegistryItem(p.MainKey, p.VariantKey);
                if (item == null) continue;
                p.Scale = ResolveSavedScale(p.Scale, item.Prefab);
                NormalizeMultiPlacement(ref p, item);
                currentMap.Multis[i] = p;

                if (BlocksOccupancy(item))
                    MarkArea(p.Cell, GetFootprintSize(item), p.Height, PlacementKind.Multi, i);

                var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
                go.name      = $"{item.Name}_Multi_{i}";
                go.hideFlags = HideFlags.DontSave;
                go.transform.position = p.Position;
                go.transform.rotation   = Quaternion.identity;
                go.transform.localScale = Vector3.one * ResolveSavedScale(p.Scale, item.Prefab);
                foreach (var col in go.GetComponentsInChildren<Collider>())
                    col.enabled = false;

                scenePlacements.Add(new ScenePlacement
                { Go = go, Index = i, Kind = PlacementKind.Multi });
            }

            // 도로 재구축
            for (int i = 0; i < currentMap.Roads.Count; i++)
            {
                InstantiateRoadAt(i);
            }

            // StartPoint 베이스 기지 재구축
            if (currentMap.StartPoints != null)
            {
                foreach (var sp in currentMap.StartPoints)
                {
                    // BaseSingles
                    if (sp.BaseSingles != null)
                    {
                        for (int i = 0; i < sp.BaseSingles.Count; i++)
                        {
                            var p    = sp.BaseSingles[i];
                            var item = FindRegistryItem(p.MainKey, p.VariantKey);
                            if (item == null) continue;
                            p.Scale = ResolveSavedScale(p.Scale, item.Prefab);
                            NormalizeSinglePlacement(ref p, item);
                            sp.BaseSingles[i] = p;

                            int2 cellOrigin = p.Cell;
                            // 점유 (Single과 공유 — 통합 점유)
                            int height = p.Height;
                            var footprint = GetFootprintSize(item);
                            if (BlocksOccupancy(item) && IsAreaFree(cellOrigin, footprint, height))
                                MarkArea(cellOrigin, footprint, height, PlacementKind.Single, -1);

                            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
                            go.name      = $"SP{sp.Number}_Single_{i}";
                            go.hideFlags = HideFlags.DontSave;
                            go.transform.position   = p.Position;
                            go.transform.rotation   = Quaternion.identity;
                            go.transform.localScale = Vector3.one * ResolveSavedScale(p.Scale, item.Prefab);
                            foreach (var col in go.GetComponentsInChildren<Collider>())
                                col.enabled = false;

                            // 팀 컬러 구분용 — Index에 팀 번호 음수로 인코딩
                            scenePlacements.Add(new ScenePlacement
                            {
                                Go    = go,
                                Index = -(sp.Number * 10000 + i),
                                Kind  = PlacementKind.Single,
                            });
                        }
                    }

                    // BaseMultis
                    if (sp.BaseMultis != null)
                    {
                        for (int i = 0; i < sp.BaseMultis.Count; i++)
                        {
                            var p    = sp.BaseMultis[i];
                            var item = FindRegistryItem(p.MainKey, p.VariantKey);
                            if (item == null) continue;
                            p.Scale = ResolveSavedScale(p.Scale, item.Prefab);
                            NormalizeMultiPlacement(ref p, item);
                            sp.BaseMultis[i] = p;

                            var footprint = GetFootprintSize(item);
                            if (BlocksOccupancy(item) && IsAreaFree(p.Cell, footprint, p.Height))
                                MarkArea(p.Cell, footprint, p.Height, PlacementKind.Multi, -1);

                            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
                            go.name      = $"SP{sp.Number}_Multi_{i}";
                            go.hideFlags = HideFlags.DontSave;
                            go.transform.position   = p.Position;
                            go.transform.rotation   = Quaternion.identity;
                            go.transform.localScale = Vector3.one * ResolveSavedScale(p.Scale, item.Prefab);
                            foreach (var col in go.GetComponentsInChildren<Collider>())
                                col.enabled = false;

                            scenePlacements.Add(new ScenePlacement
                            {
                                Go    = go,
                                Index = -(sp.Number * 10000 + 2500 + i),
                                Kind  = PlacementKind.Multi,
                            });
                        }
                    }

                    // BaseRoads
                    if (sp.BaseRoads != null)
                    {
                        for (int i = 0; i < sp.BaseRoads.Count; i++)
                        {
                            var p    = sp.BaseRoads[i];
                            var item = FindRegistryItem(p.MainKey, p.VariantKey);
                            if (item == null) continue;
                            p.Scale = ResolveSavedScale(p.Scale, item.Prefab);
                            p.Position = ResolveRoadPosition(p, item);
                            sp.BaseRoads[i] = p;

                            if (IsAreaFree(p.Cell, new Vector2Int(1, 1), p.Height))
                                MarkArea(p.Cell, new Vector2Int(1, 1), p.Height, PlacementKind.Road, -1);

                            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
                            go.name      = $"SP{sp.Number}_Road_{i}";
                            go.hideFlags = HideFlags.DontSave;
                            go.transform.position   = p.Position;
                            go.transform.localScale = Vector3.one * ResolveSavedScale(p.Scale, item.Prefab);
                            foreach (var col in go.GetComponentsInChildren<Collider>())
                                col.enabled = false;

                            scenePlacements.Add(new ScenePlacement
                            {
                                Go    = go,
                                Index = -(sp.Number * 10000 + 5000 + i),
                                Kind  = PlacementKind.Road,
                            });
                        }
                    }
                }
            }

            ApplyCategoryVisibility();
        }

        void UndoLastPlacement()
        {
            if (currentMap.Singles.Count > 0)
            {
                int lastIdx = currentMap.Singles.Count - 1;
                var p = currentMap.Singles[lastIdx];
                var item = FindRegistryItem(p.MainKey, p.VariantKey);
                if (BlocksOccupancy(item))
                {
                    UnmarkArea(p.Cell, GetFootprintSize(item), p.Height);
                }
                RemoveSinglePlacement(lastIdx);
                Repaint();
            }
        }

        // ══════════════════════════════════════════════════════════
        //  씬 동기화 (편집 후)
        // ══════════════════════════════════════════════════════════
        void SyncSinglePlacement(int singleIndex)
        {
            for (int i = 0; i < scenePlacements.Count; i++)
            {
                var sp = scenePlacements[i];
                if (sp.Kind != PlacementKind.Single || sp.Index != singleIndex) continue;
                if (sp.Go == null) continue;

                var p = currentMap.Singles[singleIndex];
                var item = FindRegistryItem(p.MainKey, p.VariantKey);
                if (item == null) return;

                sp.Go.transform.position   = p.Position;
                sp.Go.transform.rotation   = Quaternion.Euler(0, p.RotationY, 0);
                sp.Go.transform.localScale = Vector3.one * ResolveSavedScale(p.Scale, item.Prefab);
                break;
            }
            SceneView.RepaintAll();
        }

        void ClearScenePlacements()
        {
            foreach (var sp in scenePlacements)
                if (sp.Go != null) DestroyImmediate(sp.Go);
            scenePlacements.Clear();
            ClearAllSelections();
        }

        // ══════════════════════════════════════════════════════════
        //  스케일 단축키
        // ══════════════════════════════════════════════════════════
        void HandleScaleShortcuts(Event e)
        {
            if (e.type != EventType.KeyDown) return;
            if (selectedMainKey == int.MinValue) return;

            float step = e.shift ? 0.01f : 0.1f;
            bool changed = false;

            if (e.keyCode == KeyCode.Equals || e.keyCode == KeyCode.Plus
                || e.keyCode == KeyCode.KeypadPlus)
            { instanceScale = Mathf.Max(0.01f, instanceScale + step); changed = true; }
            else if (e.keyCode == KeyCode.Minus || e.keyCode == KeyCode.KeypadMinus)
            { instanceScale = Mathf.Max(0.01f, instanceScale - step); changed = true; }
            else if (e.keyCode == KeyCode.Alpha0 || e.keyCode == KeyCode.Keypad0)
            {
                var p = GetSelectedPrefab();
                instanceScale = GetPrefabDefaultScale(p);
                changed = true;
            }

            if (changed) { e.Use(); Repaint(); }
        }

        // ══════════════════════════════════════════════════════════
        //  헬퍼
        // ══════════════════════════════════════════════════════════
        RegistryItem GetSelectedItem()
        {
            if (selectedMainKey == int.MinValue) return null;
            return FindRegistryItem(selectedMainKey, selectedVariantKey);
        }

        GameObject GetSelectedPrefab() => GetSelectedItem()?.Prefab;

        float GetPrefabDefaultScale(GameObject prefab)
        {
            return prefab != null ? Mathf.Max(0.01f, prefab.transform.localScale.x) : 1f;
        }

        float ResolveSavedScale(float savedScale, GameObject prefab)
        {
            return savedScale > 0f ? savedScale : GetPrefabDefaultScale(prefab);
        }

        void ResetInstanceSettings(RegistryItem item)
        {
            instanceScale = GetPrefabDefaultScale(item?.Prefab);
            instanceRotationY = 0f;
            previewHeight = 0f;
        }

        Vector3 GetMultiPosition(int2 cell, int height, RegistryItem item, float extraYOffset = 0f)
        {
            float cs = currentMap.Settings.CellSize;
            float y = GameUtility.GetHeightWorldPosition(height, cs, item.Offset.y + extraYOffset);
            var footprint = GetFootprintSize(item);
            var size = new int2(footprint.x, footprint.y);
            return GameUtility.GetFootprintCenterWorldPosition(cell, size, cs, y);
        }

        Vector3 GetSinglePosition(int2 cell, int height, RegistryItem item, float extraYOffset = 0f)
        {
            float cs = currentMap.Settings.CellSize;
            float y = GameUtility.GetHeightWorldPosition(height, cs, item.Offset.y + extraYOffset);
            var footprint = GetFootprintSize(item);
            var size = new int2(footprint.x, footprint.y);
            return GameUtility.GetFootprintCenterWorldPosition(cell, size, cs, y);
        }

        Vector3 GetRoadPosition(int2 cell, int height, RegistryItem item)
        {
            float cs = currentMap.Settings.CellSize;
            float y = GameUtility.GetHeightWorldPosition(height, cs, item.Offset.y);
            var footprint = GetFootprintSize(item);
            var size = new int2(footprint.x, footprint.y);
            return GameUtility.GetFootprintCenterWorldPosition(cell, size, cs, y);
        }

        Vector3 ResolveMultiPosition(MultiPlacement placement, RegistryItem item)
        {
            return GetMultiPosition(placement.Cell, placement.Height, item);
        }

        Vector3 ResolveRoadPosition(RoadPlacement placement, RegistryItem item)
        {
            return GetRoadPosition(placement.Cell, placement.Height, item);
        }

        void NormalizeMultiPlacement(ref MultiPlacement placement, RegistryItem item)
        {
            int surfaceHeight = GetTerrainCellData(placement.Cell).Height;
            int inferredHeight = Mathf.Max(surfaceHeight, GetPlacementHeightIndex(placement.Position.y, item));
            if (placement.Height < surfaceHeight || inferredHeight > placement.Height)
                placement.Height = inferredHeight;
            placement.Position = ResolveMultiPosition(placement, item);
        }

        void NormalizeSinglePlacement(ref SinglePlacement placement, RegistryItem item)
        {
            if (placement.Cell.Equals(default))
                placement.Cell = FootprintOriginFromPosition(placement.Position, GetFootprintSize(item));

            int surfaceHeight = GetTerrainCellData(placement.Cell).Height;
            int inferredHeight = Mathf.Max(surfaceHeight, GetPlacementHeightIndex(placement.Position.y, item));
            if (placement.Height < surfaceHeight || inferredHeight > placement.Height)
                placement.Height = inferredHeight;
        }

        RegistryItem FindRegistryItem(int mainKey, int variantKey)
        {
            foreach (var reg in registries)
            {
                if (reg == null) continue;
                var item = reg.Find(mainKey, variantKey);
                if (item != null) return item;
            }
            return null;
        }

        RegistryItem FindItemByIndex(PlacementKind kind, int index)
        {
            if (index < 0) return null;

            if (kind == PlacementKind.Single && index < currentMap.Singles.Count)
            {
                var p = currentMap.Singles[index];
                return FindRegistryItem(p.MainKey, p.VariantKey);
            }
            if (kind == PlacementKind.Multi && index < currentMap.Multis.Count)
            {
                var p = currentMap.Multis[index];
                return FindRegistryItem(p.MainKey, p.VariantKey);
            }
            if (kind == PlacementKind.Road && index < currentMap.Roads.Count)
            {
                var p = currentMap.Roads[index];
                return FindRegistryItem(p.MainKey, p.VariantKey);
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════
        //  StartPoint 헬퍼
        // ══════════════════════════════════════════════════════════

        TeamStartPoint GetOrCreateStartPoint(int teamNumber)
        {
            if (currentMap.StartPoints == null)
                currentMap.StartPoints = new();

            for (int i = 0; i < currentMap.StartPoints.Count; i++)
                if (currentMap.StartPoints[i].Number == teamNumber)
                    return currentMap.StartPoints[i];

            // 없으면 신규 생성
            var sp = new TeamStartPoint
            {
                Number      = teamNumber,
                Position    = Vector3.zero,
                BaseSingles = new(),
                BaseMultis  = new(),
                BaseRoads   = new(),
            };
            currentMap.StartPoints.Add(sp);
            return sp;
        }

        TeamStartPoint GetStartPoint(int teamNumber)
        {
            if (currentMap.StartPoints == null) return default;
            for (int i = 0; i < currentMap.StartPoints.Count; i++)
                if (currentMap.StartPoints[i].Number == teamNumber)
                    return currentMap.StartPoints[i];
            return default;
        }

        void SetStartPoint(int teamNumber, TeamStartPoint updated)
        {
            if (currentMap.StartPoints == null) return;
            for (int i = 0; i < currentMap.StartPoints.Count; i++)
            {
                if (currentMap.StartPoints[i].Number == teamNumber)
                {
                    currentMap.StartPoints[i] = updated;
                    return;
                }
            }
            currentMap.StartPoints.Add(updated);
        }

        void RemoveStartPoint(int teamNumber)
        {
            if (currentMap.StartPoints == null) return;

            // 해당 팀 베이스 기지의 씬 인스턴스 제거
            // (ScenePlacement에 팀 태그가 없으므로 전체 재구축)
            for (int i = currentMap.StartPoints.Count - 1; i >= 0; i--)
            {
                if (currentMap.StartPoints[i].Number == teamNumber)
                {
                    currentMap.StartPoints.RemoveAt(i);
                    break;
                }
            }
            RebuildOccupancyAndRefs();
            Repaint();
        }

        void ClearStartPointBase(int teamNumber)
        {
            var sp = GetOrCreateStartPoint(teamNumber);
            sp.BaseSingles?.Clear();
            sp.BaseMultis?.Clear();
            sp.BaseRoads?.Clear();
            SetStartPoint(teamNumber, sp);
            RebuildOccupancyAndRefs();
            Repaint();
        }

        // ── 스타트포인트 위치 클릭 처리 ───────────────────────────
        void HandleStartPointPositionClick(Event e)
        {
            if (!isSettingPosition) return;
            if (e.type != EventType.MouseDown || e.button != 0) return;

            var ray   = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            var plane = new Plane(Vector3.up, Vector3.up * previewHeight);
            if (!plane.Raycast(ray, out float t)) return;

            var pos = ray.GetPoint(t);
            var sp  = GetOrCreateStartPoint(editingTeamNumber);
            sp.Position = pos;
            SetStartPoint(editingTeamNumber, sp);

            isSettingPosition = false;
            e.Use();
            Repaint();
            SceneView.RepaintAll();
        }

        // ── 스타트포인트 기즈모 (씬 시각화) ──────────────────────
        void DrawStartPointGizmos()
        {
            if (currentMap?.StartPoints == null) return;

            var prevColor = Handles.color;

            foreach (var sp in currentMap.StartPoints)
            {
                if (sp.Number < 1 || sp.Number > 8) continue;
                var color = TeamColors[sp.Number];

                // 구체
                Handles.color = color;
                Handles.SphereHandleCap(
                    0,
                    sp.Position,
                    Quaternion.identity,
                    currentMap.Settings.CellSize,
                    EventType.Repaint);

                // 팀 번호 레이블
                Handles.color = Color.white;
                Handles.Label(
                    sp.Position + Vector3.up * currentMap.Settings.CellSize * 1.2f,
                    $"T{sp.Number}",
                    new GUIStyle(EditorStyles.boldLabel)
                    {
                        normal = { textColor = color },
                        fontSize = 14,
                    });

                // 위치 지정 중이면 강조
                if (isSettingPosition && editingTeamNumber == sp.Number)
                {
                    Handles.color = new Color(color.r, color.g, color.b, 0.3f);
                    Handles.DrawWireDisc(
                        sp.Position,
                        Vector3.up,
                        currentMap.Settings.CellSize * 2f);
                }
            }

            // 위치 지정 중: 마우스 위치 미리보기 구체
            if (isSettingPosition)
            {
                var ray   = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
                var plane = new Plane(Vector3.up, Vector3.up * previewHeight);
                if (plane.Raycast(ray, out float t))
                {
                    var pos = ray.GetPoint(t);
                    var c = editingTeamNumber >= 1 && editingTeamNumber <= 8
                        ? TeamColors[editingTeamNumber] : Color.white;
                    Handles.color = new Color(c.r, c.g, c.b, 0.5f);
                    Handles.SphereHandleCap(
                        0, pos, Quaternion.identity,
                        currentMap.Settings.CellSize * 0.8f,
                        EventType.Repaint);
                }
            }

            Handles.color = prevColor;
        }

        // ══════════════════════════════════════════════════════════
        //  맵 저장 / 로드 / 초기화
        // ══════════════════════════════════════════════════════════
        void ValidateCurrentMap()
        {
            mapValidationIssues.Clear();
            mapValidationHasRun = true;

            if (currentMap == null)
            {
                AddMapValidationIssue(ValidationLevel.Error, "Map data is null.");
                return;
            }

            if (currentMap.Settings.Width <= 0 || currentMap.Settings.Height <= 0)
                AddMapValidationIssue(ValidationLevel.Error, "Map size must be greater than zero.");
            if (currentMap.Settings.CellSize <= 0f)
                AddMapValidationIssue(ValidationLevel.Error, "Cell size must be greater than zero.");

            ValidateTerrainCells();

            var occupancy = new Dictionary<OccupancyKey, string>();
            if (currentMap.Singles != null)
                for (int i = 0; i < currentMap.Singles.Count; i++)
                    ValidateSinglePlacement(currentMap.Singles[i], $"Singles[{i}]", occupancy);

            if (currentMap.Multis != null)
                for (int i = 0; i < currentMap.Multis.Count; i++)
                    ValidateMultiPlacement(currentMap.Multis[i], $"Multis[{i}]", occupancy);

            if (currentMap.Roads != null)
                for (int i = 0; i < currentMap.Roads.Count; i++)
                    ValidateRoadPlacement(currentMap.Roads[i], $"Roads[{i}]", occupancy);

            ValidateStartPoints(occupancy);
        }

        void ValidateTerrainCells()
        {
            if (currentMap.TerrainCells == null)
                return;

            var seen = new HashSet<int2>();
            for (int i = 0; i < currentMap.TerrainCells.Count; i++)
            {
                var data = currentMap.TerrainCells[i];
                string label = $"TerrainCells[{i}] ({data.Cell.x},{data.Cell.y})";

                if (!IsCellInMap(data.Cell))
                    AddMapValidationIssue(ValidationLevel.Error, $"{label}: outside map bounds.");
                if (!seen.Add(data.Cell))
                    AddMapValidationIssue(ValidationLevel.Error, $"{label}: duplicate terrain override.");
                if (IsDefaultTerrain(data))
                    AddMapValidationIssue(ValidationLevel.Info, $"{label}: default Land/Height 0 override can be removed.");
            }
        }

        void ValidateStartPoints(Dictionary<OccupancyKey, string> occupancy)
        {
            if (currentMap.StartPoints == null)
                return;

            var seenTeams = new HashSet<int>();
            for (int i = 0; i < currentMap.StartPoints.Count; i++)
            {
                var sp = currentMap.StartPoints[i];
                string prefix = $"StartPoints[{i}] Team {sp.Number}";

                if (sp.Number < 1 || sp.Number > 8)
                    AddMapValidationIssue(ValidationLevel.Error, $"{prefix}: team number must be 1~8.");
                if (!seenTeams.Add(sp.Number))
                    AddMapValidationIssue(ValidationLevel.Error, $"{prefix}: duplicate team start point.");

                int2 cell = WorldToCell(sp.Position);
                if (!IsCellInMap(cell))
                    AddMapValidationIssue(ValidationLevel.Warning, $"{prefix}: position is outside map bounds.");

                if (sp.BaseSingles == null && sp.BaseMultis == null && sp.BaseRoads == null)
                    AddMapValidationIssue(ValidationLevel.Warning, $"{prefix}: has no base placement lists.");

                if (sp.BaseSingles != null)
                    for (int j = 0; j < sp.BaseSingles.Count; j++)
                        ValidateSinglePlacement(sp.BaseSingles[j], $"{prefix}.BaseSingles[{j}]", occupancy);

                if (sp.BaseMultis != null)
                    for (int j = 0; j < sp.BaseMultis.Count; j++)
                        ValidateMultiPlacement(sp.BaseMultis[j], $"{prefix}.BaseMultis[{j}]", occupancy);

                if (sp.BaseRoads != null)
                    for (int j = 0; j < sp.BaseRoads.Count; j++)
                        ValidateRoadPlacement(sp.BaseRoads[j], $"{prefix}.BaseRoads[{j}]", occupancy);
            }
        }

        void ValidateSinglePlacement(SinglePlacement placement, string label, Dictionary<OccupancyKey, string> occupancy)
        {
            var item = FindRegistryItem(placement.MainKey, placement.VariantKey);
            if (item == null)
            {
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: missing prefab key ({placement.MainKey}, {placement.VariantKey}).");
                return;
            }

            if (item.Prefab == null)
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: registry item has null prefab.");

            var footprint = GetFootprintSize(item);
            ValidatePlacementSurface(label, placement.Cell, footprint, placement.Height, item);
            AddPlacementOccupancy(label, placement.Cell, footprint, placement.Height, item, PlacementKind.Single, occupancy);
        }

        void ValidateMultiPlacement(MultiPlacement placement, string label, Dictionary<OccupancyKey, string> occupancy)
        {
            var item = FindRegistryItem(placement.MainKey, placement.VariantKey);
            if (item == null)
            {
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: missing prefab key ({placement.MainKey}, {placement.VariantKey}).");
                return;
            }

            if (item.Prefab == null)
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: registry item has null prefab.");

            var footprint = GetFootprintSize(item);
            ValidatePlacementSurface(label, placement.Cell, footprint, placement.Height, item);
            AddPlacementOccupancy(label, placement.Cell, footprint, placement.Height, item, PlacementKind.Multi, occupancy);
        }

        void ValidateRoadPlacement(RoadPlacement placement, string label, Dictionary<OccupancyKey, string> occupancy)
        {
            var item = FindRegistryItem(placement.MainKey, placement.VariantKey);
            if (item == null)
            {
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: missing road prefab key ({placement.MainKey}, {placement.VariantKey}).");
                return;
            }

            if (!IsRoadItem(item))
                AddMapValidationIssue(ValidationLevel.Warning, $"{label}: prefab key is not marked as a road item.");
            if (item.Prefab == null)
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: registry item has null prefab.");

            byte itemDirections = GetRoadMask(item);
            if (placement.Directions == 0)
            {
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: road placement has no Directions.");
            }
            else if (!RoadShapeMapping.IsValidMask(placement.Directions))
            {
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: road Directions {placement.Directions} is invalid.");
            }
            else
            {
                if (itemDirections != placement.Directions)
                {
                    AddMapValidationIssue(ValidationLevel.Error,
                        $"{label}: saved key ({placement.MainKey}, {placement.VariantKey}) has directions {itemDirections}, " +
                        $"but placement stores directions {placement.Directions}.");
                }

                var directionItem = FindRoadItemByDirections(placement.Directions, placement.VariantKey);
                if (directionItem == null)
                {
                    AddMapValidationIssue(ValidationLevel.Error,
                        $"{label}: no road prefab registered for Directions={placement.Directions}, VariantKey={placement.VariantKey}.");
                }
                else if (directionItem.MainKey != placement.MainKey || directionItem.VariantKey != placement.VariantKey)
                {
                    AddMapValidationIssue(ValidationLevel.Warning,
                        $"{label}: road key can be normalized to ({directionItem.MainKey}, {directionItem.VariantKey}) for Directions={placement.Directions}.");
                }
            }

            var footprint = Vector2Int.one;
            ValidatePlacementSurface(label, placement.Cell, footprint, placement.Height, item);
            AddPlacementOccupancy(label, placement.Cell, footprint, placement.Height, item, PlacementKind.Road, occupancy);
        }

        void ValidatePlacementSurface(string label, int2 origin, Vector2Int footprint, int height, RegistryItem item)
        {
            if (!IsFootprintInMap(origin, footprint))
            {
                AddMapValidationIssue(ValidationLevel.Error,
                    $"{label}: footprint is outside map bounds at ({origin.x}, {origin.y}) size {footprint.x}x{footprint.y}.");
                return;
            }

            if (!TryResolvePlacementSurface(origin, item, out int surfaceHeight))
            {
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: terrain type/height does not satisfy placement rules.");
                return;
            }

            if (height < surfaceHeight)
                AddMapValidationIssue(ValidationLevel.Error, $"{label}: placement height {height} is below terrain surface {surfaceHeight}.");

            if (item.RequiresRoadAdjacency && !HasAdjacentRoad(origin, footprint, height))
                AddMapValidationIssue(ValidationLevel.Warning, $"{label}: requires road adjacency but no adjacent road was found.");
        }

        void AddPlacementOccupancy(
            string label,
            int2 origin,
            Vector2Int footprint,
            int height,
            RegistryItem item,
            PlacementKind kind,
            Dictionary<OccupancyKey, string> occupancy)
        {
            bool blocks = kind == PlacementKind.Road || BlocksOccupancy(item);
            if (!blocks || !IsFootprintInMap(origin, footprint))
                return;

            int sx = Mathf.Max(1, footprint.x);
            int sz = Mathf.Max(1, footprint.y);
            for (int x = 0; x < sx; x++)
            for (int y = 0; y < sz; y++)
            {
                var cell = new int2(origin.x + x, origin.y + y);
                var key = new OccupancyKey(cell, height);
                if (occupancy.TryGetValue(key, out string previous))
                {
                    AddMapValidationIssue(ValidationLevel.Error, $"{label}: overlaps {previous} at ({cell.x}, {cell.y}) height {height}.");
                    continue;
                }

                occupancy[key] = label;
            }
        }

        void AddMapValidationIssue(ValidationLevel level, string message)
        {
            mapValidationIssues.Add(new ValidationIssue(level, -1, message));
        }

        int CountMapValidationIssues(ValidationLevel level)
        {
            int count = 0;
            for (int i = 0; i < mapValidationIssues.Count; i++)
                if (mapValidationIssues[i].Level == level)
                    count++;
            return count;
        }

        void SaveMap()
        {
            ValidateCurrentMap();
            int errorCount = CountMapValidationIssues(ValidationLevel.Error);
            if (errorCount > 0
                && !EditorUtility.DisplayDialog(
                    "Map Validation Errors",
                    $"Map validation found {errorCount} error(s). Save anyway?",
                    "Save Anyway",
                    "Cancel"))
            {
                Repaint();
                return;
            }

            if (string.IsNullOrEmpty(mapSavePath))
            {
                mapSavePath = EditorUtility.SaveFilePanel(
                    "Save Map", Application.dataPath, $"{mapName}.json", "json");
                if (string.IsNullOrEmpty(mapSavePath)) return;
            }

            currentMap.MapName = mapName;
            string json = JsonUtility.ToJson(currentMap, prettyPrint: true);
            System.IO.File.WriteAllText(mapSavePath, json, System.Text.Encoding.UTF8);
            AssetDatabase.Refresh();
            Debug.Log($"[MapEditor] Map saved: {mapSavePath}");
        }

        void LoadMap()
        {
            string path = EditorUtility.OpenFilePanel("Load Map", Application.dataPath, "json");
            if (string.IsNullOrEmpty(path)) return;

            string json = System.IO.File.ReadAllText(path);
            currentMap  = JsonUtility.FromJson<MapData>(json);
            EnsureTerrainCells();
            mapName     = currentMap.MapName;
            mapSavePath = path;

            SetTerrainOverlayDirty();
            RebuildOccupancyAndRefs();
            Repaint();
            Debug.Log($"[MapEditor] Map loaded: {path}");
        }

        void ClearMap()
        {
            if (!EditorUtility.DisplayDialog("Clear Map",
                "모든 배치 데이터를 삭제합니다.",
                "Clear", "Cancel")) return;

            currentMap.Singles.Clear();
            currentMap.Multis.Clear();
            currentMap.Roads.Clear();
            EnsureTerrainCells();
            currentMap.TerrainCells.Clear();
            currentMap.RequiredDlcs.Clear();
            currentMap.StartPoints?.Clear();
            ClearScenePlacements();
            occupiedCells.Clear();
            SetTerrainOverlayDirty();
            Repaint();
        }
    }
}
