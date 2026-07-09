using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  물류 — 재고(stock) 인터페이스 (slice 1 기반)
    //
    //  데이터 모델:
    //    · 품목은 3티어 고정: 원재료 / 중간재 / 완성품. 티어는 품목의 정적 속성.
    //    · 보관: 원재료·중간재 → 창고(WarehouseTag). 완성품 → 만든 건물 로컬
    //      (창고를 거치지 않음). 완성품은 생산 입력이 될 수 없음(citizen 소비 전용).
    //    · 입고 = par-level: 입력 재고가 reorder 밑으로 떨어지면 target까지 pull.
    //    · 출고 = push: 출력 재고가 discharge 위로 차면 창고로 push.
    //    · 임계(reorder/target/discharge)는 Capacity × 퍼센트/100(정수 나눗셈)으로
    //      read 시 계산 → 업그레이드 시 Capacity만 바뀌고 순서(reorder<target≤capacity)
    //      보존. 정수라 결정적(float 절삭 artifact 없음).
    //
    //  팩션/생산 경계:
    //    이 파일은 전부 공통(팩션 무관). 생산(레시피·등급·제작시간)은 별도 단계.
    //    등급은 "같은 패밀리 내 완성품 변종 = 별도 Commodity id"로 두므로 이 재고
    //    인터페이스를 건드리지 않는다(품목 id로만 옮기고, 완성품은 애초에 안 옮김).
    //
    //  기존 조각과 연결:
    //    시민이 찾아가는 "공급자"(ServiceSearch/Hunger 해소) = 완성품을 만들어 로컬
    //    보관하는 소비점. 그 LocalFinal 재고를 나중에 item 4(StockItem 차감)가 깎는다.
    //
    //  ※ stub: CommodityDefs는 정적 스위치(Burst-safe). 품목이 늘거나 DLC/오써링에서
    //    오면 NeedLookup/PrefabLookup처럼 베이크 카탈로그(싱글톤)로 이관.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 물류 품목. 서수 enum(양 의미 — 비트마스크 아님). 맵 ResourceType(지형 자원
    /// 레이어)와는 완전히 별개. 티어/임계 메타는 CommodityDefs 표 참조.
    /// stub 체인: 곡물(원재료) → 밀가루(중간재) → 식사(완성품).
    /// </summary>
    public enum Commodity : ushort
    {
        None  = 0,
        Grain = 1,   // 원재료
        Flour = 2,   // 중간재
        Meal  = 3,   // 완성품
        // 완성품 등급 변종(예: MealQuality, MealElite)은 생산 단계에서 같은 패밀리
        // 별도 id로 추가.
    }

    /// <summary>품목 티어. 보관 라우팅과 입력 가능 여부를 결정.</summary>
    public enum CommodityTier : byte
    {
        Raw = 0,        // 원재료 → 창고 보관
        Intermediate,   // 중간재 → 창고 보관
        Final,          // 완성품 → 만든 건물 로컬 보관(창고 X), 생산 입력 불가
    }

    /// <summary>
    /// 건물이 한 품목을 "어떻게 쓰는가". 재고 1칸의 운영 플래그.
    /// 티어 + 이 건물의 입력/출력 여부로 재고 구성 시점에 정해진다(티어가 제약:
    /// 완성품은 Input이 될 수 없고, 완성품 출력은 Output이 아니라 LocalFinal).
    /// </summary>
    public enum StockRole : byte
    {
        Input = 0,    // 입력(소비) — pull로 par-level 유지
        Output,       // 출력(원재료·중간재 생산물) — 창고로 push
        Store,        // 창고 보관 — 수동 보관소(스스로 pull/push 안 함; 공급/수용 대상)
        LocalFinal,   // 완성품 로컬 — 물류 이동 없음(만든 자리서 소비)
    }

    /// <summary>품목 정적 메타(stub). 임계는 정수 퍼센트(0~100).</summary>
    public struct CommodityDef
    {
        public CommodityTier Tier;
        public int ReorderPct;    // 입력: Current < Capacity*ReorderPct/100 → pull 발동
        public int TargetPct;     // 입력: pull 목표 = Capacity*TargetPct/100
        public int DischargePct;  // 출력: Current > Capacity*DischargePct/100 → push 발동
        // 불변식: 0 < ReorderPct < TargetPct ≤ 100, 0 < DischargePct ≤ 100.
        // 계산은 Capacity*Pct/100(정수 나눗셈) — 결정적, float 절삭 없음.
    }

    /// <summary>
    /// 품목 → 정적 메타 조회(stub, Burst-safe 정적 스위치).
    /// 퍼센트는 stub 기본값(원재료·중간재·완성품 동일). 추후 품목별/건물타입별로 세분.
    /// </summary>
    public static class CommodityDefs
    {
        public static CommodityDef Get(Commodity c)
        {
            switch (c)
            {
                case Commodity.Grain:
                    return new CommodityDef { Tier = CommodityTier.Raw,
                        ReorderPct = 25, TargetPct = 90, DischargePct = 80 };
                case Commodity.Flour:
                    return new CommodityDef { Tier = CommodityTier.Intermediate,
                        ReorderPct = 25, TargetPct = 90, DischargePct = 80 };
                case Commodity.Meal:
                    return new CommodityDef { Tier = CommodityTier.Final,
                        ReorderPct = 25, TargetPct = 90, DischargePct = 80 };
                default:
                    return new CommodityDef { Tier = CommodityTier.Raw,
                        ReorderPct = 25, TargetPct = 90, DischargePct = 80 };
            }
        }

        public static CommodityTier TierOf(Commodity c) => Get(c).Tier;
    }

    /// <summary>
    /// 건물 재고 1칸. 한 건물이 여러 품목을 들 수 있으므로 DynamicBuffer<StockEntry>.
    /// 임계는 Capacity × 퍼센트/100(정수)로 read 시 계산(업그레이드 시 Capacity만
    /// 갱신 → 순서 보존). 임계 "값"은 사실(fact)이므로 여기서 제공하고, "pull/push
    /// 할지"의 판단은 pull/push 시스템이 한다(helper=사실, system=결정).
    /// </summary>
    public struct StockEntry : IBufferElementData
    {
        public Commodity Commodity;
        public int       Current;
        public int       Capacity;
        public StockRole Role;

        // ── 파생 사실(read 시 계산, 정수 나눗셈) ──
        public readonly int Reorder   => Capacity * CommodityDefs.Get(Commodity).ReorderPct   / 100;
        public readonly int Target    => Capacity * CommodityDefs.Get(Commodity).TargetPct     / 100;
        public readonly int Discharge => Capacity * CommodityDefs.Get(Commodity).DischargePct   / 100;
        public readonly int Free      => Capacity - Current;
    }

    /// <summary>
    /// 창고 역할 + stamp BFS 소스. 창고는 Store 역할 StockEntry들을 들고 원재료·중간재
    /// 입출고 허브가 된다. 품목별 용량은 StockEntry.Capacity(사람 정원 BuildingOccupancy와 별개).
    ///
    /// slice 2: 자기 입구 도로셀에서 BFS로 도달 범위에 `SupplierRef{Kind=Warehouse}`를
    /// 도장(StampRebuildSystem). 그래서 OwnerLocalId(자기 도로망)·MaxDist(범위)가 필요 —
    /// `StampSupplier`와 대칭. BFS 소스가 되려면 BuildingFootprint+BuildingEntrance도 필요.
    ///
    /// ※ 다른 건물 role 태그(ResidenceBuilding/WorkplaceBuilding/ServiceBuilding)는
    ///   BuildingOccupancy.cs에 있음. WarehouseTag는 물류 소유라 이 파일에 둔다.
    /// </summary>
    public struct WarehouseTag : IComponentData
    {
        /// <summary>이 창고를 소유한 플레이어(0~7). 자기 도로망에만 stamp.</summary>
        public int OwnerLocalId;

        /// <summary>stamp 도달 범위 상한(BFS 최대 도로 칸 수). 0 이하면 무제한.</summary>
        public int MaxDist;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  LogisticsPool — 플레이어별 창고 공유 풀 (2026-07-09, 요청 #1)
    // ──────────────────────────────────────────────────────────────────────────
    //  창고 여러 채를 "한 논리 저장소"로 묶는다. 개별 창고 buffer 대신 (owner,commodity)
    //  단위 집계 슬롯 하나로 재고를 보관:
    //    · Capacity = 그 플레이어 모든 창고의 그 품목 Store 칸 Capacity 합.
    //    · Stored   = 풀 자체가 보관하는 진실. **창고 StockEntry.Current는 미사용(vestigial)** —
    //      창고는 이제 "용량 기여 + stamp 커버리지"만 담당(Store 칸은 Capacity 선언용).
    //  커버리지 = 이진: 건물 입구 도로셀에 그 플레이어 창고 stamp(Kind=Warehouse)가 하나라도
    //  닿으면 "풀 접속" → 풀 전체(모든 창고 합)에 접근. 개별 창고 반경에 안 갇힘 → 창고를
    //  어디에 더 지어도 커버 겹치는 건물은 전체 용량을 공유(요청 #1 해소).
    //
    //  key = int2(owner, (int)commodity). LogisticsPoolSystem이 Capacity 재계산·clamp,
    //  Pull/Push가 Stored 증감. 싱글톤 NativeHashMap(메인만 접근 — DemandField 패턴,
    //  맵 내용 변경은 SetSingleton 불필요: 핸들이 공유 데이터를 가리킴).
    // ══════════════════════════════════════════════════════════════════════════
    public struct PoolCell
    {
        public int Stored;
        public int Capacity;
        public readonly int Free => Capacity - Stored;
    }

    public struct LogisticsPool : IComponentData
    {
        /// <summary>key=int2(owner,(int)commodity) → (Stored, Capacity). owner별 창고 Store 합.</summary>
        public NativeHashMap<int2, PoolCell> Cells;

        public static int2 Key(int owner, Commodity c) => new int2(owner, (int)c);
    }
}
