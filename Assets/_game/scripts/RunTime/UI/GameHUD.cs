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
    //    [건설]  — BuildingPlaceController 연결 (레지스트리 Building 항목 버튼 자동 생성)
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
    //      Content(Transform, VerticalLayoutGroup 등) → _buildButtonContainer
    //      ButtonTemplate(Button, 비활성)             → _buildButtonTemplate
    //        · _registries의 Building 항목마다 이 버튼을 복제해 자동 채움.
    //      CoverageToggles(선택 — 커버리지 통합창, 2026-07-12)
    //        TglCovWarehouse → _tglCovWarehouse  (창고 커버 초록)
    //        TglCovSupply    → _tglCovSupply     (서비스 커버 주황)
    //        TglCovAura      → _tglCovAura       (치안 오라 보라)
    //        · 배치 프리뷰의 종류별 커버 표시를 켜고 끔(CoverageView 정적 상태).
    //        · 미할당이면 전부 표시(기본값) — 창은 나중에 붙여도 됨("길만 열어두기").
    //        · 상태는 PlayerPrefs로 세션 간 유지.
    //
    //  단축키:
    //    Enter / Space  — 도로 확정
    //    Z              — 되돌리기
    //    R              — (건설 탭) 건물 90° 회전
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

        // ── 건설 탭 ────────────────────────────────────────────────
        [Header("건물 배치")]
        [SerializeField] BuildingPlaceController _buildController;
        [Tooltip("버튼을 생성할 프리팹 레지스트리 SO(들). Building 카테고리 항목마다 버튼 1개.")]
        [SerializeField] GamePrefabRegistry[] _registries;
        [Tooltip("버튼이 들어갈 부모(레이아웃 그룹 권장).")]
        [SerializeField] Transform _buildButtonContainer;
        [Tooltip("복제할 버튼 템플릿(비활성 상태로 두면 됨). 자식 TMP_Text에 건물명 표시.")]
        [SerializeField] Button    _buildButtonTemplate;

        bool _buildButtonsBuilt;

        // ── 커버리지 표시 토글(통합창, 2026-07-12) — 미할당 시 전부 표시 ──
        [Header("커버리지 표시 토글(선택 — 미할당이면 전부 표시)")]
        [SerializeField] Toggle _tglCovWarehouse;
        [SerializeField] Toggle _tglCovSupply;
        [SerializeField] Toggle _tglCovAura;

        // ── 탭 색 ──────────────────────────────────────────────────
        static readonly Color TabActive   = new Color(0.25f, 0.55f, 1.00f);
        static readonly Color TabInactive = new Color(0.20f, 0.20f, 0.20f);

        int _activeTab = -1;

        // GC/재레이아웃 방지(2026-07-05) — TMP .text는 같은 내용이라도 대입하면 재구축이 돌 수
        //   있고, 매 프레임 보간 문자열은 GC 쓰레기. 컨트롤러가 상태 문자열을 캐싱(참조 안정)
        //   하므로 '참조가 바뀔 때만' 대입한다.
        string _assignedToggle, _assignedSegment, _assignedHover;
        int    _lastSegmentCount = -1;
        string _segmentText = string.Empty;

        static void SetTextIfChanged(TMP_Text lbl, string s, ref string last)
        {
            if (lbl == null || ReferenceEquals(last, s)) return;
            lbl.text = s;
            last = s;
        }

        // ───────────────────────────────────────────────────────────
        void Start()
        {
            _tabRoad .onClick.AddListener(() => ShowTab(0));
            _tabBuild.onClick.AddListener(() => ShowTab(1));

            _btnRoadToggle .onClick.AddListener(OnRoadToggle);
            _btnRoadConfirm.onClick.AddListener(OnRoadConfirm);
            _btnRoadUndo   .onClick.AddListener(OnRoadUndo);

            // 커버리지 통합창 토글 — CoverageView(정적 상태) ↔ PlayerPrefs 영속.
            WireCoverageToggle(_tglCovWarehouse, "CovShowWarehouse",
                v => CoverageView.ShowWarehouse = v, () => CoverageView.ShowWarehouse);
            WireCoverageToggle(_tglCovSupply, "CovShowSupply",
                v => CoverageView.ShowSupply = v, () => CoverageView.ShowSupply);
            WireCoverageToggle(_tglCovAura, "CovShowAura",
                v => CoverageView.ShowAura = v, () => CoverageView.ShowAura);

            ShowTab(0);
        }

        // 커버리지 토글 와이어링 — 씬에 토글이 없으면(미할당) 해당 종류는 항상 표시.
        static void WireCoverageToggle(Toggle tgl, string prefKey,
            System.Action<bool> set, System.Func<bool> get)
        {
            if (tgl == null) return;
            bool v = PlayerPrefs.GetInt(prefKey, get() ? 1 : 0) != 0;
            set(v);
            tgl.SetIsOnWithoutNotify(v);
            tgl.onValueChanged.AddListener(on =>
            {
                set(on);
                PlayerPrefs.SetInt(prefKey, on ? 1 : 0);
            });
        }

        void Update()
        {
            if (_activeTab == 0) { UpdateRoadTab(); return; }
            if (_activeTab == 1) { UpdateBuildTab(); return; }
        }

        void UpdateRoadTab()
        {
            bool roadActive = _roadController != null && _roadController.IsModeActive;
            if (!roadActive) return;

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

        void UpdateBuildTab()
        {
            if (_buildController == null) return;

            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && _buildController.IsModeActive)
                _buildController.ExitMode();

            // 호버 상태 라벨 재사용 (도로 탭과 공유).
            SetTextIfChanged(_lblHoverStatus,
                _buildController.IsModeActive ? _buildController.StatusText : string.Empty,
                ref _assignedHover);
        }

        // ───────────────────────────────────────────────────────────
        //  탭 전환
        // ───────────────────────────────────────────────────────────
        void ShowTab(int idx)
        {
            if (_activeTab == idx) return;

            // 이전 탭 정리 — 떠나는 탭의 건설 모드 해제.
            if (_activeTab == 0 && _roadController != null && _roadController.IsModeActive)
                _roadController.ExitBuildMode();
            if (_activeTab == 1 && _buildController != null && _buildController.IsModeActive)
                _buildController.ExitMode();

            _activeTab = idx;

            SetTabColor(_tabRoad,  idx == 0 ? TabActive : TabInactive);
            SetTabColor(_tabBuild, idx == 1 ? TabActive : TabInactive);

            if (_panelRoad  != null) _panelRoad .SetActive(idx == 0);
            if (_panelBuild != null) _panelBuild.SetActive(idx == 1);

            if (idx == 0) RefreshRoadPanel();
            if (idx == 1) EnsureBuildButtons();
        }

        // ───────────────────────────────────────────────────────────
        //  건설 탭 — 레지스트리 Building 항목으로 버튼 자동 생성 (1회)
        // ───────────────────────────────────────────────────────────
        void EnsureBuildButtons()
        {
            if (_buildButtonsBuilt) return;
            _buildButtonsBuilt = true;

            if (_buildButtonTemplate == null || _buildButtonContainer == null || _registries == null)
            {
                Debug.LogWarning("[GameHUD] 건설 버튼 생성 생략 — "
                    + "_buildButtonTemplate/_buildButtonContainer/_registries 와이어링 확인.");
                return;
            }

            _buildButtonTemplate.gameObject.SetActive(false);   // 템플릿 자체는 숨김

            int made = 0;
            foreach (var reg in _registries)
            {
                if (reg == null) continue;
                foreach (var item in reg.Items)
                {
                    if (item == null || item.IsDeleted || item.Prefab == null) continue;
                    if (item.Category != PrefabCategory.Building) continue;
                    if (item.VariantKey != 0) continue;   // 대표(V0)만 — 능력은 per-MainKey
                    if (item.MainKey <= 0) continue;      // 빈/널 키 행 제외

                    int    mainKey = item.MainKey;
                    string label   = string.IsNullOrEmpty(item.Name) ? $"#{mainKey}" : item.Name;

                    var btn = Instantiate(_buildButtonTemplate, _buildButtonContainer);
                    btn.gameObject.SetActive(true);
                    var txt = btn.GetComponentInChildren<TMP_Text>(true);
                    if (txt != null) txt.text = label;
                    btn.onClick.AddListener(() => OnBuildingSelected(mainKey));
                    made++;
                }
            }
            Debug.Log($"[GameHUD] 건설 버튼 {made}개 생성.");
        }

        void OnBuildingSelected(int mainKey)
        {
            if (_buildController == null) return;
            // 상호배타 — 도로 모드가 켜져 있으면 끈다.
            if (_roadController != null && _roadController.IsModeActive)
                _roadController.ExitBuildMode();
            _buildController.EnterMode(mainKey);
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
            bool roadActive  = _roadController.IsModeActive;

            // 리터럴은 intern 되어 참조가 안정 — 바뀔 때만 대입된다.
            SetTextIfChanged(_lblRoadToggle, roadActive ? "Stop" : "Build Road", ref _assignedToggle);

            // 도로 건설 전용 버튼은 도로 모드일 때만 활성.
            if (_btnRoadConfirm != null)
                _btnRoadConfirm.interactable = roadActive;

            if (_btnRoadUndo != null)
                _btnRoadUndo.interactable = roadActive;

            // 구간 수 문자열은 수가 바뀔 때만 재조립.
            string seg = string.Empty;
            if (roadActive)
            {
                int n = _roadController.SegmentCount;
                if (n != _lastSegmentCount) { _lastSegmentCount = n; _segmentText = $"Segments: {n}"; }
                seg = _segmentText;
            }
            SetTextIfChanged(_lblSegmentInfo, seg, ref _assignedSegment);

            // 호버 사유 라벨은 도로 건설 상태를 표시(컨트롤러가 상수/캐시 문자열 반환).
            SetTextIfChanged(_lblHoverStatus,
                roadActive ? _roadController.HoverStatusText : string.Empty, ref _assignedHover);
        }
    }
}
