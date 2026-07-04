using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Game.Unit;   // CombatDeadTag

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritoryCaptureSystem — 영토 전환 파괴 (capture = 파괴, dwell 유예)
    // ──────────────────────────────────────────────────────────────────────────
    //  룰: 구조물(건물/도로)이 '타팀 영토'에 놓이면 CaptureDoom(데드라인) 부착 →
    //    dwell(게임시간, config) 동안 유지되면 파괴. 그 전에 영토가 되돌아오면 사면(제거).
    //    → 점령지는 항상 깨끗한 빈 땅이 되어 새 주인이 즉시 개발 가능.
    //    → 짧은 밀당(핑퐁)은 dwell이 흡수해 파괴 없음.
    //
    //  판정: 셀 팀(cellTeam) >= 0 && != 소유자 팀. 경합지(-2)/중립(absent)은 캡처 아님.
    //    건물은 config.RequireFullFootprint=1이면 footprint '전체'가 넘어가야 대상(경계 보호).
    //    CaptureExempt(베이스/HQ) / CombatDeadTag(전투 사망 중)는 제외.
    //
    //  파괴: 패스당 config.MaxDestroysPerPass 상한(대량 함락 스파이크 방지, 이월).
    //    건물 = Raze와 동일 정리(Occupancy/GridMap 해제 + StampDirty + destroy).
    //    도로 = Forced RemoveRoadCommand (RoadSystem이 footprint·엔티티·이웃·StampDirty 처리).
    //
    //  ~1초 주기(영토 재계산과 동일 리듬). 데드라인은 GameClock.TotalSeconds(게임초) 기준
    //  → 일시정지 시 자동 정지. 경고 비주얼은 CaptureDoom.DeadlineSeconds를 읽어 표현.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TerritorySystem))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct TerritoryCaptureSystem : ISystem
    {
        double _nextPass;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GameClock>();
            _nextPass = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            double rt = SystemAPI.Time.ElapsedTime;
            if (rt < _nextPass) return;
            _nextPass = rt + 1.0;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) return;
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!SystemAPI.TryGetSingleton<TeamTable>(out var teams)) teams = TeamTable.Identity;
            var cfg = SystemAPI.TryGetSingleton<TerritoryCaptureConfig>(out var c)
                ? c : TerritoryCaptureConfig.Default;

            double now       = clock.TotalSeconds;
            double dwellSecs = (clock.SecondsPerDay / GameClock.HoursPerDay)
                               * math.max(0f, cfg.DwellGameHours);

            bool hasGridMap = SystemAPI.HasSingleton<GridMap>();
            var gridMap     = hasGridMap ? SystemAPI.GetSingleton<GridMap>() : default;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // ── 1) 건물 마킹/사면 ──────────────────────────────────────────
            foreach (var (bfRO, e) in
                     SystemAPI.Query<RefRO<BuildingFootprint>>()
                              .WithNone<CaptureExempt, CombatDeadTag>()
                              .WithEntityAccess())
            {
                var  bf  = bfRO.ValueRO;
                int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);
                bool captured = FootprintCaptured(
                    bf.Origin, eff, teams.Get(bf.OwnerLocalId),
                    cfg.RequireFullFootprint != 0, in layers.TerritoryLayer);

                bool marked = SystemAPI.HasComponent<CaptureDoom>(e);
                if (captured && !marked)
                    ecb.AddComponent(e, new CaptureDoom
                    { DeadlineSeconds = now + dwellSecs, DwellSeconds = dwellSecs });
                else if (!captured && marked)
                    ecb.RemoveComponent<CaptureDoom>(e);   // 사면 — 영토 회복
            }

            // 중립 도로 타겟화용 — owner 팀 정보(LocalId별 대표값) 수집.
            var teamsByLocalId   = new NativeArray<TeamInfoData>(8, Allocator.Temp);
            var hasTeamByLocalId = new NativeArray<byte>(8, Allocator.Temp);
            foreach (var team in SystemAPI.Query<RefRO<TeamInfoData>>())
            {
                int lid = math.clamp(team.ValueRO.LocalID, 0, 7);
                teamsByLocalId[lid] = team.ValueRO;
                hasTeamByLocalId[lid] = 1;
            }
            bool hasGridSettings = SystemAPI.TryGetSingleton<GridSettings>(out var gridSettings);

            // ── 2) 도로 마킹/사면 + 중립(무법지대) 전투 타겟화 ──────────────
            foreach (var (roadRO, e) in
                     SystemAPI.Query<RefRO<Road>>().WithEntityAccess())
            {
                var road = roadRO.ValueRO;
                if (!layers.RoadLayer.TryGetValue(road.FootprintOrigin, out var rc)) continue;
                int  size = math.max(1, (int)road.Size);
                bool captured = FootprintCaptured(
                    road.FootprintOrigin, new int2(size, size), teams.Get(rc.OwnerLocalId),
                    cfg.RequireFullFootprint != 0, in layers.TerritoryLayer);

                bool marked = SystemAPI.HasComponent<CaptureDoom>(e);
                if (captured && !marked)
                    ecb.AddComponent(e, new CaptureDoom
                    { DeadlineSeconds = now + dwellSecs, DwellSeconds = dwellSecs });
                else if (!captured && marked)
                    ecb.RemoveComponent<CaptureDoom>(e);

                // ── 중립 도로 = 전투 타겟 가능 (footprint 전체가 중립일 때만) ──
                //   벽-스팸/고아 파편에 대한 군사 카운터. 보호 영토(자기/타팀/경합지)로
                //   돌아오면 비활성(민간 인프라 보호 복원). 파괴 정리는 BuildingDeathCleanupSystem.
                //   구조 변경은 최초 1회(부착)뿐 — 이후엔 IEnableableComponent 토글(관례 준수).
                if (SystemAPI.HasComponent<CombatDeadTag>(e)) continue;   // 죽는 중 — 건드리지 않음
                bool neutral = cfg.NeutralRoadHealth > 0f
                               && FootprintAllNeutral(road.FootprintOrigin, size, in layers.TerritoryLayer);
                bool hasTgt = SystemAPI.HasComponent<CombatTargetable>(e);
                if (neutral && !hasTgt)
                {
                    // 최초 부착(enabled 기본) — 중립을 한 번이라도 겪은 도로만 전투 컴포넌트 보유.
                    ecb.AddComponent(e, new CombatTargetable { TargetType = CombatTargetMask.Building });
                    ecb.AddComponent(e, new CombatHealth
                    { Health = cfg.NeutralRoadHealth, MaxHealth = cfg.NeutralRoadHealth });
                    int lid = math.clamp(rc.OwnerLocalId, 0, 7);
                    if (hasTeamByLocalId[lid] == 1 && !SystemAPI.HasComponent<TeamInfoData>(e))
                        ecb.AddComponent(e, teamsByLocalId[lid]);
                    // 전투 타겟 요건: LocalTransform — 도로 논리 엔티티에 없을 수 있어 footprint 중심으로 보장.
                    if (hasGridSettings && !SystemAPI.HasComponent<Unity.Transforms.LocalTransform>(e))
                    {
                        byte hStep = layers.TerrainLayer.IsCreated
                                     && layers.TerrainLayer.TryGetValue(road.FootprintOrigin, out var tc)
                            ? tc.Height : (byte)0;
                        ecb.AddComponent(e, Unity.Transforms.LocalTransform.FromPosition(
                            gridSettings.CellCenter(road.FootprintOrigin.x, road.FootprintOrigin.y,
                                new int2(size, size), hStep)));
                    }
                }
                else if (hasTgt)
                {
                    bool enabled = SystemAPI.IsComponentEnabled<CombatTargetable>(e);
                    if (neutral && !enabled)
                    {
                        ecb.SetComponentEnabled<CombatTargetable>(e, true);
                        // 재중립 = 풀 힐(보호 기간의 피해 리셋 + config 변경 반영).
                        ecb.SetComponent(e, new CombatHealth
                        { Health = cfg.NeutralRoadHealth, MaxHealth = cfg.NeutralRoadHealth });
                    }
                    else if (!neutral && enabled)
                        ecb.SetComponentEnabled<CombatTargetable>(e, false);
                }
            }
            teamsByLocalId.Dispose(); hasTeamByLocalId.Dispose();

            // ── 3) 데드라인 지난 것 파괴 (패스당 상한) ─────────────────────
            int destroyed = 0;
            int cap = math.max(1, cfg.MaxDestroysPerPass);
            var stampDirty = new NativeHashSet<int>(8, Allocator.Temp);

            foreach (var (doom, e) in
                     SystemAPI.Query<RefRO<CaptureDoom>>().WithEntityAccess())
            {
                if (destroyed >= cap) break;
                if (now < doom.ValueRO.DeadlineSeconds) continue;

                if (SystemAPI.HasComponent<BuildingFootprint>(e))
                {
                    // 건물 — Raze와 동일 정리 + destroy.
                    var  bf  = SystemAPI.GetComponent<BuildingFootprint>(e);
                    int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);
                    for (int dx = 0; dx < eff.x; dx++)
                    for (int dz = 0; dz < eff.y; dz++)
                    {
                        int2 cell = bf.Origin + new int2(dx, dz);
                        layers.OccupancyLayer.Remove(cell);
                        if (hasGridMap) gridMap.BuildingCells.Remove(cell);
                    }
                    if ((uint)bf.OwnerLocalId < StampLayers.MaxPlayers)
                        stampDirty.Add(bf.OwnerLocalId);
                    ecb.DestroyEntity(e);
                    destroyed++;
                }
                else if (SystemAPI.HasComponent<Road>(e))
                {
                    // 도로 — Forced 철거 명령(RoadSystem이 footprint·엔티티·이웃 일괄 처리).
                    var road = SystemAPI.GetComponent<Road>(e);
                    var cmd = ecb.CreateEntity();
                    ecb.AddComponent(cmd, new RemoveRoadCommand
                    {
                        Cell = road.FootprintOrigin, OwnerLocalId = -1, Forced = 1,
                    });
                    ecb.RemoveComponent<CaptureDoom>(e);   // 재발행 방지(엔티티는 RoadSystem이 파괴)
                    destroyed++;
                }
                else
                {
                    ecb.RemoveComponent<CaptureDoom>(e);   // 알 수 없는 대상 — 마커만 해제
                }
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

        // footprint 전체가 중립(TerritoryLayer에 없음)인가 — 팀/경합지(-2) 셀이 하나라도 있으면 false.
        static bool FootprintAllNeutral(int2 origin, int size, in NativeHashMap<int2, int> territory)
        {
            if (!territory.IsCreated) return true;
            for (int dx = 0; dx < size; dx++)
            for (int dz = 0; dz < size; dz++)
                if (territory.ContainsKey(origin + new int2(dx, dz))) return false;
            return true;
        }

        // footprint가 '타팀 영토'에 캡처됐나. full=true면 전체 셀, false면 한 셀이라도.
        //   셀 팀 >= 0 && != ownerTeam 만 캡처. 경합지(-2)/중립(absent)은 캡처 아님.
        static bool FootprintCaptured(
            int2 origin, int2 size, int ownerTeam, bool full,
            in NativeHashMap<int2, int> territory)
        {
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                bool cellCaptured =
                    territory.TryGetValue(origin + new int2(dx, dz), out int t)
                    && t >= 0 && t != ownerTeam;
                if (full && !cellCaptured) return false;   // 전체 요구 — 하나라도 아니면 탈락
                if (!full && cellCaptured) return true;    // 하나면 충분
            }
            return full;   // full: 전부 통과 → 캡처 / any: 하나도 없음 → 아님
        }
    }
}
