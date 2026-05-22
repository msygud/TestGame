using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.Serialization;

namespace CitySim
{
    public struct ContentModuleSceneLoadRequest : IComponentData
    {
        public Unity.Entities.Hash128 SceneGuid;
        public FixedString512Bytes ScenePath;
        public FixedString128Bytes ModuleKey;
        public FixedString512Bytes LocationKey;
        public int DlcId;
        public bool BlockOnStreamIn;
    }

    public struct ContentModuleSceneLoaded : IComponentData
    {
        public Entity SceneEntity;
        public Unity.Entities.Hash128 SceneGuid;
        public FixedString128Bytes ModuleKey;
        public int DlcId;
    }

    [System.Serializable]
    public struct ContentModuleDefinition
    {
        public int DlcId;
        public string ModuleName;
        [FormerlySerializedAs("Label")]
        public string AddressKey;
        [Tooltip("Optional explicit Unity scene asset GUID. Use this for ECS SubScene content modules.")]
        public string SceneGuid;
        [Tooltip("Optional explicit scene asset path, for example Assets/_game/scenes/editor/DLC1.unity.")]
        public string ScenePath;
    }

    public sealed class AddressableContentModuleLoader : MonoBehaviour
    {
        [Tooltip("Addressables addresses or keys for content modules that should be loaded on start.")]
        [FormerlySerializedAs("ModuleLabels")]
        public List<string> StartupModuleKeys = new();

        [Tooltip("DLC content modules. AddressKey should point to a module scene address, or to an Addressable prefab that contains SubScene references.")]
        [FormerlySerializedAs("DlcModules")]
        public List<ContentModuleDefinition> ContentModules = new();

        [Tooltip("Check and update the remote Addressables catalog before resolving module keys.")]
        public bool CheckRemoteCatalogOnStart = true;

        [Tooltip("Resolve configured startup module keys automatically when this component starts.")]
        public bool LoadOnStart = true;

        [Tooltip("Automatically request the matching DLC content module when map loading waits for MissingDlcId.")]
        public bool LoadMissingDlcOnMapWait = true;

        [Tooltip("Block DLC content module loading unless the DLC id is present in OwnedDlcIds. Replace this list with account/store entitlement data later.")]
        public bool EnforceDlcOwnership = true;

        [Tooltip("Temporary local DLC ownership list used by the loader until a real entitlement service is connected.")]
        public List<int> OwnedDlcIds = new();

        [Tooltip("Ask SceneSystem to block while streaming the entity scene content.")]
        public bool BlockOnStreamIn;

        readonly HashSet<string> requestedModules = new();
        readonly HashSet<string> requestedModuleKeys = new();
        readonly HashSet<int> requestedDlcIds = new();
        readonly HashSet<int> reportedBlockedDlcIds = new();
        readonly HashSet<int> reportedMissingModuleDlcIds = new();
        Coroutine loadRoutine;

        void Start()
        {
            if (LoadOnStart)
                LoadContentModules();
        }

        void Update()
        {
            if (LoadMissingDlcOnMapWait)
                RequestMissingDlcFromMapLoadState();
        }

        [ContextMenu("Load Content Modules")]
        public void LoadContentModules()
        {
            if (loadRoutine != null || !isActiveAndEnabled)
                return;

            loadRoutine = StartCoroutine(LoadContentModulesRoutine());
        }

        IEnumerator LoadContentModulesRoutine()
        {
            if (CheckRemoteCatalogOnStart)
                yield return UpdateRemoteCatalogs();

            for (int i = 0; i < StartupModuleKeys.Count; i++)
            {
                string moduleKey = StartupModuleKeys[i];
                if (string.IsNullOrWhiteSpace(moduleKey))
                    continue;

                yield return ResolveScenesForModuleKeyOnce(moduleKey, 0);
            }

            loadRoutine = null;
        }

        static IEnumerator UpdateRemoteCatalogs()
        {
            var checkHandle = Addressables.CheckForCatalogUpdates(false);
            yield return checkHandle;

            if (checkHandle.Status == AsyncOperationStatus.Succeeded
                && checkHandle.Result != null
                && checkHandle.Result.Count > 0)
            {
                var updateHandle = Addressables.UpdateCatalogs(checkHandle.Result, false);
                yield return updateHandle;
                Addressables.Release(updateHandle);
            }

            Addressables.Release(checkHandle);
        }

