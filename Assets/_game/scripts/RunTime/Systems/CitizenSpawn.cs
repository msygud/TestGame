using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

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
    //    5. OwnerShared(SharedComponent) 부착 — 플레이어 LocalId별 청크 분리
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
        public int     LocalId;       // 0~7. 소유 플레이어 슬롯. OwnerShared 부착에 사용.
        public float3   Position;     // 스폰 위치
        public uint     Seed;         // 능력치 결정적 생성 시드(0이면 위치로 파생)
        public JobType  InitialJob;   // 초기 직업(Unemployed 가능)
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CitizenSpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // 허기 증가율(끼니 주기) — 밸런스 패널(CitizenConfig)에서. 스폰 시점 베이킹.
            float hungerRate = (SystemAPI.TryGetSingleton<CitizenConfig>(out var cfg)
                ? cfg : CitizenConfig.Default).HungerRatePerGameSec;
            if (hungerRate <= 0f) hungerRate = CitizenConfig.Default.HungerRatePerGameSec;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (reqRO, reqEntity) in
                     SystemAPI.Query<RefRO<SpawnCitizenRequest>>().WithEntityAccess())
            {
                var req = reqRO.ValueRO;
                SpawnOne(req, hungerRate, ecb);
                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        // ── 시민 1명 생성 ─────────────────────────────────────────────────
        static void SpawnOne(in SpawnCitizenRequest req, float hungerRate, EntityCommandBuffer ecb)
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
                Pursuing   = NeedType.None,
            });

            // 욕구 컴포넌트 — 개별 부착(부정 방향).
            // A-1: Hunger 하나로 골격. 다른 욕구/팩션 조합은 이후 단계에서 추가.
            // 초기 Level·Rate 무작위 분산(시드 결정적, 2026-07-06): 동시 스폰 집단이
            //   같은 주기로 일제히 배고파지면 보행자/탐색이 한 프레임에 몰려 스파이크
            //   (동조화·thundering herd). 시작점과 주기를 흩어 영구히 탈동조화한다.
            var needRng = Random.CreateFromIndex(seed ^ 0x9E3779B9u);
            ecb.AddComponent(e, new Hunger
            {
                Level     = needRng.NextFloat(0f, 0.55f),           // 임계(0.6) 미만에서 산포
                Rate      = hungerRate * needRng.NextFloat(0.85f, 1.15f),
                Threshold = 0.6f,
            });

            // 공무불만(2026-07-17 통합 — 구 치안 CitizenSafety 자리·난수 소비 동일 = 기존
            //   Boredom 이후 산포 불변): 현재 위치의 관리형 서비스 커버 가중합(치안·소방·
            //   환경·행정)이 목표를 정함(CivicSystem). Rate 0.0005/게임초 = 전면 미커버 방치
            //   시 ~1.2 게임일에 임계(0.6) 도달.
            ecb.AddComponent(e, new CitizenCivic
            {
                Level     = needRng.NextFloat(0f, 0.3f),
                Rate      = 0.0005f * needRng.NextFloat(0.85f, 1.15f),
                Threshold = 0.6f,
            });

            // 따분함(체류형 욕구 v1, 2026-07-12) — 공원류 방문·체류(시간 적분)로 해소.
            //   기본 Rate 0.0008/게임초 ≈ 방치 시 ~0.9 게임일에 임계(0.6) 도달.
            //   Safety 뒤에 추가(needRng 소비 순서 보존 — 기존 욕구 산포 불변).
            ecb.AddComponent(e, new CitizenBoredom
            {
                Level     = needRng.NextFloat(0f, 0.3f),
                Rate      = 0.0008f * needRng.NextFloat(0.85f, 1.15f),
                Threshold = 0.6f,
            });

            // 나이 + 질병(생애주기 v1, 2026-07-12) — Boredom 뒤 추가(needRng 순서 보존).
            ecb.AddComponent(e, new CitizenAge { Years = needRng.NextFloat(18f, 50f) });
            ecb.AddComponent(e, new CitizenSickness
            {
                Level     = 0f,
                Rate      = 0.0005f,   // 자연 회복(병세 0.8 ≈ 27게임시간) — 입원(0.003)의 1/6
                Threshold = 0.3f,
            });

            // 질병 상태 토글 + 헬스케어 커버 값(2026-07-13 상태화). DiseasedTag는 비활성으로
            //   시작(건강) — 발병 시 SicknessSystem이 enable. CitizenHealthcare는 질병 체크가
            //   현재 건물 커버로 머티리얼라이즈(고정 상수·전원 동일, needRng 미소비 = 순서 무관).
            ecb.AddComponent<DiseasedTag>(e);
            ecb.SetComponentEnabled<DiseasedTag>(e, false);
            ecb.AddComponent(e, new CitizenHealthcare { Value = 0f });

            // 교육(체류형, 2026-07-17 — 학교 방문·체류 해소, Boredom 동형). 관리형 오라
            //   서비스(환경·행정·소방)는 개별 컴포넌트 없음 — 위 CitizenCivic 하나로 통합.
            ecb.AddComponent(e, new CitizenEducation
            {
                Level     = needRng.NextFloat(0f, 0.3f),
                Rate      = 0.0006f * needRng.NextFloat(0.85f, 1.15f),   // 방치 ~1.2게임일에 임계
                Threshold = 0.6f,
            });

            // 콜드: 직업 + 직업별 숙련(고용 2차 — 직업이 바뀌어도 각 숙련 보존)
            ecb.AddComponent(e, new JobData { Job = req.InitialJob });
            ecb.AddComponent(e, CitizenSkills.Empty);

            // 소속: 미배정으로 시작(배정 시스템이 채움)
            ecb.AddComponent(e, new CitizenResidence
            {
                Home = Entity.Null,
                Work = Entity.Null,
            });
            ecb.AddComponent<UnassignedTag>(e);   // 주거 대기 큐
            ecb.AddComponent<JobSeekerTag>(e);    // 고용 대기 큐(분리 — 2026-07-06)

            // 동적: 상태
            ecb.AddComponent(e, new CitizenState
            {
                Activity        = CitizenActivity.Idle,
                CurrentBuilding = Entity.Null,
                ActionEndTime   = 0.0,
            });

            // 소유: SharedComponent (플레이어 LocalId별 청크 분리 — 모든 소유물 공통 골격)
            ecb.AddSharedComponent(e, new OwnerShared(req.LocalId));

            // 서비스 탐색 결과 슬롯(초기 비어있음). ServiceSearchSystem이 채운다.
            ecb.AddComponent(e, ServiceTarget.None);
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
    }
}
