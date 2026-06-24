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

        // ── 관리소 배치 (도로 인프라 — 건물탭 아님) ─────────────────
        [Header("도로 관리시설 배치")]
        [SerializeField] DepotPlaceController _depotController;
        [SerializeField] Button   _btnDepotToggle;   // 배치 시작 / 중지 토글
        [SerializeField] TMP_Text _lblDepotToggle;   // "Place Depot" / "Stop Depot"

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

            if (_btnDepotToggle != null)
                _btnDepotToggle.onClick.AddListener(OnDepotToggle);

            ShowTab(0);
        }

        void Update()
        {
            if (_activeTab != 0) return;

            bool roadActive  = _roadController  != null && _roadController.IsModeActive;
            bool depotActive = _depotController != null && _depotController.IsModeActive;
            if (!roadActive && !depotActive) return;

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (roadActive)
                {
                    if (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)
                        OnRoadConfirm();
                    else if (kb.zKey.wasPressedThisFrame)
                        OnRoadUndo();
                    else if (kb.escapeKey.wasPressedThisFrame)
                        OnRoadToggle();
                }
                else if (depotActive && kb.escapeKey.wasPressedThisFrame)
                {
                    OnDepotToggle();
                }
            }

            RefreshRoadPanel();
        }

        // ───────────────────────────────────────────────────────────
        //  탭 전환
        // ───────────────────────────────────────────────────────────
        void ShowTab(int idx)
        {
            if (_activeTab == idx) return;

            // 이전 탭 정리 — 도로탭의 두 도구(도로 건설/관리소 배치) 모두 해제.
            if (_activeTab == 0)
            {
                if (_roadController != null && _roadController.IsModeActive)
                    _roadController.ExitBuildMode();
                if (_depotController != null && _depotController.IsModeActive)
                    _depotController.ExitMode();
            }

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
            {
                _roadController.ExitBuildMode();
            }
            else
            {
                // 상호배타 — 관리소 배치 모드가 켜져 있으면 먼저 끈다.
                if (_depotController != null && _depotController.IsModeActive)
                    _depotController.ExitMode();
                _roadController.EnterBuildMode();
            }

            RefreshRoadPanel();
        }

        // 관리소 배치 토글 — 도로 건설과 상호배타(같은 프리뷰 버퍼 공유).
        void OnDepotToggle()
        {
            if (_depotController == null) return;

            if (_depotController.IsModeActive)
            {
                _depotController.ExitMode();
            }
            else
            {
                if (_roadController != null && _roadController.IsModeActive)
                    _roadController.ExitBuildMode();
                _depotController.EnterMode();
            }

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
            bool roadActive  = _roadController.IsModeActive;
            bool depotActive = _depotController != null && _depotController.IsModeActive;

            if (_lblRoadToggle  != null)
                _lblRoadToggle.text = roadActive ? "Stop" : "Build Road";

            if (_lblDepotToggle != null)
                _lblDepotToggle.text = depotActive ? "Stop Depot" : "Place Depot";

            // 도로 건설 전용 버튼은 도로 모드일 때만 활성.
            if (_btnRoadConfirm != null)
                _btnRoadConfirm.interactable = roadActive;

            if (_btnRoadUndo != null)
                _btnRoadUndo.interactable = roadActive;

            if (_lblSegmentInfo != null)
                _lblSegmentInfo.text = roadActive
                    ? $"Segments: {_roadController.SegmentCount}"
                    : string.Empty;

            // 호버 사유 라벨은 활성 도구의 상태를 표시.
            if (_lblHoverStatus != null)
            {
                if (depotActive)      _lblHoverStatus.text = _depotController.StatusText;
                else if (roadActive)  _lblHoverStatus.text = _roadController.HoverStatusText;
                else                  _lblHoverStatus.text = string.Empty;
            }
        }
    }
}
