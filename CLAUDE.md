# 프로젝트 컨텍스트

## 기술 스택
- Unity 6 + URP 17.3 (Universal Render Pipeline)
- ECS / DOTS — `com.unity.entities` 1.4.5, `com.unity.entities.graphics` 1.4.19
- `com.unity.physics` 1.4.6, `com.unity.ai.navigation` 2.0.10
- `com.unity.inputsystem` 1.18.0, `com.unity.addressables` 2.8.1
- `com.unity.visualeffectgraph` 17.3.0
- 솔로 개발 / 네임스페이스: `CitySim`

## 게임 개요
도시 건설 메커니즘과 실시간 전략 전투를 결합한 전략 게임.
시민 시뮬레이션(욕구·직업·물류)이 군사 효율성으로 직접 연결되는 구조.

---

# 작업 규칙

## 세션 시작 시
1. 루트의 `PROGRESS.md`를 **반드시 먼저 읽을 것**. 현재 진행 상황과 다음 단계가 여기 있음.
2. 해당 주제의 관련 소스 파일을 디스크에서 다시 읽어 최신 상태를 확인할 것.
   (다른 세션이나 에디터에서 수정됐을 수 있음 — 컨텍스트의 스냅샷을 신뢰하지 말 것)

## 세션 종료 시
- `PROGRESS.md`의 해당 섹션을 갱신할 것: 완료한 것, 미결정 사항, 다음 단계.

## 작업 범위
- **`Assets/_game/scripts/`** 하위만 수정. (`Assets/Scripts/`가 아님)
- `Library/`, `Temp/`, `obj/`, `Logs/` 는 절대 건드리지 말 것.
- 셰이더: `Assets/_game/Shader_Graph/`, `Assets/_game/Weather/`
- 컴퓨트: `MinimapCompute.compute`, `SnowAccumulation.compute`

---

# 디렉토리 구조

