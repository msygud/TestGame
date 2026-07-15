using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  AuraLoadHud — 오라 시설(경찰서류) 머리 위 "감당중/감당가능" 라벨 (F6 연동)
    //
    //  관리형 관측 도구(2026-07-16): F6(커버리지 오버레이)이 켜져 있으면 모든 오라 시설 위에
    //  부하/캐퍼를 표시한다. 데이터 = AuraLoadMap(AuraCoverageSystem 시간당 발행: 시설 →
    //  (감당중 b, 감당가능 a)). b = 범위 내 건물 캐퍼 합 / a = 배정 근무자수 × 담당인원.
    //    · a>0: "150/300" — 여유(b≤a)=초록 / 초과(b≤2a)=주황 / 2배 초과=빨강
    //    · a≤0(무근무 = 무서비스): 감당중만 회백.
    //
    //  GC 규약(CLAUDE.md): 쿼리 월드당 1회 / 문자열은 발행 Version 변화 시만 재조립 /
    //  Repaint 전용. 화면 투영은 매 Repaint 계산(카메라 추종, 무할당).
    // ══════════════════════════════════════════════════════════════
    public class AuraLoadHud : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static bool _created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (_created) return;
            _created = true;
            var go = new GameObject("[AuraLoadHud]");
            go.AddComponent<AuraLoadHud>();
            DontDestroyOnLoad(go);
        }
#endif
        World       _qWorld;
        EntityQuery _mapQ, _gsQ;

        GUIStyle _style;
        uint     _builtVersion = uint.MaxValue;
        readonly List<Entity> _ents  = new(32);
        readonly List<string> _texts = new(32);
        readonly List<Color>  _cols  = new(32);

        static readonly Color COk   = new(0.45f, 0.95f, 0.45f, 1f);   // 여유
        static readonly Color COver = new(1.00f, 0.65f, 0.20f, 1f);   // 정원 초과
        static readonly Color CHot  = new(1.00f, 0.30f, 0.25f, 1f);   // 2배 초과
        static readonly Color CInf  = new(0.80f, 0.80f, 0.80f, 1f);   // 무제한(정원 0)

        void OnGUI()
        {
            if (!CoverageOverlaySystem.GlobalEnabled) return;
            if (Event.current.type != EventType.Repaint) return;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { _qWorld = null; return; }
            var em = world.EntityManager;

            if (!ReferenceEquals(_qWorld, world))
            {
                _qWorld = world;
                _mapQ = em.CreateEntityQuery(ComponentType.ReadOnly<AuraLoadMap>());
                _gsQ  = em.CreateEntityQuery(typeof(GridSettings));
                _builtVersion = uint.MaxValue;
            }
            if (_mapQ.IsEmpty || _gsQ.IsEmpty) return;

            var cam = Camera.main;
            if (cam == null) return;
            float cs = _gsQ.GetSingleton<GridSettings>().CellSize;
            if (cs <= 0f) return;

            var load = _mapQ.GetSingleton<AuraLoadMap>();
            if (!load.Map.IsCreated) return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };

            // 문자열·색은 발행 버전 변화 시에만 재조립(GC 규약).
            if (_builtVersion != load.Version)
            {
                _builtVersion = load.Version;
                _ents.Clear(); _texts.Clear(); _cols.Clear();
                foreach (var kv in load.Map)
                {
                    int b = kv.Value.x, cap = kv.Value.y;   // 감당중 b / 감당가능 a(=cap)
                    _ents.Add(kv.Key);
                    if (cap > 0)
                    {
                        _texts.Add($"{b}/{cap}");
                        _cols.Add(b <= cap ? COk : b <= cap * 2 ? COver : CHot);
                    }
                    else
                    {
                        _texts.Add(b.ToString());
                        _cols.Add(CInf);
                    }
                }
            }

            for (int i = 0; i < _ents.Count; i++)
            {
                Entity e = _ents[i];
                if (!em.Exists(e) || !em.HasComponent<BuildingFootprint>(e)) continue;   // 철거 잔존 가드
                var fp   = em.GetComponentData<BuildingFootprint>(e);
                int2 eff = EntranceOps.RotateSize(fp.Size, fp.RotSteps);
                var wpos = new Vector3((fp.Origin.x + eff.x * 0.5f) * cs, 0f,
                                       (fp.Origin.y + eff.y * 0.5f) * cs);
                var sp = cam.WorldToScreenPoint(wpos);
                if (sp.z <= 0f) continue;   // 카메라 뒤

                var prev = GUI.color;
                GUI.color = _cols[i];
                GUI.Label(new Rect(sp.x - 50f, Screen.height - sp.y - 34f, 100f, 18f),
                          _texts[i], _style);
                GUI.color = prev;
            }
        }
    }
}
