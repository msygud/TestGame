using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritoryOutlineRenderSystem — 영역 경계 아웃라인 상시 렌더 (소유팀 색)
    // ──────────────────────────────────────────────────────────────────────────
    //  GridLayers.TerritoryLayer를 읽어, '소유자가 다른(또는 영역 아님) 이웃과
    //  맞닿은 변'만 경계로 렌더 → 각 영역의 외곽선이 그려진다.
    //    · 팀 경계  = 소유팀 색, 얇은 라인(상시).
    //    · 경합지(-2) 경계 = 흰색, 폭 있는 띠(셀 10%, 상시).
    //    · 경합지 사선 해치 = 가는 대각선 1줄/셀(F8 토글, 기본 OFF) — 켜면 '분쟁지' 줄무늬.
    //  (셀마다 4변 다 그리지 않고 경계 변만 → 깔끔 + 라인 수 절감.)
    //
    //  렌더 경로: 동적 Mesh + Graphics.DrawMesh (GL 즉시모드/endCameraRendering 폐기).
    //    이유 — Unity 6 URP(17)는 Screen Space Overlay UI를 '파이프라인 내부 패스'로 그린 뒤
    //    endCameraRendering 콜백이 실행돼, 거기서 GL을 그리면 UI '위'로 올라온다.
    //    머티리얼 renderQueue=Transparent로 두면 투명 패스(=UI 패스보다 앞)에서 그려져
    //    Overlay UI가 위에 온다(#UI). ZTest=LessEqual이라 유닛/건물이 앞이면 가린다(#깊이).
    //  팀 경계/경합지 띠는 정식 기능(상시), 사선 해치만 F8 토글.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class TerritoryOutlineRenderSystem : SystemBase
    {
        Material _mat;
        Mesh     _lineMesh;   // 팀 경계 + 경합지 해치 (MeshTopology.Lines)
        Mesh     _bandMesh;   // 경합지 띠 (삼각형 — 폭 있는 strip)
        bool     _hatchOn;    // F8 = 경합지 사선 해치 토글(기본 OFF)

        // 메시 빌드 버퍼(재사용 — 매 프레임 Clear 후 재충전).
        readonly List<Vector3> _lv = new(1024);
        readonly List<Color>   _lc = new(1024);
        readonly List<int>     _li = new(1024);
        readonly List<Vector3> _bv = new(512);
        readonly List<Color>   _bc = new(512);
        readonly List<int>     _bi = new(768);

        // 경합지 강조색 (불투명 흰색 — 팀색/중립과 셋 구분 유지하되 띠 두께로 두드러지게).
        static readonly Color ContestedColor = new Color(1f, 1f, 1f, 1f);
        // 경합지 사선 해치색 (가는 라인 — 띠보다 옅게, '분쟁지' 느낌).
        static readonly Color HatchColor = new Color(1f, 1f, 1f, 0.5f);

        // 소유자(LocalId)별 색 (TerritoryDebugSystem과 동일 팔레트).
        static readonly Color[] OwnerColors =
        {
            new Color(0.20f, 0.55f, 1.00f, 1f), // 0 파랑
            new Color(0.95f, 0.30f, 0.30f, 1f), // 1 빨강
            new Color(0.30f, 0.85f, 0.45f, 1f), // 2 초록
            new Color(0.95f, 0.85f, 0.25f, 1f), // 3 노랑
            new Color(0.80f, 0.40f, 0.90f, 1f), // 4 보라
            new Color(0.30f, 0.85f, 0.90f, 1f), // 5 청록
            new Color(1.00f, 0.55f, 0.15f, 1f), // 6 주황
            new Color(0.85f, 0.85f, 0.85f, 1f), // 7 흰
        };

        // 4-이웃 오프셋.
        static readonly int2[] NB = { new int2(0, 1), new int2(1, 0), new int2(0, -1), new int2(-1, 0) };

        protected override void OnCreate()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetColor("_Color", Color.white);   // 최종색 = 정점색 × _Color
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            // LessEqual — 월드 깊이 존중. 유닛/건물이 앞에 있으면 오버레이를 가린다.
            _mat.SetInt("_ZTest",  (int)CompareFunction.LessEqual);
            // 투명 큐 — 투명 패스(Overlay UI 패스보다 앞)에서 그려져 UI가 위에 온다.
            _mat.renderQueue = (int)RenderQueue.Transparent;

            _lineMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _bandMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _lineMesh.MarkDynamic();
            _bandMesh.MarkDynamic();

            RequireForUpdate<GridLayers>();
            RequireForUpdate<GridSettings>();
        }

        protected override void OnDestroy()
        {
            if (_mat != null)      Object.DestroyImmediate(_mat);
            if (_lineMesh != null) Object.DestroyImmediate(_lineMesh);
            if (_bandMesh != null) Object.DestroyImmediate(_bandMesh);
        }

        protected override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f8Key.wasPressedThisFrame) _hatchOn = !_hatchOn;

            _lv.Clear(); _lc.Clear(); _li.Clear();
            _bv.Clear(); _bc.Clear(); _bi.Clear();

            var settings = SystemAPI.GetSingleton<GridSettings>();
            float cs = settings.CellSize;
            if (cs <= 0f) { Flush(); return; }
            float halfW = cs * 0.10f * 0.5f;   // 경합지 띠 폭 = 셀의 10%

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) { Flush(); return; }
            bool hasTerrain = layers.TerrainLayer.IsCreated;

            foreach (var kv in layers.TerritoryLayer)
            {
                int2 cell = kv.Key;
                int  v    = kv.Value;

                bool contested = v == TerritoryOps.Contested;
                Color col;
                if (contested)                          col = ContestedColor;
                else if ((uint)v < OwnerColors.Length)  col = OwnerColors[v];
                else                                    continue;

                float h = (hasTerrain && layers.TerrainLayer.TryGetValue(cell, out var tc))
                    ? tc.Height * cs : 0f;
                h += contested ? 0.12f : 0.06f;   // 경합지 띠를 팀 라인 위로 살짝 띄움

                float x0 = cell.x * cs, x1 = x0 + cs;
                float z0 = cell.y * cs, z1 = z0 + cs;

                for (int d = 0; d < 4; d++)
                {
                    // 같은 값(팀/경합) 이웃과 맞닿은 변은 내부 → 스킵. 다르면 경계 변 → 그림.
                    if (layers.TerritoryLayer.TryGetValue(cell + NB[d], out int no) && no == v)
                        continue;

                    Vector3 a, b;
                    switch (d)
                    {
                        case 0: a = new Vector3(x0, h, z1); b = new Vector3(x1, h, z1); break; // N
                        case 1: a = new Vector3(x1, h, z0); b = new Vector3(x1, h, z1); break; // E
                        case 2: a = new Vector3(x0, h, z0); b = new Vector3(x1, h, z0); break; // S
                        default:a = new Vector3(x0, h, z0); b = new Vector3(x0, h, z1); break; // W
                    }

                    if (contested) AddBand(a, b, halfW, col);   // 폭 있는 띠(삼각형)
                    else           AddLine(a, b, col);          // 얇은 라인
                }

                // 경합지 사선 해치(F8 토글) — 셀 대각선 1줄(가는 라인). 인접 셀과 이어져
                //   45° 줄무늬가 되어 '분쟁지'로 읽힌다. 기본 OFF, F8로 켜고 끈다.
                if (contested && _hatchOn)
                    AddLine(new Vector3(x0, h, z0), new Vector3(x1, h, z1), HatchColor);
            }

            Flush();
        }

        // ── 메시 빌드 헬퍼 ──────────────────────────────────────────────────────
        void AddLine(Vector3 a, Vector3 b, Color c)
        {
            int i = _lv.Count;
            _lv.Add(a); _lv.Add(b);
            _lc.Add(c); _lc.Add(c);
            _li.Add(i); _li.Add(i + 1);
        }

        // 변(a→b)에 수직(XZ)으로 폭을 줘 두꺼운 띠 quad(삼각형 2개)를 만든다.
        void AddBand(Vector3 a, Vector3 b, float halfW, Color c)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            float len = math.sqrt(dx * dx + dz * dz);
            if (len < 1e-4f) return;
            float nx = -dz / len * halfW;
            float nz =  dx / len * halfW;

            int i = _bv.Count;
            _bv.Add(new Vector3(a.x - nx, a.y, a.z - nz));
            _bv.Add(new Vector3(a.x + nx, a.y, a.z + nz));
            _bv.Add(new Vector3(b.x + nx, b.y, b.z + nz));
            _bv.Add(new Vector3(b.x - nx, b.y, b.z - nz));
            _bc.Add(c); _bc.Add(c); _bc.Add(c); _bc.Add(c);
            _bi.Add(i); _bi.Add(i + 1); _bi.Add(i + 2);
            _bi.Add(i); _bi.Add(i + 2); _bi.Add(i + 3);
        }

        // 빌드된 버퍼를 메시에 올리고 DrawMesh로 큐잉(투명 패스 → UI 아래, 깊이 존중).
        void Flush()
        {
            _lineMesh.Clear();
            if (_lv.Count > 0)
            {
                _lineMesh.SetVertices(_lv);
                _lineMesh.SetColors(_lc);
                _lineMesh.SetIndices(_li, MeshTopology.Lines, 0, false);
                Graphics.DrawMesh(_lineMesh, Matrix4x4.identity, _mat, 0);
            }

            _bandMesh.Clear();
            if (_bv.Count > 0)
            {
                _bandMesh.SetVertices(_bv);
                _bandMesh.SetColors(_bc);
                _bandMesh.SetIndices(_bi, MeshTopology.Triangles, 0, false);
                Graphics.DrawMesh(_bandMesh, Matrix4x4.identity, _mat, 0);
            }
        }
    }
}
