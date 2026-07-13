using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Game.Unit;   // CombatHealth(내구 표시)

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  BuildingInspectorHud — 개별 건물 용도별 통계 (에디터/개발빌드, F11 토글)
    //
    //  통합 HUD(F10)는 "도시 전체가 건강한가"만 보여줘 개별 건물의 병목을 못 짚는다.
    //  이 인스펙터는 F11로 켠 뒤 마우스로 가리킨 건물의 용도별 상태를 보여준다:
    //    · 집     — 거주(BuildingOccupancy)
    //    · 직장   — 직종·근로자(WorkplaceBuilding+Occupancy)
    //    · 식당   — 좌석(VisitorOccupancy)·식사 재고
    //    · 생산   — 레시피·SkillFactor·진행(ProductionJob)
    //    · 창고   — 보관 재고(StockEntry Store)
    //    · 공통   — 소유주·셀·재고 버퍼 전 품목
    //  건물마다 있는 컴포넌트만 골라 해당 섹션을 출력(HasComponent 분기).
    //
    //  피킹: 배치 건물은 OccupancyLayer.Occupant=Null(BuildingPlacement) → 셀 역참조 불가.
    //    BuildingFootprint 순회로 커서 셀을 포함하는 건물을 찾는다(피킹은 0.2초 스로틀).
    //
    //  GC 규약(CLAUDE.md): 쿼리 월드당 1회, Repaint 전용, 피킹·문자열은 0.2초 스로틀
    //  (건물당 열거 ToString은 소량·저빈도라 수용). 실제 자원 비주얼처럼 임시 도구.
    // ══════════════════════════════════════════════════════════════
    public class BuildingInspectorHud : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static bool _created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (_created) return;
            _created = true;
            var go = new GameObject("[BuildingInspectorHud]");
            go.AddComponent<BuildingInspectorHud>();
            DontDestroyOnLoad(go);
        }
