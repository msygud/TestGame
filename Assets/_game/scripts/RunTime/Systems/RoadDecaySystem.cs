using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadDecayState — 미관리 도로 decay 누적 카운터 (싱글톤)
    // ──────────────────────────────────────────────────────────────────────────
    //  도로 관리시설(RoadMaintenanceDepot)의 coverage(StampKind.RoadMaintenance 도장)
    //  밖에 있는 도로는 **미관리가 된 시각부터 셀별 연속 타이머**가 흐르고, 유예
    //  (GraceDays × 하루초)가 지나면 강제 철거(RemoveRoadCommand{Forced=1})된다.
    //  covered 복귀 시 타이머 리셋. **전역 day 일괄 아님** — 셀마다 미관리 시작 시점이
    //  달라 철거가 자연 분산되고, 진행도를 telegraph로 시각화해 압박감을 준다.
    //
    //  · 타이머는 RoadCell이 아니라 이 싱글톤의 별도 맵에 둔다 → RoadSystem/RoadCell을
    //    건드리지 않아 도로 시스템과 결합 없음. 셀이 사라지면 orphan 정리 패스로 비운다.
    //  · 베이스 외곽 링(Exempt 셀)은 면제 → 베이스 brick 방지. Exempt는 FactionBaseSpawnSystem이
    //    베이스 생성 시 **모든 팀(휴먼·AI)** 의 외곽 링 셀을 등록한다(구역 시스템과 무관 —
    //    영구 구역 등록이 AiCityGrowthSystem(AI 전용)에서만 일어나 휴먼 베이스가 빠지던 버그 회피).
    // ══════════════════════════════════════════════════════════════════════════
    public struct RoadDecayState : IComponentData
    {
        /// <summary>도로셀 → 미관리가 시작된 게임시각(GameClock.TotalSeconds). covered/exempt면 제거.</summary>
        public NativeHashMap<int2, double> Unmaintained;

        /// <summary>유예 일수 K. 미관리가 이 일수에 도달하면 강제 철거. 0 이하면 decay 비활성.</summary>
        public int GraceDays;

        /// <summary>decay 면제 셀(베이스 외곽 링). FactionBaseSpawnSystem이 베이스 생성 시 채운다.</summary>
        public NativeHashSet<int2> Exempt;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RoadDecayInitSystem — RoadDecayState alloc/dispose (GridInit 수명주기 패턴)
    // ══════════════════════════════════════════════════════════════════════════
    public partial struct RoadDecayInitSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<RoadDecayState>()) return;

            var s = new RoadDecayState
            {
                Unmaintained = new NativeHashMap<int2, double>(1024, Allocator.Persistent),
                GraceDays    = 3,   // 기본 유예 3일. 0 이하로 두면 decay off. (실측 튜닝 대상)
                Exempt       = new NativeHashSet<int2>(256, Allocator.Persistent),
            };
            var e = state.EntityManager.CreateEntity(typeof(RoadDecayState));
            state.EntityManager.SetComponentData(e, s);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<RoadDecayState>()) return;
            var s = SystemAPI.GetSingleton<RoadDecayState>();
            if (s.Unmaintained.IsCreated) s.Unmaintained.Dispose();
            if (s.Exempt.IsCreated)       s.Exempt.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RoadDecaySystem — 미관리 도로 decay (DayChanged 게이트)
    // ──────────────────────────────────────────────────────────────────────────
    //  매 게임시간(HourChanged) 점검:
    //    ① 면제 셀 = RoadDecayState.Exempt (FactionBaseSpawnSystem이 채운 모든 팀 베이스 링).
    //    ② 모든 도로셀 순회 — 소유 없음/면제/covered면 타이머 리셋,
    //       미관리면 타이머 시작(없을 때 now 기록) / 경과 ≥ 유예면 RemoveRoadCommand{Forced=1}.
    //    ③ orphan 정리 — RoadLayer에 더는 없는 타이머 엔트리 제거.
    //
    //  covered 판정 = 그 셀 owner의 StampLayers 슬롯에 RoadMaintenance 도장이 있나.
    //  유예 = GraceDays × SecondsPerDay(게임초). 셀별 (now − since) ≥ 유예면 철거 →
    //  전역 day 일괄이 아니라 셀마다 제 시점에 만료(자연 분산). HourChanged는 '점검 주기'일
    //  뿐, 만료 판정은 연속 시각 기반. 진행도는 RoadDecayTelegraphSystem이 시각화(압박감).
    //
    //  [UpdateBefore(RoadSystem)] — 같은 프레임에 도로 철거가 실행되도록.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct RoadDecaySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<StampLayers>();
            state.RequireForUpdate<RoadDecayState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 게이트: 게임시간(hour) 경계마다 점검(전역 day 일괄 아님 — 만료는 연속 시각).
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!clock.HourChanged)
                return;

            var decay = SystemAPI.GetSingleton<RoadDecayState>();
            if (decay.GraceDays <= 0)
                return; // decay 비활성

            var roadLayer = SystemAPI.GetSingleton<GridLayers>().RoadLayer;
            if (!roadLayer.IsCreated)
                return;
            var stamp = SystemAPI.GetSingleton<StampLayers>();

            double now   = clock.TotalSeconds;                            // 현재 게임시각
            double grace = (double)decay.GraceDays * clock.SecondsPerDay; // 유예(게임초)

            // 면제 = 베이스 외곽 링(FactionBaseSpawnSystem이 모든 팀 등록).
            var exempt = decay.Exempt;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 도로셀 순회 — 셀별 미관리 연속 타이머.
            var cells = roadLayer.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < cells.Length; i++)
            {
                int2 cell  = cells[i];
                int  owner = roadLayer[cell].OwnerLocalId;

                // 소유 없음 / 면제 / covered → 타이머 리셋.
                if ((uint)owner >= StampLayers.MaxPlayers
                    || (exempt.IsCreated && exempt.Contains(cell))
                    || IsCovered(stamp[owner], cell))
                {
                    decay.Unmaintained.Remove(cell);
                    continue;
                }

                // 미관리 → 타이머 시작(없으면 now 기록) / 경과 ≥ 유예면 철거.
                if (!decay.Unmaintained.TryGetValue(cell, out double since))
                {
                    decay.Unmaintained[cell] = now;   // 이 셀의 미관리 타이머 시작
                }
                else if (now - since >= grace)
                {
                    var e = ecb.CreateEntity();
                    ecb.AddComponent(e, new RemoveRoadCommand
                    {
                        Cell = cell, OwnerLocalId = owner, Forced = 1,
                    });
                    decay.Unmaintained.Remove(cell);
                }
            }
            cells.Dispose();

            // orphan 정리 — 다른 경로(raze 등)로 사라진 도로의 잔여 타이머 제거.
            var keys = decay.Unmaintained.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
                if (!roadLayer.ContainsKey(keys[i]))
                    decay.Unmaintained.Remove(keys[i]);
            keys.Dispose();

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>owner 슬롯 stamp에 이 셀의 RoadMaintenance 도장이 하나라도 있나.</summary>
        static bool IsCovered(in NativeParallelMultiHashMap<int2, SupplierRef> map, int2 cell)
        {
            if (!map.IsCreated) return false;
            if (!map.TryGetFirstValue(cell, out var v, out var it)) return false;
            do
            {
                if (v.Kind == StampKind.RoadMaintenance)
                    return true;
            }
            while (map.TryGetNextValue(out v, ref it));
            return false;
        }
    }
}
