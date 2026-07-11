using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using CitySim;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  BuildingAuthoring — 건물 능력의 단일 작성 지점 (다지기 ①, 2026-07-11 개편)
    //
    //  원칙(CLAUDE.md "능력은 컴포넌트로 산다"):
    //    건물이 "무엇이고 무엇을 하나"(거주·고용·공급·생산·재고·창고·내구)는 전부
    //    per-MainKey 능력 = **프리팹 엔티티의 컴포넌트**로 여기서 굽는다.
    //    SpawnSystem은 Instantiate 복사에 맡기고 per-instance 사실(owner)만 주입.
    //    배치검증·UI·AI는 PrefabLookup이 준 프리팹 엔티티의 같은 컴포넌트를 읽는다.
    //
    //  구조(2026-07-11): 배타적 Kind 스위치 → **직교(composable) 섹션**. 식당처럼
    //    공급자+직장+생산+재고를 겸하는 건물이 정상 표현된다. 설정 안 한 섹션은 안 굽는다.
    //    (Kind 필드는 거주 마킹 + 기존 주택 프리팹 직렬화 호환용으로 유지.)
    //
    //  owner 규약: StampSupplier/WarehouseTag의 OwnerLocalId는 per-instance 사실이라
    //    여기선 -1로 굽고 SpawnSystem이 스폰 시 SetComponent로 주입한다.
    //
    //  주의(§0): BuildingOccupancy는 "정원/예약" 카운트지 시민 명단이 아니다.
    //    거주와 고용은 같은 BuildingOccupancy를 쓰므로 **한 건물에 둘 다 설정 금지**
    //    (거주가 우선, 경고 로그).
    //  주의: NeedType(ulong)은 직렬화 미지원 → ulong 백킹 + 프로퍼티(확립 패턴).
    // ══════════════════════════════════════════════════════════════════════════

    public enum BuildingKind : byte
    {
        None = 0,
        Residence,   // 집 — Capacity = 거주 정원
        Workplace,   // (레거시 — ProvidedJob 설정으로 대체, 남겨둔 직렬화 호환)
        Service,     // (레거시 — ReliefRaw 설정으로 대체, 남겨둔 직렬화 호환)
    }

    /// <summary>재고 1칸 사양 — DynamicBuffer&lt;StockEntry&gt;로 베이크.</summary>
    [System.Serializable]
    public struct StockSpec
    {
        public Commodity Commodity;
        public StockRole Role;       // Input=pull / Output=push / Store=창고 / LocalFinal=완성품 로컬
        public int       Capacity;
        public int       Initial;    // 개점 완충(식당 Meal 등). 보통 0.
    }

    public class BuildingAuthoring : MonoBehaviour
    {
        [Header("거주 (Kind=Residence일 때)")]
        [Tooltip("건물 종류. Residence만 의미 있음 — 고용/공급은 아래 섹션이 결정(직교).")]
        public BuildingKind Kind = BuildingKind.None;

        [Tooltip("Kind=Residence: 거주 정원. (레거시 Workplace: 일자리 수 폴백)")]
        public int Capacity = 1;

        [Header("고용")]
        [Tooltip("제공 직업. Unemployed가 아니면 WorkplaceBuilding + 일자리 정원을 굽는다.")]
        public JobType ProvidedJob = JobType.Unemployed;

        [Tooltip("일자리 정원. 0이면 Capacity 폴백(레거시 Workplace 프리팹 호환).")]
        public int WorkerSlots = 0;

        [Header("욕구 공급 — 방문형(도로 stamp)")]
        [Tooltip("해소하는 욕구 조합(NeedType ulong 비트합). 0이 아니면 StampSupplier를 굽는다.\n" +
                 "예: Hunger=1. 여러 개면 합(OR).")]
        public ulong ReliefRaw = 0;

        // NeedType : ulong 은 Unity 직렬화(베이킹) 미지원 → ulong 백킹 + 프로퍼티 우회.
        public NeedType ReliefMask => (NeedType)ReliefRaw;

        [Tooltip("stamp BFS 최대 도로 칸 수(공급 반경).")]
        public int SupplyMaxDist = 15;

        [Tooltip("동시 방문 좌석(예약 기반). 0이면 VisitorOccupancy 안 굽음(방문 무제한 하위호환).")]
        public int VisitorSlots = 0;

        [Header("욕구 공급 — 오라형(단순 반경, 경찰서·광장류 — 커버형 욕구용)")]
        [Tooltip("오라로 해소하는 욕구 조합(NeedType ulong 비트합). 0이 아니고 반경>0이면 AuraSupplier.")]
        public ulong AuraReliefRaw = 0;

        public NeedType AuraReliefMask => (NeedType)AuraReliefRaw;

        [Tooltip("오라 반경(셀). 판정 = footprint 최근접 유클리드 제곱(dx²+dz² ≤ R²).")]
        public int AuraRadius = 0;

        [Header("생산")]
        [Tooltip("이 건물이 만드는 품목(RecipeDefs 키). None이 아니면 ProductionJob을 굽는다.")]
        public Commodity ProductionOutput = Commodity.None;

        [Header("재고")]
        [Tooltip("재고 칸 구성 → DynamicBuffer<StockEntry>. 비면 안 굽음.")]
        public StockSpec[] Stocks;

        [Header("물류 창고")]
        [Tooltip("true면 WarehouseTag(공유 풀 용량 기여 + Warehouse stamp 소스).")]
        public bool IsWarehouse = false;

        [Tooltip("창고 stamp BFS 최대 도로 칸 수. 지구 피치(24)=커버 유도의 근거 값 30 권장.")]
        public int WarehouseStampMaxDist = 30;

        [Header("전투")]
        [Tooltip("내구도(최대 체력). 0 이하면 SpawnConfig.BuildingDefaultHealth 폴백.")]
        public float MaxHealth = 0f;

        class Baker : Baker<BuildingAuthoring>
        {
            public override void Bake(BuildingAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.Dynamic);

                // ── 거주 / 고용 (같은 BuildingOccupancy 공유 — 거주 우선, 중복 설정 경고) ──
                if (a.Kind == BuildingKind.Residence)
                {
                    AddComponent<ResidenceBuilding>(e);
                    AddComponent(e, new BuildingOccupancy
                    { Current = 0, Capacity = math.max(1, a.Capacity) });
                    if (a.ProvidedJob != JobType.Unemployed)
                        Debug.LogWarning($"[BuildingAuthoring] {a.name}: 거주+고용 동시 설정 — " +
                                         "BuildingOccupancy 충돌로 고용은 무시됨(거주 우선)");
                }
                else if (a.ProvidedJob != JobType.Unemployed)
                {
                    AddComponent(e, new WorkplaceBuilding { ProvidedJob = a.ProvidedJob });
                    int cap = a.WorkerSlots > 0 ? a.WorkerSlots : math.max(1, a.Capacity);
                    AddComponent(e, new BuildingOccupancy { Current = 0, Capacity = cap });
                }

                // ── 욕구 공급(방문형) — owner는 스폰 주입(-1 표식) ──
                //   (구 ServiceBuilding 베이크는 은퇴 — 소비자 없는 잔재. 공급자 진실 = StampSupplier.)
                if (a.ReliefRaw != 0)
                {
                    AddComponent(e, new StampSupplier
                    {
                        OwnerLocalId = -1,
                        Relief       = a.ReliefMask,
                        MaxDist      = math.max(0, a.SupplyMaxDist),
                    });
                    if (a.VisitorSlots > 0)
                    {
                        AddComponent(e, new VisitorOccupancy { Current = 0, Capacity = a.VisitorSlots });
                        AddComponent(e, new ServiceStats { TodayServed = 0, YesterdayServed = 0 });
                    }
                }

                // ── 욕구 공급(오라형) — 커버형 욕구(경찰서·광장류) 자리. 소비 시스템은 후속 단계.
                if (a.AuraReliefRaw != 0 && a.AuraRadius > 0)
                    AddComponent(e, new AuraSupplier
                    { Relief = a.AuraReliefMask, Radius = a.AuraRadius });

                // ── 생산 ──
                if (a.ProductionOutput != Commodity.None)
                    AddComponent(e, ProductionJob.Make(a.ProductionOutput));

                // ── 재고 ──
                if (a.Stocks != null && a.Stocks.Length > 0)
                {
                    var buf = AddBuffer<StockEntry>(e);
                    for (int i = 0; i < a.Stocks.Length; i++)
                    {
                        var s = a.Stocks[i];
                        if (s.Commodity == Commodity.None || s.Capacity <= 0) continue;
                        buf.Add(new StockEntry
                        {
                            Commodity = s.Commodity,
                            Current   = math.clamp(s.Initial, 0, s.Capacity),
                            Capacity  = s.Capacity,
                            Role      = s.Role,
                        });
                    }
                }

                // ── 창고 — owner는 스폰 주입(-1 표식) ──
                if (a.IsWarehouse)
                    AddComponent(e, new WarehouseTag
                    { OwnerLocalId = -1, MaxDist = math.max(0, a.WarehouseStampMaxDist) });

                // ── 내구(값만 — 전투 컴포넌트 부착 골격은 SpawnSystem 공통 코드) ──
                if (a.MaxHealth > 0f)
                    AddComponent(e, new BuildingDurability { MaxHealth = a.MaxHealth });
            }
        }
    }
}
