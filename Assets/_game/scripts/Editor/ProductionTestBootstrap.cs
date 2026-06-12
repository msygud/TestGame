#if UNITY_EDITOR
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ProductionTestBootstrap — 생산 사이클 검증
    //
    //  시나리오:
    //    · 방앗간(밀 → 밀가루): Input Grain 10/50, Output Flour 0/50
    //      ProductionJob(RecipeOutput=Flour, SkillFactor=1.0, Progress=-1)
    //      레시피: Grain×2 → Flour×1, 10게임초
    //
    //  기대:
    //    - F1: GameClock 싱글톤 + 밀 방앗간 생성. Progress=-1(대기).
    //    - 이후: Grain 충분(10 ≥ 2) → Progress=0, Grain→8.
    //      10게임초 경과 후 → Flour 1 추가, Progress=-1(재대기).
    //      Grain 남아있으면 즉시 다시 시작.
    //
    //  GameClock: TimeScale=1000(빠른 진행), SecondsPerDay=1200.
    //  로그를 풀어 확인 후 UNITY_EDITOR 토글 또는 파일 삭제.
    // ══════════════════════════════════════════════════════════════════════════
    public partial struct ProductionTestBootstrap : ISystem
    {
        int    _frame;
        bool   _built;
        Entity _mill;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frame++;

            if (!_built)
            {
                Setup(ref state);
                _built = true;
                return;
            }

            if (_frame > 60) { state.Enabled = false; return; }

            var em  = state.EntityManager;
            var job = em.GetComponentData<ProductionJob>(_mill);
            var buf = em.GetBuffer<StockEntry>(_mill);

            int grain = StockOf(in buf, Commodity.Grain);
            int flour = StockOf(in buf, Commodity.Flour);
            Debug.Log($"[ProdTest] F{_frame}: Grain={grain} Flour={flour} Progress={job.Progress:F2}");
        }

        void Setup(ref SystemState state)
        {
            var em = state.EntityManager;

            //GameClock (없으면 생성, 있으면 TimeScale만 높여 빠른 진행).
            if (!SystemAPI.HasSingleton<GameClock>())
            {
                var clockEntity = em.CreateEntity(typeof(GameClock));
                var clock = GameClock.Default;
                clock.TimeScale = 1000f;
                em.SetComponentData(clockEntity, clock);
            }

            // 밀 방앗간: Input Grain 10/50, Output Flour 0/50.
            _mill = em.CreateEntity();
            em.AddComponentData(_mill, ProductionJob.Make(Commodity.Flour, skillFactor: 1f));
            var buf = em.AddBuffer<StockEntry>(_mill);
            buf.Add(new StockEntry { Commodity = Commodity.Grain, Current = 10, Capacity = 50, Role = StockRole.Input  });
            buf.Add(new StockEntry { Commodity = Commodity.Flour, Current =  0, Capacity = 50, Role = StockRole.Output });

            Debug.Log("[ProdTest] 밀 방앗간 생성: Grain 10/50(Input), Flour 0/50(Output). 레시피: Grain×2→Flour×1, 10s.");
        }

        static int StockOf(in DynamicBuffer<StockEntry> buf, Commodity c)
        {
            for (int i = 0; i < buf.Length; i++)
                if (buf[i].Commodity == c) return buf[i].Current;
            return -1;
        }
    }
}
#endif
