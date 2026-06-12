#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace CitySim.Editor
{
    // ══════════════════════════════════════════════════════════════
    //  AddressableGroupUtility
    //
    //  GamePrefabRegistry SO를 기반으로 Addressables 그룹을
    //  자동으로 구성하는 에디터 유틸리티.
    //
    //  주요 기능:
    //    1. DlcName 기준으로 그룹 자동 생성/재사용
    //    2. SubScene을 해당 그룹에 등록
    //    3. Registry SO를 해당 그룹에 등록 (label="registry")
    //    4. Origin = Local, DLC = Remote 빌드 경로 자동 설정
    //
    //  호출:
    //    - PrefabRegistryWindow의 "Setup Addressables" 버튼
    //    - 메뉴: Tools > CitySim > Setup Addressable Groups
    // ══════════════════════════════════════════════════════════════
    public static class AddressableGroupUtility
    {
        // ── 메뉴 진입점 ──────────────────────────────────────────────

        [MenuItem("Tools/CitySim/Setup Addressable Groups")]
        public static void SetupFromMenu()
        {
            // 프로젝트의 모든 GamePrefabRegistry SO 수집
            var guids = AssetDatabase.FindAssets("t:GamePrefabRegistry");
            var registries = new List<GamePrefabRegistry>(guids.Length);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var reg  = AssetDatabase.LoadAssetAtPath<GamePrefabRegistry>(path);
                if (reg != null) registries.Add(reg);
            }

            if (registries.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "CitySim",
                    "GamePrefabRegistry SO가 없습니다.\nAssets > Create > CitySim > Game Prefab Registry",
                    "확인");
                return;
            }

            SetupGroups(registries);
            EditorUtility.DisplayDialog("CitySim", "Addressable 그룹 설정 완료.", "확인");
        }

        // ── 핵심 로직 ────────────────────────────────────────────────

        /// <summary>
        /// Registry SO 목록을 기반으로 Addressable 그룹을 생성/갱신한다.
        /// </summary>
        public static void SetupGroups(IReadOnlyList<GamePrefabRegistry> registries)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[CitySim] Addressable Asset Settings를 찾을 수 없습니다. " +
                               "Window > Asset Management > Addressables > Groups에서 초기화하세요.");
                return;
            }

            // "registry" 라벨이 없으면 생성
            EnsureLabel(settings, DlcAddressConfig.LabelRegistry);
            EnsureLabel(settings, DlcAddressConfig.LabelMapMeta);
            EnsureLabel(settings, DlcAddressConfig.LabelMapData);

            foreach (var reg in registries)
            {
                if (reg == null || string.IsNullOrEmpty(reg.DlcName)) continue;

                bool isOrigin = reg.DlcId == 0;
                var  group    = EnsureGroup(settings, reg.DlcName, isOrigin);

                // Registry SO 등록
                RegisterAsset(settings, group, reg,
                    address: DlcAddressConfig.Registry(reg.DlcName),
                    labels:  new[] { DlcAddressConfig.LabelRegistry,
                                     DlcAddressConfig.LabelDlc(reg.DlcName) });

                Debug.Log($"[CitySim] 그룹 '{reg.DlcName}' 설정 완료 " +
                          $"(isOrigin={isOrigin}).");
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// SubScene을 해당 DLC 그룹에 등록한다.
        /// PrefabRegistryWindow 또는 별도 버튼에서 호출.
        /// </summary>
        public static void RegisterSubScene(GameObject subSceneAsset, string dlcName)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            bool isOrigin = dlcName == DlcAddressConfig.OriginGroupName;
            var  group    = EnsureGroup(settings, dlcName, isOrigin);

            RegisterAsset(settings, group, subSceneAsset,
                address: DlcAddressConfig.SubScene(dlcName),
                labels:  new[] { DlcAddressConfig.LabelDlc(dlcName) });

            AssetDatabase.SaveAssets();
            Debug.Log($"[CitySim] SubScene '{subSceneAsset.name}' → 그룹 '{dlcName}' 등록 완료.");
        }

        /// <summary>
        /// 맵 파일(TextAsset)을 Maps 그룹에 등록한다.
        /// 맵 저장 시 MapEditorWindow에서 호출.
        /// </summary>
        public static void RegisterMapFiles(
            Object metaAsset, string mapId,
            Object dataAsset)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            // Maps 그룹 (로컬 또는 별도 원격 그룹)
            var mapsGroup = EnsureGroup(settings, "Maps", isLocal: true);

            EnsureLabel(settings, DlcAddressConfig.LabelMapMeta);
            EnsureLabel(settings, DlcAddressConfig.LabelMapData);

            RegisterAsset(settings, mapsGroup, metaAsset,
                address: DlcAddressConfig.MapMeta(mapId),
                labels:  new[] { DlcAddressConfig.LabelMapMeta });

            RegisterAsset(settings, mapsGroup, dataAsset,
                address: DlcAddressConfig.MapData(mapId),
                labels:  new[] { DlcAddressConfig.LabelMapData });

            AssetDatabase.SaveAssets();
        }

        // ── 내부 헬퍼 ────────────────────────────────────────────────

        /// <summary>그룹이 없으면 생성, 있으면 반환. 빌드 경로도 설정.</summary>
        static AddressableAssetGroup EnsureGroup(
            AddressableAssetSettings settings,
            string groupName,
            bool   isLocal)
        {
            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                group = settings.CreateGroup(
                    groupName,
                    setAsDefaultGroup: false,
                    readOnly:          false,
                    postEvent:         true,
                    schemasToCopy:     null,
                    typeof(BundledAssetGroupSchema),
                    typeof(ContentUpdateGroupSchema));

                Debug.Log($"[CitySim] Addressable 그룹 생성: '{groupName}'");
            }

            // 빌드/로드 경로 설정
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema != null)
            {
                if (isLocal)
                {
                    schema.BuildPath.SetVariableByName(
                        settings, AddressableAssetSettings.kLocalBuildPath);
                    schema.LoadPath.SetVariableByName(
                        settings, AddressableAssetSettings.kLocalLoadPath);
                }
                else
                {
                    // DLC: 원격 빌드 경로
                    schema.BuildPath.SetVariableByName(
                        settings, AddressableAssetSettings.kRemoteBuildPath);
                    schema.LoadPath.SetVariableByName(
                        settings, AddressableAssetSettings.kRemoteLoadPath);

                    // DLC는 별도 카탈로그 빌드
                    var contentSchema = group.GetSchema<ContentUpdateGroupSchema>();
                    if (contentSchema != null)
                        contentSchema.StaticContent = false;
                }
            }

            return group;
        }

        /// <summary>에셋을 그룹에 등록하고 주소/라벨을 설정한다.</summary>
        static void RegisterAsset(
            AddressableAssetSettings settings,
            AddressableAssetGroup    group,
            Object                   asset,
            string                   address,
            string[]                 labels = null)
        {
            if (asset == null) return;

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogWarning($"[CitySim] 에셋 경로를 찾을 수 없습니다: {asset.name}");
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            var entry   = settings.CreateOrMoveEntry(guid, group, readOnly: false, postEvent: false);

            entry.address = address;

            if (labels != null)
            {
                foreach (var label in labels)
                {
                    EnsureLabel(settings, label);
                    entry.SetLabel(label, enable: true, force: true, postEvent: false);
                }
            }
        }

        /// <summary>라벨이 없으면 생성.</summary>
        static void EnsureLabel(AddressableAssetSettings settings, string label)
        {
            var labels = settings.GetLabels();
            if (!labels.Contains(label))
                settings.AddLabel(label);
        }
    }
}
#endif
