# 진행 상황 (PROGRESS)

> 작업 시작 전 해당 섹션을 읽고, 종료 시 갱신할 것.
> 형식: ✅ 완료 / 🔧 진행 중 / ⬜ 다음 단계 / ❓ 미결정

---

## Spatial Query (Radar / Vision / Minimap)
아키텍처: **단일 position/index 업데이트 시스템**이 독립적인 백그라운드 HashMap들에 공급, 결과는 사전 계산해 캐싱.

- ✅ 단일 position/index 업데이트 시스템 설계 확정
- ✅ 독립 백그라운드 HashMap 구조 (radar / vision / minimap 각각)
- ⬜ (다음 단계 기입)
- ❓ (미결정 사항 기입)

---

## 물류 운반자 비주얼 (Carrier Visual)
- ✅ `LogisticsCarrierRequest` — 재고 이전 직후 LogisticsPullSystem이 발행 (창고셀→건물셀 + OwnerLocalId)
- ✅ `CarrierPrefabSingleton` + `CarrierPrefabAuthoring` — SubScene에 운반자 프리팹 연결
- ✅ `CarrierSpawnSystem` — CivilianBFS 경로 계산 후 운반자 엔티티 스폰 (비주얼 전용, 재고 무관)
- ✅ `CarrierMoveSystem` — 도로 경로 선형 보간 이동 (Speed=3 셀/초), 목적지 도착 후 자동 소멸
- ⬜ Unity 에디터 설정 필요: SubScene에 `CarrierPrefabAuthoring` 추가 + 운반자 프리팹(소형 큐브/구) 할당
- ❓ LogisticsPushSystem도 운반자 비주얼 추가할지 여부 미결정

---

## City-builder (그리드 기반)
- ✅ `RoadDir` 4비트 비트마스크
- ✅ `NativeHashMap<int2, Entity>` 싱글톤 `GridMap`
- ✅ 건물 접근 도로 수집
- ✅ 공급/소비자 매칭: stamp BFS + `ServiceSearchSystem`
- ✅ `StampSupplier` 자동 부착: `RegistryItem.IsSupplier/Relief/SupplyMaxDist` → `BakedPrefabEntry` → `PrefabMeta` → `SpawnRequest` → `SpawnSystem`
- ✅ 물류 3티어 (원재료→중간재→완성품): `StockEntry`, `LogisticsPullSystem`, `LogisticsPushSystem`
- ✅ 생산 시스템: `RecipeDef`/`RecipeDefs`(static Burst-safe) + `ProductionJob` + `ProductionSystem`
    - Progress=-1 대기 / ≥0 진행(게임초 누적 × SkillFactor) / 완료→Output 추가 / 출력 포화 시 클램프
    - stub 레시피: Grain×2→Flour×1(10s), Flour×1→Meal×2(8s)
    - 테스트: `ProductionTestBootstrap.cs`(UNITY_EDITOR)
- ✅ `StampRebuildSystem` 게이팅: `GameClock.HourChanged` 게이트 (매 게임 시간 1회, Dirty 없으면 즉시 탈출)
- ✅ **멀티셀 도로 지원 (N×N 정사각형)**
    - `RegistryItem.Relief`: `NeedType : ulong` Unity 직렬화 불가 → `ulong ReliefRaw` 백킹 필드 + `NeedType Relief` 프로퍼티로 우회
    - `RoadCell`에 `FootprintOrigin` + `Size` 추가 (철거 시 역참조)
    - `Road` 컴포넌트에 `FootprintOrigin` + `Size` 추가 (FixupRoadLayer용)
    - `PlaceRoadCommand`에 `Size` 추가
    - `RoadSystem`: 배치 시 N×N 전체 셀 `RoadLayer`/`OccupancyLayer` 등록 → 내부 방향 재계산 → 외곽 이웃 갱신 / 철거 시 `FootprintOrigin`+`Size`로 전체 footprint 제거 / `FixupRoadLayer` footprint 전체 셀에 `RoadEntity` 참조 채움
    - `BuildingPlacementSystem`: `new int2(1,1)` 하드코딩 → `meta.Size`
    - `CivilianBFS` / `StampRebuildSystem` / 물류 시스템 / 입구 시스템 **변경 없음** (셀 단위 추상화 덕분)
    - ✅ (2026-06-17) 멀티셀 도로 비주얼 스케일링 해결 — 아래 섹션 참고

---

## 맵 로드 → 로비 → 베이스캠프 → 도로 파이프라인 (2026-06-17)

### 로비 / 맵 로드 연동
- ✅ `SkirmishLobby` → `DlcBootstrap.LoadMap()` 직접 트리거 (Awake에서 `ExternalMapLoadControl=true`로 자동로드 차단 후 팀 구성 끝나면 명시적 호출)
- ✅ `DlcBootstrap.DevMapJsonPath` 추가 — Addressables 카탈로그 빌드 전 파일 경로로 임시 테스트 가능 (`MapLoadRequest`/`MapLoadSystem` 경로, `MapLoadCommand`/`MapLoaderSystem`은 별개의 구버전 경로로 남겨둠)
- ✅ **스타트포인트는 맵데이터가 유일한 소스.** `SkirmishLobby`는 더 이상 `TeamStartPoint`(Cell)를 만들지 않음. `MapLoadSystem.RegisterStartPoints`가 만든 고립 엔티티를 `TeamStartPointMergeSystem`(신규)이 `TeamIndex` 기준으로 팀 엔티티에 병합.
- ✅ `FactionConfigAuthoring`/`FactionBaseAuthoring`/`CellTypeRegistryAuthoring`/`RoadKeyAuthoring`/`NeedMappingAuthoring`의 MonoBehaviour를 전부 파일명=클래스명 단독 파일로 분리 (`RunTime/Authoring/`) — 안 그러면 컴포넌트가 GameObject에 안 붙는 문제 있었음.
- ✅ **SO도 동일 문제 발견**: `CellTypeRegistry`/`FactionDefinition`/`FactionBaseDefinition`을 한 파일에 다른 타입과 같이 두면 `m_Script: {fileID: 0}`로 깨져 직렬화됨(Live Baking 중엔 안 보이고 SubScene 닫힌 캐시 임포트에서만 터짐). 전부 `RunTime/Registry/`에 단독 파일로 분리 + 깨진 기존 .asset 재생성으로 해결.
  - ❓ **새 ScriptableObject/MonoBehaviour 클래스를 만들 때는 처음부터 파일 하나당 타입 하나로 만들 것** (영구 규칙으로 삼을 만함)

### FactionConfig 싱글톤
- ✅ `NativeHashMap` 포함 컴포넌트는 베이킹 시 직렬화 불가(포인터) → Baker가 아닌 `FactionConfigSystem.OnCreate`에서 코드로 생성 (GridInitSystem과 동일 패턴)

### 베이스캠프 배치 좌표계
- ✅ **"시작점에서 우상향(+X+Z)으로만 확장, 절대 음수 좌표 금지"** 규칙 확정 (메모리에도 저장: `feedback_grid_index_origin`)
- ✅ `campSize`(BaseCampSize)는 **건물 전용 안쪽 영역** 크기. 도로는 그 바깥에 한 겹(`roadSize`만큼) 두름. `buildOrigin = originCell + (roadSize, roadSize)`로 안쪽 이동, 도로 링은 `originCell` 그대로(음수 없음).
- ✅ **배치 위치 공식**: `(index + 0.5f) * cellSize` — 오브젝트 중심이 셀 중심에 맞춰짐.
  - `GridSettings.CellCenter`, `MapLoadSystem.CellToWorld`, `MapLoaderSystem.EmitSingleSpawn`, `RoadSystem` worldPos, `MapEditorWindow` Instantiate/Preview 함수 전체 일치.
  - (2026-06-17) 한 차례 `index * cellSize`로 변경했다가 원복함.
