using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Game.Unit;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  FactionBaseSpawnSystem
    //
    //  게임 시작 시 1회 실행.
    //  각 팀의 팩션 초기 베이스(건물·유닛)를 스타트포인트에 배치한다.
    //
    //  실행 전제조건 (RequireForUpdate):
    //    ① MapLoaded 싱글톤    — 맵 로드 완료
    //    ② FactionConfig 싱글톤 — 로비에서 팀·팩션 배정 완료
    //    ③ VariantProfile 싱글톤 — 베리언트 설정 로드 완료
    //    ④ BakedFactionBase 버퍼 — 서브씬 베이킹 완료
    //
    //  흐름:
    //    TeamInfoData + TeamStartPoint 엔티티 순회
    //      → FactionConfig.Slots[teamIndex].FactionId 확인
    //      → BakedFactionBase 중 해당 FactionId 항목 탐색
    //      → SlotController 결정 (IsPlayer → User, 아니면 AI)
    //      → VariantKey 결정
    //          · VariantKeyOverride > 0 → 고정값 사용
    //          · VariantKeyOverride = 0 → VariantProfile.Resolve
    //      → PlaceBuildingRequest 발행 (BuildingPlacementSystem이 처리)
    //    완료 → FactionBaseSpawnDone 태그 생성 + state.Enabled = false
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoaderSystem))]
    public partial struct FactionBaseSpawnSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapLoaded>();
            state.RequireForUpdate<FactionConfig>();
            state.RequireForUpdate<VariantProfile>();
            state.RequireForUpdate<RoadKeyLookup>();
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<BakedFactionBase>()
                    .Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 이미 완료된 경우 건너뜀 (안전장치)
            if (SystemAPI.HasSingleton<FactionBaseSpawnDone>()) return;

            var factionConfig  = SystemAPI.GetSingleton<FactionConfig>();
            var variantProfile = SystemAPI.GetSingleton<VariantProfile>();
            var roadKeyLookup  = SystemAPI.GetSingleton<RoadKeyLookup>();
            var ecb            = new EntityCommandBuffer(Allocator.Temp);

            // ── BakedFactionBase 버퍼 가져오기 ───────────────────
            DynamicBuffer<BakedFactionBase> bakedBuf = default;
            bool hasBuf = false;
            foreach (var buf in
                     SystemAPI.Query<DynamicBuffer<BakedFactionBase>>())
            {
                bakedBuf = buf;
                hasBuf   = true;
                break;
            }

            if (!hasBuf)
            {
                Debug.LogWarning(
                    "[FactionBaseSpawnSystem] BakedFactionBase 버퍼가 없습니다.\n" +
                    "FactionBaseAuthoring을 서브씬에 배치하고 Re-bake 하세요.");
                Finish(ref ecb, 0);
                ecb.Playback(state.EntityManager);
                ecb.Dispose();
                state.Enabled = false;
                return;
            }

            // ── BakedFactionMeta 버퍼 가져오기 ──────────────────────
            DynamicBuffer<BakedFactionMeta> metaBuf = default;
            foreach (var buf in SystemAPI.Query<DynamicBuffer<BakedFactionMeta>>())
            {
                metaBuf = buf;
                break;
            }

            // ── 팀 엔티티 순회 ────────────────────────────────────
            int requestCount = 0;

            foreach (var (teamInfo, startPoint, teamEntity) in
                     SystemAPI.Query<
                         RefRO<TeamInfoData>,
                         RefRO<TeamStartPoint>>().WithEntityAccess())
            {
                int teamIndex    = startPoint.ValueRO.TeamIndex;
                int ownerLocalId = teamInfo.ValueRO.LocalID;
                int2 originCell  = startPoint.ValueRO.Cell;

                if (!factionConfig.Slots.TryGetValue(teamIndex, out var slot))
                {
                    Debug.LogWarning(
                        $"[FactionBaseSpawnSystem] TeamIndex={teamIndex} 슬롯 없음.");
                    continue;
                }

                if (slot.FactionId < 0)
                {
                    Debug.LogWarning(
                        $"[FactionBaseSpawnSystem] TeamIndex={teamIndex} " +
                        "FactionId 미배정 (-1). SkirmishLobby의 슬롯에 FactionId를 설정하세요.");
                    continue;
                }

                var who = teamInfo.ValueRO.IsPlayer()
                    ? SlotController.User
                    : SlotController.AI;

                // ── 베이스캠프 외곽 도로 발행 ─────────────────────────
                int campSize = 8;
                if (metaBuf.IsCreated)
                {
                    for (int m = 0; m < metaBuf.Length; m++)
                    {
                        if (metaBuf[m].FactionId == slot.FactionId)
                        {
                            campSize = metaBuf[m].BaseCampSize;
                            break;
                        }
                    }
                }

                // 시작점(originCell)은 도로 링의 좌하단 원점 — 음수 좌표로 빠지지 않고
                // 시작점에서 우상향(+X+Z)으로만 확장한다는 규약을 그대로 지킨다.
                // 건물 영역(campSize×campSize)은 도로 링 두께(roadSize)만큼 안쪽으로 들어간다.
                byte roadSize  = roadKeyLookup.GetSize(slot.FactionId);
                int2 buildOrigin = originCell + new int2(roadSize, roadSize);

                requestCount += EmitPerimeterRoads(
                    ref ecb, originCell, campSize, roadSize, ownerLocalId, slot.FactionId);

                // ── 블록 그리드 정의 부착 (AI 도시 성장이 이걸 따라 블록식으로 자람) ──
                //   Anchor=originCell, Block=campSize, Road=roadSize → 베이스=블록(0,0).
                ecb.AddComponent(teamEntity, new CityGrid
                {
                    Anchor    = originCell,
                    Block     = campSize,
                    Road      = roadSize,
                    FactionId = slot.FactionId,
                    // 게임 시작마다 다른 시드(UnityEngine.Random은 세션별 자동 시드).
                    Seed      = (uint)UnityEngine.Random.Range(1, int.MaxValue),
                });

                // ── 건물·유닛 배치 요청 발행 ──────────────────────────
                for (int i = 0; i < bakedBuf.Length; i++)
                {
                    var b = bakedBuf[i];
                    if (b.FactionId != slot.FactionId) continue;

                    int vk = b.VariantKeyOverride > 0
                        ? b.VariantKeyOverride
                        : variantProfile.Resolve(b.MainKey, who);

                    int2 cell = buildOrigin + b.CellOffset;

                    var reqEntity = ecb.CreateEntity();
                    ecb.AddComponent(reqEntity, new PlaceBuildingRequest
                    {
                        MainKey           = b.MainKey,
                        VariantKey        = vk,
                        Cell              = cell,
                        RotationY         = b.RotationY,
                        OwnerLocalId      = ownerLocalId,
                        FactionId         = slot.FactionId,
                        RequireRoadAccess = true,
                    });

                    requestCount++;
                }
            }

            Debug.Log(
                $"[FactionBaseSpawnSystem] 완료. " +
                $"커맨드 {requestCount}개 발행.");

            Finish(ref ecb, requestCount);
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            state.Enabled = false;
        }

        // ── 베이스캠프 외곽 도로 발행 ─────────────────────────────
        // origin = 시작점(TeamStartPoint) — 도로 링의 좌하단 원점.
        // "시작점에서 우상향(+X+Z)으로만 확장" 규약을 지키기 위해
        // 음수 좌표로 빠지지 않는다 — 건물 영역(campSize)이 도로 링
        // 두께(roadSize)만큼 안쪽으로 들어가는 쪽으로 보정한다
        // (FactionBaseSpawnSystem.OnUpdate의 buildOrigin 참고).
        //
        // campSize = 건물이 들어갈 안쪽 영역의 한 변 셀 수 (도로 제외, 순수 내부).
        // roadSize = RoadPrefabRegistry.DefaultSize(도로 한 칸의 한 변 셀 수).
        // 링 한 변의 매크로 셀 수 = (campSize/step) + 2(안쪽 둘레 + 바깥 한 겹).
        // campSize가 step으로 나누어떨어지지 않으면 안쪽 가장자리 일부가
        // 비어 남을 수 있다(추후 정밀 핏 보정 필요 시 별도 처리).
        //
        // 비주얼 방향: 블록(매크로) 좌표 기준으로 실제 이웃 블록이 링 위에
        // 있는지 확인해 RoadDir을 계산한다. 셀 단위 ComputeDirections는
        // 블록 크기>1일 때 블록 내부 셀까지 "연결됨"으로 잡혀 오염되므로
        // 쓰지 않는다(PlaceRoadCommand.VisualDirectionsOverride로 강제).
        static int EmitPerimeterRoads(
            ref EntityCommandBuffer ecb,
            int2 origin, int campSize, byte roadSize,
            int ownerLocalId, int factionId)
        {
            int step       = math.max(1, roadSize);
            int innerMacro = math.max(1, campSize / step);
            int ringMacro  = innerMacro + 2;   // 안쪽 + 바깥 한 겹씩
            int emitted    = 0;

            for (int mx = 0; mx < ringMacro; mx++)
            for (int mz = 0; mz < ringMacro; mz++)
            {
                bool onRing = mx == 0 || mx == ringMacro - 1 || mz == 0 || mz == ringMacro - 1;
                if (!onRing) continue;

                RoadDir dir = RoadDir.None;
                if (IsRingMacroCell(mx + 1, mz, ringMacro)) dir |= RoadDir.E;
                if (IsRingMacroCell(mx - 1, mz, ringMacro)) dir |= RoadDir.W;
                if (IsRingMacroCell(mx, mz + 1, ringMacro)) dir |= RoadDir.N;
                if (IsRingMacroCell(mx, mz - 1, ringMacro)) dir |= RoadDir.S;

                bool hasEW = (dir & (RoadDir.E | RoadDir.W)) != RoadDir.None;
                bool hasNS = (dir & (RoadDir.N | RoadDir.S)) != RoadDir.None;
                var axis = (hasEW && hasNS) ? RoadPlacedAxis.Any
                         : hasEW            ? RoadPlacedAxis.EW
                                            : RoadPlacedAxis.NS;

                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new PlaceRoadCommand
                {
                    Cell                      = origin + new int2(mx * step, mz * step),
                    OwnerLocalId              = ownerLocalId,
                    LaneCount                 = 2,
                    FactionId                 = factionId,
                    Size                      = (byte)step,
                    VisualDirectionsOverride  = dir,
                    Axis                      = axis,
                });
                emitted++;
            }

            return emitted;
        }

        // 매크로 좌표(mx,mz)가 링(테두리) 위에 있는지 — 범위 밖이면 false.
        static bool IsRingMacroCell(int mx, int mz, int ringMacro)
        {
            if (mx < 0 || mx >= ringMacro || mz < 0 || mz >= ringMacro) return false;
            return mx == 0 || mx == ringMacro - 1 || mz == 0 || mz == ringMacro - 1;
        }

        // ── 완료 마커 생성 ────────────────────────────────────────
        static void Finish(ref EntityCommandBuffer ecb, int count)
        {
            var e = ecb.CreateEntity();
            ecb.SetName(e, "FactionBaseSpawnDone");
            ecb.AddComponent<FactionBaseSpawnDone>(e);
        }
    }
}
