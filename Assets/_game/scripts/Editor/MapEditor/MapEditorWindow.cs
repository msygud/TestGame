using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  MapEditorWindow
    //
    //  맵에디터 메인 창. ECS 무관, GameObject 직접 사용.
    //  메뉴: Tools > CitySim > Map Editor
    //
    //  탭 구조:
    //    [레이어]  — Terrain / Resource 레이어 페인팅
    //    [배치]    — 6카테고리 프리팹 배치
    //  설정은 헤더 하단 접이식 섹션으로 배치.
    //  SceneView에 셀 그리드 + 5단위 눈금 레이블 표시.
    // ══════════════════════════════════════════════════════════════
    public class MapEditorWindow : EditorWindow
    {
        // ── 데이터 ─────────────────────────────────────────────────
        MapData currentMap;
        string mapName = "NewMap";

        List<GamePrefabRegistry> registries = new();

        // ── 레이어 편집 ────────────────────────────────────────────
        BrushSettings brush = new();

        ILayerPainter[] painters;
        int activeLayerIndex = 0;
        bool isPainting = false;

        // ── 탭 ─────────────────────────────────────────────────────
        // 메인 탭: 레이어(Layer) / 배치(Place)
        enum MainTab { Layer, Place }
        // 레이어 서브탭
        enum LayerSub { Terrain, Resource }

        MainTab activeTab = MainTab.Layer;
        LayerSub activeLayer = LayerSub.Terrain;

        // ── 선택 상태 ──────────────────────────────────────────────
        int selectedMainKey = int.MinValue;
        int selectedVariantKey = 0;
        float instanceScale = 1f;

        // ── 랜덤 배치 옵션 ─────────────────────────────────────────
        bool  randomRotation   = false;
        bool  randomOffset     = false;
        float randomOffsetRange = 0.3f;  // 셀 크기 비율 (0~0.5)

        // ── 스타트 포인트 편집 상태 ────────────────────────────────
        bool isPlacingStartPoint = false;
        int editingTeamIndex = -1;

        // ── UI 상태 ────────────────────────────────────────────────
        Vector2 scrollPos;
        bool showBoundary = true;
        bool showThumbnails = true;
        bool showStartPoints = true;
        bool showTerrainLayer = true;  // Terrain 레이어 표시
        bool showResourceLayer = true;  // Resource 레이어 표시
        bool showGrid = true;  // SceneView 그리드
        bool showGridLabels = true;  // 5단위 눈금 레이블

        bool foldSettings = false;   // 헤더 하단 접이식 설정
        bool foldStartPoints = true;
        bool foldVariant = true;
        bool foldInstance = true;
        bool foldRegistries = false;
        bool foldPrefabList = true;
        bool foldDebug = false;

        // 셀 오버레이 메시 (셀 전체 = 단일 Mesh, DrawMeshNow 1회)
        Mesh _overlayMesh;
        Material _overlayMat;
        bool _overlayDirty = true;

        // 그리드 라인 캐시 (매 프레임 new[] 방지)
        Vector3[] _gridPts;
        Vector3[] _gridBoldPts;
        int _gridCacheW = -1, _gridCacheH = -1;
        float _gridCacheCs = -1f;

        Dictionary<(int dlcId, PrefabCategory cat), bool> categoryFolds = new();
        Dictionary<int, bool> dlcFolds = new();

        // ── 배치 점유 캐시 (에디터 전용, 저장 안 함) ───────────────
        // Other 카테고리는 _otherCells로 별도 추적.
        // 나머지(Single non-Other, Road)는 _occupancy로 추적.
        // 비-Other 항목은 _otherCells를 무시하고 배치 가능.
        enum PlacementKind { Single, Road, Other }
        Dictionary<Vector2Int, PlacementKind> _occupancy  = new();
        HashSet<Vector2Int>                   _otherCells = new();

        // ── 에디터 씬 배치 GameObjects ────────────────────────────
        //  셀 원점 → 인스턴스화된 GO. 삭제 / 로드 시 함께 관리.
        Dictionary<Vector2Int, GameObject> _placedObjects      = new();
        Dictionary<Vector2Int, GameObject> _otherPlacedObjects = new();

        // ── 배치 프리뷰 GO ────────────────────────────────────────
        GameObject _previewGo;
        Vector2Int? _previewCell;

        // ── 선택된 배치물 셀 (인스펙터 표시용) ──────────────────
        Vector2Int? _selectedCell = null;

        // ── 맵 저장 경로 ──────────────────────────────────────────
        string _saveFilePath = "";

        // 팀 인덱스별 색상 (0~7)
        static readonly Color[] TeamColors =
        {
            new Color(0.2f, 0.5f, 1.0f),  // 0 파랑
            new Color(1.0f, 0.2f, 0.2f),  // 1 빨강
            new Color(0.2f, 0.8f, 0.2f),  // 2 초록
            new Color(1.0f, 0.8f, 0.0f),  // 3 노랑
            new Color(0.8f, 0.2f, 0.8f),  // 4 보라
            new Color(1.0f, 0.5f, 0.0f),  // 5 주황
            new Color(0.0f, 0.8f, 0.8f),  // 6 청록
            new Color(1.0f, 1.0f, 1.0f),  // 7 흰색
        };

        // ══════════════════════════════════════════════════════════
        //  창 열기
        // ══════════════════════════════════════════════════════════
        [MenuItem("Tools/CitySim/Map Editor")]
        public static void ShowWindow()
        {
            var window = GetWindow<MapEditorWindow>("Map Editor");
            window.minSize = new Vector2(380, 600);
            window.Show();
        }

        void OnEnable()
        {
            if (currentMap == null)
            {
                currentMap = new MapData
                {
                    MapName = mapName,
                    Settings = new MapSettings { CellSize = 2f, Width = 50, Height = 50 }
                };
                currentMap.RebuildDicts();
            }

            InitPainters();
            _overlayMat = CreateOverlayMaterial();
            _overlayMesh = new Mesh { name = "CellOverlay", hideFlags = HideFlags.HideAndDontSave };

            RebuildOccupancy();

            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            isPlacingStartPoint = false;
            isPainting = false;
            ClearPreview();
            ClearAllPlacedObjects();
            if (_overlayMesh != null) { Object.DestroyImmediate(_overlayMesh); _overlayMesh = null; }
            if (_overlayMat != null) { Object.DestroyImmediate(_overlayMat); _overlayMat = null; }
        }

        void InitPainters()
        {
            painters = new ILayerPainter[]
            {
                new TerrainLayerPainter(),
                new ResourceLayerPainter(),
            };
        }

        // ══════════════════════════════════════════════════════════
        //  메인 GUI
        // ══════════════════════════════════════════════════════════
        void OnGUI()
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            DrawHeader();
            DrawSettingsSection();   // 헤더 하단 접이식 설정
            EditorGUILayout.Space(6);

            // 메인 탭: [레이어] [배치]
            var prevTab = activeTab;
            activeTab = (MainTab)GUILayout.Toolbar(
                (int)activeTab,
                new[] { "  레이어  ", "  배치  " },
                GUILayout.Height(30));
            EditorGUILayout.Space(6);

            // 탭 전환 처리
            if (activeTab != prevTab)
            {
                if (activeTab == MainTab.Layer)
                {
                    // 레이어 탭 진입 → 프리팹 선택 + 프리뷰 초기화
                    ClearPreview();
                    selectedMainKey = int.MinValue;
                    selectedVariantKey = 0;
                }
                else
                {
                    // 배치 탭 진입 → 레이어 페인팅 종료
                    isPainting = false;
                }
                SceneView.RepaintAll();
            }

            // ── 레이어 가시성 토글 (탭과 무관하게 항상 표시) ───
            DrawLayerVisibilityBar();
            EditorGUILayout.Space(4);

            switch (activeTab)
            {
                case MainTab.Layer: DrawLayerTab(); break;
                case MainTab.Place: DrawPlaceTab(); break;
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Map Editor", EditorStyles.boldLabel);
            // 그리드 토글 버튼 (우측)
            var gridLabel = showGrid ? "Grid ON" : "Grid OFF";
            if (GUILayout.Button(gridLabel, EditorStyles.miniButton, GUILayout.Width(64)))
            {
                showGrid = !showGrid;
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            mapName = EditorGUILayout.TextField("Map Name", mapName);
            if (currentMap != null) currentMap.MapName = mapName;
        }

        // ══════════════════════════════════════════════════════════
        //  Settings 섹션 (헤더 하단 접이식)
        //  탭에서 분리하여 항상 접근 가능하도록.
        // ══════════════════════════════════════════════════════════
        void DrawSettingsSection()
        {
            foldSettings = EditorGUILayout.Foldout(
                foldSettings, "⚙ 맵 설정 / 스타트포인트", true, EditorStyles.foldoutHeader);
            if (!foldSettings) return;

            EditorGUI.indentLevel++;
            DrawMapSettings();
            EditorGUILayout.Space(4);
            DrawStartPoints();
            EditorGUILayout.Space(4);
            DrawDebugSection();
            EditorGUI.indentLevel--;
        }

        // ── 레이어 가시성 토글 바 (탭 상단 고정) ─────────────────────
        void DrawLayerVisibilityBar()
        {
            var prevBg = GUI.backgroundColor;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("표시:", GUILayout.Width(34));

            // Terrain 토글
            GUI.backgroundColor = showTerrainLayer
                ? new Color(0.45f, 0.32f, 0.16f, 1f)   // 갈색
                : new Color(0.3f, 0.3f, 0.3f, 1f);
            if (GUILayout.Button("▣ 지형", GUILayout.Width(68), GUILayout.Height(22)))
            {
                showTerrainLayer = !showTerrainLayer;
                _overlayDirty = true;
                SceneView.RepaintAll();
            }

            // Resource 토글
            GUI.backgroundColor = showResourceLayer
                ? new Color(0.20f, 0.55f, 0.35f, 1f)   // 초록
                : new Color(0.3f, 0.3f, 0.3f, 1f);
            if (GUILayout.Button("▣ 자원", GUILayout.Width(68), GUILayout.Height(22)))
            {
                showResourceLayer = !showResourceLayer;
                _overlayDirty = true;
                SceneView.RepaintAll();
            }

            GUI.backgroundColor = prevBg;
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════
        //  레이어 탭 — Terrain / Resource 서브탭 포함
        //  이 탭 진입 시 프리팹 선택은 이미 초기화된 상태 (OnGUI에서 처리).
        // ══════════════════════════════════════════════════════════
        void DrawLayerTab()
        {
            // 서브탭 (Terrain / Resource)
            var prevSub = activeLayer;
            activeLayer = (LayerSub)GUILayout.Toolbar(
                (int)activeLayer,
                new[] { "Terrain", "Resource" },
                GUILayout.Height(24));

            if (activeLayer != prevSub)
            {
                // 서브탭 전환 시 페인팅 중이면 종료
                if (isPainting) { isPainting = false; SceneView.RepaintAll(); }
            }

            EditorGUILayout.Space(6);

            int painterIndex = (int)activeLayer;
            if (painters == null || painterIndex >= painters.Length) return;
            var painter = painters[painterIndex];

            // 브러시 설정 (공통)
            EditorGUILayout.LabelField("브러시 설정", EditorStyles.miniBoldLabel);
            brush.DrawToolbar();

            EditorGUILayout.Space(6);

            // 레이어별 타입 선택 툴바
            EditorGUILayout.LabelField(painter.LayerName + " 설정", EditorStyles.miniBoldLabel);
            painter.DrawToolbar();

            EditorGUILayout.Space(6);

            // 페인팅 모드 안내
            if (isPainting)
            {
                EditorGUILayout.HelpBox(
                    "Scene View에서 드래그하여 페인팅. ESC로 종료.",
                    MessageType.Info);
                if (GUILayout.Button("페인팅 종료"))
                {
                    isPainting = false;
                    SceneView.RepaintAll();
                }
            }
            else
            {
                if (GUILayout.Button("Scene View에서 페인팅 시작", GUILayout.Height(30)))
                {
                    isPainting = true;
                    activeLayerIndex = painterIndex;
                    SceneView.RepaintAll();
                    // 버튼 클릭 직후 SceneView 로 포커스 이동
                    // (EditorWindow 에 포커스가 있으면 SceneView 가 이벤트를 못 받음)
                    if (SceneView.lastActiveSceneView != null)
                        SceneView.lastActiveSceneView.Focus();
                }
            }

            EditorGUILayout.Space(4);

            // 전체 지우기
            if (GUILayout.Button($"{painter.LayerName} 전체 지우기"))
            {
                if (EditorUtility.DisplayDialog(
                    "확인",
                    $"{painter.LayerName} 레이어를 모두 지우겠습니까?",
                    "확인", "취소"))
                {
                    if (painterIndex == 0) currentMap.TerrainDict.Clear();
                    else currentMap.ResourceDict.Clear();
                    SceneView.RepaintAll();
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        //  배치 탭 — 6카테고리 프리팹 배치
        //  이 탭 진입 시 레이어 페인팅은 이미 종료된 상태 (OnGUI에서 처리).
        // ══════════════════════════════════════════════════════════
        void DrawPlaceTab()
        {
            DrawSaveLoad();
            EditorGUILayout.Space(6);
            DrawVariantSelector();
            EditorGUILayout.Space(8);
            DrawInstanceSettings();
            EditorGUILayout.Space(6);
            DrawSelectedPlacementInfo();
            EditorGUILayout.Space(10);
            DrawRegistries();
            EditorGUILayout.Space(10);
            DrawPrefabList();
        }

        // ══════════════════════════════════════════════════════════
        //  Map Settings
        // ══════════════════════════════════════════════════════════
        void DrawMapSettings()
        {
            EditorGUILayout.LabelField("Map Settings", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            var s = currentMap.Settings;
            float newCs = EditorGUILayout.FloatField("Cell Size (units)", s.CellSize);
            int newW = EditorGUILayout.IntField("Width (cells)", s.Width);
            int newH = EditorGUILayout.IntField("Height (cells)", s.Height);

            newCs = Mathf.Max(0.1f, newCs);
            newW = Mathf.Max(1, newW);
            newH = Mathf.Max(1, newH);

            if (newCs != s.CellSize || newW != s.Width || newH != s.Height)
            {
                s.CellSize = newCs; s.Width = newW; s.Height = newH;
                currentMap.Settings = s;
                SceneView.RepaintAll();
            }

            EditorGUILayout.LabelField(
                $"Total: {s.Width * s.Height} cells, " +
                $"{s.Width * s.CellSize} × {s.Height * s.CellSize} units",
                EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Start Points
        //
        //  팩션 배정 없음 — 위치(Cell) + 팀 인덱스(TeamIndex)만.
        //  팩션은 로비에서 결정.
        // ══════════════════════════════════════════════════════════
        void DrawStartPoints()
        {
            foldStartPoints = EditorGUILayout.Foldout(
                foldStartPoints, "Start Points", true, EditorStyles.foldoutHeader);
            if (!foldStartPoints) return;

            EditorGUI.indentLevel++;

            var pts = currentMap.StartPoints;

            // 배치 모드 안내
            if (isPlacingStartPoint)
            {
                EditorGUILayout.HelpBox(
                    $"Scene View를 클릭해 팀 {editingTeamIndex}번 시작 위치를 지정하세요.\n" +
                    "ESC로 취소.",
                    MessageType.Info);

                if (GUILayout.Button("취소"))
                {
                    isPlacingStartPoint = false;
                    editingTeamIndex = -1;
                    SceneView.RepaintAll();
                }
            }

            // 스타트 포인트 목록
            for (int i = 0; i < pts.Count; i++)
            {
                var pt = pts[i];
                EditorGUILayout.BeginHorizontal();

                // 팀 색상 아이콘
                var iconRect = GUILayoutUtility.GetRect(16, 16,
                    GUILayout.Width(16), GUILayout.Height(16));
                EditorGUI.DrawRect(iconRect, TeamColors[pt.TeamIndex % TeamColors.Length]);

                EditorGUILayout.LabelField(
                    $"Team {pt.TeamIndex}",
                    GUILayout.Width(60));

                EditorGUILayout.LabelField(
                    $"Cell ({pt.Cell.x}, {pt.Cell.y})",
                    GUILayout.Width(110));

                // 위치 재지정
                if (GUILayout.Button("재지정", GUILayout.Width(50)))
                {
                    isPlacingStartPoint = true;
                    editingTeamIndex = pt.TeamIndex;
                    SceneView.RepaintAll();
                }

                // 삭제
                if (GUILayout.Button("X", GUILayout.Width(24)))
                {
                    pts.RemoveAt(i);
                    SceneView.RepaintAll();
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.indentLevel--;
                    return;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);

            // 새 스타트 포인트 추가 (최대 8개)
            if (pts.Count < 8)
            {
                if (GUILayout.Button($"+ Add Start Point (Team {pts.Count})"))
                {
                    pts.Add(new StartPointData
                    {
                        TeamIndex = pts.Count,
                        Cell = Vector2Int.zero,
                    });
                    isPlacingStartPoint = true;
                    editingTeamIndex = pts.Count - 1;
                    SceneView.RepaintAll();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("최대 8개 팀까지 지원합니다.", MessageType.None);
            }

            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  Variant Selector
        // ══════════════════════════════════════════════════════════
        void DrawVariantSelector()
        {
            foldVariant = EditorGUILayout.Foldout(
                foldVariant, "Variant Selector", true, EditorStyles.foldoutHeader);
            if (!foldVariant) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (selectedMainKey == int.MinValue)
            {
                EditorGUILayout.LabelField(" ", GUILayout.Height(70));
                EditorGUILayout.EndVertical();
                return;
            }

            var variants = new List<(RegistryItem item, GamePrefabRegistry source)>();
            foreach (var reg in registries)
            {
                if (reg == null) continue;
                foreach (var it in reg.Items)
                {
                    if (it.IsDeleted) continue;
                    if (it.MainKey != selectedMainKey) continue;
                    variants.Add((it, reg));
                }
            }
            variants.Sort((a, b) => a.item.VariantKey.CompareTo(b.item.VariantKey));

            EditorGUILayout.BeginHorizontal();
            foreach (var (item, source) in variants)
                DrawVariantTile(item, source);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
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
            if (preview != null)
            {
                var imgRect = new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4);
                GUI.DrawTexture(imgRect, preview, ScaleMode.ScaleToFit);
            }

            string srcName = source != null ? source.DlcName : "?";
            var dlcRect = new Rect(rect.x + 2, rect.yMax - 14, rect.width - 4, 12);
            EditorGUI.DrawRect(dlcRect, new Color(0, 0, 0, 0.6f));
            GUI.Label(dlcRect, " " + srcName, new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.LowerLeft,
                normal = { textColor = Color.white },
                fontSize = 9,
            });

            GUI.backgroundColor = prevBg;

            EditorGUILayout.LabelField($"V{item.VariantKey}",
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
            instanceScale = EditorGUILayout.FloatField("Scale (uniform)", instanceScale);
            if (GUILayout.Button("Reset", GUILayout.Width(60))) instanceScale = 1f;
            EditorGUILayout.EndHorizontal();
            instanceScale = Mathf.Max(0.01f, instanceScale);
            EditorGUILayout.LabelField(
                "Shortcuts in Scene: + / - / 0 (with mouse over Scene View)",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4);

            // 랜덤 회전
            randomRotation = EditorGUILayout.Toggle(
                new GUIContent("Random Rotation", "배치 시 Y축 0~360° 완전 무작위 회전"),
                randomRotation);

            // 랜덤 XZ 오프셋
            EditorGUILayout.BeginHorizontal();
            randomOffset = EditorGUILayout.Toggle(
                new GUIContent("Random XZ Offset", "배치 시 셀 중심에서 무작위 XZ 이동"),
                randomOffset);
            EditorGUI.BeginDisabledGroup(!randomOffset);
            randomOffsetRange = EditorGUILayout.Slider(randomOffsetRange, 0f, 0.5f);
            EditorGUILayout.LabelField("× CellSize", EditorStyles.miniLabel, GUILayout.Width(64));
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

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
                    Repaint();
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.indentLevel--;
                    return;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Registry"))
                registries.Add(null);

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
                EditorGUILayout.HelpBox(
                    "No registries. Add GamePrefabRegistry SOs above.",
                    MessageType.Info);
                return;
            }

            showThumbnails = EditorGUILayout.Toggle("Show Thumbnails", showThumbnails);

            var byDlc = new SortedDictionary<int, List<(RegistryItem item, GamePrefabRegistry src)>>();
            foreach (var reg in validRegs)
            {
                foreach (var it in reg.Items)
                {
                    if (it.IsDeleted) continue;
                    if (it.VariantKey != 0) continue;
                    if ((it.Usage & PrefabUsage.MapEditor) == 0) continue;

                    if (!byDlc.TryGetValue(reg.DlcId, out var l))
                        byDlc[reg.DlcId] = l = new List<(RegistryItem, GamePrefabRegistry)>();
                    l.Add((it, reg));
                }
            }

            if (byDlc.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No items with VariantKey 0 + MapEditor usage.",
                    MessageType.Info);
                return;
            }

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
                    byCat[pair.item.Category] = l = new List<(RegistryItem, GamePrefabRegistry)>();
                l.Add(pair);
            }

            foreach (var kv in byCat)
            {
                var key = (dlcId, kv.Key);
                if (!categoryFolds.TryGetValue(key, out bool open)) open = true;
                open = EditorGUILayout.Foldout(open, $"{kv.Key} ({kv.Value.Count})", true);
                categoryFolds[key] = open;
                if (!open) continue;

                DrawPrefabRow(kv.Value);
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
                else { selectedMainKey = item.MainKey; selectedVariantKey = 0; }
                Repaint();
            }

            Texture2D preview = showThumbnails && item.Prefab != null
                ? AssetPreview.GetAssetPreview(item.Prefab) : null;

            if (preview != null)
                GUI.DrawTexture(
                    new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4),
                    preview, ScaleMode.ScaleToFit);
            else
                GUI.Label(rect, $"M{item.MainKey}", new GUIStyle(EditorStyles.miniBoldLabel)
                { alignment = TextAnchor.MiddleCenter });

            var dlcRect = new Rect(rect.x + 2, rect.yMax - 14, rect.width - 4, 12);
            EditorGUI.DrawRect(dlcRect, new Color(0, 0, 0, 0.6f));
            GUI.Label(dlcRect, " " + (source != null ? source.DlcName : "?"),
                new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.LowerLeft,
                    normal = { textColor = Color.white },
                    fontSize = 9,
                });

            GUI.backgroundColor = prevBg;

            EditorGUILayout.LabelField($"M{item.MainKey}",
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
                    $"Selected: Main {selectedMainKey}, Variant {selectedVariantKey}, " +
                    $"Scale {instanceScale:0.00}",
                    EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════
        //  Debug
        // ══════════════════════════════════════════════════════════
        void DrawDebugSection()
        {
            foldDebug = EditorGUILayout.Foldout(
                foldDebug, "Debug", true, EditorStyles.foldoutHeader);
            if (!foldDebug) return;

            EditorGUI.indentLevel++;

            bool newGrid = EditorGUILayout.Toggle("Show Grid", showGrid);
            if (newGrid != showGrid) { showGrid = newGrid; SceneView.RepaintAll(); }

            if (showGrid)
            {
                EditorGUI.indentLevel++;
                bool newGL = EditorGUILayout.Toggle("Grid Labels (5단위)", showGridLabels);
                if (newGL != showGridLabels) { showGridLabels = newGL; SceneView.RepaintAll(); }
                EditorGUI.indentLevel--;
            }

            bool newShow = EditorGUILayout.Toggle("Show Boundary in Scene", showBoundary);
            if (newShow != showBoundary) { showBoundary = newShow; SceneView.RepaintAll(); }

            bool newSP = EditorGUILayout.Toggle("Show Start Points in Scene", showStartPoints);
            if (newSP != showStartPoints) { showStartPoints = newSP; SceneView.RepaintAll(); }

            bool newTL = EditorGUILayout.Toggle("Terrain 레이어 표시", showTerrainLayer);
            if (newTL != showTerrainLayer) { showTerrainLayer = newTL; _overlayDirty = true; SceneView.RepaintAll(); }
            bool newRL = EditorGUILayout.Toggle("Resource 레이어 표시", showResourceLayer);
            if (newRL != showResourceLayer) { showResourceLayer = newRL; _overlayDirty = true; SceneView.RepaintAll(); }

            EditorGUI.indentLevel--;
        }

        // ══════════════════════════════════════════════════════════
        //  SceneView
        // ══════════════════════════════════════════════════════════
        void OnSceneGUI(SceneView sceneView)
        {
            HandleScaleShortcuts();
            HandleStartPointPlacement(sceneView);
            HandleLayerPainting(sceneView);
            HandlePlacementInput(sceneView);

            if (currentMap == null) return;

            // ── 모든 Handles 드로잉은 Repaint 이벤트에서만 ────────
            //  다른 이벤트(Layout, MouseMove 등)에서 드로잉하면 깜빡임 유발
            if (Event.current.type != EventType.Repaint) return;

            // 셀 오버레이 메시 (dirty 시에만 재빌드 — DrawMeshNow 1회)
            if ((showTerrainLayer || showResourceLayer) && painters != null && painters.Length >= 2)
            {
                if (_overlayDirty) { RebuildOverlayMesh(); _overlayDirty = false; }
                DrawOverlayMesh();
            }

            // 배치 탭 오버레이 (배치된 오브젝트 + 마우스 하이라이트)
            DrawPlacementOverlay(sceneView);

            // 그리드 (Handles.DrawLines 배치 — 드로우콜 2개)
            if (showGrid) DrawGridHandles(currentMap.Settings);

            // 경계선 / 스타트포인트
            if (showBoundary) MapBoundaryGizmo.Draw(currentMap.Settings);
            if (showStartPoints) DrawStartPointGizmos();

            // 브러시 미리보기 / 높이 라벨
            bool activeLayerVisible = activeLayerIndex == 0 ? showTerrainLayer : showResourceLayer;
            if (activeLayerVisible && isPainting)
                DrawBrushPreview();
            if (activeLayerVisible && painters != null &&
                activeLayerIndex < painters.Length &&
                painters[activeLayerIndex].WantsHeightOverlay)
                DrawHeightOverlay(painters[activeLayerIndex]);

            // 5단위 눈금 레이블
            if (showGrid && showGridLabels)
                DrawGridLabels(currentMap.Settings);
        }

        // ════════════════════════════════════════════════════════
        //  그리드 + 셀 오버레이 렌더링
        //
        //  셀 오버레이: 모든 셀 = 단일 Mesh → Graphics.DrawMeshNow 1회
        //    · 드로우콜 1개, 셀 수와 무관하게 일정한 부하
        //    · dirty 플래그로 페인팅 시에만 메시 재빌드
        //  그리드  : Handles.DrawLines 배치 (드로우콜 2개)
        // ════════════════════════════════════════════════════════

        // ── 오버레이 머티리얼 생성 ──────────────────────────────
        static Material CreateOverlayMaterial()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            return mat;
        }

        // ── 오버레이 메시 재빌드 (dirty 시 1회) ─────────────────
        //  Terrain + Resource 셀을 하나의 Mesh 로 합침.
        //  셀 1개 = 쿼드 2삼각형. 버텍스 컬러로 색상 표현.
        void RebuildOverlayMesh()
        {
            if (currentMap == null || _overlayMesh == null) return;
            if (_overlayMat == null) _overlayMat = CreateOverlayMaterial();

            float cs = currentMap.Settings.CellSize;
            const float yT = 0.010f;   // Terrain 높이
            const float yR = 0.015f;   // Resource 높이 (위에 겹침)

            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();

            void AddQuad(Vector2Int cell, Color col, float y)
            {
                float x0 = cell.x * cs, x1 = x0 + cs;
                float z0 = cell.y * cs, z1 = z0 + cs;
                int b = verts.Count;
                verts.Add(new Vector3(x0, y, z0));
                verts.Add(new Vector3(x1, y, z0));
                verts.Add(new Vector3(x1, y, z1));
                verts.Add(new Vector3(x0, y, z1));
                cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }

            // Terrain (showTerrainLayer 시에만)
            if (showTerrainLayer)
                foreach (var kv in currentMap.TerrainDict)
                    if (painters[0].TryGetCellColor(kv.Key, currentMap, out var col))
                        AddQuad(kv.Key, col, yT);

            // Resource (showResourceLayer 시에만, 반투명으로 위에 겹침)
            if (showResourceLayer)
                foreach (var kv in currentMap.ResourceDict)
                    if (painters[1].TryGetCellColor(kv.Key, currentMap, out var col))
                        AddQuad(kv.Key, new Color(col.r, col.g, col.b, 0.65f), yR);

            _overlayMesh.Clear();
            if (verts.Count == 0) return;

            // 셀 수가 많으면 16비트 인덱스(65535) 초과 → 32비트로 강제
            _overlayMesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            _overlayMesh.SetVertices(verts);
            _overlayMesh.SetColors(cols);
            _overlayMesh.SetTriangles(tris, 0);
            _overlayMesh.RecalculateBounds();
        }

        // ── 오버레이 메시 렌더 (Repaint 1회 = DrawMeshNow 1회) ──
        void DrawOverlayMesh()
        {
            if (_overlayMesh == null || _overlayMesh.vertexCount == 0) return;
            if (_overlayMat == null) return;
            _overlayMat.SetPass(0);
            Graphics.DrawMeshNow(_overlayMesh, Matrix4x4.identity);
        }

        // ── 그리드: Handles.DrawLines 배치 (드로우콜 2개) ───────
        //  큰 맵에서 셀 단위 선은 너무 빽빽해 의미 없으므로
        //  셀 크기가 씬뷰에서 3픽셀 미만이면 셀 선 스킵, 5단위 선만 표시.
        void DrawGridHandles(MapSettings s)
        {
            float cs = s.CellSize;
            int w = s.Width;
            int h = s.Height;

            // 그리드 라인 캐시: w/h/cs가 바뀔 때만 재계산
            if (_gridCacheW != w || _gridCacheH != h || _gridCacheCs != cs)
            {
                _gridCacheW = w; _gridCacheH = h; _gridCacheCs = cs;

                // 일반 셀 선
                _gridPts = new Vector3[(w + 1 + h + 1) * 2];
                int idx = 0;
                for (int x = 0; x <= w; x++)
                { _gridPts[idx++] = new Vector3(x * cs, 0f, 0f); _gridPts[idx++] = new Vector3(x * cs, 0f, h * cs); }
                for (int z = 0; z <= h; z++)
                { _gridPts[idx++] = new Vector3(0f, 0f, z * cs); _gridPts[idx++] = new Vector3(w * cs, 0f, z * cs); }

                // 5단위 강조 선
                int bw = w / 5 + 1, bh = h / 5 + 1;
                _gridBoldPts = new Vector3[(bw + bh) * 2];
                idx = 0;
                for (int x = 0; x <= w; x += 5)
                { _gridBoldPts[idx++] = new Vector3(x * cs, 0f, 0f); _gridBoldPts[idx++] = new Vector3(x * cs, 0f, h * cs); }
                for (int z = 0; z <= h; z += 5)
                { _gridBoldPts[idx++] = new Vector3(0f, 0f, z * cs); _gridBoldPts[idx++] = new Vector3(w * cs, 0f, z * cs); }
            }

            Handles.color = new Color(1f, 1f, 1f, 0.10f);
            Handles.DrawLines(_gridPts);

            Handles.color = new Color(1f, 1f, 1f, 0.28f);
            Handles.DrawLines(_gridBoldPts);
        }

        // ── 5단위 눈금 레이블 ────────────────────────────────────
        void DrawGridLabels(MapSettings s)
        {
            float cs = s.CellSize;
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(1f, 1f, 0.4f, 0.85f) },
                fontSize = 9,
                alignment = TextAnchor.UpperLeft,
            };
            for (int x = 5; x <= s.Width; x += 5)
                Handles.Label(new Vector3(x * cs, 0f, -cs * 0.35f), x.ToString(), style);
            for (int z = 5; z <= s.Height; z += 5)
                Handles.Label(new Vector3(-cs * 0.35f, 0f, z * cs), z.ToString(), style);
        }

        // ── 브러시 미리보기 ──────────────────────────────────────
        void DrawBrushPreview()
        {
            if (!isPainting || currentMap == null || Event.current == null) return;
            var sv = SceneView.currentDrawingSceneView;
            if (sv == null || _overlayMat == null) return;

            var s = currentMap.Settings;
            var center = WorldToCell(
                GetWorldPositionFromMouse(Event.current.mousePosition, sv), s.CellSize);
            if (center == null) return;
            var cells = new List<Vector2Int>(brush.GetCells(center.Value, s));
            if (cells.Count == 0) return;

            // 브러시 셀을 임시 메시로 렌더 (DrawMeshNow)
            float cs = s.CellSize;
            var verts = new Vector3[cells.Count * 4];
            var tris = new int[cells.Count * 6];
            var cols = new Color[cells.Count * 4];
            var brushCol = new Color(1f, 1f, 0f, 0.30f);
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                float x0 = c.x * cs, x1 = x0 + cs, z0 = c.y * cs, z1 = z0 + cs;
                int b = i * 4;
                verts[b] = new Vector3(x0, 0.02f, z0);
                verts[b + 1] = new Vector3(x1, 0.02f, z0);
                verts[b + 2] = new Vector3(x1, 0.02f, z1);
                verts[b + 3] = new Vector3(x0, 0.02f, z1);
                cols[b] = cols[b + 1] = cols[b + 2] = cols[b + 3] = brushCol;
                int t = i * 6;
                tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 2;
                tris[t + 3] = b; tris[t + 4] = b + 2; tris[t + 5] = b + 3;
            }
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            _overlayMat.SetPass(0);
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            Object.DestroyImmediate(mesh);

            // 테두리만 Handles (브러시 크기 * 4선)
            Handles.color = Color.yellow;
            foreach (var c in cells)
            {
                float x0 = c.x * cs, x1 = x0 + cs, z0 = c.y * cs, z1 = z0 + cs, y = 0.02f;
                Handles.DrawLine(new Vector3(x0, y, z0), new Vector3(x1, y, z0));
                Handles.DrawLine(new Vector3(x1, y, z0), new Vector3(x1, y, z1));
                Handles.DrawLine(new Vector3(x1, y, z1), new Vector3(x0, y, z1));
                Handles.DrawLine(new Vector3(x0, y, z1), new Vector3(x0, y, z0));
            }
        }


        // ── 높이 숫자 오버레이 ───────────────────────────────────
        void DrawHeightOverlay(ILayerPainter painter)
        {
            if (currentMap == null) return;
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 1f, 0.3f, 1f) },
            };
            float cs = currentMap.Settings.CellSize;
            foreach (var kv in currentMap.TerrainDict)
            {
                if (!painter.TryGetHeightLabel(kv.Key, currentMap, out var label)) continue;
                Handles.Label(
                    new Vector3((kv.Key.x + 0.5f) * cs, 0.02f, (kv.Key.y + 0.5f) * cs),
                    label, style);
            }
        }

        // ── 레이어 페인팅 SceneView 핸들러 ─────────────────────────
        //  Layout 이벤트에서 AddDefaultControl → 선택 도구 차단
        //  MouseDown/Drag 에서 e.Use() → 드래그 박스 차단
        void HandleLayerPainting(SceneView sceneView)
        {
            if (!isPainting || painters == null || currentMap == null) return;
            if (activeLayerIndex >= painters.Length) return;

            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(
                    GUIUtility.GetControlID(FocusType.Passive));
                return;
            }

            var painter = painters[activeLayerIndex];

            // ESC: 종료
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                isPainting = false;
                e.Use();
                Repaint();
                return;
            }

            // Ctrl + 스크롤: 높이 ±1
            if (e.type == EventType.ScrollWheel && e.control)
            {
                var cell = WorldToCell(
                    GetWorldPositionFromMouse(e.mousePosition, sceneView),
                    currentMap.Settings.CellSize);
                if (cell.HasValue && painter.HandleScroll(cell.Value, currentMap, -e.delta.y))
                { sceneView.Repaint(); Repaint(); }
                e.Use();
                return;
            }

            // 좌클릭 / 드래그: 페인트 또는 지우기
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
                && e.button == 0)
            {
                var cell = WorldToCell(
                    GetWorldPositionFromMouse(e.mousePosition, sceneView),
                    currentMap.Settings.CellSize);
                if (cell == null) { e.Use(); return; }

                if (e.shift || brush.Mode == BrushMode.Erase)
                {
                    foreach (var c in brush.GetCells(cell.Value, currentMap.Settings))
                        painter.Erase(c, currentMap);
                }
                else
                {
                    painter.Paint(cell.Value, currentMap, brush);
                }

                _overlayDirty = true;

                e.Use();
                sceneView.Repaint();
                Repaint();
            }
        }

        // ── 스타트 포인트 SceneView 배치 ────────────────────────────
        void HandleStartPointPlacement(SceneView sceneView)
        {
            if (!isPlacingStartPoint) return;

            var e = Event.current;

            // ESC 취소
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                isPlacingStartPoint = false;
                editingTeamIndex = -1;
                e.Use();
                Repaint();
                return;
            }

            // 마우스 클릭으로 셀 지정
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                var cell = WorldToCell(
                    GetWorldPositionFromMouse(e.mousePosition, sceneView),
                    currentMap.Settings.CellSize);
                if (cell == null) { e.Use(); return; }

                // 기존 항목 갱신 또는 새로 추가
                var pts = currentMap.StartPoints;
                bool updated = false;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (pts[i].TeamIndex == editingTeamIndex)
                    {
                        pts[i] = new StartPointData
                        {
                            TeamIndex = editingTeamIndex,
                            Cell = new Vector2Int(cell.Value.x, cell.Value.y),
                        };
                        updated = true;
                        break;
                    }
                }
                if (!updated)
                {
                    pts.Add(new StartPointData
                    {
                        TeamIndex = editingTeamIndex,
                        Cell = new Vector2Int(cell.Value.x, cell.Value.y),
                    });
                }

                isPlacingStartPoint = false;
                editingTeamIndex = -1;
                e.Use();
                Repaint();
                SceneView.RepaintAll();
            }

            // 배치 중 커서 위치 미리보기
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
                SceneView.RepaintAll();

            // 클릭 이벤트가 다른 오브젝트에 먹히지 않도록
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        }

        // ── 스타트 포인트 기즈모 ────────────────────────────────────
        void DrawStartPointGizmos()
        {
            var s = currentMap.Settings;
            var pts = currentMap.StartPoints;

            foreach (var pt in pts)
            {
                var color = TeamColors[pt.TeamIndex % TeamColors.Length];
                var center = new Vector3(
                    (pt.Cell.x + 0.5f) * s.CellSize,
                    0f,
                    (pt.Cell.y + 0.5f) * s.CellSize);

                // 셀 사각형
                Handles.color = new Color(color.r, color.g, color.b, 0.3f);
                Handles.DrawSolidDisc(center, Vector3.up, s.CellSize * 0.45f);

                // 테두리
                Handles.color = color;
                Handles.DrawWireDisc(center, Vector3.up, s.CellSize * 0.45f);

                // 팀 번호 라벨
                var style = new GUIStyle
                {
                    normal = { textColor = color },
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                Handles.Label(center + Vector3.up * 0.1f, pt.TeamIndex.ToString(), style);
            }

            // 배치 중 미리보기
            if (isPlacingStartPoint && Event.current != null)
            {
                var cell = WorldToCell(
                    GetWorldPositionFromMouse(
                        Event.current.mousePosition, SceneView.currentDrawingSceneView),
                    s.CellSize);
                if (!cell.HasValue) return;
                var center = new Vector3(
                    (cell.Value.x + 0.5f) * s.CellSize,
                    0f,
                    (cell.Value.y + 0.5f) * s.CellSize);

                var color = TeamColors[editingTeamIndex % TeamColors.Length];
                Handles.color = new Color(color.r, color.g, color.b, 0.5f);
                Handles.DrawSolidDisc(center, Vector3.up, s.CellSize * 0.45f);
                Handles.color = color;
                Handles.DrawWireDisc(center, Vector3.up, s.CellSize * 0.45f);
            }
        }

        // ── 유틸리티 ────────────────────────────────────────────────
        void HandleScaleShortcuts()
        {
            var e = Event.current;
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
            { instanceScale = 1f; changed = true; }

            if (changed) { e.Use(); Repaint(); }
        }

        static Vector3? GetWorldPositionFromMouse(Vector2 mousePos, SceneView sceneView)
        {
            var ray = HandleUtility.GUIPointToWorldRay(mousePos);
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float dist))
                return ray.GetPoint(dist);
            return null;
        }

        static Vector2Int? WorldToCell(Vector3? worldPos, float cellSize)
        {
            if (worldPos == null) return null;
            return new Vector2Int(
                Mathf.FloorToInt(worldPos.Value.x / cellSize),
                Mathf.FloorToInt(worldPos.Value.z / cellSize));
        }

        // ══════════════════════════════════════════════════════════
        //  배치 시스템
        //
        //  HandlePlacementInput      - SceneView 입력 처리
        //  TryPlace                  - 셀 배치 + GO Instantiate
        //  TryDeleteAtCell           - 배치 삭제 + GO DestroyImmediate
        //  RebuildOccupancy          - 점유 캐시 재빌드
        //  ReloadAllPlacedObjects    - MapData 씬 GO 전체 재생성
        //  ClearAllPlacedObjects     - 씬 GO 전체 제거
        //  FindItem                  - 레지스트리 조회
        //  DrawSaveLoad              - 저장/로드 UI
        //  DrawSelectedPlacementInfo - 읽기 전용 인스펙터
        //  DrawPlacementOverlay      - SceneView 오버레이
        //
        //  조작 (배치 탭):
        //    좌클릭              - 프리팹 선택 중이면 배치, 아니면 배치물 선택
        //    우클릭 / Delete     - 마우스 위치 배치물 삭제
        //    Ctrl + 스크롤       - 마우스 위치 배치물 Y 높이 조절 (단위: CellSize*0.5)
        //    ESC                 - 프리팹 선택 해제
        // ══════════════════════════════════════════════════════════

        // ── 프리뷰 GO 관리 ──────────────────────────────────────
        void UpdatePreview(Vector2Int? cell)
        {
            if (selectedMainKey == int.MinValue || currentMap == null)
            { ClearPreview(); return; }

            var item = FindItem(selectedMainKey, selectedVariantKey);
            if (item == null || item.Prefab == null)
            { ClearPreview(); return; }

            if (cell == null)
            { ClearPreview(); return; }

            // 같은 셀이면 GO 유지, 스케일만 갱신
            if (_previewCell == cell && _previewGo != null)
            {
                _previewGo.transform.localScale = Vector3.one * (instanceScale > 0f ? instanceScale : 1f);
                return;
            }

            ClearPreview();
            _previewCell = cell;

            float cs = currentMap.Settings.CellSize;
            var size = ValidSize(item);
            var pos = new Vector3(
                (cell.Value.x + size.x * 0.5f) * cs + item.Offset.x,
                item.Offset.y,
                (cell.Value.y + size.y * 0.5f) * cs + item.Offset.z);

            _previewGo = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
            _previewGo.transform.SetPositionAndRotation(pos, Quaternion.identity);
            _previewGo.transform.localScale = Vector3.one * (instanceScale > 0f ? instanceScale : 1f);
            _previewGo.hideFlags = HideFlags.HideAndDontSave;

            // 반투명 적용 (MaterialPropertyBlock — URP _BaseColor alpha)
            foreach (var r in _previewGo.GetComponentsInChildren<Renderer>(true))
            {
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.45f));
                r.SetPropertyBlock(mpb);
            }
        }

        void ClearPreview()
        {
            if (_previewGo != null) { Object.DestroyImmediate(_previewGo); _previewGo = null; }
            _previewCell = null;
        }

        // ── SceneView 입력 핸들러 ────────────────────────────────
        void HandlePlacementInput(SceneView sceneView)
        {
            if (activeTab != MainTab.Place) return;
            if (currentMap == null) return;

            var e = Event.current;
            if (e == null) return;

            // Layout: 항상 컨트롤 등록 (배치 탭에서는 드래그 선택 차단)
            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                return;
            }

            // ESC: 프리팹 선택 해제 + 배치물 선택 해제
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                ClearPreview();
                selectedMainKey = int.MinValue;
                selectedVariantKey = 0;
                _selectedCell = null;
                e.Use();
                Repaint();
                return;
            }

            // 마우스 이동: 프리뷰 + 미리보기 갱신
            if (e.type == EventType.MouseMove)
            {
                var hoverCell = WorldToCell(
                    GetWorldPositionFromMouse(e.mousePosition, sceneView),
                    currentMap.Settings.CellSize);
                UpdatePreview(hoverCell);
                sceneView.Repaint();
                return;
            }

            float cs = currentMap.Settings.CellSize;
            var cell = WorldToCell(GetWorldPositionFromMouse(e.mousePosition, sceneView), cs);
            if (cell == null)
            {
                if (e.type == EventType.MouseDown || e.type == EventType.ScrollWheel) e.Use();
                return;
            }

            // ── Ctrl + 스크롤: 높이 조절 ─────────────────────────
            if (e.type == EventType.ScrollWheel && e.control)
            {
                float step = cs * 0.5f;
                float delta = e.delta.y < 0 ? step : -step;
                bool changed = false;

                foreach (var p in currentMap.Singles)
                {
                    var item = FindItem(p.MainKey, p.VariantKey);
                    var size = ValidSize(item);
                    if (cell.Value.x >= p.CellX && cell.Value.x < p.CellX + size.x &&
                        cell.Value.y >= p.CellZ && cell.Value.y < p.CellZ + size.y)
                    {
                        p.PositionY += delta;
                        var origin = new Vector2Int(p.CellX, p.CellZ);
                        if (_placedObjects.TryGetValue(origin, out var go) && go != null)
                        {
                            var t = go.transform;
                            t.position = new Vector3(t.position.x, p.PositionY, t.position.z);
                        }
                        changed = true;
                        break;
                    }
                }
                if (changed) { sceneView.Repaint(); Repaint(); }
                e.Use();
                return;
            }

            // ── Ctrl + 우클릭 / Delete: 삭제 ─────────────────────
            bool doDelete = (e.type == EventType.MouseDown && e.button == 1 && e.control)
                         || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Delete);
            if (doDelete)
            {
                if (TryDeleteAtCell(cell.Value))
                {
                    if (_selectedCell == cell.Value) _selectedCell = null;
                    RebuildOccupancy();
                    sceneView.Repaint();
                    Repaint();
                }
                e.Use();
                return;
            }

            // ── 좌클릭 ───────────────────────────────────────────
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (selectedMainKey != int.MinValue)
                {
                    ClearPreview();
                    TryPlace(cell.Value);
                }
                else
                    _selectedCell = (_occupancy.ContainsKey(cell.Value) || _otherCells.Contains(cell.Value))
                        ? cell.Value : (Vector2Int?)null;

                e.Use();
                sceneView.Repaint();
                Repaint();
            }
        }

        // ── 배치 ────────────────────────────────────────────────
        void TryPlace(Vector2Int cell)
        {
            var item = FindItem(selectedMainKey, selectedVariantKey);
            if (item == null || item.Prefab == null) return;

            var s = currentMap.Settings;
            if (cell.x < 0 || cell.y < 0 || cell.x >= s.Width || cell.y >= s.Height) return;

            float cs = s.CellSize;

            if (item.IsRoad)
            {
                if (_occupancy.ContainsKey(cell)) return;
                var p = new RoadPlacement { MainKey = selectedMainKey, CellX = cell.x, CellZ = cell.y };
                currentMap.Roads.Add(p);
                _occupancy[cell] = PlacementKind.Road;
                _placedObjects[cell] = InstantiateRoad(p, item, cs);
            }
            else if (item.Category == PrefabCategory.Other)
            {
                // Other 항목: _otherCells에서만 충돌 확인 (비-Other 위에 배치 가능)
                var size = ValidSize(item);
                for (int dx = 0; dx < size.x; dx++)
                    for (int dz = 0; dz < size.y; dz++)
                    {
                        var c = new Vector2Int(cell.x + dx, cell.y + dz);
                        if (c.x >= s.Width || c.y >= s.Height) return;
                        if (_otherCells.Contains(c)) return;
                    }

                var (rRot, rOx, rOz) = SampleRandom(cs);
                var p = new SinglePlacement
                {
                    MainKey    = selectedMainKey,
                    VariantKey = selectedVariantKey,
                    CellX      = cell.x,
                    CellZ      = cell.y,
                    PositionY  = 0f,
                    OffsetX    = rOx,
                    OffsetZ    = rOz,
                    RotationY  = rRot,
                    Scale      = instanceScale,
                };
                currentMap.Singles.Add(p);

                for (int dx = 0; dx < size.x; dx++)
                    for (int dz = 0; dz < size.y; dz++)
                        _otherCells.Add(new Vector2Int(cell.x + dx, cell.y + dz));

                _otherPlacedObjects[cell] = InstantiateSingle(p, item, cs);
            }
            else // Single (non-Other)
            {
                // 비-Other: _occupancy에서만 충돌 확인 (_otherCells 무시 → Other 위에 배치 가능)
                var size = ValidSize(item);
                for (int dx = 0; dx < size.x; dx++)
                    for (int dz = 0; dz < size.y; dz++)
                    {
                        var c = new Vector2Int(cell.x + dx, cell.y + dz);
                        if (c.x >= s.Width || c.y >= s.Height) return;
                        if (_occupancy.ContainsKey(c)) return;
                    }

                // footprint 원점 셀에 Other가 있으면 그 PositionY 위에 배치
                float baseY = GetOtherHeightAt(cell);

                var (rRot, rOx, rOz) = SampleRandom(cs);
                var p = new SinglePlacement
                {
                    MainKey    = selectedMainKey,
                    VariantKey = selectedVariantKey,
                    CellX      = cell.x,
                    CellZ      = cell.y,
                    PositionY  = baseY,
                    OffsetX    = rOx,
                    OffsetZ    = rOz,
                    RotationY  = rRot,
                    Scale      = instanceScale,
                };
                currentMap.Singles.Add(p);

                for (int dx = 0; dx < size.x; dx++)
                    for (int dz = 0; dz < size.y; dz++)
                        _occupancy[new Vector2Int(cell.x + dx, cell.y + dz)] = PlacementKind.Single;

                _placedObjects[cell] = InstantiateSingle(p, item, cs);
            }
        }

        // ── 셀 배치물 삭제 ──────────────────────────────────────
        //  Size > 1×1 인 Single은 해당 셀을 포함한 원점 항목을 찾아 삭제.
        bool TryDeleteAtCell(Vector2Int cell)
        {
            // 비-Other(_occupancy) 우선 삭제, 없으면 Other(_otherCells) 삭제
            if (_occupancy.TryGetValue(cell, out var kind))
            {
                switch (kind)
                {
                    case PlacementKind.Road:
                    {
                        int idx = currentMap.Roads.FindIndex(p => p.CellX == cell.x && p.CellZ == cell.y);
                        if (idx < 0) break;
                        currentMap.Roads.RemoveAt(idx);
                        DestroyPlacedAt(cell);
                        return true;
                    }
                    case PlacementKind.Single:
                    {
                        for (int i = 0; i < currentMap.Singles.Count; i++)
                        {
                            var p    = currentMap.Singles[i];
                            var item = FindItem(p.MainKey, p.VariantKey);
                            if (item?.Category == PrefabCategory.Other) continue;
                            var size = ValidSize(item);
                            if (cell.x < p.CellX || cell.x >= p.CellX + size.x) continue;
                            if (cell.y < p.CellZ || cell.y >= p.CellZ + size.y) continue;
                            currentMap.Singles.RemoveAt(i);
                            DestroyPlacedAt(new Vector2Int(p.CellX, p.CellZ));
                            return true;
                        }
                        break;
                    }
                }
            }

            // Other 레이어 삭제
            if (_otherCells.Contains(cell))
            {
                for (int i = 0; i < currentMap.Singles.Count; i++)
                {
                    var p    = currentMap.Singles[i];
                    var item = FindItem(p.MainKey, p.VariantKey);
                    if (item?.Category != PrefabCategory.Other) continue;
                    var size = ValidSize(item);
                    if (cell.x < p.CellX || cell.x >= p.CellX + size.x) continue;
                    if (cell.y < p.CellZ || cell.y >= p.CellZ + size.y) continue;
                    currentMap.Singles.RemoveAt(i);
                    var origin = new Vector2Int(p.CellX, p.CellZ);
                    if (_otherPlacedObjects.TryGetValue(origin, out var go) && go != null)
                        Object.DestroyImmediate(go);
                    _otherPlacedObjects.Remove(origin);
                    return true;
                }
            }

            return false;
        }

        // ── 점유 캐시 재빌드 ─────────────────────────────────────
        void RebuildOccupancy()
        {
            _occupancy.Clear();
            _otherCells.Clear();
            if (currentMap == null) return;

            foreach (var p in currentMap.Singles)
            {
                var item = FindItem(p.MainKey, p.VariantKey);
                var size = ValidSize(item);
                bool isOther = item?.Category == PrefabCategory.Other;
                for (int dx = 0; dx < size.x; dx++)
                    for (int dz = 0; dz < size.y; dz++)
                    {
                        var c = new Vector2Int(p.CellX + dx, p.CellZ + dz);
                        if (isOther) _otherCells.Add(c);
                        else         _occupancy[c] = PlacementKind.Single;
                    }
            }
            foreach (var p in currentMap.Roads)
                _occupancy[new Vector2Int(p.CellX, p.CellZ)] = PlacementKind.Road;
        }

        // ── 레지스트리 조회 헬퍼 ────────────────────────────────
        RegistryItem FindItem(int mainKey, int variantKey)
        {
            foreach (var reg in registries)
            {
                if (reg == null) continue;
                var item = reg.GetItem(mainKey, variantKey);
                if (item != null) return item;
            }
            return null;
        }

        // ── Size 헬퍼 (null 안전) ────────────────────────────────
        static Vector2Int ValidSize(RegistryItem item)
            => (item != null && item.Size.x >= 1 && item.Size.y >= 1)
                ? item.Size : Vector2Int.one;

        // ── 랜덤 배치 샘플 ──────────────────────────────────────────
        (float rot, float ox, float oz) SampleRandom(float cs)
        {
            float rot = randomRotation ? UnityEngine.Random.Range(0f, 360f) : 0f;
            float ox  = 0f, oz = 0f;
            if (randomOffset)
            {
                float range = randomOffsetRange * cs;
                ox = UnityEngine.Random.Range(-range, range);
                oz = UnityEngine.Random.Range(-range, range);
            }
            return (rot, ox, oz);
        }

        // ── Other 항목 높이 조회 ─────────────────────────────────
        // 해당 셀에 Other 항목이 있으면 그 PositionY를 반환, 없으면 0.
        float GetOtherHeightAt(Vector2Int cell)
        {
            if (!_otherCells.Contains(cell)) return 0f;
            foreach (var p in currentMap.Singles)
            {
                var item = FindItem(p.MainKey, p.VariantKey);
                if (item?.Category != PrefabCategory.Other) continue;
                var size = ValidSize(item);
                if (cell.x >= p.CellX && cell.x < p.CellX + size.x &&
                    cell.y >= p.CellZ && cell.y < p.CellZ + size.y)
                    return p.PositionY;
            }
            return 0f;
        }

        // ══════════════════════════════════════════════════════════
        //  씬 GO 관리
        // ══════════════════════════════════════════════════════════

        // 단일 GO 파괴 (비-Other)
        void DestroyPlacedAt(Vector2Int origin)
        {
            if (_placedObjects.TryGetValue(origin, out var go))
            {
                _placedObjects.Remove(origin);
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        // 전체 GO 파괴 (에디터 종료 / 새 맵 로드 전)
        void ClearAllPlacedObjects()
        {
            foreach (var kv in _placedObjects)
                if (kv.Value != null) Object.DestroyImmediate(kv.Value);
            _placedObjects.Clear();

            foreach (var kv in _otherPlacedObjects)
                if (kv.Value != null) Object.DestroyImmediate(kv.Value);
            _otherPlacedObjects.Clear();
        }

        // MapData → 씬 GO 전체 재생성 (저장 후 로드 시)
        void ReloadAllPlacedObjects()
        {
            ClearAllPlacedObjects();
            if (currentMap == null) return;
            float cs = currentMap.Settings.CellSize;

            foreach (var p in currentMap.Singles)
            {
                var item = FindItem(p.MainKey, p.VariantKey);
                if (item == null || item.Prefab == null) continue;
                var origin = new Vector2Int(p.CellX, p.CellZ);
                var go = InstantiateSingle(p, item, cs);
                if (item.Category == PrefabCategory.Other)
                    _otherPlacedObjects[origin] = go;
                else
                    _placedObjects[origin] = go;
            }
            foreach (var p in currentMap.Roads)
            {
                var item = FindItem(p.MainKey, 0);
                if (item == null || item.Prefab == null) continue;
                _placedObjects[new Vector2Int(p.CellX, p.CellZ)] = InstantiateRoad(p, item, cs);
            }
        }

        // ── Instantiate 헬퍼 ─────────────────────────────────────

        static GameObject InstantiateSingle(SinglePlacement p, RegistryItem item, float cs)
        {
            var size = ValidSize(item);
            var pos = new Vector3(
                (p.CellX + size.x * 0.5f) * cs + item.Offset.x + p.OffsetX,
                p.PositionY + item.Offset.y,
                (p.CellZ + size.y * 0.5f) * cs + item.Offset.z + p.OffsetZ);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, p.RotationY, 0f));
            go.transform.localScale = Vector3.one * (p.Scale > 0f ? p.Scale : 1f);
            go.hideFlags = HideFlags.DontSave;
            return go;
        }

        static GameObject InstantiateMulti(MultiPlacement p, RegistryItem item, float cs)
        {
            // 에디터에서는 셀 중심에 1개 미리보기만 표시 (인게임에서 랜덤 배치)
            var pos = new Vector3((p.CellX + 0.5f) * cs, p.Height, (p.CellZ + 0.5f) * cs);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
            go.transform.position = pos;
            go.transform.localScale = Vector3.one;
            go.hideFlags = HideFlags.DontSave;
            return go;
        }

        static GameObject InstantiateRoad(RoadPlacement p, RegistryItem item, float cs)
        {
            var pos = new Vector3((p.CellX + 0.5f) * cs, 0f, (p.CellZ + 0.5f) * cs);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(item.Prefab);
            go.transform.position = pos;
            go.hideFlags = HideFlags.DontSave;
            return go;
        }

        // ══════════════════════════════════════════════════════════
        //  저장 / 로드
        // ══════════════════════════════════════════════════════════
        void DrawSaveLoad()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("맵 저장 / 로드", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _saveFilePath = EditorGUILayout.TextField(_saveFilePath);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string dir = string.IsNullOrEmpty(_saveFilePath)
                    ? Application.dataPath : System.IO.Path.GetDirectoryName(_saveFilePath);
                string path = EditorUtility.SaveFilePanel(
                    "맵 저장 경로", dir,
                    currentMap?.MapName ?? "NewMap", "json");
                if (!string.IsNullOrEmpty(path)) { _saveFilePath = path; Repaint(); }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_saveFilePath)))
            {
                if (GUILayout.Button("저장", GUILayout.Height(24)))
                    SaveMap();
            }

            if (GUILayout.Button("로드…", GUILayout.Height(24)))
            {
                string path = EditorUtility.OpenFilePanel("맵 로드", Application.dataPath, "json");
                if (!string.IsNullOrEmpty(path)) { _saveFilePath = path; LoadMap(path); }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        void SaveMap()
        {
            if (currentMap == null || string.IsNullOrEmpty(_saveFilePath)) return;
            currentMap.FlushDicts();
            string json = UnityEngine.JsonUtility.ToJson(currentMap, prettyPrint: true);
            System.IO.File.WriteAllText(_saveFilePath, json, System.Text.Encoding.UTF8);
            AssetDatabase.Refresh();
            Debug.Log($"[MapEditor] 저장 완료: {_saveFilePath}");
        }

        void LoadMap(string path)
        {
            if (!System.IO.File.Exists(path)) { Debug.LogError($"[MapEditor] 파일 없음: {path}"); return; }
            string json = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
            var loaded = UnityEngine.JsonUtility.FromJson<MapData>(json);
            if (loaded == null) { Debug.LogError("[MapEditor] JSON 파싱 실패"); return; }

            currentMap = loaded;
            mapName = loaded.MapName;
            currentMap.RebuildDicts();

            ClearAllPlacedObjects();
            RebuildOccupancy();
            ReloadAllPlacedObjects();

            _selectedCell = null;
            selectedMainKey = int.MinValue;
            selectedVariantKey = 0;
            _overlayDirty = true;

            SceneView.RepaintAll();
            Repaint();
            Debug.Log($"[MapEditor] 로드 완료: {loaded.MapName}");
        }

        // ══════════════════════════════════════════════════════════
        //  읽기 전용 배치물 인스펙터
        // ══════════════════════════════════════════════════════════
        void DrawSelectedPlacementInfo()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("선택된 배치물", EditorStyles.boldLabel);

            if (_selectedCell == null || currentMap == null)
            {
                EditorGUILayout.LabelField("(없음)", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            var cell = _selectedCell.Value;

            bool inOccupancy = _occupancy.TryGetValue(cell, out var kind);
            bool inOther     = _otherCells.Contains(cell);

            if (!inOccupancy && !inOther)
            {
                _selectedCell = null;
                EditorGUILayout.LabelField("(없음)", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.BeginDisabledGroup(true);

            float cs = currentMap.Settings.CellSize;

            void ShowSingle(SinglePlacement p, string kindLabel)
            {
                var item = FindItem(p.MainKey, p.VariantKey);
                var size = ValidSize(item);
                EditorGUILayout.LabelField("종류", kindLabel);
                EditorGUILayout.TextField("이름", item?.Name ?? $"M{p.MainKey}");
                EditorGUILayout.Vector3Field("Position",
                    new Vector3((p.CellX + size.x * 0.5f) * cs, p.PositionY, (p.CellZ + size.y * 0.5f) * cs));
                EditorGUILayout.FloatField("Height (Y)", p.PositionY);
                EditorGUILayout.FloatField("Scale", p.Scale);
                EditorGUILayout.IntField("MainKey", p.MainKey);
                EditorGUILayout.IntField("VariantKey", p.VariantKey);
            }

            if (inOccupancy && kind == PlacementKind.Road)
            {
                var p = currentMap.Roads.Find(x => x.CellX == cell.x && x.CellZ == cell.y);
                if (p != null)
                {
                    EditorGUILayout.LabelField("종류", "Road");
                    EditorGUILayout.Vector3Field("Position",
                        new Vector3((p.CellX + 0.5f) * cs, 0f, (p.CellZ + 0.5f) * cs));
                    EditorGUILayout.IntField("MainKey", p.MainKey);
                }
            }
            else if (inOccupancy && kind == PlacementKind.Single)
            {
                var p = currentMap.Singles.Find(x =>
                {
                    var item = FindItem(x.MainKey, x.VariantKey);
                    if (item?.Category == PrefabCategory.Other) return false;
                    var size = ValidSize(item);
                    return cell.x >= x.CellX && cell.x < x.CellX + size.x
                        && cell.y >= x.CellZ && cell.y < x.CellZ + size.y;
                });
                if (p != null) ShowSingle(p, "Single");
            }

            if (inOther)
            {
                var p = currentMap.Singles.Find(x =>
                {
                    var item = FindItem(x.MainKey, x.VariantKey);
                    if (item?.Category != PrefabCategory.Other) return false;
                    var size = ValidSize(item);
                    return cell.x >= x.CellX && cell.x < x.CellX + size.x
                        && cell.y >= x.CellZ && cell.y < x.CellZ + size.y;
                });
                if (p != null) ShowSingle(p, "Other");
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        // ── 배치 탭 SceneView 오버레이 ──────────────────────────
        //  실제 GO가 있으면 오버레이 색 불필요 → 마우스 셀 + 선택 셀만 표시.
        //  GO 없는 셀(프리팹 null 등)은 보조 색으로 표시.
        //  Repaint 이벤트에서만 호출할 것.
        void DrawPlacementOverlay(SceneView sceneView)
        {
            if (activeTab != MainTab.Place || currentMap == null) return;
            if (_overlayMat == null) return;

            float cs = currentMap.Settings.CellSize;
            var s = currentMap.Settings;

            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();

            void AddQuad(Vector2Int c, Color col, float y = 0.005f)
            {
                float x0 = c.x * cs, x1 = x0 + cs;
                float z0 = c.y * cs, z1 = z0 + cs;
                int b = verts.Count;
                verts.Add(new Vector3(x0, y, z0)); verts.Add(new Vector3(x1, y, z0));
                verts.Add(new Vector3(x1, y, z1)); verts.Add(new Vector3(x0, y, z1));
                cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }

            // ① GO 없는 점유 셀만 보조 오버레이 (GO 있으면 스킵)
            var goFootprint      = new HashSet<Vector2Int>();
            var otherGoFootprint = new HashSet<Vector2Int>();
            foreach (var p in currentMap.Singles)
            {
                var item   = FindItem(p.MainKey, p.VariantKey);
                var origin = new Vector2Int(p.CellX, p.CellZ);
                bool isOther = item?.Category == PrefabCategory.Other;
                var dict = isOther ? _otherPlacedObjects : _placedObjects;
                if (!(dict.TryGetValue(origin, out var sgo) && sgo != null)) continue;
                var sz = ValidSize(item);
                for (int dx = 0; dx < sz.x; dx++)
                    for (int dz = 0; dz < sz.y; dz++)
                    {
                        var fc = new Vector2Int(p.CellX + dx, p.CellZ + dz);
                        if (isOther) otherGoFootprint.Add(fc);
                        else         goFootprint.Add(fc);
                    }
            }
            foreach (var p in currentMap.Roads)
                if (_placedObjects.TryGetValue(new Vector2Int(p.CellX, p.CellZ), out var rgo) && rgo != null)
                    goFootprint.Add(new Vector2Int(p.CellX, p.CellZ));

            foreach (var kv in _occupancy)
            {
                if (goFootprint.Contains(kv.Key)) continue;
                var col = kv.Value == PlacementKind.Road
                    ? new Color(1.0f, 0.85f, 0.1f, 0.30f)
                    : new Color(0.2f, 0.9f,  0.3f, 0.30f);
                AddQuad(kv.Key, col);
            }
            foreach (var c in _otherCells)
            {
                if (otherGoFootprint.Contains(c)) continue;
                AddQuad(c, new Color(0.6f, 0.4f, 0.9f, 0.25f));
            }

            // ② 선택된 배치물 하이라이트 (흰 테두리)
            if (_selectedCell.HasValue &&
                (_occupancy.ContainsKey(_selectedCell.Value) || _otherCells.Contains(_selectedCell.Value)))
                AddQuad(_selectedCell.Value, new Color(1f, 1f, 1f, 0.35f), 0.025f);

            // ③ 마우스 위치 셀 하이라이트
            if (Event.current != null)
            {
                var mouseCell = WorldToCell(
                    GetWorldPositionFromMouse(Event.current.mousePosition, sceneView), cs);
                if (mouseCell.HasValue)
                {
                    Color hoverCol;
                    if (selectedMainKey != int.MinValue)
                    {
                        var selItem = FindItem(selectedMainKey, selectedVariantKey);
                        bool isOther = selItem?.Category == PrefabCategory.Other;
                        bool blocked = isOther
                            ? _otherCells.Contains(mouseCell.Value)
                            : _occupancy.ContainsKey(mouseCell.Value);
                        bool inBounds = mouseCell.Value.x >= 0 && mouseCell.Value.y >= 0
                                     && mouseCell.Value.x < s.Width && mouseCell.Value.y < s.Height;
                        hoverCol = (!blocked && inBounds)
                            ? new Color(0.3f, 1.0f, 0.4f, 0.45f)
                            : new Color(1.0f, 0.25f, 0.25f, 0.45f);
                    }
                    else
                    {
                        hoverCol = new Color(1f, 1f, 1f, 0.15f);
                    }
                    AddQuad(mouseCell.Value, hoverCol, 0.02f);
                }
            }

            if (verts.Count > 0)
            {
                var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                mesh.indexFormat = verts.Count > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(verts);
                mesh.SetColors(cols);
                mesh.SetTriangles(tris, 0);
                _overlayMat.SetPass(0);
                Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
                Object.DestroyImmediate(mesh);
            }

            // ④ 이름 라벨 (GO 실물이 있어도 셀 좌표 기준 표시)
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 9,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 0.5f, 0.9f) },
            };

            foreach (var p in currentMap.Singles)
            {
                var item = FindItem(p.MainKey, p.VariantKey);
                var size = ValidSize(item);
                string lbl = item?.Name ?? $"M{p.MainKey}";
                float cx = (p.CellX + size.x * 0.5f) * cs;
                float cz = (p.CellZ + size.y * 0.5f) * cs;
                Handles.Label(new Vector3(cx, p.PositionY + 0.1f, cz), lbl, labelStyle);
            }
            foreach (var p in currentMap.Roads)
            {
                Handles.Label(
                    new Vector3((p.CellX + 0.5f) * cs, 0.1f, (p.CellZ + 0.5f) * cs),
                    "Road", labelStyle);
            }
        }
    }
}