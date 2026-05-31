using UnityEditor;
using UnityEngine;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  MapBoundaryGizmo
    //
    //  SceneView에 맵 바운더리(가로/세로 영역)와 그리드를 그린다.
    //  맵은 원점(0, 0, 0)을 기준으로 +X, +Z 방향으로 확장.
    //
    //  표시 요소:
    //    - 외곽 사각형 (강한 색)
    //    - 셀 그리드 (옅은 색)
    //    - 원점 표시
    //    - 크기 라벨
    // ══════════════════════════════════════════════════════════════
    public static class MapBoundaryGizmo
    {
        static readonly Color BoundaryColor = new(0.2f, 0.9f, 0.4f, 1f);
        static readonly Color GridColor     = new(0.4f, 0.4f, 0.4f, 0.4f);
        static readonly Color OriginColor   = new(1f, 0.3f, 0.3f, 1f);

        /// <summary>맵 바운더리를 SceneView에 그린다.</summary>
        public static void Draw(MapSettings settings)
        {
            float w = settings.Width  * settings.CellSize;
            float h = settings.Height * settings.CellSize;

            DrawOuterBoundary(w, h);
            DrawGrid(settings, w, h);
            DrawOrigin();
            DrawSizeLabel(settings, w, h);
        }

        static void DrawOuterBoundary(float w, float h)
        {
            Handles.color = BoundaryColor;
            Vector3 p0 = new(0, 0, 0);
            Vector3 p1 = new(w, 0, 0);
            Vector3 p2 = new(w, 0, h);
            Vector3 p3 = new(0, 0, h);
            Handles.DrawLine(p0, p1, 3f);
            Handles.DrawLine(p1, p2, 3f);
            Handles.DrawLine(p2, p3, 3f);
            Handles.DrawLine(p3, p0, 3f);
        }

        static void DrawGrid(MapSettings settings, float w, float h)
        {
            Handles.color = GridColor;
            float cs = settings.CellSize;

            // 세로 선 (X축 따라)
            for (int i = 1; i < settings.Width; i++)
            {
                float x = i * cs;
                Handles.DrawLine(new Vector3(x, 0, 0), new Vector3(x, 0, h));
            }

            // 가로 선 (Z축 따라)
            for (int j = 1; j < settings.Height; j++)
            {
                float z = j * cs;
                Handles.DrawLine(new Vector3(0, 0, z), new Vector3(w, 0, z));
            }
        }

        static void DrawOrigin()
        {
            Handles.color = OriginColor;
            Handles.SphereHandleCap(0, Vector3.zero, Quaternion.identity, 0.3f, EventType.Repaint);
        }

        static void DrawSizeLabel(MapSettings settings, float w, float h)
        {
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = BoundaryColor },
                fontSize = 12,
            };
            string text = $"{settings.Width} × {settings.Height} cells\n" +
                          $"({w:0.#} × {h:0.#} units)";
            Handles.Label(new Vector3(w * 0.5f, 0, h + 1f), text, style);
        }
    }
}
