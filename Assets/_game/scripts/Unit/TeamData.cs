using System;
using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    public class TeamData
    {

    }
    //싱글톤
    public struct UserPlayer : IComponentData
    {
        public int LocalID;
        public int TeamID;
    }
    //팀매니저용 각 팀
    
    public struct LocalPlayerTag : IComponentData { }
    [Flags]
    public enum TeamMask : ushort
    {
        Team1 = 1 << 0,
        Team2 = 1 << 1,
        Team3 = 1 << 2,
        Team4 = 1 << 3,
        Team5 = 1 << 4,
        Team6 = 1 << 5,
        Team7 = 1 << 6,
        Team8 = 1 << 7,

        IsPlayerTeam = 1 << 14,
        IsPlayer = 1 << 15
    }

    public struct TeamInfoData : IComponentData
    {
        const ushort RemovePlayernPlayerTeamMask = (ushort)(TeamMask.IsPlayerTeam | TeamMask.IsPlayer);

        public int TeamID;
        public int LocalID;
        public TeamMask teamMask;
        public TeamMask EnemyTeam;
        public TeamMask AllyTeam;
        public TeamMask NeutralTeam;

        private TeamMask GetLocalTeamMask(TeamMask team)
        {
            return (TeamMask)((ushort)team & ~RemovePlayernPlayerTeamMask);
        }
        public int GetLocalID()
        {
            TeamMask baseTeam = GetLocalTeamMask(teamMask);
            for (int i = 0; i < 8; i++)
            {
                if ((baseTeam & (TeamMask)(1 << i)) != 0)
                    return i;
            }
            return -1; // No team assigned
        }
        
        public bool IsSameTeam(TeamMask otherTeam)
        {
            return (GetLocalTeamMask(teamMask) & ~GetLocalTeamMask(otherTeam)) == 0;
        }
        public bool IsDifferentTeam(TeamMask otherTeam)
        {
            return (GetLocalTeamMask(teamMask) & GetLocalTeamMask(otherTeam)) == 0;
        }
        public bool IsInTeam(TeamMask otherTeam)
        {
            return (GetLocalTeamMask(teamMask) & GetLocalTeamMask(otherTeam)) != 0;
        }
        public bool IsEnemy(TeamMask otherTeam)
        {
            return (EnemyTeam & GetLocalTeamMask(otherTeam)) != 0;
        }
        public bool IsAlly(TeamMask otherTeam)
        {
            return (AllyTeam & GetLocalTeamMask(otherTeam)) != 0;
        }
        public bool IsNeutral(TeamMask otherTeam)
        {
            return (NeutralTeam & GetLocalTeamMask(otherTeam)) != 0;
        }
        public bool IsPlayerTeam()
        {
            return (teamMask & TeamMask.IsPlayerTeam) != 0;
        }
        public bool IsPlayer()
        {
            return (teamMask & TeamMask.IsPlayer) != 0;
        }
        public void SetPlayerTeam(bool isPlayerTeam)
        {
            if (isPlayerTeam)
                teamMask |= TeamMask.IsPlayerTeam;
            else
                teamMask &= ~TeamMask.IsPlayerTeam;
        }
        public void SetPlayer(bool isPlayer)
        {
            if (isPlayer)
                teamMask |= TeamMask.IsPlayer;
            else
                teamMask &= ~TeamMask.IsPlayer;
        }
        public void SetLocalID(int localid)
        {
            teamMask = (TeamMask)((ushort)teamMask & RemovePlayernPlayerTeamMask | (1 << localid));
        }
        public void SetEnemyTeams(TeamMask enemyTeams)
        {
            EnemyTeam = enemyTeams;
        }
        public void SetAllyTeams(TeamMask allyTeams)
        {
            AllyTeam = allyTeams;
        }
        public void SetNeutralTeams(TeamMask neutralTeams)
        {
            NeutralTeam = neutralTeams;
        }
        public void SetAllyTeams(params int[] allyTeamIndices)
        {
            AllyTeam = 0;
            foreach (var index in allyTeamIndices)
            {
                AllyTeam |= (TeamMask)(1 << index);
            }
        }
        public void SetEnemyTeams(params int[] enemyTeamIndices)
        {
            EnemyTeam = 0;
            foreach (var index in enemyTeamIndices)
            {
                EnemyTeam |= (TeamMask)(1 << index);
            }
        }
        public void SetNeutralTeams(params int[] neutralTeamIndices)
        {
            NeutralTeam = 0;
            foreach (var index in neutralTeamIndices)
            {
                NeutralTeam |= (TeamMask)(1 << index);
            }
        }
        public void AddAllyTeam(int teamIndex)
        {
            AllyTeam |= (TeamMask)(1 << teamIndex);
        }
        public void AddEnemyTeam(int teamIndex)
        {
            EnemyTeam |= (TeamMask)(1 << teamIndex);
        }
        public void AddNeutralTeam(int teamIndex)
        {
            NeutralTeam |= (TeamMask)(1 << teamIndex);
        }

        public static TeamMask CreateTeamMask(int teamIndex, bool isPlayerTeam = false, bool isPlayer = false)
        {
            TeamMask mask = (TeamMask)(1 << teamIndex);
            if (isPlayerTeam)
                mask |= TeamMask.IsPlayerTeam;
            if (isPlayer)
                mask |= TeamMask.IsPlayer;
            return mask;
        }
        public static TeamInfoData CreateTeamInfo(int teamIndex, TeamMask enemyTeams, TeamMask allyTeams, TeamMask neutralTeams, bool isPlayerTeam = false, bool isPlayer = false)
        {
            return new TeamInfoData
            {
                teamMask = CreateTeamMask(teamIndex, isPlayerTeam, isPlayer),
                EnemyTeam = enemyTeams,
                AllyTeam = allyTeams,
                NeutralTeam = neutralTeams
            };
        }
        public void CopyFrom(TeamInfoData other)
        {
            teamMask = other.teamMask;
            EnemyTeam = other.EnemyTeam;
            AllyTeam = other.AllyTeam;
            NeutralTeam = other.NeutralTeam;
        }
        public void CopyTo(ref TeamInfoData other)
        {
            other.teamMask = teamMask;
            other.EnemyTeam = EnemyTeam;
            other.AllyTeam = AllyTeam;
            other.NeutralTeam = NeutralTeam;
        }
    }

    public struct TeamUnitCountData : IComponentData
    {
        public int UnitCount;
    }
}
