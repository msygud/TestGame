using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  배치 그리드(지구 슬롯) + 지구 전략 평가 — 자료구조 (2026-07-11 설계 합의)
    // ──────────────────────────────────────────────────────────────────────────
    //  지구(District) = 커버 반경 피치의 월드 양자화 격자(DemandGrid와 동형 — 전
    //  플레이어 공용, 상태 0, 결정적, 접경 좌표계 충돌 없음). 두 용도:
    //    ① 배치: 지구당 인프라 슬롯(중앙 수요셀 8×8)을 소프트 예약 — 창고·서비스가
    //       슬롯을 선호하고, 주택은 인프라 미충족 지구의 슬롯을 후순위(구 계획 F의
    //       일반화 — 기존 창고 주변이 아니라 "앞으로 올" 인프라 자리를 지킨다).
    //    ② 전략: 지구별 테이블(개발 여지·자원·owner 존재감·확장 프런트 — 일 1회
    //       서베이 잡) → 팀별 확장 목표 지구 선정 → GrowOneBlock 소프트 bias
    //       (미시 기계 무수정 — 로컬 그리디의 "매판 같은 모양"을 거시에서 깬다).
    //
    //  피치 24 근거: 창고 커버 = 도로 BFS SpawnSystem.WarehouseStampMaxDist(30).
    //  지구 중앙 슬롯의 창고가 지구 모서리까지 맨해튼 ~24 + 링 우회 여유 ≤ 30 —
    //  "창고 1채 = 지구 1개 온전 커버"가 성립하는 최대 피치. DemandGrid.CellSize(8)의
    //  3배라 수요셀 3×3이 지구 1개에 정렬(우세 수요셀 → 지구 변환이 자명).
    //  피치·슬롯 크기 = 공용 정책. 팩션 비대칭이 필요해지면 팩션 config 오버라이드가
    //  확장점(현재는 상수).
    // ══════════════════════════════════════════════════════════════════════════
    public static class DistrictGrid
    {
        public const int Pitch    = DemandGrid.CellSize * 3;   // 24 — 수요셀 3×3 정렬
        public const int SlotSize = DemandGrid.CellSize;       // 중앙 슬롯 = 지구 중앙 수요셀(8×8)

        public static int2 ToDistrict(int2 realCell)
            => new int2(FloorDiv(realCell.x, Pitch), FloorDiv(realCell.y, Pitch));

        /// <summary>지구 → 실셀 원점(좌하단).</summary>
        public static int2 ToRealOrigin(int2 district)
            => new int2(district.x * Pitch, district.y * Pitch);

        /// <summary>지구 중심 실셀 — 전략 bias·창고 앵커용.</summary>
        public static int2 Center(int2 district)
            => ToRealOrigin(district) + new int2(Pitch / 2, Pitch / 2);

        /// <summary>지구 중앙 슬롯(SlotSize×SlotSize) 원점.</summary>
        public static int2 SlotOrigin(int2 district)
            => ToRealOrigin(district) + new int2((Pitch - SlotSize) / 2, (Pitch - SlotSize) / 2);

        /// <summary>실셀이 자기 지구의 중앙 슬롯 안인가 — 인프라 선호/주거 유보 판정.</summary>
        public static bool InSlot(int2 realCell)
        {
            int lx = realCell.x - FloorDiv(realCell.x, Pitch) * Pitch;
            int ly = realCell.y - FloorDiv(realCell.y, Pitch) * Pitch;
            const int lo = (Pitch - SlotSize) / 2, hi = lo + SlotSize;
            return lx >= lo && lx < hi && ly >= lo && ly < hi;
        }

        static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }
    }

    /// <summary>지구 1개의 서베이 통계 — DistrictSurveyJob(AiCityGrowth 잡 체인)이 일 1회 채움.</summary>
    public struct DistrictStat
    {
        public ushort Room;        // 개발 여지: buildable & 미점유 & 비수(水) 셀 수 — 회랑/포화 도심 = 극소
        public ushort Resource;    // 채취 자원 셀 수(Amount>0)
        public byte   RoadOwners;  // 지구 안 도로 보유 owner(LocalId 0~7) 비트마스크
        public byte   BldOwners;   // 지구 안 건물 보유 owner 비트마스크
        public byte   TerrTeams;   // 지구 안 영토(TerritoryLayer)의 '팀 id' 비트마스크(0~7만 기록)
        public byte   FrontOwners; // 전일 대비 새로 도로가 생긴 owner 비트 = 확장 프런트(경쟁항 입력)
    }

    /// <summary>
    /// 지구 테이블 front 싱글톤 — AiCityGrowthSystem이 서베이 잡 완료 후 발행(복사).
    /// 독자(오버레이 / 다음 틱의 창고 확장 앵커)는 이 front만 읽는다(메인만 씀 = 안전).
    /// Targets = 팀별 확장 목표 지구(owner → district) — 오버레이/디버그용.
    /// Version = 발행마다 +1(렌더 캐시 무효화 키).
    /// </summary>
    public struct DistrictTable : IComponentData
    {
        public NativeHashMap<int2, DistrictStat> Stats;
        public NativeHashMap<int, int2>          Targets;
        public uint Version;
    }
}
