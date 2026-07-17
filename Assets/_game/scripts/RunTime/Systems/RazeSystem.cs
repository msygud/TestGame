using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RazeSystem — 광역 파괴 (건물)
    //
    //  RazeAreaCommand([Min,Max])를 받아:
    //    ① 사각형과 겹치는 건물을 파괴 — 엔티티 destroy + OccupancyLayer/GridMap 점유 해제
    //       (살아있는 땅이 재사용 가능해짐).
    //    ② 파괴된 건물이 공급자/창고였을 수 있으므로 소유 팀에 StampDirtyEvent 발행
    //       (다음 재빌드에서 coverage 도장 갱신).
    //
    //  소유 무관(공평) — 플레이어·적·AI 누구의 도시든 영역 안이면 동일하게 파괴된다.
    //  도로 정리는 Territory 시스템이 담당(남의 영역 안의 도로·건설물 파괴).
    //  시스템 순서: RoadSystem 전(같은 프레임에 후속 도로 철거가 실행되도록).
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
            // 인간 유저 수동 철거 사각형(Human=1) — 겹친 주택의 거주민을 DisplacedCitizen
            //   목록에 등재(유지/해산 선택 UI, 2026-07-17). 자원은 어느 경로든 여기서
            //   승계 기록 없음 = 전량 파괴(승계는 AI 재개발 전용 — StockInheritance 참조).
            var humanRects = new NativeList<int4>(4, Allocator.Temp);

            // 1) raze 사각형 수집 (소비)
            foreach (var (cmd, e) in SystemAPI.Query<RefRO<RazeAreaCommand>>().WithEntityAccess())
            {
                int2 mn = math.min(cmd.ValueRO.Min, cmd.ValueRO.Max);
                int2 mx = math.max(cmd.ValueRO.Min, cmd.ValueRO.Max);
                rects.Add(new int4(mn.x, mn.y, mx.x, mx.y));
                if (cmd.ValueRO.Human != 0) humanRects.Add(new int4(mn.x, mn.y, mx.x, mx.y));
                ecb.DestroyEntity(e);
            }

            // 2) 건물 파괴 — 사각형과 겹친 건물만 destroy(점유 해제).
            var stampDirty   = new NativeHashSet<int>(8, Allocator.Temp);  // stamp 재빌드 대상 팀
            var razedHomes   = new NativeHashSet<Entity>(8, Allocator.Temp);  // 인간 철거 주택(거주민 등재용)
            int razedBuildings = 0;
            foreach (var (bfRO, e) in SystemAPI.Query<RefRO<BuildingFootprint>>().WithEntityAccess())
            {
                var  bf  = bfRO.ValueRO;
                int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);   // 회전 적용 실 footprint
                if (!FootprintIntersectsAny(bf.Origin, eff, in rects)) continue;

                // 인간 철거 + 주택 → 거주민 목록 대상(파괴 전에 표시 — 아래 3.5에서 등재).
                if (!humanRects.IsEmpty && SystemAPI.HasComponent<ResidenceBuilding>(e)
                    && FootprintIntersectsAny(bf.Origin, eff, in humanRects))
                    razedHomes.Add(e);

                for (int dx = 0; dx < eff.x; dx++)
                for (int dz = 0; dz < eff.y; dz++)
                {
                    int2 cell = bf.Origin + new int2(dx, dz);
                    layers.OccupancyLayer.Remove(cell);
                    if (hasGridMap) gridMap.BuildingCells.Remove(cell);
                }
                ecb.DestroyEntity(e);   // LinkedEntityGroup 시각까지 파괴
                razedBuildings++;

                // 파괴된 건물이 공급자/창고였을 수 있음 → 소유자 stamp 재빌드 필요.
                if ((uint)bf.OwnerLocalId < StampLayers.MaxPlayers)
                    stampDirty.Add(bf.OwnerLocalId);
            }

            // 3) 건물 파괴 팀의 stamp 무효화 — 다음 재빌드에서 파괴된 공급자/창고의
            //    coverage 도장이 사라진다.
            foreach (var owner in stampDirty)
            {
                var de = ecb.CreateEntity();
                ecb.AddComponent(de, new StampDirtyEvent { OwnerLocalId = owner });
            }

            // 3.5) 인간 철거 주택 거주민 등재(2026-07-17) — Home이 철거 대상인 시민을
            //   DisplacedCitizen 목록(싱글톤 buffer)에 올린다. 시민 자체는 건드리지 않음:
            //   건물 파괴 후 DeadReferenceReclaim이 UnassignedTag(재하우징 큐)를 붙이고,
            //   UI(DisplacedCitizensDialog)에서 유저가 유지(목록 제거만)/해산(파괴)을 선택.
            //   건물엔 명단이 없으므로(§2.3) 시민 역쿼리 1회 — 인간 철거는 저빈도 이벤트.
            int displaced = 0;
            if (!razedHomes.IsEmpty)
            {
                var listEntity = SystemAPI.TryGetSingletonEntity<DisplacedCitizen>(out var le)
                    ? le : state.EntityManager.CreateEntity(typeof(DisplacedCitizen));
                var list = state.EntityManager.GetBuffer<DisplacedCitizen>(listEntity);
                foreach (var (res, ce) in
                         SystemAPI.Query<RefRO<CitizenResidence>>()
                                  .WithAll<CitizenTag>().WithEntityAccess())
                {
                    if (res.ValueRO.Home == Entity.Null || !razedHomes.Contains(res.ValueRO.Home))
                        continue;
                    list.Add(new DisplacedCitizen { Citizen = ce });
                    displaced++;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            Debug.Log($"[Raze] 건물 {razedBuildings} 파괴"
                + (displaced > 0 ? $", 이재민 {displaced}명 목록 등재" : ""));

            rects.Dispose();
            humanRects.Dispose();
            stampDirty.Dispose();
            razedHomes.Dispose();
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