#endif
        bool _enabled = true;   // F11 토글

        World       _qWorld;
        EntityQuery _gsQ, _bfQ, _citQ, _poolQ, _auraQ, _dmdQ;

        GUIStyle _style;
        Entity   _hovered = Entity.Null;
        string   _text    = string.Empty;
        float    _nextPickRT;

        void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f11Key.wasPressedThisFrame) _enabled = !_enabled;
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
                _gsQ = em.CreateEntityQuery(typeof(GridSettings));
                _bfQ = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingFootprint>());
                _citQ = em.CreateEntityQuery(
                    ComponentType.ReadOnly<CitizenTag>(),
                    ComponentType.ReadOnly<CitizenResidence>(),
                    ComponentType.ReadOnly<CitizenState>());
                _poolQ = em.CreateEntityQuery(ComponentType.ReadOnly<LogisticsPool>());
                _auraQ = em.CreateEntityQuery(ComponentType.ReadOnly<AuraLoadMap>());
                _dmdQ  = em.CreateEntityQuery(ComponentType.ReadOnly<DemandField>());
                _hovered = Entity.Null; _text = string.Empty;
            }

            _style ??= new GUIStyle(GUI.skin.box)
            {
                fontSize  = 12,
                alignment = TextAnchor.UpperLeft,
                richText  = false,
            };

            // 피킹 + 문자열 재조립(0.2초 스로틀).
            if (Time.unscaledTime >= _nextPickRT)
            {
                _nextPickRT = Time.unscaledTime + 0.2f;
                Entity e = PickBuilding(em);
                _hovered = e;
                _text = e != Entity.Null ? BuildText(em, e) : string.Empty;
            }

            const float w = 270f;
            if (_hovered == Entity.Null || string.IsNullOrEmpty(_text))
            {
                GUI.Box(new Rect(12f, 12f, w, 22f), "[F11] Building inspector — hover a building", _style);
                return;
            }
            // 줄 수에 맞춰 높이(대략).
            int lines = 1;
            for (int i = 0; i < _text.Length; i++) if (_text[i] == '\n') lines++;
            float h = 8f + lines * 15f;
            GUI.Box(new Rect(12f, 12f, w, h), _text, _style);
        }

        // 커서 아래 건물 엔티티(footprint 포함). 없으면 Null.
        Entity PickBuilding(EntityManager em)
        {
            if (_gsQ.IsEmpty) return Entity.Null;
            float cs = _gsQ.GetSingleton<GridSettings>().CellSize;
            if (cs <= 0f) return Entity.Null;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            var cam   = Camera.main;
            if (mouse == null || cam == null) return Entity.Null;

            Ray ray = cam.ScreenPointToRay((Vector2)mouse.position.ReadValue());
            Vector3 p;
            if (Physics.Raycast(ray, out var hit, 5000f)) p = hit.point;
            else if (math.abs(ray.direction.y) > 1e-5f)
            {
                float t = -ray.origin.y / ray.direction.y;
                if (t <= 0f) return Entity.Null;
                p = ray.origin + ray.direction * t;
            }
            else return Entity.Null;

            int2 cell = new int2((int)math.floor(p.x / cs), (int)math.floor(p.z / cs));

            var ents = _bfQ.ToEntityArray(Allocator.Temp);
            var fps  = _bfQ.ToComponentDataArray<BuildingFootprint>(Allocator.Temp);
            Entity found = Entity.Null;
            for (int i = 0; i < fps.Length; i++)
            {
                int2 eff = EntranceOps.RotateSize(fps[i].Size, fps[i].RotSteps);
                var o = fps[i].Origin;
                if (cell.x >= o.x && cell.x < o.x + eff.x &&
                    cell.y >= o.y && cell.y < o.y + eff.y)
                { found = ents[i]; break; }
            }
            ents.Dispose(); fps.Dispose();
            return found;
        }

        // 건물의 용도별 통계 문자열(컴포넌트 유무로 섹션 구성).
        string BuildText(EntityManager em, Entity e)
        {
            if (!em.Exists(e)) return string.Empty;
            var sb = new System.Text.StringBuilder(256);

            // ── 헤더: 유형 + 소유주 + 셀 ──
            var bf = em.GetComponentData<BuildingFootprint>(e);
            string type = ClassifyType(em, e);
            sb.Append(type).Append("   P").Append(bf.OwnerLocalId)
              .Append("   @(").Append(bf.Origin.x).Append(',').Append(bf.Origin.y).Append(')');

            // ── 내구(전투 파괴 가능 건물) ──
            if (em.HasComponent<CombatHealth>(e))
            {
                var hp = em.GetComponentData<CombatHealth>(e);
                sb.Append("   hp ").Append(hp.Health.ToString("0")).Append('/')
                  .Append(hp.MaxHealth.ToString("0"));
            }

            // ── 용도 요약: 해소하는 욕구(방문 / 오라) + 직무 효과 ──
            //   Serves = 찾아와서(방문) 해소하는 욕구 / Aura = 반경 안 수동 해소(치안·의료).
            if (em.HasComponent<StampSupplier>(e))
            {
                var ss = em.GetComponentData<StampSupplier>(e);
                sb.Append("\nServes ").Append(ReliefName(ss.Relief)).Append(" (visit)");
            }
            if (em.HasComponent<AuraSupplier>(e))
            {
                var a = em.GetComponentData<AuraSupplier>(e);
                sb.Append("\nAura ").Append(ReliefName(a.Relief)).Append("  r").Append(a.Radius);
                // 커버 부하(최근접 귀속 인구)/정원 + 품질(초과 시 정원÷부하). AuraLoadMap에서.
                if (_auraQ.CalculateEntityCount() == 1)
                {
                    var lm = _auraQ.GetSingleton<AuraLoadMap>();
                    if (lm.Map.IsCreated && lm.Map.TryGetValue(e, out var load))
                    {
                        int pop = load.x, cap = load.y;
                        sb.Append("  load ").Append(pop);
                        if (cap > 0)
                        {
                            int q = pop <= cap ? 100 : (int)(100f * cap / pop);
                            sb.Append('/').Append(cap).Append("  q").Append(q).Append('%');
                        }
                        else sb.Append(" (uncapped)");
                    }
                }
            }
            // 직무 효과(유인 직장 공통 — 무인/폐점=0, 정상=숙련·컨디션·적성 합÷정원).
            if (em.HasComponent<StaffEffect>(e))
                sb.Append("\nStaff x").Append(em.GetComponentData<StaffEffect>(e).Factor.ToString("0.00"));

            // ── 이 셀 사람들의 미충족 수요(사유별, 누적 — remedy 진단) ──
            //   집·직장 = 여기 거주민/근로자가 못 얻는 것 / 서비스 건물 = 자기 상태(Seats·Stock·Staff)로 판단.
            //   cov=신설 / full=증설 / goods=상류 / staff=고용. (누적값 — 비율/추세가 진단 포인트.)
            if (_dmdQ.CalculateEntityCount() == 1)
            {
                var df = _dmdQ.GetSingleton<DemandField>();
                if (df.Stats.IsCreated)
                {
                    int2 dc = DemandGrid.ToCell(bf.Origin);
                    AppendDemand(sb, in df, bf.OwnerLocalId, dc, NeedType.Hunger, "food");
                    AppendDemand(sb, in df, bf.OwnerLocalId, dc, NeedType.LowEntertainment, "fun");
                    AppendDemand(sb, in df, bf.OwnerLocalId, dc, NeedType.Disease, "hlth");
                }
            }

            // ── 거주 / 근로: 명부 vs 실제 출근 구분(2026-07-07 유저 지적) ──
            bool isResidence = em.HasComponent<ResidenceBuilding>(e);
            bool isWorkplace = em.HasComponent<WorkplaceBuilding>(e);
            var occup = (isResidence || isWorkplace) ? ScanOccupants(e) : default;

            if (em.HasComponent<BuildingOccupancy>(e))
            {
                var occ = em.GetComponentData<BuildingOccupancy>(e);
                if (isResidence)
                {
                    // 명부(정원) + 거주민 고용/출근 분해.
                    sb.Append("\nResidents ").Append(occ.Current).Append('/').Append(occ.Capacity);
                    sb.Append("\n employed ").Append(occup.ResEmployed)
                      .Append("  jobless ").Append(occup.ResJobless);
                    sb.Append("\n atWork ").Append(occup.ResAtWork)
                      .Append("  out ").Append(occup.ResOut)
                      .Append("  home ").Append(occup.ResHome);
                }
                else if (isWorkplace)
                {
                    var wp = em.GetComponentData<WorkplaceBuilding>(e);
                    // Roster = 고용 명부(BuildingOccupancy), Present = 지금 실제 출근(AtWork).
                    sb.Append("\nRoster ").Append(occ.Current).Append('/').Append(occ.Capacity)
                      .Append("  (").Append(wp.ProvidedJob.ToString()).Append(')')
                      .Append("\n present ").Append(occup.WorkPresent).Append(" (AtWork now)");
                }
                else
                    sb.Append("\nOccupancy ").Append(occ.Current).Append('/').Append(occ.Capacity);
            }

            // ── 방문 좌석(식당 등) ──
            if (em.HasComponent<VisitorOccupancy>(e))
            {
                var vo = em.GetComponentData<VisitorOccupancy>(e);
                sb.Append("\nSeats ").Append(vo.Current).Append('/').Append(vo.Capacity);
            }

            // ── 손님 누적(오늘/어제) ──
            if (em.HasComponent<ServiceStats>(e))
            {
                var ss = em.GetComponentData<ServiceStats>(e);
                sb.Append("\nCustomers today ").Append(ss.TodayServed)
                  .Append("  yday ").Append(ss.YesterdayServed);
            }

            // ── 생산 ──
            if (em.HasComponent<ProductionJob>(e))
            {
                var pj = em.GetComponentData<ProductionJob>(e);
                var recipe = RecipeDefs.Get(pj.RecipeOutput);
                sb.Append("\nProd ").Append(pj.RecipeOutput.ToString());   // staff는 위 공통 라인
                if (recipe.BaseDuration > 0f)
                {
                    if (pj.Progress < 0f) sb.Append("  [idle]");
                    else sb.Append("  ").Append(pj.Progress.ToString("0.0"))
                           .Append('/').Append(recipe.BaseDuration.ToString("0"));
                }
            }

            // ── 재고(입력/출력/보관/완성품 전 품목) ──
            if (em.HasBuffer<StockEntry>(e))
            {
                var stock = em.GetBuffer<StockEntry>(e, true);
                for (int i = 0; i < stock.Length; i++)
                {
                    var s = stock[i];
                    sb.Append('\n').Append(s.Commodity.ToString())
                      .Append(' ').Append(s.Current).Append('/').Append(s.Capacity)
                      .Append(" [").Append(s.Role.ToString()).Append(']');
                }
            }

            // ── 창고: 실제 재고는 owner 공유 풀(LogisticsPool)이 진실 — 위 [Store] 칸은 용량
            //   기여분(Current=0 vestigial). 이 창고가 속한 풀의 품목별 Stored/Capacity 표시. ──
            if (em.HasComponent<WarehouseTag>(e) && _poolQ.CalculateEntityCount() == 1)
            {
                var pool = _poolQ.GetSingleton<LogisticsPool>();
                if (pool.Cells.IsCreated)
                {
                    sb.Append("\n-- shared pool (P").Append(bf.OwnerLocalId).Append(") --");
                    foreach (var kv in pool.Cells)
                        if (kv.Key.x == bf.OwnerLocalId)
                            sb.Append('\n').Append(((Commodity)kv.Key.y).ToString())
                              .Append(' ').Append(kv.Value.Stored).Append('/').Append(kv.Value.Capacity)
                              .Append(" [Pool]");
                }
            }

            return sb.ToString();
        }

        struct OccupantScan
        {
            public int WorkPresent;                          // Work==건물 & AtWork
            public int ResEmployed, ResJobless;              // Home==건물 거주민 고용/무직
            public int ResAtWork, ResOut, ResHome;           // 거주민 현재 위치
        }

        // 건물 하나에 대한 시민 역쿼리(호버 시만, 0.2초 스로틀 — 저빈도라 전수 순회 수용).
        //   명부(BuildingOccupancy)는 소속 수, 이 스캔은 '지금 어디 있나'(실제 출근/외출).
        OccupantScan ScanOccupants(Entity building)
        {
            var scan = new OccupantScan();
            var res = _citQ.ToComponentDataArray<CitizenResidence>(Allocator.Temp);
            var st  = _citQ.ToComponentDataArray<CitizenState>(Allocator.Temp);
            for (int i = 0; i < res.Length; i++)
            {
                if (res[i].Work == building && st[i].Activity == CitizenActivity.AtWork)
                    scan.WorkPresent++;

                if (res[i].Home == building)
                {
                    if (res[i].Work != Entity.Null) scan.ResEmployed++; else scan.ResJobless++;
                    switch (st[i].Activity)
                    {
                        case CitizenActivity.AtWork:        scan.ResAtWork++; break;
                        case CitizenActivity.Traveling:
                        case CitizenActivity.AtDestination: scan.ResOut++;    break;
                        default:                            scan.ResHome++;   break;   // Idle/AtHome
                    }
                }
            }
            res.Dispose(); st.Dispose();
            return scan;
        }

        static string ClassifyType(EntityManager em, Entity e)
        {
            if (em.HasComponent<WarehouseTag>(e)) return "Warehouse";
            // 오라 공급자 우선(치안·의료 커버 시설 — 병원은 오라+방문 겸용이라 여기서 잡힘).
            if (em.HasComponent<AuraSupplier>(e))
            {
                var a = em.GetComponentData<AuraSupplier>(e);
                if ((a.Relief & NeedType.HighCrime) != NeedType.None)      return "Police";
                if ((a.Relief & NeedType.PoorHealthcare) != NeedType.None) return "Hospital";
            }
            if (em.HasComponent<StampSupplier>(e))
            {
                var ss = em.GetComponentData<StampSupplier>(e);
                if ((ss.Relief & NeedType.Hunger) != NeedType.None)           return "Restaurant";
                if ((ss.Relief & NeedType.Disease) != NeedType.None)          return "Hospital";
                if ((ss.Relief & NeedType.LowEntertainment) != NeedType.None) return "Park";
                return "Service";
            }
            if (em.HasComponent<ResidenceBuilding>(e)) return "House";
            if (em.HasComponent<WorkplaceBuilding>(e))
            {
                var wp = em.GetComponentData<WorkplaceBuilding>(e);
                return wp.ProvidedJob switch
                {
                    JobType.Farmer   => "Farm",
                    JobType.Engineer => "Mill",
                    JobType.Merchant => "Restaurant",
                    JobType.Doctor   => "Hospital",
                    _                => "Workplace",
                };
            }
            return "Building";
        }

        // 이 셀·욕구의 사유별 미충족 누적을 한 줄로(0이면 생략). df.Stats 키 = (owner,dx,dy,needBit).
        static void AppendDemand(System.Text.StringBuilder sb, in DemandField df,
                                 int owner, int2 dc, NeedType need, string label)
        {
            int bit = math.tzcnt((ulong)need);
            if (df.Stats.TryGetValue(new int4(owner, dc.x, dc.y, bit), out var st) && st.Failures > 0)
                sb.Append("\ndmd ").Append(label)
                  .Append(" cov").Append(st.FailNoCoverage)
                  .Append(" full").Append(st.FailFull)
                  .Append(" goods").Append(st.FailNoGoods)
                  .Append(" staff").Append(st.FailUnstaffed);
        }

        // NeedType(단일/복합 비트) → 짧은 표시명. 복합이면 첫 매칭.
        static string ReliefName(NeedType r)
        {
            if ((r & NeedType.Hunger) != NeedType.None)           return "food";
            if ((r & NeedType.LowEntertainment) != NeedType.None) return "fun";
            if ((r & NeedType.Disease) != NeedType.None)          return "disease";
            if ((r & NeedType.HighCrime) != NeedType.None)        return "safety";
            if ((r & NeedType.PoorHealthcare) != NeedType.None)   return "health";
            if (r == NeedType.None) return "none";
            return "#" + ((ulong)r).ToString();
        }
    }
}
