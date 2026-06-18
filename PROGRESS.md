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

- 🐞 **(2026-06-19 미해결, 기록만) 큰 건물 1개 후 무한 셀검증실패 + 도로 폴백 안 됨**
  - 증상: AI가 큰 건물 1개 짓고 나선 계속 큰 건물만 시도. 도로를 더 깔아 다른 해법을 찾지 못하고 `BuildingPlacementSystem` "셀검증실패(ValidateCells)" 로그만 반복.
  - 추정 원인 1 — **회전 footprint 불일치**: `TryPlaceBuilding`은 **비회전** `meta.Size`로 origin/footprint를 검증(`FootprintFreeFlat`)하지만, 발행 시 `RotationY`(입구가 도로 향하게)를 같이 넘김. `BuildingPlacementSystem.ValidateCells`는 **회전된** 크기(`RotateSize`)로 다른 셀 집합을 검증 → 비정사각 큰 건물에서 AI가 안 본 셀이 점유/범위 밖이라 실패.
  - 추정 원인 2 — **점수 고착**: 점수=응집×1000 + 넓이라 큰 건물(넓이 큼)이 항상 최고점. 같은 자리·같은 큰 건물을 매일 다시 골라 계속 실패. 게다가 `found=true`로 반환되어(발행은 했으니) **도로 연장 폴백이 안 일어남** → 영구 교착.
  - 추정 원인 3 — **WrongTerrain 미검사**: AI는 지형 타입(`BuildableOn`)을 안 봐서, 물/불가지형 도로변을 골라 발행하면 ValidateCells가 `WrongTerrain`으로 실패(원인1과 겹쳐 교착 심화).
  - 해결 방향(미착수): ① AI가 회전을 footprint 검증에 반영(회전된 size로 검사하거나 입구-도로 정렬을 footprintFree 콜백 오버로드로) ② 발행 전 ValidateCells와 동치 검증(지형 타입 포함)으로 "성공 보장된 것만" 발행 ③ 실패/미배치 시 `found=false`로 도로 연장 폴백 보장 ④ 큰 건물 고착 완화(넓이 가중 축소 또는 자리 적합도 우선).

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
