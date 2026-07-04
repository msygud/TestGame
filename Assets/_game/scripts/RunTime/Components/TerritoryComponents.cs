using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Territory & Influence — 인구 기반 영역
    // ──────────────────────────────────────────────────────────────────────────
    //  설계(2026-06-26):
    //    1. 영역 = 플레이어 고유 셀. 적은 내 영역 안에 신규 건설 불가.
    //       내 영역에 새로 먹힌 적 기존 건물·도로는 파괴된다(capture).
    //    2. 영역 원천 = 인구. 거주건물 인구 ÷ PopPerCell = 영역 셀 수.
    //       거주지 중심에서 원형으로 전파한다.
    //    3. 영향력(Influence) = 영역의 힘. 거주지 가까울수록 큼(거리 감쇠).
    //    4. 영역 겹침 = 같은 셀의 플레이어별 영향력을 합산(같은 팀)·비교(다른 팀) →
    //       순(net) 영향력 최대 팀이 그 셀을 소유한다.
    //
    //  저장: GridLayers.TerritoryLayer(int2 → LocalId, -1=중립) 재사용.
    //    TerritorySystem이 유일한 writer. 다른 시스템은 읽기만(빌드 게이트).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 영역 계산 튜닝(싱글톤). 없으면 TerritorySystem이 Default 사용.
    /// 테스트 스크립트(Test.cs)가 런타임에 PopPerCell을 써서 즉시 반영.
    /// </summary>
    public struct TerritoryConfig : IComponentData
    {
        /// <summary>
        /// 셀 1칸 점유에 필요한 인구(충족값, float). 영역 셀 수 = floor(인구 / PopPerCell).
        /// 인구(Capacity)는 int지만 나눗셈은 float, 나머지는 올림 없이 무조건 내림.
        /// </summary>
        public float PopPerCell;
        /// <summary>영역 확산 윈도우 최대 반경(셀, 성능·폭주 가드).</summary>
        public int MaxRadius;

        public static TerritoryConfig Default => new TerritoryConfig
        {
            PopPerCell = 5f,
            MaxRadius  = 64,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  플레이어별 영향력/팀 입력 (테스트용) — Test.cs가 매 프레임 채운다.
    //   · Influence = 경합 해소용 스칼라(팀 합산 후 승자팀−2등팀). 추후 행복도/팩션으로 대체.
    //   · Team      = 동맹 id(같은 Team = 동맹, 영향력 합산·서로 경합 안 함).
    //   인덱스 = LocalId(0~7). 싱글톤 엔티티(PlayerInfluenceConfig 태그)에 버퍼로 붙는다.
    // ══════════════════════════════════════════════════════════════════════════
    public struct PlayerInfluenceConfig : IComponentData { }

    [InternalBufferCapacity(8)]
    public struct PlayerInfluenceElement : IBufferElementData
    {
        public float Influence;
        public int   Team;
    }

    /// <summary>
    /// LocalId(0..7) → 팀 id 매핑(싱글톤). TerritoryLayer가 '팀 id'를 담으므로,
    /// 빌드 게이트가 '셀의 팀'과 '내 팀'을 비교하려면 LocalId를 팀으로 풀 수 있어야 한다.
    /// TeamTableSystem이 PlayerInfluenceElement 버퍼에서 매 프레임 갱신(버퍼 없으면 Identity).
    /// 값 타입 — 게이트에 in으로 넘겨 Burst/메인스레드 모두에서 무할당 조회.
    /// </summary>
    public struct TeamTable : IComponentData
    {
        public int4 Lo;   // localId 0..3 → 팀
        public int4 Hi;   // localId 4..7 → 팀

        /// <summary>localId의 팀 id. 범위 밖은 자기 자신(team=localId)으로 폴백.</summary>
        public int Get(int localId)
            => (uint)localId < 4u ? Lo[localId]
             : (uint)localId < 8u ? Hi[localId - 4]
             : localId;

        /// <summary>team = localId (동맹 없음 기본).</summary>
        public static TeamTable Identity => new TeamTable
        {
            Lo = new int4(0, 1, 2, 3),
            Hi = new int4(4, 5, 6, 7),
        };
    }

    /// <summary>
    /// TerritoryLayer 변경 버전(싱글톤) — TerritorySystem이 재계산(~1초)마다 +1.
    /// 렌더 소비자(아웃라인/F7 채움)는 버전이 바뀔 때만 메시 재구축(매 프레임 재구축 방지 —
    /// 전맵 규모에선 프레임당 수 ms 낭비였음). GL/DrawMesh 제출 자체는 매 프레임.
    /// </summary>
    public struct TerritoryVersion : IComponentData
    {
        public uint Value;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  영토 전환 파괴 (capture = 파괴) — TerritoryCaptureSystem
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 영토 전환 파괴 밸런스(싱글톤). 없으면 Default. 전부 런타임 커스터마이즈 가능
    /// (Test.cs 등에서 싱글톤에 push — TerritoryConfig와 동일 패턴).
    /// </summary>
    public struct TerritoryCaptureConfig : IComponentData
    {
        /// <summary>타팀 영토에 놓인 구조물이 파괴되기까지의 유예(게임 시간, 시간 단위).
        /// 짧은 밀당(핑퐁)은 이 안에 되돌아오면 파괴 없음.</summary>
        public float DwellGameHours;

        /// <summary>1 = 건물은 footprint '전체'가 타팀 영토일 때만 파괴 대상(경계 걸침 보호).
        /// 0 = 한 셀이라도 넘어가면 대상.</summary>
        public byte RequireFullFootprint;

        /// <summary>패스당 파괴 상한(대량 함락 프레임 스파이크 방지 — 넘치면 다음 패스로 이월).</summary>
        public int MaxDestroysPerPass;

        /// <summary>AI 확장이 적 영토에서 유지할 완충 거리(셀). 0 = 완충 없음.
        /// 국경 개발 churn(짓자마자 잠식→파괴 반복) 방지.</summary>
        public int AiEnemyBufferCells;

        /// <summary>중립(무법지대) 도로의 전투 체력. **0 = 기능 끔**(중립 도로도 비타겟).
        /// footprint 전체가 중립일 때만 타겟 가능 — 벽-스팸/잔해에 대한 군사 카운터.
        /// 보호 영토(자기/타팀/경합지) 도로는 여전히 직접 타격 불가.</summary>
        public float NeutralRoadHealth;

        public static TerritoryCaptureConfig Default => new TerritoryCaptureConfig
        {
            DwellGameHours      = 1f,    // 게임 1시간 (기본 SecondsPerDay=1200 기준 현실 50초)
            RequireFullFootprint = 1,
            MaxDestroysPerPass  = 32,
            AiEnemyBufferCells  = 4,
            NeutralRoadHealth   = 200f,
        };
    }

    /// <summary>
    /// 파괴 예정 마커 — 타팀 영토에 놓인 구조물(건물/도로 엔티티)에 부착.
    /// DeadlineSeconds(GameClock.TotalSeconds 기준)가 지나면 TerritoryCaptureSystem이 파괴.
    /// 영토가 되돌아오면 제거(사면). 경고 비주얼은 이 컴포넌트를 읽어 남은 시간을 표현.
    /// </summary>
    public struct CaptureDoom : IComponentData
    {
        public double DeadlineSeconds;
        /// <summary>부착 시점의 dwell 총량(게임초) — 경고 비주얼의 남은 비율 계산용.</summary>
        public double DwellSeconds;
    }

    /// <summary>영토 전환 파괴 면제(베이스/HQ 등 — 전투로만 파괴 가능). 스폰 시 부착.</summary>
    public struct CaptureExempt : IComponentData { }

    /// <summary>
    /// 영역 조회 순수 헬퍼(빌드 게이트 공용 — 건물/도로/AI). TerritoryLayer만 읽는다.
    /// TerritoryLayer는 '팀 id'를 담으므로 게이트는 TeamTable로 LocalId→팀을 풀어
    /// '셀 팀 ≠ 내 팀'을 비교한다(동맹=같은 팀은 적이 아니며, 내 영역도 정확히 통과).
    /// </summary>
    public static class TerritoryOps
    {
        public const int Neutral   = -1;   // 미점유(열림) — absent도 중립 취급
        public const int Contested = -2;   // 경합지(잠김) — 누구도 건설/도로 불가

        /// <summary>cell이 경합지(-2)인가 — 모든 플레이어 건설 차단.</summary>
        public static bool IsContested(in NativeHashMap<int2, int> territory, int2 cell)
            => territory.IsCreated && territory.TryGetValue(cell, out int v) && v == Contested;

        /// <summary>
        /// cell이 내 팀이 아닌 '다른 팀'의 영역이면 true(= 적 영역).
        /// 셀값은 팀 id이므로 myOwner(LocalId)를 teams로 풀어 팀끼리 비교한다.
        /// 같은 팀(동맹)·내 영역은 false. 중립(-1)/경합지(-2)도 false(경합지는 IsContested로 별도 차단).
        /// </summary>
        public static bool InEnemyTerritory(
            in NativeHashMap<int2, int> territory, int2 cell, int myOwner, in TeamTable teams)
        {
            if (!territory.IsCreated) return false;
            if (!territory.TryGetValue(cell, out int cellTeam)) return false;
            return cellTeam >= 0 && cellTeam != teams.Get(myOwner);
        }

        /// <summary>cell이 누구의 것이든 영역(구역)에 속하면 true. (AI 확장 게이트용)</summary>
        public static bool InAnyTerritory(in NativeHashMap<int2, int> territory, int2 cell)
        {
            if (!territory.IsCreated) return false;
            return territory.TryGetValue(cell, out int owner) && owner >= 0;
        }

        /// <summary>footprint [origin, origin+size) 중 한 셀이라도 적 팀 영역이면 true.</summary>
        public static bool FootprintInEnemyTerritory(
            in NativeHashMap<int2, int> territory, int2 origin, int2 size, int myOwner, in TeamTable teams)
        {
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
                if (InEnemyTerritory(in territory, origin + new int2(dx, dz), myOwner, in teams))
                    return true;
            return false;
        }
    }
}
