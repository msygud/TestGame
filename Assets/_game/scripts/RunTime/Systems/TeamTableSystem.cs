using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TeamTableSystem — LocalId→팀 매핑 싱글톤(TeamTable) 유지
    // ──────────────────────────────────────────────────────────────────────────
    //  빌드 게이트(TerritoryOps.InEnemyTerritory)는 TerritoryLayer의 '팀 id'와
    //  '내 팀'을 비교한다. 그러려면 LocalId를 팀으로 풀 매핑이 필요한데, 그 원천은
    //  PlayerInfluenceElement.Team 버퍼(인덱스=LocalId)다. 이 시스템이 매 프레임 버퍼를
    //  읽어 TeamTable 싱글톤으로 미러링한다(버퍼 없으면 Identity=team:localId).
    //
    //  TerritorySystem도 같은 버퍼에서 pTeam을 만들지만 ~1초 주기라, 게이트가 매 프레임
    //  최신 매핑을 읽도록 별도로 가볍게(8칸) 유지한다. SimulationSystemGroup 선두에 둬서
    //  같은 프레임의 도로/건물/AI 게이트보다 먼저 갱신된다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct TeamTableSystem : ISystem
    {
        const int MP = StampLayers.MaxPlayers;   // 8

        public void OnCreate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<TeamTable>()) return;
            var e = state.EntityManager.CreateEntity(typeof(TeamTable));
            state.EntityManager.SetComponentData(e, TeamTable.Identity);
        }

        public void OnUpdate(ref SystemState state)
        {
            var table = TeamTable.Identity;

            if (SystemAPI.TryGetSingletonEntity<PlayerInfluenceConfig>(out var cfgE)
                && state.EntityManager.HasBuffer<PlayerInfluenceElement>(cfgE))
            {
                var buf = state.EntityManager.GetBuffer<PlayerInfluenceElement>(cfgE);
                for (int i = 0; i < MP && i < buf.Length; i++)
                {
                    int t    = buf[i].Team;
                    int team = (uint)t < MP ? t : i;   // 범위 밖 = 자기 자신
                    if (i < 4) table.Lo[i] = team;
                    else       table.Hi[i - 4] = team;
                }
            }

            SystemAPI.SetSingleton(table);
        }
    }
}
