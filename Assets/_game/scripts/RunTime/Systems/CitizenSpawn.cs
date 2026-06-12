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
    //    5. CitizenOwner(SharedComponent) 부착 — 플레이어 LocalId별 청크 분리
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
        public int     LocalId;       // 0~7. 소유 플레이어 슬롯. CitizenOwner 부착에 사용.
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
                Pursuing   = NeedType.None,
            });

            // 욕구 컴포넌트 — 개별 부착(부정 방향, Level 0=만족에서 시작).
            // A-1: Hunger 하나로 골격. 다른 욕구/팩션 조합은 이후 단계에서 추가.
            // 증가율·임계치는 기본값(추후 능력치/밸런싱 조정).
            ecb.AddComponent(e, new Hunger
            {
                Level     = 0f,
                Rate      = 0.010f,
                Threshold = 0.6f,
            });

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

            // 소유: SharedComponent (플레이어 LocalId별 청크 분리)
            ecb.AddSharedComponent(e, new CitizenOwner(req.LocalId));

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
