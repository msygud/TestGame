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
    //    - "무엇을 지을까"는 고정 (GrowthConfig.BuildingMainKey, 고정 구획 크기).
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
            var cfg = GrowthConfig.Default;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // ── AI 팀만 순회 (플레이어 제외) ───────────────────────────
            //   팀 인덱스 출처는 TeamStartPoint.TeamIndex (FactionBaseSpawnSystem과 일치).
            foreach (var (teamRO, startRO) in
                     SystemAPI.Query<RefRO<TeamInfoData>, RefRO<TeamStartPoint>>())
            {
                var team = teamRO.ValueRO;
                if (team.IsPlayer()) continue;        // 플레이어 도시는 자율 성장 안 함

                int teamIndex = startRO.ValueRO.TeamIndex;
                int factionId = ResolveFactionId(in factionConfig, teamIndex);

                TryGrowOneBlock(in layers, in entranceLookup, in metaLookup,
                    teamIndex, factionId, in cfg, ref ecb);
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
            int teamIndex, int factionId,
            in GrowthConfig cfg,
            ref EntityCommandBuffer ecb)
        {
            // 1) 후보 수집 (도로 특이점 인접 빈 저해상도 자리)
            var candidates = new NativeList<int2>(64, Allocator.Temp);
            BlockOps.CollectAnchorCandidates(
                in layers.RoadLayer,
                in layers.BlockLayer,
                in layers.OccupancyLayer,
                in layers.TerrainLayer,
                ref candidates);

            // 후보 없음 → 이 팀은 이번 턴 성장 안 함 (느슨하게 넘어감)
            if (candidates.Length == 0)
            {
                candidates.Dispose();
                return;
            }

            int2 blockSize = cfg.BlockSize;   // 고정 크기 (저해상도 단위)

            // 2~3) 통과 후보 중 공유 변 최대 선택
            int bestScore = -1;
            int2 bestPos = default;
            bool found = false;

            for (int i = 0; i < candidates.Length; i++)
            {
                int2 pos = candidates[i];

                if (!BlockOps.CanPlaceBlock(
                        in layers.BlockLayer,
                        in layers.OccupancyLayer,
                        in layers.TerrainLayer,
                        pos, blockSize))
                    continue;

                int score = BlockOps.CountSharedEdges(in layers.BlockLayer, pos, blockSize);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = pos;
                    found = true;
                }
            }

            candidates.Dispose();

            if (!found) return;   // 들어갈 자리 없음

            // 구획 원점의 실셀 좌표 = 건물 배치 기준 셀
            int2 realCell = BlockGrid.ToReal(bestPos);

            // 3.5) 입구-도로 정렬 회전 탐색 ─────────────────────────────
            //   작은 건물이 (큰 건물 기준으로 잡힌) 구획에 들어갈 때, 입구가
            //   경계 도로를 향하도록 회전을 고른다. 헬퍼는 "어느 회전이면 닿는가"
            //   라는 가능성만 반환하고, "그 회전으로 발행한다"는 결정은 여기서 한다.
            //
            //   입구 정의가 없는 건물(HasEntrance=false 또는 EntranceLookup 미등록)은
            //   FindRoadFacingRotation이 0을 돌려주므로 기본 회전으로 발행된다.
            //   어느 회전으로도 도로에 닿지 않으면(-1) 이번 턴 이 구획은 포기한다
            //   (구획 등록도 하지 않음 → 다음 기회에 다른 자리/도로 확장 후 재시도).
            //
            //   ⚠ 현재 건물 footprint는 정사각(저해상도 1×1 = 실셀 2×2)이라 회전이
            //     점유 셀 집합을 바꾸지 않으므로 입구만 보는 이 오버로드로 충분하다.
            //     비정사각 건물(Size.x≠Size.y)로 확장하면 회전마다 footprint가 달라지므로
            //     EntranceOps.FindRoadFacingRotation(origin, size, ..., footprintFree)
            //     오버로드로 교체하고, footprintFree 콜백에 BlockOps의 점유/범위 판정을
            //     연결해야 한다(입구 정렬 + footprint 적합을 함께 만족하는 회전 선택).
            float rotationY = 0f;

            if (metaLookup.TryGetMeta(cfg.BuildingMainKey, 0, out var meta) &&
                meta.HasEntrance &&
                entranceLookup.TryGet(cfg.BuildingMainKey, out var entranceOffsets))
            {
                int steps = EntranceOps.FindRoadFacingRotation(
                    realCell, in entranceOffsets, in layers.RoadLayer,
                    requireAll: false);

                if (steps < 0)
                    return;   // 입구를 도로로 향하게 할 수 없음 → 이 자리 포기

                rotationY = EntranceOps.StepsToRotationY(steps);
            }

            // 4) 구획 등록 + 건물 발행
            //    RegisterBlock 은 BlockLayer(NativeHashMap)만 갱신 (구조 변경 아님).
            //    layers 는 싱글톤 복사본이지만 NativeHashMap 핸들은 내부 버퍼를
            //    공유하므로 쓰기가 원본에 반영된다.
            var blockLayer = layers.BlockLayer;
            BlockOps.RegisterBlock(ref blockLayer, bestPos, blockSize, teamIndex);

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new PlaceBuildingRequest
            {
                MainKey = cfg.BuildingMainKey,
                VariantKey = 0,
                Cell = realCell,
                RotationY = rotationY,
                TeamIndex = teamIndex,
                FactionId = factionId,
                RequireRoadAccess = true,   // AI 자율 성장 → 입구-도로 정렬 강제
            });
        }

        // ──────────────────────────────────────────────────────────────────
        //  팀 인덱스 → FactionId (FactionConfig.Slots 조회)
        // ──────────────────────────────────────────────────────────────────
        static int ResolveFactionId(in FactionConfig cfg, int teamIndex)
        {
            if (cfg.Slots.IsCreated && cfg.Slots.TryGetValue(teamIndex, out var slot))
                return slot.FactionId;
            return 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GrowthConfig — 단순 성장 단계의 설정값
    //
    //  지금은 고정 기본값. 추후 SO/싱글톤으로 팩션·난이도별 분리 가능.
    //    - BuildingMainKey: 성장에 사용할 건물 프리팹 키 (PrefabLookup에 등록돼야 함).
    //    - BlockSize: 한 번에 놓는 구획 크기 (저해상도 단위). {2,4,8} → {1,2,4}.
    // ══════════════════════════════════════════════════════════════════════════
    public struct GrowthConfig
    {
        public int BuildingMainKey;
        public int2 BlockSize;

        public static GrowthConfig Default => new GrowthConfig
        {
            BuildingMainKey = 1000,           // Building 범위(1000~4999). TODO: 실제 등록 키로 교체.
            BlockSize = new int2(1, 1), // 저해상도 1×1 = 실셀 2×2
        };
    }
}
