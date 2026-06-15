#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CitySim.MapEditor
{
    [CustomEditor(typeof(RoadPrefabRegistry))]
    public class RoadPrefabRegistryEditor : UnityEditor.Editor
    {
        SerializedProperty _entriesProp;
        SerializedProperty _defaultSizeProp;
        bool _foldEntries = true;

        void OnEnable()
        {
            _entriesProp     = serializedObject.FindProperty("Entries");
            _defaultSizeProp = serializedObject.FindProperty("DefaultSize");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_defaultSizeProp,
                new GUIContent("Default Size", "배치 시 기본 도로 크기 (한 변 셀 수). 1=1×1, 2=2×2 등."));

            EditorGUILayout.Space(6);

            _foldEntries = EditorGUILayout.Foldout(
                _foldEntries, $"Entries  ({_entriesProp.arraySize})", true, EditorStyles.foldoutHeader);

            int deleteAt = -1;

            if (_foldEntries)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < _entriesProp.arraySize; i++)
                {
                    if (DrawEntry(i))
                        deleteAt = i;
                }
                EditorGUI.indentLevel--;

                EditorGUILayout.Space(4);
                if (GUILayout.Button("+ Add Entry", GUILayout.Height(24)))
                {
                    // SerializedProperty로만 추가 — 새 슬롯을 열고 모든 필드를 기본값으로 초기화
                    int newIdx = _entriesProp.arraySize;
                    _entriesProp.arraySize = newIdx + 1;

                    var elem = _entriesProp.GetArrayElementAtIndex(newIdx);
                    elem.FindPropertyRelative("FactionId").intValue  = 0;
                    elem.FindPropertyRelative("Dir").intValue         = 0;
                    elem.FindPropertyRelative("MainKey").intValue     = 0;
                    elem.FindPropertyRelative("Note").stringValue     = "";
                }
            }

            serializedObject.ApplyModifiedProperties();

            // 삭제는 ApplyModifiedProperties 이후에 처리
            if (deleteAt >= 0)
            {
                serializedObject.Update();
                _entriesProp.DeleteArrayElementAtIndex(deleteAt);
                serializedObject.ApplyModifiedProperties();
            }

            // 검증
            EditorGUILayout.Space(6);
            var reg    = (RoadPrefabRegistry)target;
            var issues = reg.Validate();
            if (issues.Count == 0)
            {
                EditorGUILayout.HelpBox("Validation OK", MessageType.Info);
            }
            else
            {
                foreach (var issue in issues)
                {
                    var mt = issue.Level == ValidationLevel.Error   ? MessageType.Error
                           : issue.Level == ValidationLevel.Warning ? MessageType.Warning
                                                                    : MessageType.Info;
                    EditorGUILayout.HelpBox(issue.Message, mt);
                }
            }
        }

        /// <returns>true이면 이 항목 삭제 요청</returns>
        bool DrawEntry(int i)
        {
            var elem     = _entriesProp.GetArrayElementAtIndex(i);
            var factionP = elem.FindPropertyRelative("FactionId");
            var dirP     = elem.FindPropertyRelative("Dir");
            var mainKeyP = elem.FindPropertyRelative("MainKey");
            var noteP    = elem.FindPropertyRelative("Note");

            bool wantsDelete = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"[{i}]", EditorStyles.miniBoldLabel, GUILayout.Width(28));
            GUILayout.FlexibleSpace();
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("X", GUILayout.Width(22)))
                wantsDelete = true;
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(factionP,
                new GUIContent("Faction ID", "0 = 공통(폴백), 1~8 = 개별 팩션"));
            EditorGUILayout.PropertyField(dirP,
                new GUIContent("Dir", "도로 방향 비트마스크 (1~15). N=1,E=2,S=4,W=8"));
            EditorGUILayout.PropertyField(mainKeyP,
                new GUIContent("Main Key", "Road 범위 1~999"));
            EditorGUILayout.PropertyField(noteP,
                new GUIContent("Note", "메모 (선택)"));

            EditorGUILayout.EndVertical();

            return wantsDelete;
        }
    }
}
#endif