```
Assets/_game/scripts/
├── Editor/                      # Unity Editor 전용 (UNITY_EDITOR 가드)
│   ├── MapEditor/               # MapEditorWindow — 지형/자원 레이어 편집
│   ├── PrefabRegister/          # GamePrefabRegistryEditor, PrefabRegistryWindow
│   ├── AddressableGroupUtility.cs
│   ├── MapBoundaryGizmo.cs
│   └── VariantSelectionWindow.cs
│
├── RunTime/
│   ├── Authoring/               # Baker 전용 MonoBehaviour
│   │   ├── BuildingAuthoring.cs
│   │   ├── GameClockAuthoring.cs
│   │   ├── GamePrefabRegistryAuthoring.cs
│   │   ├── NeedMappingAuthoring.cs
│   │   └── TeamStartPoint.cs
│   │
│   ├── Components/              # IComponentData / IBufferElementData 정의
│   │   ├── BuildingOccupancy.cs   — 건물 정원·예약, 건물 분류 태그
│   │   ├── CitizenComponents.cs   — 시민 엔티티 전체 골격
│   │   ├── GameClock.cs           — 게임 시간 싱글톤
│   │   ├── GridLayers.cs          — 5종 레이어 싱글톤 + 셀 구조체
│   │   ├── GridMap.cs             — 정적 건물 점유 싱글톤
│   │   ├── LogisticsComponents.cs — Commodity, StockEntry, WarehouseTag
│   │   ├── LookupComponents.cs    — PrefabLookup, NeedLookup 조회 구조
│   │   ├── MapLoadComponents.cs   — MapLoadState, SpawnRequest 변형들
│   │   ├── PrefabLookup.cs        — (MainKey,VariantKey)→Entity 싱글톤
│   │   ├── ProductionComponents.cs — RecipeDef, RecipeDefs, ProductionJob
│   │   ├── RoadComponents.cs      — Road, RoadCell, PlaceRoadCommand 등
│   │   ├── SpawnComponents.cs     — SpawnRequest, BuildingFootprint, BuildingEntrance
│   │   ├── StampComponents.cs     — StampLayers, SupplierRef, StampDirtyEvent
│   │   └── TeamData.cs
│   │
│   ├── Registry/
│   │   ├── GamePrefabRegistry.cs  — SO: RegistryItem, PrefabCategory, MainKeyRange
│   │   └── RoadPrefabRegistry.cs
│   │
│   ├── Services/
│   │   ├── DlcBootstrap.cs / DlcOwnershipService.cs
│   │   ├── FactionBaseConfig.cs
│   │   ├── LookupHelper.cs
│   │   └── SkirmishLobby.cs       — 플레이어 슬롯 모델 (최대 8)
│   │
│   └── Systems/                 # ISystem (partial struct) 전용
│       ├── AiCityGrowthSystem.cs  — 자율 도시 확장 AI
│       ├── BlockOps.cs            — 구획 연산
│       ├── BuildingPlacement.cs   — 건물 배치 시스템
│       ├── CellTypeSystem.cs
│       ├── CitizenAssignment.cs   — 집·직장 배정
│       ├── CitizenMovementSystem.cs
│       ├── CitizenSpawn.cs
│       ├── CivilianBFS.cs         — 도로망 BFS (시민 경로)
│       ├── ConditionUpdateSystem.cs
│       ├── EntranceOps.cs         — 건물 입구 계산 유틸
│       ├── FactionBaseSpawnSystem.cs
│       ├── GameClockSystem.cs     — TotalSeconds 누적 + 경계 플래그
│       ├── GridInitSystem.cs      — GridLayers/GridMap 생성·해제
│       ├── HungerSystem.cs
│       ├── LayerPainters.cs
│       ├── LogisticsPullSystem.cs — 재고 pull (par-level 유지)
│       ├── LogisticsPushSystem.cs — 재고 push (창고 이동)
│       ├── LookupBuildSystem.cs
│       ├── MapLoadSystem.cs / MapLoaderSystem.cs
│       ├── NeedDecisionSystem.cs
│       ├── PrefabLookupBuildSystem.cs
│       ├── ProductionSystem.cs    — 레시피 기반 생산 사이클
│       ├── RoadBuildController.cs / RoadBuildPreview.cs / RoadKeyBuild.cs
│       ├── RoadSystem.cs          — 도로 배치·철거·비트마스크 갱신
│       ├── ServiceSearchSystem.cs — stamp BFS + 욕구 공급자 매칭
│       ├── SpawnSystem.cs         — SpawnRequest → 실 엔티티 생성
│       └── StampRebuildSystem.cs  — dirty 플레이어 stamp 재BFS
│
├── Shared/                      # Editor + Runtime 공유 (순수 데이터/열거형)
│   ├── DlcAddressConfig.cs / DlcSubSceneManifest.cs
│   ├── FactionConfig.cs
│   ├── MapData.cs / MapMeta.cs
│   ├── NeedType.cs              — NeedType(ulong flags), NeedTypeOps
│   ├── RoadDirection.cs         — RoadDir(4bit), RoadDirOps
│   └── VariantSettings.cs
│
├── Unit/
│   ├── auth/                    — 유닛 Baker (CombatWeaponAuthoring 등)
│   ├── Components/              — UnitMovementComponents
│   ├── System/                  — 전투·이동·렌더러 시스템
│   └── Tests/                   — UnitPathfindingTests
│
├── camctrl/
│   └── CamCtrl.cs
│
└── test/                        (현재 비어 있음)
```

---

# 핵심 데이터 모델

## 그리드 레이어 (`GridLayers` 싱글톤)
`GridInitSystem`이 `OnCreate`에서 할당, `OnDestroy`에서 해제.

