using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  DisplacedCitizensDialog — 인간 유저 수동 철거 이재민 선택 UI (2026-07-17)
    //
    //  RazeSystem(Human=1)이 철거 주택의 거주민을 DisplacedCitizen 싱글톤
    //  buffer에 등재하면 이 다이얼로그가 뜬다. 유저가 시민별로 부분 선택:
    //    · Keep    = 예비자 유지 — 시민은 이미 UnassignedTag(재하우징 큐)에
    //                있으므로 목록에서 제거만 한다. 새 주택이 서면 우선 입주
    //                (이민 회계가 대기자를 정원에서 차감 — 이중 유입 없음).
    //    · Dismiss = 해산 — 엔티티 파괴. 빈 정원만큼 이민이 새 시민(새 랜덤
    //                능력치)을 자연 재생성한다("다시 생성"의 실체).
    //  숙련 든 시민을 남길지, 새 뽑기로 갈지의 선택지 — 능력치는 스폰 랜덤,
    //  숙련은 축적이므로 목록에 직업·숙련·능력치를 표시한다.
    //
    //  해산 정리(카운트 누수 방지 — DeadReferenceReclaim은 죽은 '건물' 참조
    //  전담이라 죽은 '시민'의 좌석/근무 슬롯은 여기서 직접 해제):
    //    · Work 생존 → BuildingOccupancy.Release (근무 슬롯).
    //    · ServiceTarget.Supplier 생존 + (Service 이동 중 or 목적지 체류)
    //      → VisitorOccupancy.Release (방문 좌석 — 출발 시점 예약 규약).
    //
    //  IMGUI 대화형(버튼/토글)이라 Repaint 제한 없음 — 표시 중에만 비용 발생,
    //  철거는 저빈도 이벤트. UI 문구는 영문(프로젝트 규약).
    // ══════════════════════════════════════════════════════════════
    public class DisplacedCitizensDialog : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static bool _created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (_created) return;
            _created = true;
            var go = new GameObject("[DisplacedCitizensDialog]");
            go.AddComponent<DisplacedCitizensDialog>();
            DontDestroyOnLoad(go);
        }
#endif
        World       _qWorld;
        EntityQuery _qList;

        readonly List<Entity> _rows = new();
        readonly List<bool>   _keep = new();
        int     _snapshotLen = -1;   // buffer 길이 변화 감지(신규 철거 = 목록 재구성)
        Vector2 _scroll;

        void OnGUI()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { _qWorld = null; return; }
            var em = world.EntityManager;

            if (!ReferenceEquals(_qWorld, world))
            {
                _qWorld = world;
                _qList  = em.CreateEntityQuery(ComponentType.ReadWrite<DisplacedCitizen>());
                _snapshotLen = -1;
            }
            if (_qList.IsEmpty) { _snapshotLen = -1; return; }

            var listEntity = _qList.GetSingletonEntity();
            var buf        = em.GetBuffer<DisplacedCitizen>(listEntity);
            if (buf.Length == 0) { _snapshotLen = -1; return; }

            // 스냅샷 재구성(새 철거로 목록이 바뀌었을 때만) — 죽은 엔티티는 걸러냄.
            if (buf.Length != _snapshotLen)
            {
                _snapshotLen = buf.Length;
                _rows.Clear(); _keep.Clear();
                for (int i = 0; i < buf.Length; i++)
                {
                    if (!em.Exists(buf[i].Citizen)) continue;
                    _rows.Add(buf[i].Citizen);
                    _keep.Add(true);   // 기본 = 유지(보수적 — 실수로 숙련 시민을 잃지 않게)
                }
                if (_rows.Count == 0) { ClearList(em, listEntity); return; }
            }

            const float W = 420f;
            float h = Mathf.Min(140f + _rows.Count * 24f, 480f);
            var rect = new Rect((Screen.width - W) * 0.5f, (Screen.height - h) * 0.5f, W, h);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label($"<b>Displaced Citizens ({_rows.Count})</b> — demolished housing",
                RichLabel());
            GUILayout.Label("Checked = keep (waits for new housing). Unchecked = dismiss "
                + "(replaced later by fresh immigrants).", RichLabel());

            _scroll = GUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _rows.Count; i++)
            {
                var ce = _rows[i];
                if (!em.Exists(ce)) continue;
                string desc = Describe(em, ce);
                _keep[i] = GUILayout.Toggle(_keep[i], desc);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("All"))  for (int i = 0; i < _keep.Count; i++) _keep[i] = true;
            if (GUILayout.Button("None")) for (int i = 0; i < _keep.Count; i++) _keep[i] = false;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Confirm"))
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_keep[i] || !em.Exists(_rows[i])) continue;
                    Dismiss(em, _rows[i]);
                }
                ClearList(em, listEntity);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void ClearList(EntityManager em, Entity listEntity)
        {
            em.GetBuffer<DisplacedCitizen>(listEntity).Clear();
            _rows.Clear(); _keep.Clear();
            _snapshotLen = -1;
        }

        // 시민 1줄 요약: 직업 + 그 직업 숙련 + 능력치 축약(체/지/손).
        static string Describe(EntityManager em, Entity ce)
        {
            var job = em.HasComponent<JobData>(ce)
                ? em.GetComponentData<JobData>(ce).Job : JobType.Unemployed;
            float skill = em.HasComponent<CitizenSkills>(ce)
                ? em.GetComponentData<CitizenSkills>(ce).Get(job) : 0f;
            string attrs = "";
            if (em.HasComponent<CitizenAttributes>(ce))
            {
                var a = em.GetComponentData<CitizenAttributes>(ce);
                attrs = $"  P{a.Physique} I{a.Intelligence} D{a.Dexterity}";
            }
            return $"{job}  skill {skill:0.#}{attrs}";
        }

        // 해산: 근무 슬롯·방문 좌석 해제 후 엔티티 파괴(주석 상단 참조).
        static void Dismiss(EntityManager em, Entity ce)
        {
            if (em.HasComponent<CitizenResidence>(ce))
            {
                var work = em.GetComponentData<CitizenResidence>(ce).Work;
                if (work != Entity.Null && em.Exists(work) && em.HasComponent<BuildingOccupancy>(work))
                {
                    var occ = em.GetComponentData<BuildingOccupancy>(work);
                    occ.Release();
                    em.SetComponentData(work, occ);
                }
            }
            if (em.HasComponent<ServiceTarget>(ce) && em.HasComponent<CitizenState>(ce))
            {
                var st  = em.GetComponentData<CitizenState>(ce);
                var tgt = em.GetComponentData<ServiceTarget>(ce);
                bool seated = (st.Activity == CitizenActivity.Traveling
                               && st.Purpose == TravelPurpose.Service)
                              || st.Activity == CitizenActivity.AtDestination;
                if (seated && tgt.Supplier != Entity.Null && em.Exists(tgt.Supplier)
                    && em.HasComponent<VisitorOccupancy>(tgt.Supplier))
                {
                    var vo = em.GetComponentData<VisitorOccupancy>(tgt.Supplier);
                    vo.Release();
                    em.SetComponentData(tgt.Supplier, vo);
                }
            }
            em.DestroyEntity(ce);
        }

        static GUIStyle _rich;
        static GUIStyle RichLabel()
        {
            _rich ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            return _rich;
        }
    }
}
