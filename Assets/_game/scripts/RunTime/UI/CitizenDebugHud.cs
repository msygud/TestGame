using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  CitizenDebugHud — 시민 파이프라인 관찰 (임시/디버그 IMGUI, F10 토글)
    //
    //  욕구 루프(Hunger 상승 → 결정 → 탐색 → 이동 → 식사 → 해소)가 실제로
    //  도는지 숫자로 확인한다. 물리 이동이 아직 타이머 전용이라(비주얼 없음)
    //  이 카운터가 유일한 관찰 수단이다.
    //
    //  표시: 인구(미배정) / 활동별 분포 / 허기 평균·활성·추구 / 거주 점유.
    //  화면 우상단. 에디터/개발 빌드 자동 생성 — 씬 와이어링 불필요.
    //
    //  GC 규약(CLAUDE.md): 쿼리 월드당 1회, 문자열은 0.5초마다 재조립,
    //  Repaint 이벤트에서만 그림. ToComponentDataArray(Temp)는 네이티브(무GC).
    // ══════════════════════════════════════════════════════════════
    public class CitizenDebugHud : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static bool _created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (_created) return;
            _created = true;
            var go = new GameObject("[CitizenDebugHud]");
            go.AddComponent<CitizenDebugHud>();
            DontDestroyOnLoad(go);
        }
