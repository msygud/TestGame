using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Game.Unit;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Stage A — 시민 생성 (Spawn)
    //
    //  SpawnCitizenRequest 를 발행하면 CitizenSpawnSystem이:
    //    1. 시민 엔티티 생성
    //    2. 능력치(콜드) 결정적 랜덤 생성 (Seed)
    //    3. 컨디션(핫) Healthy 초기화, 욕구 비움
    //    4. 직업·소속·상태 초기화
    //    5. CitizenTeam(SharedComponent) 부착 — 팀별 청크 분리
    //    6. 요청 엔티티 파괴
    //
    //  소속(집·직장) 실제 배정은 Stage A 후반 또는 별도 배정 시스템에서.
    //  여기서는 Home/Work = Entity.Null(미배정)로 시작 가능.
    //
    //  구조 변경 원칙(§0-5): foreach 안에서 직접 구조 변경 금지.
    //    → EntityCommandBuffer 사용. 시민 생성은 ecb.CreateEntity로 안전.
    // ══════════════════════════════════════════════════════════════════════════

    public struct SpawnCitizenRequest : IComponentData
    {
        public int     TeamIndex;     // 0~7. CitizenTeam 부착에 사용.
        public bool     IsPlayer;     // 플레이어 본인 슬롯 여부
        public bool     IsPlayerTeam; // 플레이어 팀 여부
        public float3   Position;     // 스폰 위치
        public uint     Seed;         // 능력치 결정적 생성 시드(0이면 위치로 파생)
        public JobType  InitialJob;   // 초기 직업(Unemployed 가능)
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CitizenSpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (reqRO, reqEntity) in
                     SystemAPI.Query<RefRO<SpawnCitizenRequest>>().WithEntityAccess())
            {
                var req = reqRO.ValueRO;
                SpawnOne(req, ecb);
                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        // ── 시민 1명 생성 ─────────────────────────────────────────────────
        static void SpawnOne(in SpawnCitizenRequest req, EntityCommandBuffer ecb)
        {
            var e = ecb.CreateEntity();

            // 식별
            ecb.AddComponent<CitizenTag>(e);

            // 위치
            ecb.AddComponent(e, LocalTransform.FromPosition(req.Position));

            // 콜드: 능력치(결정적 랜덤)
            uint seed = req.Seed != 0
                ? req.Seed
                : math.hash(new float3(req.Position.x, req.Position.y, req.Position.z)) | 1u;
            ecb.AddComponent(e, RollAttributes(seed));

            // 핫: 컨디션 / 욕구
            ecb.AddComponent(e, CitizenConditions.Healthy);
            ecb.AddComponent(e, new CitizenNeeds
            {
                ActiveMask = NeedType.None,
                Pursuing   = NeedType.None,
            });

            // 욕구 게이지 버퍼 — 시민 개인 생활 욕구(부정 방향, 0=만족에서 시작).
            // 증가율·임계치는 Stage B 기본값. 추후 능력치/밸런싱으로 조정.
            var needs = ecb.AddBuffer<NeedElement>(e);
            AddNeed(ref needs, NeedType.Hunger,           rate: 0.010f, threshold: 0.6f);
            AddNeed(ref needs, NeedType.LowEntertainment, rate: 0.004f, threshold: 0.7f);
            AddNeed(ref needs, NeedType.LowEducation,     rate: 0.002f, threshold: 0.7f);
            AddNeed(ref needs, NeedType.LowReligion,      rate: 0.002f, threshold: 0.8f);
            // 주거/직장은 배정 상태에서 파생(미배정이면 즉시 불만) → ConditionUpdate에서
            // 다룰 수도 있으나, 일단 게이지로도 추적 가능하게 추가해 둠.
            AddNeed(ref needs, NeedType.Homeless,         rate: 0f,     threshold: 0.5f);
            AddNeed(ref needs, NeedType.Unemployed,       rate: 0f,     threshold: 0.5f);

            // 콜드: 직업
            ecb.AddComponent(e, new JobData
            {
                Job   = req.InitialJob,
                Skill = 0f,
            });

            // 소속: 미배정으로 시작(배정 시스템이 채움)
            ecb.AddComponent(e, new CitizenResidence
            {
                Home = Entity.Null,
                Work = Entity.Null,
            });
            ecb.AddComponent<UnassignedTag>(e);   // 배정 대상 표시

            // 동적: 상태
            ecb.AddComponent(e, new CitizenState
            {
                Activity        = CitizenActivity.Idle,
                CurrentBuilding = Entity.Null,
                ActionEndTime   = 0.0,
            });

            // 팀: SharedComponent (팀별 청크 분리)
            ecb.AddSharedComponent(e, new CitizenTeam(
                req.TeamIndex, req.IsPlayer, req.IsPlayerTeam));
        }

        // ── 능력치 결정적 생성 ────────────────────────────────────────────
        //  정규분포 흉내: 두 균등난수 평균 → 가운데로 모이는 분포(중앙값 ~128).
        //  극단값이 드물어 "대부분 평범, 일부 특출/저열"한 인구 분포.
        static CitizenAttributes RollAttributes(uint seed)
        {
            if (seed == 0) seed = 1u;   // Unity.Mathematics.Random은 seed 0에서 부정확.
            var rng = new Random(seed);
            return new CitizenAttributes
            {
                Physique     = Roll(ref rng),
                Intelligence = Roll(ref rng),
                Dexterity    = Roll(ref rng),
                Sociability  = Roll(ref rng),
                Diligence    = Roll(ref rng),
                Resilience   = Roll(ref rng),
                Creativity   = Roll(ref rng),
            };
        }

        static byte Roll(ref Random rng)
        {
            // 두 난수 평균 → 가운데로 모임(0~255).
            int a = rng.NextInt(0, 256);
            int b = rng.NextInt(0, 256);
            return (byte)((a + b) >> 1);
        }

        // ── 욕구 게이지 1개 추가(초기 Level 0 = 만족) ─────────────────────
        static void AddNeed(
            ref DynamicBuffer<NeedElement> buf,
            NeedType type, float rate, float threshold)
        {
            buf.Add(new NeedElement
            {
                Type      = type,
                Level     = 0f,
                Rate      = rate,
                Threshold = threshold,
            });
        }
    }
}
