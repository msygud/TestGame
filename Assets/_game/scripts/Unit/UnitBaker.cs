using Unity.Entities;
using UnityEngine;
using Game.Minimap;
using Unity.Collections;
using Unity.Mathematics;
using System;

namespace Game.Unit
{
    public class UnitBaker : MonoBehaviour
    {
         public PrefabInfo[] _prefabs;

        [Serializable]
        public class PrefabInfo
        {
            public GameObject Prefab;
            public int ID;
        }
    }

    class UnitBakerBaker : Baker<UnitBaker>
    {
        public override void Bake(UnitBaker authoring)
        {
            Entity bufferentiry= GetEntity(TransformUsageFlags.None);
             var buffer = AddBuffer<PrefabInfoElemental>(bufferentiry);
            
            for (int i = 0; i < authoring._prefabs.Length; i++)
            {
                var prefab = GetEntity(authoring._prefabs[i].Prefab, TransformUsageFlags.Dynamic);
                
                buffer.Add(new PrefabInfoElemental { Prefab = prefab, ID = authoring._prefabs[i].ID });
            }
        }
    }

    
}
