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
        bool _citizenSpawned;

        // 테스트 파라미터 ─────────────────────────────────────────────────
        const int TestOwner = 0;          // 플레이어 슬롯 0
        const int TestFaction = 0;
        const int SupplyRange = 0;          // 0 = 무제한 (작은 맵이니 전부 도달)
        static readonly NeedType TestRelief = (NeedType)0x1UL; // 임시 비트 1개

        public void OnCreate(ref SystemState state)
        {
            _frame = 0;
            _roadsIssued = false;
            _supplierSpawned = false;
            _citizenSpawned = false;
            // 그리드/도로/stamp 인프라가 준비된 뒤에만 동작.
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<StampLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frame++;

            // ── F0: 도로 ㄱ자를 RoadLayer에 직접 꽂음 ────────────────────
            //   ※ PlaceRoadCommand(→RoadSystem) 경로를 우회한다. RoadSystem은
            //     RoadKeyLookup(도로 프리팹 키 테이블) 싱글톤을 RequireForUpdate
            //     하는데, 테스트 씬엔 그게 없어 RoadSystem이 통째로 스킵된다.
            //     지금 목적은 "도로가 있을 때 BFS가 도장 찍는가"의 격리 검증이지
            //     RoadSystem 동작 검증이 아니므로, RoadLayer에 직접 등록한다.
            if (!_roadsIssued)
            {
                var layers = SystemAPI.GetSingleton<GridLayers>();
                var road = layers.RoadLayer; // 핸들 — 직접 Add 반영됨.

                // (10,10)→(14,10) 가로 5칸 + (14,11)→(14,13) 세로 3칸 = 8칸.
                for (int x = 10; x <= 14; x++)
                    AddRoad(ref road, new int2(x, 10));
                for (int y = 11; y <= 13; y++)
                    AddRoad(ref road, new int2(14, y));

                _roadsIssued = true;
                Debug.Log("[StampTest] F" + _frame + ": RoadLayer에 도로 8칸 직접 등록 (owner=0)");
                return;
            }

            // ── F2: 공급자 직접 생성 + dirty 발행 ─────────────────────────
            //   도로가 RoadLayer에 등록될 여유를 주기 위해 2프레임 대기.
            if (!_supplierSpawned && _frame >= 3)
            {
                SpawnSupplier(ref state);
                _supplierSpawned = true;
                return;
            }

            // ── F5: 집 건물 + 시민 생성 ───────────────────────────────────
            //   공급자 dirty(F3) → 재빌드가 도장을 찍을 시간을 준 뒤(F5) 시민 투입.
            //   집을 (15,13)에 두고 입구 서향(W) → 입구 도로셀 (14,13).
            //   거기엔 공급자 E172가 d=7로 도장돼 있어야 함 → 시민이 그걸 찾는다.
            if (!_citizenSpawned && _frame >= 5)
            {
                SpawnCitizenAtHome(ref state);
                _citizenSpawned = true;
            }
        }

        void AddRoad(ref Unity.Collections.NativeHashMap<int2, RoadCell> road, int2 cell)
        {
            // 이미 있으면 덮어쓰지 않음(중복 Add 예외 회피). 없을 때만 등록.
            if (road.ContainsKey(cell))
                return;
            road.Add(cell, new RoadCell
            {
                Directions = RoadDir.None, // BFS는 4방 존재검사만 하므로 방향 불필요
                FlowAxis = default,
                LaneCount = 2,
                OwnerLocalId = TestOwner,
                RoadEntity = Entity.Null,
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
            int2 size = new int2(1, 1);
            int rot = 0;

            var entrance = new EntranceInfo
            {
                Offset = new int2(0, 0),
                Dir = (byte)RoadDir.N, // 북 — enum 실제값에 자동 일치
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
                Origin = origin,
                Size = size,
                RotSteps = rot,
                OwnerLocalId = TestOwner,
            });
            em.AddComponentData(supplier, new BuildingEntrance
            {
                Entrance = entrance,
            });
            em.AddComponentData(supplier, new StampSupplier
            {
                OwnerLocalId = TestOwner,
                Relief = TestRelief,
                MaxDist = SupplyRange,
            });

            // dirty 발행 → 다음 재빌드 틱에 슬롯 0 재BFS.
            var evt = em.CreateEntity();
            em.AddComponentData(evt, new StampDirtyEvent { OwnerLocalId = TestOwner });

            Debug.Log("[StampTest] F" + _frame + ": 공급자 생성 origin=" + origin
                      + " 입구도로셀=" + roadCell + " (도로 첫칸=(10,10)과 일치해야 함)"
                      + " / StampDirtyEvent(owner=0) 발행");
        }

        // ──────────────────────────────────────────────────────────────────
        //  집 건물 1개 + 그 집에 있는 시민 1명을 직접 생성.
        //
        //  집: (15,13) 1×1, 입구 서향(W) → 입구 도로셀 (14,13).
        //      (14,13)엔 공급자 E172가 d=7로 도장돼 있어야 함.
        //  시민: CurrentBuilding=집, Pursuing=TestRelief(0x1), CitizenOwner(0).
        //  기대: ServiceSearchSystem이 ServiceTarget.Supplier=E172, Dist=7로 채움.
        // ──────────────────────────────────────────────────────────────────
        void SpawnCitizenAtHome(ref SystemState state)
        {
            var em = state.EntityManager;

            // ── 집 건물 (footprint + 입구) ──
            int2 homeOrigin = new int2(15, 13);
            int2 homeSize = new int2(1, 1);
            int homeRot = 0;
            var homeEntrance = new EntranceInfo
            {
                Offset = new int2(0, 0),
                Dir = (byte)RoadDir.W, // 서향 → 입구 도로셀 (14,13)
            };
            int2 homeRoadCell = EntranceOps.EntranceRoadCell(
                homeOrigin, homeSize, in homeEntrance, homeRot);

            var home = em.CreateEntity();
            em.AddComponentData(home, new BuildingFootprint
            {
                Origin = homeOrigin,
                Size = homeSize,
                RotSteps = homeRot,
                OwnerLocalId = TestOwner,
            });
            em.AddComponentData(home, new BuildingEntrance { Entrance = homeEntrance });

            // ── 시민 (집에 있음, 욕구 활성) ──
            var citizen = em.CreateEntity();
            em.AddComponent<CitizenTag>(citizen);
            em.AddSharedComponent(citizen, new CitizenOwner(TestOwner));
            em.AddComponentData(citizen, new CitizenNeeds
            {
                Pursuing = TestRelief,   // 이 욕구를 추구 → ServiceSearch가 공급자 검색
            });
            // 욕구 컴포넌트(Hunger) 부착 — 활성 상태로 둬서 검증.
            em.AddComponentData(citizen, new Hunger
            {
                Level = 0.8f,   // 임계(0.6) 초과 = 활성
                Rate = 0.010f,
                Threshold = 0.6f,
            });
            em.AddComponentData(citizen, new CitizenState
            {
                Activity = CitizenActivity.AtHome,
                CurrentBuilding = home,    // ← 기준 건물
                ActionEndTime = 0.0,
            });
            em.AddComponentData(citizen, ServiceTarget.None);

            Debug.Log("[StampTest] F" + _frame + ": 집 생성 origin=" + homeOrigin
                      + " 입구도로셀=" + homeRoadCell + " (=(14,13) 기대, 거기 E172 d=7)"
                      + " / 시민 생성 CurrentBuilding=집, Pursuing=0x"
                      + ((ulong)TestRelief).ToString("X"));
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

            // ── 진단 A: DirtyMask 현재값 (재빌드가 dirty를 소비했는가) ──────
            //Debug.Log("[StampDiag] DirtyMask=0x" + stamp.DirtyMask.ToString("X")
            //          + " (0이면 재빌드가 dirty 소비 완료 / 0이 아니면 재빌드 미실행)");

            // ── 진단 B: RoadLayer 상태 (도로가 owner=0으로 등록됐는가) ──────
            if (SystemAPI.HasSingleton<GridLayers>())
            {
                var road = SystemAPI.GetSingleton<GridLayers>().RoadLayer;
                if (road.IsCreated)
                {
                    bool has1010 = road.TryGetValue(new int2(10, 10), out var rc1010);
                    //Debug.Log("[StampDiag] (10,10) 존재=" + has1010
                    //          + (has1010 ? " owner=" + rc1010.OwnerLocalId : " <- 도로 미등록!"));
                    // 도로 가로줄 끝 칸도 확인 (전 구간 등록됐는지).
                    bool has1410 = road.ContainsKey(new int2(14, 10));
                    bool has1413 = road.ContainsKey(new int2(14, 13));
                    //Debug.Log("[StampDiag] (14,10) 존재=" + has1410
                    //          + " / (14,13) 존재=" + has1413);
                }
                else
                {
                    //Debug.Log("[StampDiag] RoadLayer 미생성");
                }
            }

            // ── 진단 C: 공급자 엔티티 (쿼리 3종 컴포넌트 보유 여부) ──────────
            int supAll = 0, supWithFp = 0, supWithEnt = 0;
            foreach (var (sup, e) in
                     SystemAPI.Query<RefRO<StampSupplier>>().WithEntityAccess())
            {
                supAll++;
                if (SystemAPI.HasComponent<BuildingFootprint>(e)) supWithFp++;
                if (SystemAPI.HasComponent<BuildingEntrance>(e)) supWithEnt++;
            }
            //Debug.Log("[StampDiag] StampSupplier 엔티티=" + supAll
            //          + " / +BuildingFootprint=" + supWithFp
            //          + " / +BuildingEntrance=" + supWithEnt
            //          + " (BFS는 셋 다 가진 엔티티만 처리)");

            var map = stamp[0]; // 슬롯 0만 확인.
            if (!map.IsCreated)
            {
                Debug.Log("[StampDump] 슬롯0 맵 미생성");
                return;
            }

            // 키 배열(중복 포함 — 값마다 1개씩 나옴)을 받아 수동 dedup.
            // GetUniqueKeyArray는 키 정렬(IComparable)을 요구해 int2엔 못 쓴다.
            var keys = map.GetKeyArray(Allocator.Temp);
            var seen = new NativeHashSet<int2>(keys.Length, Allocator.Temp); // int2는 IEquatable
            int uniqueCells = 0;
            int totalStamps = 0;

            var sb = new System.Text.StringBuilder();
            sb.Append("[StampDump] 슬롯0 도장 현황 (frame ").Append(_frame).Append(")\n");

            for (int i = 0; i < keys.Length; i++)
            {
                var cell = keys[i];
                if (!seen.Add(cell))
                    continue; // 이미 출력한 셀.
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

            //Debug.Log(sb.ToString());

            seen.Dispose();
            keys.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ServiceDumpSystem — 시민의 ServiceTarget(찾기 결과)을 주기적으로 덤프
    // ──────────────────────────────────────────────────────────────────────
    //  ServiceSearchSystem이 채운 ServiceTarget을 시민별로 출력.
    //  "찾았는가 / 누구를 / 거리 얼마"를 확인. 120프레임마다 1회.
    // ══════════════════════════════════════════════════════════════════════
    public partial struct ServiceDumpSystem : ISystem
    {
        int _frame;

        public void OnCreate(ref SystemState state)
        {
            _frame = 0;
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frame++;
            if (_frame % 120 != 0)
                return;

            var sb = new System.Text.StringBuilder();
            sb.Append("[ServiceDump] 시민 ServiceTarget 현황 (frame ").Append(_frame).Append(")\n");

            double nowSec = SystemAPI.HasSingleton<GameClock>()
                ? SystemAPI.GetSingleton<GameClock>().TotalSeconds : -1.0;
            sb.Append("  now(게임초)=").Append(nowSec.ToString("F2")).Append("\n");

            int n = 0;
            foreach (var (target, needs, st) in
                     SystemAPI.Query<RefRO<ServiceTarget>, RefRO<CitizenNeeds>,
                                     RefRO<CitizenState>>()
                         .WithAll<CitizenTag>())
            {
                n++;
                var t = target.ValueRO;
                sb.Append("  시민#").Append(n)
                  .Append(" [").Append(st.ValueRO.Activity.ToString()).Append("]")
                  .Append(" endT=").Append(st.ValueRO.ActionEndTime.ToString("F2"))
                  .Append(" Pursuing=0x").Append(((ulong)needs.ValueRO.Pursuing).ToString("X"))
                  .Append(" CurBuilding=E").Append(st.ValueRO.CurrentBuilding.Index)
                  .Append(" → ");

                if (t.Has)
                    sb.Append("찾음: 공급자=E").Append(t.Supplier.Index)
                      .Append(" relief=0x").Append(((ulong)t.Relief).ToString("X"))
                      .Append(" dist=").Append(t.Dist).Append("\n");
                else
                    sb.Append("못 찾음 (Supplier=Null)\n");
            }

            if (n == 0)
                sb.Append("  (시민 없음)\n");

            //Debug.Log(sb.ToString());
        }
    }
}
#endif
