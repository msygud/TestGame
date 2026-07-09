#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DemandHeatmapSystem — 미충족 수요 히트맵 (에디터/개발빌드, F12 토글, 기본 OFF)
    // ──────────────────────────────────────────────────────────────────────────
    //  DemandField(수요셀·욕구별 실패 누적)를 색으로 채운다. "어디에 무엇이 왜
    //  부족한가"를 눈으로 확인 — 욕구 주도 배치의 입력 신호 검증.
    //  색조 = 욕구(Hunger=주황) / 알파 = 실패 강도 / 파란색 편이 = Reached 비율
    //  (선명 = NoCoverage = 신설 / 파랑 = Reached = 기존 공급 보강).
    //
    //  렌더: 동적 Mesh + Graphics.DrawMesh(Transparent) — TerritoryDebug와 동일 패턴.
    //  버전 게이트: DemandField.Version 바뀔 때만 메시 재구축(매 프레임 재구축 방지).
    //  ※ 대량 셀 → indexFormat UInt32(정점 65,535 초과 시 랩 깨짐 방지).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class DemandHeatmapSystem : SystemBase
    {
        Material _mat;
        Mesh     _mesh;
        bool     _enabled;                     // F12
        uint     _builtVersion = uint.MaxValue;
        bool     _builtEnabled;

        readonly List<Vector3> _v = new(1024);
        readonly List<Color>   _c = new(1024);
        readonly List<int>     _i = new(1536);

        const int   IntensityCap = 15;         // 상대 정규화 하한(이 값 미만이면 옅게, 이상은 최댓값 기준)
        const float MaxAlpha     = 0.6f;

        // resource id → 색. 0~63=욕구 비트 / 64+=commodity 수요(공급자 상류 부족, 진단용 구분색).
        static Color NeedColor(int res) => res switch
        {
            0  => new Color(1.00f, 0.45f, 0.10f, 1f),   // Hunger 주황
            1  => new Color(0.60f, 0.40f, 0.90f, 1f),   // Homeless 보라
            2  => new Color(0.30f, 0.60f, 1.00f, 1f),   // Unemployed 파랑
            3  => new Color(0.95f, 0.30f, 0.55f, 1f),   // LowEntertainment 분홍
            65  => new Color(0.30f, 0.85f, 0.35f, 1f),   // Grain 수요(64+1) — 농장 필요(초록)
            66  => new Color(0.95f, 0.80f, 0.20f, 1f),   // Flour 수요(64+2) — 제분소 필요(노랑)
            128 => new Color(0.25f, 0.80f, 0.85f, 1f),   // 창고 수요 — offload 불가(청록)
            _   => new Color(0.90f, 0.90f, 0.90f, 1f),
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

            RequireForUpdate<DemandField>();
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
            if (kb != null && kb.f12Key.wasPressedThisFrame) _enabled = !_enabled;

            uint ver = SystemAPI.TryGetSingleton<DemandField>(out var df) ? df.Version : 0;
            if (ver == _builtVersion && _enabled == _builtEnabled)
            {
                if (_enabled && _mesh.vertexCount > 0)
                    Graphics.DrawMesh(_mesh, Matrix4x4.identity, _mat, 0);
                return;
            }
            _builtVersion = ver; _builtEnabled = _enabled;

            _v.Clear(); _c.Clear(); _i.Clear();
            if (!_enabled || !df.Stats.IsCreated) { _mesh.Clear(); return; }

            float cs = SystemAPI.GetSingleton<GridSettings>().CellSize;
            if (cs <= 0f) { _mesh.Clear(); return; }
            float cell = DemandGrid.CellSize * cs;   // 수요셀 월드 변

            // 누적이라 고정 상한은 곧 포화 → 현재 필드 최댓값 기준 상대 정규화(항상 가독).
            //   소수 실패는 옅게 유지: denom = max(IntensityCap, 현재 최대). (재구축은 1초 1회.)
            int maxFail = 0;
            foreach (var kv in df.Stats)
                if (kv.Value.Failures > maxFail) maxFail = kv.Value.Failures;
            float denom = math.max(IntensityCap, maxFail);

            foreach (var kv in df.Stats)
            {
                var stat  = kv.Value;
                int total = stat.Failures;
                if (total <= 0) continue;

                int2 dcell = new int2(kv.Key.y, kv.Key.z);   // key = (owner, dx, dy, bit)
                int  bit   = kv.Key.w;

                int2 ro = DemandGrid.ToRealOrigin(dcell);
                float x0 = ro.x * cs, x1 = x0 + cell;
                float z0 = ro.y * cs, z1 = z0 + cell;
                const float h = 0.06f;

                // 색조=욕구 / 알파=실패 강도 / 파란색 편이=Reached 비율(기존 보강 신호).
                Color col = NeedColor(bit);
                float reachedFrac = stat.FailReached / (float)total;
                col = Color.Lerp(col, new Color(0.20f, 0.55f, 1.00f, 1f), 0.7f * reachedFrac);
                col.a = MaxAlpha * math.min(1f, total / denom);

                int b = _v.Count;
                _v.Add(new Vector3(x0, h, z0));
                _v.Add(new Vector3(x0, h, z1));
                _v.Add(new Vector3(x1, h, z1));
                _v.Add(new Vector3(x1, h, z0));
                _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
                _i.Add(b); _i.Add(b + 1); _i.Add(b + 2);
                _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
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
    }
}
#endif
