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

            // ── 팀 엔티티 순회 ────────────────────────────────────
            int requestCount = 0;

            foreach (var (teamInfo, startPoint) in
                     SystemAPI.Query<
                         RefRO<TeamInfoData>,
                         RefRO<TeamStartPoint>>())
            {
                int  teamIndex  = startPoint.ValueRO.TeamIndex;
                int2 originCell = startPoint.ValueRO.Cell;

                // FactionConfig에서 FactionId 조회
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

                // User / AI 구분 → VariantProfile 조회 키
                var who = teamInfo.ValueRO.IsPlayer()
                    ? SlotController.User
                    : SlotController.AI;

                // 해당 팩션의 BakedFactionBase 항목 처리
                for (int i = 0; i < bakedBuf.Length; i++)
                {
                    var b = bakedBuf[i];
                    if (b.FactionId != slot.FactionId) continue;

                    // VariantKey 결정
                    int vk = b.VariantKeyOverride > 0
                        ? b.VariantKeyOverride               // 강제 고정
                        : variantProfile.Resolve(b.MainKey, who); // 프로파일 해결

                    int2 cell = originCell + b.CellOffset;

                    // PlaceBuildingRequest 발행
                    var reqEntity = ecb.CreateEntity();
                    ecb.AddComponent(reqEntity, new PlaceBuildingRequest
                    {
                        MainKey    = b.MainKey,
                        VariantKey = vk,
                        Cell       = cell,
                        RotationY  = b.RotationY,
                        TeamIndex  = teamIndex,
                    });

                    requestCount++;
                }
            }

            Debug.Log(
                $"[FactionBaseSpawnSystem] 완료. " +
                $"PlaceBuildingRequest {requestCount}개 발행.");

            Finish(ref ecb, requestCount);
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            state.Enabled = false;
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
