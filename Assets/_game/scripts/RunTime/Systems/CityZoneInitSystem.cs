using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CityZoneInitSystem — CityZones 싱글톤 수명주기 (GridInitSystem과 동일 패턴)
    //
    //  NativeHashMap을 포함하는 컴포넌트는 베이킹 직렬화 불가 → 코드로 생성/해제.
    //  AiCityGrowthSystem(등록)·RazeSystem(해체)·NetworkRepairSystem(재연결)이 사용.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct CityZoneInitSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<CityZones>()) return;

            var z = new CityZones
            {
                Zones        = new NativeHashMap<int2, ZoneRecord>(256, Allocator.Persistent),
                InteriorZone = new NativeHashMap<int2, int2>(2048, Allocator.Persistent),
                RingRef      = new NativeHashMap<int2, int>(2048, Allocator.Persistent),
            };
            var e = state.EntityManager.CreateEntity(typeof(CityZones));
            state.EntityManager.SetComponentData(e, z);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<CityZones>()) return;
            var z = SystemAPI.GetSingleton<CityZones>();
            if (z.Zones.IsCreated)        z.Zones.Dispose();
            if (z.InteriorZone.IsCreated) z.InteriorZone.Dispose();
            if (z.RingRef.IsCreated)      z.RingRef.Dispose();
        }
    }
}
