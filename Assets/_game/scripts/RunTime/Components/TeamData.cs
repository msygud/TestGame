using System;
using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    // ══════════════════════════════════════════════════════════════════
    //  UserPlayer  — 싱글톤
    //  플레이어가 어느 슬롯/팀인지 기록.
    // ══════════════════════════════════════════════════════════════════
    public struct UserPlayer : IComponentData
    {
        public int LocalID;  // 슬롯 인덱스 (0~7)
        public int TeamID;   // 팀 그룹 번호
    }

    // ══════════════════════════════════════════════════════════════════
    // ══════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════
    //  TeamMask  — 비트마스크 (bit 0~7 = 슬롯, bit 14~15 = 플래그)
    // ══════════════════════════════════════════════════════════════════
    [Flags]
    public enum TeamMask : ushort
    {
        None    = 0,
        Team1   = 1 << 0,
        Team2   = 1 << 1,
        Team3   = 1 << 2,
        Team4   = 1 << 3,
        Team5   = 1 << 4,
        Team6   = 1 << 5,
        Team7   = 1 << 6,
        Team8   = 1 << 7,

        IsPlayerTeam = 1 << 14,
        IsPlayer     = 1 << 15,
    }

    // ══════════════════════════════════════════════════════════════════
    //  TeamInfoData  — 팀 엔티티 1개당 붙는 컴포넌트
    // ══════════════════════════════════════════════════════════════════
    public struct TeamInfoData : IComponentData
    {
        // 플래그 비트를 제거하고 순수 슬롯 비트만 남기는 마스크
        private const ushort SlotBitsMask =
            (ushort)~(TeamMask.IsPlayerTeam | TeamMask.IsPlayer);

        public int      TeamID;      // 팀 그룹 번호 (같은 TeamID = 아군)
        public int      LocalID;     // 슬롯 인덱스 (0~7)
        public TeamMask teamMask;    // 자신의 슬롯 비트 + IsPlayer/IsPlayerTeam 플래그
        public TeamMask EnemyTeam;
        public TeamMask AllyTeam;
        public TeamMask NeutralTeam;

        // ── 내부 헬퍼 ─────────────────────────────────────────────────
        private static TeamMask SlotBitsOnly(TeamMask m)
            => (TeamMask)((ushort)m & SlotBitsMask);

        // ── 조회 ──────────────────────────────────────────────────────
        public bool IsPlayerTeam() => (teamMask & TeamMask.IsPlayerTeam) != 0;
        public bool IsPlayer()     => (teamMask & TeamMask.IsPlayer)     != 0;

        public bool IsSameTeam(TeamMask other)
            => (SlotBitsOnly(teamMask) & SlotBitsOnly(other)) != 0;

        public bool IsEnemy(TeamMask other)
            => (EnemyTeam   & SlotBitsOnly(other)) != 0;

        public bool IsAlly(TeamMask other)
            => (AllyTeam    & SlotBitsOnly(other)) != 0;

        public bool IsNeutral(TeamMask other)
            => (NeutralTeam & SlotBitsOnly(other)) != 0;

        // ── 세터 ──────────────────────────────────────────────────────
        public void SetPlayer(bool v)
        {
            if (v) teamMask |= TeamMask.IsPlayer;
            else   teamMask &= ~TeamMask.IsPlayer;
        }

        public void SetPlayerTeam(bool v)
        {
            if (v) teamMask |= TeamMask.IsPlayerTeam;
            else   teamMask &= ~TeamMask.IsPlayerTeam;
        }

        /// <summary>슬롯 비트를 localID 하나로 교체 (플래그 비트 유지).</summary>
        public void SetLocalID(int localId)
        {
            ushort flags   = (ushort)((ushort)teamMask & ~SlotBitsMask); // 플래그만 추출
            teamMask = (TeamMask)(flags | (ushort)(1 << localId));
        }

        public void SetEnemyTeams(TeamMask  m) => EnemyTeam   = m;
        public void SetAllyTeams(TeamMask   m) => AllyTeam    = m;
        public void SetNeutralTeams(TeamMask m) => NeutralTeam = m;

        public void SetAllyTeams(params int[]    indices) => AllyTeam    = Combine(indices);
        public void SetEnemyTeams(params int[]   indices) => EnemyTeam   = Combine(indices);
        public void SetNeutralTeams(params int[] indices) => NeutralTeam = Combine(indices);

        public void AddAllyTeam(int i)    => AllyTeam    |= (TeamMask)(1 << i);
        public void AddEnemyTeam(int i)   => EnemyTeam   |= (TeamMask)(1 << i);
        public void AddNeutralTeam(int i) => NeutralTeam |= (TeamMask)(1 << i);

        // ── 팩토리 ────────────────────────────────────────────────────
        public static TeamMask CreateTeamMask(
            int teamIndex,
            bool isPlayerTeam = false,
            bool isPlayer     = false)
        {
            TeamMask m = (TeamMask)(1 << teamIndex);
            if (isPlayerTeam) m |= TeamMask.IsPlayerTeam;
            if (isPlayer)     m |= TeamMask.IsPlayer;
            return m;
        }

        public static TeamInfoData CreateTeamInfo(
            int teamIndex,
            TeamMask enemyTeams,
            TeamMask allyTeams,
            TeamMask neutralTeams,
            bool isPlayerTeam = false,
            bool isPlayer     = false)
        {
            return new TeamInfoData
            {
                TeamID      = teamIndex,
                LocalID     = teamIndex,
                teamMask    = CreateTeamMask(teamIndex, isPlayerTeam, isPlayer),
                EnemyTeam   = enemyTeams,
                AllyTeam    = allyTeams,
                NeutralTeam = neutralTeams,
            };
        }

        // ── 유틸 ──────────────────────────────────────────────────────
        private static TeamMask Combine(int[] indices)
        {
            TeamMask m = 0;
            foreach (var i in indices) m |= (TeamMask)(1 << i);
            return m;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  TeamUnitCountData
    // ══════════════════════════════════════════════════════════════════
    public struct TeamUnitCountData : IComponentData
    {
        public int UnitCount;
    }

    // ══════════════════════════════════════════════════════════════════
    //  VisibleStateData  — 전장의 안개 설정 싱글톤
    // ══════════════════════════════════════════════════════════════════
    public struct VisibleStateData : IComponentData
    {
        public enum State : byte
        {
            /// <summary>전장의 안개 ON — 시야 범위 내만 표시.</summary>
            RealVisible = 0,
            /// <summary>전장의 안개 OFF — 전맵 공개 (관전·테스트).</summary>
            FullVisible  = 1,
        }

        public State Visible;
    }
}
