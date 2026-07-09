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

        GUIStyle _style;
        string   _text = string.Empty;
        float    _nextBuildRT;

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
                _qState = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<CitizenState>());
                _qNeeds = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<CitizenNeeds>(),
                    ComponentType.ReadOnly<ServiceTarget>(), ComponentType.ReadOnly<CitizenState>());
                _qHunger = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<Hunger>());
                _qUnassigned = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<UnassignedTag>());
                _qResidence = em.CreateEntityQuery(
                    ComponentType.ReadOnly<ResidenceBuilding>(), ComponentType.ReadOnly<BuildingOccupancy>());
                _qMealStock = em.CreateEntityQuery(ComponentType.ReadOnly<StockEntry>());
                _qCond = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(), ComponentType.ReadOnly<CitizenConditions>());
                _qPool = em.CreateEntityQuery(ComponentType.ReadOnly<LogisticsPool>());
            }

            if (Time.unscaledTime >= _nextBuildRT)
            {
                _nextBuildRT = Time.unscaledTime + 0.5f;
                _text = BuildText();
            }
            if (string.IsNullOrEmpty(_text)) return;

            _style ??= new GUIStyle(GUI.skin.box)
            {
                fontSize  = 12,
                alignment = TextAnchor.UpperLeft,
            };

            float w = 250f, h = 128f;
            GUI.Box(new Rect(Screen.width - w - 12f, 12f, w, h), _text, _style);
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
            var needs   = _qNeeds.ToComponentDataArray<CitizenNeeds>(Allocator.Temp);
            var targets = _qNeeds.ToComponentDataArray<ServiceTarget>(Allocator.Temp);
            var nStates = _qNeeds.ToComponentDataArray<CitizenState>(Allocator.Temp);
            int pursuing = 0, unmet = 0;
            for (int i = 0; i < needs.Length; i++)
            {
                if (needs[i].Pursuing == NeedType.None) continue;
                pursuing++;
                var a = nStates[i].Activity;
                if (!targets[i].Has && (a == CitizenActivity.Idle || a == CitizenActivity.AtHome))
                    unmet++;
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

            // 거주 점유
            var occs = _qResidence.ToComponentDataArray<BuildingOccupancy>(Allocator.Temp);
            int cur = 0, cap = 0;
            for (int i = 0; i < occs.Length; i++) { cur += occs[i].Current; cap += occs[i].Capacity; }
            occs.Dispose();

            int unassigned = _qUnassigned.CalculateEntityCount();

            // 전 건물 재고 합(경제 체인 관찰): Meal은 소비로 줄고 생산으로 차고,
            //   Grain/Flour는 농장→창고→제분소→식당 흐름의 총량.
            var em2 = _qWorld.EntityManager;
            var stockEnts = _qMealStock.ToEntityArray(Allocator.Temp);
            int meals = 0, mealCap = 0, grain = 0, flour = 0;
            for (int i = 0; i < stockEnts.Length; i++)
            {
                var stock = em2.GetBuffer<StockEntry>(stockEnts[i], true);
                for (int s = 0; s < stock.Length; s++)
                    switch (stock[s].Commodity)
                    {
                        case Commodity.Meal:
                            meals += stock[s].Current; mealCap += stock[s].Capacity; break;
                        case Commodity.Grain: grain += stock[s].Current; break;
                        case Commodity.Flour: flour += stock[s].Current; break;
                    }
            }
            stockEnts.Dispose();

            // 창고 재고는 이제 공유 풀(LogisticsPool)이 진실 — 창고 buffer는 Current=0(vestigial).
            //   전 건물 합(=생산자 Output + 소비자 Input 인플라이트)에 풀 Stored를 더해야 진짜 총량.
            if (_qPool.CalculateEntityCount() == 1)
            {
                var pool = _qPool.GetSingleton<LogisticsPool>();
                if (pool.Cells.IsCreated)
                    foreach (var kv in pool.Cells)
                    {
                        if (kv.Key.y == (int)Commodity.Grain)      grain += kv.Value.Stored;
                        else if (kv.Key.y == (int)Commodity.Flour) flour += kv.Value.Stored;
                    }
            }

            return $"Citizens {total}  (unassigned {unassigned})\n"
                 + $"Idle {idle}  Home {home}  Work {work}\n"
                 + $"Travel {travel}  Eat {dest}  Stuck {stuck}\n"
                 + $"Hunger avg {hungerAvg:0.00}  hungry {hungry}  En {energyAvg:0.00}\n"
                 + $"pursuing {pursuing}  UNMET {unmet}\n"
                 + $"Housing {cur}/{cap}\n"
                 + $"Meals {meals}/{mealCap}  Grain {grain}  Flour {flour}";
        }
    }
}
