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

        /// <summary>인구 비례 비축 목표(P2, 2026-07-17) = min(Capacity, 거주 인구 × 품목 계수).
        /// LogisticsPoolSystem이 시간당 재계산. 생산 시작 게이트(ProductionSystem)와 생산자 신설
        /// 억제(AiCityGrowth)의 기준 — **용량이 아니라 이 값**. 구 용량 앵커는 커버 확장(창고 신설)
        /// 마다 비축 천장이 래칫돼 생산이 도시 면적을 따라 무한히 노동·부지를 흡수(무한 생산 함정,
        /// 유저 2026-07-17). 인구가 늘면 목표가 따라 올라 생산 자동 재개(자기 조절).</summary>
        public int Target;

        public readonly int Free => Capacity - Stored;
    }

    /// <summary>
    /// 인구 비례 비축 정책(P2, 2026-07-17) — "비축의 앵커는 창고 수가 아니라 인구".
    /// 전쟁물자(MRP)는 미래에 품목 case로 별도 앵커(군 규모)를 추가 — 이 스위치가 그 골격.
    /// Burst-safe 정적 스위치(RecipeDefs 패턴).
    /// </summary>
    public static class StockPolicy
    {
        // ⚠ 밸런스 상수(v1 임시): 1인당 목표 재고(단위 볼륨). "N일치 소비" 근사 —
        //   Meal 사슬(Grain→Flour→Meal) 상류일수록 버퍼 크게(공급 지연 흡수).
        public static int PerCapita(Commodity c) => c switch
        {
            Commodity.Grain => 3,
            Commodity.Flour => 2,
            _               => 2,   // 새 품목 기본값 — 도입 시 case 추가
        };

        /// <summary>풀 비축 목표 — 용량 상한 클램프(창고가 모자라면 용량까지만).</summary>
        public static int Target(Commodity c, int population, int capacity)
            => math.min(capacity, population * PerCapita(c));
    }

    /// <summary>풀 흐름 창(성장 틱마다 소비·클리어) — Out=실제 유출, In=실제 유입. **물리 흐름만**
    /// (2026-07-10 층 분리 합의): 미충족 요구(desire)는 풀 장부가 아니라 소비자의 결핍 신호 —
    /// 수요층(LogisticsMissLog→DemandField) 소관. 풀은 알아도 모른척.</summary>
    public struct PoolFlow
    {
        public int Out, In;
    }

    public struct LogisticsPool : IComponentData
    {
        /// <summary>key=int2(owner,(int)commodity) → (Stored, Capacity). owner별 창고 Store 합.</summary>
        public NativeHashMap<int2, PoolCell> Cells;

        /// <summary>풀 거래 계측(P v2/v3, 2026-07-10 — 합의 모델 "창고만 보면 모든 흐름이 보인다"):
        /// Raw·Intermediate는 전부 풀을 경유하므로 풀 인터페이스의 **물리 거래량**이 물량 흐름의
        /// 완전한 통계. 생산자 스케일링 판단 = 유출>유입 + 목표재고 미달(고갈 전 선제). 결핍
        /// (못 받은 요구)은 수요층 소관 — 이 장부엔 없음. AiCityGrowth가 성장 틱마다 읽고 Clear.</summary>
        public NativeHashMap<int2, PoolFlow> Flow;

        public static int2 Key(int owner, Commodity c) => new int2(owner, (int)c);

        /// <summary>유출 기록: got=실제 꺼낸 양만(물리 장부 — 미충족분은 여기 안 남음). 메인 전용.</summary>
        public void RecordDraw(int2 key, int got)
        {
            Flow.TryGetValue(key, out var f);
            f.Out += got;
            Flow[key] = f;
        }

        /// <summary>유입 기록: put=실제 넣은 양(In). 메인 전용.</summary>
        public void RecordDeposit(int2 key, int put)
        {
            Flow.TryGetValue(key, out var f);
            f.In += put;
            Flow[key] = f;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  LogisticsMissLog — 결핍(미스) 신호 창 → 수요층 (2026-07-10, 층 분리 확정)
    // ──────────────────────────────────────────────────────────────────────────
    //  Pull/Push가 미스를 플래그로 세우고 DemandAggregation 1초 틱이 DemandSample로 변환 후
    //  Clear. 플래그(idempotent)라 실행 빈도 무관 셀당 1샘플/윈도. 원인별 2채널:
    //    · 커버 미스(stamp 안 닿음 + 거래할 것 있음) → WarehouseId → 그 셀의 창고 수요(지역).
    //    · 양적 미스(커버 있음 + 풀에서 요구를 못 채움) → ForCommodity → 생산자 수요.
    //      **부트스트랩·절대 결핍 전담**(흐름 0이면 풀 층이 못 봄) — 시민 미충족과 같은 층/기계.
    //  층 분리: 풀(LogisticsPool.Flow) = 물리 흐름만 보고 정상 체인의 선제 스케일링,
    //  수요층(여기) = 결핍 해소. 풀은 결핍을 알아도 모른척(장부 분리).
    //  ※ 커버 있음+풀 만석 = 과잉생산 — 수요 발행 안 함. 메인 전용(DemandField 패턴).
    // ══════════════════════════════════════════════════════════════════════════
    public struct LogisticsMissLog : IComponentData
    {
        /// <summary>(owner, 수요셀x, 수요셀y, resId) → 이 창(~1초) 동안 미스 발생(1).</summary>
        public NativeHashMap<int4, byte> Window;

        /// <summary>미스 기록 — 건물 origin을 수요셀로 양자화. 메인 전용, idempotent.</summary>
        public void Record(int owner, int2 buildingOrigin, int resId)
        {
            int2 d = DemandGrid.ToCell(buildingOrigin);
            Window[new int4(owner, d.x, d.y, resId)] = 1;
        }
    }
}
