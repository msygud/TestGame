#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using Unity.Scenes;
using UnityEngine;

namespace CitySim.EditorTools
{
    public static class ContentModuleAddressablesValidator
    {
        [MenuItem("CitySim/Content Modules/Validate Addressables Setup")]
        public static void ValidateAddressablesSetup()
        {
            var loaders = FindSceneLoaders();
            if (loaders.Count == 0)
            {
                Debug.LogWarning("[ContentModuleValidator] No AddressableContentModuleLoader found in open scenes.");
                return;
            }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            int issueCount = 0;

            if (settings == null)
            {
                Debug.LogError("[ContentModuleValidator] AddressableAssetSettings not found.");
                return;
            }

            foreach (var loader in loaders)
                issueCount += ValidateLoader(loader, settings);

            issueCount += ValidatePrefabRegistryAuthoringPerScene();

            if (issueCount == 0)
                Debug.Log($"[ContentModuleValidator] Validation OK. Loaders checked: {loaders.Count}.");
            else
                Debug.LogWarning($"[ContentModuleValidator] Validation finished with {issueCount} issue(s).");
        }

        [MenuItem("CitySim/Content Modules/Sync SubScene Prefab References")]
        public static void SyncSubScenePrefabReferences()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[ContentModuleValidator] AddressableAssetSettings not found.");
                return;
            }

