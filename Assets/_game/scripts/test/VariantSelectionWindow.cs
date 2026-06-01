#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  VariantSelectionWindow
    //
    //  스커미쉬 세션 베리언트 선택 창.
    //  메뉴: Tools > CitySim > Variant Selection
    //
    //  흐름:
    //    1. VariantSettings SO 지정 (drag-in 또는 새로 생성)
    //    2. 프로젝트의 GamePrefabRegistry SO 자동 스캔
    //    3. 베리언트가 있는 유닛을 MainKey 그룹별로 표시
    //    4. User 행 / AI 행 별도 선택 → 동시 선택 가능
    //    5. 변경 즉시 SO에 반영 (EditorUtility.SetDirty)
    //
    //  표시 조건:
    //    같은 MainKey에 VariantKey > 0 항목이 하나라도 있는 그룹만 표시.
    //    도로(IsRoad) 항목은 제외.
    // ══════════════════════════════════════════════════════════════
    public class VariantSelectionWindow : EditorWindow
    {
        // ── 데이터 ─────────────────────────────────────────────────
        VariantSettings _settings;
        List<GamePrefabRegistry> _registries = new();

        // MainKey → 같은 MainKey의 RegistryItem 목록 (VariantKey 오름차순)
        Dictionary<int, List<RegistryItem>> _groups = new();

        // ── UI 상태 ───────────────────────────────────────────────
        Vector2 _scroll;
        bool    _needsScan = true;

        // 미리보기 텍스처 캐시 (비동기 로딩 대응)
        readonly Dictionary<GameObject, Texture2D> _previewCache = new();

        // ── 상수 ─────────────────────────────────────────────────
        const float  kThumbSize   = 58f;
        const string kPrefsKey    = "CitySim_VariantWindow_SettingsGuid";

        static readonly Color kSelectedColor  = new(0.35f, 0.75f, 1f, 1f);
        static readonly Color kUserLabelColor = new(0.5f, 0.85f, 1f, 1f);
        static readonly Color kAILabelColor   = new(1f, 0.75f, 0.4f, 1f);

        // ══════════════════════════════════════════════════════════
        //  메뉴 / 생명주기
        // ══════════════════════════════════════════════════════════

        [MenuItem("Tools/CitySim/Variant Selection")]
        public static void Open()
            => GetWindow<VariantSelectionWindow>("Variant Selection");

        void OnEnable()
        {
            // 마지막 SO 복원
            var guid = EditorPrefs.GetString(kPrefsKey, "");
            if (!string.IsNullOrEmpty(guid))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                _settings = AssetDatabase.LoadAssetAtPath<VariantSettings>(path);
            }
            _needsScan = true;
        }

        void OnDisable()
            => _previewCache.Clear();

        // 비동기 미리보기 로딩 중 Repaint
        void Update()
        {
            if (AssetPreview.IsLoadingAssetPreviews())
                Repaint();
        }

        // ══════════════════════════════════════════════════════════
        //  OnGUI
        // ══════════════════════════════════════════════════════════

        void OnGUI()
        {
            if (_needsScan)
            {
                ScanRegistries();
                _needsScan = false;
            }

            DrawHeader();

            if (_settings == null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "VariantSettings SO를 지정하거나 새로 생성하세요.",
                    MessageType.Info);
                return;
            }

            if (_groups.Count == 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "베리언트가 있는 유닛/건물이 없습니다.\n" +
                    "GamePrefabRegistry에 VariantKey > 0 항목을 추가하세요.",
                    MessageType.Warning);
                return;
            }

            DrawBody();
            DrawFooter();
        }

        // ══════════════════════════════════════════════════════════
        //  헤더
        // ══════════════════════════════════════════════════════════

        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // VariantSettings SO 필드
            EditorGUI.BeginChangeCheck();
            var next = (VariantSettings)EditorGUILayout.ObjectField(
                _settings, typeof(VariantSettings), false,
                GUILayout.Width(210));
            if (EditorGUI.EndChangeCheck())
            {
                _settings = next;
                if (_settings != null)
                {
                    var path = AssetDatabase.GetAssetPath(_settings);
                    EditorPrefs.SetString(kPrefsKey,
                        AssetDatabase.AssetPathToGUID(path));
                }
            }

            // 새 SO 생성
            if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(38)))
                CreateNewSettings();

            GUILayout.FlexibleSpace();

            // 레지스트리 재스캔
            if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(44)))
            {
                _previewCache.Clear();
                ScanRegistries();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════
        //  본문
        // ══════════════════════════════════════════════════════════

        void DrawBody()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            foreach (var kv in _groups.OrderBy(x => x.Key))
            {
                int  mainKey  = kv.Key;
                var  variants = kv.Value;   // VariantKey 오름차순

                // 대표 이름 (VariantKey=0 우선, 없으면 첫 번째)
                var base0 = variants.FirstOrDefault(v => v.VariantKey == 0);
                string label = base0 != null && !string.IsNullOrEmpty(base0.Name)
                    ? base0.Name
                    : (variants[0].Name ?? $"MainKey={mainKey}");

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // 그룹 헤더
                EditorGUILayout.LabelField(
                    $"{label}  (MainKey = {mainKey})",
                    EditorStyles.boldLabel);

                EditorGUILayout.Space(2);

                // User 행
                DrawControllerRow(mainKey, variants, SlotController.User,
                    "User", kUserLabelColor);

                EditorGUILayout.Space(1);

                // AI 행
                DrawControllerRow(mainKey, variants, SlotController.AI,
                    "AI  ", kAILabelColor);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(3);
            }

            EditorGUILayout.EndScrollView();
        }

        // ── 컨트롤러별 베리언트 행 ────────────────────────────────

        void DrawControllerRow(
            int mainKey,
            List<RegistryItem> variants,
            SlotController who,
            string rowLabel,
            Color labelColor)
        {
            int currentVk = _settings.Resolve(mainKey, who);

            EditorGUILayout.BeginHorizontal();

            // 행 레이블 (User / AI)
            var prevColor = GUI.color;
            GUI.color = labelColor;
            GUILayout.Label(rowLabel,
                EditorStyles.boldLabel,
                GUILayout.Width(36),
                GUILayout.Height(kThumbSize));
            GUI.color = prevColor;

            // 베리언트 썸네일 버튼들
            foreach (var item in variants)
            {
                bool isSelected = item.VariantKey == currentVk;

                // 선택된 항목 하이라이트
                var bgPrev = GUI.backgroundColor;
                if (isSelected) GUI.backgroundColor = kSelectedColor;

                var thumb   = GetPreview(item.Prefab);
                var content = thumb != null
                    ? new GUIContent(thumb,
                        $"V{item.VariantKey}" +
                        (string.IsNullOrEmpty(item.Name) ? "" : $"\n{item.Name}"))
                    : new GUIContent(
                        $"V{item.VariantKey}",
                        string.IsNullOrEmpty(item.Name) ? "" : item.Name);

                if (GUILayout.Button(content,
                    isSelected ? GUI.skin.box : GUI.skin.button,
                    GUILayout.Width(kThumbSize),
                    GUILayout.Height(kThumbSize)))
                {
                    // 이미 선택된 것을 재클릭 → 기본값(0)으로 리셋
                    int newVk = isSelected ? 0 : item.VariantKey;
                    Undo.RecordObject(_settings, "Set Variant");
                    _settings.Set(mainKey, newVk, who);
                    EditorUtility.SetDirty(_settings);
                    Repaint();
                }

                GUI.backgroundColor = bgPrev;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ══════════════════════════════════════════════════════════
        //  푸터
        // ══════════════════════════════════════════════════════════

        void DrawFooter()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("모두 초기화", GUILayout.Width(90)))
            {
                if (EditorUtility.DisplayDialog(
                    "베리언트 초기화",
                    "모든 유닛·건물의 베리언트를 기본값(V0)으로 초기화합니다.",
                    "확인", "취소"))
                {
                    Undo.RecordObject(_settings, "Clear All Variants");
                    _settings.ClearAll();
                    EditorUtility.SetDirty(_settings);
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        // ══════════════════════════════════════════════════════════
        //  레지스트리 스캔
        // ══════════════════════════════════════════════════════════

        void ScanRegistries()
        {
            _registries.Clear();
            _groups.Clear();

            // 프로젝트 내 모든 GamePrefabRegistry SO 탐색
            foreach (var guid in AssetDatabase.FindAssets("t:GamePrefabRegistry"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var reg  = AssetDatabase.LoadAssetAtPath<GamePrefabRegistry>(path);
                if (reg != null) _registries.Add(reg);
            }

            // MainKey 그룹화 (삭제/도로/null 제외)
            var raw = new Dictionary<int, List<RegistryItem>>();
            foreach (var reg in _registries)
            {
                foreach (var item in reg.Items)
                {
                    if (item.IsDeleted || item.Prefab == null) continue;
                    if (item.IsRoad) continue;

                    if (!raw.TryGetValue(item.MainKey, out var list))
                    {
                        list = new List<RegistryItem>();
                        raw[item.MainKey] = list;
                    }
                    list.Add(item);
                }
            }

            // VariantKey > 0 가 있는 그룹만 표시 (선택지가 있어야 의미 있음)
            foreach (var kv in raw)
            {
                var sorted = kv.Value.OrderBy(i => i.VariantKey).ToList();
                if (sorted.Any(i => i.VariantKey > 0))
                    _groups[kv.Key] = sorted;
            }

            Repaint();
        }

        // ══════════════════════════════════════════════════════════
        //  헬퍼
        // ══════════════════════════════════════════════════════════

        void CreateNewSettings()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "새 Variant Settings 저장",
                "VariantSettings", "asset",
                "저장 위치를 선택하세요.");
            if (string.IsNullOrEmpty(path)) return;

            var so = CreateInstance<VariantSettings>();
            AssetDatabase.CreateAsset(so, path);
            AssetDatabase.SaveAssets();

            _settings = so;
            EditorPrefs.SetString(kPrefsKey,
                AssetDatabase.AssetPathToGUID(path));
            Repaint();
        }

        Texture2D GetPreview(GameObject prefab)
        {
            if (prefab == null) return null;
            if (_previewCache.TryGetValue(prefab, out var tex) && tex != null)
                return tex;

            tex = AssetPreview.GetAssetPreview(prefab);
            if (tex != null)
                _previewCache[prefab] = tex;

            return tex;
        }
    }
}
#endif
