using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Game.Unit;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  BuildingDeathCleanupSystem — 전투로 죽은 건물의 그리드 정리 (Raze의 전투 버전)
    // ──────────────────────────────────────────────────────────────────────────
    //  전투 데미지로 CombatDeadTag가 붙은 건물을, CombatDeathSystem이 엔티티를 destroy
    //  '하기 전에' 가로채:
    //    ① footprint를 OccupancyLayer/GridMap에서 제거 → 땅 회수(재건설 가능).
    //    ② 소유자 stamp 무효화(StampDirtyEvent) → 파괴된 공급자/창고 coverage 갱신.
    //  엔티티 destroy는 하지 않는다 — CombatDeathSystem이 담당(UpdateBefore로 순서 보장).
    //  BuildingFootprint를 가진 dead 엔티티만 대상(유닛 등 비건물은 자동 제외).
    //
    //  순서: CombatDamageApplySystem(CombatDeadTag 부착) → 이 시스템 → CombatDeathSystem(destroy).
    //        같은 프레임 안에서 1회 처리(건물엔 CombatDestroyOnDeath가 붙어 다음 프레임엔 사라짐).
    //  전투로 죽은 '중립 도로'도 여기서 처리 — Forced RemoveRoadCommand로 RoadSystem에 위임.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatDamageApplySystem))]
    [UpdateBefore(typeof(CombatDeathSystem))]
    public partial struct BuildingDeathCleanupSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<CombatDeadTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers      = SystemAPI.GetSingleton<GridLayers>();
            bool hasGridMap = SystemAPI.HasSingleton<GridMap>();
            var gridMap     = hasGridMap ? SystemAPI.GetSingleton<GridMap>() : default;

            var ecb        = new EntityCommandBuffer(Allocator.Temp);
            var stampDirty = new NativeHashSet<int>(8, Allocator.Temp);

            foreach (var bfRO in
                     SystemAPI.Query<RefRO<BuildingFootprint>>().WithAll<CombatDeadTag>())
            {
                var  bf  = bfRO.ValueRO;
                int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);   // 회전 적용 실 footprint

                for (int dx = 0; dx < eff.x; dx++)
                for (int dz = 0; dz < eff.y; dz++)
                {
                    int2 cell = bf.Origin + new int2(dx, dz);
                    layers.OccupancyLayer.Remove(cell);
                    if (hasGridMap) gridMap.BuildingCells.Remove(cell);
                }

                if ((uint)bf.OwnerLocalId < StampLayers.MaxPlayers)
                    stampDirty.Add(bf.OwnerLocalId);
            }

            // ── 전투로 죽은 '중립 도로' → 정상 철거 명령 ────────────────────
            //   도로는 CombatDestroyOnDeath를 안 붙인다 — RoadSystem이 파괴해야
            //   footprint·이웃 비트·Stamp가 정리되므로 Forced RemoveRoadCommand로 위임.
            //   전투 컴포넌트를 즉시 떼서 재발행/재타겟 방지(엔티티는 RoadSystem이 파괴).
            foreach (var (roadRO, e) in
                     SystemAPI.Query<RefRO<Road>>().WithAll<CombatDeadTag>().WithEntityAccess())
            {
                var cmd = ecb.CreateEntity();
                ecb.AddComponent(cmd, new RemoveRoadCommand
                { Cell = roadRO.ValueRO.FootprintOrigin, OwnerLocalId = -1, Forced = 1 });
                ecb.RemoveComponent<CombatDeadTag>(e);
                ecb.SetComponentEnabled<CombatTargetable>(e, false);   // 재타겟 방지(토글 — 구조 변경 없음)
            }

            foreach (var owner in stampDirty)
            {
                var de = ecb.CreateEntity();
                ecb.AddComponent(de, new StampDirtyEvent { OwnerLocalId = owner });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            stampDirty.Dispose();
        }
    }
}
