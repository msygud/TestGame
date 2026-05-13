using Game.Combat;
using Game.Unit;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game
{
    public class SkirmishLoby : MonoBehaviour
    {
        public bool _isInit;

        [SerializeField]
        private GridBase[] _grids;

        public List<TeamSlot> _slots = new List<TeamSlot>();
        [SerializeField]
        private SubTeamIndex[] _teams;
        
        World _world;

        public int UserID;
        public int UserTeamID;


        public bool AllMapClear;

        public bool ExistUserPlayer;


        // Start is called once before the first execution of Update after the MonoBehaviour is created
         IEnumerator Start()
        {
            do
            {
                yield return null;
                _world = World.DefaultGameObjectInjectionWorld;
            } while (_world == null);

            _isInit = true;

            GameStart();
        }

        // Update is called once per frame
        void Update()
        {

        }

        public void GameStart()
        {
            if (!_isInit)
                return;
            ExistUserPlayer = ArrangeTeam();
            CreateTeamEntity();
            CreateGridData();
        }
        public bool ArrangeTeam()
        {
            bool _existPlayer = true;
            _teams = new SubTeamIndex[_slots.Count];
            for (int i = 0; i < _slots.Count; i++)
            {
                _teams[i] = new SubTeamIndex();
            }
            Debug.Log(_slots.Count);
            UserID = -1;
            for (int i = 1; i <= _slots.Count; i++)
            {
                if (_slots[i-1].IsPlayer)
                {
                    UserID = i;
                    UserTeamID = _slots[i-1].TeamID;
                    break;
                }
            }
            if(UserID==-1)
            {
                Debug.Log("no player");
                _existPlayer = false;
            }

            for (int i = 1; i <= _slots.Count; i++)
            {
               var team=_teams[i-1];
                var myslot = _slots[i-1];
                Debug.LogError(team==null);
                Debug.LogError(myslot==null);
                if (!myslot.IsOpen)
                    continue;
                team.IsPlayer = myslot.IsPlayer;
                team.IsPlayerTeam = myslot.TeamID == UserTeamID;
                team.LocalID = i;
                for (int t = 0; t < _slots.Count; t++)
                {
                    var otherteam = _slots[t];
                    if(!otherteam.IsOpen)
                        continue;
                    int otberteamid = otherteam.TeamID;
                    if (otherteam.TeamID==myslot.TeamID)
                    {
                        team.AllyTeam.Add(t);
                    }
                    else
                    {
                        team.EnemyTeam.Add(t);
                    }
                }
            }
            return _existPlayer;
        }
        private void CreateTeamEntity()
        {
            if (_world == null)
                return;

            EntityManager em = _world.EntityManager;
            if (UserID != -1)
            {
                var usertag = em.CreateEntity();
                em.AddComponentData<UserPlayer>(usertag, new UserPlayer { LocalID = UserID, TeamID = UserTeamID });
            }
            for (int i = 0; i < _teams.Length; i++)
            {
                var team = _teams[i];
                var teamEntity = em.CreateEntity();
                TeamInfoData teamdata = new TeamInfoData
                {
                    TeamID = team.TeamID,
                    LocalID = team.LocalID,
                };
                for (int j = 0; team.AllyTeam.Count < j; j++)
                {
                    teamdata.AddAllyTeam(team.AllyTeam[j]);
                }
                for(int j = 0; team.EnemyTeam.Count < j; j++)
                {
                    teamdata.AddEnemyTeam(team.EnemyTeam[j]);
                }
                teamdata.SetLocalID(team.LocalID);
                teamdata.SetPlayer(team.IsPlayer);
                teamdata.SetPlayerTeam(team.IsPlayerTeam);
                em.AddComponentData<TeamInfoData>(teamEntity, teamdata);
                em.AddComponent<LocalPlayerTag>(teamEntity);
                em.AddComponentData<TeamUnitCountData>(teamEntity, new TeamUnitCountData { UnitCount = 0 });
                string teamname = team.LocalID.ToString() + ": isplayer " + team.IsPlayer;
                em.SetName(teamEntity, teamname);
            }

            var visibleentity = em.CreateEntity();
            VisibleStateData.State state = VisibleStateData.State.RealVisible;
            if (ExistUserPlayer || AllMapClear)
                state = VisibleStateData.State.FullVisible;
            VisibleStateData visiblestate = new VisibleStateData()
            {
                Visible = state
            };
            em.AddComponentData(visibleentity, visiblestate);
        }
        private void CreateGridData()
        {
            for (int i = 0; i < _grids.Length; i++)
            {
                var basegrid = _grids[i];
                int withcount = UnityEngine.Mathf.CeilToInt(basegrid.Width / basegrid.CellSize);
                int heightcount = Mathf.CeilToInt(basegrid.Height / basegrid.CellSize);

                EntityManager em = _world.EntityManager;
                Entity gridentity = em.CreateEntity();
                switch (basegrid.Type)
                {
                    case GridBase.GridType.Combat:
                        CombatGridInfo info = new CombatGridInfo
                        {
                            MapSize = basegrid.MapSize,
                            Cellsize = basegrid.CellSize,
                            GridCount = new int2(withcount, heightcount),
                            MaxPosition = basegrid.MapSize,
                            MinPosition = float2.zero,
                        };
                        em.AddComponentData<CombatGridInfo>(gridentity, info);
                        break;
                    case GridBase.GridType.Road:
                        break;
                    case GridBase.GridType.Construct:
                        break;
                    default:
                        break;
                }
            }
        }
        [Serializable]
        public class SubTeamIndex
        {
            public bool IsPlayer;
            public bool IsPlayerTeam;
            public int LocalID;
            public int TeamID;
            [Range(0, 7)]
            public List<int> AllyTeam;
            [Range(0, 7)]
            public List<int> EnemyTeam;
            [Range (0, 7)]
            public List<int> NeutralTeam;
            public SubTeamIndex()
            {
                AllyTeam = new List<int>();
                EnemyTeam = new List<int>();
                NeutralTeam = new List<int>();
            }
        }
        [Serializable]
        public sealed class TeamSlot
        {
            public bool IsOpen;
            public int TeamID;
            public bool IsPlayer;
        }

        [Serializable]
        public sealed class GridBase
        {
            public enum GridType { Combat, Road, Construct }
            public GridType Type;
            public float2 MapSize;
            public float Width;
            public float Height;
            public float CellSize;
        }
    }
}
