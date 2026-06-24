using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadDecayState — 미관리 도로 decay 누적 카운터 (싱글톤)
    // ──────────────────────────────────────────────────────────────────────────
    //  도로 관리시설(RoadMaintenanceDepot)의 coverage(StampKind.RoadMaintenance 도장)
    //  밖에 있는 도로는 '미관리' 상태로 누적되다가, 유예 일수(GraceDays)에 도달하면
    //  강제 철거(RemoveRoadCommand{Forced=1})된다. covered 복귀 시 카운터 리셋.
    //
    //  · 카운터는 RoadCell이 아니라 이 싱글톤의 별도 맵에 둔다 → RoadSystem/RoadCell을
    //    건드리지 않아 도로 시스템과 결합 없음. 셀이 사라지면 orphan 정리 패스로 비운다.
    //  · 베이스 외곽 링(영구 구역 CityZones.Permanent의 링 셀)은 면제 → 베이스 brick 방지.
    // ══════════════════════════════════════════════════════════════════════════
    public struct RoadDecayState : IComponentData
    {
        /// <summary>도로셀 → 미관리 누적 일수. covered/exempt면 제거(=0).</summary>
        public NativeHashMap<int2, int> Unmaintained;

        /// <summary>유예 일수 K. 미관리가 이 일수에 도달하면 강제 철거. 0 이하면 decay 비활성.</summary>
        public int GraceDays;
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
                Unmaintained = new NativeHashMap<int2, int>(1024, Allocator.Persistent),
                GraceDays    = 3,   // 기본 유예 3일. 0 이하로 두면 decay off. (실측 튜닝 대상)
            };
            var e = state.EntityManager.CreateEntity(typeof(RoadDecayState));
            state.EntityManager.SetComponentData(e, s);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<RoadDecayState>()) return;
            var s = SystemAPI.GetSingleton<RoadDecayState>();
            if (s.Unmaintained.IsCreated) s.Unmaintained.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RoadDecaySystem — 미관리 도로 decay (DayChanged 게이트)
    // ──────────────────────────────────────────────────────────────────────────
    //  매 게임일 1회:
    //    ① 영구 구역(베이스) 외곽 링 = 면제 셀 집합 구축(CityZones.Permanent).
    //    ② 모든 도로셀 순회 — 소유 없음/면제/covered면 카운터 리셋,
    //       아니면 +1, GraceDays 도달 시 RemoveRoadCommand{Forced=1} 발행.
    //    ③ orphan 정리 — RoadLayer에 더는 없는 카운터 엔트리 제거.
    //
    //  covered 판정 = 그 셀 owner의 StampLayers 슬롯에 RoadMaintenance 도장이 있나
    //  (StampRebuildSystem이 HourChanged마다 갱신 → Day 경계엔 충분히 최신).
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
            // 게이트: 게임일이 바뀐 프레임에만 진입.
            if (!SystemAPI.GetSingleton<GameClock>().DayChanged)
                return;

            var decay = SystemAPI.GetSingleton<RoadDecayState>();
            if (decay.GraceDays <= 0)
                return; // decay 비활성

            var roadLayer = SystemAPI.GetSingleton<GridLayers>().RoadLayer;
            if (!roadLayer.IsCreated)
                return;
            var stamp = SystemAPI.GetSingleton<StampLayers>();

            // ── ① 면제 셀(영구 구역=베이스 외곽 링) 집합 ──
            var exempt = new NativeHashSet<int2>(256, Allocator.Temp);
            if (SystemAPI.HasSingleton<CityZones>())
            {
                var cz = SystemAPI.GetSingleton<CityZones>();
                var zk = cz.Zones.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < zk.Length; i++)
                {
                    var rec = cz.Zones[zk[i]];
                    if (!rec.Permanent) continue;
                    ZoneOps.CollectRingCells(zk[i], rec.K, rec.Road, ref exempt);
                }
                zk.Dispose();
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // ── ② 도로셀 순회 ──
            var cells = roadLayer.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < cells.Length; i++)
            {
                int2 cell  = cells[i];
                int  owner = roadLayer[cell].OwnerLocalId;

                // 소유 없는 도로 / 면제 셀 → 카운터 리셋 후 건너뜀.
                if ((uint)owner >= StampLayers.MaxPlayers || exempt.Contains(cell))
                {
                    decay.Unmaintained.Remove(cell);
                    continue;
                }

                // covered면 관리됨 → 리셋.
                if (IsCovered(stamp[owner], cell))
                {
                    decay.Unmaintained.Remove(cell);
                    continue;
                }

                // 미관리 → 누적 +1, GraceDays 도달 시 강제 철거.
                int days = (decay.Unmaintained.TryGetValue(cell, out int d) ? d : 0) + 1;
                if (days >= decay.GraceDays)
                {
                    var e = ecb.CreateEntity();
                    ecb.AddComponent(e, new RemoveRoadCommand
                    {
                        Cell         = cell,
                        OwnerLocalId = owner,
                        Forced       = 1,
                    });
                    decay.Unmaintained.Remove(cell);
                }
                else
                {
                    decay.Unmaintained[cell] = days;
                }
            }
            cells.Dispose();

            // ── ③ orphan 정리 — 다른 경로(raze 등)로 사라진 도로의 잔여 카운터 제거 ──
            var keys = decay.Unmaintained.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
                if (!roadLayer.ContainsKey(keys[i]))
                    decay.Unmaintained.Remove(keys[i]);
            keys.Dispose();

            exempt.Dispose();
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