- ✅ **`GridLayers.TerrainLayer` 미등록 버그 발견·수정** — `MapLoadSystem.SpawnTerrain`이 지형 비주얼만 스폰하고 `TerrainLayer`(건물 배치 검증이 보는 레이어)에는 한 번도 기록 안 했음 → 모든 셀이 영원히 `OutOfBounds`로 판정되던 근본 원인. 이제 `SpawnTerrain`에서 `layers.TerrainLayer[cell] = new TerrainCell{...}` 등록.

### 도로 크기/방향
- ✅ **도로 크기는 `RoadPrefabRegistry.DefaultSize` 기준으로 통일** (예전엔 `GamePrefabRegistry`의 Road 항목 Size가 에디터에서 항상 (1,1) 강제라 무의미했음). `RoadKeyAuthoring`이 `BakedRoadKey.Size`로 같이 굽고, `RoadKeyBuildSystem`이 `RoadKeyLookup.SizeByFaction`(FactionId→byte) 구성. `BuildingPlacementSystem.EmitRoad`, `FactionBaseSpawnSystem.EmitPerimeterRoads`가 이걸 따름.
  - `FactionBaseSpawnSystem`/`MapLoaderSystem`/`MapLoadSystem`의 1셀짜리 개별 도로(외곽 perimeter, 맵에디터에서 한 칸씩 찍은 도로)는 의도적으로 `Size=1` 유지 — roadSize 설정과 무관.
- ✅ **블록(매크로) 단위 방향 계산 신규 도입**: 기존 `RoadSystem.ComputeDirections`는 셀 1개 기준이라 `Size>1`이면 블록 내부 셀까지 "연결됨"으로 잡혀 방향이 오염됨. `Road`/`PlaceRoadCommand`에 `VisualDirectionsOverride` 추가 — 비주얼(프리팹 선택)만 이 값 우선 사용, 보행 경로(`CivilianBFS`)가 쓰는 셀 단위 방향은 그대로 자동계산(정확함, 영향 없음). `FactionBaseSpawnSystem.EmitPerimeterRoads`가 매크로 좌표 인접성으로 직접 계산해서 채움.
- ✅ **회전 버그 수정**: `RoadSystem`이 도로 인스턴스화 시 `LocalTransform.FromPosition(...)`을 써서 프리팹에 미리 베이크된 회전을 매번 identity(0)로 덮어쓰고 있었음 — 방향별로 직접 회전시킨 15종 프리팹을 등록해도 전부 회전 0으로 보이던 원인. 이제 프리팹의 원래 Rotation/Scale을 읽어서 보존.

### 인게임 HUD (2026-06-17)
- ✅ `GameHUD.cs` — 도로/건설 탭 UI MonoBehaviour (Canvas 씬 설정은 유저 담당)
  - 도로 탭: 건설 시작/중지 토글, 확정, 되돌리기 버튼 + 구간 수 레이블
  - 단축키: Enter/Space=확정, Z=되돌리기, Escape=모드 해제
  - `RoadBuildController` API(`EnterBuildMode`, `ExitBuildMode`, `Confirm`, `Undo`, `IsModeActive`, `SegmentCount`) 위임
- ⬜ Unity 에디터에서 Canvas 계층 구성 및 GameHUD 필드 와이어링
- ⬜ 건설 탭 — 건물 배치 UI (미착수)

### 도로 방향·연결 정책 (2026-06-17~18)
- ✅ **`RoadPlacedAxis (Any/EW/NS)` 도입** — 평행 도로 간 자동 연결 차단
  - `RoadCell`, `Road`, `PlaceRoadCommand` 모두에 `Axis` 필드 추가
  - `ComputeAxisFilteredMacroDirections`: `myAllows || neighborAllows` 규칙 — 한쪽이라도 허용하면 연결
- ✅ **`ComputeMacroDirections`** 신규 도입 — `Size>1` 도로의 내부 셀 방향 오염 방지 (경계 셀만 검사)
- ✅ **L-드래그 폐지** — 한 드래그 = 단일 축 직선만 허용 (지배 축 자동 선택)
  - 여러 방향 도로는 직선 드래그 복수 → 단일 Confirm
  - `Confirm()`에서 세그먼트 축 = `EW` or `NS` (코너 `Any` 없음)
- ✅ **베이스캠프 외곽 도로 `Axis` 자동 결정** (`FactionBaseSpawnSystem.EmitPerimeterRoads`)
  - 직선 구간: `dir`에 EW만 있으면 `Axis=EW`, NS만 있으면 `Axis=NS`
  - 코너 셀: 두 축 모두 있으면 `Axis=Any` (코너이므로 양방향 연결 허용)
- ✅ **플레이어별 도로 레이어 분리** (2026-06-18)
  - `ComputeDirections`, `ComputeMacroDirections`, `ComputeAxisFilteredMacroDirections` 모두 `ownerLocalId` 파라미터 추가
  - 이웃 셀 `OwnerLocalId` 불일치 시 연결 무시 → 다른 플레이어 도로와 시각적·비트마스크 모두 분리
  - `UpdateFootprintBoundaryDirections`: 이웃 갱신 시 그 이웃 자신의 `OwnerLocalId` 사용
  - BFS(`CivilianBFS`)는 원래부터 `OwnerLocalId` 체크 → 경로 탐색은 이미 분리돼 있었음
- ❓ 물-육지 경계 삼거리 이슈 — `StampTestBootstrap`이 원인으로 추정, 미확인
- ❓ 인접 평행 도로를 가로질러 수직 도로 연결 불가 — 점유 레이어가 막음. 설계 제약으로 수용 (최소 1셀 간격 필요)

### 도로 프리뷰 사유 표시 + 단차 처리 (2026-06-18)
- ✅ **프리뷰 사유별 상태 구분 (`PreviewStatus`)** — 기존 `bool Valid` → 6종 enum으로 교체
  - `Valid`(초록) / `Occupied`(빨강+흰 외곽선, 건설불가) / `OutOfBounds`(어두운 빨강, 불가)
  - `HeightMismatch`(주황, 불가) / `ParallelWarn`(노랑, 경고·건설가능) / `OwnerWarn`(자홍, 경고·가능)
  - `PreviewStatusOps`: `IsBlocking`(차단 여부) / `ShowOutline`(점유 강조) / `ToText`(HUD 라벨용)
  - `RoadBuildController.EvaluateCell(cell, axis, ownerSlot, layers)` — 점유/범위/단차/축/소유자 우선순위 평가
  - `Confirm()`은 `IsBlocking`인 셀만 명령 발행 생략(경고는 발행 허용)
- ✅ **#3 점유 대상 오브젝트 강조** — `RoadBuildPreviewRenderSystem`이 `Occupied` 셀에 GL.LINES 흰 외곽선 추가로 "무엇이 막는지" 식별
- ✅ **#2 도로 높이 처리** — 시각 Y가 `0f` 하드코딩이라 height 1+ 지형 위 도로가 전부 0에 깔리던 버그 수정
  - `RoadSystem` 시각 worldPos Y = `TerrainLayer[cell].Height * cellSize` (CellToWorld 규약 일치)
  - 프리뷰 마커 Y도 지형 높이 추종 (`RoadBuildPreviewRenderSystem` OnUpdate)
  - **단차 불일치 시 건설 불가**: `RoadSystem.HeightMatchesNeighbors` — 같은 소유자 인접 도로와 지형 높이가 다르면 배치 거부. 컨트롤러 프리뷰도 동일 판정(`HeightMismatch`)
- ✅ **#4 Undo** — `OnRoadUndo`에 `RefreshRoadPanel()` 추가(즉시 피드백). (확정된 도로 철거는 범위 외 — 추후 RemoveRoadCommand 연동)
  - ✅ **버튼 Undo 미작동 근본 원인 수정**: UI 버튼 클릭이 `HandleDragInput`에 그대로 흘러들어 유령 드래그→세그먼트 추가로 Undo와 상쇄됐음(Z 단축키는 마우스 무관이라 정상이었음). `EventSystem.IsPointerOverGameObject()` 가드로 UI 위에선 새 드래그 시작 차단(진행 중 드래그는 유지). → Confirm/모든 UI 버튼의 유령 입력도 동시 해결.
