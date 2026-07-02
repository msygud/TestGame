using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CaptureDoomWarningSystem — 파괴 예정(CaptureDoom) 구조물 경고 오버레이
    // ──────────────────────────────────────────────────────────────────────────
    //  타팀 영토에 놓여 dwell 카운트다운 중인 건물/도로의 footprint에:
    //    · 펄스 점멸 테두리 띠 — 남은 시간이 줄수록 점멸이 빨라지고 주황→빨강.
    //    · 옅은 채움 펄스 — footprint 전체가 위험 상태임을 표시.
    //  영토가 회복되면 CaptureDoom이 사면(제거)돼 자동으로 사라진다.
    //  렌더: 동적 Mesh + Graphics.DrawMesh(renderQueue=Transparent → HUD UI 아래),
    //  ZTest=LessEqual(유닛/건물이 앞이면 가림). Territory 오버레이와 동일 패턴.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class CaptureDoomWarningSystem : SystemBase
    {
        Material _mat;
        Mesh     _mesh;

        readonly List<Vector3> _v = new(512);
        readonly List<Color>   _c = new(512);
        readonly List<int>     _i = new(768);

        static readonly Color SafeColor   = new Color(1.00f, 0.60f, 0.10f, 1f);   // 주황 (여유)
        static readonly Color DangerColor = new Color(1.00f, 0.12f, 0.08f, 1f);   // 빨강 (임박)

        protected override void OnCreate()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetColor("_Color", Color.white);
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest",  (int)CompareFunction.LessEqual);
            _mat.renderQueue = (int)RenderQueue.Transparent;

            _mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();

            RequireForUpdate<CaptureDoom>();
            RequireForUpdate<GridSettings>();
            RequireForUpdate<GameClock>();
        }

        protected override void OnDestroy()
        {
            if (_mat != null)  Object.DestroyImmediate(_mat);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
        }

        protected override void OnUpdate()
        {
            _v.Clear(); _c.Clear(); _i.Clear();

            float cs = SystemAPI.GetSingleton<GridSettings>().CellSize;
            if (cs <= 0f) { _mesh.Clear(); return; }
            var clock = SystemAPI.GetSingleton<GameClock>();
            double now = clock.TotalSeconds;
            float  rt  = (float)SystemAPI.Time.ElapsedTime;   // 점멸은 현실 시간(일시정지에도 시각 유지)

            bool hasTerrain = SystemAPI.TryGetSingleton<GridLayers>(out var layers)
                              && layers.TerrainLayer.IsCreated;

            float bandW = cs * 0.12f;

            foreach (var (doomRO, e) in SystemAPI.Query<RefRO<CaptureDoom>>().WithEntityAccess())
            {
                // footprint 해상 — 건물 또는 도로.
                int2 origin; int2 eff;
                if (SystemAPI.HasComponent<BuildingFootprint>(e))
                {
                    var bf = SystemAPI.GetComponent<BuildingFootprint>(e);
                    origin = bf.Origin;
                    eff    = EntranceOps.RotateSize(bf.Size, bf.RotSteps);
                }
                else if (SystemAPI.HasComponent<Road>(e))
                {
                    var road = SystemAPI.GetComponent<Road>(e);
                    origin = road.FootprintOrigin;
                    int s  = math.max(1, (int)road.Size);
                    eff    = new int2(s, s);
                }
                else continue;

                var doom = doomRO.ValueRO;
                float dwell    = (float)math.max(0.01, doom.DwellSeconds);
                float remain01 = math.saturate((float)((doom.DeadlineSeconds - now) / dwell));
                float danger   = 1f - remain01;                       // 0=여유 → 1=임박

                // 점멸: 임박할수록 빠르게 (1.2Hz → 5Hz).
                float freq  = math.lerp(1.2f, 5f, danger);
                float pulse = 0.5f + 0.5f * math.sin(rt * freq * 2f * math.PI);

                Color col = Color.Lerp(SafeColor, DangerColor, danger);

                float h = (hasTerrain && layers.TerrainLayer.TryGetValue(origin, out var tc))
                    ? tc.Height * cs : 0f;
                h += 0.15f;   // 영역 띠(0.12)보다 위

                float x0 = origin.x * cs, x1 = (origin.x + eff.x) * cs;
                float z0 = origin.y * cs, z1 = (origin.y + eff.y) * cs;

                // 옅은 채움 펄스 (footprint 전체 위험 표시).
                Color fill = col; fill.a = math.lerp(0.06f, 0.22f, pulse);
                AddQuad(new Vector3(x0, h, z0), new Vector3(x0, h, z1),
                        new Vector3(x1, h, z1), new Vector3(x1, h, z0), fill);

                // 펄스 테두리 띠 (4변, 안쪽으로 bandW).
                Color band = col; band.a = math.lerp(0.35f, 0.95f, pulse);
                AddQuad(new Vector3(x0, h, z1 - bandW), new Vector3(x0, h, z1),
                        new Vector3(x1, h, z1), new Vector3(x1, h, z1 - bandW), band);   // N
                AddQuad(new Vector3(x0, h, z0), new Vector3(x0, h, z0 + bandW),
                        new Vector3(x1, h, z0 + bandW), new Vector3(x1, h, z0), band);   // S
                AddQuad(new Vector3(x0, h, z0), new Vector3(x0, h, z1),
                        new Vector3(x0 + bandW, h, z1), new Vector3(x0 + bandW, h, z0), band); // W
                AddQuad(new Vector3(x1 - bandW, h, z0), new Vector3(x1 - bandW, h, z1),
                        new Vector3(x1, h, z1), new Vector3(x1, h, z0), band);           // E
            }

            _mesh.Clear();
            if (_v.Count > 0)
            {
                _mesh.SetVertices(_v);
                _mesh.SetColors(_c);
                _mesh.SetIndices(_i, MeshTopology.Triangles, 0, false);
                Graphics.DrawMesh(_mesh, Matrix4x4.identity, _mat, 0);
            }
        }

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
        {
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _i.Add(i); _i.Add(i + 1); _i.Add(i + 2);
            _i.Add(i); _i.Add(i + 2); _i.Add(i + 3);
        }
    }
}
