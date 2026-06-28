using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritorySystem — 인구 기반 영역 재계산 (파괴 없음 — 표시 전용)
    // ──────────────────────────────────────────────────────────────────────────
    //  ~1초마다(실시간) 전체 재계산(기존 결정 셀도 매번 재결정). 두 개념을 분리한다:
    //
    //    [영역 reach] = 물리적 범위. 거주건물마다 셀 수 = floor(인구 / PopPerCell).
    //      소유자별 예산(셀 수 합)만큼 거주지 중심에서 '가장 가까운 셀'을 차지(다중소스 nearest-N).
    //      → 어느 팀이 그 셀에 '닿는가(영역이 미치는가)'를 결정. 겹치면 경합.
    //
    //    [영향력 influence] = 그 셀에서 팀이 가진 '힘'. 여기선 Σ 거주지 인구/(1+거리)
    //      (가까운/큰 인구일수록 강함). ※placeholder — 추후 시민 행복도·팩션 보정을 곱해 대체.
    //      겹친(경합) 셀의 소유는 **영향력 1등**이 가진다. 동률(±ContestMargin)이면 중립(미소유).
    //      3팀+ 경합도 영향력 상위 둘만 보면 되므로 별도 로직 불필요.
    //
    //    · 결과를 GridLayers.TerritoryLayer(int2→LocalId)에 기록(클리어 후 재작성).
    //
    //  ※ capture(영역 침범물 파괴)는 폐기 — 진 팀 건물/도로는 파괴하지 않는다.
    //    소유 변화는 시각(아웃라인=TerritoryOutlineRenderSystem)으로만 표시.
    //  메인스레드·저빈도(1초). TerritoryLayer의 유일한 writer.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TerritorySystem : ISystem
    {
        // 경합 셀에서 1·2등 영향력 차가 (이 비율 × 1등) 이하면 동률로 보고 중립.
        const float ContestMargin = 0.02f;

        double _nextRecompute;   // 다음 재계산 실시각(초)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            _nextRecompute = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── 초단위 게이트 ───────────────────────────────────────────────
            double now = SystemAPI.Time.ElapsedTime;
            if (now < _nextRecompute) return;
            _nextRecompute = now + 1.0;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) return;

            float popPerCell = TerritoryConfig.Default.PopPerCell;
            int   maxRadius  = TerritoryConfig.Default.MaxRadius;
            if (SystemAPI.TryGetSingleton<TerritoryConfig>(out var cfg))
            {
                popPerCell = cfg.PopPerCell > 0f ? cfg.PopPerCell : TerritoryConfig.Default.PopPerCell;
                maxRadius  = math.max(1, cfg.MaxRadius);
            }

            // ── 거주건물 수집 ──────────────────────────────────────────────
            var residences = new NativeList<ResInfo>(64, Allocator.Temp);
            foreach (var (occ, bf) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                              .WithAll<ResidenceBuilding>())
            {
                int owner = bf.ValueRO.OwnerLocalId;
                if ((uint)owner >= StampLayers.MaxPlayers) continue;

                int pop = occ.ValueRO.Current > 0 ? occ.ValueRO.Current : occ.ValueRO.Capacity;
                if (pop <= 0) continue;

                // 셀 수 = floor(인구 / PopPerCell). float 나눗셈, 나머지는 무조건 내림.
                int cells = (int)math.floor(pop / popPerCell);
                if (cells <= 0) continue;   // 충족 인구 미달 → 영역 0칸

                int2 eff    = EntranceOps.RotateSize(bf.ValueRO.Size, bf.ValueRO.RotSteps);
                int2 center = bf.ValueRO.Origin + eff / 2;

                residences.Add(new ResInfo
                {
                    Owner  = owner,
                    Center = center,
                    Cells  = cells,
                    Pop    = pop,
                });
            }

            // ── 소유자별 nearest-N 배정(영역) → 셀마다 상위 2팀(영향력) 추적 ──
            var best = new NativeHashMap<int2, Owner2>(2048, Allocator.Temp);
            var cmp  = new ClaimComparer();

            for (int owner = 0; owner < StampLayers.MaxPlayers; owner++)
            {
                // 이 소유자의 예산 + 경계 박스
                int budget = 0;
                int2 bbMin = default, bbMax = default;
                bool any = false;
                for (int i = 0; i < residences.Length; i++)
                {
                    if (residences[i].Owner != owner) continue;
                    budget += residences[i].Cells;
                    int2 c = residences[i].Center;
                    if (!any) { bbMin = c; bbMax = c; any = true; }
                    else { bbMin = math.min(bbMin, c); bbMax = math.max(bbMax, c); }
                }
                if (!any || budget <= 0) continue;

                int margin = math.min(maxRadius, (int)math.ceil(math.sqrt(budget / math.PI)) + 2);

                // 윈도우 내 후보 셀: 영역 선택용 '최근접 거리(md)' + '영향력(inf)' 동시 계산.
                //   inf = Σ 거주지 인구/(1+거리) — 가까운/큰 인구일수록 그 셀에서 강함.
                var cand = new NativeList<Claim>(256, Allocator.Temp);
                for (int z = bbMin.y - margin; z <= bbMax.y + margin; z++)
                for (int x = bbMin.x - margin; x <= bbMax.x + margin; x++)
                {
                    int2 cell = new int2(x, z);
                    if (!layers.TerrainLayer.ContainsKey(cell)) continue;   // 맵 안만

                    float md = float.MaxValue, inf = 0f;
                    for (int i = 0; i < residences.Length; i++)
                    {
                        if (residences[i].Owner != owner) continue;
                        float d = math.distance((float2)cell, (float2)residences[i].Center);
                        if (d < md) md = d;
                        inf += residences[i].Pop / (1f + d);
                    }
                    if (md < float.MaxValue) cand.Add(new Claim { Dist = md, Inf = inf, Cell = cell });
                }

                // [영역] 가까운 순으로 예산만큼만 reach. 각 reach 셀에 [영향력] 상위 2팀 기록.
                var arr = cand.AsArray();
                arr.Sort(cmp);
                int take = math.min(budget, arr.Length);
                for (int k = 0; k < take; k++)
                {
                    var c = arr[k];
                    if (!best.TryGetValue(c.Cell, out var b))
                        b = new Owner2 { O1 = -1, Inf1 = 0f, O2 = -1, Inf2 = 0f };
                    // owner는 셀당 1회만 들어오므로 기존 O1/O2와 항상 다른 팀. 영향력 큰 순.
                    if (c.Inf > b.Inf1)      { b.O2 = b.O1; b.Inf2 = b.Inf1; b.O1 = owner; b.Inf1 = c.Inf; }
                    else if (c.Inf > b.Inf2) { b.O2 = owner; b.Inf2 = c.Inf; }
                    best[c.Cell] = b;
                }
                cand.Dispose();
            }

            // ── TerritoryLayer 재작성 — 경합은 영향력 1등 소유, 동률(±Margin)이면 중립 ──
            layers.TerritoryLayer.Clear();
            foreach (var kv in best)
            {
                var b = kv.Value;
                if (b.O1 < 0) continue;
                // 경합(2팀+ 도달)에서 1·2등 영향력이 박빙이면 중립(힘겨루기 무승부).
                if (b.O2 >= 0 && (b.Inf1 - b.Inf2) <= ContestMargin * b.Inf1) continue;
                layers.TerritoryLayer[kv.Key] = b.O1;
            }

            residences.Dispose();
            best.Dispose();
        }

        struct ResInfo { public int Owner; public int2 Center; public int Cells; public int Pop; }
        // 셀당 상위 2팀(영향력). O2=-1이면 단독(경합 아님).
        struct Owner2 { public int O1; public float Inf1; public int O2; public float Inf2; }
        struct Claim { public float Dist; public float Inf; public int2 Cell; }

        // 거리 오름차순(영역 reach 선택용), 동률은 셀 좌표(결정적).
        struct ClaimComparer : IComparer<Claim>
        {
            public int Compare(Claim a, Claim b)
            {
                if (a.Dist < b.Dist) return -1;
                if (a.Dist > b.Dist) return 1;
                if (a.Cell.x != b.Cell.x) return a.Cell.x - b.Cell.x;
                return a.Cell.y - b.Cell.y;
            }
        }
    }
}
