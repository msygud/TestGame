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

### 다음 단계
- ⬜ 그라운드 큐브 같은 중심-피벗 프리팹들의 `RegistryItem.Offset` 일괄 점검/설정
- ⬜ 시민 초기 스폰 트리거 (여전히 미착수)
- ⬜ 플레이어 건물 배치 UI (여전히 미착수)
- ❓ `campSize % roadSize != 0`일 때 가장자리 핏 보정 (현재는 단순 truncation, 약간 빈 틈 가능)

---

## City Expansion AI (자율 다중 팀)
- ✅ `NativeHashMap<int2, CellData>` 싱글톤 셀
- ✅ 팀 단위 `DynamicBuffer<BuildRequest/Response>`
- ✅ `CityCellManagerSystem`
- ✅ 불변식: 도로는 항상 닫힌 사각형 / 볼록 정점만 확장 후보 (사분면 압축) / `ClaimedTeam` = 점유와 무관한 영구 영토 소유 / 유효 정점 쌍 없을 때 U자 폴백
- ⬜ (다음 단계 기입)

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
