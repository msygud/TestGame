using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CitizenWalkerSpawnSystem — 시민 보행 비주얼 스폰
    //
    //  CitizenWalkerRequest(출발/귀가)를 소비해 보행자 비주얼 엔티티를 스폰한다.
    //  CarrierSpawnSystem과 동일 구조 — 이동·소멸은 Carrier 인프라를 재사용:
    //    1. CivilianBFS로 From → To 경로 계산(자기 도로망).
    //    2. 경로 없으면 요청만 삭제(도로 미연결 — 논리 이동은 어차피 타이머라 무영향).
    //    3. 경로 있으면 CitizenVisualPrefabSingleton 프리팹 인스턴스화 +
    //       CarrierTag/CarrierState/CarrierPathCell → CarrierMoveSystem이 이동·소멸.
    //
    //  순수 비주얼 — 시민 엔티티(논리)와 완전 분리, 시뮬레이션 영향 없음.
    //  ※ 보행 속도(CarrierMoveSystem.Speed, 현실초 기준)와 논리 도착 타이머(게임초)는
    //    독립 — 고배속에선 비주얼이 늦게 도착할 수 있음(코스메틱, 수용).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenMovementSystem))]
    // 변환 그룹보다 먼저 — 스폰 프레임 안에 LTW 계산(1프레임 원점 플래시 방지).
    [UpdateBefore(typeof(Unity.Transforms.TransformSystemGroup))]
    public partial struct CitizenWalkerSpawnSystem : ISystem
    {
        // 스파이크 가드(2026-07-06, 실측 버벅 대응): 스폰은 BFS+Instantiate라 버스트에 취약.
        //   · 프레임 버짓 — 초과 요청은 이월 상한까지 다음 프레임으로(시각 지연만).
        //   · 동시 보행자 상한 — 초과분은 비주얼 생략(드롭). 순수 코스메틱이라 무해.
        //   · 이월 상한(과압 드롭, 120x 실측 대응) — 고배속에선 식사가 현실초당 배속만큼
        //     몰려 요청 유입이 처리량을 수십 배 초과(큐 무한 적체·메모리 증가·시스템 최상위).
        //     이월분이 상한을 넘으면 나머지는 그 자리에서 소비(비주얼 생략).
        const int MaxSpawnsPerFrame   = 12;
        const int MaxWalkersAlive     = 200;
        const int MaxPendingRequests  = 64;

        EntityQuery _aliveQ;   // 살아있는 보행자/운반자 수 (CarrierTag 공유)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenWalkerRequest>();   // 요청 있을 때만
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GridSettings>();
            _aliveQ = state.GetEntityQuery(ComponentType.ReadOnly<CarrierTag>());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 프리팹 싱글톤이 없거나 비어 있으면 요청만 소비(비주얼 생략 — 누적 방지).
            Entity prefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<CitizenVisualPrefabSingleton>(out var vp))
                prefab = vp.Prefab;

            var layers       = SystemAPI.GetSingleton<GridLayers>();
            var gridSettings = SystemAPI.GetSingleton<GridSettings>();
            var ecb          = new EntityCommandBuffer(Allocator.Temp);

            int alive  = _aliveQ.CalculateEntityCount();
            int budget = MaxSpawnsPerFrame;
            int kept   = 0;

            foreach (var (req, reqEntity) in
                     SystemAPI.Query<RefRO<CitizenWalkerRequest>>().WithEntityAccess())
            {
                // 버짓 소진 후: 이월 상한까지 보존, 초과분은 드롭(소비만 — 과압 방어).
                if (budget <= 0)
                {
                    if (kept < MaxPendingRequests) { kept++; continue; }   // 다음 프레임에
                    ecb.DestroyEntity(reqEntity);                          // 드롭(비주얼 생략)
                    continue;
                }

                if (prefab != Entity.Null && alive < MaxWalkersAlive)
                {
                    budget--;   // BFS+Instantiate 시도 1회 = 버짓 1 (성공 여부 무관)
                    var path = new NativeList<int2>(64, Allocator.Temp);

                    bool found = CivilianBFS.FindPath(
                        req.ValueRO.FromRoadCell,
                        req.ValueRO.ToRoadCell,
                        in layers.RoadLayer,
                        req.ValueRO.OwnerLocalId,
                        ref path,
                        Allocator.Temp);

                    if (found && path.Length > 0)
                    {
                        float3 startPos = gridSettings.CellCenter(path[0].x, path[0].y)
                                          + new float3(0, 0.5f, 0);

                        var walker = ecb.Instantiate(prefab);
                        ecb.SetComponent(walker, LocalTransform.FromPosition(startPos));
                        ecb.AddComponent<CarrierTag>(walker);
                        ecb.AddComponent(walker, new CarrierState { PathIndex = 0, MoveTimer = 0f });

                        var buf = ecb.AddBuffer<CarrierPathCell>(walker);
                        for (int i = 0; i < path.Length; i++)
                            buf.Add(new CarrierPathCell { Cell = path[i] });

                        alive++;   // 상한 판정에 이번 프레임 스폰분 포함
                    }

                    path.Dispose();
                }

                ecb.DestroyEntity(reqEntity);   // 단발성 요청 소비
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
