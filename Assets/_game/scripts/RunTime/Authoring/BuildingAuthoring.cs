using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using CitySim;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  BuildingAuthoring — 건물 능력의 단일 작성 지점 (다지기 ①, 2026-07-11 개편 /
    //  2026-07-18 유저 정리: Kind 드롭다운 은퇴 + 용어 통일)
    //
    //  원칙(CLAUDE.md "능력은 컴포넌트로 산다"):
    //    건물이 "무엇이고 무엇을 하나"(거주·고용·공급·생산·재고·창고·내구)는 전부
    //    per-MainKey 능력 = **프리팹 엔티티의 컴포넌트**로 여기서 굽는다.
    //    SpawnSystem은 Instantiate 복사에 맡기고 per-instance 사실(owner)만 주입.
    //    배치검증·UI·AI는 PrefabLookup이 준 프리팹 엔티티의 같은 컴포넌트를 읽는다.
    //
    //  구조 = **직교(composable) 섹션 — 상호배타 아님이 의도**(2026-07-18 유저 확인):
    //    식당(공급+고용+생산+재고), 병원(오라+방문+고용)처럼 겸직이 정상. 설정 안 한
    //    섹션은 안 굽는다. 배타 스위치(구 Kind 드롭다운)는 이 의도와 모순이라 은퇴 —
    //    거주는 체크박스 하나(IsResidence).
    //    ⚠ 유일한 실제 배타 = **거주 vs 고용**: 둘 다 같은 BuildingOccupancy(정원
    //    카운트, 명단 아님 §0)를 쓰므로 한 건물에 동시 설정 금지(거주 우선, 경고).
    //
    //  용어(2026-07-18 유저 정리): 방문형 정원 = **이용 정원(ServiceSlots)** — 식당
    //    좌석·병원 병상·공원 수용을 중립으로 통칭(구 "방문자 좌석"은 환자·학생까지
    //    방문자로 불러 어색). 런타임 컴포넌트명(VisitorOccupancy)은 예약 기계 공유
    //    그대로 — 이름만 authoring에서 중립화.
    //
    //  owner 규약: StampSupplier/WarehouseTag의 OwnerLocalId는 per-instance 사실이라
    //    여기선 -1로 굽고 SpawnSystem이 스폰 시 SetComponent로 주입한다.
    //  주의: NeedType(ulong)은 직렬화 미지원 → ulong 백킹 + 프로퍼티(확립 패턴).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>레거시(2026-07-18 은퇴) — 구 프리팹 직렬화 호환 + 마이그레이션 전용.
    /// 전 프리팹 재저장 확인 후 enum·필드째 삭제 예정.</summary>
    public enum BuildingKind : byte
    {
        None = 0,
        Residence,   // → IsResidence 체크박스로 이관
        Workplace,   // (구 레거시 — ProvidedJob이 결정)
        Service,     // (구 레거시 — ReliefRaw가 결정)
    }

    /// <summary>
    /// 방문형 서비스 종류(2026-07-18 유저 요청 — ulong 비트 직접 입력은 매칭 불가).
    /// NeedType이 ulong이라 인스펙터 직렬화가 안 되므로, 실사용(공급자 = 단일 비트)에 맞춘
    /// int-백킹 단일 선택 드롭다운 → Baker가 NeedType 비트로 변환.
    /// ⚠ DiseaseCare는 욕구가 아니라 **상태(DiseasedTag) 해제 방문**(2026-07-18 유저 확인):
    ///   비트는 시스템 간 통화일 뿐, 욕구/상태 구분은 시민 측 라우팅이 담당(공급 기계 공유).
    /// 복수 비트 공급자가 필요해지면 이 필드를 배열로 확장(현재 전 건물 단일).
    /// </summary>
    public enum VisitService : byte
    {
        None = 0,
        Hunger,          // 식당 — NeedType.Hunger(bit0)
        Entertainment,   // 공원 — NeedType.LowEntertainment(bit3), 체류형
        Education,       // 학교 — NeedType.LowEducation(bit5), 체류형
        DiseaseCare,     // 병원 — NeedType.Disease(bit19), 상태 해제 방문(입원)
    }

    /// <summary>오라형(관리형) 서비스 종류 — VisitService와 동일 사유의 드롭다운.
    /// 전부 공무불만(CitizenCivic) 가중합으로 소비, 직종 = CivilServant.</summary>
    public enum AuraService : byte
    {
        None = 0,
        Safety,          // 경찰서 — NeedType.HighCrime(bit16)
        Healthcare,      // 병원 오라 — NeedType.PoorHealthcare(bit9), 발병 저항 전용
        Sanitation,      // 청소국 — NeedType.PoorSanitation(bit10)
        Administration,  // 관공서 — NeedType.PoorAdministration(bit13)
        Fire,            // 소방서 — NeedType.Fire(bit17)
    }

    public static class ServiceEnumOps
    {
        public static NeedType ToMask(VisitService s) => s switch
        {
            VisitService.Hunger        => NeedType.Hunger,
            VisitService.Entertainment => NeedType.LowEntertainment,
            VisitService.Education     => NeedType.LowEducation,
            VisitService.DiseaseCare   => NeedType.Disease,
            _                          => NeedType.None,
        };

        public static NeedType ToMask(AuraService s) => s switch
        {
            AuraService.Safety         => NeedType.HighCrime,
            AuraService.Healthcare     => NeedType.PoorHealthcare,
            AuraService.Sanitation     => NeedType.PoorSanitation,
            AuraService.Administration => NeedType.PoorAdministration,
            AuraService.Fire           => NeedType.Fire,
            _                          => NeedType.None,
        };

        // 레거시 raw → enum 역매핑(마이그레이션 전용). 미지 비트 = None(raw 유지·경고).
        public static VisitService VisitFromMask(NeedType m) => m switch
        {
            NeedType.Hunger           => VisitService.Hunger,
            NeedType.LowEntertainment => VisitService.Entertainment,
            NeedType.LowEducation     => VisitService.Education,
            NeedType.Disease          => VisitService.DiseaseCare,
            _                         => VisitService.None,
        };

        public static AuraService AuraFromMask(NeedType m) => m switch
        {
            NeedType.HighCrime          => AuraService.Safety,
            NeedType.PoorHealthcare     => AuraService.Healthcare,
            NeedType.PoorSanitation     => AuraService.Sanitation,
            NeedType.PoorAdministration => AuraService.Administration,
            NeedType.Fire               => AuraService.Fire,
            _                           => AuraService.None,
        };
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
        // ── 레거시 직렬화 호환(숨김) — OnValidate/Baker가 IsResidence로 이관 ──
        [HideInInspector] public BuildingKind Kind = BuildingKind.None;

        [Header("거주")]
        [Tooltip("체크 = 집(ResidenceBuilding). 거주 정원은 아래 ResidentSlots.\n" +
                 "⚠ 고용과 동시 설정 금지 — 같은 정원 카운트(BuildingOccupancy)를 공유(거주 우선).")]
        public bool IsResidence = false;

        [Tooltip("거주 정원(침상).")]
        [FormerlySerializedAs("Capacity")]
        public int ResidentSlots = 1;

        [Header("고용")]
        [Tooltip("제공 직업. Unemployed가 아니면 WorkplaceBuilding + 일자리 정원을 굽는다.\n" +
                 "오라 서비스 건물은 공공서비스=CivilServant.")]
        public JobType ProvidedJob = JobType.Unemployed;

        [Tooltip("일자리 정원(데스크). 0이면 ResidentSlots 값 폴백(레거시 프리팹 호환 — 새 프리팹은 직접 입력).")]
        public int WorkerSlots = 0;

        [Header("방문형 서비스 (도로 stamp 도달)")]
        [Tooltip("제공하는 방문 서비스. None이 아니면 StampSupplier를 굽는다.\n" +
                 "Hunger·Entertainment·Education = 욕구 해소 / DiseaseCare = 질병 상태 해제(입원).")]
        public VisitService VisitRelief = VisitService.None;

        // 레거시 ulong 비트 직렬화 호환(숨김) — OnValidate가 enum으로 이관, Baker 폴백.
        [HideInInspector] public ulong ReliefRaw = 0;

        // 해석된 마스크(enum 우선, 레거시 raw 폴백). NeedType(ulong)은 직렬화 미지원(확립 패턴).
        public NeedType ReliefMask
            => VisitRelief != VisitService.None ? ServiceEnumOps.ToMask(VisitRelief) : (NeedType)ReliefRaw;

        [Tooltip("stamp BFS 최대 도로 칸 수(공급 반경).")]
        public int SupplyMaxDist = 15;

        [Tooltip("동시 이용 정원 — 식당 좌석·병원 병상·공원/학교 수용 인원의 중립 통칭. " +
                 "예약 기반(출발 시 자리 확보). 0이면 안 굽음 = 이용 무제한(하위호환).")]
        [FormerlySerializedAs("VisitorSlots")]
        public int ServiceSlots = 0;

        [Header("오라형 서비스 (반경 커버 — 관리형: 치안·의료·환경·행정·소방)")]
        [Tooltip("제공하는 오라 서비스. None이 아니고 반경>0이면 AuraSupplier를 굽는다.\n" +
                 "전부 공무불만 가중합으로 소비, 직종 = CivilServant(Healthcare만 발병 저항 전용).")]
        public AuraService AuraType = AuraService.None;

        // 레거시 ulong 비트 직렬화 호환(숨김) — OnValidate가 enum으로 이관, Baker 폴백.
        [HideInInspector] public ulong AuraReliefRaw = 0;

        public NeedType AuraReliefMask
            => AuraType != AuraService.None ? ServiceEnumOps.ToMask(AuraType) : (NeedType)AuraReliefRaw;

        [Tooltip("오라 반경(셀). 판정 = footprint 최근접 유클리드 제곱(dx²+dz² ≤ R²).")]
        public int AuraRadius = 0;

        [Tooltip("근무자 1인당 담당 인원(관리형 서비스). 캐퍼(처리량) a = 배정 근무자수 × 이 값. " +
                 "⚠ 오라 건물은 반드시 ProvidedJob(공공서비스=CivilServant)+WorkerSlots+이 값 필요 — " +
                 "0이거나 근무자 없으면 무커버.")]
        public int AuraPerWorkerCoverage = 100;

        [Tooltip("서비스 품질 c(0~100) — 이 건물이 제공하는 값. 캐퍼 안 넘으면 이 값 그대로, " +
                 "넘으면 비율 감소(d = Quality × min(1, 캐퍼/부하)). 동종 겹치면 셀에서 합산(상한 100).")]
        public int AuraQuality = 80;

        [Header("생산")]
        [Tooltip("이 건물이 만드는 품목(RecipeDefs 키). None이 아니면 ProductionJob을 굽는다.")]
        public Commodity ProductionOutput = Commodity.None;

        [Header("채취 (footprint 아래 맵 자원 소모 — 무입력 생산 변형)")]
        [Tooltip("채취하는 맵 자원 TypeId(ResourceCatalog — Iron=2, Oil=7). -1 = 채취 건물 아님.\n" +
                 "설정 시: 배치는 매칭 자원 셀 위만 허용(≥1셀 필수), 생산 사이클마다 아래 자원을 소모, " +
                 "고갈되면 유휴. ProductionOutput(무입력 레시피)과 함께 설정할 것.")]
        public int ExtractResourceTypeId = -1;

        [Tooltip("생산 1사이클당 소모하는 자원량.")]
        public int ExtractAmountPerCycle = 1;

        [Tooltip("해상 공급자(수상 건물 — 도로·stamp 불가). 출력은 항만 창고(SeaRange) 반경으로 " +
                 "풀 접속(OffshorePushSystem). 레지스트리 BuildableOn=Water와 함께 설정.")]
        public bool IsOffshore = false;

        [Header("재고")]
        [Tooltip("재고 칸 구성 → DynamicBuffer<StockEntry>. 비면 안 굽음.")]
        public StockSpec[] Stocks;

        [Header("물류 창고")]
        [Tooltip("true면 WarehouseTag(공유 풀 용량 기여 + Warehouse stamp 소스).")]
        public bool IsWarehouse = false;

        [Tooltip("창고 stamp BFS 최대 도로 칸 수. 지구 피치(24)=커버 유도의 근거 값 30 권장.")]
        public int WarehouseStampMaxDist = 30;

        [Tooltip("항만 반경(셀, 유클리드). 0 = 항만 아님. 해상 공급자(IsOffshore)가 이 반경 안에 " +
                 "있으면 풀 접속 — 해안에 지어 바다 시추를 커버(도로 stamp와 직교, 겸직 가능).")]
        public int WarehouseSeaRange = 0;

        [Header("전투")]
        [Tooltip("내구도(최대 체력). 0 이하면 SpawnConfig.BuildingDefaultHealth 폴백.")]
        public float MaxHealth = 0f;

        // 레거시 마이그레이션(2026-07-18): 인스펙터에서 열리면 자동 이관 + 재저장 유도.
        //   에디터에서 안 열린 프리팹은 Baker 폴백(마스크 프로퍼티의 raw 폴백)이 커버 — 동작 무손실.
        void OnValidate()
        {
            if (Kind == BuildingKind.Residence)
            {
                IsResidence = true;
                Kind = BuildingKind.None;
                Debug.Log($"[BuildingAuthoring] {name}: 레거시 Kind=Residence → IsResidence 자동 이관 — 프리팹 저장 요망.");
            }
            if (VisitRelief == VisitService.None && ReliefRaw != 0)
            {
                var v = ServiceEnumOps.VisitFromMask((NeedType)ReliefRaw);
                if (v != VisitService.None)
                {
                    VisitRelief = v; ReliefRaw = 0;
                    Debug.Log($"[BuildingAuthoring] {name}: 레거시 ReliefRaw → VisitRelief={v} 자동 이관 — 프리팹 저장 요망.");
                }
                else
                    Debug.LogWarning($"[BuildingAuthoring] {name}: ReliefRaw={ReliefRaw} 미지 비트 조합 — " +
                                     "enum 이관 불가(raw로 계속 베이크). 복수 비트면 VisitService 배열 확장 검토.");
            }
            if (AuraType == AuraService.None && AuraReliefRaw != 0)
            {
                var v = ServiceEnumOps.AuraFromMask((NeedType)AuraReliefRaw);
                if (v != AuraService.None)
                {
                    AuraType = v; AuraReliefRaw = 0;
                    Debug.Log($"[BuildingAuthoring] {name}: 레거시 AuraReliefRaw → AuraType={v} 자동 이관 — 프리팹 저장 요망.");
                }
                else
                    Debug.LogWarning($"[BuildingAuthoring] {name}: AuraReliefRaw={AuraReliefRaw} 미지 비트 — " +
                                     "enum 이관 불가(raw로 계속 베이크).");
            }
        }

        class Baker : Baker<BuildingAuthoring>
        {
            public override void Bake(BuildingAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.Dynamic);

                // ── 거주 / 고용 (같은 BuildingOccupancy 공유 — 유일한 배타, 거주 우선) ──
                //   Kind==Residence 폴백 = 미재저장 레거시 프리팹 커버(OnValidate 주석 참조).
                bool residence = a.IsResidence || a.Kind == BuildingKind.Residence;
                if (residence)
                {
                    AddComponent<ResidenceBuilding>(e);
                    AddComponent(e, new BuildingOccupancy
                    { Current = 0, Capacity = math.max(1, a.ResidentSlots) });
                    if (a.ProvidedJob != JobType.Unemployed)
                        Debug.LogWarning($"[BuildingAuthoring] {a.name}: 거주+고용 동시 설정 — " +
                                         "BuildingOccupancy 충돌로 고용은 무시됨(거주 우선)");
                }
                else if (a.ProvidedJob != JobType.Unemployed)
                {
                    AddComponent(e, new WorkplaceBuilding { ProvidedJob = a.ProvidedJob });
                    int cap = a.WorkerSlots > 0 ? a.WorkerSlots : math.max(1, a.ResidentSlots);
                    AddComponent(e, new BuildingOccupancy { Current = 0, Capacity = cap });
                    // 직무 효과 스칼라(2026-07-12) — 유인 직장 공통. 0 시작(직원 출근 시 갱신).
                    AddComponent(e, new StaffEffect { Factor = 0f });
                }

                // ── 방문형 서비스 — owner는 스폰 주입(-1 표식) ──
                //   ReliefMask = enum 우선, 레거시 raw 폴백(미재저장 프리팹 커버).
                //   (구 ServiceBuilding 베이크는 은퇴 — 소비자 없는 잔재. 공급자 진실 = StampSupplier.)
                if (a.ReliefMask != NeedType.None)
                {
                    AddComponent(e, new StampSupplier
                    {
                        OwnerLocalId = -1,
                        Relief       = a.ReliefMask,
                        MaxDist      = math.max(0, a.SupplyMaxDist),
                    });
                    if (a.ServiceSlots > 0)
                    {
                        AddComponent(e, new VisitorOccupancy { Current = 0, Capacity = a.ServiceSlots });
                        AddComponent(e, new ServiceStats { TodayServed = 0, YesterdayServed = 0 });
                    }
                }

                // ── 오라형 서비스 (관리형 — enum 우선, 레거시 raw 폴백) ──
                if (a.AuraReliefMask != NeedType.None && a.AuraRadius > 0)
                    AddComponent(e, new AuraSupplier
                    {
                        Relief            = a.AuraReliefMask,
                        Radius            = a.AuraRadius,
                        PerWorkerCoverage = a.AuraPerWorkerCoverage,
                        Quality           = a.AuraQuality,
                    });

                // ── 생산 ──
                if (a.ProductionOutput != Commodity.None)
                    AddComponent(e, ProductionJob.Make(a.ProductionOutput));

                // ── 채취(2026-07-19) — 무입력 생산 변형: footprint 아래 맵 자원 소모 ──
                if (a.ExtractResourceTypeId >= 0)
                {
                    AddComponent(e, new ResourceExtractor
                    {
                        ResourceTypeId = a.ExtractResourceTypeId,
                        AmountPerCycle = math.max(1, a.ExtractAmountPerCycle),
                    });
                    if (a.ProductionOutput == Commodity.None)
                        Debug.LogWarning($"[BuildingAuthoring] {a.name}: 채취 설정인데 " +
                                         "ProductionOutput=None — 생산 사이클이 없어 아무것도 안 캔다.");
                }
                if (a.IsOffshore)
                    AddComponent<OffshoreSupplier>(e);

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
                    {
                        OwnerLocalId = -1,
                        MaxDist      = math.max(0, a.WarehouseStampMaxDist),
                        SeaRange     = math.max(0, a.WarehouseSeaRange),
                    });

                // ── 내구(값만 — 전투 컴포넌트 부착 골격은 SpawnSystem 공통 코드) ──
                if (a.MaxHealth > 0f)
                    AddComponent(e, new BuildingDurability { MaxHealth = a.MaxHealth });
            }
        }
    }
}
