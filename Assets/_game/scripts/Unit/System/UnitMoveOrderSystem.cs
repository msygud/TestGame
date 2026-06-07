using Unity.Collections;
using Unity.Entities;

namespace Game.Unit
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct UnitMoveOrderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MoveOrderRequest>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var targets = SystemAPI.GetComponentLookup<UnitMoveTarget>(false);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<MoveOrderRequest>>()
                         .WithEntityAccess())
            {
                var unit = request.ValueRO.Unit;
                if (SystemAPI.Exists(unit))
                {
                    var target = new UnitMoveTarget
                    {
                        Position = request.ValueRO.Target,
                        StopDistance = request.ValueRO.StopDistance,
                        HasTarget = 1,
                    };

                    if (targets.HasComponent(unit))
                        targets[unit] = target;
                    else
                        ecb.AddComponent(unit, target);
                }

                ecb.DestroyEntity(requestEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