- ✅ **호버 사유에 점유 종류 표기** — `OccupancyCell.Type` 읽어 "건설 불가: 건물 점유" 식으로 `HoverStatusText` 강화
  - ✅ **버튼 클릭 시 유령 드래그 차단** — `EventSystem.IsPointerOverGameObject()` 가드(UI 위 새 드래그 시작 차단). Undo/Confirm 버튼이 동시에 세그먼트를 추가/상쇄하던 근본 원인

### 점유/자원 레이어 모델 정리 (2026-06-18)
- ✅ **`OccupantType` 정리** — `Unit`/`Terrain` 제거(쓰기 0건 데드값), `Environment` 추가
  - 유닛=동적→spatial query 소관 / 통행불가지형=`CellType.Passable`+TerrainLayer 파생(이중소스 금지)
  - `OccupancyLayer` 단일 책임 = "정적으로 셀을 점유하는 것"(Road/Building/Environment)
- ✅ **자원 차단은 미러링 X, ResourceLayer 직접 조회** — 배치는 저빈도(비핫패스)라 조회 1회 추가 비용 무의미. 미러링 시 고갈 동기 부채 → 단일 소스 유지. 고갈 시 ResourceLayer에서 제거 = `ContainsKey`가 곧 차단
  - `BuildingPlacement.ValidateCells`: 자원(Amount>0)→`ResourceBlocked`(신규 실패코드), 환경은 비차단
  - `RoadSystem` 배치: 자원→거부, 환경→통과 후 제거, 단차→거부
  - 프리뷰 `EvaluateCell`: `ResourceBlocked`(청록) 신규 / 환경 비차단(초록, 배치 시 제거)
- ✅ **환경물(나무/바위) 경고 없이 제거** — 도로/건물 배치 시 footprint 내 `Environment` 점유 셀의 `Occupant` 엔티티 destroy + 셀 비움 (`ClearEnvironment` / RoadSystem 인라인)
- ✅ **ResourceLayer 등록** — `MapLoadSystem.SpawnResources`가 `mapData.ResourceCells`(TypeId+Amount)를 `ResourceLayer`에 기록(Amount>0만). 기존엔 쓰기 0건이라 자원이 레이어에 없었음
- ✅ **자원 테스트 비주얼** — `ResourceDebugVisualizer`(임시 MonoBehaviour, 에디터/개발빌드 자동생성). 자원 프리팹 없어서 ResourceLayer 셀을 화면에 종류별 색 + 양(숫자) 박스로 OnGUI 오버레이. 실제 프리팹 들어오면 삭제
- ✅ **단차 건설 차단 (AI 대비)** — AI 도로는 평면(2D) 연결만 보므로 단차를 넘는 도로를 깔 수 있음. `RoadSystem.HeightMatchesNeighbors`가 같은 소유자 인접 도로와 지형 높이 불일치 시 배치 거부(PlaceRoadCommand 경로 = 유저·AI 공통). 건물은 `ValidateCells`가 이미 footprint 균일 높이 강제
- ✅ **맵 스폰 오브젝트 → OccupancyLayer 런타임 점유 수집** — 기존엔 `SpawnSingles`가 `GridMap.BuildingCells`에만 등록(빌드 검증이 보는 `OccupancyLayer`엔 0건), `SpawnMultis`(환경 scatter)는 점유 미등록 → 환경요소가 점유로 안 잡히던 문제
  - `OccupantFor(mainKey)` = `MainKeyRange.CategoryOf` → Building/Environment 분류(런타임 판단)
  - `SpawnSingles`: 종류별로 `OccupancyLayer[cell]` 등록(Occupant=인스턴스). GridMap.BuildingCells는 시민 BFS용으로 병행 유지
  - `SpawnMultis`/`SpawnSingles`: 환경물 인스턴스마다 `EnvironmentInstance{Cell}` 태그 + `OccupancyLayer`에 Environment 등록
- ✅ **환경물 제거 = 셀 단위 요청 방식 (LEG 폐기)** — 도로가 환경물 직전까지만 깔리고 멈추던 버그 근본 수정
  - 원인: ECB로 구성한 `LinkedEntityGroup` 부모를 `ecb.DestroyEntity`하면 RoadSystem playback이 그 환경 셀에서 예외 → 이후 도로 비주얼 생성·cmd 정리가 전부 중단(RoadLayer엔 등록됐지만 비주얼 없음)
  - 해결: 직접 destroy 금지. 도로/건물은 `EnvironmentClearRequest{Cell}`만 발행, `EnvironmentClearSystem`(UpdateAfter Road/Building)이 해당 셀의 `EnvironmentInstance`를 단일 패스로 destroy. ECB playback 예외 위험 제거
  - 환경 single은 `GridMap.BuildingCells` 등록 제외(철거 후 죽은 엔티티 참조 방지)
- ✅ **프리뷰: 철거될 오브젝트 표시** — `PreviewStatus.WillClear`(갈색 + 흰 외곽선) 신규. 환경물 위 셀은 "환경물 철거 후 건설"로 표시(비차단), 외곽선으로 사라질 대상 강조
- ✅ **단차 = 세그먼트 단위 무조건 건설불가** — 기존 단차 체크는 인접 기존 도로와만 비교 → 경사면에 처음 까는 새 도로는 비교 대상이 없어 단차를 못 잡던 문제
  - `SegmentBlockStatus(seg)`: 한 드래그 구간에 건물/자원/단차가 하나라도 있으면 **구간 전체** 건설불가. 단차 판정 = 세그먼트 내 모든 셀 지형 높이 동일 + 연결될 기존 도로와도 높이 동일
  - 프리뷰: 차단 구간은 전 셀을 그 사유 색으로(전체 빨강 등), 통과 구간만 셀별 상태(환경철거/경고/가능). `AddSegmentPreview`
  - `Confirm()`: 차단 세그먼트는 통째로 명령 발행 생략(부분 배치 안 함)
  - `RoadSystem`: footprint 내부 단차도 거부(멀티셀·AI 경로 대비)
- ✅ **단차 = "연결 차단"이지 "배치 차단"이 아님 (정책 정정)** — 아래 0층 도로를 깔고, 위 1층에서 이어 깔 때 경계에서 막히던 문제. 두 평지 도로는 각자 깔 수 있어야 하고 절벽 너머로 연결만 안 되면 됨
  - 연결 함수 높이 인식: `ComputeDirections`/`ComputeMacroDirections`/`ComputeAxisFilteredMacroDirections`에 `TerrainLayer` 인자 추가 → **같은 소유자 + 같은 지형 높이** 이웃만 연결(비주얼·BFS 공통). 단차 경계는 자동으로 끊김
  - `RoadSystem` 배치: 인접 도로 단차로 인한 배치 거부 제거(`HeightMatchesNeighbors` 삭제). footprint **내부** 단차만 거부 유지
  - 컨트롤러 `SegmentBlockStatus`: 세그먼트 **내부** 단차만 차단. 기존 도로와의 단차는 차단 안 함
  - `EvaluateCell`: 단차 너머 이웃은 막지도 경고하지도 않고 그냥 연결만 스킵
- ✅ **HUD 호버 사유 라벨** — `GameHUD._lblHoverStatus`(옵션) ← `RoadBuildController.HoverStatusText`
  - ⬜ Unity 에디터에서 `_lblHoverStatus` 라벨 와이어링(옵션)

### 다음 단계
- ⬜ 그라운드 큐브 같은 중심-피벗 프리팹들의 `RegistryItem.Offset` 일괄 점검/설정
- ⬜ 시민 초기 스폰 트리거 (여전히 미착수)
- ⬜ 플레이어 건물 배치 UI (여전히 미착수)
- ❓ `campSize % roadSize != 0`일 때 가장자리 핏 보정 (현재는 단순 truncation, 약간 빈 틈 가능)

