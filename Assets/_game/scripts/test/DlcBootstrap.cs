using CitySim.MapEditor;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Scenes;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  DlcBootstrap  (MonoBehaviour)
    //
    //  게임 시작 시 진입점. MonoBehaviour → ECS 브릿지 역할.
    //
    //  실행 순서:
    //    1. DLC 보유 등록 (스텁 → SDK 교체 지점)
    //    2. 보유 DLC의 Addressables 카탈로그 로드
    //    3. 보유 DLC의 SubScene 로드 (SceneSystem)
    //    4. 모든 SubScene 로드 완료 대기
    //    5. 맵 접근 권한 검증
    //    6. MapData 로드 (Addressables)
    //    7. ECS 월드에 MapLoadRequest 엔티티 생성
    //
    //  개발 중 (Use Asset Database 모드):
    //    Addressables 빌드 불필요.
    //    SubScene은 Inspector의 SubSceneManifests로 직접 참조.
    //    StartMapId를 설정하면 플레이 버튼 즉시 해당 맵 로드.
    // ══════════════════════════════════════════════════════════════
    public class DlcBootstrap : MonoBehaviour
    {
        [Header("DLC SubScenes")]
        [Tooltip("보유 여부에 관계없이 모든 DLC Manifest SO를 등록합니다. " +
                 "개발 중에는 Addressables 대신 직접 참조.")]
        public List<DlcSubSceneManifest> SubSceneManifests = new();

        [Header("개발용 — 직접 DLC 보유 설정")]
        [Tooltip("플레이 시 보유 처리할 DLC ID 목록. 0은 항상 포함.")]
        public List<int> OwnedDlcIdsForDev = new();

        [Header("맵 로드")]
        [Tooltip("플레이 시 바로 로드할 맵 ID. 비워두면 맵 선택 화면으로.")]
        public string StartMapId;

        [Tooltip("맵 JSON 문자열 직접 입력 (개발용). " +
                 "비워두면 Addressables에서 로드.")]
        [TextArea(5, 20)]
        public string DevMapJson;

        // ── 내부 상태 ─────────────────────────────────────────────
        World _world;

        // ── Unity 생명주기 ────────────────────────────────────────

        void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;

            StartCoroutine(BootstrapCoroutine());
        }

        // ══════════════════════════════════════════════════════════
        //  부트스트랩 코루틴
        // ══════════════════════════════════════════════════════════
        IEnumerator BootstrapCoroutine()
        {
            // 1. DLC 보유 등록 (스텁)
            RegisterOwnedDlcs();

            // 2. Addressables 카탈로그 로드 (DLC별 원격 카탈로그)
            yield return LoadDlcCatalogsCoroutine();

            // 3. 보유 DLC SubScene 로드
            yield return LoadSubScenesCoroutine();

            // 4. 맵 로드 요청 생성
            if (!string.IsNullOrEmpty(StartMapId))
                yield return RequestMapLoadCoroutine(StartMapId);
        }

        // ── 1. DLC 보유 등록 ─────────────────────────────────────

        void RegisterOwnedDlcs()
        {
            // 개발 중: Inspector에서 설정한 DLC를 보유로 처리
            DlcOwnershipService.Reset();
            DlcOwnershipService.RegisterOwned(OwnedDlcIdsForDev);

            Debug.Log(
                $"[DlcBootstrap] 보유 DLC: " +
                string.Join(", ", DlcOwnershipService.OwnedDlcIds));

            // 실배포 교체 지점:
            // var dlcList = await SteamApps.RequestAllOwnedAppIds();
            // foreach (var appId in dlcList) DlcOwnershipService.RegisterOwned(appId);
        }

        // ── 2. Addressables 카탈로그 로드 ────────────────────────

        IEnumerator LoadDlcCatalogsCoroutine()
        {
            foreach (var manifest in SubSceneManifests)
            {
                if (manifest == null) continue;
                if (manifest.DlcId == 0) continue;           // Origin은 로컬
                if (!DlcOwnershipService.Owns(manifest.DlcId)) continue;

                // 카탈로그 URL은 실배포 시 서버 주소로 교체
                // 개발 중(Use Asset Database)에는 이 단계 불필요 — 스킵
#if !UNITY_EDITOR
                string catalogUrl = $"https://your-cdn.com/dlc/{manifest.DlcName}/catalog.json";
                var catalogHandle = Addressables.LoadContentCatalogAsync(catalogUrl);
                yield return catalogHandle;

                if (catalogHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogWarning(
                        $"[DlcBootstrap] DLC '{manifest.DlcName}' 카탈로그 로드 실패. " +
                        $"미보유 또는 서버 오류.");
                }
#else
                yield return null; // 에디터: 스킵
#endif
            }
        }

        // ── 3. SubScene 로드 ──────────────────────────────────────

        IEnumerator LoadSubScenesCoroutine()
        {
            var loadingScenes = new List<Entity>();

            foreach (var manifest in SubSceneManifests)
            {
                if (manifest == null) continue;
                if (!DlcOwnershipService.Owns(manifest.DlcId)) continue;

                // SceneSystem으로 SubScene 로드
                var loadParams = new SceneSystem.LoadParameters
                {
                    Flags = SceneLoadFlags.LoadAdditive,
                };

                var sceneEntity = SceneSystem.LoadSceneAsync(
                    _world.Unmanaged,
                    manifest.SceneReference,
                    loadParams);

                loadingScenes.Add(sceneEntity);
                Debug.Log($"[DlcBootstrap] SubScene 로드 시작: {manifest.DlcName}");
            }

            // 모든 SubScene 로드 완료 대기
            bool allLoaded;
            do
            {
                yield return null;
                allLoaded = true;
                foreach (var sceneEntity in loadingScenes)
                {
                    if (!SceneSystem.IsSceneLoaded(_world.Unmanaged, sceneEntity))
                    {
                        allLoaded = false;
                        break;
                    }
                }
            }
            while (!allLoaded);

            Debug.Log($"[DlcBootstrap] 모든 SubScene 로드 완료 ({loadingScenes.Count}개)");
        }

        // ── 4. 맵 로드 요청 ──────────────────────────────────────

        IEnumerator RequestMapLoadCoroutine(string mapId)
        {
            MapData mapData = null;

            // 개발용: Inspector에서 직접 입력한 JSON 우선
            if (!string.IsNullOrEmpty(DevMapJson))
            {
                mapData = JsonUtility.FromJson<MapData>(DevMapJson);
                Debug.Log("[DlcBootstrap] 개발용 MapJson 사용.");
            }
            else
            {
                // Addressables에서 맵 메타 먼저 로드 → 접근 검증
                var metaAddress = DlcAddressConfig.MapMeta(mapId);
                var metaHandle = Addressables.LoadAssetAsync<TextAsset>(metaAddress);
                yield return metaHandle;

                if (metaHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogError($"[DlcBootstrap] MapMeta 로드 실패: {metaAddress}");
                    yield break;
                }

                var meta = JsonUtility.FromJson<MapMeta>(metaHandle.Result.text);
                Addressables.Release(metaHandle);

                // 접근 권한 검증
                if (!meta.CanAccess())
                {
                    var missing = meta.MissingDlcs();
                    Debug.LogError(
                        $"[DlcBootstrap] 맵 '{mapId}' 접근 불가. " +
                        $"미보유 DLC: {string.Join(", ", missing)}");
                    yield break;
                }

                // 맵 데이터 로드
                var dataAddress = DlcAddressConfig.MapData(mapId);
                var dataHandle = Addressables.LoadAssetAsync<TextAsset>(dataAddress);
                yield return dataHandle;

                if (dataHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogError($"[DlcBootstrap] MapData 로드 실패: {dataAddress}");
                    yield break;
                }

                mapData = JsonUtility.FromJson<MapData>(dataHandle.Result.text);
                Addressables.Release(dataHandle);
            }

            if (mapData == null)
            {
                Debug.LogError("[DlcBootstrap] MapData 파싱 실패.");
                yield break;
            }

            // ECS 월드에 MapLoadRequest 엔티티 생성
            var em = _world.EntityManager;
            var requestEntity = em.CreateEntity();
            em.AddComponentData(requestEntity, new MapLoadRequest
            {
                MapId = mapId,
            });
            em.AddComponentData(requestEntity, new ManagedMapLoadRequest
            {
                Data = mapData,
            });

            Debug.Log($"[DlcBootstrap] 맵 '{mapId}' 로드 요청 생성 완료.");
        }

        // ── 외부 호출 (맵 선택 화면 등) ──────────────────────────

        /// <summary>맵 선택 화면에서 맵 ID를 전달받아 로드를 시작한다.</summary>
        public void LoadMap(string mapId)
        {
            StartCoroutine(RequestMapLoadCoroutine(mapId));
        }
    }
}