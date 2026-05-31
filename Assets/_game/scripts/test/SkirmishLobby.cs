using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Game.Unit;
using CitySim;

namespace Game
{
    /// <summary>
    /// 스커미시 로비.
    ///
    /// Inspector에서 슬롯(TeamSlot)을 구성하면
    /// 각 슬롯의 Cell이 곧 그 팀의 스타트포인트가 된다.
    ///
    /// 흐름:
    ///   슬롯 구성 → BuildTeamInfos() → CreateEntities()
    ///   → ECS: TeamInfoData + TeamStartPoint (슬롯당 1 엔티티)
    ///           UserPlayer 싱글톤 (플레이어 슬롯이 있을 때)
    ///           VisibleStateData 싱글톤
    /// </summary>
    public class SkirmishLobby : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────
        [SerializeField] private List<TeamSlot> _slots = new();
        [Tooltip("true 이면 전장의 안개 없이 전맵 공개 (관전·테스트용)")]
        [SerializeField] private bool _allMapClear;

        // ── 런타임 결과 (GameStart 이후 읽기용) ───────────────────────────
        public int  UserSlotIndex { get; private set; } = -1;
        public int  UserTeamID   { get; private set; } = -1;
        public bool HasUserPlayer => UserSlotIndex >= 0;

        // ── Private ───────────────────────────────────────────────────────
        private bool  _initialized;
        private World _world;

        // ══════════════════════════════════════════════════════════════════
        //  Unity lifecycle
        // ══════════════════════════════════════════════════════════════════

        private IEnumerator Start()
        {
            while (World.DefaultGameObjectInjectionWorld == null)
                yield return null;

            _world       = World.DefaultGameObjectInjectionWorld;
            _initialized = true;
            GameStart();
        }

        // ══════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 로비 설정이 완료된 뒤 ECS 엔티티를 생성한다.
        /// _initialized 전에는 무시된다.
        /// </summary>
        public void GameStart()
        {
            if (!_initialized) return;

            var infos = BuildTeamInfos();
            CreateEntities(infos);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Step 1 ─ 슬롯 → TeamBuildInfo 계산
        // ══════════════════════════════════════════════════════════════════

        private List<TeamBuildInfo> BuildTeamInfos()
        {
            // ── 유저 플레이어 슬롯 탐색 ───────────────────────────────────
            UserSlotIndex = -1;
            UserTeamID    = -1;

            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                if (s.IsOpen && s.IsPlayer)
                {
                    UserSlotIndex = i;
                    UserTeamID    = s.TeamID;
                    break;
                }
            }

            if (UserSlotIndex < 0)
                Debug.LogWarning("[SkirmishLobby] 플레이어 슬롯 없음 (관전·테스트 모드).");

            // ── 열린 슬롯 수집 ────────────────────────────────────────────
            var open = new List<(int idx, TeamSlot slot)>(_slots.Count);
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].IsOpen)
                    open.Add((i, _slots[i]));

            // ── Ally / Enemy 마스크 계산 ──────────────────────────────────
            var result = new List<TeamBuildInfo>(open.Count);

            foreach (var (idx, slot) in open)
            {
                TeamMask ally   = 0;
                TeamMask enemy  = 0;
                // neutral: 추후 필요 시 슬롯에 Stance 필드 추가

                foreach (var (otherIdx, other) in open)
                {
                    if (otherIdx == idx) continue; // 자기 자신 제외

                    var bit = (TeamMask)(1 << otherIdx);
                    if (other.TeamID == slot.TeamID)
                        ally  |= bit;
                    else
                        enemy |= bit;
                }

                bool isPlayerTeam = (UserTeamID >= 0) && (slot.TeamID == UserTeamID);
                bool isPlayer     = slot.IsPlayer;

                // TeamInfoData 빌드
                var data = TeamInfoData.CreateTeamInfo(
                    teamIndex    : idx,
                    enemyTeams   : enemy,
                    allyTeams    : ally,
                    neutralTeams : 0,
                    isPlayerTeam : isPlayerTeam,
                    isPlayer     : isPlayer);

                data.TeamID  = slot.TeamID;
                data.LocalID = idx;

                result.Add(new TeamBuildInfo
                {
                    SlotIndex    = idx,
                    Data         = data,
                    Cell         = slot.Cell,   // ← 슬롯 위치 = 스타트포인트
                    IsPlayer     = isPlayer,
                    IsPlayerTeam = isPlayerTeam,
                });
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Step 2 ─ ECS 엔티티 생성
        // ══════════════════════════════════════════════════════════════════

        private void CreateEntities(List<TeamBuildInfo> infos)
        {
            EntityManager em = _world.EntityManager;

            // ── UserPlayer 싱글톤 ──────────────────────────────────────────
            if (HasUserPlayer)
            {
                var e = em.CreateEntity();
                em.SetName(e, "UserPlayer");
                em.AddComponentData(e, new UserPlayer
                {
                    LocalID = UserSlotIndex,
                    TeamID  = UserTeamID,
                });
            }

            // ── 팀 엔티티 (슬롯당 1개) ────────────────────────────────────
            foreach (var info in infos)
            {
                var e = em.CreateEntity();
                em.SetName(e, $"Team_{info.SlotIndex}" + (info.IsPlayer ? "_Player" : "_AI"));

                em.AddComponentData(e, info.Data);
                em.AddComponentData(e, new TeamUnitCountData { UnitCount = 0 });

                // 스타트포인트: 슬롯 Cell이 곧 시작 위치
                em.AddComponentData(e, new TeamStartPoint
                {
                    Cell      = new int2(info.Cell.x, info.Cell.y),
                    TeamIndex = info.SlotIndex,
                });

            }

            // ── 가시성 싱글톤 ──────────────────────────────────────────────
            var visEntity = em.CreateEntity();
            em.SetName(visEntity, "VisibleState");
            em.AddComponentData(visEntity, new VisibleStateData
            {
                Visible = (!HasUserPlayer || _allMapClear)
                    ? VisibleStateData.State.FullVisible
                    : VisibleStateData.State.RealVisible,
            });
        }

        // ══════════════════════════════════════════════════════════════════
        //  Inner types
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// BuildTeamInfos → CreateEntities 사이의 중간 전달 데이터.
        /// </summary>
        private struct TeamBuildInfo
        {
            public int          SlotIndex;
            public TeamInfoData Data;
            public Vector2Int   Cell;        // 스타트포인트 셀
            public bool         IsPlayer;
            public bool         IsPlayerTeam;
        }

        // ── Inspector 슬롯 ────────────────────────────────────────────────

        /// <summary>
        /// 로비에 배치하는 팀 슬롯 1개.
        /// Cell 값이 이 팀의 스타트포인트 그리드 좌표.
        /// </summary>
        [Serializable]
        public sealed class TeamSlot
        {
            [Tooltip("이 슬롯 활성 여부. false = 빈 슬롯(참가 없음).")]
            public bool IsOpen = true;

            [Tooltip("팀 그룹 번호 (0~7). 같은 번호끼리 아군.")]
            [Range(0, 7)]
            public int TeamID;

            [Tooltip("이 슬롯을 플레이어가 사용하는가. false = AI.")]
            public bool IsPlayer;

            [Tooltip("이 슬롯의 스타트포인트 그리드 셀.\n" +
                     "MapEditorWindow의 StartPoint 모드에서 찍은 위치와 맞춰야 한다.")]
            public Vector2Int Cell;
        }
    }
}