        public void LoadDlcContent(int dlcId)
        {
            if (dlcId == 0 || !isActiveAndEnabled)
                return;

            if (!CanLoadDlc(dlcId))
            {
                if (reportedBlockedDlcIds.Add(dlcId))
                    Debug.LogWarning($"[ContentModuleLoader] DLC content blocked by ownership: dlcId={dlcId}.");
                return;
            }

            if (!TryGetContentModule(dlcId, out var module))
            {
                if (reportedMissingModuleDlcIds.Add(dlcId))
                    Debug.LogWarning($"[ContentModuleLoader] No content module configured for DLC {dlcId}.");
                return;
            }

            if (!requestedDlcIds.Add(dlcId))
                return;

            Debug.Log(
                $"[ContentModuleLoader] Requesting DLC content module: " +
                $"dlcId={dlcId}, module='{module.ModuleName}', addressKey='{module.AddressKey}'.");
            StartCoroutine(ResolveScenesForContentModuleOnce(module));
        }

        bool CanLoadDlc(int dlcId)
        {
            return !EnforceDlcOwnership || OwnedDlcIds.Contains(dlcId);
        }

        bool TryGetContentModule(int dlcId, out ContentModuleDefinition module)
        {
            for (int i = 0; i < ContentModules.Count; i++)
            {
                module = ContentModules[i];
                if (module.DlcId == dlcId && !string.IsNullOrWhiteSpace(module.AddressKey))
                {
                    return true;
                }
            }

            module = default;
            return false;
        }

        void RequestMissingDlcFromMapLoadState()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<MapLoadState>());
            if (query.IsEmptyIgnoreFilter)
                return;

            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                if (entities.Length == 0)
                    return;