#endif
        bool _enabled = true;   // F10 토글

        World       _qWorld;
        EntityQuery _qState, _qNeeds, _qHunger, _qUnassigned, _qResidence, _qMealStock, _qCond, _qPool;
        EntityQuery _qProd, _qWh, _qDemand;   // 플레이어별 분해(2026-07-10): 생산 건물/창고/수요장

        GUIStyle _style;
        string   _text = string.Empty;
        float    _nextBuildRT;
        float    _boxH = 128f;   // 텍스트 줄 수에 따라 재계산(플레이어별 섹션 가변)

        void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f10Key.wasPressedThisFrame) _enabled = !_enabled;
        }

        void OnGUI()
        {
            if (!_enabled) return;
            if (Event.current.type != EventType.Repaint) return;   // 비대화형 — Layout 불필요

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { _qWorld = null; return; }
            var em = world.EntityManager;

            if (!ReferenceEquals(_qWorld, world))
            {
                _qWorld = world;
                // OwnerShared 포함 = 플레이어별 SetSharedComponentFilter 가능(전 시민/건물이 보유 —
                //   매칭 집합 불변). 전역 집계는 필터 없이, 플레이어별은 필터 걸고 같은 쿼리 재사용.
                _qState = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<CitizenState>(),
                    ComponentType.ReadOnly<OwnerShared>());
                _qNeeds = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<CitizenNeeds>(),
                    ComponentType.ReadOnly<ServiceTarget>(), ComponentType.ReadOnly<CitizenState>(),
                    ComponentType.ReadOnly<OwnerShared>());
                _qHunger = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<Hunger>(),
                    ComponentType.ReadOnly<OwnerShared>());
                _qUnassigned = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<UnassignedTag>());
                _qResidence = em.CreateEntityQuery(
                    ComponentType.ReadOnly<ResidenceBuilding>(), ComponentType.ReadOnly<BuildingOccupancy>(),
                    ComponentType.ReadOnly<BuildingFootprint>());
                _qMealStock = em.CreateEntityQuery(
                    ComponentType.ReadOnly<StockEntry>(), ComponentType.ReadOnly<BuildingFootprint>());
                _qCond = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<CitizenConditions>());
                _qPool = em.CreateEntityQuery(ComponentType.ReadOnly<LogisticsPool>());
                _qProd = em.CreateEntityQuery(
                    ComponentType.ReadOnly<ProductionJob>(), ComponentType.ReadOnly<BuildingFootprint>());
                _qWh   = em.CreateEntityQuery(ComponentType.ReadOnly<WarehouseTag>());
                _qDemand = em.CreateEntityQuery(ComponentType.ReadOnly<DemandField>());
            }

            if (Time.unscaledTime >= _nextBuildRT)
            {
                _nextBuildRT = Time.unscaledTime + 0.5f;
                _text = BuildText();
                int lines = 1;
                for (int i = 0; i < _text.Length; i++) if (_text[i] == '\n') lines++;
                _boxH = 14f + lines * 15f;
            }
            if (string.IsNullOrEmpty(_text)) return;

            _style ??= new GUIStyle(GUI.skin.box)
            {
                fontSize  = 12,
                alignment = TextAnchor.UpperLeft,
            };

            float w = 430f;
            GUI.Box(new Rect(Screen.width - w - 12f, 12f, w, _boxH), _text, _style);
        }

        string BuildText()
        {
            int total = _qState.CalculateEntityCount();
            if (total == 0) return "Citizens: 0";

            // 활동별 분포
            var states = _qState.ToComponentDataArray<CitizenState>(Allocator.Temp);
            int idle = 0, home = 0, work = 0, dest = 0, travel = 0, stuck = 0;
            for (int i = 0; i < states.Length; i++)
                switch (states[i].Activity)
                {
                    case CitizenActivity.Idle:          idle++;   break;
                    case CitizenActivity.AtHome:        home++;   break;
                    case CitizenActivity.AtWork:        work++;   break;
                    case CitizenActivity.AtDestination: dest++;   break;
                    case CitizenActivity.Traveling:     travel++; break;
                    case CitizenActivity.Stuck:         stuck++;  break;
                }
            states.Dispose();

            // 추구 중(Pursuing != None) — 그중 '미충족'(공급자를 못 찾아 대기)을 분리.
            //   unmet = 욕구는 켜졌는데 목적지 없이 Idle/AtHome — 커버리지 밖 집·집 미배정 등.
            //   이 수치가 곧 "미충족 욕구 통계"(욕구 주도 배치의 입력 신호)의 원형.
            //   같은 쿼리의 배열들은 엔티티 순서가 정렬 일치(상관 분석 가능).
            // UNMET 세분화(2026-07-10): cov=커버 공백(NoCoverage — 건설 신호가 되는 실수요) /
            //   full=도달 가능하나 못 서빙(Reached — 만석·재고·무인 = 수용량/일시 대기, 증설 신호 아님).
            var needs   = _qNeeds.ToComponentDataArray<CitizenNeeds>(Allocator.Temp);
            var targets = _qNeeds.ToComponentDataArray<ServiceTarget>(Allocator.Temp);
            var nStates = _qNeeds.ToComponentDataArray<CitizenState>(Allocator.Temp);
            int pursuing = 0, unmet = 0;
            // 미충족 세분(2026-07-14): [욕구 0=food/1=fun/2=health/3=other][사유 0=cov/1=full/2=goods/3=staff].
            //   remedy 진단(유저·AI 공통): cov=신설 / full=증설(더/크게) / goods=상류 / staff=고용.
            var ub = new NativeArray<int>(16, Allocator.Temp);
            static int NeedIdx(NeedType n) =>
                  (n & NeedType.Hunger) != NeedType.None          ? 0
                : (n & NeedType.LowEntertainment) != NeedType.None ? 1
                : (n & NeedType.Disease) != NeedType.None          ? 2 : 3;
            static int CauseIdx(ServiceOutcome o) =>
                  o == ServiceOutcome.Full      ? 1
                : o == ServiceOutcome.NoGoods   ? 2
                : o == ServiceOutcome.Unstaffed ? 3 : 0;
            for (int i = 0; i < needs.Length; i++)
            {
                if (needs[i].Pursuing == NeedType.None) continue;
                pursuing++;
                var a = nStates[i].Activity;
                if (!targets[i].Has && (a == CitizenActivity.Idle || a == CitizenActivity.AtHome))
                {
                    unmet++;
                    ub[NeedIdx(needs[i].Pursuing) * 4 + CauseIdx(targets[i].LastOutcome)]++;
                }
            }
            needs.Dispose(); targets.Dispose(); nStates.Dispose();

            // 허기: 평균 + 활성(임계 초과)
            var hungers = _qHunger.ToComponentDataArray<Hunger>(Allocator.Temp);
            float hungerSum = 0f; int hungry = 0;
            for (int i = 0; i < hungers.Length; i++)
            {
                hungerSum += hungers[i].Level;
                if (hungers[i].IsActive) hungry++;
            }
            float hungerAvg = hungers.Length > 0 ? hungerSum / hungers.Length : 0f;
            hungers.Dispose();

            // Energy 평균(컨디션 — 근무 소모/휴식 회복 동역학 관찰)
            var conds = _qCond.ToComponentDataArray<CitizenConditions>(Allocator.Temp);
            float energySum = 0f;
            for (int i = 0; i < conds.Length; i++) energySum += conds[i].Energy;
            float energyAvg = conds.Length > 0 ? energySum / conds.Length : 0f;
            conds.Dispose();

            // 거주 점유 — footprint owner로 전역+플레이어별 동시 집계(단일 패스).
            var occs   = _qResidence.ToComponentDataArray<BuildingOccupancy>(Allocator.Temp);
            var resFps = _qResidence.ToComponentDataArray<BuildingFootprint>(Allocator.Temp);
            int cur = 0, cap = 0;
            var pCur = new NativeArray<int>(8, Allocator.Temp);
            var pCap = new NativeArray<int>(8, Allocator.Temp);
            for (int i = 0; i < occs.Length; i++)
            {
                cur += occs[i].Current; cap += occs[i].Capacity;
                int o = resFps[i].OwnerLocalId;
                if ((uint)o < 8) { pCur[o] += occs[i].Current; pCap[o] += occs[i].Capacity; }
            }
            occs.Dispose(); resFps.Dispose();

            int unassigned = _qUnassigned.CalculateEntityCount();

            // 전 건물 재고 합(경제 체인 관찰) — footprint owner로 플레이어별 Meal도 동시 집계.
            var em2 = _qWorld.EntityManager;
            var stockEnts = _qMealStock.ToEntityArray(Allocator.Temp);
            var stockFps  = _qMealStock.ToComponentDataArray<BuildingFootprint>(Allocator.Temp);
            int meals = 0, mealCap = 0, grain = 0, flour = 0;
            var pMeal = new NativeArray<int>(8, Allocator.Temp);
            var pMealCap = new NativeArray<int>(8, Allocator.Temp);
            for (int i = 0; i < stockEnts.Length; i++)
            {
                int o = stockFps[i].OwnerLocalId;
                var stock = em2.GetBuffer<StockEntry>(stockEnts[i], true);
                for (int s = 0; s < stock.Length; s++)
                    switch (stock[s].Commodity)
                    {
                        case Commodity.Meal:
                            meals += stock[s].Current; mealCap += stock[s].Capacity;
                            if ((uint)o < 8) { pMeal[o] += stock[s].Current; pMealCap[o] += stock[s].Capacity; }
                            break;
                        case Commodity.Grain: grain += stock[s].Current; break;
                        case Commodity.Flour: flour += stock[s].Current; break;
                    }
            }
            stockEnts.Dispose(); stockFps.Dispose();

            // 풀(공유 저장소) — 전역 합 + 플레이어별 Grain/Flour Stored/Capacity + 흐름 창
            //   (o=물리 유출/i=유입, 지난 성장 틱 이후 누적 — 풀 층 스케일링 판단의 원재료.
            //    결핍(미충족)은 수요층 → dmd C에 집계).
            var pGrainS = new NativeArray<int>(8, Allocator.Temp);
            var pGrainC = new NativeArray<int>(8, Allocator.Temp);
            var pFlourS = new NativeArray<int>(8, Allocator.Temp);
            var pFlourC = new NativeArray<int>(8, Allocator.Temp);
            var pGD = new NativeArray<int>(8, Allocator.Temp);
            var pGI = new NativeArray<int>(8, Allocator.Temp);
            var pFD = new NativeArray<int>(8, Allocator.Temp);
            var pFI = new NativeArray<int>(8, Allocator.Temp);
            if (_qPool.CalculateEntityCount() == 1)
            {
                var pool = _qPool.GetSingleton<LogisticsPool>();
                if (pool.Cells.IsCreated)
                    foreach (var kv in pool.Cells)
                    {
                        int o = kv.Key.x;
                        if (kv.Key.y == (int)Commodity.Grain)
                        {
                            grain += kv.Value.Stored;
                            if ((uint)o < 8) { pGrainS[o] += kv.Value.Stored; pGrainC[o] += kv.Value.Capacity; }
                        }
                        else if (kv.Key.y == (int)Commodity.Flour)
                        {
                            flour += kv.Value.Stored;
                            if ((uint)o < 8) { pFlourS[o] += kv.Value.Stored; pFlourC[o] += kv.Value.Capacity; }
                        }
                    }
                if (pool.Flow.IsCreated)
                    foreach (var kv in pool.Flow)
                    {
                        int o = kv.Key.x;
                        if ((uint)o >= 8) continue;
                        int d = kv.Value.Out;   // 물리 유출(결핍은 수요층 — dmd C에 나타남)
                        if (kv.Key.y == (int)Commodity.Grain)      { pGD[o] += d; pGI[o] += kv.Value.In; }
                        else if (kv.Key.y == (int)Commodity.Flour) { pFD[o] += d; pFI[o] += kv.Value.In; }
                    }
            }

            // 건물 구성(플레이어별): 생산 건물은 레시피 출력으로 분류(Meal=식당/Flour=제분/Grain=농장),
            //   창고는 WarehouseTag. MainKey 무관 — 능력 기준이라 프리팹이 늘어도 유효.
            var pRest = new NativeArray<int>(8, Allocator.Temp);
            var pMill = new NativeArray<int>(8, Allocator.Temp);
            var pFarm = new NativeArray<int>(8, Allocator.Temp);
            var pWh   = new NativeArray<int>(8, Allocator.Temp);
            {
                var prods = _qProd.ToComponentDataArray<ProductionJob>(Allocator.Temp);
                var pfps  = _qProd.ToComponentDataArray<BuildingFootprint>(Allocator.Temp);
                for (int i = 0; i < prods.Length; i++)
                {
                    int o = pfps[i].OwnerLocalId;
                    if ((uint)o >= 8) continue;
                    switch (prods[i].RecipeOutput)
                    {
                        case Commodity.Meal:  pRest[o]++; break;
                        case Commodity.Flour: pMill[o]++; break;
                        case Commodity.Grain: pFarm[o]++; break;
                    }
                }
                prods.Dispose(); pfps.Dispose();
                var whs = _qWh.ToComponentDataArray<WarehouseTag>(Allocator.Temp);
                for (int i = 0; i < whs.Length; i++)
                    if ((uint)whs[i].OwnerLocalId < 8) pWh[whs[i].OwnerLocalId]++;
                whs.Dispose();
            }

            // 수요장 누적(플레이어별, NoCoverage): 욕구/commodity/창고 3분류 — 어디가 막혔는지의 지표.
            var pDmdNeed = new NativeArray<int>(8, Allocator.Temp);
            var pDmdCmdy = new NativeArray<int>(8, Allocator.Temp);
            var pDmdWh   = new NativeArray<int>(8, Allocator.Temp);
            if (_qDemand.CalculateEntityCount() == 1)
            {
                var df = _qDemand.GetSingleton<DemandField>();
                if (df.Stats.IsCreated)
                    foreach (var kv in df.Stats)
                    {
                        int o = kv.Key.x;
                        if ((uint)o >= 8) continue;
                        int res = kv.Key.w, nc = kv.Value.FailNoCoverage;
                        if (DemandResource.IsWarehouse(res))       pDmdWh[o]   += nc;
                        else if (DemandResource.IsCommodity(res))  pDmdCmdy[o] += nc;
                        else                                       pDmdNeed[o] += nc;
                    }
            }

            var sb = new System.Text.StringBuilder(1024);
            sb.Append($"Citizens {total}  (unassigned {unassigned})\n")
              .Append($"Idle {idle}  Home {home}  Work {work}\n")
              .Append($"Travel {travel}  Eat {dest}  Stuck {stuck}\n")
              .Append($"Hunger avg {hungerAvg:0.00}  hungry {hungry}  En {energyAvg:0.00}\n")
              .Append($"pursuing {pursuing}  UNMET {unmet}  cov{ub[0]+ub[4]+ub[8]+ub[12]} full{ub[1]+ub[5]+ub[9]+ub[13]} goods{ub[2]+ub[6]+ub[10]+ub[14]} staff{ub[3]+ub[7]+ub[11]+ub[15]}\n")
              .Append($"  food c{ub[0]} f{ub[1]} g{ub[2]} s{ub[3]}    fun c{ub[4]} f{ub[5]} g{ub[6]} s{ub[7]}    hlth c{ub[8]} f{ub[9]} g{ub[10]} s{ub[11]}\n")
              .Append($"Housing {cur}/{cap}\n")
              .Append($"Meals {meals}/{mealCap}  Grain {grain}  Flour {flour}");
            ub.Dispose();

            // ── 플레이어별(존재하는 owner만, 2줄씩): 시민 지표는 OwnerShared 청크 필터로 ──
            for (int p = 0; p < 8; p++)
            {
                bool hasAny = pCap[p] > 0 || pWh[p] > 0 || pRest[p] > 0 || pFarm[p] > 0;
                if (!hasAny) continue;

                _qState.SetSharedComponentFilter(new OwnerShared(p));
                _qHunger.SetSharedComponentFilter(new OwnerShared(p));
                _qNeeds.SetSharedComponentFilter(new OwnerShared(p));

                int pop = _qState.CalculateEntityCount();

                var hs = _qHunger.ToComponentDataArray<Hunger>(Allocator.Temp);
                float hSum = 0f;
                for (int i = 0; i < hs.Length; i++) hSum += hs[i].Level;
                float hAvg = hs.Length > 0 ? hSum / hs.Length : 0f;
                hs.Dispose();

                var pn = _qNeeds.ToComponentDataArray<CitizenNeeds>(Allocator.Temp);
                var pt = _qNeeds.ToComponentDataArray<ServiceTarget>(Allocator.Temp);
                var ps = _qNeeds.ToComponentDataArray<CitizenState>(Allocator.Temp);
                int pPur = 0, pUnmet = 0, pUnCov = 0, pUnFull = 0;
                for (int i = 0; i < pn.Length; i++)
                {
                    if (pn[i].Pursuing == NeedType.None) continue;
                    pPur++;
                    var a = ps[i].Activity;
                    if (!pt[i].Has && (a == CitizenActivity.Idle || a == CitizenActivity.AtHome))
                    {
                        pUnmet++;
                        if (pt[i].LastOutcome == ServiceOutcome.NoCoverage) pUnCov++;
                        else                                                pUnFull++;   // 도달·거절
                    }
                }
                pn.Dispose(); pt.Dispose(); ps.Dispose();

                sb.Append($"\n─ P{p}  pop {pop}  hun {hAvg:0.00}  pur {pPur}  UNMET {pUnmet}(c{pUnCov}/f{pUnFull})  hse {pCur[p]}/{pCap[p]}\n")
                  .Append($"   R{pRest[p]} M{pMill[p]} F{pFarm[p]} W{pWh[p]}")
                  .Append($"  G {K(pGrainS[p])}/{K(pGrainC[p])}  Fl {K(pFlourS[p])}/{K(pFlourC[p])}")
                  .Append($"  Meal {K(pMeal[p])}/{K(pMealCap[p])}\n")
                  .Append($"   dmd N{K(pDmdNeed[p])} C{K(pDmdCmdy[p])} W{K(pDmdWh[p])}")
                  .Append($"  flow G {K(pGD[p])}→{K(pGI[p])}  Fl {K(pFD[p])}→{K(pFI[p])}");
            }
            _qState.ResetFilter();
            _qHunger.ResetFilter();
            _qNeeds.ResetFilter();

            pCur.Dispose(); pCap.Dispose(); pMeal.Dispose(); pMealCap.Dispose();
            pGrainS.Dispose(); pGrainC.Dispose(); pFlourS.Dispose(); pFlourC.Dispose();
            pGD.Dispose(); pGI.Dispose(); pFD.Dispose(); pFI.Dispose();
            pRest.Dispose(); pMill.Dispose(); pFarm.Dispose(); pWh.Dispose();
            pDmdNeed.Dispose(); pDmdCmdy.Dispose(); pDmdWh.Dispose();

            return sb.ToString();
        }

        // 압축 표기(수요 누적 등 큰 수): 10,000 이상은 k 단위.
        static string K(int v) => v >= 10000 ? $"{v / 1000}k" : v.ToString();
    }
}
