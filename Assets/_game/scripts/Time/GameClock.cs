using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  게임 시간 (GameClock)
    //
    //  거의 모든 시뮬레이션 시스템의 기반:
    //    - 욕구 틱, 스케줄(출근·점심), 체증 통계, 이동 도착 타이머, 행동 종료.
    //
    //  설계 원칙:
    //    - 진실의 원천은 TotalSeconds(double) 하나. 시/일/주/월은 전부 파생 계산.
    //      → 여러 단위를 따로 저장하면 동기화 버그. 하나만 누적하고 계산으로 도출.
    //    - 실시간 deltaTime × TimeScale → 게임초 증가. (일시정지=0, 배속=2,3…)
    //    - 시간 경계(시/일/주/월 바뀜)는 GameClockSystem이 한 곳에서 계산해
    //      플래그로 제공. 각 시스템이 중복 계산하지 않음(§0-1).
    //
    //  단위 비율: 현실적(24시간/7일/30일). 스케줄 직관성 우선.
    //    게임 편의 비율이 필요하면 상수만 변경.
    // ══════════════════════════════════════════════════════════════════════════
    public struct GameClock : IComponentData
    {
        // ── 진실의 원천 ────────────────────────────────────────────────────
        /// <summary>게임 시작부터 누적된 게임 시간(초). 모든 파생의 기준.</summary>
        public double TotalSeconds;

        // ── 설정 ────────────────────────────────────────────────────────────
        /// <summary>현실 1초 = 게임 몇 초. 일시정지=0, 1배속=1, 2·3배속 등.</summary>
        public float TimeScale;

        /// <summary>게임 하루의 길이(게임초). 예: 86400이면 1:1 현실시간.</summary>
        public float SecondsPerDay;

        // ── 이번 틱 경계 플래그 (GameClockSystem이 매 틱 갱신) ──────────────
        /// <summary>이번 틱에 시(hour)가 바뀌었는가.</summary>
        public bool HourChanged;
        /// <summary>이번 틱에 일(day)이 바뀌었는가.</summary>
        public bool DayChanged;
        /// <summary>이번 틱에 주(week)가 바뀌었는가.</summary>
        public bool WeekChanged;
        /// <summary>이번 틱에 월(month)이 바뀌었는가.</summary>
        public bool MonthChanged;

        // ── 파생 단위 (TotalSeconds에서 계산, 캐시) ─────────────────────────
        //  GameClockSystem이 매 틱 채워 둠. 다른 시스템은 읽기만.
        public int   Hour;        // 0~23 (하루 중 시각)
        public int   DayOfWeek;   // 0~6
        public int   Day;         // 누적 일수 (0부터)
        public int   Week;        // 누적 주수
        public int   Month;       // 누적 월수

        // ── 상수 (현실 비율) ────────────────────────────────────────────────
        public const int HoursPerDay   = 24;
        public const int DaysPerWeek   = 7;
        public const int DaysPerMonth  = 30;

        // ── 파생 헬퍼 ────────────────────────────────────────────────────────
        /// <summary>하루 길이 대비 현재 진행도 0~1 (0=자정, 0.5=정오).</summary>
        public readonly float DayProgress01
        {
            get
            {
                if (SecondsPerDay <= 0f) return 0f;
                double dayPart = TotalSeconds % SecondsPerDay;
                return (float)(dayPart / SecondsPerDay);
            }
        }

        /// <summary>게임초 → 일수(실수).</summary>
        public readonly double TotalDays
            => SecondsPerDay > 0f ? TotalSeconds / SecondsPerDay : 0.0;

        public static GameClock Default => new GameClock
        {
            TotalSeconds  = 0.0,
            TimeScale     = 1f,
            SecondsPerDay = 1200f,   // 현실 20분 = 게임 하루 (기본값)
            Hour = 0, DayOfWeek = 0, Day = 0, Week = 0, Month = 0,
        };
    }
}
