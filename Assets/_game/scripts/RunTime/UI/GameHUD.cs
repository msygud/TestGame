using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  GameHUD — 인게임 메인 HUD
    //
    //  탭 구조:
    //    [도로]  — RoadBuildController 연결
    //    [건설]  — (추후 구현)
    //
    //  씬 설정:
    //    Canvas(Screen Space Overlay) 아래에 아래 계층 구성.
    //
    //    TabBar
    //      BtnTabRoad   → _tabRoad
    //      BtnTabBuild  → _tabBuild
    //    PanelRoad      → _panelRoad
    //      BtnRoadToggle  → _btnRoadToggle  (건설 시작 / 건설 중지 토글)
    //      BtnConfirm     → _btnRoadConfirm
    //      BtnUndo        → _btnRoadUndo
    //    PanelBuild     → _panelBuild
    //      (추후)
    //
    //  단축키:
    //    Enter / Space  — 도로 확정
    //    Z              — 되돌리기
    //    Escape         — 건설 모드 해제
    // ══════════════════════════════════════════════════════════════
    public class GameHUD : MonoBehaviour
    {
        // ── 탭 ─────────────────────────────────────────────────────
        [Header("탭 버튼")]
        [SerializeField] Button _tabRoad;
        [SerializeField] Button _tabBuild;

        [Header("패널")]
        [SerializeField] GameObject _panelRoad;
        [SerializeField] GameObject _panelBuild;

        // ── 도로 탭 ────────────────────────────────────────────────
        [Header("도로 건설")]
        [SerializeField] RoadBuildController _roadController;
        [SerializeField] Button    _btnRoadToggle;
        [SerializeField] Button    _btnRoadConfirm;
        [SerializeField] Button    _btnRoadUndo;
        [SerializeField] TMP_Text  _lblRoadToggle;   // "건설 시작" / "건설 중지"
        [SerializeField] TMP_Text  _lblSegmentInfo;  // "구간: N"
        [SerializeField] TMP_Text  _lblHoverStatus;  // 호버 셀 사유 ("건설 불가: 단차 불일치" 등)

        // ── 탭 색 ──────────────────────────────────────────────────
        static readonly Color TabActive   = new Color(0.25f, 0.55f, 1.00f);
        static readonly Color TabInactive = new Color(0.20f, 0.20f, 0.20f);

        int _activeTab = -1;

        // ───────────────────────────────────────────────────────────
        void Start()
        {
            _tabRoad .onClick.AddListener(() => ShowTab(0));
            _tabBuild.onClick.AddListener(() => ShowTab(1));

            _btnRoadToggle .onClick.AddListener(OnRoadToggle);
            _btnRoadConfirm.onClick.AddListener(OnRoadConfirm);
            _btnRoadUndo   .onClick.AddListener(OnRoadUndo);

            ShowTab(0);
        }

        void Update()
        {
            if (_activeTab == 0 && _roadController != null && _roadController.IsModeActive)
            {
                var kb = Keyboard.current;
                if (kb != null)
                {
                    if (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)
                        OnRoadConfirm();
                    else if (kb.zKey.wasPressedThisFrame)
                        OnRoadUndo();
                    else if (kb.escapeKey.wasPressedThisFrame)
                        OnRoadToggle();
                }

                RefreshRoadPanel();
            }
        }

        // ───────────────────────────────────────────────────────────
        //  탭 전환
        // ───────────────────────────────────────────────────────────
        void ShowTab(int idx)
        {
            if (_activeTab == idx) return;

            // 이전 탭 정리
            if (_activeTab == 0 && _roadController != null && _roadController.IsModeActive)
                _roadController.ExitBuildMode();

            _activeTab = idx;

            SetTabColor(_tabRoad,  idx == 0 ? TabActive : TabInactive);
            SetTabColor(_tabBuild, idx == 1 ? TabActive : TabInactive);

            if (_panelRoad  != null) _panelRoad .SetActive(idx == 0);
            if (_panelBuild != null) _panelBuild.SetActive(idx == 1);

            if (idx == 0) RefreshRoadPanel();
        }

        static void SetTabColor(Button btn, Color col)
        {
            if (btn == null) return;
            var img = btn.GetComponent<Image>();
            if (img != null) img.color = col;
        }

        // ───────────────────────────────────────────────────────────
        //  도로 탭 — 버튼 핸들러
        // ───────────────────────────────────────────────────────────
        void OnRoadToggle()
        {
            if (_roadController == null) return;

            if (_roadController.IsModeActive)
                _roadController.ExitBuildMode();
            else
                _roadController.EnterBuildMode();

            RefreshRoadPanel();
        }

        void OnRoadConfirm()
        {
            if (_roadController == null) return;
            _roadController.Confirm();
            RefreshRoadPanel();
        }

        void OnRoadUndo()
        {
            if (_roadController == null) return;
            _roadController.Undo();
            RefreshRoadPanel();   // 되돌린 직후 구간 수 라벨 즉시 갱신
        }

        // ───────────────────────────────────────────────────────────
        //  도로 탭 — UI 갱신
        // ───────────────────────────────────────────────────────────
        void RefreshRoadPanel()
        {
            if (_roadController == null) return;
            bool active = _roadController.IsModeActive;

            if (_lblRoadToggle  != null)
                _lblRoadToggle.text = active ? "Stop" : "Build Road";

            if (_btnRoadConfirm != null)
                _btnRoadConfirm.interactable = active;

            if (_btnRoadUndo != null)
                _btnRoadUndo.interactable = active;

            if (_lblSegmentInfo != null)
                _lblSegmentInfo.text = active
                    ? $"Segments: {_roadController.SegmentCount}"
                    : string.Empty;

            if (_lblHoverStatus != null)
                _lblHoverStatus.text = active ? _roadController.HoverStatusText : string.Empty;
        }
    }
}
