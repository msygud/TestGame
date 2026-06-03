#if UNITY_EDITOR
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
    //    - 여러 GamePrefabRegistry SO 드롭다운 전환
    //    - 항목 편집 (Name, Key, Prefab, Size, Offset, RoadMask, Usage, Category)
    //    - Refresh (단일 SO 검증) + Export JSON
    //    - Deleted 섹션 (복원 가능)
    //    - Multi 전용 옵션 (Count, ItemSize)
    //    - 도로 항목: Category == Road 판정, Size = (1,1) 강제
    // ══════════════════════════════════════════════════════════════
    public class PrefabRegistryWindow : EditorWindow
    {
        // ── 대상 SO ───────────────────────────────────────────────
        List<GamePrefabRegistry> registries = new();
        int selectedRegistryIdx = 0;

        // ── 내부 SerializedObject ─────────────────────────────────
        SerializedObject   serializedReg;
        SerializedProperty itemsProp;
        SerializedProperty dlcIdProp;
        SerializedProperty dlcNameProp;
        SerializedProperty displayNameProp;
        SerializedProperty jsonExportPathProp;

        // ── UI 상태 ────────────────────────────────────────────────
        Vector2 scrollPos;
        bool foldDlc        = true;
        bool foldRoad        = true;
        bool foldBuilding    = true;
        bool foldEnvironment = true;
        bool foldCombatUnit  = true;
        bool foldProjectile  = true;
        bool foldEffect      = true;
        bool foldOther       = true;
        bool foldDeleted     = false;
        bool showThumbs     = true;

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

        public static void OpenWith(GamePrefabRegistry registry)
        {
            var w = GetWindow<PrefabRegistryWindow>("Prefab Registry");
            w.minSize = new Vector2(420, 600);

            int idx = w.registries.IndexOf(registry);
            if (idx < 0) { w.registries.Add(registry); idx = w.registries.Count - 1; }
            w.SelectRegistry(idx);
            w.Show();
        }

        void OnEnable() => AutoDiscoverRegistries();

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
        //  상단 바
        // ══════════════════════════════════════════════════════════
        void DrawTopBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var names = new string[registries.Count];
            for (int i = 0; i < registries.Count; i++)
                names[i] = registries[i] != null
                    ? $"{registries[i].dlcName} (DLC {registries[i].dlcId})"
                    : "(null)";

            int newIdx = EditorGUILayout.Popup(selectedRegistryIdx, names,
                GUILayout.MinWidth(200));
            if (newIdx != selectedRegistryIdx) SelectRegistry(newIdx);

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
                EditorGUILayout.PropertyField(dlcIdProp,      new GUIContent("DLC ID"));
                EditorGUILayout.PropertyField(dlcNameProp,    new GUIContent("DLC Name"));
                EditorGUILayout.PropertyField(displayNameProp,
                    new GUIContent("Display Name", "맵에디터/UI 표시용. 비면 DLC Name 사용."));

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("JSON Export", EditorStyles.miniBoldLabel);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(jsonExportPathProp, new GUIContent("Export Path"));
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
        //  툴바
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
                    Debug.Log($"[{reg.dlcName}] Validation OK.");
                else
                    foreach (var issue in issues)
                        Debug.LogWarning($"[{reg.dlcName}] {issue.Message}");
            }

            if (GUILayout.Button("Export JSON", GUILayout.Height(26), GUILayout.Width(110)))
            {
                serializedReg.ApplyModifiedProperties();
                if (reg.ExportJson(out string err))
                    Debug.Log($"[{reg.dlcName}] Exported: {reg.jsonExportPath}");
                else
                    Debug.LogError($"[{reg.dlcName}] Export failed: {err}");
            }

            showThumbs = GUILayout.Toggle(showThumbs, "Thumbs", "Button",
                GUILayout.Width(60), GUILayout.Height(26));

            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════
        //  항목 그룹 분류
        //  대분류: 터레인 / 유닛 / 투사체 / 이펙트 / 로드 / 그 외
        //  Road 판정: Category == Road (RoadMask가 아닌 Category 기준)
        // ══════════════════════════════════════════════════════════
        void DrawItemGroups()
        {
            var reg = registries[selectedRegistryIdx];
            if (reg == null) return;

            var roads        = new List<int>();
            var buildings    = new List<int>();
            var environments = new List<int>();
            var combatUnits  = new List<int>();
            var projectiles  = new List<int>();
            var effects      = new List<int>();
            var others       = new List<int>();
            var deleted      = new List<int>();

            for (int i = 0; i < itemsProp.arraySize; i++)
            {
                var elem = itemsProp.GetArrayElementAtIndex(i);
                if (elem.FindPropertyRelative("IsDeleted").boolValue)
                { deleted.Add(i); continue; }

                var cat = (PrefabCategory)elem.FindPropertyRelative("Category").enumValueIndex;
                switch (cat)
                {
                    case PrefabCategory.Road:        roads.Add(i);        break;
                    case PrefabCategory.Building:    buildings.Add(i);    break;
                    case PrefabCategory.Environment: environments.Add(i); break;
                    case PrefabCategory.CombatUnit:  combatUnits.Add(i);  break;
                    case PrefabCategory.Projectile:  projectiles.Add(i);  break;
                    case PrefabCategory.Effect:      effects.Add(i);      break;
                    default:                         others.Add(i);       break;
                }
            }

            DrawGroup("도로 (Road)", roads, reg, ref foldRoad,
                () =>
                {
                    var item      = reg.AddItem();
                    item.Category = PrefabCategory.Road;
                    item.Size     = new Vector2Int(1, 1);
                    // RoadMask는 사용자가 직접 설정 (기본 None = 미설정)
                });
            EditorGUILayout.Space(5);

            DrawGroup("건물 (Building)", buildings, reg, ref foldBuilding,
                () => { var item = reg.AddItem(); item.Category = PrefabCategory.Building; });
            EditorGUILayout.Space(5);

            DrawGroup("환경 (Environment)", environments, reg, ref foldEnvironment,
                () => { var item = reg.AddItem(); item.Category = PrefabCategory.Environment; });
            EditorGUILayout.Space(5);

            DrawGroup("전투유닛 (CombatUnit)", combatUnits, reg, ref foldCombatUnit,
                () => { var item = reg.AddItem(); item.Category = PrefabCategory.CombatUnit; });
            EditorGUILayout.Space(5);

            DrawGroup("투사체 (Projectile)", projectiles, reg, ref foldProjectile,
                () => { var item = reg.AddItem(); item.Category = PrefabCategory.Projectile; });
            EditorGUILayout.Space(5);

            DrawGroup("이펙트 (Effect)", effects, reg, ref foldEffect,
                () => { var item = reg.AddItem(); item.Category = PrefabCategory.Effect; });
            EditorGUILayout.Space(5);

            DrawGroup("그 외 (Other)", others, reg, ref foldOther,
                () => { var item = reg.AddItem(); item.Category = PrefabCategory.Other; });

            if (deleted.Count > 0)
            { EditorGUILayout.Space(10); DrawDeletedSection(deleted, reg); }
        }

        void DrawGroup(string title, List<int> indices, GamePrefabRegistry reg,
            ref bool foldout, System.Action onAdd)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foldout = EditorGUILayout.Foldout(
                foldout, $"{title}  ({indices.Count})", true, EditorStyles.foldoutHeader);

            if (foldout)
            {
                foreach (int idx in indices) DrawItem(idx, reg);

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
            var sizeProp       = elem.FindPropertyRelative("Size");
            var offsetProp     = elem.FindPropertyRelative("Offset");
            var roadMaskProp   = elem.FindPropertyRelative("RoadMask");   // RoadDir 비트마스크
            var multiSizeProp  = elem.FindPropertyRelative("MultiItemSize");
            var multiCntProp   = elem.FindPropertyRelative("MultiCountPerCell");
            var buildableOnProp   = elem.FindPropertyRelative("BuildableOn");

            var category = (PrefabCategory)categoryProp.enumValueIndex;
            bool isRoad     = category == PrefabCategory.Road;
            bool isMulti    = category == PrefabCategory.Environment;
            bool isBuilding = category == PrefabCategory.Building;

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

            // ── 1행: Name + 키들 + 삭제 ──────────────────────────
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(nameProp, GUIContent.none, GUILayout.MinWidth(90));
            EditorGUILayout.LabelField("M", GUILayout.Width(13));
            EditorGUILayout.PropertyField(mainKeyProp,    GUIContent.none, GUILayout.Width(52));
            EditorGUILayout.LabelField("V", GUILayout.Width(13));
            EditorGUILayout.PropertyField(variantKeyProp, GUIContent.none, GUILayout.Width(42));
            EditorGUILayout.LabelField("D", GUILayout.Width(13));
            EditorGUILayout.PropertyField(dlcKeyProp,     GUIContent.none, GUILayout.Width(36));

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("X", GUILayout.Width(22)))
            {
                GUI.backgroundColor = prevBg;
                if (EditorUtility.DisplayDialog("Mark Deleted",
                    $"Mark '{nameProp.stringValue}' ({mainKeyProp.intValue},{variantKeyProp.intValue}) as deleted?",
                    "Mark Deleted", "Cancel"))
                {
                    Undo.RecordObject(reg, "Mark Deleted");
                    reg.items[index].IsDeleted = true;
                    EditorUtility.SetDirty(reg);
                }
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            // ── 2행: Prefab ───────────────────────────────────────
            EditorGUILayout.PropertyField(prefabProp, new GUIContent("Prefab"));

            // ── 3행: Category ─────────────────────────────────────
            EditorGUILayout.PropertyField(categoryProp, new GUIContent("Category"));

            // ── 4행: 스폰 방식 안내 (Category에서 자동 결정) ─────────
            if (!isRoad)
            {
                string mode = isMulti
                    ? "Environment → 셀 내 랜덤 다중"
                    : (isBuilding ? "Building → footprint 1:1 (입구 보유)"
                                  : "단일 1:1");
                EditorGUILayout.LabelField("Spawn", mode, EditorStyles.miniLabel);
            }

            // ── 5행: BuildableOn (도로 아닐 때) ──────────────────────
            if (!isRoad)
            {
                var buildableOn = (TerrainMask)buildableOnProp.intValue;
                EditorGUI.BeginChangeCheck();
                buildableOn = (TerrainMask)EditorGUILayout.EnumFlagsField(
                    new GUIContent("Buildable On",
                        "배치 가능한 지형. Land=땅, Water=물, Any=모두."),
                    buildableOn);
                if (EditorGUI.EndChangeCheck())
                    buildableOnProp.intValue = (int)buildableOn;
            }

            // ── 6행: Size / Offset ────────────────────────────────
            if (!isRoad && !isMulti)
                EditorGUILayout.PropertyField(sizeProp, new GUIContent("Size (XZ cells)"));
            else if (isRoad)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Vector2IntField("Size (fixed 1x1)", new Vector2Int(1, 1));
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Size", "1x1 (Multi fixed)", EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.PropertyField(offsetProp, new GUIContent("Offset"));

            // ── 7행: Road Mask (도로만) ───────────────────────────
            if (isRoad)
            {
                // RoadDir는 [Flags] enum이므로 EnumFlagsField 사용
                var roadDir = (RoadDir)roadMaskProp.intValue;
                EditorGUI.BeginChangeCheck();
                roadDir = (RoadDir)EditorGUILayout.EnumFlagsField(
                    new GUIContent("Road Mask",
                        "비트마스크(1~15). N=1, E=2, S=4, W=8. 조합이 프리팹 1:1 매핑."),
                    roadDir);
                if (EditorGUI.EndChangeCheck())
                    roadMaskProp.intValue = (int)roadDir;

                // VariantKey를 RoadMask와 동기화 (도로: VariantKey == 비트마스크)
                if (variantKeyProp.intValue != (int)roadDir)
                {
                    variantKeyProp.intValue = (int)roadDir;
                }
            }

            // ── 8행: Multi 전용 ────────────────────────────────────
            if (isMulti)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(multiSizeProp, new GUIContent("Item Size"));
                EditorGUILayout.PropertyField(multiCntProp,  new GUIContent("Count Per Cell"));
                EditorGUI.indentLevel--;
            }

            // ── 8.5행: 입구 (Building 전용, VariantKey=0에서만 편집) ──
            //  입구는 MainKey 단위 게임플레이 속성이므로 GamePrefabRegistry.Entrances에 저장.
            //  변형마다 중복 입력 방지를 위해 대표(VariantKey=0)에서만 편집 노출.
            if (isBuilding)
            {
                if (variantKeyProp.intValue == 0)
                    DrawEntranceEditor(reg, mainKeyProp.intValue);
                else
                    EditorGUILayout.LabelField(
                        "Entrance", "MainKey 대표(V0)에서 편집", EditorStyles.miniLabel);
            }

            // ── 9행: Usage (Flags, 폴드) ─────────────────────────
            int foldKey = (mainKeyProp.intValue << 16) | (variantKeyProp.intValue & 0xFFFF);
            if (!usageFolds.TryGetValue(foldKey, out bool usageOpen)) usageOpen = false;
            usageOpen = EditorGUILayout.Foldout(usageOpen,
                $"Usage: {(PrefabUsage)usageProp.intValue}", true);
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

        // ══════════════════════════════════════════════════════════
        //  입구 편집 (Building MainKey 단위)
        //
        //  GamePrefabRegistry.Entrances에서 해당 MainKey 항목을 찾아
        //  Offsets(footprint 원점 기준 셀)를 추가/삭제한다.
        // ══════════════════════════════════════════════════════════
        void DrawEntranceEditor(GamePrefabRegistry reg, int mainKey)
        {
            // ══════════════════════════════════════════════════════════
            //  입구 편집 (Building MainKey 단위, 단일 입구 + 방향)
            //
            //  GamePrefabRegistry.Entrances에서 해당 MainKey 항목을 찾아
            //  Offset(footprint 원점=좌하단 기준 셀) + Dir(입구 방향)을 편집한다.
            //  단일 입구 규약: MainKey당 입구 1개.
            // ══════════════════════════════════════════════════════════
            void DrawEntranceEditor(GamePrefabRegistry reg, int mainKey)
            {
                var ent = reg.GetEntrance(mainKey);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("입구 (Entrance)", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "footprint 원점(좌하단) 기준 셀 + 바라보는 방향. 회전 0° 기준.",
                    EditorStyles.miniLabel);

                if (ent == null)
                {
                    // 입구 미정의 → 추가 버튼만
                    if (GUILayout.Button("+ 입구 설정", GUILayout.Width(100)))
                    {
                        Undo.RecordObject(reg, "Add Entrance");
                        reg.Entrances.Add(new EntranceEntry
                        {
                            MainKey = mainKey,
                            Offset = Vector2Int.zero,
                            Dir = RoadDir.S,
                        });
                        EditorUtility.SetDirty(reg);
                    }
                }
                else
                {
                    // Offset 편집
                    EditorGUI.BeginChangeCheck();
                    var off = EditorGUILayout.Vector2IntField(
                        new GUIContent("Offset (cell)"), ent.Offset);

                    // Dir 편집 — 단일 비트 강제 (EnumPopup, Flags 아님)
                    var dir = (RoadDir)EditorGUILayout.EnumPopup(
                        new GUIContent("Dir", "입구가 바라보는 방향 (단일: N/E/S/W)"),
                        ent.Dir);

                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(reg, "Edit Entrance");
                        ent.Offset = off;
                        ent.Dir = dir;
                        EditorUtility.SetDirty(reg);
                    }

                    // 방향이 단일 비트가 아니면 경고 (None이나 복합 선택 방지)
                    if (RoadDirOps.PopCount(ent.Dir) != 1)
                    {
                        EditorGUILayout.HelpBox(
                            "Dir은 N/E/S/W 중 하나여야 합니다.", MessageType.Error);
                    }

                    // 제거
                    var prevBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
                    if (GUILayout.Button("입구 제거", GUILayout.Width(100)))
                    {
                        Undo.RecordObject(reg, "Remove Entrance");
                        reg.Entrances.Remove(ent);
                        EditorUtility.SetDirty(reg);
                    }
                    GUI.backgroundColor = prevBg;
                }

                EditorGUILayout.EndVertical();
            }
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
                    EditorGUILayout.LabelField($"({mk}, {vk})  {nm}", EditorStyles.miniLabel);
                    if (GUILayout.Button("Restore", GUILayout.Width(70)))
                    {
                        Undo.RecordObject(reg, "Restore Item");
                        reg.items[idx].IsDeleted = false;
                        EditorUtility.SetDirty(reg);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
        }
    }
}
#endif