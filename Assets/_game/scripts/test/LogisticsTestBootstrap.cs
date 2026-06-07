#if UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════
    //  LogisticsTestBootstrap (slice 2) — 물류 pull/push를 "stamp 경유"로 검증
    //
    //  StampTestBootstrap 패턴을 본떠 도로 위 시나리오를 만든다:
    //    F1 : RoadLayer에 가로 도로 (10,10)→(14,10) 5칸 직접 등록 (owner=0).
    //    F3 : 건물 3개 직접 생성(footprint+입구가 도로를 향함) + StampDirtyEvent(0):
    //          · 창고  (10,9) 입구N→(10,10). WarehouseTag + Store: Grain100/100, Flour0/100.
    //          · 소비  (12,9) 입구N→(12,10). Input  Grain 5/50.
    //          · 생산  (14,9) 입구N→(14,10). Output Flour 90/100.
    //         → StampRebuild가 창고 입구(10,10)에서 BFS → 도로셀마다 Kind=Warehouse 도장.
    //    이후: pull(소비)/push(생산)이 자기 도로셀 stamp에서 창고를 찾아 이전.
    //          몇 프레임 재고 로그로 관측.
    //
    //  기대(스탬프 빌드 후 1~2프레임):
    //    소비 Grain  5→45,  창고 Grain 100→60   (pull: (12,10)에서 창고 d=2 발견)
    //    생산 Flour 90→0,   창고 Flour   0→90    (push: (14,10)에서 창고 d=4 발견)
    //
    //  ※ WarehouseLink 안 씀(은퇴). 창고는 stamp로만 발견.
    //  ※ 도로는 RoadSystem 우회하고 RoadLayer에 직접 등록(StampTest와 동일 이유).
    //  ※ 확인 끝나면 파일 삭제(또는 UNITY_EDITOR 토글). 12프레임 뒤 자동 정지.
    // ══════════════════════════════════════════════════════════════════════
    public partial struct LogisticsTestBootstrap : ISystem
    {
        int    _frame;
        bool   _roadsLaid;
        bool   _built;
        Entity _warehouse;
        Entity _consumer;
        Entity _producer;

        const int TestOwner = 0;

        public void OnCreate(ref SystemState state)
        {
            _frame = 0;
            _roadsLaid = false;
            _built = false;
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<StampLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frame++;

            // ── F1: 가로 도로 5칸 등록 ────────────────────────────────────
            if (!_roadsLaid)
            {
                var road = SystemAPI.GetSingleton<GridLayers>().RoadLayer;
                for (int x = 10; x <= 14; x++)
                    AddRoad(ref road, new int2(x, 10));
                _roadsLaid = true;
                //Debug.Log("[LogiTest] F" + _frame + ": 도로 (10,10)→(14,10) 5칸 등록 (owner=0)");
                return;
            }

            // ── F3: 건물 3개 + dirty ──────────────────────────────────────
            if (!_built && _frame >= 3)
            {
                BuildScenario(ref state);
                _built = true;
                return;
            }

            // ── 이후: 재고 관측 ──────────────────────────────────────────
            if (_built)
            {
                var em = state.EntityManager;
                //Debug.Log($"[LogiTest] F{_frame}: 소비Grain={StockOf(em, _consumer, Commodity.Grain)}"
                //          + $" 창고Grain={StockOf(em, _warehouse, Commodity.Grain)}"
                //          + $" | 생산Flour={StockOf(em, _producer, Commodity.Flour)}"
                //          + $" 창고Flour={StockOf(em, _warehouse, Commodity.Flour)}");

                if (_frame >= 12)
                    state.Enabled = false;
            }
        }

        void AddRoad(ref NativeHashMap<int2, RoadCell> road, int2 cell)
        {
            if (road.ContainsKey(cell)) return;
            road.Add(cell, new RoadCell
            {
                Directions   = RoadDir.None,   // BFS는 4방 존재검사만
                FlowAxis     = default,
                LaneCount    = 2,
                OwnerLocalId = TestOwner,
                RoadEntity   = Entity.Null,
            });
        }

        void BuildScenario(ref SystemState state)
        {
            var em = state.EntityManager;

            // 입구 북향(N) 1×1 → 입구 도로셀 = origin + (0,+1).
            var entN = new EntranceInfo { Offset = new int2(0, 0), Dir = (byte)RoadDir.N };

            // ── 창고 (10,9) → 입구 (10,10). Store Grain100/100, Flour0/100. ──
            _warehouse = em.CreateEntity();
            em.AddComponentData(_warehouse, LocalTransform.FromPosition(new float3(10, 0, 9)));
            em.AddComponentData(_warehouse, new BuildingFootprint
            { Origin = new int2(10, 9), Size = new int2(1, 1), RotSteps = 0, OwnerLocalId = TestOwner });
            em.AddComponentData(_warehouse, new BuildingEntrance { Entrance = entN });
            em.AddComponentData(_warehouse, new WarehouseTag { OwnerLocalId = TestOwner, MaxDist = 0 }); // 0=무제한
            var wbuf = em.AddBuffer<StockEntry>(_warehouse);
            wbuf.Add(new StockEntry { Commodity = Commodity.Grain, Current = 100, Capacity = 100, Role = StockRole.Store });
            wbuf.Add(new StockEntry { Commodity = Commodity.Flour, Current = 0,   Capacity = 100, Role = StockRole.Store });

            // ── 소비 (12,9) → 입구 (12,10). Input Grain 5/50 (reorder=12, target=45). ──
            _consumer = em.CreateEntity();
            em.AddComponentData(_consumer, LocalTransform.FromPosition(new float3(12, 0, 9)));
            em.AddComponentData(_consumer, new BuildingFootprint
            { Origin = new int2(12, 9), Size = new int2(1, 1), RotSteps = 0, OwnerLocalId = TestOwner });
            em.AddComponentData(_consumer, new BuildingEntrance { Entrance = entN });
            var cbuf = em.AddBuffer<StockEntry>(_consumer);
            cbuf.Add(new StockEntry { Commodity = Commodity.Grain, Current = 5, Capacity = 50, Role = StockRole.Input });

            // ── 생산 (14,9) → 입구 (14,10). Output Flour 90/100 (discharge=80). ──
            _producer = em.CreateEntity();
            em.AddComponentData(_producer, LocalTransform.FromPosition(new float3(14, 0, 9)));
            em.AddComponentData(_producer, new BuildingFootprint
            { Origin = new int2(14, 9), Size = new int2(1, 1), RotSteps = 0, OwnerLocalId = TestOwner });
            em.AddComponentData(_producer, new BuildingEntrance { Entrance = entN });
            var pbuf = em.AddBuffer<StockEntry>(_producer);
            pbuf.Add(new StockEntry { Commodity = Commodity.Flour, Current = 90, Capacity = 100, Role = StockRole.Output });

            // dirty 발행 → StampRebuild가 슬롯0 재BFS(공급자+창고 도장).
            var evt = em.CreateEntity();
            em.AddComponentData(evt, new StampDirtyEvent { OwnerLocalId = TestOwner });

            //Debug.Log("[LogiTest] F" + _frame + ": 창고(10,9)→(10,10) / 소비(12,9)→(12,10) / 생산(14,9)→(14,10) 생성 + StampDirtyEvent(0). "
            //          + "기대: 소비Grain→45, 창고Grain→60, 생산Flour→0, 창고Flour→90.");
        }

        static int StockOf(EntityManager em, Entity e, Commodity c)
        {
            if (e == Entity.Null || !em.HasBuffer<StockEntry>(e)) return -1;
            var buf = em.GetBuffer<StockEntry>(e);
            for (int i = 0; i < buf.Length; i++)
                if (buf[i].Commodity == c) return buf[i].Current;
            return -1;
        }
    }
}
#endif
