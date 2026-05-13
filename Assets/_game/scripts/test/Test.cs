using NUnit.Framework;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;

public class Test : MonoBehaviour
{

    
    // Update is called once per frame
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        int2 a = int2.zero;
    }
    void Update()
    {
        
    }
    [ContextMenu("test")]
    public void TestF()
    {
        var result = Addressables.CheckForCatalogUpdates();
        do
        {
            var r = result.Result;
            for (int i = 0;r.Count<i;i++)
            {
                Debug.Log(r[i]);
            }
            Debug.Log("complete");
        } while (!result.IsDone);
    }
}
