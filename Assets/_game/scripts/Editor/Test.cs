using CitySim;
using System.Collections;
using Unity.Entities;
using UnityEngine;

public class Test : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    IEnumerator Start()
    {
        World world;
        do
        {
            world = World.DefaultGameObjectInjectionWorld;
            yield return null;  
        } while (world == null);
        EntityManager em = world.EntityManager;
        Entity e= em.CreateEntity(typeof(MapLoadCommand));
        em.SetComponentData<MapLoadCommand>(e, new MapLoadCommand { JsonPath = "Assets/_game/data/Map/NewMap.json" });
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
