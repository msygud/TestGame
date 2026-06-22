using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RazeSystem — 광역 파괴 (건물 + orphan 도로 정리)
    //
    //  RazeAreaCommand([Min,Max])를 받아:
    //    ① 사각형과 겹치는 건물을 파괴 — 엔티티 destroy + OccupancyLayer/GridMap 점유 해제
    //       (살아있는 땅이 재사용 가능해짐).
    //    ② 살아있는(파괴 안 된) 같은-소유자 건물에 더는 닿지 않는 도로를 orphan으로 수집
    //       (BlockOps.CollectOrphanRoads — 파괴 건물 인접 시드 + 파면 leaf-prune).
    //    ③ orphan 도로를 강제 RemoveRoadCommand로 발행 → RoadSystem이 footprint/시각/이웃 정리.
    //
    //  소유 무관(공평) — 플레이어·적·AI 누구의 도시든 영역 안이면 동일하게 파괴된다.
    //  시스템 순서: RoadSystem 전(같은 프레임에 도로 철거가 실행되도록).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct RazeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<RazeAreaCommand>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers      = SystemAPI.GetSingleton<GridLayers>();
            bool hasGridMap = SystemAPI.HasSingleton<GridMap>();
            var gridMap     = hasGridMap ? SystemAPI.GetSingleton<GridMap>() : default;

            var ecb   = new EntityCommandBuffer(Allocator.Temp);
            var rects = new NativeList<int4>(8, Allocator.Temp);

            // 1) raze 사각형 수집 (소비)
            foreach (var (cmd, e) in SystemAPI.Query<RefRO<RazeAreaCommand>>().WithEntityAccess())
            {
                int2 mn = math.min(cmd.ValueRO.Min, cmd.ValueRO.Max);
                int2 mx = math.max(cmd.ValueRO.Min, cmd.ValueRO.Max);
                rects.Add(new int4(mn.x, mn.y, mx.x, mx.y));
                ecb.DestroyEntity(e);
            }

            // 2) 건물 분류 — 사각형과 겹치면 파괴(점유 해제+entity destroy), 아니면 라이브로 등록.
            var liveOwner  = new NativeHashMap<int2, int>(256, Allocator.Temp);
            var razedCells = new NativeList<int2>(64, Allocator.Temp);   // 파괴 건물 footprint 셀(orphan 시드)
            int razedBuildings = 0;
            foreach (var (bfRO, e) in SystemAPI.Query<RefRO<BuildingFootprint>>().WithEntityAccess())
            {
                var  bf  = bfRO.ValueRO;
                int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);   // 회전 적용 실 footprint

                if (FootprintIntersectsAny(bf.Origin, eff, in rects))
                {
                    for (int dx = 0; dx < eff.x; dx++)
                    for (int dz = 0; dz < eff.y; dz++)
                    {
                        int2 cell = bf.Origin + new int2(dx, dz);
                        layers.OccupancyLayer.Remove(cell);
                        if (hasGridMap) gridMap.BuildingCells.Remove(cell);
                        razedCells.Add(cell);
                    }
                    ecb.DestroyEntity(e);   // LinkedEntityGroup 시각까지 파괴
                    razedBuildings++;
                }
                else
                {
                    for (int dx = 0; dx < eff.x; dx++)
                    for (int dz = 0; dz < eff.y; dz++)
                        liveOwner.TryAdd(bf.Origin + new int2(dx, dz), bf.OwnerLocalId);
                }
            }

            // 3) orphan 도로 수집 — 파괴 건물에 인접한 '그 건물만 쓰던' 링 도로를 끊고 풀어낸다.
            //    연결 ≥2(살아있는 통과/교차로)는 보존, 파괴 건물과 무관한 도로는 시드에 안 잡힘.
            var removeCells = new NativeList<int2>(128, Allocator.Temp);
            BlockOps.CollectOrphanRoads(in layers.RoadLayer, in liveOwner, in razedCells, ref removeCells);

            // 4) orphan 도로 → 강제 RemoveRoadCommand (RoadSystem이 footprint/시각/이웃/StampDirty 처리)
            for (int i = 0; i < removeCells.Length; i++)
            {
                int2 cell = removeCells[i];
                if (!layers.RoadLayer.TryGetValue(cell, out var rc)) continue;
                var ce = ecb.CreateEntity();
                ecb.AddComponent(ce, new RemoveRoadCommand
                {
                    Cell = cell, OwnerLocalId = rc.OwnerLocalId, Forced = 1,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            Debug.Log($"[Raze] 건물 {razedBuildings} 파괴, orphan 도로 {removeCells.Length} 셀 철거");

            rects.Dispose();
            liveOwner.Dispose();
            razedCells.Dispose();
            removeCells.Dispose();
        }

        // footprint [origin, origin+size-1] 이 사각형 중 하나라도 겹치나(AABB).
        static bool FootprintIntersectsAny(int2 origin, int2 size, in NativeList<int4> rects)
        {
            int2 mx = origin + size - 1;
            for (int i = 0; i < rects.Length; i++)
            {
                int4 r = rects[i];
                if (origin.x <= r.z && mx.x >= r.x && origin.y <= r.w && mx.y >= r.y) return true;
            }
            return false;
        }
    }
}
