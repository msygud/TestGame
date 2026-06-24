using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Game.Unit;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  NetworkRepairSystem — 단절된 AI 도로 섬을 베이스에 재연결 (공평성 복구)
    //
    //  구역 해체(RazeSystem → ZoneOps.ReleaseZone)로 중간 도로가 제거되면, 그 너머
    //  살아있는 구역이 베이스에서 단절된 '섬'이 될 수 있다(물류·시민 BFS 사망).
    //  사람은 손으로 다시 잇지만 AI는 자동화해야 공평 → 이 시스템이 라우터로 다리를 놓는다.
    //
    //  처리: NetworkRepairRequest{Owner}를 받아 팀별로
    //    ① 베이스 블록(CityGrid.Anchor) 근처 팀 도로를 시드로 BFS → baseSet(베이스 연결 도로).
    //    ② 나머지 팀 도로를 4-인접 컴포넌트로 묶어 '섬' 도출.
    //    ③ 섬마다 BlockOps.FindReconnectPath(baseSet→섬)로 빈 평지 다리 경로를 찾아
    //       그린-방향 PlaceRoadCommand로 발행(양 끝 OR 병합 → 베이스·섬에 교차로로 붙음).
    //  적 영토/물/단차로 막혀 못 이으면 그냥 둠 = 사람도 못 잇는 상황이라 공평.
    //
    //  시스템 순서: RoadSystem 이후(도로 제거가 반영된 RoadLayer를 봐야 단절을 정확히 판정).
    //    발행한 다리 PlaceRoadCommand는 다음 프레임 RoadSystem이 깐다(1프레임 지연, 허용).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RoadSystem))]
    public partial struct NetworkRepairSystem : ISystem
    {
        const int MaxExplore = 8192;   // 다리 BFS 방문 상한
        const int BridgeCap  = 32;     // 한 번에 놓는 다리 수 상한(폭주 방지)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<CellTypeLookup>();
            state.RequireForUpdate<NetworkRepairRequest>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers         = SystemAPI.GetSingleton<GridLayers>();
            var cellTypeLookup = SystemAPI.GetSingleton<CellTypeLookup>();

            var ecb    = new EntityCommandBuffer(Allocator.Temp);
            var owners = new NativeHashSet<int>(8, Allocator.Temp);

            // 요청 수집(소비) — 같은 팀 중복 제거.
            foreach (var (reqRO, e) in SystemAPI.Query<RefRO<NetworkRepairRequest>>().WithEntityAccess())
            {
                owners.Add(reqRO.ValueRO.OwnerLocalId);
                ecb.DestroyEntity(e);
            }

            // 팀(CityGrid)별 재연결 — 플레이어는 수동 관리라 제외.
            foreach (var (teamRO, gridRO) in
                     SystemAPI.Query<RefRO<TeamInfoData>, RefRO<CityGrid>>())
            {
                var team = teamRO.ValueRO;
                if (team.IsPlayer()) continue;
                if (!owners.Contains(team.LocalID)) continue;
                RepairTeam(in layers, in cellTypeLookup, gridRO.ValueRO, team.LocalID, ref ecb);
            }

            owners.Dispose();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        static void RepairTeam(
            in GridLayers layers, in CellTypeLookup cellTypeLookup,
            in CityGrid grid, int owner, ref EntityCommandBuffer ecb)
        {
            int Road = math.max(1, grid.Road);

            // ① baseSet — 베이스 블록 박스 안의 팀 도로를 시드로 BFS(4-인접 같은 소유자).
            var baseSet = new NativeHashSet<int2>(256, Allocator.Temp);
            var queue   = new NativeList<int2>(256, Allocator.Temp);

            int2 lo = grid.Anchor;
            int2 hi = grid.Anchor + new int2(grid.Block + Road, grid.Block + Road);
            foreach (var kv in layers.RoadLayer)
            {
                if (kv.Value.OwnerLocalId != owner) continue;
                int2 c = kv.Key;
                if (c.x < lo.x || c.x > hi.x || c.y < lo.y || c.y > hi.y) continue;
                if (baseSet.Add(c)) queue.Add(c);
            }
            FloodTeamRoads(in layers, owner, ref baseSet, ref queue);

            if (baseSet.IsEmpty)   // 베이스 도로가 없음(베이스 파괴 등) → 루트 불명, 재연결 불가.
            {
                baseSet.Dispose(); queue.Dispose();
                return;
            }

            // ② 나머지 팀 도로 = 단절 후보. 4-인접 컴포넌트로 묶어 섬 도출.
            var remaining = new NativeHashSet<int2>(256, Allocator.Temp);
            foreach (var kv in layers.RoadLayer)
                if (kv.Value.OwnerLocalId == owner && !baseSet.Contains(kv.Key))
                    remaining.Add(kv.Key);

            var path = new NativeList<int2>(64, Allocator.Temp);
            int bridges = 0;
            while (!remaining.IsEmpty && bridges < BridgeCap)
            {
                int2 start = default;
                foreach (var c in remaining) { start = c; break; }

                // 섬 컴포넌트 BFS(remaining에서 떼어냄).
                var island = new NativeHashSet<int2>(128, Allocator.Temp);
                var iq     = new NativeList<int2>(128, Allocator.Temp);
                island.Add(start); iq.Add(start); remaining.Remove(start);
                int head = 0;
                while (head < iq.Length)
                {
                    int2 cur = iq[head++];
                    for (int d = 0; d < 4; d++)
                    {
                        int2 nb = cur + RoadDirOps.Offsets[d];
                        if (!remaining.Contains(nb)) continue;
                        island.Add(nb); remaining.Remove(nb); iq.Add(nb);
                    }
                }

                // ③ 다리 — baseSet → island. 찾으면 그린-방향으로 발행(양 끝 OR 병합).
                if (BlockOps.FindReconnectPath(
                        in layers.RoadLayer, in layers.OccupancyLayer, in layers.TerrainLayer,
                        in layers.ResourceLayer, in cellTypeLookup,
                        in baseSet, in island, MaxExplore, ref path))
                {
                    EmitGreenPath(in path, owner, grid.FactionId, ref ecb);
                    bridges++;
                }
                else
                {
                    Debug.Log($"[NetRepair] team{owner}: 단절 섬({island.Count}셀) 재연결 경로 없음(막힘)");
                }

                island.Dispose(); iq.Dispose();
            }

            baseSet.Dispose(); queue.Dispose(); remaining.Dispose(); path.Dispose();
        }

        // 시드 집합에서 4-인접 같은 소유자 도로로 flood-fill(이미 시드가 큐에 담긴 상태로 진입).
        static void FloodTeamRoads(
            in GridLayers layers, int owner, ref NativeHashSet<int2> set, ref NativeList<int2> queue)
        {
            int head = 0;
            while (head < queue.Length)
            {
                int2 cur = queue[head++];
                for (int d = 0; d < 4; d++)
                {
                    int2 nb = cur + RoadDirOps.Offsets[d];
                    if (set.Contains(nb)) continue;
                    if (!layers.RoadLayer.TryGetValue(nb, out var rc) || rc.OwnerLocalId != owner) continue;
                    set.Add(nb); queue.Add(nb);
                }
            }
        }

        // 열린 폴리라인 → 셀별 그린-방향 비트(경로 이전/다음 이웃을 향함)로 PlaceRoadCommand 발행.
        //   양 끝(기존 베이스/섬 도로)도 발행 → RoadSystem이 OR 병합(교차/T 승격)으로 연결.
        static void EmitGreenPath(in NativeList<int2> path, int owner, int faction, ref EntityCommandBuffer ecb)
        {
            for (int i = 0; i < path.Length; i++)
            {
                RoadDir bits = RoadDir.None;
                if (i > 0)               bits |= DirBit(path[i], path[i - 1]);
                if (i < path.Length - 1) bits |= DirBit(path[i], path[i + 1]);
                if (bits == RoadDir.None) continue;

                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new PlaceRoadCommand
                {
                    Cell = path[i], OwnerLocalId = owner, LaneCount = 2,
                    FactionId = faction, Size = 1,
                    Axis = RoadPlacedAxis.Any, Directions = bits,
                });
            }
        }

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
