#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DistrictOverlaySystem — 지구 그리드 시각화 (에디터/개발빌드, 2026-07-11)
    // ──────────────────────────────────────────────────────────────────────────
    //  F5 토글: ① 지구 경계선(회색 격자) ② 중앙 인프라 슬롯 8×8(흰색 채움)
    //           ③ 팀별 확장 목표 지구(팀 색 채움 — AiCityGrowth가 발행한 Targets).
    //  "창고·서비스가 슬롯에 앉는지 / 확장이 목표 지구로 향하는지"를 눈으로 확인.
    //
    //  렌더: 동적 Mesh + Graphics.DrawMesh(Transparent) — CoverageOverlay와 동일 패턴.
    //  재구축: DistrictTable.Version 변화 시에만(일 1회 발행이라 사실상 무비용).
    //
    //  F키 맵: F5 지구 / F6 커버리지 / F7 영역 / F8 해치 / F9 자원 / F10 시민 / F11 건물 / F12 수요.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class DistrictOverlaySystem : SystemBase
    {
        Material _mat;
        Mesh     _mesh;
        bool     _enabled;                    // F5
        uint     _builtVersion = uint.MaxValue;

        readonly List<Vector3> _v = new(4096);
        readonly List<Color>   _c = new(4096);
        readonly List<int>     _i = new(6144);

        static readonly Color CBorder = new(0.45f, 0.45f, 0.45f, 0.30f);   // 지구 경계선
        static readonly Color CSlot   = new(1.00f, 1.00f, 1.00f, 0.14f);   // 중앙 인프라 슬롯

        // 팀별 목표 지구 색(LocalId 0~7).
        static readonly Color[] CTeam =
        {
            new(0.95f, 0.30f, 0.25f, 0.20f), new(0.25f, 0.45f, 0.95f, 0.20f),
            new(0.95f, 0.85f, 0.25f, 0.20f), new(0.60f, 0.30f, 0.90f, 0.20f),
            new(0.95f, 0.55f, 0.20f, 0.20f), new(0.25f, 0.85f, 0.85f, 0.20f),
            new(0.90f, 0.40f, 0.75f, 0.20f), new(0.35f, 0.85f, 0.35f, 0.20f),
        };

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
            _mesh.indexFormat = IndexFormat.UInt32;
            _mesh.MarkDynamic();

            RequireForUpdate<DistrictTable>();
            RequireForUpdate<GridSettings>();
        }

        protected override void OnDestroy()
        {
            if (_mat != null)  Object.DestroyImmediate(_mat);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
        }

        protected override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f5Key.wasPressedThisFrame) _enabled = !_enabled;
            if (!_enabled) return;

            float cs = SystemAPI.GetSingleton<GridSettings>().CellSize;
            if (cs <= 0f) return;
            var dt = SystemAPI.GetSingleton<DistrictTable>();
            if (!dt.Stats.IsCreated) return;

            if (dt.Version != _builtVersion)
            {
                _builtVersion = dt.Version;
                Build(in dt, cs);
            }
            if (_mesh.vertexCount > 0)
                Graphics.DrawMesh(_mesh, Matrix4x4.identity, _mat, 0);
        }

        void Build(in DistrictTable dt, float cs)
        {
            _v.Clear(); _c.Clear(); _i.Clear();
            int P = DistrictGrid.Pitch, S = DistrictGrid.SlotSize;
            float border = 0.2f;   // 경계선 두께(셀)

            foreach (var kv in dt.Stats)
            {
                int2 ro = DistrictGrid.ToRealOrigin(kv.Key);
                // 경계선: 서(W)·남(S) 변만 — 이웃 지구와 합쳐 완전한 격자가 된다.
                AddRect(ro.x, ro.y, border, P, cs, 0.045f, CBorder);            // W
                AddRect(ro.x, ro.y, P, border, cs, 0.045f, CBorder);            // S
                // 중앙 인프라 슬롯.
                int2 so = DistrictGrid.SlotOrigin(kv.Key);
                AddRect(so.x, so.y, S, S, cs, 0.04f, CSlot);
            }

            if (dt.Targets.IsCreated)
                foreach (var kv in dt.Targets)
                {
                    if ((uint)kv.Key >= 8) continue;
                    int2 ro = DistrictGrid.ToRealOrigin(kv.Value);
                    AddRect(ro.x, ro.y, P, P, cs, 0.035f, CTeam[kv.Key]);
                }

            _mesh.Clear();
            if (_v.Count > 0)
            {
                _mesh.SetVertices(_v);
                _mesh.SetColors(_c);
                _mesh.SetIndices(_i, MeshTopology.Triangles, 0, false);
            }
        }

        void AddRect(float cellX, float cellZ, float w, float h, float cs, float y, Color col)
        {
            float x0 = cellX * cs, x1 = (cellX + w) * cs;
            float z0 = cellZ * cs, z1 = (cellZ + h) * cs;
            int b = _v.Count;
            _v.Add(new Vector3(x0, y, z0));
            _v.Add(new Vector3(x0, y, z1));
            _v.Add(new Vector3(x1, y, z1));
            _v.Add(new Vector3(x1, y, z0));
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _i.Add(b); _i.Add(b + 1); _i.Add(b + 2);
            _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
        }
    }
}
#endif
