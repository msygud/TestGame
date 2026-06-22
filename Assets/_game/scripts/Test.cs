using CitySim;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

// 파괴 테스트 — 마우스 좌클릭으로 가리킨 건물 1개를 파괴.
//   클릭 셀을 포함하는 건물의 footprint를 RazeAreaCommand로 발행
//   → RazeSystem이 그 건물 파괴(점유 해제) + 주변 orphan 도로 정리.
//   한 구역의 건물을 하나씩 부수면, 그 도로가 더는 라이브 건물에 안 닿는 순간 풀려 사라진다
//   (구역이 비면 링이 끊기고 leaf-prune으로 풀려나감).
public class Test : MonoBehaviour
{
    void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

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

        // 클릭 셀을 포함하는 건물 footprint 찾기
        var bq  = em.CreateEntityQuery(ComponentType.ReadOnly<BuildingFootprint>());
        var arr = bq.ToComponentDataArray<BuildingFootprint>(Allocator.Temp);
        bool found = false; int2 mn = default, mx = default;
        foreach (var bf in arr)
        {
            int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);
            if (cell.x >= bf.Origin.x && cell.x < bf.Origin.x + eff.x &&
                cell.y >= bf.Origin.y && cell.y < bf.Origin.y + eff.y)
            {
                found = true; mn = bf.Origin; mx = bf.Origin + eff - 1; break;
            }
        }
        arr.Dispose(); bq.Dispose();

        if (!found) { Debug.Log($"[RazeTest] cell {cell} — 건물 없음"); return; }

        var e = em.CreateEntity();
        em.AddComponentData(e, new RazeAreaCommand { Min = mn, Max = mx });
        Debug.Log($"[RazeTest] 건물 파괴 {mn} ~ {mx}");
    }
}
