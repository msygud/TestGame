using System.Collections;
using System.IO;
using System.Text;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    [DisallowMultipleComponent]
    public sealed class MapLoadBootstrap : MonoBehaviour
    {
        [SerializeField] bool loadOnStart = true;
        [SerializeField] TextAsset mapJsonAsset;
        [SerializeField] string mapJsonPath;
        [SerializeField, Min(0)] int waitFramesAfterWorldReady = 2;

        IEnumerator Start()
        {
            if (!loadOnStart)
                yield break;

            yield return LoadWhenReady();
        }

        public void LoadMap()
        {
            StartCoroutine(LoadWhenReady());
        }

        IEnumerator LoadWhenReady()
        {
            World world;
            do
            {
                yield return null;
                world = World.DefaultGameObjectInjectionWorld;
            } while (world == null || !world.IsCreated);

            for (int i = 0; i < waitFramesAfterWorldReady; i++)
                yield return null;

            if (!TryResolveMapPath(out string path))
                yield break;

            if (path.Length > 511)
            {
                Debug.LogError($"[MapLoadBootstrap] Map path is too long for MapLoadCommand: {path}");
                yield break;
            }

            var em = world.EntityManager;
            var e = em.CreateEntity();
            em.AddComponentData(e, new MapLoadCommand
            {
                JsonPath = path,
            });

            Debug.Log($"[MapLoadBootstrap] Map load requested: {path}");
        }

        bool TryResolveMapPath(out string path)
        {
            if (mapJsonAsset != null)
            {
                string directory = Path.Combine(Application.persistentDataPath, "MapCache");
                Directory.CreateDirectory(directory);

                path = Path.Combine(directory, $"{mapJsonAsset.name}.json");
                File.WriteAllText(path, mapJsonAsset.text, Encoding.UTF8);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(mapJsonPath))
            {
                path = mapJsonPath;
                return true;
            }

            Debug.LogWarning("[MapLoadBootstrap] No map JSON asset or path assigned.");
            path = "";
            return false;
        }
    }
}
