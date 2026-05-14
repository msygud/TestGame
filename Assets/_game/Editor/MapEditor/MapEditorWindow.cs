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
        bool  snapToFreeHeight  = true;

        // ── 점유 셀 데이터 (맵에디터 전용) ─────────────────────────
        // (cell) → (placement type, placement index)
        Dictionary<OccupancyKey, OccupancyEntry> occupiedCells = new();

        // ── Shift 드래그 상태 ─────────────────────────────────────
        int2 lastPlacedCellOrigin = new int2(int.MinValue, int.MinValue);

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
        Dictionary<PrefabCategory, bool> categoryVisibility = new()
        {
            { PrefabCategory.Terrain,  true },
            { PrefabCategory.Road,     true },
            { PrefabCategory.Building, true },
            { PrefabCategory.Other,    true },
        };

        // ── 그리드 스타일 ──────────────────────────────────────────
        Color gridColor      = new Color(1f, 1f, 1f, 0.3f);
        float gridLineWidth  = 1.5f;
        bool  drawGrid       = true;
        float boundaryGridHeight = 0f;

        // ── UI 상태 ────────────────────────────────────────────────
        Vector2 scrollPos;
        bool showBoundary = true;
        bool showThumbnails = true;

        bool foldSettings   = true;
        bool foldVariant    = true;
        bool foldInstance   = true;
        bool foldRegistries = false;
        bool foldPrefabList = true;
        bool foldVisibility = false;
        bool foldGrid       = false;
        bool foldDebug      = false;

        Dictionary<(int dlcId, PrefabCategory cat), bool> categoryFolds = new();
        Dictionary<int, bool> dlcFolds = new();

        // ── 내부 ──────────────────────────────────────────────────
        struct ScenePlacement
        {
            public GameObject Go;
            public int        Index;          // MapData.Singles 또는 Multis 인덱스
            public PlacementKind Kind;
        }

        enum PlacementKind { Single, Multi, Road }

        struct OccupancyEntry
        {
            public PlacementKind Kind;
            public int           Index;
        }

        struct PlacementSnap
        {
            public int2    Cell;
            public int     Height;
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
                SceneView.RepaintAll();
            }

            EditorGUILayout.LabelField(
                $"Total: {s.Width * s.Height} cells, " +
                $"{s.Width * s.CellSize} x {s.Height * s.CellSize} units");
            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Variant Selector
        // ══════════════════════════════════════════════════════════
        void DrawVariantSelector()
        {
            // 도로 모드인지 확인
            var selItem = GetSelectedItem();
            bool isRoadMode = selItem != null && selItem.RoadShape != RoadShape.NotRoad;

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
                    if (it.RoadShape == RoadShape.NotRoad) continue;
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
            EditorGUILayout.BeginHorizontal();
            float newRot = EditorGUILayout.FloatField("Rotation Y", instanceRotationY);
            if (GUILayout.Button("0", GUILayout.Width(24)))   newRot = 0f;
            if (GUILayout.Button("90", GUILayout.Width(28)))  newRot = 90f;
            if (GUILayout.Button("180", GUILayout.Width(34))) newRot = 180f;
            if (GUILayout.Button("270", GUILayout.Width(34))) newRot = 270f;
            EditorGUILayout.EndHorizontal();
            instanceRotationY = ((newRot % 360f) + 360f) % 360f;

            // 높이
            EditorGUILayout.BeginHorizontal();
            float newHeight = EditorGUILayout.FloatField("Height (Y)", previewHeight);
            if (GUILayout.Button("Reset", GUILayout.Width(50))) newHeight = 0f;
            EditorGUILayout.EndHorizontal();
            previewHeight = Mathf.Max(0f, newHeight);
            snapToFreeHeight = EditorGUILayout.Toggle("Snap To Free Y", snapToFreeHeight);

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
            var byCat = new SortedDictionary<PrefabCategory,
                List<(RegistryItem item, GamePrefabRegistry src)>>();
            foreach (var pair in items)
            {
                if (!byCat.TryGetValue(pair.item.Category, out var l))
                    byCat[pair.item.Category] = l = new();
                l.Add(pair);
            }

            foreach (var kv in byCat)
            {
                var key = (dlcId, kv.Key);
                if (!categoryFolds.TryGetValue(key, out bool open)) open = true;
                open = EditorGUILayout.Foldout(open, $"{kv.Key} ({kv.Value.Count})", true);
                categoryFolds[key] = open;
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
                EditorGUILayout.LabelField(
                    $"Main {selectedMainKey}, V{selectedVariantKey}, " +
                    $"Scale {instanceScale:0.00}, Rot {instanceRotationY:0}",
                    EditorStyles.miniLabel);
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
                foldVisibility, "Category Visibility", true, EditorStyles.foldoutHeader);
            if (!foldVisibility) return;

            EditorGUI.indentLevel++;
            foreach (var cat in System.Enum.GetValues(typeof(PrefabCategory)))
            {
                var c = (PrefabCategory)cat;
                bool current = categoryVisibility.TryGetValue(c, out bool v) && v;
                bool nv = EditorGUILayout.Toggle(c.ToString(), current);
                if (nv != current)
                {
                    categoryVisibility[c] = nv;
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
                if (item == null) continue;

                bool visible = categoryVisibility.TryGetValue(item.Category, out bool v) && v;
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

            DrawStartPointGizmos();
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
                // 배치 모드에서도 우클릭으로 셀 삭제 가능
                if (e.type == EventType.MouseDown && e.button == 1 && e.control)
                {
                    var rray  = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    var rplane = new Plane(Vector3.up, Vector3.up * previewHeight);
                    if (rplane.Raycast(rray, out float rt))
                    {
                        int2 rcell = WorldToCell(rray.GetPoint(rt));
                        DeleteCellAt(rcell);
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
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
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

            Handles.color = prevColor;
            Handles.zTest = prevZTest;
        }

        // ══════════════════════════════════════════════════════════
        //  셀 스냅 (좌하단 기준)
        // ══════════════════════════════════════════════════════════
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

        int ResolvePlacementHeight(int2 cell, Vector2Int size, int desiredHeight)
        {
            if (!snapToFreeHeight) return desiredHeight;

            int height = Mathf.Max(0, desiredHeight);
            for (int i = 0; i < 128; i++)
            {
                if (IsAreaFree(cell, size, height)) return height;
                height++;
            }

            return desiredHeight;
        }

        bool TryGetPlacementSnap(Ray ray, RegistryItem item, out PlacementSnap snap)
        {
            int height = GetHeightIndex(previewHeight);
            bool isRoad = item.RoadShape != RoadShape.NotRoad;

            for (int i = 0; i < 8; i++)
            {
                if (!TryGetCellOnHeightPlane(ray, height, out var cell, out var hitPos))
                {
                    snap = default;
                    return false;
                }

                int resolvedHeight = isRoad && HasRoadAt(cell, height)
                    ? height
                    : ResolvePlacementHeight(cell, item.Size, height);

                if (resolvedHeight != height)
                {
                    height = resolvedHeight;
                    continue;
                }

                float cs = currentMap.Settings.CellSize;
                snap = new PlacementSnap
                {
                    Cell   = cell,
                    Height = height,
                    Origin = new Vector3(
                        Mathf.Floor(hitPos.x / cs) * cs,
                        GetHeightFromIndex(height),
                        Mathf.Floor(hitPos.z / cs) * cs),
                };
                return true;
            }

            snap = default;
            return false;
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
            previewInstance.transform.rotation   = Quaternion.Euler(0, instanceRotationY, 0);
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
            else if (e.alt && !e.control)
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
            bool isDrag  = e.type == EventType.MouseDrag && e.button == 0 && e.shift && !e.alt;

            if (!isClick && !isDrag) return;

            var item = GetSelectedItem();
            if (item == null) return;

            int2 cellOrigin = snap.Cell;

            // Shift 드래그: 셀 영역 다를 때만 배치
            if (isDrag && cellOrigin.Equals(lastPlacedCellOrigin)) return;

            // 도로/일반 분기
            bool isRoad = item.RoadShape != RoadShape.NotRoad;

            if (isRoad)
            {
                if (TryPlaceRoad(item, cellOrigin, snap.Height, isDrag))
                    lastPlacedCellOrigin = cellOrigin;
            }
            else if (item.SpawnMode == PrefabSpawnMode.Single)
            {
                if (TryPlaceSingle(item, cellOrigin, snap.Height))
                    lastPlacedCellOrigin = cellOrigin;
            }
            else
            {
                if (TryPlaceMulti(item, cellOrigin, snap.Height))
                    lastPlacedCellOrigin = cellOrigin;
            }

            e.Use();
        }

        // ── Single 배치 ────────────────────────────────────────────
        bool TryPlaceSingle(RegistryItem item, int2 cellOrigin, int height)
        {
            if (!IsAreaFree(cellOrigin, item.Size, height)) return false;

            var placement = new SinglePlacement
            {
                MainKey    = item.MainKey,
                VariantKey = selectedVariantKey,
                Position   = GetSinglePosition(cellOrigin, height, item),
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
            MarkArea(cellOrigin, item.Size, height, PlacementKind.Single, idx);

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
        bool TryPlaceMulti(RegistryItem item, int2 cellOrigin, int height)
        {
            int seed = System.DateTime.Now.GetHashCode() ^ cellOrigin.GetHashCode();

            if (!IsAreaFree(cellOrigin, item.Size, height)) return false;

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
            MarkArea(cellOrigin, item.Size, height, PlacementKind.Multi, idx);

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
        bool TryPlaceRoad(RegistryItem item, int2 cell, int height, bool isDrag)
        {
            // 빈 셀 / 중첩 구분
            bool isOverlap = HasRoadAt(cell, height);

            // Multi/Single이 점유한 셀에는 도로 못 깜
            if (!isOverlap && occupiedCells.TryGetValue(new OccupancyKey(cell, height), out var occ))
            {
                if (occ.Kind != PlacementKind.Road) return false;
            }

            byte newMask;
            int  newRot90;

            if (isDrag)
            {
                // ── Shift 드래그: 완전 자동 ──────────────────────────
                newMask = ComputeAutoBitmask(cell, height);
                if (newMask == 0)
                {
                    // 인접 도로 0개 — 사용자 선택 그대로 (DeadEnd 등)
                    newMask = RoadShapeMapping.ToBitmask(
                        item.RoadShape, Mathf.RoundToInt(instanceRotationY / 90f));
                }
            }
            else if (!isOverlap)
            {
                // ── 빈 셀 단일 클릭: 사용자 의도 그대로 ────────────────
                newMask = RoadShapeMapping.ToBitmask(
                    item.RoadShape, Mathf.RoundToInt(instanceRotationY / 90f));
            }
            else
            {
                // ── 중첩 단일 클릭: 사용자 비트 중 연결 가능한 것만 유지 ──
                byte userMask = RoadShapeMapping.ToBitmask(
                    item.RoadShape, Mathf.RoundToInt(instanceRotationY / 90f));
                newMask = FilterMaskByConnectivity(cell, height, userMask);
            }

            // 마스크 → Shape, Rotation
            if (!RoadShapeMapping.TryGetShape(newMask, out RoadShape shape, out newRot90))
            {
                // 마스크 0 (모든 비트 폐쇄) — 도로 제거 시나리오
                if (isOverlap) RemoveRoadAt(cell, height);
                return false;
            }

            // 결과 Shape에 해당하는 RegistryItem 찾기
            var roadItem = FindRoadItemByShape(shape, item.VariantKey);
            if (roadItem == null)
            {
                // 같은 Variant에 없으면 V0으로 폴백
                roadItem = FindRoadItemByShape(shape, 0);
                if (roadItem == null) return false;
            }

            // 중첩이면 기존 도로 제거
            if (isOverlap) RemoveRoadAt(cell, height);

            // 새 도로 등록
            int newIdx = AddRoadPlacement(roadItem, cell, height, newMask, newRot90);
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
            if (isDrag)
                UpdateAdjacentRoads(cell, height);

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

                if (!RoadShapeMapping.TryGetShape(newMask, out RoadShape sh, out int rot)) continue;

                var newItem = FindRoadItemByShape(sh, p.VariantKey)
                           ?? FindRoadItemByShape(sh, 0);
                if (newItem == null) continue;

                // 기존 인스턴스 제거 → 새 모양으로 재생성
                RemoveRoadInstance(adjIdx);

                p.MainKey    = newItem.MainKey;
                p.VariantKey = newItem.VariantKey;
                p.Directions = newMask;
                p.RotationY  = rot * 90f;
                currentMap.Roads[adjIdx] = p;

                InstantiateRoadAt(adjIdx);
            }
        }

        // ── 도로 등록/제거 ─────────────────────────────────────────

        int AddRoadPlacement(RegistryItem roadItem, int2 cell, int height, byte mask, int rot90)
        {
            var placement = new RoadPlacement
            {
                MainKey    = roadItem.MainKey,
                VariantKey = roadItem.VariantKey,
                Cell       = cell,
                Position   = GetRoadPosition(cell, height, roadItem),
                Height     = height,
                Directions = mask,
                RotationY  = rot90 * 90f,
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
            go.transform.rotation   = Quaternion.Euler(0, p.RotationY, 0);
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

        RegistryItem FindRoadItemByShape(RoadShape shape, int variantKey)
        {
            foreach (var reg in registries)
            {
                if (reg == null) continue;
                foreach (var it in reg.Items)
                {
                    if (it.IsDeleted) continue;
                    if (it.RoadShape == shape && it.VariantKey == variantKey)
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
            for (int dx = 0; dx < size.x; dx++)
                for (int dz = 0; dz < size.y; dz++)
                {
                    var c = new int2(origin.x + dx, origin.y + dz);
                    if (occupiedCells.ContainsKey(new OccupancyKey(c, height))) return false;
                }
            return true;
        }

        void MarkArea(int2 origin, Vector2Int size, int height, PlacementKind kind, int index)
        {
            for (int dx = 0; dx < size.x; dx++)
                for (int dz = 0; dz < size.y; dz++)
                {
                    var c = new int2(origin.x + dx, origin.y + dz);
                    occupiedCells[new OccupancyKey(c, height)] =
                        new OccupancyEntry { Kind = kind, Index = index };
                }
        }

        void UnmarkArea(int2 origin, Vector2Int size, int height)
        {
            for (int dx = 0; dx < size.x; dx++)
                for (int dz = 0; dz < size.y; dz++)
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
                    var plane = new Plane(Vector3.up, Vector3.up * previewHeight);
                    if (plane.Raycast(ray, out float t))
                    {
                        var hitPos = ray.GetPoint(t);
                        int2 cell = WorldToCell(hitPos);
                        DeleteCellAt(cell);
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
        void DeleteCellAt(int2 cell)
        {
            int height = GetHeightIndex(previewHeight);
            if (!occupiedCells.TryGetValue(new OccupancyKey(cell, height), out var entry)) return;

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
                currentMap.Singles[i] = p;

                int2 cellOrigin = FootprintOriginFromPosition(p.Position, item.Size);
                int height = GetPlacementHeightIndex(p.Position.y, item);
                MarkArea(cellOrigin, item.Size, height, PlacementKind.Single, i);

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
                p.Position = ResolveMultiPosition(p, item);
                currentMap.Multis[i] = p;

                MarkArea(p.Cell, item.Size, p.Height, PlacementKind.Multi, i);

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
                            sp.BaseSingles[i] = p;

                            int2 cellOrigin = FootprintOriginFromPosition(p.Position, item.Size);
                            // 점유 (Single과 공유 — 통합 점유)
                            int height = GetPlacementHeightIndex(p.Position.y, item);
                            if (IsAreaFree(cellOrigin, item.Size, height))
                                MarkArea(cellOrigin, item.Size, height, PlacementKind.Single, -1);

                            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
                            go.name      = $"SP{sp.Number}_Single_{i}";
                            go.hideFlags = HideFlags.DontSave;
                            go.transform.position   = p.Position;
                            go.transform.rotation   = Quaternion.Euler(0, p.RotationY, 0);
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
                            p.Position = ResolveMultiPosition(p, item);
                            sp.BaseMultis[i] = p;

                            if (IsAreaFree(p.Cell, item.Size, p.Height))
                                MarkArea(p.Cell, item.Size, p.Height, PlacementKind.Multi, -1);

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
                            go.transform.rotation   = Quaternion.Euler(0, p.RotationY, 0);
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
                if (item != null)
                {
                    int2 origin = FootprintOriginFromPosition(p.Position, item.Size);
                    int height = GetPlacementHeightIndex(p.Position.y, item);
                    UnmarkArea(origin, item.Size, height);
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

        Vector3 GetMultiPosition(int2 cell, int height, RegistryItem item)
        {
            float cs = currentMap.Settings.CellSize;
            float y = GameUtility.GetHeightWorldPosition(height, cs, item.Offset.y);
            var size = new int2(Mathf.Max(1, item.Size.x), Mathf.Max(1, item.Size.y));
            return GameUtility.GetFootprintCenterWorldPosition(cell, size, cs, y);
        }

        Vector3 GetSinglePosition(int2 cell, int height, RegistryItem item)
        {
            float cs = currentMap.Settings.CellSize;
            float y = GameUtility.GetHeightWorldPosition(height, cs, item.Offset.y);
            var size = new int2(Mathf.Max(1, item.Size.x), Mathf.Max(1, item.Size.y));
            return GameUtility.GetFootprintCenterWorldPosition(cell, size, cs, y);
        }

        Vector3 GetRoadPosition(int2 cell, int height, RegistryItem item)
        {
            float cs = currentMap.Settings.CellSize;
            float y = GameUtility.GetHeightWorldPosition(height, cs, item.Offset.y);
            var size = new int2(Mathf.Max(1, item.Size.x), Mathf.Max(1, item.Size.y));
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
        void SaveMap()
        {
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
            mapName     = currentMap.MapName;
            mapSavePath = path;

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
            currentMap.RequiredDlcs.Clear();
            currentMap.StartPoints?.Clear();
            ClearScenePlacements();
            occupiedCells.Clear();
            Repaint();
        }
    }
}