---

## City Expansion AI (자율 다중 팀)
> ⚠️ (2026-06-18 정정) 아래 옛 설계 메모(`CityCellManagerSystem`/`CellData`/`BuildRequest`/
> 닫힌 사각형 도로/사분면 압축/U자 폴백)는 **현재 코드에 존재하지 않음**(grep 0건). 제거됐거나
> 미구현. 실제 구현은 아래 "현행" 참조.

### 현행 (실제 코드)
- `AiCityGrowthSystem`: `GameClock.DayChanged`마다 AI팀당 **건물 1개** 배치 (결정 예산 1/일).
  - 후보 = `BlockOps.CollectAnchorCandidates`(도로 특이점=끝/꺾임/분기 인접 빈 저해상도 셀)
  - 선택 = `CountSharedEdges` 최대(응집). `RegisterBlock` 후 `PlaceBuildingRequest` 발행.
  - **AI는 도로를 깔지 않음** → 도시가 초기 둘레(베이스 외곽선) 밖으로 못 자람.
- `BlockOps`: 구획 가능성/후보/공유변 순수 함수.
- ✅ **(2026-06-18) 새 도로/배치 규칙과 정합화** — `BlockOps`가 건물 검증(`ValidateCells`)과 같은 기준을 보도록 수정. 안 그러면 `RegisterBlock`이 먼저 실행되고 건물 검증이 나중에 실패해 **유령 구획**(등록만 되고 빈 칸)이 영구히 남음
  - `CanPlaceBlock`/`IsRealCellRangeFree`/`CollectAnchorCandidates`에 `ResourceLayer` 추가
  - 환경물(Environment)은 비어있는 것으로 간주(건물이 치움) / 채취 자원(Amount>0)은 불가 / 구획 전체 단차 거부

### AI 성장 규칙 — 확정 (2026-06-19, 도로변 모델)
> "길만 깔던" 근본 원인: 건물이 도로 **특이점(끝/코너)에만** 붙어 도로 길이당 건물 1~2개뿐 →
> 도로가 건물보다 빨리 자라 건물 땅까지 도로로 덮음. 아래 규칙으로 재정립.

- **R1. 하루 1행동, 건물 우선** — 건물 자리 있으면 건물, 전혀 없을 때만 도로 연장.
- **R2. 건물 자리 = 내 도로 옆면 전체** — 도로에 인접한 빈 평지면 모두 후보(특이점 한정 폐기).
- **R3. 깊이 1격, 도로 접면 필수** — footprint가 도로 접면 가장자리에 붙고 바깥으로 뻗음. 입구가 도로를 향해야 발행.
- **R4. 응집 우선·큰 건물 우선** — 점수=이웃 점유/도로 수×1000 + footprint 넓이.
- **R5. 도로는 간격 둔 평행 확장** (자리 다 찰 때만) — ⬜ 평행 간격 로직은 다음 단계. 현재는 끝/옆 1 footprint 연장(연결 유지).
- **R6. 겹침 금지·평탄·환경 철거** — 기존 규칙 유지.

- ✅ **(2026-06-19) `TryPlaceBuilding` 실셀 도로변 모델로 재작성** — 저해상도 `BlockGrid`(2×2)는 "1격 접면" 정밀도에 안 맞아 폐기(1칸 건물이 블록 안에서 도로 반대 구석에 앉아 접면 실패). 이제 실셀 단위로 팀 도로 4방향 인접 빈 셀에 footprint를 접면시켜 배치. 헬퍼: `CellBuildable`/`FootprintFreeFlat`/`Cohesion`. BlockLayer/RegisterBlock 미사용(점유는 OccupancyLayer 단일 소스)
  - ⬜ `BlockOps`의 성장용 헬퍼(`CollectRoadsideCandidates`/`CanPlaceBlock`/`CountSharedEdges`/`RegisterBlock` 등) 이제 미사용 → 정리 대상
  - ❓ 도로변 1격이라 도로에서 2칸 떨어진 곳은 도로가 가까이 와야 채워짐 → R5(평행 간격 도로)로 해결 예정

- ✅ **(2026-06-19 해결) 큰 건물 1개 후 무한 셀검증실패 + 도로 폴백 안 됨 + 입구-도로 정렬 필수화**
  - 원인 1 — **회전 footprint 불일치**: `TryPlaceBuilding`이 **비회전** `meta.Size`로 origin/footprint를 검증한 뒤 회전을 *나중에 별도로* 찾아(`FindRoadFacingRotation`, footprint 재검증 없음) 발행. `ValidateCells`는 **회전된** `RotateSize`로 다른 셀집합을 검증 → 비정사각 건물에서 불일치 실패.
  - 원인 2 — **점수 고착 + 폴백 차단**: 검증 불일치로 항상 실패하는데도 `found=true`로 반환 → 도로 연장 폴백이 안 일어나 영구 교착.
  - 원인 3 — **WrongTerrain 미검사**: AI가 지형 타입(`BuildableOn`)을 안 봐 물/불가지형 도로변 발행.
  - **해결**: `TryPlaceBuilding` 재구조화 — 회전(steps 0~3)을 1차 루프로 끌어올려 각 회전마다
    ① `eff = RotateSize(meta.Size, steps)`로 origin/footprint를 잡고(=`ValidateCells`와 동일 좌하단·동일 크기)
    ② `FootprintBuildableFlat`이 점유·자원·**지형타입(BuildableOn)**·평탄을 `ValidateCells`와 동치로 검사
    ③ `IsEntranceOnRoad(origin, meta.Size, steps)`로 **입구가 도로에 닿는 회전만 통과(필수)** — 발행 RotationY = `StepsToRotationY(steps)`로 최종 검증과 비트동일.
    셋 다 통과 못하면 `found=false` → `TryExtendRoad` 폴백 보장. → 발행=성공 보장, 교착 해소, 입구-도로 맞물림 강제.
  - `AiCityGrowthSystem`에 `CellTypeLookup` 의존성 추가. footprint-비인지 `FindRoadFacingRotation` 사용 폐기.

### AI 도로 확장 — 유기적 연장 (2026-06-19, 1번 스타일)
- ✅ **건물 자리 부족 시 도로 1줄 연장** — `AiCityGrowthSystem`: 하루에 건물 1개 시도 → 자리 없으면 `TryExtendRoad`로 도로 stub 연장. 새 끝(특이점)이 다음 턴 건물 anchor가 됨 → 유기적 펄스 성장
- ✅ `BlockOps.FindRoadExtension` (fact 헬퍼) — 팀 소유 도로 footprint 원점에서 4방향 중 빈 평지로 **가장 길게** 뻗을 수 있는 직선 stub 반환. 단차/자원/점유(환경 제외)/맵경계 만나면 정지
  - 도로 크기 = 팩션 `RoadKeyLookup.GetSize` footprint 단위 (베이스 외곽선과 일치)
  - 연장 축(EW/NS) 자동 설정 → 새 연결 규칙(높이·축·소유자)과 정합. 단차 너머로는 자동 비연결
  - `GrowthConfig.MaxRoadExtendSteps`(기본 3) = 하루 최대 연장 footprint 수
