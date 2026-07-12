using CitySim;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

// 디버그 입력 — 마우스로 가리킨 건물에:
//   Alt+좌클릭 = 파괴 (건물=RazeAreaCommand, 도로=Forced RemoveRoadCommand).
//   Ctrl+우클릭 = '거주건물'로 지정 (ResidenceBuilding + BuildingOccupancy{Capacity=50}).
//            → TerritorySystem이 1초마다 전체 재계산해 그 건물 중심으로 영역을 그린다.
//            영역 확인: F7(TerritoryDebugSystem 오버레이). PopPerCell 필드로 영역 크기 조절.
//   ⚠ Ctrl 게이트(2026-07-12): 맨 우클릭은 유닛 이동 명령과 겹쳐, 명령 중 커서 아래 건물
//     (창고 포함)을 조용히 거주건물로 오태그 → 재개발 철거 대상이 되던 함정("창고 소멸"의
//     유력 경로). 파괴(Alt+좌클릭)와 동일한 수식키 규약으로 봉인.
//   ※ 프로덕션은 프리팹에 BuildingAuthoring(Kind=Residence, Capacity)로 베이크하는 게 정석.
//     우클릭 태깅은 프리팹 미설정 상태에서 영역 파이프라인을 즉시 검증하기 위한 테스트용.
public class Test : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════════
    //  통합 밸런스 패널 (2026-07-05) — 도메인별 config 싱글톤에 매 프레임 push.
    //  인스펙터에서 바꾸면 즉시 반영. 여기 안 보이는 값의 기본은 각 struct의 Default.
    //  ⚠ 인스펙터 초기값은 씬에 직렬화됨 — struct Default를 바꿔도 씬 값이 우선.
    // ═══════════════════════════════════════════════════════════════════

    [Header("영역 (TerritoryConfig)")]
    [Tooltip("셀 1칸 점유에 필요한 인구(float). 영역 셀 수 = floor(거주건물 인구 / 이 값).")]
    public float PopPerCell = 5f;
    [Tooltip("영역 확산 윈도우 최대 반경(셀) — 성능·폭주 가드.")]
    public int MaxRadius = 64;

    [Header("영토 전환 파괴 (TerritoryCaptureConfig)")]
    [Tooltip("타팀 영토에 놓인 구조물이 파괴되기까지의 유예(게임 시간). 1 = 게임 1시간 ≈ 현실 50초.")]
    public float DwellGameHours = 1f;
    [Tooltip("건물은 footprint 전체가 넘어가야 파괴 대상(경계 걸침 보호).")]
    public bool RequireFullFootprint = true;
    [Tooltip("패스당 파괴 상한(대량 함락 스파이크 방지 — 넘치면 이월).")]
    public int MaxDestroysPerPass = 32;
    [Tooltip("AI 확장이 적 영토에서 유지할 완충 거리(셀). 0 = 완충 없음.")]
    public int AiEnemyBufferCells = 4;
    [Tooltip("중립(무법지대) 도로의 전투 체력. 0 = 기능 끔(중립 도로 비타겟).")]
    public float NeutralRoadHealth = 200f;

    [Header("AI 성장 (GrowthConfig)")]
    public int BuildingKeyA = 1004;
    public int BuildingKeyB = 1005;
    [Tooltip("확장 편향 데드밴드(셀) — 블록 한 변(4~8)보다 커야 좌우 떨림 방지.")]
    public int BalanceDeadband = 8;
    [Tooltip("기존 도로와 공유 없이 한 칸 평행으로 깔리는 블록을 거부.")]
    public bool RejectParallelSeam = true;
    [Tooltip("한 틱(게임-일)당 팀별 최대 배치 수.")]
    public int BuildPerTick = 6;
    [Tooltip("'베이스-연결' 판정 시드 반경(셀) — 성장 앵커 게이트와 janitor가 공유.")]
    public int BaseSeedRadius = 8;

    [Header("AI janitor (AiJanitorConfig)")]
    [Tooltip("owner당 하루 입구 도로 복구 상한.")]
    public int MaxEntranceRepairsPerDay = 2;
    [Tooltip("고립 섬 재연결 BFS 탐색 상한(셀).")]
    public int ReconnectMaxExplore = 8192;

    [Header("스폰 (SpawnConfig)")]
    [Tooltip("건물 기본 전투 체력(균일, 임시 — 추후 프리팹별 베이킹).")]
    public float BuildingDefaultHealth = 500f;

    [Header("시민 (CitizenConfig)")]
    [Tooltip("게임-시간당 owner별 이민 유입 상한(빈 거주 정원 내). 0 = 유입 정지.")]
    public int ImmigrantsPerHourPerPlayer = 4;
    [Tooltip("허기 증가율(게임초당). 끼니 주기 = 0.6/이 값. 0.010=게임 1분 1끼(과속), "
           + "0.0013≈하루 2~3끼(권장). 스폰 시 베이킹 — 새 시민부터 적용.")]
    public float HungerRatePerGameSec = 0.010f;
    [Tooltip("근무 시작 시(게임 시간 0~23). 이 시간대에 직장 있는 시민이 출근.")]
    public int WorkStartHour = 8;
    [Tooltip("근무 종료 시(게임 시간, Start보다 커야 함). 이 시각에 퇴근.")]
    public int WorkEndHour = 18;
    [Tooltip("근무 1게임시간당 숙련 기본 성장량(적성 배율 전). 1.0 ≈ 하루 +10.")]
    public float SkillGrowthPerWorkHour = 1.0f;
    [Tooltip("점심시간 시작 시(게임 시간). 길이가 0이면 무시.")]
    public int LunchStartHour = 12;
    [Tooltip("점심시간 길이(게임 시간). 0 = 점심 없음(기본). 켜면 12시 식당行→복귀가 창발.")]
    public int LunchGameHours = 0;

    [Header("Territory 팀/영향력 테스트 (인덱스 = LocalId 0~7)")]
    [Tooltip("플레이어별 영향력(경합 해소용 스칼라). 같은 팀끼리 합산해 승자팀−2등팀으로 경합 결정.")]
    public float[] PlayerInfluence = { 10, 10, 10, 10, 10, 10, 10, 10 };
    [Tooltip("플레이어별 팀(동맹) id. 같은 값 = 동맹(영향력 합산·서로 경합 안 함). 기본 각자 자기 팀.")]
    public int[]   PlayerTeam      = { 0, 1, 2, 3, 4, 5, 6, 7 };

    [Header("테스트 입력")]
    [Tooltip("우클릭 태깅 시, BuildingOccupancy가 '없는' 건물에만 줄 정원(인구). "
           + "이미 베이킹된 BuildingOccupancy가 있으면 그 값을 존중(덮어쓰지 않음).")]
    public int TestResidenceCapacity = 10;

    void Update()
    {
        SyncBalanceConfigs();

        var mouse = Mouse.current;
        if (mouse == null) return;
        // 파괴 = Alt+좌클릭(건물·도로 공통) — 일반 좌클릭은 건설(도로/건물 배치)과 충돌하므로
        //   수식키로 분리(맨 클릭 파괴가 건설 시작 도로를 지워 베이스-연결 전제를 깨던 문제).
        var kb   = Keyboard.current;
        bool alt = kb != null && kb.altKey.isPressed;
        bool raze = alt && mouse.leftButton.wasPressedThisFrame;
        // Ctrl 필수 — 맨 우클릭(유닛 명령)이 건물을 거주건물로 오태그하지 않도록(헤더 주석 참조).
        bool tag  = kb != null && kb.ctrlKey.isPressed && mouse.rightButton.wasPressedThisFrame;
        if (!raze && !tag) return;

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;
        var em = world.EntityManager;

        // 셀 크기 (GridSettings 규약: 셀 중심 = idx*cs + cs/2 → idx = floor(world/cs))
        var gsQ = em.CreateEntityQuery(ComponentType.ReadOnly<GridSettings>());
        if (gsQ.IsEmpty) { gsQ.Dispose(); return; }
        float cs = gsQ.GetSingleton<GridSettings>().CellSize;
        gsQ.Dispose();
        if (cs <= 0f) return;

        // 마우스 → 월드 → 셀 (콜라이더 우선, 없으면 y=0 평면)
        var cam = Camera.main;
        if (cam == null) return;
        Ray ray = cam.ScreenPointToRay((Vector2)mouse.position.ReadValue());
        Vector3 p;
        if (Physics.Raycast(ray, out var hit, 5000f)) p = hit.point;
        else if (math.abs(ray.direction.y) > 1e-5f)
        {
            float t = -ray.origin.y / ray.direction.y;
            if (t <= 0f) return;
            p = ray.origin + ray.direction * t;
        }
        else return;
        int2 cell = new int2((int)math.floor(p.x / cs), (int)math.floor(p.z / cs));

        // 클릭 셀을 포함하는 건물 엔티티 찾기
        var bq   = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingFootprint>());
        var ents = bq.ToEntityArray(Allocator.Temp);
        Entity target = Entity.Null; int2 mn = default, mx = default;
        foreach (var ent in ents)
        {
            var bf = em.GetComponentData<BuildingFootprint>(ent);
            int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);
            if (cell.x >= bf.Origin.x && cell.x < bf.Origin.x + eff.x &&
                cell.y >= bf.Origin.y && cell.y < bf.Origin.y + eff.y)
            {
                target = ent; mn = bf.Origin; mx = bf.Origin + eff - 1; break;
            }
        }
        ents.Dispose(); bq.Dispose();

        if (target == Entity.Null)
        {
            // 건물 없음 → 도로면 강제 철거 (테스트 파괴에 도로 포함).
            if (raze)
            {
                var glQ = em.CreateEntityQuery(ComponentType.ReadOnly<GridLayers>());
                bool isRoad = !glQ.IsEmpty
                              && glQ.GetSingleton<GridLayers>().RoadLayer.TryGetValue(cell, out _);
                glQ.Dispose();
                if (isRoad)
                {
                    var re = em.CreateEntity();
                    em.AddComponentData(re, new RemoveRoadCommand
                    { Cell = cell, OwnerLocalId = -1, Forced = 1 });
                    Debug.Log($"[Test] 도로 파괴 {cell}");
                    return;
                }
            }
            Debug.Log($"[Test] cell {cell} — 건물/도로 없음");
            return;
        }

        if (raze)
        {
            var e = em.CreateEntity();
            em.AddComponentData(e, new RazeAreaCommand { Min = mn, Max = mx });
            Debug.Log($"[Test] 건물 파괴 {mn} ~ {mx}");
            return;
        }

        // 우클릭 = 거주건물로 지정 (영역 테스트)
        if (!em.HasComponent<ResidenceBuilding>(target))
            em.AddComponent<ResidenceBuilding>(target);

        // 베이킹된 BuildingOccupancy가 있으면 그 정원(인구)을 존중 — 덮어쓰지 않음.
        //   없을 때만 테스트용 정원을 부여(프리팹에 BuildingAuthoring 미설정 상태 대비).
        int cap;
        if (em.HasComponent<BuildingOccupancy>(target))
        {
            cap = em.GetComponentData<BuildingOccupancy>(target).Capacity;
        }
        else
        {
            cap = Mathf.Max(1, TestResidenceCapacity);
            em.AddComponentData(target, new BuildingOccupancy { Current = 0, Capacity = cap });
        }
        Debug.Log($"[Test] 거주건물 지정 (Capacity={cap}, 베이킹값 존중) @ {mn} — F7로 영역 확인");
    }

    // ── 통합 밸런스 push ─────────────────────────────────────────────────
    //  싱글톤 엔티티는 캐시(매 프레임 CreateEntityQuery는 관리형 쓰레기 → GC 스파이크).
    //  월드 교체(플레이 재시작 등) 시 캐시 리셋.
    World  _cfgWorld;
    Entity _eTerritory, _eCapture, _eGrowth, _eJanitor, _eSpawn, _eCitizen, _eInfluence;

    static Entity GetOrCreateSingleton<T>(EntityManager em, ref Entity cached)
        where T : unmanaged, IComponentData
    {
        if (cached != Entity.Null && em.Exists(cached) && em.HasComponent<T>(cached))
            return cached;
        var q = em.CreateEntityQuery(typeof(T));   // 최초/월드 교체 시에만 도달
        cached = q.IsEmpty ? em.CreateEntity(typeof(T)) : q.GetSingletonEntity();
        q.Dispose();
        return cached;
    }

    void SyncBalanceConfigs()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) { _cfgWorld = null; return; }
        if (!ReferenceEquals(_cfgWorld, world))
        {
            _cfgWorld = world;
            _eTerritory = _eCapture = _eGrowth = _eJanitor = _eSpawn = _eCitizen = _eInfluence = Entity.Null;
        }
        var em = world.EntityManager;

        em.SetComponentData(GetOrCreateSingleton<TerritoryConfig>(em, ref _eTerritory),
            new TerritoryConfig
            {
                PopPerCell = Mathf.Max(0.01f, PopPerCell),
                MaxRadius  = Mathf.Max(1, MaxRadius),
            });

        em.SetComponentData(GetOrCreateSingleton<TerritoryCaptureConfig>(em, ref _eCapture),
            new TerritoryCaptureConfig
            {
                DwellGameHours       = Mathf.Max(0f, DwellGameHours),
                RequireFullFootprint = RequireFullFootprint ? (byte)1 : (byte)0,
                MaxDestroysPerPass   = Mathf.Max(1, MaxDestroysPerPass),
                AiEnemyBufferCells   = Mathf.Max(0, AiEnemyBufferCells),
                NeutralRoadHealth    = Mathf.Max(0f, NeutralRoadHealth),
            });

        em.SetComponentData(GetOrCreateSingleton<GrowthConfig>(em, ref _eGrowth),
            new GrowthConfig
            {
                BuildingKeyA       = BuildingKeyA,
                BuildingKeyB       = BuildingKeyB,
                BalanceDeadband    = Mathf.Max(0, BalanceDeadband),
                RejectParallelSeam = RejectParallelSeam,
                BuildPerTick       = Mathf.Max(1, BuildPerTick),
                BaseSeedRadius     = Mathf.Max(1, BaseSeedRadius),
            });

        em.SetComponentData(GetOrCreateSingleton<AiJanitorConfig>(em, ref _eJanitor),
            new AiJanitorConfig
            {
                MaxEntranceRepairsPerDay = Mathf.Max(0, MaxEntranceRepairsPerDay),
                ReconnectMaxExplore      = Mathf.Max(256, ReconnectMaxExplore),
            });

        em.SetComponentData(GetOrCreateSingleton<SpawnConfig>(em, ref _eSpawn),
            new SpawnConfig
            {
                BuildingDefaultHealth = Mathf.Max(1f, BuildingDefaultHealth),
            });

        em.SetComponentData(GetOrCreateSingleton<CitizenConfig>(em, ref _eCitizen),
            new CitizenConfig
            {
                ImmigrantsPerHourPerPlayer = Mathf.Max(0, ImmigrantsPerHourPerPlayer),
                HungerRatePerGameSec       = Mathf.Max(0.0001f, HungerRatePerGameSec),
                WorkStartHour              = Mathf.Clamp(WorkStartHour, 0, 23),
                WorkEndHour                = Mathf.Clamp(WorkEndHour, 1, 24),
                SkillGrowthPerWorkHour     = Mathf.Max(0f, SkillGrowthPerWorkHour),
                LunchStartHour             = Mathf.Clamp(LunchStartHour, 0, 23),
                LunchGameHours             = Mathf.Max(0, LunchGameHours),
            });

        // 영향력/팀 버퍼 (인덱스 = LocalId 0~7)
        var ie = GetOrCreateSingleton<PlayerInfluenceConfig>(em, ref _eInfluence);
        if (!em.HasBuffer<PlayerInfluenceElement>(ie)) em.AddBuffer<PlayerInfluenceElement>(ie);
        var buf = em.GetBuffer<PlayerInfluenceElement>(ie);
        buf.Clear();
        for (int i = 0; i < 8; i++)
            buf.Add(new PlayerInfluenceElement
            {
                Influence = (PlayerInfluence != null && i < PlayerInfluence.Length) ? PlayerInfluence[i] : 1f,
                Team      = (PlayerTeam      != null && i < PlayerTeam.Length)      ? PlayerTeam[i]      : i,
            });
    }
}
