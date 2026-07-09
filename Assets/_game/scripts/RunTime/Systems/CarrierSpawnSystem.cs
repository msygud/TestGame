using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CarrierSpawnSystem
    //
    //  LogisticsCarrierRequest를 소비해 운반자 엔티티를 스폰한다.
    //
    //  처리 순서:
    //    1. CivilianBFS로 SourceRoadCell → DestRoadCell 경로 계산.
    //    2. 경로 없으면 요청 삭제 후 스킵 (창고·건물 간 도로 미연결).
    //    3. 경로 있으면 CarrierPrefabSingleton 프리팹 인스턴스화.
    //       CarrierTag / CarrierState / CarrierPathCell 버퍼 추가.
    //    4. 요청 엔티티 삭제.
    //
    //  재고 영향 없음 — 순수 비주얼.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(LogisticsPullSystem))]
    // 변환 그룹보다 먼저 — 스폰 프레임 안에 LTW 계산(1프레임 원점 플래시 방지).
    [UpdateBefore(typeof(Unity.Transforms.TransformSystemGroup))]
    public partial struct CarrierSpawnSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CarrierPrefabSingleton>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GridSettings>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var prefabSingleton = SystemAPI.GetSingleton<CarrierPrefabSingleton>();
            if (prefabSingleton.Prefab == Entity.Null) return;

            var layers      = SystemAPI.GetSingleton<GridLayers>();
            var gridSettings = SystemAPI.GetSingleton<GridSettings>();
            var ecb         = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, reqEntity) in
                     SystemAPI.Query<RefRO<LogisticsCarrierRequest>>().WithEntityAccess())
            {
                var path = new NativeList<int2>(64, Allocator.Temp);

                bool found = CivilianBFS.FindPath(
                    req.ValueRO.SourceRoadCell,
                    req.ValueRO.DestRoadCell,
                    in layers.RoadLayer,
                    req.ValueRO.OwnerLocalId,
                    ref path,
                    Allocator.Temp);

                if (found && path.Length > 0)
                {
                    float3 startPos = gridSettings.CellCenter(path[0].x, path[0].y) + new float3(0, 0.5f, 0);

                    var carrier = ecb.Instantiate(prefabSingleton.Prefab);
                    ecb.SetComponent(carrier, LocalTransform.FromPosition(startPos));
                    ecb.AddComponent<CarrierTag>(carrier);
                    ecb.AddComponent(carrier, new CarrierState { PathIndex = 0, MoveTimer = 0f });
                    // 소유: SharedComponent (플레이어 LocalId별 청크 분리 — 소유물 공통 골격).
                    ecb.AddSharedComponent(carrier, new OwnerShared(req.ValueRO.OwnerLocalId));

                    var buf = ecb.AddBuffer<CarrierPathCell>(carrier);
                    for (int i = 0; i < path.Length; i++)
                        buf.Add(new CarrierPathCell { Cell = path[i] });
                }

                path.Dispose();
                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
