using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  ResourceDebugVisualizer — 자원 테스트 표시 (임시)
    //
    //  자원 프리팹 비주얼이 아직 없어서, ResourceLayer에 등록된 셀을
    //  화면에 종류별 색 + 양(숫자) 박스로 오버레이한다.
    //
    //  에디터/개발 빌드에서 자동 생성 — 별도 씬 와이어링 불필요.
    //  실제 자원 비주얼(프리팹)이 들어오면 이 파일은 삭제하면 된다.
    // ══════════════════════════════════════════════════════════════
    public class ResourceDebugVisualizer : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static bool _created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (_created) return;
            _created = true;
            var go = new GameObject("[ResourceDebugVisualizer]");
            go.AddComponent<ResourceDebugVisualizer>();
            DontDestroyOnLoad(go);
        }
#endif
        GUIStyle _style;

        void OnGUI()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;

            var lq = em.CreateEntityQuery(typeof(GridLayers));
            var sq = em.CreateEntityQuery(typeof(GridSettings));
            if (lq.IsEmpty || sq.IsEmpty) { lq.Dispose(); sq.Dispose(); return; }
            var layers = lq.GetSingleton<GridLayers>();
            var cs     = sq.GetSingleton<GridSettings>().CellSize;
            lq.Dispose(); sq.Dispose();
            if (!layers.ResourceLayer.IsCreated || cs <= 0f) return;

            var cam = Camera.main;
            if (cam == null) return;

            _style ??= new GUIStyle(GUI.skin.box)
            {
                fontSize  = 11,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };

            var keys = layers.ResourceLayer.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
            {
                var cell = keys[i];
                var rc   = layers.ResourceLayer[cell];

                var world3 = new Vector3((cell.x + 0.5f) * cs, 0f, (cell.y + 0.5f) * cs);
                Vector3 sp = cam.WorldToScreenPoint(world3);
                if (sp.z <= 0f) continue;   // 카메라 뒤

                var rect = new Rect(sp.x - 26f, Screen.height - sp.y - 14f, 52f, 28f);
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = TypeColor(rc.TypeId);
                GUI.Box(rect, $"T{rc.TypeId}\n{rc.Amount}", _style);
                GUI.backgroundColor = prev;
            }
            keys.Dispose();
        }

        // TypeId → 결정적 색상 (HSV hue 회전). 종류별로 구분되게.
        static Color TypeColor(int typeId)
        {
            float hue = (typeId * 0.137f) % 1f;
            if (hue < 0f) hue += 1f;
            var c = Color.HSVToRGB(hue, 0.65f, 0.95f);
            c.a = 0.9f;
            return c;
        }
    }
}
