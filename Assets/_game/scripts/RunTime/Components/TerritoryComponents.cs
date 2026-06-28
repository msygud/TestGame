using Unity.Collections;
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

    /// <summary>영역 계산 튜닝(플레인 struct — GrowthConfig와 동일하게 .Default로 사용).</summary>
    public struct TerritoryConfig
    {
        /// <summary>셀당 인구 기준. 영역 셀 수 = 인구 / PopPerCell.</summary>
        public int PopPerCell;
        /// <summary>한 거주건물 영역의 최대 반경(셀, 성능·폭주 가드).</summary>
        public int MaxRadius;

        public static TerritoryConfig Default => new TerritoryConfig
        {
            PopPerCell = 5,
            MaxRadius  = 24,
        };
    }

    /// <summary>
    /// 영역 조회 순수 헬퍼(빌드 게이트 공용 — 건물/도로/AI). TerritoryLayer만 읽는다.
    /// </summary>
    public static class TerritoryOps
    {
        public const int Neutral = -1;

        /// <summary>cell이 myOwner가 아닌 '실제 플레이어'의 영역이면 true(= 적 영역).</summary>
        public static bool InEnemyTerritory(
            in NativeHashMap<int2, int> territory, int2 cell, int myOwner)
        {
            if (!territory.IsCreated) return false;
            if (!territory.TryGetValue(cell, out int owner)) return false;
            return owner >= 0 && owner != myOwner;
        }

        /// <summary>footprint [origin, origin+size) 중 한 셀이라도 적 영역이면 true.</summary>
        public static bool FootprintInEnemyTerritory(
            in NativeHashMap<int2, int> territory, int2 origin, int2 size, int myOwner)
        {
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
                if (InEnemyTerritory(in territory, origin + new int2(dx, dz), myOwner))
                    return true;
            return false;
        }
    }
}
