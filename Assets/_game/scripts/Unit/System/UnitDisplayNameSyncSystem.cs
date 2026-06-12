using Unity.Entities;

namespace Game.Unit
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct UnitDisplayNameSyncSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitDisplayName>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

            foreach (var (displayName, entity) in
                     SystemAPI.Query<RefRO<UnitDisplayName>>()
                         .WithNone<UnitDisplayNameSynced>()
                         .WithEntityAccess())
            {
                entityManager.SetName(entity, displayName.ValueRO.Value.ToString());
                ecb.AddComponent<UnitDisplayNameSynced>(entity);
            }

            ecb.Playback(entityManager);
            ecb.Dispose();
        }
    }
}
