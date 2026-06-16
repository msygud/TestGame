using Unity.Collections;
using Unity.Entities;
using Game.Unit;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  TeamStartPointMergeSystem
    //
    //  맵 로드 시 MapLoadSystem.RegisterStartPoints가 만든
    //  고립된 TeamStartPoint 엔티티(TeamInfoData 없음, 좌표만 있음)를
    //  SkirmishLobby가 만든 팀 엔티티(TeamInfoData 있음, 좌표 없음)에
    //  TeamIndex 기준으로 병합한다.
    //
    //  좌표의 유일한 소스는 맵 데이터(MapEditorWindow의 StartPoint)다.
    //  SkirmishLobby의 TeamSlot.Cell은 더 이상 좌표를 만들지 않는다.
    //
    //  게임 시작 시 1회 실행 후 비활성화.
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoadSystem))]
    [UpdateBefore(typeof(FactionBaseSpawnSystem))]
    public partial struct TeamStartPointMergeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapLoaded>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<TeamStartPointMergeDone>()) return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            var orphanQuery = SystemAPI.QueryBuilder()
                .WithAll<TeamStartPoint>()
                .WithNone<TeamInfoData>()
                .Build();
            var orphanEntities = orphanQuery.ToEntityArray(Allocator.Temp);

            foreach (var spEntity in orphanEntities)
            {
                var sp        = SystemAPI.GetComponent<TeamStartPoint>(spEntity);
                int teamIndex = sp.TeamIndex;
                var cell      = sp.Cell;

                foreach (var (teamInfo, teamEntity) in
                         SystemAPI.Query<RefRO<TeamInfoData>>().WithEntityAccess())
                {
                    if (teamInfo.ValueRO.LocalID != teamIndex) continue;

                    ecb.AddComponent(teamEntity, new TeamStartPoint
                    {
                        Cell      = cell,
                        TeamIndex = teamIndex,
                    });
                    break;
                }

                ecb.DestroyEntity(spEntity);
            }

            orphanEntities.Dispose();

            var doneEntity = ecb.CreateEntity();
            ecb.SetName(doneEntity, "TeamStartPointMergeDone");
            ecb.AddComponent<TeamStartPointMergeDone>(doneEntity);

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            state.Enabled = false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  TeamStartPointMergeDone  (태그 컴포넌트)
    //  TeamStartPointMergeSystem 1회 실행 완료 마커.
    // ══════════════════════════════════════════════════════════════
    public struct TeamStartPointMergeDone : IComponentData { }
}