                var loadState = em.GetComponentData<MapLoadState>(entities[0]);
                if (loadState.Status == MapLoadStatus.WaitingForContent
                    && loadState.MissingDlcId != 0)
                {
                    LoadDlcContent(loadState.MissingDlcId);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        IEnumerator ResolveScenesForModuleKeyOnce(string moduleKey, int dlcId)
        {
            if (string.IsNullOrWhiteSpace(moduleKey) || !requestedModuleKeys.Add(moduleKey))
                yield break;

            yield return ResolveScenesForModuleKey(moduleKey, dlcId);
        }

        IEnumerator ResolveScenesForContentModuleOnce(ContentModuleDefinition module)
        {
            string moduleKey = module.AddressKey;
            if (string.IsNullOrWhiteSpace(moduleKey) || !requestedModuleKeys.Add(moduleKey))
                yield break;

            if (TryBuildExplicitSceneRequest(module, out var explicitRequest))
            {
                yield return ValidateAddressableModuleKey(moduleKey, module.DlcId);
                QueueEntitySceneLoad(explicitRequest);
                Debug.Log(
                    $"[ContentModuleLoader] Queued explicit ECS scene load: moduleKey='{moduleKey}', " +
                    $"dlcId={module.DlcId}, sceneGuid='{module.SceneGuid}', scenePath='{module.ScenePath}'.");
                yield break;
            }

            yield return ResolveScenesForModuleKey(moduleKey, module.DlcId);
        }

        IEnumerator ResolveScenesForModuleKey(string moduleKey, int dlcId)
        {
            var locationsHandle = Addressables.LoadResourceLocationsAsync(
                moduleKey,
                typeof(UnityEngine.ResourceManagement.ResourceProviders.SceneInstance));
            yield return locationsHandle;

            if (locationsHandle.Status != AsyncOperationStatus.Succeeded
                || locationsHandle.Result == null
                || locationsHandle.Result.Count == 0)
            {
                Addressables.Release(locationsHandle);
                yield return LogUntypedLocations(moduleKey);
                yield return ResolveSubScenePrefab(moduleKey, dlcId);
                yield break;
            }

            Debug.Log(
                $"[ContentModuleLoader] Found {locationsHandle.Result.Count} content scene location(s) " +
                $"for module key '{moduleKey}'" +
                (dlcId != 0 ? $", dlcId={dlcId}." : "."));

            foreach (var location in locationsHandle.Result)
                RequestEntitySceneLoad(location, moduleKey, dlcId);

            Addressables.Release(locationsHandle);
        }

        IEnumerator ValidateAddressableModuleKey(string moduleKey, int dlcId)
        {
            var locationsHandle = Addressables.LoadResourceLocationsAsync(moduleKey);
            yield return locationsHandle;

            if (locationsHandle.Status != AsyncOperationStatus.Succeeded
                || locationsHandle.Result == null
                || locationsHandle.Result.Count == 0)
            {
                Debug.LogWarning(
                    $"[ContentModuleLoader] AddressKey '{moduleKey}' did not resolve in Addressables. " +
                    $"The explicit ECS scene request will still be queued, dlcId={dlcId}.");
                Addressables.Release(locationsHandle);
                yield break;
            }

            Debug.Log(
                $"[ContentModuleLoader] AddressKey '{moduleKey}' resolved to {locationsHandle.Result.Count} Addressables location(s), dlcId={dlcId}.");
            Addressables.Release(locationsHandle);
        }

        IEnumerator LogUntypedLocations(string moduleKey)
        {
            var locationsHandle = Addressables.LoadResourceLocationsAsync(moduleKey);
            yield return locationsHandle;

            if (locationsHandle.Status != AsyncOperationStatus.Succeeded
                || locationsHandle.Result == null
                || locationsHandle.Result.Count == 0)
            {
                Addressables.Release(locationsHandle);
                yield break;
            }

            foreach (var location in locationsHandle.Result)
            {
                Debug.LogWarning(
                    $"[ContentModuleLoader] AddressKey '{moduleKey}' exists but is not a scene Addressables location. " +
                    $"PrimaryKey='{location.PrimaryKey}', InternalId='{location.InternalId}', ResourceType='{location.ResourceType}'. " +
                    "Trying to read SubScene references from it as a prefab wrapper.");
            }

            Addressables.Release(locationsHandle);
        }

        IEnumerator ResolveSubScenePrefab(string moduleKey, int dlcId)
        {
            var prefabHandle = Addressables.LoadAssetAsync<GameObject>(moduleKey);
            yield return prefabHandle;

            if (prefabHandle.Status != AsyncOperationStatus.Succeeded || prefabHandle.Result == null)
            {
                Debug.LogWarning(
                    $"[ContentModuleLoader] Module key '{moduleKey}' is not a loadable GameObject prefab wrapper.");
                Addressables.Release(prefabHandle);
                yield break;
            }

            var prefab = prefabHandle.Result;
            int queuedCount = QueueReferencedScenesFromPrefabWrapper(prefab, moduleKey, dlcId);
            if (queuedCount > 0)
            {
                Debug.Log(
                    $"[ContentModuleLoader] Queued {queuedCount} ECS scene load request(s) " +
                    $"from Addressable content module reference prefab '{moduleKey}', dlcId={dlcId}.");
                Addressables.Release(prefabHandle);
                yield break;
            }

            var subScenes = prefab.GetComponentsInChildren<SubScene>(true);
            if (subScenes == null || subScenes.Length == 0)
            {
                Debug.LogWarning(
                    $"[ContentModuleLoader] Addressable prefab wrapper '{moduleKey}' has no ContentModuleSubSceneReference or SubScene component.");
                Addressables.Release(prefabHandle);
                yield break;
            }

            int fallbackQueuedCount = 0;
            for (int i = 0; i < subScenes.Length; i++)
            {
                var subScene = subScenes[i];
                if (subScene == null)
                    continue;

                var guid = subScene.SceneGUID;
                if (!guid.IsValid)
                {
                    Debug.LogWarning(
                        $"[ContentModuleLoader] SubScene reference in prefab wrapper '{moduleKey}' has an invalid SceneGUID.");
                    continue;
                }

                string key = $"subscene-prefab:{moduleKey}:{guid}";
                if (!requestedModules.Add(key))
                    continue;

                QueueEntitySceneLoad(new ContentModuleSceneLoadRequest
                {
                    SceneGuid = guid,
                    ModuleKey = AssignFixedString128(moduleKey),
                    LocationKey = AssignFixedString512(key),
                    DlcId = dlcId,
                    BlockOnStreamIn = BlockOnStreamIn,
                });
                fallbackQueuedCount++;
            }

            if (fallbackQueuedCount > 0)
            {
                Debug.Log(
                    $"[ContentModuleLoader] Queued {fallbackQueuedCount} ECS scene load request(s) " +
                    $"from Addressable SubScene prefab wrapper '{moduleKey}', dlcId={dlcId}.");
            }
            else
            {
                Debug.LogWarning(
                    $"[ContentModuleLoader] Addressable prefab wrapper '{moduleKey}' did not contain any valid runtime scene reference. " +
                    "Add or sync a ContentModuleSubSceneReference component on the wrapper prefab.");
            }

            Addressables.Release(prefabHandle);
        }

        int QueueReferencedScenesFromPrefabWrapper(GameObject prefab, string moduleKey, int dlcId)
        {
            var references = prefab.GetComponentsInChildren<ContentModuleSubSceneReference>(true);
            if (references == null || references.Length == 0)
                return 0;

            int queuedCount = 0;
            for (int i = 0; i < references.Length; i++)
            {
                var reference = references[i];
                if (reference == null)
                    continue;

                if (!TryBuildSceneRequestFromReference(reference, moduleKey, dlcId, out var request))
                    continue;

                string stableKey = !request.SceneGuid.IsValid
                    ? $"content-ref:{moduleKey}:{request.ScenePath}"
                    : $"content-ref:{moduleKey}:{request.SceneGuid}";

                if (!requestedModules.Add(stableKey))
                    continue;

                request.LocationKey = AssignFixedString512(stableKey);
                QueueEntitySceneLoad(request);
                queuedCount++;
            }

            return queuedCount;
        }

        static bool TryBuildSceneRequestFromReference(
            ContentModuleSubSceneReference reference,
            string moduleKey,
            int dlcId,
            out ContentModuleSceneLoadRequest request)
        {
            request = new ContentModuleSceneLoadRequest
            {
                ModuleKey = AssignFixedString128(moduleKey),
                DlcId = dlcId != 0 ? dlcId : reference.DlcId,
            };

            bool hasGuid = TryParseGuid(reference.SceneGuid, out var sceneGuid);
            bool hasPath = TryAssignPath(reference.ScenePath, out var scenePath);

            if (!hasGuid && !hasPath)
            {
                Debug.LogWarning(
                    $"[ContentModuleLoader] ContentModuleSubSceneReference on '{reference.name}' " +
                    $"has no valid SceneGuid or ScenePath.");
                return false;
            }

            request.SceneGuid = hasGuid ? sceneGuid : default;
            request.ScenePath = hasPath ? scenePath : default;
            return true;
        }

        void RequestEntitySceneLoad(IResourceLocation location, string moduleKey, int dlcId)
        {
            if (location == null)
                return;

            string key = GetStableLocationKey(location);
            if (!requestedModules.Add(key))
                return;

            var request = new ContentModuleSceneLoadRequest
            {
                SceneGuid = TryExtractGuid(location, out var guid) ? guid : default,
                ScenePath = TryExtractPath(location, out var path) ? path : default,
                ModuleKey = AssignFixedString128(moduleKey),
                LocationKey = AssignFixedString512(key),
                DlcId = dlcId,
                BlockOnStreamIn = BlockOnStreamIn,
            };

            if (!request.SceneGuid.IsValid && request.ScenePath.IsEmpty)
            {
                Debug.LogWarning(
                    $"[ContentModuleLoader] Could not resolve an entity scene GUID or path from " +
                    $"Addressables location '{location.PrimaryKey}'.");
                return;
            }

            QueueEntitySceneLoad(request);
            Debug.Log(
                $"[ContentModuleLoader] Queued content scene load: moduleKey='{moduleKey}', " +
                $"dlcId={dlcId}, key='{key}'.");
        }

        static bool TryBuildExplicitSceneRequest(
            ContentModuleDefinition module,
            out ContentModuleSceneLoadRequest request)
        {
            request = new ContentModuleSceneLoadRequest
            {
                ModuleKey = AssignFixedString128(module.AddressKey),
                LocationKey = AssignFixedString512(module.AddressKey),
                DlcId = module.DlcId,
                BlockOnStreamIn = false,
            };

            bool hasGuid = TryParseGuid(module.SceneGuid, out var sceneGuid);
            bool hasPath = TryAssignPath(module.ScenePath, out var scenePath);

            if (!hasGuid && !hasPath)
                return false;

            request.SceneGuid = hasGuid ? sceneGuid : default;
            request.ScenePath = hasPath ? scenePath : default;
            return true;
        }

        void QueueEntitySceneLoad(ContentModuleSceneLoadRequest request)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogWarning("[ContentModuleLoader] Default ECS world is not ready.");
                return;
            }