| 레이어 | 타입 | 목적 | 변경 주기 |
|---|---|---|---|
| `TerrainLayer` | `NativeHashMap<int2, TerrainCell>` | 지형 타입·높이 | 맵 로드 시 고정 |
| `ResourceLayer` | `NativeHashMap<int2, ResourceCell>` | 채취 자원 | 채취 시 |
| `OccupancyLayer` | `NativeHashMap<int2, OccupancyCell>` | 건설 가능 여부 | 배치·철거 시 |
| `RoadLayer` | `NativeHashMap<int2, RoadCell>` | BFS용 도로망 | 도로 배치·철거 시 |
| `TerritoryLayer` | `NativeHashMap<int2, int>` | 플레이어 영역(LocalId) | 전투·점령 시 |
| `BlockLayer` | `NativeHashMap<int2, BlockCell>` | 저해상도 구획 메타 | 구획 등록 시 |

`GridMap` (`BuildingCells: NativeHashMap<int2, Entity>`) — 정적 건물 점유. `GridLayers`와 별개.

## Stamp 인프라 (`StampLayers` 싱글톤)
- 슬롯 0~7 (`StampLayers.MaxPlayers = 8`) = LocalId별 독립 `NativeParallelMultiHashMap<int2, SupplierRef>`.
- 중첩 네이티브 컨테이너 금지 우회: 슬롯을 `_0.._7` 개별 필드로 펼치고 인덱서로 접근.
- `DirtyMask` (byte 비트) → `StampDirtyEvent` 수집 → `StampRebuildSystem` 라운드로빈 재BFS.
- 무효화 회피: 낡은 stamp를 패치하지 않고 `Clear()` 후 전체 재BFS.
- `StampKind`: `Supplier`(욕구 공급자), `Warehouse`(물류 창고).

## GameClock 싱글톤
- 진실의 원천: `TotalSeconds (double)` 하나. 시/일/주/월은 모두 파생.
- `GameClockSystem`이 경계 플래그(`HourChanged`, `DayChanged`, …) 한 곳에서 계산 → 다른 시스템이 중복 계산 불필요.
- 기본값: `SecondsPerDay = 1200f` (현실 20분 = 게임 하루).
- `StampRebuildSystem` 등 저빈도 시스템은 `HourChanged` 게이트로 호출 횟수 제한.

## 물류 3티어
```
원재료(Raw)  →  중간재(Intermediate)  →  완성품(Final)
   Grain              Flour                  Meal
```
- `StockEntry` (`DynamicBuffer<StockEntry>`): 품목·현재량·용량·역할(`Input/Output/Store/LocalFinal`).
- 임계(`Reorder/Target/Discharge`)는 `Capacity × Pct/100` 정수 나눗셈 — 결정적, float artifact 없음.
- `LogisticsPullSystem`: 입력 재고가 Reorder 이하 → Target까지 pull.
- `LogisticsPushSystem`: 출력 재고가 Discharge 초과 → 창고로 push.
- 완성품은 물류 이동 없음 (`StockRole.LocalFinal`), **생산 입력 불가 (설계 원칙)**.
  모든 레시피의 재료는 창고를 경유하는 Raw·Intermediate만 사용한다.
  고품질 완성품이 필요하면 Final을 재활용하는 것이 아니라,
  더 좋은/많은 Raw·Intermediate 조합으로 레시피를 설계한다.
  (순환 의존 방지 + 물류망 단순화 — 영구 원칙)

## 생산 시스템
- `RecipeDefs.Get(Commodity output)` — Burst-safe 정적 스위치.
- `ProductionJob`: `Progress < 0` = 대기, `≥ 0` = 진행, `BaseDuration` 도달 시 완료.
- 출력 포화 시 `Progress`를 `BaseDuration`에 클램프(공간 생길 때 완료).
- `ProductionSystem`은 메인스레드: 버퍼 alias 회피 + 저빈도.

## 프리팹 레지스트리
- `GamePrefabRegistry` (ScriptableObject, DLC 1개당 1 SO):
  - `items[]: RegistryItem` — `(MainKey, VariantKey)` → 프리팹.
  - `Entrances[]` — Building MainKey → 입구 정의.
  - `NeedMaps[]` — NeedMask → MainKey.
