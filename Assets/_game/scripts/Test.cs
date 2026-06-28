using CitySim;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

// 디버그 입력 — 마우스로 가리킨 건물에:
//   좌클릭 = 파괴 (RazeAreaCommand → RazeSystem이 점유 해제).
//   우클릭 = '거주건물'로 지정 (ResidenceBuilding + BuildingOccupancy{Capacity=50}).
//            → TerritorySystem이 1초마다 전체 재계산해 그 건물 중심으로 영역을 그린다.
//            영역 확인: F7(TerritoryDebugSystem 오버레이). PopPerCell 필드로 영역 크기 조절.
//   ※ 프로덕션은 프리팹에 BuildingAuthoring(Kind=Residence, Capacity)로 베이크하는 게 정석.
//     우클릭 태깅은 프리팹 미설정 상태에서 영역 파이프라인을 즉시 검증하기 위한 테스트용.
public class Test : MonoBehaviour
{
    [Header("Territory 테스트")]
    [Tooltip("셀 1칸 점유에 필요한 인구(float). 영역 셀 수 = floor(거주건물 인구 / 이 값). "
           + "매 프레임 TerritoryConfig 싱글톤에 반영 → TerritorySystem이 1초마다 전체 재계산.")]
    public float PopPerCell = 5f;

    [Tooltip("우클릭 태깅 시, BuildingOccupancy가 '없는' 건물에만 줄 정원(인구). "
           + "이미 베이킹된 BuildingOccupancy가 있으면 그 값을 존중(덮어쓰지 않음).")]
    public int TestResidenceCapacity = 10;

    void Update()
    {
        SyncTerritoryConfig();

        var mouse = Mouse.current;
        if (mouse == null) return;
        bool raze = mouse.leftButton.wasPressedThisFrame;
        bool tag  = mouse.rightButton.wasPressedThisFrame;
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

        if (target == Entity.Null) { Debug.Log($"[Test] cell {cell} — 건물 없음"); return; }

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

    // PopPerCell을 TerritoryConfig 싱글톤에 반영(없으면 생성). 매 프레임 — 인스펙터 변경 즉시 반영.
    void SyncTerritoryConfig()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;

        var q = em.CreateEntityQuery(typeof(TerritoryConfig));
        Entity e = q.IsEmpty ? em.CreateEntity(typeof(TerritoryConfig)) : q.GetSingletonEntity();
        q.Dispose();

        em.SetComponentData(e, new TerritoryConfig
        {
            PopPerCell = Mathf.Max(0.01f, PopPerCell),
            MaxRadius  = 64,
        });
    }
}