            request.BlockOnStreamIn = BlockOnStreamIn;
            var entity = world.EntityManager.CreateEntity(typeof(ContentModuleSceneLoadRequest));
            world.EntityManager.SetComponentData(entity, request);
        }

        static string GetStableLocationKey(IResourceLocation location)
        {
            if (!string.IsNullOrEmpty(location.PrimaryKey))
                return location.PrimaryKey;

            return location.InternalId;
        }

        static bool TryExtractGuid(IResourceLocation location, out Unity.Entities.Hash128 guid)
        {
            if (TryParseGuid(location.PrimaryKey, out guid))
                return true;

            return TryParseGuid(location.InternalId, out guid);
        }

        static bool TryParseGuid(string value, out Unity.Entities.Hash128 guid)
        {
            guid = default;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Replace("-", "");
            if (normalized.Length != 32)
                return false;

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                bool isHex = c is >= '0' and <= '9'
                    || c is >= 'a' and <= 'f'
                    || c is >= 'A' and <= 'F';
                if (!isHex)
                    return false;
            }

            guid = new Unity.Entities.Hash128(normalized);
            return guid.IsValid;
        }

        static bool TryExtractPath(IResourceLocation location, out FixedString512Bytes path)
        {
            path = default;

            if (TryAssignPath(location.PrimaryKey, out path))
                return true;

            return TryAssignPath(location.InternalId, out path);
        }

        static bool TryAssignPath(string value, out FixedString512Bytes path)
        {
            path = default;
            if (string.IsNullOrWhiteSpace(value)
                || !value.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase)
                || value.Length > path.Capacity)
            {
                return false;
            }

            path = value;
            return true;
        }

        static FixedString128Bytes AssignFixedString128(string value)
        {
            if (string.IsNullOrEmpty(value))
                return default;

            if (value.Length > FixedString128Bytes.UTF8MaxLengthInBytes)
                value = value[..FixedString128Bytes.UTF8MaxLengthInBytes];

            return value;
        }

        static FixedString512Bytes AssignFixedString512(string value)
        {
            if (string.IsNullOrEmpty(value))
                return default;

            if (value.Length > FixedString512Bytes.UTF8MaxLengthInBytes)
                value = value[..FixedString512Bytes.UTF8MaxLengthInBytes];

            return value;
        }
    }

    public sealed class ContentModuleSubSceneReference : MonoBehaviour
    {
        public int DlcId;
        public string ModuleName;
        public string SceneGuid;
        public string ScenePath;

        [SerializeField]
        SubScene sourceSubScene;

#if UNITY_EDITOR
        void OnValidate()
        {
            if (sourceSubScene == null)
                sourceSubScene = GetComponent<SubScene>();

            SyncFromSubScene(sourceSubScene);
        }

        public void SyncFromSubScene(SubScene subScene)
        {
            if (subScene == null)
                return;

            sourceSubScene = subScene;

            if (subScene.SceneGUID.IsValid)
                SceneGuid = subScene.SceneGUID.ToString();

            if (!string.IsNullOrWhiteSpace(subScene.EditableScenePath))
                ScenePath = subScene.EditableScenePath;
        }
#endif
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(SceneSystemGroup))]
    public partial struct ContentModuleSceneLoadSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var requests = SystemAPI
                .QueryBuilder()
                .WithAll<ContentModuleSceneLoadRequest>()
                .Build()
                .ToEntityArray(Allocator.Temp);

            for (int i = 0; i < requests.Length; i++)
            {
                var requestEntity = requests[i];
                var request = state.EntityManager.GetComponentData<ContentModuleSceneLoadRequest>(requestEntity);
                var guid = request.SceneGuid;

                if (!guid.IsValid && !request.ScenePath.IsEmpty)
                {
                    var sceneSystemState = state.WorldUnmanaged.GetExistingSystemState<SceneSystem>();
                    guid = SceneSystem.GetSceneGUID(ref sceneSystemState, request.ScenePath.ToString());
                }

                if (!guid.IsValid)
                {
                    Debug.LogWarning("[ContentModuleSceneLoadSystem] Invalid content module scene reference.");
                    state.EntityManager.DestroyEntity(requestEntity);
                    continue;
                }

                var loadParameters = new SceneSystem.LoadParameters();
                if (request.BlockOnStreamIn)
                    loadParameters.Flags |= SceneLoadFlags.BlockOnStreamIn;

                var sceneEntity = SceneSystem.LoadSceneAsync(state.WorldUnmanaged, guid, loadParameters);
                state.EntityManager.AddComponentData(requestEntity, new ContentModuleSceneLoaded
                {
                    SceneEntity = sceneEntity,
                    SceneGuid = guid,
                    ModuleKey = request.ModuleKey,
                    DlcId = request.DlcId,
                });
                state.EntityManager.RemoveComponent<ContentModuleSceneLoadRequest>(requestEntity);

                Debug.Log(
                    $"[ContentModuleSceneLoadSystem] Loading content module scene: " +
                    $"moduleKey='{request.ModuleKey}', dlcId={request.DlcId}, guid={guid}.");
            }

            requests.Dispose();
        }
    }
}
