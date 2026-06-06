#if UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════
    //  StampTestBootstrap  — stamp 파이프라인 런타임 확인용 임시 시스템
    // ──────────────────────────────────────────────────────────────────────
    //  목적: SO/Baker를 건드리지 않고, stamp BFS가 "실제로 도장을 찍는지"를
    //        Play 모드에서 눈으로 확인한다.
    //
    //  단계(프레임 카운터로 분리 — 도로가 RoadLayer에 등록될 시간 확보):
    //    F0  : 도로 ㄱ자로 몇 칸 PlaceRoadCommand 발행 (실제 도로 경로 그대로).
    //    F2  : 공급자 건물 1개 직접 생성 — BuildingFootprint + BuildingEntrance
    //          + StampSupplier(Relief/MaxDist 직접 박음). 입구가 도로 첫 칸을
    //          향하도록 배치. 그리고 StampDirtyEvent 발행.
    //          → StampDirtyCollectSystem이 DirtyMask 세팅
    //          → StampRebuildSystem이 그 슬롯 재BFS → 도장.
    //    이후: StampDumpSystem이 주기적으로 stamp 내용을 로그로 출력.
    //
    //  ※ 정식 스폰 파이프라인(SpawnRequest→SpawnSystem)을 일부러 우회한다.
    //    공급자 엔티티를 직접 만들어 footprint/입구/Relief를 한자리에서 명확히
    //    세팅 → SpawnSystem 타이밍과 분리, 확인을 단순화.
    //  ※ 도로는 우회하지 않는다 — PlaceRoadCommand 실경로를 그대로 타서
    //    RoadLayer 등록 + dirty 발행까지 실제 동작을 검증.
    //
    //  사용법: 이 파일을 프로젝트에 넣고 Play. Console에서 도장 로그 확인.
    //          확인 끝나면 파일 삭제(또는 TEST_STAMP 심볼로 토글).
    // ══════════════════════════════════════════════════════════════════════
    public partial struct StampTestBootstrap : ISystem
    {
        int _frame;
        bool _roadsIssued;
        bool _supplierSpawned;

        // 테스트 파라미터 ─────────────────────────────────────────────────
        const int   TestOwner   = 0;          // 플레이어 슬롯 0
        const int   TestFaction = 0;
        const int   SupplyRange = 0;          // 0 = 무제한 (작은 맵이니 전부 도달)
        static readonly NeedType TestRelief = (NeedType)0x1UL; // 임시 비트 1개

        public void OnCreate(ref SystemState state)
        {
            _frame = 0;
            _roadsIssued = false;
            _supplierSpawned = false;
            // 그리드/도로/stamp 인프라가 준비된 뒤에만 동작.
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<StampLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frame++;

            // ── F0: 도로 ㄱ자 발행 (실제 PlaceRoadCommand 경로) ───────────
            if (!_roadsIssued)
            {
                var ecb = new EntityCommandBuffer(Allocator.Temp);

                // (10,10) → (14,10) 가로 5칸, 이어서 (14,11) → (14,13) 세로 3칸.
                for (int x = 10; x <= 14; x++)
                    IssueRoad(ref ecb, new int2(x, 10));
                for (int y = 11; y <= 13; y++)
                    IssueRoad(ref ecb, new int2(14, y));

                ecb.Playback(state.EntityManager);
                ecb.Dispose();

                _roadsIssued = true;
                Debug.Log("[StampTest] F" + _frame + ": 도로 8칸 PlaceRoadCommand 발행");
                return;
            }

            // ── F2: 공급자 직접 생성 + dirty 발행 ─────────────────────────
            //   도로가 RoadLayer에 등록될 여유를 주기 위해 2프레임 대기.
            if (!_supplierSpawned && _frame >= 3)
            {
                SpawnSupplier(ref state);
                _supplierSpawned = true;
            }
        }

        void IssueRoad(ref EntityCommandBuffer ecb, int2 cell)
        {
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new PlaceRoadCommand
            {
                Cell         = cell,
                OwnerLocalId = TestOwner,
                LaneCount    = 2,
                FactionId    = TestFaction,
            });
        }

        // ──────────────────────────────────────────────────────────────────
        //  공급자 건물 1개를 직접 생성.
        //
        //  배치: 1×1 건물을 (10, 9)에 둔다. 입구는 북(N)을 향함 →
        //        입구 도로셀 = (10,9) 위쪽 = (10,10) = 도로 첫 칸. 도로에 닿음.
        //
        //  EntranceInfo: 1×1이므로 Offset=(0,0), Dir=(byte)RoadDir.N.
        //  DirToOffset(RoadDir.N)=(0,+1) (EntranceOps에서 확인) → roadCell=(10,10).
        // ──────────────────────────────────────────────────────────────────
        void SpawnSupplier(ref SystemState state)
        {
            int2 origin = new int2(10, 9);
            int2 size   = new int2(1, 1);
            int  rot    = 0;

            var entrance = new EntranceInfo
            {
                Offset = new int2(0, 0),
                Dir    = (byte)RoadDir.N, // 북 — enum 실제값에 자동 일치
            };

            // 입구 도로셀 검산 (로그로 출력해 도로와 맞는지 즉시 확인).
            int2 roadCell = EntranceOps.EntranceRoadCell(origin, size, in entrance, rot);

            var em = state.EntityManager;
            var supplier = em.CreateEntity();

            // 시각 트랜스폼(있어도 그만 없어도 그만 — 확인엔 불필요하지만 형태 유지).
            em.AddComponentData(supplier, LocalTransform.FromPosition(
                new float3(origin.x, 0f, origin.y)));

            em.AddComponentData(supplier, new BuildingFootprint
            {
                Origin       = origin,
                Size         = size,
                RotSteps     = rot,
                OwnerLocalId = TestOwner,
            });
            em.AddComponentData(supplier, new BuildingEntrance
            {
                Entrance = entrance,
            });
            em.AddComponentData(supplier, new StampSupplier
            {
                OwnerLocalId = TestOwner,
                Relief       = TestRelief,
                MaxDist      = SupplyRange,
            });

            // dirty 발행 → 다음 재빌드 틱에 슬롯 0 재BFS.
            var evt = em.CreateEntity();
            em.AddComponentData(evt, new StampDirtyEvent { OwnerLocalId = TestOwner });

            Debug.Log("[StampTest] F" + _frame + ": 공급자 생성 origin=" + origin
                      + " 입구도로셀=" + roadCell + " (도로 첫칸=(10,10)과 일치해야 함)"
                      + " / StampDirtyEvent(owner=0) 발행");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  StampDumpSystem  — 현재 stamp 내용을 주기적으로 Console에 덤프
    // ──────────────────────────────────────────────────────────────────────
    //  슬롯 0의 모든 (도로셀 → [공급자/Relief/Dist...]) 를 출력.
    //  120프레임마다 1회 (스팸 방지). 도장이 0개면 "비어있음"도 출력.
    // ══════════════════════════════════════════════════════════════════════
    public partial struct StampDumpSystem : ISystem
    {
        int _frame;

        public void OnCreate(ref SystemState state)
        {
            _frame = 0;
            state.RequireForUpdate<StampLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frame++;
            if (_frame % 120 != 0)
                return;

            var stamp = SystemAPI.GetSingleton<StampLayers>();
            var map = stamp[0]; // 슬롯 0만 확인.
            if (!map.IsCreated)
            {
                Debug.Log("[StampDump] 슬롯0 맵 미생성");
                return;
            }

            // 키별로 묶어 출력. GetUniqueKeyArray = (중복 제거된 키, 개수).
            var (keys, uniqueCount) = map.GetUniqueKeyArray(Allocator.Temp);
            int uniqueCells = 0;
            int totalStamps = 0;

            var sb = new System.Text.StringBuilder();
            sb.Append("[StampDump] 슬롯0 도장 현황 (frame ").Append(_frame).Append(")\n");

            for (int i = 0; i < uniqueCount; i++)
            {
                var cell = keys[i];
                uniqueCells++;

                sb.Append("  cell ").Append(cell.ToString()).Append(" : ");
                int cnt = 0;
                if (map.TryGetFirstValue(cell, out var sr, out var it))
                {
                    do
                    {
                        sb.Append("[E").Append(sr.Supplier.Index)
                          .Append(" relief=0x").Append(((ulong)sr.Relief).ToString("X"))
                          .Append(" d=").Append(sr.Dist).Append("] ");
                        cnt++;
                        totalStamps++;
                    }
                    while (map.TryGetNextValue(out sr, ref it));
                }
                sb.Append("(").Append(cnt).Append(")\n");
            }

            if (uniqueCells == 0)
                sb.Append("  (비어있음 — 도장 0개)\n");
            else
                sb.Append("  요약: 셀 ").Append(uniqueCells)
                  .Append("개, 총 도장 ").Append(totalStamps).Append("개\n");

            Debug.Log(sb.ToString());

            keys.Dispose();
        }
    }
}
#endif
