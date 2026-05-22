using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  PrefabRegistryWindow
    //
    //  메뉴: Tools > CitySim > Prefab Registry Editor
    //  또는 SO 인스펙터의 "Open in Registry Editor" 버튼
    //
    //  기능:
    //    - 여러 GamePrefabRegistry SO를 드롭다운으로 전환
    //    - 항목 편집 (Name, Key, Prefab, Size, Offset, RoadShape, Usage, Category)
    //    - Refresh (단일 SO 검증) + Export JSON
    //    - Deleted 섹션 (복원 가능)
    //    - Multi 전용 옵션 (Count, ItemSize)
    //    - 도로 항목 Size = (1,1) 강제 + 표시
    // ══════════════════════════════════════════════════════════════
    public class PrefabRegistryWindow : EditorWindow
    {
        // ── 대상 SO ───────────────────────────────────────────────
        List<GamePrefabRegistry> registries = new();
        int   selectedRegistryIdx = 0;

        // ── 내부 SerializedObject (선택된 SO) ─────────────────────
        SerializedObject   serializedReg;
        SerializedProperty itemsProp;
        SerializedProperty dlcIdProp;
        SerializedProperty dlcNameProp;
        SerializedProperty displayNameProp;
        SerializedProperty jsonExportPathProp;

        // ── UI 상태 ────────────────────────────────────────────────
        Vector2 scrollPos;
        bool foldDlc     = true;
        bool foldSingles = true;
        bool foldMultis  = true;
        bool foldRoads   = true;
        bool foldDeleted = false;
        bool showThumbs  = true;

        Dictionary<int, bool> usageFolds = new();

        // ══════════════════════════════════════════════════════════
        //  창 열기
        // ══════════════════════════════════════════════════════════
        [MenuItem("Tools/CitySim/Prefab Registry Editor")]
        public static void ShowWindow()
        {
            var w = GetWindow<PrefabRegistryWindow>("Prefab Registry");
            w.minSize = new Vector2(420, 600);
            w.Show();
        }

        /// <summary>외부에서 특정 SO를 지정해 열기 (인스펙터 버튼 등).</summary>
        public static void OpenWith(GamePrefabRegistry registry)
        {
            var w = GetWindow<PrefabRegistryWindow>("Prefab Registry");
            w.minSize = new Vector2(420, 600);

            // 이미 등록된 SO면 선택, 없으면 추가
            int idx = w.registries.IndexOf(registry);
            if (idx < 0)
            {
                w.registries.Add(registry);
                idx = w.registries.Count - 1;
            }
            w.SelectRegistry(idx);
            w.Show();
        }

        void OnEnable()
        {
            // 프로젝트 내 모든 GamePrefabRegistry 자동 검색
            AutoDiscoverRegistries();
        }

        void AutoDiscoverRegistries()
        {
            var guids = AssetDatabase.FindAssets("t:GamePrefabRegistry");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var reg  = AssetDatabase.LoadAssetAtPath<GamePrefabRegistry>(path);
                if (reg != null && !registries.Contains(reg))
                    registries.Add(reg);
            }

            if (registries.Count > 0 && selectedRegistryIdx >= registries.Count)
                selectedRegistryIdx = 0;

            if (registries.Count > 0)
                SelectRegistry(selectedRegistryIdx);
        }

        void SelectRegistry(int idx)
        {
            if (idx < 0 || idx >= registries.Count) return;
            selectedRegistryIdx = idx;

            var reg = registries[idx];
            if (reg == null) return;

            serializedReg      = new SerializedObject(reg);
            itemsProp          = serializedReg.FindProperty("items");
            dlcIdProp          = serializedReg.FindProperty("dlcId");
            dlcNameProp        = serializedReg.FindProperty("dlcName");
            displayNameProp    = serializedReg.FindProperty("displayName");
            jsonExportPathProp = serializedReg.FindProperty("jsonExportPath");

            Repaint();
        }

        // ══════════════════════════════════════════════════════════
        //  메인 GUI
        // ══════════════════════════════════════════════════════════
        void OnGUI()
        {
            DrawTopBar();

            if (registries.Count == 0 || serializedReg == null)
            {
                EditorGUILayout.HelpBox(
                    "No GamePrefabRegistry found.\n" +
                    "Create one via Assets > Create > CitySim > Game Prefab Registry.",
                    MessageType.Info);
                return;
            }

            serializedReg.Update();

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            DrawDlcHeader();
            EditorGUILayout.Space(5);
            DrawValidation();
            EditorGUILayout.Space(5);
            DrawToolbar();
            EditorGUILayout.Space(10);
            DrawItemGroups();

            EditorGUILayout.EndScrollView();

            serializedReg.ApplyModifiedProperties();
        }

        // ══════════════════════════════════════════════════════════
        //  상단 바 — SO 선택 드롭다운 + 새로고침
        // ══════════════════════════════════════════════════════════
        void DrawTopBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // SO 드롭다운
            var names = new string[registries.Count];
            for (int i = 0; i < registries.Count; i++)
                names[i] = registries[i] != null
                    ? $"{registries[i].DlcName} (DLC {registries[i].DlcId})"
                    : "(null)";

            int newIdx = EditorGUILayout.Popup(selectedRegistryIdx, names,
                GUILayout.MinWidth(200));
            if (newIdx != selectedRegistryIdx)
                SelectRegistry(newIdx);

            // SO 직접 지정 슬롯
            var dragged = (GamePrefabRegistry)EditorGUILayout.ObjectField(
                GUIContent.none,
                selectedRegistryIdx < registries.Count ? registries[selectedRegistryIdx] : null,
                typeof(GamePrefabRegistry), false, GUILayout.Width(160));
            if (dragged != null && (selectedRegistryIdx >= registries.Count
                || dragged != registries[selectedRegistryIdx]))
            {
                if (!registries.Contains(dragged)) registries.Add(dragged);
                SelectRegistry(registries.IndexOf(dragged));
            }

            if (GUILayout.Button("⟳", EditorStyles.toolbarButton, GUILayout.Width(26)))
            {
                registries.Clear();
                AutoDiscoverRegistries();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════
        //  DLC 헤더
        // ══════════════════════════════════════════════════════════
        void DrawDlcHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foldDlc = EditorGUILayout.Foldout(foldDlc,
                $"{dlcNameProp.stringValue}  (DLC ID: {dlcIdProp.intValue})",
                true, EditorStyles.foldoutHeader);

            if (foldDlc)
            {
                EditorGUILayout.PropertyField(dlcIdProp,
                    new GUIContent("DLC ID"));
                EditorGUILayout.PropertyField(dlcNameProp,
                    new GUIContent("DLC Name"));
                EditorGUILayout.PropertyField(displayNameProp,
                    new GUIContent("Display Name",
                        "맵에디터/UI 표시용 이름. 비어있으면 DLC Name 사용."));

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("JSON Export", EditorStyles.miniBoldLabel);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(jsonExportPathProp,
                    new GUIContent("Export Path"));
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string path = EditorUtility.SaveFilePanel(
                        "Save Registry JSON",
                        string.IsNullOrEmpty(jsonExportPathProp.stringValue)
                            ? Application.dataPath
                            : System.IO.Path.GetDirectoryName(jsonExportPathProp.stringValue),
                        $"Registry_{dlcNameProp.stringValue}.json",
                        "json");
                    if (!string.IsNullOrEmpty(path))
                    {
                        jsonExportPathProp.stringValue = path;
                        serializedReg.ApplyModifiedProperties();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════
        //  검증 메시지
        // ══════════════════════════════════════════════════════════
        void DrawValidation()
        {
            var reg = registries[selectedRegistryIdx];
            if (reg == null) return;

            var issues = reg.Validate();
            if (issues.Count == 0) return;

            foreach (var issue in issues)
            {
                MessageType mt = issue.Level == ValidationLevel.Error   ? MessageType.Error
                              :  issue.Level == ValidationLevel.Warning ? MessageType.Warning
                                                                        : MessageType.Info;
                EditorGUILayout.HelpBox(issue.Message, mt);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  툴바 (Refresh + Export + Thumbs)
        // ══════════════════════════════════════════════════════════
        void DrawToolbar()
        {
            var reg = registries[selectedRegistryIdx];
            if (reg == null) return;

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Refresh (Validate)", GUILayout.Height(26)))
            {
                serializedReg.ApplyModifiedProperties();
                var issues = reg.Validate();
                if (issues.Count == 0)
                    Debug.Log($"[{reg.DlcName}] Validation OK.");
                else
                    foreach (var issue in issues)
                        Debug.LogWarning($"[{reg.DlcName}] {issue.Message}");
            }

            if (GUILayout.Button("Export JSON", GUILayout.Height(26), GUILayout.Width(110)))
            {
                serializedReg.ApplyModifiedProperties();
                if (reg.ExportJson(out string err))
                    Debug.Log($"[{reg.DlcName}] Exported: {reg.JsonExportPath}");
                else
                    Debug.LogError($"[{reg.DlcName}] Export failed: {err}");
            }

            showThumbs = GUILayout.Toggle(showThumbs, "Thumbs", "Button",
                GUILayout.Width(60), GUILayout.Height(26));

            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════
        //  항목 그룹 분류 및 표시
        // ══════════════════════════════════════════════════════════
        void DrawItemGroups()
        {
            var reg = registries[selectedRegistryIdx];
            if (reg == null) return;

            var singles = new List<int>();
            var multis  = new List<int>();
            var roads   = new List<int>();
            var deleted = new List<int>();

            for (int i = 0; i < itemsProp.arraySize; i++)
            {
                var elem = itemsProp.GetArrayElementAtIndex(i);
                if (elem.FindPropertyRelative("IsDeleted").boolValue)
                {
                    deleted.Add(i); continue;
                }

                var shapeProp = elem.FindPropertyRelative("RoadShape");
                var dirsProp = elem.FindPropertyRelative("RoadDirections");
                var categoryProp = elem.FindPropertyRelative("Category");
                var objectTypeProp = elem.FindPropertyRelative("ObjectType");
                bool isRoadItem = IsRoadSlot(shapeProp, dirsProp, categoryProp, objectTypeProp);
                if (isRoadItem)
                {
                    roads.Add(i); continue;
                }

                var modeProp = elem.FindPropertyRelative("SpawnMode");
                if ((PrefabSpawnMode)modeProp.enumValueIndex == PrefabSpawnMode.Single)
                    singles.Add(i);
                else
                    multis.Add(i);
            }

            DrawGroup("Single", singles, reg, ref foldSingles,
                () => {
                    var item = reg.AddItem();
                    item.SpawnMode = PrefabSpawnMode.Single;
                });

            EditorGUILayout.Space(5);

            DrawGroup("Multi", multis, reg, ref foldMultis,
                () => {
                    var item = reg.AddItem();
                    item.SpawnMode = PrefabSpawnMode.Multi;
                });

            EditorGUILayout.Space(5);

            DrawGroup("Road", roads, reg, ref foldRoads,
                () => {
                    var item = reg.AddItem();
                    item.SpawnMode  = PrefabSpawnMode.Single;
                    item.Category   = PrefabCategory.Road;
                    item.ObjectType = PrefabObjectType.Road;
                    item.RoadShape  = RoadShape.NotRoad;
                    item.RoadDirections = RoadDirection.N | RoadDirection.S;
                    item.Size       = new Vector2Int(1, 1);
                });

            if (deleted.Count > 0)
            {
                EditorGUILayout.Space(10);
                DrawDeletedSection(deleted, reg);
            }
        }

        void DrawGroup(string title, List<int> indices, GamePrefabRegistry reg,
            ref bool foldout, System.Action onAdd)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foldout = EditorGUILayout.Foldout(
                foldout, $"{title}  ({indices.Count})", true, EditorStyles.foldoutHeader);

            if (foldout)
            {
                foreach (int idx in indices)
                    DrawItem(idx, reg);

                if (GUILayout.Button($"+ Add {title} Item"))
                {
                    Undo.RecordObject(reg, $"Add {title} Item");
                    onAdd?.Invoke();
                }
            }
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════
        //  항목 그리기
        // ══════════════════════════════════════════════════════════
        void DrawItem(int index, GamePrefabRegistry reg)
        {
            var elem = itemsProp.GetArrayElementAtIndex(index);

            var nameProp       = elem.FindPropertyRelative("Name");
            var mainKeyProp    = elem.FindPropertyRelative("MainKey");
            var variantKeyProp = elem.FindPropertyRelative("VariantKey");
            var dlcKeyProp     = elem.FindPropertyRelative("DlcKey");
            var prefabProp     = elem.FindPropertyRelative("Prefab");
            var usageProp      = elem.FindPropertyRelative("Usage");
            var categoryProp   = elem.FindPropertyRelative("Category");
            var objectTypeProp = elem.FindPropertyRelative("ObjectType");
            var domainProp     = elem.FindPropertyRelative("Domain");
            var purposeProp    = elem.FindPropertyRelative("PurposeFlags");
            var techLevelProp  = elem.FindPropertyRelative("RequiredTechLevel");
            var allowedTerrainProp = elem.FindPropertyRelative("AllowedTerrains");
            var requiresFlatProp = elem.FindPropertyRelative("RequiresFlatFootprint");
            var requiresRoadProp = elem.FindPropertyRelative("RequiresRoadAdjacency");
            var doesNotBlockOccupancyProp = elem.FindPropertyRelative("DoesNotBlockOccupancy");
            var sizeProp       = elem.FindPropertyRelative("Size");
            var offsetProp     = elem.FindPropertyRelative("Offset");
            var roadShapeProp  = elem.FindPropertyRelative("RoadShape");
            var roadDirsProp   = elem.FindPropertyRelative("RoadDirections");
            var multiSizeProp  = elem.FindPropertyRelative("MultiItemSize");
            var multiCntProp   = elem.FindPropertyRelative("MultiCountPerCell");
            var spawnModeProp  = elem.FindPropertyRelative("SpawnMode");

            bool isRoad  = IsRoadSlot(roadShapeProp, roadDirsProp, categoryProp, objectTypeProp);
            bool isMulti = (PrefabSpawnMode)spawnModeProp.enumValueIndex == PrefabSpawnMode.Multi;

            // 도로는 Size 강제 (1, 1)
            if (isRoad)
            {
                var sv = sizeProp.vector2IntValue;
                if (sv.x != 1 || sv.y != 1)
                    sizeProp.vector2IntValue = new Vector2Int(1, 1);
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            // 썸네일
            if (showThumbs)
            {
                var go = prefabProp.objectReferenceValue as GameObject;
                Texture2D preview = go != null ? AssetPreview.GetAssetPreview(go) : null;
                if (preview == null && go != null
                    && AssetPreview.IsLoadingAssetPreview(go.GetInstanceID()))
                    Repaint();
                var rect = GUILayoutUtility.GetRect(52, 52,
                    GUILayout.Width(52), GUILayout.Height(52));
                if (preview != null) GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
                else GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            }

            EditorGUILayout.BeginVertical();

            // ── 1행: Name + 키들 + 삭제 버튼 ─────────────────────
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(nameProp, GUIContent.none, GUILayout.MinWidth(90));
            EditorGUILayout.LabelField("M", GUILayout.Width(13));
            EditorGUILayout.PropertyField(mainKeyProp, GUIContent.none, GUILayout.Width(52));
            EditorGUILayout.LabelField("V", GUILayout.Width(13));
            EditorGUILayout.PropertyField(variantKeyProp, GUIContent.none, GUILayout.Width(42));
            EditorGUILayout.LabelField("D", GUILayout.Width(13));
            EditorGUILayout.PropertyField(dlcKeyProp, GUIContent.none, GUILayout.Width(36));

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("X", GUILayout.Width(22)))
            {
                GUI.backgroundColor = prevBg;
                if (EditorUtility.DisplayDialog("Mark Deleted",
                    $"Mark '{nameProp.stringValue}' " +
                    $"({mainKeyProp.intValue},{variantKeyProp.intValue}) as deleted?",
                    "Mark Deleted", "Cancel"))
                {
                    Undo.RecordObject(reg, "Mark Deleted");
                    reg.Items[index].IsDeleted = true;
                    EditorUtility.SetDirty(reg);
                }
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            // ── 2행: Prefab ───────────────────────────────────────
            EditorGUILayout.PropertyField(prefabProp, new GUIContent("Prefab"));

            // ── 3행: Category ─────────────────────────────────────
            EditorGUILayout.PropertyField(categoryProp, new GUIContent("Category"));
            if (objectTypeProp != null)
                EditorGUILayout.PropertyField(objectTypeProp, new GUIContent("Object Type"));
            if (domainProp != null)
                EditorGUILayout.PropertyField(domainProp, new GUIContent("Domain"));
            if (techLevelProp != null)
                techLevelProp.intValue = Mathf.Max(0,
                    EditorGUILayout.IntField("Required Tech Level", techLevelProp.intValue));
            if (purposeProp != null)
            {
                var purpose = (PrefabPurposeFlags)purposeProp.intValue;
                purpose = (PrefabPurposeFlags)EditorGUILayout.EnumFlagsField("Purpose Flags", purpose);
                purposeProp.intValue = (int)purpose;
            }
            if (allowedTerrainProp != null)
                EditorGUILayout.PropertyField(allowedTerrainProp, new GUIContent("Allowed Terrains"));
            if (requiresFlatProp != null || requiresRoadProp != null || doesNotBlockOccupancyProp != null)
            {
                EditorGUILayout.LabelField("Placement Rules", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                if (requiresFlatProp != null)
                    EditorGUILayout.PropertyField(requiresFlatProp, new GUIContent("Requires Flat Footprint"));
                if (requiresRoadProp != null)
                    EditorGUILayout.PropertyField(requiresRoadProp, new GUIContent("Requires Road Adjacency"));
                if (doesNotBlockOccupancyProp != null)
                {
                    bool isTerrain = objectTypeProp != null
                        && (PrefabObjectType)objectTypeProp.enumValueIndex == PrefabObjectType.Terrain;
                    EditorGUI.BeginDisabledGroup(isTerrain);
                    bool blocks = !doesNotBlockOccupancyProp.boolValue;
                    bool newBlocks = EditorGUILayout.Toggle("Blocks Occupancy", blocks);
                    if (newBlocks != blocks)
                        doesNotBlockOccupancyProp.boolValue = !newBlocks;
                    EditorGUI.EndDisabledGroup();
                }
                EditorGUI.indentLevel--;
            }

            // ── 4행: Size / Offset ────────────────────────────────
            if (!isRoad && !isMulti)
            {
                EditorGUILayout.PropertyField(sizeProp,
                    new GUIContent("Size (XZ cells)"));
            }
            else if (isRoad)
            {
                // 도로: Size 고정 표시 (편집 불가)
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Vector2IntField("Size (fixed 1x1)",
                    new Vector2Int(1, 1));
                EditorGUI.EndDisabledGroup();
            }
            else if (isMulti)
            {
                // Multi: Size 표시 안 함 (1셀 강제)
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Size", "1x1 (Multi fixed)",
                    EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }

            var offset = offsetProp.vector3Value;
            float yOffset = EditorGUILayout.FloatField("Y Offset", offset.y);
            offsetProp.vector3Value = new Vector3(0f, yOffset, 0f);

            // ── 5행: Road Shape (도로만) ──────────────────────────
            if (isRoad)
            {
                if (roadDirsProp != null)
                {
                    var dirs = (RoadDirection)roadDirsProp.intValue;
                    dirs = (RoadDirection)EditorGUILayout.EnumFlagsField("Road Directions", dirs);
                    roadDirsProp.intValue = (int)dirs;
                }

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.PropertyField(roadShapeProp,
                    new GUIContent("Legacy Road Shape"));
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                // 비도로: 숨기거나 NotRoad 고정
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Road Directions", "None", EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }

            // ── Multi 전용 ────────────────────────────────────────
            if (isMulti)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(multiSizeProp, new GUIContent("Item Size"));
                EditorGUILayout.PropertyField(multiCntProp,  new GUIContent("Count Per Cell"));
                EditorGUI.indentLevel--;
            }

            // ── Usage (Flags, 폴드) ───────────────────────────────
            int foldKey = (mainKeyProp.intValue << 16) | (variantKeyProp.intValue & 0xFFFF);
            if (!usageFolds.TryGetValue(foldKey, out bool usageOpen)) usageOpen = false;
            usageOpen = EditorGUILayout.Foldout(usageOpen,
                $"Usage: {DescribeUsage((PrefabUsage)usageProp.intValue)}", true);
            usageFolds[foldKey] = usageOpen;

            if (usageOpen)
            {
                EditorGUI.indentLevel++;
                var usage = (PrefabUsage)usageProp.intValue;
                usage = (PrefabUsage)EditorGUILayout.EnumFlagsField("Flags", usage);
                if ((int)usage != usageProp.intValue) usageProp.intValue = (int)usage;
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        static string DescribeUsage(PrefabUsage u)
        {
            if (u == PrefabUsage.None) return "None";
            var parts = new List<string>();
            if ((u & PrefabUsage.MapEditor)  != 0) parts.Add("MapEditor");
            if ((u & PrefabUsage.Runtime)    != 0) parts.Add("Runtime");
            if ((u & PrefabUsage.StartPoint) != 0) parts.Add("StartPoint");
            if ((u & PrefabUsage.Campaign)   != 0) parts.Add("Campaign");
            return string.Join(", ", parts);
        }

        static bool IsRoadSlot(
            SerializedProperty roadShapeProp,
            SerializedProperty roadDirsProp,
            SerializedProperty categoryProp,
            SerializedProperty objectTypeProp)
        {
            if (objectTypeProp != null
                && (PrefabObjectType)objectTypeProp.enumValueIndex == PrefabObjectType.Road)
                return true;

            if (categoryProp != null
                && (PrefabCategory)categoryProp.enumValueIndex == PrefabCategory.Road)
                return true;

            if (roadDirsProp != null
                && (RoadDirection)roadDirsProp.intValue != RoadDirection.None)
                return true;

            return roadShapeProp != null
                && (RoadShape)roadShapeProp.enumValueIndex != RoadShape.NotRoad;
        }

        // ══════════════════════════════════════════════════════════
        //  Deleted 섹션
        // ══════════════════════════════════════════════════════════
        void DrawDeletedSection(List<int> deleted, GamePrefabRegistry reg)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foldDeleted = EditorGUILayout.Foldout(
                foldDeleted, $"Deleted ({deleted.Count})", true, EditorStyles.foldoutHeader);

            if (foldDeleted)
            {
                foreach (int idx in deleted)
                {
                    var elem = itemsProp.GetArrayElementAtIndex(idx);
                    int mk   = elem.FindPropertyRelative("MainKey").intValue;
                    int vk   = elem.FindPropertyRelative("VariantKey").intValue;
                    string nm = elem.FindPropertyRelative("Name").stringValue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        $"({mk}, {vk})  {nm}", EditorStyles.miniLabel);
                    if (GUILayout.Button("Restore", GUILayout.Width(70)))
                    {
                        Undo.RecordObject(reg, "Restore Item");
                        reg.Items[idx].IsDeleted = false;
                        EditorUtility.SetDirty(reg);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  GamePrefabRegistryEditor (간소화된 인스펙터)
    //
    //  SO를 클릭했을 때 인스펙터에 기본 정보만 표시.
    //  편집은 PrefabRegistryWindow에서.
    // ══════════════════════════════════════════════════════════════
    [CustomEditor(typeof(GamePrefabRegistry))]
    public class GamePrefabRegistryEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var reg = (GamePrefabRegistry)target;

            // 기본 정보
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Game Prefab Registry", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"DLC ID: {reg.DlcId}   Name: {reg.DlcName}");
            EditorGUILayout.LabelField($"Items: {reg.Items.Count}");
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);

            // 열기 버튼 (크고 명확하게)
            GUI.backgroundColor = new Color(0.5f, 0.85f, 1f);
            if (GUILayout.Button("Open in Registry Editor", GUILayout.Height(36)))
                PrefabRegistryWindow.OpenWith(reg);
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);

            // 빠른 Export JSON
            if (!string.IsNullOrEmpty(reg.JsonExportPath))
            {
                if (GUILayout.Button("Export JSON", GUILayout.Height(26)))
                {
                    if (reg.ExportJson(out string err))
                        Debug.Log($"[{reg.DlcName}] Exported: {reg.JsonExportPath}");
                    else
                        Debug.LogError($"[{reg.DlcName}] Export failed: {err}");
                }
            }

            EditorGUILayout.Space(8);

            // 검증 메시지
            var issues = reg.Validate();
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    MessageType mt = issue.Level == ValidationLevel.Error   ? MessageType.Error
                                  :  issue.Level == ValidationLevel.Warning ? MessageType.Warning
                                                                            : MessageType.Info;
                    EditorGUILayout.HelpBox(issue.Message, mt);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Validation OK", MessageType.Info);
            }
        }
    }
}
