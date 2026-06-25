using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RazeSystem — 광역 파괴 (건물 + 구역 도로 정리)
    //
    //  RazeAreaCommand([Min,Max])를 받아:
    //    ① 사각형과 겹치는 건물을 파괴 — 엔티티 destroy + OccupancyLayer/GridMap 점유 해제
    //       (살아있는 땅이 재사용 가능해짐).
    //    ② 파괴 건물을 그 건물이 속한 '구역'(AI 블록)에 귀속(BuildingCount −1, ZoneOps).
    //       구역이 비면(건물 0) 그 구역의 도로 링 refcount를 감소 → 공유 안 되는(0이 된)
    //       셀만 강제 RemoveRoadCommand로 제거. 공유 변·통과 도로는 refcount≥1로 보존.
    //    ③ 도로가 제거된 팀에 NetworkRepairRequest 발행 → NetworkRepairSystem이 단절 섬 재연결.
    //
    //  degree가 아니라 '구역 소유/공유'로 판단 → 격자·링 도시에서도 빈 블록 링이 정확히 풀린다.
    //  플레이어 도로는 구역 미등록(수동 관리) → 정리·재연결 안 함. AI/베이스만 자동.
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
            bool hasZones   = SystemAPI.HasSingleton<CityZones>();
            var zones       = hasZones ? SystemAPI.GetSingleton<CityZones>() : default;

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

            // 2) 건물 파괴 + 구역 귀속 — 사각형과 겹친 건물만 destroy(점유 해제) 후 구역에 사망 보고.
            var deadZones    = new NativeList<int2>(16, Allocator.Temp);   // 비워진 구역 원점
            var repairOwners = new NativeHashSet<int>(8, Allocator.Temp);  // 재연결 검증 대상 팀
            var stampDirty   = new NativeHashSet<int>(8, Allocator.Temp);  // stamp 재빌드 대상 팀
            int razedBuildings = 0;
            foreach (var (bfRO, e) in SystemAPI.Query<RefRO<BuildingFootprint>>().WithEntityAccess())
            {
                var  bf  = bfRO.ValueRO;
                int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);   // 회전 적용 실 footprint
                if (!FootprintIntersectsAny(bf.Origin, eff, in rects)) continue;

                for (int dx = 0; dx < eff.x; dx++)
                for (int dz = 0; dz < eff.y; dz++)
                {
                    int2 cell = bf.Origin + new int2(dx, dz);
                    layers.OccupancyLayer.Remove(cell);
                    if (hasGridMap) gridMap.BuildingCells.Remove(cell);
                }
                ecb.DestroyEntity(e);   // LinkedEntityGroup 시각까지 파괴
                razedBuildings++;

                // 파괴된 건물이 공급자/창고/관리시설이었을 수 있음 → 소유자 stamp 재빌드 필요.
                //   안 하면 파괴된 depot의 coverage 도장이 stamp에 남아 그 도로가 영원히
                //   '관리됨'으로 보여 decay가 안 됨(도로 제거가 없을 때 dirty 누락 버그).
                if ((uint)bf.OwnerLocalId < StampLayers.MaxPlayers)
                    stampDirty.Add(bf.OwnerLocalId);

                // 구역 귀속 — 빈 구역이면 해체 목록에, 어느 경우든 소유 팀은 재연결 검증 대상.
                if (hasZones && ZoneOps.AttributeDeath(zones, bf.Origin, out int2 zO, out int zOwner))
                {
                    deadZones.Add(zO);
                    repairOwners.Add(zOwner);
                }
            }

            // 3) 빈 구역 해체 → 공유 안 되는 도로 셀 수집
            var removeCells = new NativeList<int2>(128, Allocator.Temp);
            for (int i = 0; i < deadZones.Length; i++)
                ZoneOps.ReleaseZone(zones, deadZones[i], ref removeCells);

            // 4) 도로 → 강제 RemoveRoadCommand (RoadSystem이 footprint/시각/이웃/StampDirty 처리)
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

            // 5) 도로 제거가 일어난 팀에 재연결 요청 — NetworkRepairSystem이 RoadSystem 이후 처리.
            if (removeCells.Length > 0)
            {
                foreach (var owner in repairOwners)
                {
                    var re = ecb.CreateEntity();
                    ecb.AddComponent(re, new NetworkRepairRequest { OwnerLocalId = owner });
                }
            }

            // 6) 건물 파괴 팀의 stamp 무효화 — 다음 재빌드에서 파괴된 공급자/창고/관리시설의
            //    coverage 도장이 사라진다(미관리로 바뀐 도로는 그 뒤 RoadDecaySystem이 철거).
            foreach (var owner in stampDirty)
            {
                var de = ecb.CreateEntity();
                ecb.AddComponent(de, new StampDirtyEvent { OwnerLocalId = owner });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            Debug.Log($"[Raze] 건물 {razedBuildings} 파괴, 구역 {deadZones.Length} 해체, 도로 {removeCells.Length} 셀 철거");

            rects.Dispose();
            deadZones.Dispose();
            repairOwners.Dispose();
            stampDirty.Dispose();
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
