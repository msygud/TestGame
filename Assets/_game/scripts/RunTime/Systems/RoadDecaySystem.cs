using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadDecaySystem — 미관리 도로 유예 후 decay (도로 관리시설 모델)
    // ──────────────────────────────────────────────────────────────────────────
    //  설계 (2026-06-24): 도로는 "관리시설(RoadMaintenanceDepot)"의 도로망 BFS
    //  coverage 안에 있어야 유지된다. coverage 밖이면 매일 미관리 카운터가 +1,
    //  임계(DecayDays) 도달 시 강제 철거(RemoveRoadCommand{Forced=1}).
    //  covered로 복귀하면 카운터 0 리셋(살릴 여지). 즉시 파괴 대비 체감 부드러움.
    //
    //  coverage = StampLayers[owner]에 그 셀의 StampKind.RoadMaintenance 도장 유무.
    //    (StampRebuildSystem이 depot 입구에서 도로망 BFS로 찍음 — supplier/warehouse와
    //     같은 stamp 레이어, Kind만 다름.)
    //
    //  예외: RoadCell.Permanent(베이스 외곽 링)는 coverage 무관하게 decay 안 함.
    //  중립/맵 도로(owner ∉ [0,8))는 플레이어 관리 대상이 아니므로 제외.
    //
    //  빈도: GameClock.DayChanged 게이트(하루 1회) — 저빈도 메인스레드. 시민·물류와
    //  동일한 "통계적·점진적" 접근(매 틱 정밀 동기화 불필요).
    //
    //  구조 변경: 직접 호출 없음. 철거는 RemoveRoadCommand를 ECB로 발행 → RoadSystem이
    //  실행(이 시스템은 [UpdateBefore(RoadSystem)] = 같은 프레임 철거).
    //
    //  ※ 현재 1×1 도로 전용(그린/RoadPath 모델과 일관). 멀티셀(size>1)은 footprint
    //    원점 셀에서만 철거를 발행(중복 방지) — 정확한 멀티셀 처리는 추후.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct RoadDecaySystem : ISystem
    {
        /// <summary>미관리 누적 임계(일). 도달 시 도로 철거. (튜닝 대상)</summary>
        const byte DecayDays = 3;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<StampLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 게이트: 하루 경계 프레임에만 진입.
            if (!SystemAPI.GetSingleton<GameClock>().DayChanged)
                return;

            var roadLayer = SystemAPI.GetSingleton<GridLayers>().RoadLayer;
            var stamp     = SystemAPI.GetSingleton<StampLayers>();

            // 키 스냅샷 위로 순회 → 값 수정(re-put)은 안전(키 추가/삭제 아님).
            var cells = roadLayer.GetKeyArray(Allocator.Temp);
            var ecb   = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < cells.Length; i++)
            {
                int2 cell = cells[i];
                if (!roadLayer.TryGetValue(cell, out var rc))
                    continue;

                // 중립/맵 도로 — 플레이어 관리 대상 아님(decay 제외).
                if ((uint)rc.OwnerLocalId >= StampLayers.MaxPlayers)
                    continue;

                // 영구 도로(베이스 링) — coverage 무관하게 유지.
                if (rc.Permanent)
                {
                    if (rc.UnmaintainedDays != 0) { rc.UnmaintainedDays = 0; roadLayer[cell] = rc; }
                    continue;
                }

                bool covered = CoveredByMaintenance(in stamp, rc.OwnerLocalId, cell);

                if (covered)
                {
                    // 관리됨 — 카운터 리셋(살아남음).
                    if (rc.UnmaintainedDays != 0) { rc.UnmaintainedDays = 0; roadLayer[cell] = rc; }
                    continue;
                }

                // 미관리 — 누적(byte 오버플로 방지 캡).
                if (rc.UnmaintainedDays < byte.MaxValue)
                    rc.UnmaintainedDays++;
                roadLayer[cell] = rc;

                // 임계 도달 → 강제 철거 발행(멀티셀은 footprint 원점에서 1회만).
                if (rc.UnmaintainedDays >= DecayDays && cell.Equals(rc.FootprintOrigin))
                {
                    var e = ecb.CreateEntity();
                    ecb.AddComponent(e, new RemoveRoadCommand
                    {
                        Cell         = cell,
                        OwnerLocalId = rc.OwnerLocalId,
                        Forced       = 1,   // 미관리 decay = 소유자 가드 우회(강제)
                    });
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            cells.Dispose();
        }

        /// <summary>StampLayers[owner]에 cell의 RoadMaintenance 도장이 하나라도 있나.</summary>
        static bool CoveredByMaintenance(in StampLayers stamp, int owner, int2 cell)
        {
            var map = stamp[owner];
            if (!map.IsCreated) return false;

            if (!map.TryGetFirstValue(cell, out var v, out var it))
                return false;
            do
            {
                if (v.Kind == StampKind.RoadMaintenance)
                    return true;
            } while (map.TryGetNextValue(out v, ref it));

            return false;
        }
    }
}
