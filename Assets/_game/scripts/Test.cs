using CitySim;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

// 디버그 입력 — 마우스로 가리킨 건물에:
//   좌클릭 = 파괴 (RazeAreaCommand → RazeSystem이 점유 해제).
//   우클릭 = '거주건물'로 지정 (ResidenceBuilding + BuildingOccupancy{Capacity=50}).
//            → TerritorySystem이 다음 게임시간(HourChanged)에 그 건물 중심으로 영역을 그린다.
//            영역 확인: F7(TerritoryDebugSystem 오버레이). 시간이 흘러야 갱신되니
//            GameClockHud 배속(최대 120x)으로 한 시간 넘기면 즉시 보인다.
//   ※ 프로덕션은 프리팹에 BuildingAuthoring(Kind=Residence, Capacity)로 베이크하는 게 정석.
//     우클릭 태깅은 프리팹 미설정 상태에서 영역 파이프라인을 즉시 검증하기 위한 테스트용.
public class Test : MonoBehaviour
{
    void Update()
    {
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

        if (em.HasComponent<BuildingOccupancy>(target))
        {
            var occ = em.GetComponentData<BuildingOccupancy>(target);
            occ.Capacity = math.max(occ.Capacity, 50);
            em.SetComponentData(target, occ);
        }
        else
        {
            em.AddComponentData(target, new BuildingOccupancy { Current = 0, Capacity = 50 });
        }
        Debug.Log($"[Test] 거주건물 지정 (Capacity≥50) @ {mn} — F7로 영역 확인, 시간 진행(배속) 필요");
    }
}
