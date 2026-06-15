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

            foreach (var (teamInfo, startPoint) in
                     SystemAPI.Query<
                         RefRO<TeamInfoData>,
                         RefRO<TeamStartPoint>>())
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

                EmitPerimeterRoads(ref ecb, originCell, campSize, ownerLocalId, slot.FactionId);
                requestCount += campSize * 4 - 4;

                // ── 건물·유닛 배치 요청 발행 ──────────────────────────
                for (int i = 0; i < bakedBuf.Length; i++)
                {
                    var b = bakedBuf[i];
                    if (b.FactionId != slot.FactionId) continue;

                    int vk = b.VariantKeyOverride > 0
                        ? b.VariantKeyOverride
                        : variantProfile.Resolve(b.MainKey, who);

                    int2 cell = originCell + b.CellOffset;

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
        // origin = 좌하단 셀, size = N×N 한 변 셀 수
        static void EmitPerimeterRoads(
            ref EntityCommandBuffer ecb,
            int2 origin, int size,
            int ownerLocalId, int factionId)
        {
            for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
            {
                if (x != 0 && x != size - 1 && z != 0 && z != size - 1)
                    continue;

                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new PlaceRoadCommand
                {
                    Cell         = origin + new int2(x, z),
                    OwnerLocalId = ownerLocalId,
                    LaneCount    = 2,
                    FactionId    = factionId,
                    Size         = 1,
                });
            }
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
