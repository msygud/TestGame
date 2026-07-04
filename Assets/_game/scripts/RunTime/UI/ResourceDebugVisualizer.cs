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

        // GC 방지 캐시(2026-07-05) — OnGUI는 프레임당 2회+ 불리고 자원 셀 수만큼 반복되므로
        //   쿼리·라벨 문자열·타입 색을 매 호출 만들면 관리형 쓰레기가 대량 발생(주기적 GC 스파이크).
        //   라벨은 (TypeId, Amount)가 바뀔 때만 재조립(채취 시에만), 색은 TypeId당 1회.
        World       _qWorld;
        EntityQuery _lq, _sq;
        struct CachedLabel { public int TypeId, Amount; public string Text; }
        readonly System.Collections.Generic.Dictionary<int2, CachedLabel> _labels = new();
        readonly System.Collections.Generic.Dictionary<int, Color>        _colors = new();

        void OnGUI()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { _qWorld = null; return; }
            var em = world.EntityManager;

            if (!ReferenceEquals(_qWorld, world))
            {
                _qWorld = world;
                _lq = em.CreateEntityQuery(typeof(GridLayers));    // 월드당 1회
                _sq = em.CreateEntityQuery(typeof(GridSettings));
                _labels.Clear(); _colors.Clear();
            }
            if (_lq.IsEmpty || _sq.IsEmpty) return;
            var layers = _lq.GetSingleton<GridLayers>();
            var cs     = _sq.GetSingleton<GridSettings>().CellSize;
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

                if (!_labels.TryGetValue(cell, out var cl)
                    || cl.TypeId != rc.TypeId || cl.Amount != rc.Amount)
                {
                    cl = new CachedLabel
                    { TypeId = rc.TypeId, Amount = rc.Amount, Text = $"T{rc.TypeId}\n{rc.Amount}" };
                    _labels[cell] = cl;
                }
                if (!_colors.TryGetValue(rc.TypeId, out var col))
                {
                    col = TypeColor(rc.TypeId);
                    _colors[rc.TypeId] = col;
                }

                var rect = new Rect(sp.x - 26f, Screen.height - sp.y - 14f, 52f, 28f);
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = col;
                GUI.Box(rect, cl.Text, _style);
                GUI.backgroundColor = prev;
            }
            keys.Dispose();
        }

        // TypeId → 색상. ResourceCatalog(Resources/ResourceCatalog) 가 있으면
        // 거기 정의된 색을 쓰고, 없으면 HSV hue 회전 폴백.
        static Color TypeColor(int typeId)
        {
            var catalog = ResourceCatalog.LoadRuntime();
            if (catalog != null && catalog.TryGet(typeId, out var e))
            {
                var col = e.EditorColor;
                col.a = 0.9f;
                return col;
            }

            float hue = (typeId * 0.137f) % 1f;
            if (hue < 0f) hue += 1f;
            var c = Color.HSVToRGB(hue, 0.65f, 0.95f);
            c.a = 0.9f;
            return c;
        }
    }
}
