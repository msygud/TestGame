using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using Game.Unit;   // CombatHealth, CombatTargetBounds

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  HealthBarRenderSystem — CombatHealth를 가진 엔티티 위에 HP바 (유닛·건물 공용)
    // ──────────────────────────────────────────────────────────────────────────
    //  엔티티마다 카메라를 향하는 빌보드 바 2장(어두운 배경 + 체력 비례 채움)을
    //  엔티티 위에 띄운다. 채움 색 = 빨강(0)→노랑(0.5)→초록(1).
    //  렌더: 동적 Mesh + Graphics.DrawMesh, renderQueue=Transparent → HUD UI 아래.
    //  ZTest=Always — HP바는 항상 읽히게(가리지 않음). (Territory/Road 오버레이와 동일 패턴.)
    //
    //  띄우는 높이: CombatTargetBounds가 있으면 그 높이, 없으면 기본값(건물 등).
    //  ⚠ 현재는 CombatHealth 가진 '모든' 엔티티에 상시 표시 → 클러스터/비용 우려 시
    //    'frac<1만 표시'(피해 입은 것만)나 '선택/호버만'으로 좁히면 됨.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class HealthBarRenderSystem : SystemBase
    {
        Material _mat;
        Mesh     _mesh;

        readonly List<Vector3> _v = new(4096);
        readonly List<Color>   _c = new(4096);
        readonly List<int>     _i = new(6144);

        // 바 치수(월드 단위).
        const float BarWidth       = 1.4f;
        const float BarHeight      = 0.18f;
        const float DefaultYOffset = 2.0f;   // 엔티티 위로 띄우는 기본 높이(bounds 없을 때)

        static readonly Color BgColor = new Color(0f, 0f, 0f, 0.55f);

        protected override void OnCreate()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetColor("_Color", Color.white);
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest",  (int)CompareFunction.Always);  // 항상 읽히게
            _mat.renderQueue = (int)RenderQueue.Transparent;       // UI 아래

            _mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();

            RequireForUpdate<CombatHealth>();
        }

        protected override void OnDestroy()
        {
            if (_mat != null)  Object.DestroyImmediate(_mat);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
        }

        protected override void OnUpdate()
        {
            _v.Clear(); _c.Clear(); _i.Clear();

            var cam = Camera.main;
            if (cam == null) { _mesh.Clear(); return; }

            float3 right   = cam.transform.right;   // 스크린-정렬 빌보드(폭)
            float3 up      = cam.transform.up;       // (높이)
            float3 worldUp = new float3(0f, 1f, 0f);

            var boundsLookup = SystemAPI.GetComponentLookup<CombatTargetBounds>(true);

            // 파괴 예정(CaptureDoom) 카운트다운 게이지용.
            var doomLookup = SystemAPI.GetComponentLookup<CaptureDoom>(true);
            double gameNow = SystemAPI.TryGetSingleton<GameClock>(out var clock)
                ? clock.TotalSeconds : 0.0;

            float halfW = BarWidth  * 0.5f;
            float halfH = BarHeight * 0.5f;

            foreach (var (healthRO, xfRO, entity) in
                     SystemAPI.Query<RefRO<CombatHealth>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                var h = healthRO.ValueRO;
                if (h.MaxHealth <= 0f) continue;
                float frac = math.saturate(h.Health / h.MaxHealth);

                // 띄우는 높이: bounds 있으면 그 높이 위, 없으면 기본.
                float yOff = boundsLookup.HasComponent(entity)
                    ? boundsLookup[entity].Size.y + 0.5f
                    : DefaultYOffset;

                float3 center = xfRO.ValueRO.Position + worldUp * yOff;
                float3 l = center - right * halfW;   // 좌단
                float3 r = center + right * halfW;   // 우단

                // 배경(전체 폭).
                AddQuad(l - up * halfH, l + up * halfH, r + up * halfH, r - up * halfH, BgColor);

                // 체력 채움(좌측 정렬, frac 폭) — 배경보다 뒤(인덱스 순서)라 위에 덮인다.
                if (frac > 0f)
                {
                    float3 fr = l + right * (BarWidth * frac);
                    Color  fc = HealthColor(frac);
                    AddQuad(l - up * halfH, l + up * halfH, fr + up * halfH, fr - up * halfH, fc);
                }

                // 파괴 예정 카운트다운 게이지 — HP바 바로 아래 얇은 바(남은 dwell 비율).
                //   주황(여유)→빨강(임박). CaptureDoom 사면 시 자동 소멸.
                if (doomLookup.HasComponent(entity))
                {
                    var doom = doomLookup[entity];
                    float dwell    = (float)math.max(0.01, doom.DwellSeconds);
                    float remain01 = math.saturate((float)((doom.DeadlineSeconds - gameNow) / dwell));

                    float3 gc = center - up * (BarHeight * 1.4f);   // HP바 아래
                    float3 gl = gc - right * halfW;
                    float3 gr = gc + right * halfW;
                    float  gh = BarHeight * 0.45f * 0.5f;           // 더 얇게

                    AddQuad(gl - up * gh, gl + up * gh, gr + up * gh, gr - up * gh, BgColor);
                    if (remain01 > 0f)
                    {
                        float3 ge = gl + right * (BarWidth * remain01);
                        Color  gcol = Color.Lerp(
                            new Color(1.00f, 0.12f, 0.08f, 0.95f),   // 임박 = 빨강
                            new Color(1.00f, 0.60f, 0.10f, 0.95f),   // 여유 = 주황
                            remain01);
                        AddQuad(gl - up * gh, gl + up * gh, ge + up * gh, ge - up * gh, gcol);
                    }
                }
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

        // 빨강(0) → 노랑(0.5) → 초록(1).
        static Color HealthColor(float f)
        {
            Color red = new Color(0.90f, 0.15f, 0.10f, 0.95f);
            Color yel = new Color(0.95f, 0.85f, 0.15f, 0.95f);
            Color grn = new Color(0.20f, 0.85f, 0.25f, 0.95f);
            return f < 0.5f ? Color.Lerp(red, yel, f * 2f)
                            : Color.Lerp(yel, grn, (f - 0.5f) * 2f);
        }

        void AddQuad(float3 a, float3 b, float3 c, float3 d, Color col)
        {
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _i.Add(i); _i.Add(i + 1); _i.Add(i + 2);
            _i.Add(i); _i.Add(i + 2); _i.Add(i + 3);
        }
    }
}