- ❓ 유령 구획 잔여: `WrongTerrain`(물 위 등)은 `BlockOps`가 지형 타입을 안 봐서 여전히 누수 가능(기존 이슈). AI에 `CellTypeLookup`+`BuildableOn` 검사 추가하면 해소
- ⬜ 향후 다듬기: 직선 도로 옆구리는 anchor가 아니라 건물이 도로 끝/코너에만 붙음(현 정책). 도로변 채우려면 anchor 정책 확장 / 연장 방향 분산(한 곳만 길게 뻗는 경향 완화)
- ✅ **(2026-06-19) "변화 없음" 원인 2개 처리**
  - 시간 인지: 하루=현실 20분(`SecondsPerDay=1200`)이라 AI 성장(하루 1회)이 안 보였음 → `GameClockHud`(임시 IMGUI, 자동생성) 추가: Day/시:분 표시 + 배속(0/1/3/10/60x) 버튼으로 `GameClock.TimeScale` 조절
  - `GrowthConfig.BuildingMainKey=1000`이 placeholder라 미등록이면 건물 요청이 조용히 실패 + 도로 연장도 안 되던 버그 → `TryPlaceBuilding`이 meta 없으면 **구획 등록·발행 없이 false 반환 → 도로 연장 폴백**. (유령 구획도 방지)
  - ✅ **성장 건물 2종(1004/1005) 적용** — `GrowthConfig.BuildingKeyA/B`. 크기가 달라 구획 크기는 각 `meta.Size`에서 유도(회전 안전: 한 변=ceil(max변/UNIT) 정사각). 둘 다 미등록/자리없음이면 도로 연장 폴백
  - ✅ **(2026-06-19) 큰 건물 0개 + 도로 난립 수정** — 원인: 구획이 anchor를 원점으로 +X/+Z로만 자라 큰 구획은 빈자리를 못 찾음 → 건물 실패 잦음 → 매일 도로만 뻗음
    - 구획이 anchor 셀을 **포함하되 빈 쪽으로 미끄러지는** L×L 원점 후보 전체를 시도(`origin = c-(ox,oy)`)
    - 점수 = 공유 변(응집) 우선, 동률 시 footprint 큰 건물 우선 → 큰 건물도 지어짐
    - `MaxRoadExtendSteps` 3→1: 도로는 1 footprint씩만 뻗고 건물이 채운 뒤 재연장(난립 방지). 건물 성공률↑로 도로 연장 자체가 드물어짐

- ✅ **(2026-06-19) 자가-포위 + 도로 끝 capping 수정 (최소 버그 수정)**
  - 증상 1: 닫힌 루프 도로가 안쪽을 감싸며 자가-포위 → 바깥으로 못 뻗음. 증상 2: 건물이 도로 끝/연장 셀을 막아(capping) "양쪽 못 쓰는" 죽은 도로 발생.
  - 근본 원인: ① 건물 우선(R1)이라 도로 *끝 연장 셀*까지 건물이 채움 → 도로 capping + 바깥 링을 건물이 둘러쳐 도로망이 갇힘. ② `FindRoadExtension`이 방향성 없이 아무 footprint에서 처음 발견한 빈 방향으로만 뻗어 루프를 감싸기만 함.
  - **수정 ② 방향성 연장** (`BlockOps.FindRoadExtension` 재작성): 끝점(매크로 연결 1개)은 직선 계속만, 그 외(루프 변/분기)는 무게중심에서 *멀어지는 바깥* 방향만 분기. 점수=바깥 최우선→길이→바깥정도. 헬퍼 `MacroConnections`/`OppositeOfSingle`/`TeamRoadCentroid` 추가.
  - **수정 ① 성장 코리도 예약** (`AiCityGrowthSystem.BuildReservedCorridors`): ②와 동일 기준(바깥 방향 + 끝점 직선)으로 팀 도로의 다음 footprint 셀들을 예약. 건물 후보 footprint가 코리도와 겹치면 스킵(`FootprintHitsReserved`) → 바깥 프런티어가 늘 열려 도로가 뻗을 길 확보. 건물 배치 로직 구조는 그대로, 예약 필터만 추가.
  - 효과: 안쪽 도로변은 건물이 양쪽으로 채우고, 바깥 프런티어 코리도는 비워 도로가 바깥으로 자란다 → 자가-포위·죽은 도로 해소. 입구-도로 정렬은 직전 수정대로 강제 유지.
  - ⬜ 평행 간격 도로(R5)는 여전히 미구현 — 본 수정은 단일 코리도 확보까지. 밀집 격자(양면 블록)는 추후 격자 모델로.

