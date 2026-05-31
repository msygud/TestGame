#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  GamePrefabRegistryEditor
    //
    //  SO 인스펙터 커스텀 (간소화).
    //  실제 편집은 PrefabRegistryWindow에서 수행.
    //  인스펙터에는 기본 정보 + "Open in Registry Editor" 버튼만 표시.
    // ══════════════════════════════════════════════════════════════
    [CustomEditor(typeof(GamePrefabRegistry))]
    public class GamePrefabRegistryEditor : UnityEditor.Editor
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

            // 편집 창 열기
            GUI.backgroundColor = new Color(0.5f, 0.85f, 1f);
            if (GUILayout.Button("Open in Registry Editor", GUILayout.Height(36)))
                PrefabRegistryWindow.OpenWith(reg);
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);

            // 빠른 Export JSON
            if (!string.IsNullOrEmpty(reg.jsonExportPath))
            {
                if (GUILayout.Button("Export JSON", GUILayout.Height(26)))
                {
                    if (reg.ExportJson(out string err))
                        Debug.Log($"[{reg.dlcName}] Exported: {reg.jsonExportPath}");
                    else
                        Debug.LogError($"[{reg.dlcName}] Export failed: {err}");
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
#endif
