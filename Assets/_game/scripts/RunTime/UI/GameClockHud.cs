using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  GameClockHud — 게임 시간 표시 + 배속 조절 (임시/디버그 IMGUI)
    //
    //  화면 상단 중앙에 Day / 시:분 / 현재 배속을 표시하고,
    //  일시정지·1·3·10·60배 버튼으로 GameClock.TimeScale을 바꾼다.
    //
    //  하루 = 현실 SecondsPerDay초(기본 1200=20분)라 AI 성장(하루 1회)을
    //  눈으로 확인하려면 배속이 필요하다.
    //
    //  에디터/개발 빌드 자동 생성(씬 와이어링 불필요). 정식 HUD(GameHUD)에
    //  시간 패널이 들어오면 이 파일은 삭제하면 된다.
    // ══════════════════════════════════════════════════════════════
    public class GameClockHud : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static bool _created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (_created) return;
            _created = true;
            var go = new GameObject("[GameClockHud]");
            go.AddComponent<GameClockHud>();
            DontDestroyOnLoad(go);
        }
#endif
        GUIStyle _label;

        // GC 방지 캐시(2026-07-05) — OnGUI는 프레임당 2회+(Layout/Repaint) 불리므로
        //   ① 쿼리를 매 호출 생성/해제하면 관리형 쓰레기가 쌓이고(주기적 GC 스파이크)
        //   ② 표시 문자열도 매 호출 새로 만들면 같은 문제 → 값이 바뀔 때만 재조립(게임-분당 1회).
        World       _qWorld;
        EntityQuery _clockQ;
        string _timeText = string.Empty;
        int    _lastDay = int.MinValue, _lastMin = -1;
        float  _lastScale = float.NaN;
        float  _nextBuildRT;   // 고배속(60~120x)에선 게임-분이 매 프레임 바뀜 → 현실 시간 4Hz로 상한

        void OnGUI()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { _qWorld = null; return; }
            var em = world.EntityManager;

            if (!ReferenceEquals(_qWorld, world))
            {
                _qWorld = world;
                _clockQ = em.CreateEntityQuery(typeof(GameClock));   // 월드당 1회(월드 파괴 시 함께 정리)
            }
            if (_clockQ.IsEmpty) return;
            var clockEntity = _clockQ.GetSingletonEntity();
            var clock = em.GetComponentData<GameClock>(clockEntity);

            _label ??= new GUIStyle(GUI.skin.box)
            {
                fontSize  = 14,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };

            int totalMin = (int)(clock.DayProgress01 * 24 * 60);

            if ((clock.Day != _lastDay || totalMin != _lastMin || clock.TimeScale != _lastScale)
                && Time.unscaledTime >= _nextBuildRT)
            {
                _nextBuildRT = Time.unscaledTime + 0.25f;
                _lastDay = clock.Day; _lastMin = totalMin; _lastScale = clock.TimeScale;
                int hh = totalMin / 60;
                int mm = totalMin % 60;
                _timeText = $"Day {clock.Day + 1}   {hh:00}:{mm:00}   (x{clock.TimeScale:0.#})";
            }

            float w = 360f, h = 26f;
            float x = (Screen.width - w) * 0.5f;

            // 시간 표시
            GUI.Box(new Rect(x, 6f, w, h), _timeText, _label);

            // 배속 버튼
            const int nBtn = 6;
            float bw = 56f, by = 6f + h + 4f;
            float bx = (Screen.width - bw * nBtn - 4f * (nBtn - 1)) * 0.5f;
            DrawSpeed(em, clockEntity, clock, ref bx, by, bw, "⏸ 0",  0f);
            DrawSpeed(em, clockEntity, clock, ref bx, by, bw, "1x",   1f);
            DrawSpeed(em, clockEntity, clock, ref bx, by, bw, "3x",   3f);
            DrawSpeed(em, clockEntity, clock, ref bx, by, bw, "10x",  10f);
            DrawSpeed(em, clockEntity, clock, ref bx, by, bw, "60x",  60f);
            DrawSpeed(em, clockEntity, clock, ref bx, by, bw, "120x", 120f);
        }

        void DrawSpeed(EntityManager em, Entity e, GameClock clock,
                       ref float bx, float by, float bw, string label, float scale)
        {
            bool active = Mathf.Approximately(clock.TimeScale, scale);
            var prev = GUI.backgroundColor;
            if (active) GUI.backgroundColor = new Color(0.25f, 0.6f, 1f);

            if (GUI.Button(new Rect(bx, by, bw, 24f), label))
            {
                var c = em.GetComponentData<GameClock>(e);
                c.TimeScale = scale;
                em.SetComponentData(e, c);
            }

            GUI.backgroundColor = prev;
            bx += bw + 4f;
        }
    }
}
