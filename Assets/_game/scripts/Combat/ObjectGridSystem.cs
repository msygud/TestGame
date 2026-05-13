using Game.Unit;
using Game.Utility;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Game.Combat
{
    [UpdateAfter(typeof(TransformSystemGroup))]
    partial struct ObjectGridSystem : ISystem
    {
        EntityQuery _queryAllGridEntities;
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatGridInfo>();
            _queryAllGridEntities = SystemAPI.QueryBuilder().WithAll<LocalTransform, GridPositionData>().Build();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            CombatGridInfo gridinfo=SystemAPI.GetSingleton<CombatGridInfo>();

            var job = new JobArrangeInGrid();
            var handle = job.ScheduleParallel(_queryAllGridEntities, state.Dependency);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {

        }
    }

    public partial struct JobArrangeInGrid:IJobChunk
    {
        ComponentTypeHandle<LocalTransform> transform;
        ComponentTypeHandle<GridPositionData> gridposition;

        float2 origin;
        float cellsize;

        public void Execute(in ArchetypeChunk chunks,int chunkindex,bool useEnable,in v128 mask)
        {
            var objectposition = chunks.GetNativeArray<LocalTransform>(ref transform);
            var gridpos=chunks.GetNativeArray<GridPositionData>(ref gridposition);

            int length = gridpos.Length;
            for (int i = 0; i < length; i++)
            {
                LocalTransform trans = objectposition[i];
                GridPositionData grid = gridpos[i];
                int2 newindex = GameUtility.GetGridIndexFromWorldPosition(trans.Position, origin, cellsize);
                grid.IsMoved = false;
                grid.MoveDir = float3.zero;
                if(!grid.CurrentPos.Equals(trans.Position))
                {
                    grid.PreviousPos = grid.CurrentPos;
                    grid.CurrentPos = trans.Position;
                    grid.IsMoved = true;
                    grid.MoveDir = grid.CurrentPos - grid.PreviousPos;
                }
                grid.IsIndexMoved = false;
                if(!grid.Index.Equals(newindex))
                {
                    grid.PreviousPos = grid.CurrentPos;
                    grid.Index = newindex;
                    grid.IsMoved = true;
                }
                gridpos[i] = grid;
            }
        }
    }
}
