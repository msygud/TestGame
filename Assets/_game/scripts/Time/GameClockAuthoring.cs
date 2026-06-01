using Unity.Entities;
using UnityEngine;
using CitySim;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  GameClockAuthoring — 게임 시간 초기 설정 노출 (선택적)
    //
    //  SubScene에 배치하면 TimeScale·SecondsPerDay·시작 시각을 인스펙터에서 지정.
    //  배치하지 않아도 GameClockSystem.OnCreate가 기본값으로 싱글톤을 만든다.
    //  → 이 Authoring이 있으면 그 값으로 덮어쓰고, 없으면 Default.
    //
    //  주의: 싱글톤 중복 생성 방지를 위해, 이 baker가 만든 엔티티가 있으면
    //  GameClockSystem.OnCreate의 "없으면 생성"이 자연히 건너뛴다(HasSingleton 검사).
    // ══════════════════════════════════════════════════════════════════════════
    public class GameClockAuthoring : MonoBehaviour
    {
        [Tooltip("현실 1초 = 게임 몇 초. 1=정상, 0=일시정지, 2·3=배속.")]
        public float TimeScale = 1f;

        [Tooltip("게임 하루의 길이(게임초). 1200=현실 20분=게임 하루.")]
        public float SecondsPerDay = 1200f;

        [Header("시작 시각")]
        [Tooltip("게임 시작 시각(시, 0~23). 예: 6 = 아침 6시에 시작.")]
        [Range(0, 23)]
        public int StartHour = 6;

        [Tooltip("게임 시작 일수(0부터).")]
        public int StartDay = 0;

        class Baker : Baker<GameClockAuthoring>
        {
            public override void Bake(GameClockAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.None);

                float secondsPerDay = a.SecondsPerDay <= 0f ? 1200f : a.SecondsPerDay;

                // 시작 시각을 누적초로 환산
                double startSeconds =
                    (double)a.StartDay * secondsPerDay
                    + (a.StartHour / (double)GameClock.HoursPerDay) * secondsPerDay;

                var clock = GameClock.Default;
                clock.TimeScale     = a.TimeScale;
                clock.SecondsPerDay = secondsPerDay;
                clock.TotalSeconds  = startSeconds;

                AddComponent(e, clock);
            }
        }
    }
}

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  GameClockControl — 시간 제어 헬퍼 (UI 연결용)
    //
    //  일시정지·재개·배속 변경을 한 곳에서. UI 버튼이 이걸 호출.
    //  싱글톤이 있어야 동작(없으면 무시).
    // ══════════════════════════════════════════════════════════════════════════
    public static class GameClockControl
    {
        public static void SetTimeScale(EntityManager em, float scale)
        {
            var q = em.CreateEntityQuery(typeof(GameClock));
            if (q.IsEmpty) return;
            var clock = q.GetSingleton<GameClock>();
            clock.TimeScale = scale < 0f ? 0f : scale;
            q.SetSingleton(clock);
        }

        public static void Pause(EntityManager em)  => SetTimeScale(em, 0f);
        public static void Resume(EntityManager em) => SetTimeScale(em, 1f);

        public static bool IsPaused(EntityManager em)
        {
            var q = em.CreateEntityQuery(typeof(GameClock));
            if (q.IsEmpty) return false;
            return q.GetSingleton<GameClock>().TimeScale <= 0f;
        }
    }
}
