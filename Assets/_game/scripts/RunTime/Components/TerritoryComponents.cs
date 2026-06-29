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
    /// 영역 조회 순수 헬퍼(빌드 게이트 공용 — 건물/도로/AI). TerritoryLayer만 읽는다.
    /// ⚠ TerritoryLayer가 팀 id를 담으므로, 동맹(team≠localId) 시 게이트가 팀-인지여야 정확.
    ///   현재는 team=localId 기본에서만 정확(동맹 게이트는 후속).
    /// </summary>
    public static class TerritoryOps
    {
        public const int Neutral   = -1;   // 미점유(열림) — absent도 중립 취급
        public const int Contested = -2;   // 경합지(잠김) — 누구도 건설/도로 불가

        /// <summary>cell이 경합지(-2)인가 — 모든 플레이어 건설 차단.</summary>
        public static bool IsContested(in NativeHashMap<int2, int> territory, int2 cell)
            => territory.IsCreated && territory.TryGetValue(cell, out int v) && v == Contested;

        /// <summary>cell이 myOwner가 아닌 '실제 플레이어'의 영역이면 true(= 적 영역).</summary>
        public static bool InEnemyTerritory(
            in NativeHashMap<int2, int> territory, int2 cell, int myOwner)
        {
            if (!territory.IsCreated) return false;
            if (!territory.TryGetValue(cell, out int owner)) return false;
            return owner >= 0 && owner != myOwner;
        }

        /// <summary>cell이 누구의 것이든 영역(구역)에 속하면 true. (AI 확장 게이트용)</summary>
        public static bool InAnyTerritory(in NativeHashMap<int2, int> territory, int2 cell)
        {
            if (!territory.IsCreated) return false;
            return territory.TryGetValue(cell, out int owner) && owner >= 0;
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
