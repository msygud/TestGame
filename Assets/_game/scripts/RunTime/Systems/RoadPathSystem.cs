using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadPathSystem — 특수 목적 도로 연장 (목적 무관 재사용 메커니즘)
    //
    //  RoadPathRequest를 받아 팀 도로 네트워크에서 Target(또는 그 인접)까지
    //  그린-방향(연속) 도로 경로를 BlockOps.FindRoadPath(BFS)로 찾아 PlaceRoadCommand로
    //  발행한다. "어디로·언제" 결정은 발행자의 몫(항구/자원 등 목적 로직 — 미구현).
    //
    //  경로 = [소스 도로 셀, c1, …, goal]. 소스(기존 도로)에도 그린 비트를 발행해
    //    RoadSystem이 OR 병합 → 기존 네트워크에 교차/이음매로 연결(상호 비트 성립).
    //  유저 드래그·AI 링과 동일한 그린-방향 모델 → BFS·물류·비주얼 자동 호환.
    //
    //  시스템 순서: RoadSystem 전(도로 먼저 깔린 뒤 RoadSystem이 셀 등록·병합).
    //  현재 1×1 도로 전용(그린 모델). 멀티셀은 추후.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct RoadPathSystem : ISystem
    {
        // BFS 방문 셀 상한(맵이 커도 폭주 방지). 저빈도 이벤트라 넉넉히.
        const int MaxExplore = 8192;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<CellTypeLookup>();
            state.RequireForUpdate<RoadPathRequest>();   // 요청 있을 때만 가동
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers         = SystemAPI.GetSingleton<GridLayers>();
            var cellTypeLookup = SystemAPI.GetSingleton<CellTypeLookup>();

            var ecb  = new EntityCommandBuffer(Allocator.Temp);
            var path = new NativeList<int2>(64, Allocator.Temp);

            // 기존 지선 등록 목록 — 같은 (owner, target) 중복 등록 방지.
            var knownSpurs = new NativeHashSet<int3>(16, Allocator.Temp);
            foreach (var spur in SystemAPI.Query<RefRO<RoadSpur>>())
                knownSpurs.Add(new int3(spur.ValueRO.Target, spur.ValueRO.OwnerLocalId));

            foreach (var (reqRO, e) in SystemAPI.Query<RefRO<RoadPathRequest>>().WithEntityAccess())
            {
                var req = reqRO.ValueRO;

                bool ok = BlockOps.FindRoadPath(
                    in layers.RoadLayer, in layers.OccupancyLayer, in layers.TerrainLayer,
                    in layers.ResourceLayer, in cellTypeLookup,
                    req.OwnerLocalId, req.Target, req.StopAdjacent != 0, MaxExplore,
                    ref path);

                if (ok && HasNewCell(in path, in layers.RoadLayer))
                    EmitDrawnPath(in path, req.OwnerLocalId, req.FactionId, ref ecb);
                else if (!ok)
                    // '이미 연결'(정상 멱등)은 무로그 — 진짜 경로 실패만 관찰(2026-07-12 로그 정리).
                    Debug.Log($"[RoadPath] team{req.OwnerLocalId}: Target={req.Target} 경로 없음");

                // 활성 지선 등록(성공+RegisterSpur 요청 시, 중복 방지) — janitor가 보호·자가수리에 사용.
                //   일회성 부설(입구 복구 등)은 등록 안 함 — 대상 소멸 후에도 영구 재부설되는 것 방지.
                if (ok && req.RegisterSpur != 0 && knownSpurs.Add(new int3(req.Target, req.OwnerLocalId)))
                {
                    var se = ecb.CreateEntity();
                    ecb.AddComponent(se, new RoadSpur
                    {
                        Target = req.Target, OwnerLocalId = req.OwnerLocalId,
                        FactionId = req.FactionId, StopAdjacent = req.StopAdjacent,
                    });
                }

                ecb.DestroyEntity(e);   // 단발성 요청 소비
            }
            knownSpurs.Dispose();

            path.Dispose();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        // 경로에 새로 깔 셀이 하나라도 있나(전부 기존 도로면 이미 연결 → 발행 불필요).
        static bool HasNewCell(in NativeList<int2> path, in NativeHashMap<int2, RoadCell> roadLayer)
        {
            for (int i = 0; i < path.Length; i++)
                if (!roadLayer.ContainsKey(path[i])) return true;
            return false;
        }

        // 열린 폴리라인 → 셀별 그린-방향 비트(경로 이전/다음 이웃을 향함). 발행.
        //   소스(i=0)도 발행 → 기존 도로 셀에 OR 병합(교차/T 승격, 네트워크 연결).
        // internal — AiRoadJanitorSystem(고립 섬 재연결)도 같은 그린-경로 발행을 재사용.
        internal static void EmitDrawnPath(
            in NativeList<int2> path, int owner, int faction, ref EntityCommandBuffer ecb)
        {
            for (int i = 0; i < path.Length; i++)
            {
                RoadDir bits = RoadDir.None;
                if (i > 0)               bits |= DirBit(path[i], path[i - 1]);
                if (i < path.Length - 1) bits |= DirBit(path[i], path[i + 1]);
                if (bits == RoadDir.None) continue;   // 고립 셀(경로 1칸)

                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new PlaceRoadCommand
                {
                    Cell = path[i], OwnerLocalId = owner, LaneCount = 2,
                    FactionId = faction, Size = 1,
                    Axis = RoadPlacedAxis.Any, Directions = bits,
                });
            }
        }

        // 인접 두 셀 from→to 방향 비트.
        static RoadDir DirBit(int2 from, int2 to)
        {
            int2 d = to - from;
            if (d.x ==  0 && d.y ==  1) return RoadDir.N;
            if (d.x ==  1 && d.y ==  0) return RoadDir.E;
            if (d.x ==  0 && d.y == -1) return RoadDir.S;
            if (d.x == -1 && d.y ==  0) return RoadDir.W;
            return RoadDir.None;
        }
    }
}
