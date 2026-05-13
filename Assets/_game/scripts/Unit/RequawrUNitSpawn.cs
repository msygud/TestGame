using System.Collections;
using UnityEngine;
using Unity.Entities;
using UnityEngine.InputSystem;
using Unity.Mathematics;
using System;
using System.Collections.Generic;
using Game.Combat;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement;

namespace Game.Unit
{
    public class RequawrUNitSpawn : MonoBehaviour
    {
        public Entity unitRequestBuffer;
        public World world;
        public DynamicBuffer<RequestUnit> requestUnitBuffer;

        //¸Ê
        public float2 MapSize;
        public float2 MinPosition;
        public float2 MaxPosition;


        [Range(1,100)]
        public int SpawnCount;
        public int spawnTeam;
        private IEnumerator Start()
        {
            while (unitRequestBuffer == Entity.Null)
            {
                yield return null;
                world = World.DefaultGameObjectInjectionWorld;
                if(world!=null)
                {
                    unitRequestBuffer= world.EntityManager.CreateEntity(typeof(RequestUnit));
                    
                    world.EntityManager.AddComponentData<GeneratedInstanceIdData>(unitRequestBuffer, new GeneratedInstanceIdData
                    {
                        CurrentID = 0
                    });

                }
            }
        }

        private void Update()
        {
            bool instantiated = false;
            int id = 0;
            if (Keyboard.current.digit0Key.wasPressedThisFrame)
            {
                instantiated = true;
                id = 0;
            }
            else if (Keyboard.current.digit1Key.wasPressedThisFrame)
            {
                instantiated = true;
                id = 1;
            }
            else if (Keyboard.current.digit2Key.wasPressedThisFrame)
            {
                instantiated = true;
                id = 2;
            }
            else if (Keyboard.current.digit3Key.wasPressedThisFrame)
            {
                instantiated = true;
                id = 3;
            }
            else if(Keyboard.current.digit4Key.wasPressedThisFrame)
            {
                instantiated = true;
                id= 4;
            }
            if (instantiated && world != null)
            {
                int prefabid = id;
                requestUnitBuffer = world.EntityManager.GetBuffer<RequestUnit>(unitRequestBuffer);
                requestUnitBuffer.Add(new RequestUnit { ID = prefabid, LocalID = spawnTeam ,SpawnCount=this.SpawnCount});
            }
        }
    }
}