- `MainKeyRange` 구역: Road(1~999), Building(1000~4999), Environment(5000~6999), CombatUnit(7000~8999), Projectile(9000~9499), Effect(9500~9999).
- 도로: `VariantKey = RoadDir` 비트마스크(1~15).
- `NeedType : ulong`은 Unity 직렬화 미지원 → `ulong ReliefRaw` 백킹 필드 + `NeedType Relief` 프로퍼티.

## 프리팹 메타데이터 3역할 (인스턴스 대상 결정·해석 골격)
"무엇을 인스턴스할까"를 푸는 모든 데이터는 아래 **세 역할 중 하나**에 속한다.
역할이 소유 정책(독점/공유)을 결정한다. 새 메타데이터는 반드시 한 역할에 배치할 것.

| 역할 | 무엇 | 소유 | 예 |
|---|---|---|---|
| **해석 (Resolution)** | key → 엔티티 | **공유 단일** (칸막이 금지) | `MainKey`, `VariantKey`, `PrefabLookup` |
| **결정 (Decision)** | 상황 → key | **시스템 독점** (도메인별 테이블) | `NeedMaps`→`NeedLookupL2`, `RoadKey`, (미래) 병종표 |
| **능력 (Capability)** | 인스턴스가 무엇을 하나 | **공유 사실** (아이템 태그, 여러 시스템이 질의) | `ReliefRaw`/`IsSupplier`, `Size`, `Entrance`, `BuildableOn`, `IsRoadMaintenance` |

- **불변식**: 결정자는 여럿이지만 모두 산출은 `(MainKey, VariantKey)` 하나로 수렴 →
  단일 해석기(`PrefabLookup`)로 합류. 어떤 결정 테이블도 프리팹을 저장하지 않는다.
- **독점은 결정에서만**: 시스템마다 판단 기준(입력 형태)이 다르므로 결정 테이블은 그 시스템 독점이 옳다.
  단 **능력 데이터는 독점 금지** — 여러 시스템이 질의하므로 단일 출처로 두고 복사하지 않는다.
- **새 필드 리트머스**: "판단 기준인가(→결정·독점) / 그 물건의 속성인가(→능력·공유 단일 출처) / key를 엔티티로 바꾸나(→해석·공유)".
- **결정 테이블 거처 규칙**: 아이템 능력에서 **파생 가능**한 정책은 아이템과 동거(자동생성+얇은 오버라이드, 예 `NeedMaps`),
  어느 속성으로도 표현 안 되는 **순수 외부 정책**만 별도 SO(예 `RoadPrefabRegistry`).
- **A↔B drift 주의**: `NeedMaps`(결정: 욕구→key)와 `ReliefRaw`(능력: 건물→푸는 욕구)는 *반대 방향의 한 쌍*이다.
  중복이 아니라 서로 일치해야 하는 관계 → 모든 `NeedMaps` 행의 MainKey는 `IsSupplier && ReliefRaw ⊇ NeedMask`인
  아이템을 가져야 한다. `Validate()`에서 교차검증(미구현 — 오픈 이슈). `ReliefRaw`가 기본 관계의 단일 출처.

## 시민 컴포넌트 설계
| 분류 | 컴포넌트 | 특징 |
|---|---|---|
| 핫 | `CitizenConditions`, `Hunger`, `CitizenNeeds`, `CitizenState` | 매 틱 변경 |
| 콜드 | `CitizenAttributes`, `JobData` | 불변 또는 희소 변경 |
| 소속 | `CitizenResidence` (집·직장) | 잘 안 바뀜 |
| 동적 | `CitizenOwner` (SharedComponent, LocalId) | 청크 분리 → 플레이어별 일괄 처리 |

- 욕구: 개별 `IComponentData` 모델. 해소 = Level 감소 (구조 변경 0).
- 건물은 거주자 명단 없음 — `BuildingOccupancy.Current` 카운트만 보유.
- `UnassignedTag` → 미배정 시민만 쿼리 (매 프레임 전체 스캔 회피).

