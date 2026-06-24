using Unity.Collections;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ClaimOps — 건설 클레임(영역) 판정
    //
    //  "플레이어별 건설 영역" = 각 플레이어의 **건물** + 마진 M칸.
    //  규칙: 다른 플레이어의 클레임 안엔 못 짓는다 = "셀이 적 건물에서 M칸 이내면 거부".
    //    · 적 도시(건물) 근처 도로 도배/건물 plop 차단(개발된 구역 보호).
    //
    //  ★ 도로는 클레임을 만들지 않는다 (영구 결정, 2026-06-24).
    //    도로는 선형이라 claim에 넣으면 적이 내 확장 방향에 도로 한 줄만 깔아도 M칸 '띠'가
    //    봉쇄 구역이 됐다(작은 맵 치명적). 도로는 영역이 아닌 인프라 → 건물만 영역을 정의.
    //    적 도로 옆/근처엔 자유 건설(도로 위에만 못 올림 = 기존 impassability는 RoadSystem 소관).
    //  중립(owner<0)·환경물·도로·자기 건물은 클레임이 아니다.
    //
    //  TerritoryLayer 캐시 없이 OccupancyLayer를 그때그때 스캔(배치는 비핫패스).
    //  무거워지면 TerritoryLayer에 캐시 가능(현재는 빈 채로 예약만 됨).
    // ══════════════════════════════════════════════════════════════════════════
    public static class ClaimOps
    {
        /// <summary>기본 마진(셀). 적 점유물에서 이 거리(체비셰프) 이내엔 건설 금지.</summary>
        public const int DefaultMargin = 3;

        /// <summary>
        /// 셀이 다른 실제 플레이어의 점유물(건물/도로)에서 margin칸(체비셰프) 이내인가.
        /// true = 적 클레임 영역 → 건설 금지.
        /// </summary>
        public static bool InEnemyClaim(
            int2 cell, int ownerLocalId, int margin,
            in NativeHashMap<int2, OccupancyCell> occ)
        {
            if (!occ.IsCreated) return false;
            for (int dz = -margin; dz <= margin; dz++)
            for (int dx = -margin; dx <= margin; dx++)
            {
                if (!occ.TryGetValue(cell + new int2(dx, dz), out var o)) continue;
                if (IsEnemyClaimer(in o, ownerLocalId)) return true;
            }
            return false;
        }

        /// <summary>
        /// 박스 [min,max]를 margin만큼 부풀린 영역에 적 점유물이 있나 (AI 블록 1패스용).
        ///   "박스 안 어떤 셀이든 적과 margin 이내" ⇔ "확장 박스에 적 존재"(체비셰프 동치).
        ///   셀별 InEnemyClaim을 블록 전체에 돌리는 것보다 훨씬 싸다.
        /// </summary>
        public static bool RegionHasEnemy(
            int2 min, int2 max, int ownerLocalId, int margin,
            in NativeHashMap<int2, OccupancyCell> occ)
        {
            if (!occ.IsCreated) return false;
            for (int z = min.y - margin; z <= max.y + margin; z++)
            for (int x = min.x - margin; x <= max.x + margin; x++)
            {
                if (!occ.TryGetValue(new int2(x, z), out var o)) continue;
                if (IsEnemyClaimer(in o, ownerLocalId)) return true;
            }
            return false;
        }

        // 이 점유물이 ownerLocalId 입장에서 '적 클레임'을 만드는가.
        //   실제 플레이어(owner≥0) 소유의 **건물만**. 도로·환경물·중립·자기 것은 제외.
        //   (도로는 영역이 아닌 인프라 — 위 헤더 주석 참고.)
        static bool IsEnemyClaimer(in OccupancyCell o, int ownerLocalId)
        {
            if (o.IsEmpty) return false;
            if (o.Type != OccupantType.Building) return false;
            if (o.OwnerLocalId < 0) return false;
            return o.OwnerLocalId != ownerLocalId;
        }
    }
}