            int changedCount = 0;
            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null
                        || entry.IsFolder
                        || !entry.AssetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    changedCount += SyncSubScenePrefabReference(entry.AssetPath) ? 1 : 0;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[ContentModuleValidator] Synced SubScene prefab references. Changed prefabs: {changedCount}.");
        }

        static List<AddressableContentModuleLoader> FindSceneLoaders()
        {
            var result = new List<AddressableContentModuleLoader>();
            var loaders = Resources.FindObjectsOfTypeAll<AddressableContentModuleLoader>();
            foreach (var loader in loaders)
            {
                if (loader == null)
                    continue;

                if (EditorUtility.IsPersistent(loader))
                    continue;

                if (!loader.gameObject.scene.IsValid())
                    continue;

                result.Add(loader);
            }

            return result;
        }

        static int ValidateLoader(
            AddressableContentModuleLoader loader,
            AddressableAssetSettings settings)
        {
            int issues = 0;
            var seenStartupKeys = new HashSet<string>();
            var seenDlcIds = new HashSet<int>();
            var seenModuleKeys = new HashSet<string>();

            for (int i = 0; i < loader.StartupModuleKeys.Count; i++)
            {
                string key = loader.StartupModuleKeys[i];
                if (string.IsNullOrWhiteSpace(key))
                {
                    Warn(loader, $"StartupModuleKeys[{i}] is empty.");
                    issues++;
                    continue;
                }

                if (!seenStartupKeys.Add(key))
                {
                    Warn(loader, $"Startup module key '{key}' is duplicated.");
                    issues++;
                }

                issues += ValidateAddressKey(loader, settings, key, 0);
            }

            for (int i = 0; i < loader.ContentModules.Count; i++)
            {
                var module = loader.ContentModules[i];
                string addressKey = module.AddressKey;

                if (module.DlcId <= 0)
                {
                    Warn(loader, $"ContentModules[{i}] has invalid DlcId '{module.DlcId}'. DLC module ids should be positive.");
                    issues++;
                }

                if (!seenDlcIds.Add(module.DlcId))
                {
                    Warn(loader, $"DLC id '{module.DlcId}' is duplicated in ContentModules.");
                    issues++;
                }

                if (string.IsNullOrWhiteSpace(module.ModuleName))
                {
                    Warn(loader, $"ContentModules[{i}] for DLC {module.DlcId} has no ModuleName.");
                    issues++;
                }

                if (string.IsNullOrWhiteSpace(addressKey))
                {
                    Warn(loader, $"ContentModules[{i}] for DLC {module.DlcId} has no AddressKey.");
                    issues++;
                    continue;
                }

                if (!seenModuleKeys.Add(addressKey))
                {
                    Warn(loader, $"Content module AddressKey '{addressKey}' is duplicated.");
                    issues++;
                }

                issues += ValidateAddressKey(loader, settings, addressKey, module.DlcId);
            }

            if (loader.EnforceDlcOwnership)
            {
                var owned = new HashSet<int>();
                foreach (int dlcId in loader.OwnedDlcIds)
                {
                    if (dlcId <= 0)
                    {
                        Warn(loader, $"OwnedDlcIds contains invalid id '{dlcId}'.");
                        issues++;
                    }

                    if (!owned.Add(dlcId))
                    {
                        Warn(loader, $"OwnedDlcIds contains duplicate id '{dlcId}'.");
                        issues++;
                    }
                }
            }

            return issues;
        }

        static int ValidatePrefabRegistryAuthoringPerScene()
        {
            int issues = 0;
            var byScene = new Dictionary<string, List<PrefabRegistryAuthoring>>();
            var authorings = Resources.FindObjectsOfTypeAll<PrefabRegistryAuthoring>();

            foreach (var authoring in authorings)
            {
                if (authoring == null)
                    continue;

                if (EditorUtility.IsPersistent(authoring))
                    continue;

                var scene = authoring.gameObject.scene;
                if (!scene.IsValid())
                    continue;

                string sceneKey = string.IsNullOrEmpty(scene.path)
                    ? scene.name
                    : scene.path;

                if (!byScene.TryGetValue(sceneKey, out var list))
                    byScene[sceneKey] = list = new List<PrefabRegistryAuthoring>();

                list.Add(authoring);
            }

            foreach (var kv in byScene)
            {
                if (kv.Value.Count <= 1)
                    continue;

                issues++;
                Debug.LogWarning(
                    $"[ContentModuleValidator] Scene '{kv.Key}' has {kv.Value.Count} PrefabRegistryAuthoring components. " +
                    "A content SubScene should bake exactly one GamePrefabRegistry.",
                    kv.Value[0]);
            }

            foreach (var authoring in authorings)
            {
                if (authoring == null || EditorUtility.IsPersistent(authoring))
                    continue;

                if (authoring.Source != null)
                    continue;

                issues++;
                Debug.LogWarning(
                    "[ContentModuleValidator] PrefabRegistryAuthoring has no Source registry.",
                    authoring);
            }

            return issues;
        }

        static int ValidateAddressKey(
            Object context,
            AddressableAssetSettings settings,
            string addressKey,
            int dlcId)
        {
            var entry = FindEntryByAddress(settings, addressKey);
            if (entry == null)
            {
                Warn(context, $"AddressKey '{addressKey}' is not an Addressables entry address.");
                return 1;
            }

            int issues = 0;
            var group = entry.parentGroup;
            if (group == null)
            {
                Warn(context, $"AddressKey '{addressKey}' has no Addressables group.");
                return 1;
            }

            if (entry.IsFolder)
            {
                Warn(context, $"AddressKey '{addressKey}' points to a folder. Content modules should point to a concrete scene asset or SubScene prefab wrapper.");
                issues++;
            }

            if (entry.AssetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                issues += ValidateSubScenePrefabWrapper(context, addressKey, entry.AssetPath);
            }
            else if (!entry.AssetPath.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase))
            {
                Warn(context, $"AddressKey '{addressKey}' points to neither a scene asset nor a SubScene prefab wrapper. AssetPath='{entry.AssetPath}'.");
                issues++;
            }

            var schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
            {
                Warn(context, $"AddressKey '{addressKey}' is in group '{group.Name}', but the group has no BundledAssetGroupSchema.");
                issues++;
            }
            else if (schema.BundleMode == BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel)
            {
                Warn(context, $"Group '{group.Name}' uses Pack Together By Label. DLC content modules should be separated by group/bundle, not by label.");
                issues++;
            }

            if (dlcId > 0 && !group.Name.Contains(dlcId.ToString()))
            {
                Debug.Log(
                    $"[ContentModuleValidator] DLC {dlcId} AddressKey '{addressKey}' is in group '{group.Name}'. " +
                    "This is OK if the group is the intended DLC bundle group.",
                    context);
            }

            return issues;
        }

        static int ValidateSubScenePrefabWrapper(
            Object context,
            string addressKey,
            string assetPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Warn(context, $"AddressKey '{addressKey}' points to prefab '{assetPath}', but the prefab could not be loaded.");
                return 1;
            }

            int issues = 0;
            var references = prefab.GetComponentsInChildren<ContentModuleSubSceneReference>(true);
            if (references == null || references.Length == 0)
            {
                Warn(context, $"AddressKey '{addressKey}' prefab wrapper has no ContentModuleSubSceneReference. Run CitySim > Content Modules > Sync SubScene Prefab References. AssetPath='{assetPath}'.");
                issues++;
            }
            else
            {
                foreach (var reference in references)
                {
                    if (reference == null)
                        continue;

                    bool hasGuid = !string.IsNullOrWhiteSpace(reference.SceneGuid);
                    bool hasPath = !string.IsNullOrWhiteSpace(reference.ScenePath);
                    if (!hasGuid && !hasPath)
                    {
                        Warn(context, $"AddressKey '{addressKey}' prefab wrapper has a ContentModuleSubSceneReference with no SceneGuid or ScenePath. AssetPath='{assetPath}'.");
                        issues++;
                    }
                }
            }

            var subScenes = prefab.GetComponentsInChildren<SubScene>(true);
            if (subScenes == null || subScenes.Length == 0)
            {
                Warn(context, $"AddressKey '{addressKey}' prefab wrapper has no SubScene component. AssetPath='{assetPath}'.");
                issues++;
                return issues;
            }

            foreach (var subScene in subScenes)
            {
                if (subScene == null)
                    continue;

                if (subScene.SceneAsset == null)
                {
                    Warn(context, $"AddressKey '{addressKey}' prefab wrapper has a SubScene with no SceneAsset. AssetPath='{assetPath}'.");
                    issues++;
                }

                if (!subScene.SceneGUID.IsValid)
                {
                    Warn(context, $"AddressKey '{addressKey}' prefab wrapper has a SubScene with an invalid SceneGUID. AssetPath='{assetPath}'.");
                    issues++;
                }
            }

            return issues;
        }

        static bool SyncSubScenePrefabReference(string assetPath)
        {
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return false;

            bool changed = false;
            try
            {
                var subScenes = root.GetComponentsInChildren<SubScene>(true);
                foreach (var subScene in subScenes)
                {
                    if (subScene == null)
                        continue;

                    var reference = subScene.GetComponent<ContentModuleSubSceneReference>();
                    if (reference == null)
                    {
                        reference = subScene.gameObject.AddComponent<ContentModuleSubSceneReference>();
                        changed = true;
                    }

                    string oldGuid = reference.SceneGuid;
                    string oldPath = reference.ScenePath;
                    reference.SyncFromSubScene(subScene);
                    if (oldGuid != reference.SceneGuid || oldPath != reference.ScenePath)
                        changed = true;
                }

                if (changed)
                    PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return changed;
        }

        static AddressableAssetEntry FindEntryByAddress(
            AddressableAssetSettings settings,
            string address)
        {
            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;

                foreach (var entry in group.entries)
                {
                    if (entry != null && entry.address == address)
                        return entry;
                }
            }

            return null;
        }

        static void Warn(Object context, string message)
        {
            Debug.LogWarning($"[ContentModuleValidator] {message}", context);
        }
    }
}
#endif
