using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class Test : MonoBehaviour
{


    IEnumerator Start()
    {
        // AsyncOperationHandle 자체를 yield return에 넘겨야 완료까지 대기
        var job = Addressables.LoadAssetAsync<GameObject>("Assets/_game/prefab/subscenes/Origin.prefab");
        yield return job; // ✅ handle 자체를 넘겨야 함

        if (job.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError("Addressables 로드 실패: " + job.OperationException);
            yield break;
        }

        var sub = job.Result.GetComponent<SubScene>();
        if (sub == null)
        {
            Debug.LogError("SubScene 컴포넌트 없음");
            yield break;
        }
        else
        {
            Debug.Log(sub.SceneGUID);
        }

        var world = World.DefaultGameObjectInjectionWorld;
        while (world == null)  // ✅ world도 완료까지 대기
        {
            yield return null;
            world = World.DefaultGameObjectInjectionWorld;
        }
        Debug.Log("loading scene...");
        SceneSystem.LoadSceneAsync(world.Unmanaged, sub.SceneGUID);
    }
}