## 멀티셀 도로 (N×N 정사각형)
- `Road.Size`, `Road.FootprintOrigin`, `RoadCell.Size`, `RoadCell.FootprintOrigin` 추가.
- 배치 시 N×N 전체 셀 등록 → 내부 방향 재계산 → 외곽 이웃 갱신.
- 철거 시 `FootprintOrigin + Size`로 전체 footprint 제거.
- 시각 메시 스케일링은 미구현 (❓ 오픈 이슈).

---

# 코딩 원칙

## 일반
- **Idiomatic ECS 우선.** GameObject 하이브리드 접근 지양.
- **성능 우선**: Burst, Jobs, 캐시 효율, GPU 파이프라인 항상 염두.
- **명확한 관심사 분리**: helper = 사실(계산), system = 결정(로직).
- 수학적으로 우아하고 구조적으로 깔끔한 해법을 ad-hoc 방식보다 선호.

## 컴포넌트 설계 원칙
- **컴포넌트는 가볍게**: 하나의 컴포넌트에 모든 것을 담지 않는다. 작고 단일 책임인
  컴포넌트 여러 개의 조합이 시스템과 쿼리의 재사용성을 높인다.
- **읽기/쓰기 분리 설계**: 컴포넌트를 정의할 때 설계 범위 안에서 어느 시스템이 쓰고
  어느 시스템이 읽는지를 먼저 파악한다. 쓰기 시스템과 읽기 시스템이 겹치지 않도록
  컴포넌트를 분리하면 Job 병렬화와 의존성 선언이 자연스러워진다.
- **핫/콜드 분리**: 매 틱 변하는 데이터(핫)와 거의 변하지 않는 데이터(콜드)를
  별도 컴포넌트로 나눈다 — 청크 캐시 효율 + 쿼리 필터 단순화.

## Job / Burst 설계 원칙
- **의존성 중심 병렬 설계**: 메인스레드에서 프레임 중간에 `JobHandle.Complete()`를
  조기 호출하지 않는다. 시스템 간 실행 순서는 `state.Dependency` 체인과
  `[UpdateAfter/Before]` 어트리뷰트로 표현하고, 동기화는 프레임 끝에서만 일어난다.
- **Burst 적극 활용**: 수치 연산이 있는 Job은 `[BurstCompile]` 기본 적용. 관리형
  타입(string, List, class)이 섞이는 경우에만 Burst 제외 후 별도 Job으로 분리.
- **Job 설계 단위**: 하나의 Job이 하나의 데이터 변환만 담당하도록 좁게 설계한다.
  복합 처리가 필요하면 Job 체인(Schedule → Schedule)으로 연결.

## 시민·물류 시스템 접근 방식
- **즉각적 갱신 불필요**: 시민 욕구·물류 재고는 매 틱 정밀 동기화 대상이 아니다.
  게임 시간 경계(`HourChanged` 등) 또는 주기적 게이트로 처리 빈도를 제한한다.
- **통계적·점진적 접근**: 개별 시민/건물의 상태는 틱마다 소량씩 변화하는 모델.
  한 프레임에 전체를 재계산하지 않고, 분산(라운드로빈·dirty 플래그)시켜 처리한다.
- **결과 지연 허용**: 시민 이동 완료, 재고 보충 등은 1~수 틱 후 반영되어도 무방.
  즉각 반영을 위한 `Complete()` 강제나 메인스레드 동기화는 금지.

## 대량 데이터 핫패스 구조 변경 금지
- **핫패스에서 구조 변경 없음**: 대량 엔티티를 주기적으로 순회하는 시스템(시민 이동,
  물류 pull/push, stamp BFS 등)에서는 `AddComponent` / `RemoveComponent` /
  `CreateEntity` / `DestroyEntity` 같은 구조 변경을 직접 호출하지 않는다.
  구조 변경이 필요하면 ECB를 사용해 프레임 끝에 일괄 적용.