- ✅ **(2026-06-19) 건물 주도(building-driven) 성장으로 전환 — 무한 도로 차단 + 한쪽-건물 수정**
  - 설계 정정: 기존 "도로 옆 자리 없으면 도로 연장"은 *지을 이유와 무관하게* 도로가 무한 확장됨(런타임 결함). → **"건물을 먼저 정하고 그 건물을 어떻게든 짓는다"**로 전환.
    - 매일 팀별로 A/B 중 **랜덤 1개**(`PickBuilding`, day+localId 시드) 선택 → 그 건물만 배치 시도. (나중에 수요 판정이 이 선택을 대체 — 훅만 교체)
    - 그 건물을 놓을 자리 없으면 그 건물을 위한 공간 확보용으로 도로 1줄 연장(`TryExtendRoad`) → 다음 날 재시도. 도로는 늘 '건물 하나'를 위해서만 자람 → 무한확장 없음, 도시 차면 자연 포화·정지.
  - `TryPlaceBuilding`이 단일 `chosenKey`만 받도록 단순화(메타 1회 조회, A/B 양쪽 시도·넓이 가산점 제거). 점수=응집만.
  - **예약 코리도 = dead-end 끝 직선 연장만**(`BuildReservedCorridors`). 직선 도로 측면은 예약 안 함 → 건물이 도로 **양쪽**에 붙음(#1 "한쪽만/작은 건물만" 해결).
  - `FindRoadExtension` 점수에서 '가장 먼 spur' 가산점(mag) 제거 → 한 도로만 계속 뻗는 쏠림 완화(#1 "한 도로만 그 방향으로" 완화).
  - `GameClockHud`에 **120x 배속** 버튼 추가.
  - ⬜ 양면 밀집·다방향 분산을 더 강하게 원하면 격자 모델 필요(보류 중). 현재는 건물 주도 + 끝점 예약까지.

- ✅ **(2026-06-19) 도로+건물 원자적 한 턴 + 물 위 도로 금지**
  - 문제 #1: "한 턴 = 한 행동"이라 도로만 깔고 다음 턴엔 의도를 잊고 엉뚱한 곳에 또 도로 → 도로만 누적, 건물 안 생김.
  - 해결 #1: **도로 stub 1칸 + 건물을 한 턴에 같이 발행**(`TryStubAndBuilding`). 흐름: ① 기존 도로변에 건물 가능하면 건물만 → ② 안 되면 팀 도로에서 바깥 빈 평지(Land)로 stub 1칸 계획하고 그 옆에 건물 붙일 수 있으면 **도로+건물 동시 발행** → ③ 둘 다 실패면 이번 턴 아무것도 안 함(도로만 까는 일 없음 = 무한 도로 차단).
    - 검증은 stub을 도로로 간주(planned). 실제로는 같은 프레임에 도로가 먼저 깔리도록 시스템 순서 명시: `AiCityGrowthSystem [UpdateBefore RoadSystem]`, `RoadSystem [UpdateBefore BuildingPlacementSystem]`. → RoadSystem이 stub을 RoadLayer에 등록한 뒤 BuildingPlacement가 그 도로로 입구 재검증 → 통과.
    - 헬퍼: `RoadStubPlaceable`(stub 빈+평탄+Land), `TryBuildingBesideStub`(stub 옆 건물 접면+입구 stub 향함+footprint 유효), `InStub`/`FootprintIntersectsStub`.
    - `TryExtendRoad`(도로만 까는 폴백) 제거. `BlockOps.FindRoadExtension`/`TeamRoadCentroid`는 미사용(⬜ 정리 대상).
  - 문제 #2: AI·플레이어가 물 위에 도로를 깖.
  - 해결 #2: **RoadSystem 배치 검증에 지형 타입 추가** — footprint 셀이 `TerrainCategory.Water`면 거부(다리 미지원, Land 전용). AI·플레이어 공통 경로(PlaceRoadCommand)라 양쪽 다 차단. `RoadSystem`에 `CellTypeLookup` 의존성 추가. AI stub 계획(`RoadStubPlaceable`)도 동일하게 물 제외.

- ✅ **(2026-06-19) 베이스캠프 자기-포위 재발 수정 — 전역(centroid) 코리도 예약**
  - 증상: 닫힌 베이스 외곽은 dead-end가 없어 끝점 예약이 안 걸리고, step1(기존 도로변 건물)이 외곽 링 바깥을 건물로 다 채워 도로 탈출구를 막음 → 확장 불가.
  - 해결: `BuildReservedCorridors`에 **외곽 변 바깥 코리도 예약**(B) 추가. 무게중심 반대(바깥) 방향이고 **그 칸의 안쪽이 이미 채워져 있을 때만** 예약(=도시를 등진 진짜 외곽 변). 양쪽이 다 빈 땅이면(spur 측면) 예약 안 함 → 건물 양면 유지(한쪽만 문제 재발 방지). 헬퍼 `RegionOpenBuildable`/`ReserveFootprint`.
  - 효과: 내부가 차면 step1이 막히고 step2(stub+건물)가 예약된 바깥 코리도로 확장 → 베이스가 꽉 차도 바깥으로 계속 자란다. `TeamRoadCentroid` 재사용.
  - ❓ "크게 보는 시야": 현재는 centroid 기반 국소+준전역 규칙. 비방사형 spur의 한쪽 치우침 잔여 가능 / 진짜 매크로 확장존 계획(어느 방향으로 도시를 키울지)은 미구현 — 추후 과제.

- ✅ **(2026-06-19) 블록식 그리드 성장으로 전면 전환** — 국소 그리디(spur/stub/예약) 폐기
  - 배경: 국소 패치를 반복해도 자기-포위가 계속 재발(닫힌 루프 + 외곽 건물이 도로망을 가둠), 예비 도로도 안 쓰임. → 사용자 제안대로 **블록식**으로 전환. 그리드는 도로가 항상 바깥으로 열린 선형 격자라 **구조적으로 자기-포위 불가**.
  - **`CityGrid` 컴포넌트(신규)**: 팀별 그리드 정의 `{Anchor, Block(=campSize), Road(=roadSize), FactionId}`, `Period=Block+Road`. `FactionBaseSpawnSystem`이 베이스 생성 시 팀 엔티티에 부착 → **베이스=블록(0,0)**, 외곽 링이 그리드 선 0/P와 정렬돼 새 블록 도로가 베이스와 연결됨.
  - **`AiCityGrowthSystem` 전면 재작성**: 매 하루 팀마다 프런티어 블록 1개 개발. ① A/B 랜덤 선택 ② 개발된 블록(건물 보유)에 인접한 빈·평지·Land 블록 중 베이스에서 최근접(+X/+Z 사분면) ③ 그 블록 테두리 도로 + 내부 건물(도로에 닿는 칸만 그리디 채움) 동시 발행. 건물 0개면 도로도 안 깖.
  - 시스템 순서 `AiCityGrowth→RoadSystem→BuildingPlacement` 유지(도로 먼저 깔린 뒤 건물 입구 검증).
  - **블록 크기 = `BaseCampSize`** (그리드 정렬 때문). 12/16 블록을 원하면 BaseCampSize를 그 값으로 설정. 기본 8.
  - ⬜ **밀도 한계(중요)**: 큰 블록은 도로에 안 닿는 안쪽 칸이 비어 마당이 됨(작은 건물일수록 심함). 큰 블록을 꽉 채우려면 블록 내부 가로(alley) 세분화 필요 — 다음 단계.
  - ⬜ `BlockOps.cs`는 이제 전부 미사용(정리 대상). `GrowthConfig`는 BuildingKeyA/B만 남김.

- ✅ **(2026-06-19) 모듈 그리드(M=4셀)로 재정비 — 단위 통일 + 건물별 블록 크기 + 진단 로그**
  - 사용자 피드백: ① 크기는 항상 셀 단위로 통일 ② 블록은 모양 다양성을 위해 {4,8,12}셀 중 건물을 담는 최소 크기 선택 ③ 직전 블록식이 작동 안 함.
  - **단위 통일**: 전부 셀 단위. 모듈 M=4셀 고정 격자. 도로폭=roadSize 셀. 주기 P=M+Road.
  - **건물별 블록 크기**: nmod=1/2/3 모듈(=4/8/12셀, 흡수 도로 포함) 중 `max(meta.Size)`를 담는 최소. 건물 A/B 크기가 달라 블록 크기가 달라짐 → 모양 다양성. 멀티 모듈 블록은 내부 모듈선 도로 흡수(통건물), 도로는 블록 테두리에만.
  - **그리드 정렬 단순화**: 더 이상 `CityGrid.Block(campSize)`에 의존 안 함. M=4 모듈 그리드를 베이스 Anchor에 앵커링하고, 베이스를 "점유 모듈"로 감지(건물 보유) → 그 인접 모듈부터 프런티어 블록 개발. 베이스 외곽 링과 AI 도로는 인접 셀로 연결됨(정확한 주기 정렬 불필요).
  - **진단 로그 추가**: 팀이 못 자랄 때 이유 출력(`[AiCityGrowth]` — 도로없음/후보없음/메타없음/건물너무큼/개발실패). "왜 안 자라는지" 콘솔로 확인용(추후 제거).
  - 매 하루: 빈 nmod×nmod 모듈 블록(미개발+개발모듈인접+평지Land, +X/+Z) 중 베이스 최근접 → 테두리 도로 + 건물 1개 발행.
  - ⬜ 블록당 건물 1개라 큰 블록은 여백(yard) 생김 / 여러 건물 채우기·여백 활용은 추후.

- ✅ **(2026-06-19) "후보 블록 없음" 버그 수정 — 인접 기준을 도로 연결성으로**
  - 증상: 항상 "후보 블록 없음" 로그만. 원인: 후보 조건이 "개발된(건물 보유) 모듈에 인접"이었는데, 베이스 건물이 모듈을 꽉 안 채우면 베이스 옆(빈 모듈)은 미개발로 잡히고, 그 다음 모듈은 개발 모듈과 떨어져 후보 탈락. 베이스 링 바로 옆은 도로와 겹쳐 탈락 → 후보 0.
  - 해결: `BlockPlaceable` 인접 기준을 **"블록 테두리가 기존 팀 도로에 닿거나 4-이웃"**(`BlockConnectsToRoad`)으로 교체. 베이스 닫힌 링/이미 깐 블록 도로에 연결되면 OK. `ModuleDeveloped` 제거. span 빈땅·평지·Land 검사는 유지(베이스/도로와 안 겹침 보장).
  - 효과: 베이스 링 한 칸 바깥 블록이 베이스 도로에 인접 연결로 후보가 됨 → 성장 시작.

- ✅ **(2026-06-19) 한 방향만 보던 버그 수정 + 최적지 점수 도입**
  - 버그: 윈도우를 모듈 0 이상으로 클램프 → 베이스 기준 +X/+Z만 탐색. 로그로 확인(anchor=(37,32), 물=8): 베이스 +방향이 전부 물, 땅은 -방향이라 영영 못 자람. → 윈도우를 베이스 bbox **±3 모듈, 음수 허용**으로 확장(음수 셀은 TerrainLayer가 맵밖으로 필터). 4방향 모두 탐색.
  - **최적지 점수**(`ScoreCandidate`): 후보 중 "가장 가까운 것" → "가중 점수 최고". 3요소 각 0~1 정규화:
    - 응집도(`CohesionNorm`): 블록 4변 중 기존 팀 도로/건물이 바깥에 있는 변 수/4 (조밀).
    - 베이스 근접: 1/(1+모듈 맨해튼 거리) (동심원).
    - 확장 여지(`RoomNorm`): 블록 둘러싼 모듈 링 중 빈 평지 Land 비율.
    - 가중치 `GrowthConfig.WCohesion/WProximity/WRoom`(기본 1.0, 튜닝용).
  - ⬜ 블록 크기 {4,6,8} tier 적용은 다음(현재 모듈식 4/9/14). 가중치 튜닝으로 성장 양상 관찰 후 조정.

- ✅ **(2026-06-19) 셀 단위 tight 블록 {4,6,8}로 전환 — '블록 안 작은 블록 둥지' 버그 수정**
  - 증상: 큰 블록 안에 작은 블록이 둥지를 틈. 원인: 모듈 흡수식 멀티 블록(span 9/14)이 도로 링으로 큰 영역을 둘러싸고 건물 1개만 넣어, 남은 빈 여백(yard)이 다시 후보로 잡힘.
  - 해결: 모듈 흡수 방식 폐기. **블록 = 건물 최대변을 담는 최소 {4,6,8}셀 정사각형(tight) + 도로 링.** 여백 < 4셀이라 최소 블록(4×4)이 못 들어감 → 둥지 불가. 동시에 사용자가 원한 4/6/8 크기 충족.
  - 셀 단위 배치(STEP=2 격자 스냅), 베이스 도로 bbox ± margin 사방 탐색. `BlockStatus`(셀 span 검사), `CollectRingRoads`(span 둘레 링 도로), 점수(`CohesionNorm`/`RoomNorm` 셀 기반) 전부 셀 단위로 재작성. 모듈/`CityGrid.Block` 의존 제거(Anchor/Road/FactionId만 사용).

- ✅ **(2026-06-19) 모서리(오목/볼록) 기반 성장 + 균일 모듈 그리드(도로 공유)** — 사용자 7단계 사양
  - 이전 문제: ① 블록마다 도로 따로 깔아 미공유 ② 후보가 span(구획)만 검증해 도로 링이 물/단차에도 발행됨.
  - **균일 모듈 그리드**: 모듈 M = `CityGrid.Block`(=campSize) → 베이스가 정확히 모듈(0,0), 도로가 모듈선에 놓여 **이웃과 자동 공유**. (블록 크기 균일 — 4/6/8 혼합은 도로 공유와 양립 불가라 보류.)
  - **모서리 알고리즘**(`GrowOneBlock`): ①점유 모듈(팀 건물 보유) 수집 ②도로 bbox로 윈도우 ③**오목점**(빈 모듈인데 직각 두 변 점유=노치, `HasPerpPair`) 먼저, 깊은 노치·근접 우선 ④footprint 확보 가능하면 개발 ⑤없으면 **볼록점**(점유 모듈인데 직각 두 변 빔) ⑥두 빈 방향이 90° 확장 후보 ⑦그 방향 빈 모듈(가상 오목점) 개발.
  - **footprint 전체 검증**(`ModulePlaceable`): 내부 M + 도로 링 Road 영역 전체를 **같은 높이(단차 거부)·Land(물 거부)·맵 안**, 내부 빈땅, 링은 빈땅/기존 팀 도로(공유), 링이 팀 도로에 닿음(연결). → 물/단차엔 도로 안 깖.
  - 모듈 단위 점유 판정이라 '큰 블록 안 작은 블록' 둥지 불가. `GrowthConfig`는 BuildingKeyA/B만.
  - ⬜ 블록 크기 다양성(4/6/8)은 도로 공유와 트레이드오프 — 추후 계층 도로(간선/이면)로 분리 시 가능.

- ✅ **(2026-06-19 최신) 모서리 구동 + 가변 블록(크기 일치 강제 폐기)** — 사용자 정정 반영
  - 정정: 확장 시 변 길이/크기 일치를 검사하면 안 됨. **셀 유효성만 통과하면 도로로 감싼다.** 그러면 또 다른 오목이 생기며 자연스러워짐. 도로 공유/평탄화는 추후 정리 패스.
  - 균일 모듈 그리드 폐기 → **건물 크기 {4,6,8} 가변 블록**, STEP=2 격자 후보. 오목점(직각 두 변이 도시에 닿는 자리=노치) 우선, 없으면 볼록 확장(한 변만 닿음).
  - `BlockValid`: footprint(내부 K + 도로 링 Road) 전체를 맵 안·같은 높이(단차 거부)·Land(물 거부)·내부 빈·링 빈/팀도로로 검증 → **해변·단차엔 도로 안 깖**. `SideMasks`(변별 도로/도시 접촉), `HasPerpPair`(오목/볼록), 진단 로그(맵밖/점유/물/단차/미연결).
  - 도로 공유는 강제 안 함(이중 도로 가능) — 자연스러운 모습 우선, 추후 평탄화.

- ✅ **(2026-06-19 최신2) 도로 정렬·공유 위해 균일 모듈 그리드로 복귀**
  - 증상: 볼록 확장 시 새 도로가 기존 도로와 한 셀 어긋나 평행 도로가 됨(삼거리 안 됨). 베이스와 직접 안 닿는 블록은 도로 독자 생성(미공유).
  - 원인: 가변 크기 + STEP 격자가 기존 도로와 정렬되지 않음. **정렬·공유(깨끗한 삼거리)는 균일 그리드여야만 가능** — 크기 섞이면 경계 어긋나 공유 불가.
  - 해결: 모듈 M = `CityGrid.Block`(=campSize). 베이스 = 정확히 모듈(0,0). 주기 P=M+Road, 도로는 모듈선에 위치 → 이웃 모듈(1,0) 서쪽 링 = 베이스 동쪽 링으로 **정확히 공유**(어긋남 0, 삼거리 깔끔). 모서리(오목/볼록) 알고리즘은 모듈 위에서 그대로.
  - ⬜ 블록 크기 다양성(4/6/8)은 도로 정렬·공유와 양립 불가 → 보류(건물 A/B 차이로만). 진짜 다양성+정렬은 계층 도로(간선/이면 분리) 필요 — 추후.

- ✅ **(2026-06-20) 모서리 앵커 + 가변 크기 (오해 정정)** — 균일 그리드 불필요
  - 정정 핵심: 새 블록은 **기존 도로 모서리를 '시작점'으로 그 지점부터 셀을 채운다**. 시작 모서리에 닿는 링이 기존 도로와 **정확히 일치**(O = 도로좌표±Road) → 어긋남 없는 삼거리/사거리. **블록 크기/공유변 길이는 이웃과 달라도 됨**(겹치는 만큼만 공유, 나머지는 새 도로). → 균일 그리드(이전 결정) 폐기.
  - 구현: 각 팀 도로 셀 R의 4사분면(NE/NW/SE/SW)에 K×K 블록 앵커(`QuadrantOrigin`) → 두 근접 링이 R 도로와 정렬. 프런티어 도로만(`HasEmptyNeighbor`), 후보 dedup.
  - 선택 우선순위: 오목(닿는 변 직각쌍, `HasPerpPair`) > 닿는 변 수 > 베이스 근접 → 노치 먼저 메움. `SideMasks`로 변별 도로/건물 접촉 판정.
  - `BlockValid`: footprint(내부 K + 링 Road) 전체 평탄·Land·맵안, 내부 빈, 링 빈/팀도로 → 해변/단차 회피. 블록 크기 {4,6,8}는 건물에서 자동.

- ✅ **(2026-06-20) 모서리 앵커 정렬 + 오목/볼록 정확 구분 + 의도적 랜덤**
  - 볼록 y+1 버그: 직선 도로 중간 셀을 앵커로 잡아 블록이 한 칸 밀림. → `IsCornerRoad`로 **모서리(꺾임·끝·분기)만 앵커**(직선 통과 셀 N|S·E|W 제외). 1단계 "모서리를 모읍니다"와 일치.
  - 오목/볼록 모호성: 코너 앵커는 볼록도 두 변에 닿아 오목처럼 보임 → ~30% 볼록 오선택(비의도 랜덤). → `GetMasks`가 **4 대각 코너(diagMask)** 도 봐서 `IsConcave`로 진짜 노치(3변↑/마주보는 변/직각변+대각 점유) vs 외곽 코너 구분.
  - 의도적 랜덤: 오목/볼록 후보를 분리 수집 후 기본 오목 우선, `GrowthConfig.ConvexBias`(기본 0.3) 확률로 볼록 먼저. day+owner 시드 RNG라 결정적·재현 가능·튜닝 가능(0=완전 계획, ↑=불규칙).

---

### 🟢 현재 확정 동작 (2026-06-20 세이브) — AI 도시 성장 = 모서리 앵커 가변 블록
> 위 긴 트레일은 디버깅 과정. **현재 코드의 동작은 이 블록만 읽으면 됨.** (`AiCityGrowthSystem.cs` 전면이 이 모델)

- **모델**: 매 게임일(`DayChanged`) AI팀마다 블록 1개 성장. 블록 = 건물을 담는 {4,6,8}셀 정사각형 + 도로 링(roadSize). 크기 균일 강제 없음(건물별 가변).
- **앵커 = 기존 도로 모서리(특이점)**: 각 팀 도로 셀의 연결 형태로 판정(`RoadNeighborMask`).
  - 직선 통과(마주보는 2연결, `IsStraightThrough`) → 앵커 제외.
  - 끝점(1)·L자 꺾임(직각 2) → 볼록·오목 둘 다 가능.
  - 삼거리(3)·사거리(4)=`junction` → **오목만 가능, 볼록 불가**(통과축 있어 외부 안 꺾임).
- **앵커에서 4사분면 블록**(`QuadrantOrigin`): 근접 링이 모서리 도로와 정확히 일치 → 어긋남 없는 삼거리/사거리. 공유변 길이는 달라도 됨(겹치는 만큼 공유).
- **오목/볼록 판정**(`SideMassMask`+`IsConcave`): 블록 4변이 도시(팀 도로/건물)에 닿는지 **내부 폭 K만** 스캔(코너 돌출 제외 → 오판 방지). 닿는 변 ≥2 = 오목(노치), ≤1 = 볼록.
- **선택**: 기본 오목 우선(노치 먼저 메움) → 닿는 변 多 → 베이스 근접. `GrowthConfig.ConvexBias`(0.3) 확률로 볼록 먼저(자연스러운 불규칙). `ConvexBias=0`이면 가능한 한 항상 오목.
- **유효성**(`BlockValid`): footprint(내부 K + 도로 링) 전체가 맵 안·같은 높이(단차 거부)·Land(물 거부)·내부 빈땅·링은 빈땅/기존 팀 도로. → **해변·단차·자원 위엔 안 깖**(자원은 `CellBuildable`이 차단).
- **랜덤 시드**: `CityGrid.Seed`(베이스 생성 시 `UnityEngine.Random`으로 세션마다 다름)를 RNG에 XOR → **새 게임마다 다른 패턴**, 한 게임 내 결정적.
- **시스템 순서**: `AiCityGrowth → RoadSystem → BuildingPlacement`(같은 프레임, 도로 먼저 깔린 뒤 건물 입구 검증).
- ⬜ **다음 단계 후보**: 진단 `Debug.Log` 제거 / `BlockOps.cs` 미사용 정리 / 블록 내부 여백(yard) 활용 / 수요(시민) 연동으로 건물 선택 대체 / 큰 블록 내부 alley 세분화.
- ⚠ **미사용 정리 대상**: `BlockOps.cs` 전체(grep 0 호출), `GrowthConfig`에 남은 옛 필드 없음 확인.

---

## GPU 파이프라인 / Compute Shader
- ✅ GPU 친화 `IComponentData` 레이아웃
- ✅ `IJobChunk`/`IJobEntity`로 `NativeArray` 수집
- ✅ 매니지드 컴포넌트 싱글톤이 `GraphicsBuffer`/`ComputeShader` 참조 보유 (`SystemAPI.ManagedAPI` 접근)
- ✅ Burst 수집 잡 + 논-Burst 매니지드 GPU 호출 분리
- ✅ `AsyncGPUReadback` 논-스톨 결과, `RenderMeshIndirect`로 직접 파이핑
- ⬜ (다음 단계 기입)

---

## Minimap
- ✅ 베이크된 지형 `RenderTexture` 베이스 + compute shader 동적 오버레이 (ECS `NativeArray` 공급)
- ✅ 교훈: compute shader 전역 변수 초기화는 무시됨 (C#에서 전부 설정) / `.compute`의 한글 주석은 `INVALID_UTF8_STRING` 유발 / `enableRandomWrite`는 `Create()` 전에 / 카메라 프러스텀은 회전하는 속 빈 사다리꼴
- ⬜ (다음 단계 기입)

---

## 날씨 효과 (눈/비)
- ✅ 결론: 파티클당 엔티티 방식은 아키텍처적으로 부적절 (성능 한계)
- ✅ 권장: VFX Graph(비주얼) + ECS(`SnowAccumulationSystem`, 디포머, 젖은 바닥 셰이더 파라미터)
- ⬜ (다음 단계 기입)

---

## 렌더링 / 가시성
- ✅ "눈에만" 투명: `MaterialMeshInfo.Mesh` 조작 (수천 유닛, 비구조적·Burst)
- ✅ 엔진 생성 자식 렌더링 토글: 루트의 `LinkedEntityGroup` 순회
- ✅ 베이킹 패턴: Baker는 최소 변환, `PostBakingSystemGroup`에서 복합 조립

---

## 셰이더 (물 / 선택 마커)
- ✅ URP 물: Gerstner 파도, depth texture 교차 해안 포말, Fresnel 반사, 코스틱
- ✅ URP 선택 마커: `sdRoundedBox` SDF, 비균일 스케일 UV 보정 (`PostTransformMatrix`)

---

## 투사체 / 데미지
- ✅ 3종: 호밍 / 저장 위치 직격 / 범위
- ✅ 명중/회피 판정
- ✅ `IEnableableComponent`: `IncomingHitEvent`/`AttackerNotification`/`TargetNotification` 버퍼는 비활성 초기화 후 이벤트 시에만 토글

---

## 저장 / 로드
- ✅ 순차 `uint` 인스턴스 ID는 저장/로드 경계에서만 사용
- ✅ 로드 중 임시 `NativeHashMap<uint, Entity>` 구축
- ✅ JSON + GZip, 2패스 로드 시퀀스

---

## Input System
- ✅ Action Asset 계층 구조
- ✅ 런타임 리바인딩: 전체 에셋 순회로 중복 감지
- ✅ Common Map 패턴 (항상 활성화된 공유 액션)

---

## CLAUDE.md 문서화 (2026-06-15)
- ✅ 스크립트 경로 수정: `Assets/Scripts/` → `Assets/_game/scripts/`
- ✅ 전체 디렉토리 트리 + 파일별 역할 문서화
- ✅ 핵심 데이터 모델 전체 기술 (GridLayers, StampLayers, GameClock, 물류 3티어, 생산, 프리팹 레지스트리, 시민 컴포넌트)
- ✅ 코딩 원칙 4개 섹션 추가: 컴포넌트 설계 / Job·Burst / 시민·물류 통계적 접근 / 대량 핫패스 구조변경 금지
- ✅ 설계 원칙 표현 정확화: `Complete()` → "메인스레드 조기 Complete() 금지 (의존성 중심 병렬 설계)"
- ✅ 랜덤 액세스 쓰기 지침 범위 명확화: 대량 주기적 병렬 Job에만 해당, 희소·비주기는 ECB
- ✅ 완성품 생산 입력 불가 = 영구 설계 원칙 확정 (순환 의존 방지 + 물류망 단순화)
  - 모든 레시피 재료는 창고 경유 Raw·Intermediate만 사용
  - 고품질 완성품 = Final 재활용이 아닌 더 나은 Raw·Intermediate 조합으로 설계
- ✅ 자주 발생하는 실수 방지 표 확장 (7개 항목)
