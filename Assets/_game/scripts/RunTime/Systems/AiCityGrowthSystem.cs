using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Game.Unit;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AiCityGrowthSystem — AI 팀의 도시 성장 결정 (단순 성장 버전)
    //
    //  역할: BlockOps 헬퍼가 나열한 '가능성'을 받아 '결정'을 내린다(정책).
    //    헬퍼 = 사실(후보·공유변·가능여부), 시스템 = 선택·발행.
    //
    //  느슨함 원칙(§메모):
    //    - 트리거는 게임시간 경계(DayChanged). 일시정지/배속 자동 반영.
    //    - 결정 예산: 한 틱(하루)에 팀당 구획 1개만. 비용 상한 + 점진 성장.
    //    - 즉각 반응이 아니라 관성 있는 성장 → 재미 + 성능.
    //
    //  단순 성장 정책(이번 단계):
    //    - "무엇을 지을까"는 건물 2종(GrowthConfig.BuildingKeyA/B). 구획 크기는 meta.Size에서 유도.
    //    - 후보 점수 = 공유 변 수(CountSharedEdges)만. 클수록 응집(빈틈부터 메움).
    //    - needs/자원/응집·자유 가중치는 추후 점수 항목으로 추가.
    //
    //  처리 흐름(팀별):
    //    1. CollectAnchorCandidates → 도로 특이점 인접 빈 자리 후보
    //    2. 각 후보에 고정 크기 구획이 CanPlaceBlock?
    //    3. 통과 후보 중 CountSharedEdges 최대 선택
    //    4. RegisterBlock + 구획 원점에 건물 PlaceBuildingRequest 발행
    //
    //  주의(ECS 구조 변경):
    //    - request 엔티티 생성은 ECB로만. OnUpdate 본문에서 직접 구조변경 금지.
    //    - 헬퍼는 NativeHashMap 값 수정/조회만 (구조 변경 아님) → 메인스레드 OK.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AiCityGrowthSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<FactionConfig>();
            state.RequireForUpdate<EntranceLookup>();
            state.RequireForUpdate<PrefabMetaLookup>();
            state.RequireForUpdate<RoadKeyLookup>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();

            // ── 느슨한 트리거: 하루 경계에서만 1회 ──────────────────────
            if (!clock.DayChanged) return;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            var factionConfig = SystemAPI.GetSingleton<FactionConfig>();
            var entranceLookup = SystemAPI.GetSingleton<EntranceLookup>();
            var metaLookup = SystemAPI.GetSingleton<PrefabMetaLookup>();
            var roadKeyLookup = SystemAPI.GetSingleton<RoadKeyLookup>();
            var cfg = GrowthConfig.Default;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // ── AI 팀만 순회 (플레이어 제외) ───────────────────────────
            //   포지션 번호 출처는 TeamStartPoint.TeamIndex (FactionBaseSpawnSystem과 일치).
            //   포지션 번호 → FactionId 조회에만 사용하고,
            //   셀 소유 기록에는 소유주 LocalId(= 이 슬롯)를 넘긴다.
            foreach (var (teamRO, startRO) in
                     SystemAPI.Query<RefRO<TeamInfoData>, RefRO<TeamStartPoint>>())
            {
                var team = teamRO.ValueRO;
                if (team.IsPlayer()) continue;        // 플레이어 도시는 자율 성장 안 함

                int positionIndex = startRO.ValueRO.TeamIndex;
                int ownerLocalId = team.LocalID;     // 소유는 LocalId 단위
                int factionId = ResolveFactionId(in factionConfig, positionIndex);

                TryGrowOneBlock(in layers, in entranceLookup, in metaLookup, in roadKeyLookup,
                    ownerLocalId, factionId, in cfg, ref ecb);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        // ──────────────────────────────────────────────────────────────────
        //  팀 1개에 대해 구획 1개 성장 시도 (결정 예산 = 1)
        // ──────────────────────────────────────────────────────────────────
        static void TryGrowOneBlock(
            in GridLayers layers,
            in EntranceLookup entranceLookup,
            in PrefabMetaLookup metaLookup,
            in RoadKeyLookup roadKeyLookup,
            int ownerLocalId, int factionId,
            in GrowthConfig cfg,
            ref EntityCommandBuffer ecb)
        {
            // 건물 자리가 있으면 건물 1개. 없으면(공간 부족) 도로를 한 줄 연장해
            // 새 공간(끝=특이점)을 연다 — 유기적 확장.
            if (TryPlaceBuilding(in layers, in entranceLookup, in metaLookup,
                    ownerLocalId, factionId, in cfg, ref ecb))
                return;

            TryExtendRoad(in layers, in roadKeyLookup, ownerLocalId, factionId, in cfg, ref ecb);
        }

        // ──────────────────────────────────────────────────────────────────
        //  건물 1개 배치 시도. 발행했으면 true.
        // ──────────────────────────────────────────────────────────────────
        static bool TryPlaceBuilding(
            in GridLayers layers,
            in EntranceLookup entranceLookup,
            in PrefabMetaLookup metaLookup,
            int ownerLocalId, int factionId,
            in GrowthConfig cfg,
            ref EntityCommandBuffer ecb)
        {
            // 내 도로 옆면(인접 빈 평지)에 건물 1격을 붙인다 — 실셀 단위.
            //   · 후보 = 팀 도로 셀의 4방향 인접 빈 셀 n (방향 d = 도로 바깥쪽).
            //   · footprint는 n을 도로 접면 가장자리로 두고 d 방향으로 뻗는다(접면 필수).
            //   · 두 건물 모두 시도, 점수 = 응집(이웃 점유/도로) 우선 · 동률 시 큰 건물.
            long  bestScore = -1;
            int2  bestOrigin = default;
            int   bestKey    = 0;
            float bestRot    = 0f;
            bool  found      = false;

            foreach (var kv in layers.RoadLayer)
            {
                if (kv.Value.OwnerLocalId != ownerLocalId) continue;
                int2 r = kv.Key;

                for (int d = 0; d < 4; d++)
                {
                    int2 off = RoadDirOps.Offsets[d];
                    int2 n   = r + off;                  // 도로 바깥 인접 셀(건물 접면)
                    if (!CellBuildable(n, in layers)) continue;

                    for (int k = 0; k < 2; k++)
                    {
                        int key = k == 0 ? cfg.BuildingKeyA : cfg.BuildingKeyB;
                        if (key <= 0) continue;
                        if (!metaLookup.TryGetMeta(key, 0, out var meta)) continue;

                        int2 sz = math.max(meta.Size, new int2(1, 1));

                        // n을 도로 접면 가장자리로 두고 d 방향으로 뻗는 footprint 원점
                        int2 origin = new int2(
                            d == 3 ? n.x - (sz.x - 1) : n.x,   // W로 뻗으면 원점은 왼쪽
                            d == 2 ? n.y - (sz.y - 1) : n.y);  // S로 뻗으면 원점은 아래

                        if (!FootprintFreeFlat(origin, sz, in layers)) continue;

                        float rot = 0f;
                        if (meta.HasEntrance && entranceLookup.TryGet(key, out var ent))
                        {
                            int steps = EntranceOps.FindRoadFacingRotation(
                                origin, sz, in ent, in layers.RoadLayer);
                            if (steps < 0) continue;     // 입구가 도로를 못 향함 → 이 조합 포기
                            rot = EntranceOps.StepsToRotationY(steps);
                        }

                        int  cohesion = Cohesion(origin, sz, in layers);
                        long score    = (long)cohesion * 1000 + sz.x * sz.y;
                        if (score > bestScore)
                        {
                            bestScore  = score;
                            bestOrigin = origin;
                            bestKey    = key;
                            bestRot    = rot;
                            found      = true;
                        }
                    }
                }
            }

            // 도로변 빈자리 없음 → 도로 연장으로 폴백.
            if (!found) return false;

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new PlaceBuildingRequest
            {
                MainKey = bestKey,
                VariantKey = 0,
                Cell = bestOrigin,
                RotationY = bestRot,
                OwnerLocalId = ownerLocalId,
                FactionId = factionId,
                RequireRoadAccess = true,   // AI 자율 성장 → 입구-도로 정렬 강제
            });
            return true;
        }

        // ── 실셀 배치 판정 헬퍼 ───────────────────────────────────────────

        // 단일 셀을 건물로 쓸 수 있나: 맵 안 · 도로 아님 · 점유 비었거나 환경 · 자원 아님.
        static bool CellBuildable(int2 c, in GridLayers layers)
        {
            if (!layers.TerrainLayer.ContainsKey(c)) return false;   // 맵 밖
            if (layers.RoadLayer.ContainsKey(c))     return false;   // 도로
            if (layers.OccupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty
                && occ.Type != OccupantType.Environment) return false;
            if (layers.ResourceLayer.IsCreated
                && layers.ResourceLayer.TryGetValue(c, out var res) && res.Amount > 0) return false;
            return true;
        }

        // footprint 전체가 건물 가능 + 평탄(균일 높이)인가.
        static bool FootprintFreeFlat(int2 origin, int2 sz, in GridLayers layers)
        {
            bool first = true; byte baseH = 0;
            for (int dx = 0; dx < sz.x; dx++)
            for (int dz = 0; dz < sz.y; dz++)
            {
                int2 c = origin + new int2(dx, dz);
                if (!CellBuildable(c, in layers)) return false;
                byte h = layers.TerrainLayer.TryGetValue(c, out var tc) ? tc.Height : (byte)0;
                if (first) { baseH = h; first = false; }
                else if (h != baseH) return false;
            }
            return true;
        }

        // 응집도: footprint 4변 바깥 인접 셀 중 점유(건물)·도로 수. 클수록 기존 덩어리에 밀착.
        static int Cohesion(int2 origin, int2 sz, in GridLayers layers)
        {
            int count = 0;
            for (int dx = -1; dx <= sz.x; dx++)
            for (int dz = -1; dz <= sz.y; dz++)
            {
                bool inside = dx >= 0 && dx < sz.x && dz >= 0 && dz < sz.y;
                if (inside) continue;
                bool edge = (dx >= 0 && dx < sz.x) || (dz >= 0 && dz < sz.y); // 대각 제외, 변만
                if (!edge) continue;

                int2 c = origin + new int2(dx, dz);
                if (layers.RoadLayer.ContainsKey(c)) { count++; continue; }
                if (layers.OccupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty
                    && occ.Type != OccupantType.Environment) count++;
            }
            return count;
        }

        // ──────────────────────────────────────────────────────────────────
        //  도로 1줄 연장 시도 (건물 자리 없을 때). 발행했으면 true.
        //
        //  내 도로 끝/가장자리에서 빈 평지로 직선 stub을 깐다. 새 끝(특이점)이
        //  다음 턴 건물 후보(anchor)가 되어 도시가 그쪽으로 자란다.
        //  도로 크기는 팩션 기본 크기(RoadKeyLookup) footprint 단위.
        // ──────────────────────────────────────────────────────────────────
        static bool TryExtendRoad(
            in GridLayers layers,
            in RoadKeyLookup roadKeyLookup,
            int ownerLocalId, int factionId,
            in GrowthConfig cfg,
            ref EntityCommandBuffer ecb)
        {
            byte size = roadKeyLookup.GetSize(factionId);

            if (!BlockOps.FindRoadExtension(
                    in layers.RoadLayer,
                    in layers.OccupancyLayer,
                    in layers.TerrainLayer,
                    in layers.ResourceLayer,
                    ownerLocalId, size, cfg.MaxRoadExtendSteps,
                    out int2 extOrigin, out int dir, out int steps))
                return false;

            int2 off  = RoadDirOps.Offsets[dir];
            var  axis = off.x != 0 ? RoadPlacedAxis.EW : RoadPlacedAxis.NS;
            int  s    = math.max(1, size);

            int2 cur = extOrigin;
            for (int k = 0; k < steps; k++)
            {
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new PlaceRoadCommand
                {
                    Cell         = cur,
                    OwnerLocalId = ownerLocalId,
                    LaneCount    = 2,
                    FactionId    = factionId,
                    Size         = size,
                    Axis         = axis,
                });
                cur += off * s;
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        //  팀 인덱스 → FactionId (FactionConfig.Slots 조회)
        // ──────────────────────────────────────────────────────────────────
        static int ResolveFactionId(in FactionConfig cfg, int positionIndex)
        {
            if (cfg.Slots.IsCreated && cfg.Slots.TryGetValue(positionIndex, out var slot))
                return slot.FactionId;
            return 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GrowthConfig — 단순 성장 단계의 설정값
    //
    //  지금은 고정 기본값. 추후 SO/싱글톤으로 팩션·난이도별 분리 가능.
    //    - BuildingKeyA/B: 성장에 사용할 건물 키 2종 (PrefabLookup에 등록돼야 함).
    //      구획 크기는 각 건물 meta.Size에서 유도(회전 안전 정사각).
    // ══════════════════════════════════════════════════════════════════════════
    public struct GrowthConfig
    {
        // 성장에 쓸 건물 키 2종 (크기 달라도 됨 — 구획 크기는 meta.Size에서 유도).
        public int BuildingKeyA;
        public int BuildingKeyB;
        public int MaxRoadExtendSteps;   // 한 번에 연장할 도로 footprint 최대 수

        public static GrowthConfig Default => new GrowthConfig
        {
            BuildingKeyA = 1004,        // 등록된 성장 건물 (크기 A)
            BuildingKeyB = 1005,        // 등록된 성장 건물 (크기 B)
            MaxRoadExtendSteps = 1,     // 도로는 한 번에 1 footprint만 — 뻗고 건물이 채운 뒤 다시 뻗음(난립 방지)
        };
    }
}