- **참조로 접근한 엔티티 값 변경 회피**: Job 안에서 `ComponentLookup`·`BufferLookup`
  등 참조(랜덤 액세스)로 얻은 엔티티의 데이터를 쓰는 행위는 alias 위험과 Job 스케줄링
  제약을 유발한다. **대량 엔티티를 주기적으로 처리하는 병렬 Job**에서는 가능하면
  읽기(`ReadOnly`)만 하고, 쓰기가 필요하면 결과를 별도 NativeArray/NativeQueue에
  모아 후속 Job 또는 메인스레드에서 적용한다.
  (소수 엔티티 갱신이나 비주기 이벤트는 ECB 단일 패스로 충분.)

## ECS 세부 관례 (확립된 패턴)
- `state.Dependency` 할당은 선택이 아닌 **필수**.
- 청크 마이그레이션을 피하려면 `IEnableableComponent` 사용 (구조 변경 대신 토글).
  - 사례: `IncomingHitEvent`, `AttackerNotification`, `TargetNotification` 버퍼.
- ECB `ParallelWriter` sort key는 버퍼 슬롯이 아니라 **정렬용 태그**.
- 의도적 Write access 선언으로 시스템 간 의존성 강제 (확립된 기법).
- ECS→UI 통신: `GameDataStore` + C# 이벤트 + `NativeQueue` 브리지.
- "눈에만 안 보이게": `MaterialMeshInfo.Mesh` 조작 (비구조적, Burst 병렬).
- 비균일 스케일: `PostTransformMatrix` (SDF 셰이더에서 X/Z 다를 때 UV 보정 필요).
- 중첩 네이티브 컨테이너 금지 → 개별 필드 펼치기 + 인덱서 패턴 (예: `StampLayers._0.._7`).

## 시스템 작성 템플릿
```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SomePrecedingSystem))]
public partial struct MySystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<GameClock>();   // 필요한 싱글톤 가드
    }

    public void OnUpdate(ref SystemState state)
    {
        // 항상 state.Dependency 처리
        var job = new MyJob { ... };
        state.Dependency = job.Schedule(state.Dependency);
    }
}
```

## 싱글톤 수명주기 패턴 (`GridInitSystem`, `StampInitSystem` 등)
```csharp
public void OnCreate(ref SystemState state)
{
    if (SystemAPI.HasSingleton<MySingleton>()) return;
    var s = new MySingleton { ... /* Allocator.Persistent */ };
    var e = state.EntityManager.CreateEntity(typeof(MySingleton));
    state.EntityManager.SetComponentData(e, s);
}

public void OnDestroy(ref SystemState state)
{
    if (!SystemAPI.HasSingleton<MySingleton>()) return;
    var s = SystemAPI.GetSingleton<MySingleton>();
    // s.SomeMap.Dispose();
}
```

## 값 타입 싱글톤 수정
구조체 싱글톤은 값 복사이므로 수정 후 반드시 다시 써야 함:
```csharp
var stamp = SystemAPI.GetSingleton<StampLayers>();
stamp.MarkDirty(localId);
SystemAPI.SetSingleton(stamp);   // 필수 — 안 하면 변경 소실
```

## 단발성 이벤트 패턴
```csharp
// 발행: 이벤트 엔티티 생성
ecb.CreateEntity(); ecb.AddComponent(e, new StampDirtyEvent { OwnerLocalId = id });

// 수집: 읽고 파괴
foreach (var (evt, e) in SystemAPI.Query<RefRO<StampDirtyEvent>>().WithEntityAccess())
{
    // 처리
    ecb.DestroyEntity(e);
}
```

---

# 확립된 아키텍처 결정

## GPU 파이프라인
- Burst 수집 잡 + 논-Burst 매니지드 GPU 호출 분리.
- 매니지드 컴포넌트 싱글톤이 `GraphicsBuffer`/`ComputeShader` 참조 보유 (`SystemAPI.ManagedAPI` 접근).
- `AsyncGPUReadback` 논-스톨 결과 → `RenderMeshIndirect`로 직접 파이핑.

