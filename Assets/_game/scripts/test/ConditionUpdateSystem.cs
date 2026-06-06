using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
   
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ConditionUpdateSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            
        }
    }

}