## 미니맵
- 베이크된 지형 `RenderTexture` 베이스 + compute shader 동적 오버레이.
- 주의: compute shader 전역 변수 초기화는 무시됨 → C#에서 전부 설정.
- `.compute` 한글 주석 → `INVALID_UTF8_STRING` 유발 (영문 주석 사용).
- `enableRandomWrite`는 `Create()` 전에 설정.

## 날씨
- 파티클당 엔티티 방식 폐기(성능 한계).
- VFX Graph(비주얼) + ECS(`SnowAccumulationSystem`, 디포머, 젖은 바닥 셰이더 파라미터).

## 렌더링
- "눈에만" 투명: `MaterialMeshInfo.Mesh` 조작 (비구조적·Burst·수천 유닛 가능).
- 엔진 생성 자식 렌더링 토글: 루트의 `LinkedEntityGroup` 순회.
- 베이킹 패턴: Baker는 최소 변환, `PostBakingSystemGroup`에서 복합 조립.

## 셰이더
- URP 물: Gerstner 파도, depth texture 교차 해안 포말, Fresnel 반사, 코스틱.
- URP 선택 마커: `sdRoundedBox` SDF, 비균일 스케일 UV 보정 (`PostTransformMatrix`).

## 투사체 / 데미지
- 3종: 호밍 / 저장 위치 직격 / 범위.
- 명중/회피 판정 포함.

## 저장 / 로드
- 순차 `uint` 인스턴스 ID는 저장/로드 경계에서만 사용.
- 로드 중 임시 `NativeHashMap<uint, Entity>` 구축.
- JSON + GZip, 2패스 로드 시퀀스.

## Input System
- Action Asset 계층 구조.
- 런타임 리바인딩: 전체 에셋 순회로 중복 감지.
- Common Map 패턴 (항상 활성화된 공유 액션).

## City Expansion AI
- `NativeHashMap<int2, CellData>` 싱글톤 셀.
- 팀 단위 `DynamicBuffer<BuildRequest/Response>`.
- 불변식: 도로는 항상 닫힌 사각형 / 볼록 정점만 확장 후보 (사분면 압축) / `ClaimedTeam` = 영구 영토 / 유효 정점 쌍 없을 때 U자 폴백.

---

# 자주 발생하는 실수 방지

| 상황 | 잘못된 접근 | 올바른 접근 |
|---|---|---|
| 구조체 싱글톤 수정 | 그냥 수정 후 무시 | `SetSingleton()` 호출 필수 |
| `NeedType` 직렬화 | `NeedType` 필드 직접 사용 | `ulong ReliefRaw` + 프로퍼티 우회 |
| 멀티셀 도로 철거 | 단일 셀만 제거 | `FootprintOrigin + Size` 전체 footprint 제거 |
| 중첩 네이티브 컨테이너 | `NativeHashMap<int2, NativeList<...>>` | 필드 펼치기 + 인덱서 패턴 |
| compute shader 한글 주석 | `// 한글` | 영문 주석 사용 |
| `enableRandomWrite` 타이밍 | `Create()` 후 설정 | `Create()` 전에 설정 |
| 시민 명단 보관 | 건물에 List<Entity> | `BuildingOccupancy.Current` 카운트만 |
| 핫패스에서 구조 변경 | 순회 중 `AddComponent` 직접 호출 | ECB로 모아 프레임 끝 일괄 적용 |
| 조기 동기화 | 프레임 중간 메인스레드에서 `Complete()` 강제 | `state.Dependency` 체인 + `[UpdateAfter]`로 순서 선언 |
| 시민·물류 즉각 재계산 | 매 틱 전체 재계산 + 메인스레드 동기화 | `HourChanged` 게이트 + 라운드로빈 분산 |
| 랜덤 액세스 쓰기 (대량 병렬) | 병렬 Job 내 `ComponentLookup` 직접 쓰기 | 결과를 NativeArray에 수집 → 후속 패스에서 적용 |
