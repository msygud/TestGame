# 진행 상황 (PROGRESS)

> 작업 시작 전 해당 섹션을 읽고, 종료 시 갱신할 것.
> 형식: ✅ 완료 / 🔧 진행 중 / ⬜ 다음 단계 / ❓ 미결정

---

## 🔴 다음 세션 최우선: 시뮬레이션 잡화(jobification) — 방향 합의됨 (2026-07-01)
> **배경**: 256×256 꽉 찬 맵에서 TerritorySystem(체임퍼 최적화 후에도) 1초 틱마다 메인스레드 **~80ms 스파이크**,
> 평시 평균 **~30ms**. 유저 코드 검토 결과 전 시스템이 메인스레드 — **"느슨함" 원칙의 오독**이었음
> (저빈도 메인 실행 ✗ → **백그라운드 잡** ✓). CLAUDE.md "Job/Burst 설계 원칙"에 재해석 명문화됨(먼저 읽을 것).

**합의된 목표 실행 모델**: ① Burst 잡 + `state.Dependency` 체인 ② 공유 레이어는 **더블 버퍼**(백에 계산→완료 시
프런트 스왑) ③ `IsCompleted` **폴링**(블로킹 Complete 금지 — "이전 계산 끝났으면 다음 시작") ④ 1초 틱 위상 분산
⑤ 메인스레드는 스왑/ECB 적용/GPU 업로드만.

**다독자 계약(합의)**: TerritoryLayer 등은 독자가 많고 계속 는다(게이트 6곳+렌더+미래 전략AI) → 단일 쓰기자 +
프런트 불변 스냅샷 + `TerritoryVersion` 캐시 키. 독자는 매 업데이트 싱글톤에서 새로 받아 읽기만(기존 코드 이미 준수
— 독자 수정 사실상 0). 스왑 순서는 GridLayers 컴포넌트 접근 선언으로 강제(확립 기법).

**프로파일 실측(2026-07-04, 유저 제공 — Main Thread ms)**: AiCityGrowth **43** / UnitSelectionTestController 7.5 /
AiRoadJanitor 3.1 / RoadSystem 1.6 / HealthBar 0.26. 각 시스템이 자기 게이트(틱) 때 스파이크.
로지스틱 pull/push는 현재 사용량이 적어 낮지만 본격 사용 시 커질 전망(유저 판단).
UnitSelectionTestController는 전투 유닛 세션에서 함께 다루기로(유저).

**이행 우선순위(실측 반영 재조정)**:
1. ✅ **AiCityGrowthSystem 잡화 완료(2026-07-04, 컴파일 통과)** — 43ms 스파이크 → 워커. 아래 상세.
2. ✅ **TerritorySystem 잡화 완료(2026-07-05)** — 더블 버퍼 + 폴링. 아래 상세.
   **✅ 시뮬 검증(유저, 256×256 독립 4팀)**: 평균 20~25ms 유지, 카메라 전환 끊김 없음(메인 비블로킹 확인).
   프로파일러에 스파이크는 보이나 = 두 무거운 계산이 단일 잡으로 워커에서 20~30ms 도는 순간(의도된 모습).
   잔여 스파이크 후보: ① 스왑 프레임에 TerritoryVersion +1 → 아웃라인/F7 **메시 재구축(메인)** — 우선순위 4
   (렌더 빌더 잡화)로 해소 예정 ② 1초 틱 시스템들 위상 정렬(스태거 미적용).
3. ✅ **AiRoadJanitorSystem 잡화 완료(2026-07-05)** — AiCityGrowth와 동일 패턴(스냅샷+폴링). 아래 상세.
4. 렌더 빌더(아웃라인/F7/경고) 정점 생성을 Burst 잡으로. HP바(0.26ms)는 후순위. **← 다음 작업**.
5. TerritoryCaptureSystem / DeadReferenceReclaim → IJobEntity + ECB ParallelWriter + `EntityStorageInfoLookup.Exists`.
6. 로지스틱 pull/push — 본격 사용 전 같은 패턴 선제 적용.

### ✅ GC 쓰레기 수술 (2026-07-05) — 주기적 300MB GC 스파이크(~10ms) 대응 (⚠ 에디터 검증 필요)
> 잡화 후 실측: 평시 <20ms(시작 10ms), 최대 스파이크 = 주기적 GC(누적 ~300MB, ~10ms).
> 원인 = 매 프레임 관리형 할당(IMGUI 쿼리/문자열이 주범). 수정 원칙: **문자열은 값이 바뀔 때만
> 재조립, EntityQuery는 월드당 1회, TMP/TextMesh 대입은 참조 바뀔 때만**.
- **GameClockHud**: `CreateEntityQuery`를 OnGUI(프레임당 2회+)마다 생성/해제하던 것 → 월드당 1회 캐시.
  시간 문자열은 (일·분·배속) 변화 시만 재조립(게임-분당 1회).
- **ResourceDebugVisualizer**: 쿼리 캐시 + 셀 라벨 문자열 (TypeId,Amount) 변화 시만 재조립 +
  TypeId→색 캐시(LoadRuntime 셀×프레임 호출 제거). 자원 셀 수 × 2회/프레임 문자열이 최대 발생원이었음.
- **GameHUD**: `RefreshRoadPanel`(매 프레임)의 라벨 대입을 `SetTextIfChanged`(참조 비교)로 게이트,
  Segments 문자열은 수 변화 시만. TMP 재레이아웃도 함께 절약.
- **RoadBuildController.HoverStatusText**: `$"Blocked: {..}"` 보간 → 상수 switch(무할당).
  **BuildingPlaceController.StatusText**: (key·회전·상태) 변화 시만 재조립(캐시).
- **UnitSelectionTestController**(최소 패치 — 본 개편은 전투 세션): OnGUI 헤더(항상 켜짐, ShowDebugHud=true)
  카운트 변화 시만 재조립 / 이름 라벨(명령 종류 변화 시만) / HP 라벨(정수 표시값 변화 시만).
  ⚠ 무기 상태 라벨(FormatWeaponReadyStates)은 여전히 매 프레임 조립 — 전투 세션에서 정리.
- 남은 잠재원(미확인): IMGUI 자체 오버헤드(이벤트당 소량), 에디터 전용 할당(프로파일러/인스펙터).
  수정 후에도 GC가 남으면 프로파일러 **GC.Alloc 콜스택**으로 재확인.

### ✅ AiRoadJanitorSystem 잡화 (2026-07-05) — 스냅샷+폴링, ECB in-job (⚠ 에디터 컴파일/동작 미검증)
- **구조**: AiCityGrowth와 동일 — DayChanged → ① `SnapshotJob`(레이어 5종 + **CellTypes 테이블**까지 복사,
  `state.Dependency` 등록 + GridLayers RW 선언) → ② `JanitorJob`(Burst, 팀별 트림/섬/지선 + 입구 복구 전부 —
  **ECB에 직접 기록**, 핸들은 체인 밖 폴링) → ③ 완료 시 메인에서 `ECB.Playback`만.
- **ECB in-job 패턴 채택**: 명령 종류가 4가지(Remove/Place/PathRequest×2)라 출력 리스트 분리 대신
  Persistent ECB를 잡에 넘겨 기록 — `RoadPathSystem.EmitDrawnPath`(internal, 순수)를 잡 안에서 그대로 재사용.
- **입력 값-해석**: AI 팀(owner/faction/anchor)·RoadSpur 목록·입구 건물 목록(BldInput)을 메인에서 수집해
  배열로 전달. 룩업(CellType)은 스냅샷 복사 → 잡이 라이브 컨테이너를 안 들고 감.
- **공용 수정**: `RoadDirOps.Offset(int)` 신규(Burst-안전 switch 판 — managed 배열 `Offsets`의 잡 내 대체재),
  `BlockOps.FindRoadPathToIsland`가 이를 사용(janitor 잡에서 호출되므로). 다른 Offsets 소비자는 무변경.
- 스냅샷 복사가 AiCityGrowth와 같은 프레임(DayChanged)에 겹치지만 양쪽 다 빠른 읽기 잡이라 무해
  (RW 선언 순서에 따라 직렬화될 수 있으나 ~ms). 공유 스냅샷 통합은 이득 대비 결합도가 커서 보류.

### ✅ TerritorySystem 잡화 (2026-07-05) — 더블 버퍼 스왑 파이프라인 (⚠ 에디터 컴파일/동작 미검증)
- **구조**: 1초 게이트 → ① 입력 수집(메인, 소량: 거주지 쿼리·영향력 버퍼·config) →
  ② `ComputeJob`(Burst, 체임퍼 DT + 버킷 선별 + 경합 해소 **전부** — **백 버퍼**에만 씀, 핸들은
  체인 밖 폴링) → ③ 완료 시 메인에서 **TerritoryLayer ↔ 백 버퍼 핸들 스왑 O(1)** + `TerritoryVersion` +1.
- **다독자 계약 구현**: 프런트는 스왑 사이 불변(단일 쓰기자=이 시스템, 잡은 백에만 씀) → 게이트 6곳·렌더·
  capture 등 독자는 무수정. 스왑 직전 `GetSingletonRW<GridLayers>`(쓰기 선언)로 등록 독자 잡과 순서 강제.
- **TerrainLayer는 복사 안 함** — 맵 로드 후 불변이므로 `_terrainKeys` 키 집합을 **최초/Count 변화 시 1회**만
  `TerrainKeysJob`으로 재빌드(이것만 `state.Dependency` 등록). 매초 스냅샷 비용 0.
- 옛 프런트(스타트포인트 초기값 포함)는 스왑 후 백 버퍼가 되어 다음 계산이 Clear 후 재사용.
- 부수 효과: 예전엔 재계산 프레임에 레이어가 clear→rewrite 중간 상태였는데(같은 프레임 내라 무해했지만),
  이제 독자는 항상 **완성본만** 본다(엄밀히 개선).
- 참고: owner별 병렬 DT(8잡 분할)는 보류 — 1초 케이던스에서 지연 무의미, 단일 IJob(워커 점유 ~수십 ms/초)로 충분.
  워커 경합이 보이면 그때 분할.

### ✅ AiCityGrowthSystem 잡화 (2026-07-04) — 첫 "스냅샷+폴링" 파이프라인 (⚠ 에디터 컴파일/동작 미검증)
- **구조**: DayChanged → ① `SnapshotJob`(Burst, 레이어 5종+CellType水 해석을 잡 전용 복사본으로) →
  ② `GrowthJob`(Burst, 기존 성장 로직 전체 — 스냅샷만 읽음) → ③ `IsCompleted` 폴링 후 메인에서
  `PlaceRoadCommand`/`PlaceBuildingRequest` 엔티티 생성만(ECB Temp).
- **의존성 계약**: ①은 `state.Dependency`에 등록 + `GetSingletonRW<GridLayers>`(의도적 Write 선언)
  → 이후 레이어 접근 시스템이 '복사 완료'만 대기(빠름). ②의 핸들은 **체인 밖(프라이빗 필드)** —
  스냅샷만 읽으므로 여러 프레임 걸쳐 실행돼도 아무도 안 기다림. 잡 실행 중 DayChanged 오면
  `_dayPending`으로 완료 직후 재스케줄(일 스킵 없음).
- **잡 호환 변환**: 룩업(meta/입구)은 메인에서 `BuildOption` 값으로 선해석(잡이 룩업 컨테이너 안 듦),
  물/자원은 스냅샷 시점에 `WaterCells`/`ResourceBlocked` 셀 집합으로 선해석, `Debug.Log`는
  `GrowthLog` 이벤트로 모아 완료 후 메인 출력, `RoadDirOps.Offsets`(managed 배열)는 Burst-안전
  `Dir4()` 스위치로 대체. 로직 자체(후보 랭킹/구획 패킹/enclosure/베이스-연결)는 무변경.
- **미세 의미 변화(수용)**: `FootprintBuildableFlat`에서 TypeId 미등록 셀이 예전엔 지형 마스크 검사
  통과였는데 이제 Land 취급(성장 건물은 Land 전용이라 실질 무영향).
- ⚠ ResourceLayer 미생성 대비 `_emptyRes` 더미 캡처. OnDestroy에서 잡 완료+해제 처리.

---

> 상태(2026-07-01 세션 종료): **아래 전부 에디터 컴파일 통과.** 동작 확인된 것 — capture=파괴/dwell/경고 비주얼,
> 건물 전투 파괴(근접), janitor 섬 버그픽스 전 증상. 미확인 — 앵커 베이스-연결 제한, 골목 제거 후 재개발, RoadSpur 자가수리.

## 🟡 캡처/경합지 구조물 처리 — **설계 개정 v2: capture = 파괴** (2026-06-30/07-01)
> v1("영역 획득으로 아무것도 자동 파괴 않음 + 연결성 스윕")은 시뮬레이션 결과 **폐기**:
> 도시는 도로가 하나의 망이라 연결성 제거가 사실상 발동 안 하고, 남은 적 도로는 owner-게이트라
> 점령자가 못 쓰고 그 위에 못 지음 → AI에게 점령지 = 못 쓰는 땅. → **capture=파괴**로 전환.

**v2 룰: 구조물이 '타팀 영토'에 dwell(유예) 이상 놓이면 자동 파괴.** 점령지는 항상 깨끗한 빈 땅 →
새 주인(AI 포함)이 즉시 정상 개발. 짧은 밀당(핑퐁)은 dwell이 흡수(파괴 없음). 경합지(-2)/중립은 캡처
아님(파괴 없음). 베이스급은 `CaptureExempt` 면제(전투로만). 무혈 잠식(인구 압박에 의한 평시 파괴)은
**의도된 룰**로 수용. 밸런스 값은 전부 `TerritoryCaptureConfig` 싱글톤(커스터마이즈).

- ✅ **TerritoryCaptureSystem** ([TerritoryCaptureSystem.cs](Assets/_game/scripts/RunTime/Systems/TerritoryCaptureSystem.cs), ~1초 주기,
  TerritorySystem 후·RoadSystem 전): 타팀 영토 위 건물/도로에 `CaptureDoom{DeadlineSeconds}` 부착(게임초 기준 →
  일시정지 자동 정지), 영토 회복 시 사면(제거), 데드라인 경과 시 파괴 — 건물=Raze식 정리+destroy, 도로=Forced
  RemoveRoadCommand. 패스당 `MaxDestroysPerPass` 상한(스파이크 방지, 이월). 건물은 `RequireFullFootprint`(기본 1)
  = footprint 전체가 넘어가야 대상(경계 걸침 보호). **경고 비주얼은 CaptureDoom.DeadlineSeconds 읽으면 됨.**
- ✅ **TerritoryCaptureConfig** ([TerritoryComponents.cs](Assets/_game/scripts/RunTime/Components/TerritoryComponents.cs)) —
  `DwellGameHours`(1=게임1시간≈현실50초) / `RequireFullFootprint`(1) / `MaxDestroysPerPass`(32) /
  `AiEnemyBufferCells`(4). 싱글톤 없으면 Default — Test.cs 인스펙터 push는 TerritoryConfig 패턴으로 추후 와이어링.
- ✅ **CaptureExempt 면제 배선** — `PlaceBuildingRequest.CaptureExempt` → `SpawnRequest` → 인스턴스 태그.
  `FactionBaseSpawnSystem`이 베이스 건물에 true 설정(무혈 본진 함락 방지).
- ✅ **AI 국경 완충** — `AiCityGrowthSystem` 게이트 2곳(BlockValid·DevelopParcels)을 `NearEnemyOrContested`
  (셀+8방향 buffer 샘플 근사)로 교체, `AiEnemyBufferCells` 만큼 적 영토에서 떨어져 확장 → 건설-파괴 churn 루프 방지.
- ✅ **AiRoadJanitorSystem** ([AiRoadJanitorSystem.cs](Assets/_game/scripts/RunTime/Systems/AiRoadJanitorSystem.cs), DayChanged, AI 팀 전용) —
  반파 잔해 정리로 "버려진 구역" 방지: ① **dead-end trim**(이웃 footprint ≤1인 막다른 도로 반복 침식 — AI 불변식
  '닫힌 사각형' 덕에 오검출 없음) ② **베이스-단절 잔해 섬**(Anchor 반경 8 시드 flood 미도달 + 자기 건물 없는 순수
  도로 그룹) 제거. 가드: `Explicit`(그린-모델: 유저/RoadPath 지선) 제외, 자기 건물 인접(입구 접근로) 제외,
  degree는 footprint 단위(멀티셀 안전).
- ✅ **v1 잔재 정리** — `RoadOrphanSweepSystem`·`BuildingDestroyedEvent` 삭제(스윕 은퇴, 이벤트 소비자 없음 → 누수 방지).
- ✅ **골목(C1) 폐기(2026-07-01, 유저 결정)** — 8×8 내부 골목이 링과 안 이어지고(그린 비트 선 축뿐) 파괴 후
  잔해가 재개발을 막아 애물단지 → 연결 수정(ConnectAlleyEnd)까지 갔다가 **완전 제거**(LayAlley/EmitAlleyRoad 삭제).
  결과: 도로 안 닿는 깊은 셀은 **빈 공터로 남음**(수용). janitor **섬 제거의 Explicit 가드 해제**는 유지
  (골목 외 그린 잔해도 정리 — RoadPath 지선은 망에 연결=섬 아님, 트림의 Explicit 가드는 유지).
- ✅ **영역 과대 — 원인 확정(밸런스)** — 진단 로그로 확인: owner=1 거주지 71·예산 1772셀·**PopPerCell=3**
  (기본 5보다 낮음 → 거주지당 66% 더 넓음). 시스템 정상, **Test.cs 인스펙터 PopPerCell로 튜닝**(4~5 권장). 로그 제거.
- ✅ **janitor 개편: Explicit 가드 폐기 + 지선(RoadSpur) 관리(2026-07-01)** — 유저 관찰 "끊어진 도로 정리 안 됨"의
  원인 = **AI 링도 그린-모델(Explicit)** 이라 트림의 Explicit 가드가 AI 도로 전체를 보호(아무것도 못 다듬음).
  ① 트림 가드 = 건물 인접 + **지선 끝**으로 교체(Explicit 폐기) ② `RoadSpur` 컴포넌트 신규 — `RoadPathSystem`이
  경로 성공 시 자동 등록(중복 방지) ③ janitor가 지선 관리: 온전(타겟 인접 도로가 베이스 연결)하면 끝 footprint
  보호(끝만 보호해도 라인 전체 degree 2로 안전), **끊기면 RoadPathRequest 재발행(자가 수리)** — 옛 조각은 섬
  제거로 청소 후 새 경로. 목적 소멸 시 RoadSpur 파괴는 목적 로직 소관(미구현).
  - ✅ **버그픽스(실측)**: 섬 제거가 footprint 단위 판정이라 건물 딸린 단절 섬에서 **교차로/코너만 일제 소멸**
    (교차로는 건물과 대각 관계 = 4-인접 아님 → 비보호). → **연결요소 단위**로 수정: 섬에 건물 있으면 통째 유지,
    없으면 통째 제거.
- ✅ **설계 원칙 확정: "베이스 연결"은 행위 규칙, 상태 불변식 아님(2026-07-01)** — 의도적 건설은 무조건
  베이스-연결에서만(사람·AI), 전투/점령이 만든 단절 '상태'는 **포위(siege) 상태**로 허용(기능 저하 + 회복 압력:
  재연결/함락/janitor). 상태 강제 시 "한 칸 절단=도시 절반 소멸" 퇴화 + 정합성 의존 시스템 없음이 근거.
  - ✅ **AI 앵커/입구 베이스-연결 제한** — `ComputeBaseReached`(Anchor 반경 8 시드 flood) 신규,
    `GrowOneBlock` 앵커 후보와 `PackOnGrid` 입구 도로를 baseReached로 게이트 → 단절 섬에서 제2 도시 증식/내부 개발 금지.
    (참고: `IsTeamRoad`류 명명은 실제론 owner(LocalId) 소유 판정 — 동맹 도로 공유 아님. 영역만 팀 공유. 리네임 후보.)
  - AI 재연결 정책 확정: **최우선 아님 — 병행 과제**. 분단 감지 시 게임-일 1회 경로 시도(가능=복구, 불가=포위 지속),
    성장은 계속. 구현은 시드-필터 FindRoadPath 필요(기존 ⬜ 항목과 통합, 다음 세션).

- ✅ **중립 도로 = 전투 타겟 가능(2026-07-01, 미검증)** — 익스플로잇 봉쇄: 중립 협곡의 도로 벽/파편은 AI가
  대응 수단 0(못 짓고·못 치고·capture 안 닿음). **영토별 도로 처분 매트릭스 완성**: 내 땅=불도저+capture /
  타팀 땅=불가(민간 보호) / **중립=전투 타겟**(무법지대). 구현: `TerritoryCaptureSystem` 도로 패스에서
  footprint **전체 중립**이면 `CombatTargetable(Building)`+`CombatHealth`+owner `TeamInfoData`+`LocalTransform`
  (footprint 중심) 부착, 보호 영토로 돌아오면 해제(재중립 시 풀 힐). 사망 시 `BuildingDeathCleanupSystem`이
  **Forced RemoveRoadCommand** 위임(CombatDestroyOnDeath 안 씀 — RoadSystem 정리 필수) + 전투 컴포넌트 즉시 해제.
  `TerritoryCaptureConfig.NeutralRoadHealth`(기본 200, **0=기능 끔**). HP바는 자동 표시.
  ⚠ AI가 이 수단을 '사용'하는 행동(막힘 감지→공격 명령)은 군사 AI 소관(미래).
  - ✅ **`CombatTargetable`을 `IEnableableComponent`로 전환(유저 제안, 관례 준수)** — 국경 이동에 따른
    add/remove 구조 변경 churn 제거: 최초 중립 시 1회 부착, 이후 **enable/disable 토글**(재중립=풀 힐,
    사망=disable). ⚠ 룩업 함정: `HasComponent`는 disabled도 true → 전투 유효성 검사 5곳에
    `IsComponentEnabled` 병행 추가(쿼리 기반 수집·픽킹은 자동 제외라 무변경). HP바는 disabled 타겟 숨김.
    유닛/건물은 기본 enabled라 동작 불변.

- ✅ **HP바 표시 규칙(2026-07-01)** — ① 선택 유닛/파괴 예정(CaptureDoom) = 항상 ② 인간 유닛 = 선택 시에만
  ③ 그 외(타 플레이어·도로·건물) = 손상(<100%)만. **손상 도로는 보호 영토로 들어와도 표시 유지**(수리 정보).
- ✅ **수리 길 열어두기** — [RepairSystem.cs](Assets/_game/scripts/RunTime/Systems/RepairSystem.cs):
  `RepairRequest{Target, RequesterLocalId}` 단발 명령 + 즉시 풀-수리(placeholder). 팩션·업그레이드별
  가능여부/비용/시간(진행형)은 시스템만 확장(명령·발행자 불변). ⚠ 발행 UI 미구현(철거 도구와 함께).
- ✅ **테스트 파괴 = Alt+좌클릭(건물·도로 공통)** — Test.cs: 클릭 셀에 건물 없으면 도로 확인 → Forced
  RemoveRoadCommand. 맨 좌클릭 파괴가 도로 건설 클릭과 충돌(시작 도로가 지워져 베이스-연결 전제 붕괴) → Alt 수식키로 분리.
- ✅ **AI 섬 재연결 v2(2026-07-01, 시뮬 피드백 반영)** — v1(단일 코너 타겟·stopAdjacent) 문제: 타겟이 섬의
  사전순 코너 고정이라 빙 돌아 평행선 생성, BFS에 영토 게이트가 없어 RoadSystem이 구간 거부 → 미연결 →
  매일 재시도로 벽돌-쌓기. **v2**: `BlockOps.FindRoadPathToIsland` 신규 — 목표 = **섬의 아무 1×1 자기 도로 셀
  인접**(최근접 접점 자동), 통과성 = RoadStepFree + **영토 게이트(적/경합 제외)** = 배치 게이트와 일치(BFS가
  곧 연결 가능성 검사 → '깔고 실패' 원천 차단). janitor는 섬마다 시도해 **하루 1개 성공까지**(실패 섬은 다음 섬).
  성공 시 그린 경로 발행(`EmitDrawnPath` internal 재사용) + 접점 상호 비트(겹침 OR). 길이 1(이미 인접) = 비트만 병합.
  (시드 필터판 `FindRoadPath` 오버로드도 유지 — 범용.)
- ✅ **입구 도로 복구(2026-07-01)** — janitor에 패스 추가: 입구 도로 셀에 자기 도로가 없는 AI 건물(=죽은 자산,
  이진 고장) → `RoadPathRequest{Target=입구셀, StopAdjacent=0, RegisterSpur=0}` 발행(owner당 하루 2개 스로틀).
  `RoadPathRequest.RegisterSpur` 플래그 신규 — 일회성 부설은 RoadSpur 등록 안 함(건물 소멸 후 영구 재부설 방지).
- ✅ **회전 불일치 버그픽스(유저 보고)** — `EntranceOps`가 "RotateY=CCW"로 가정했으나 **Unity 왼손 좌표계의
  +Y 회전은 CW**(+Z→+X) → 건물 메시는 CW, footprint/입구는 CCW로 돌아 불일치. 그리드 수학을 CW로 통일:
  `RotateOffset` 90↔270 케이스 스왑, `RotateDirOffset` 부호 반전. steps 순회 소비자(AI 회전탐색 등)는 무영향,
  ⚠ 기존 배치 저장분의 RotSteps 해석이 바뀜(테스트 씬이라 수용).
- ⬜ **인간 철거 도구** — 방침 합의됨: **단일 철거 모드**(도로+건물 공통, 셀 기준 판별), 클릭=단일 +
  **드래그 사각형** 지원. 권한 = 자기 건물/자기 도로 + 내 영토 위 타인 도로([D] 불도저 게이트 기존).
  건물 철거는 owner-게이트 명령 신규 필요(RazeAreaCommand는 소유 무관이라 부적합). 수리 발행도 같은 도구에.
- ⬜ **밸런스 config 통합(다음 세션 리팩터)** — 흩어진 값 인벤토리: `TerritoryConfig`(PopPerCell·MaxRadius),
  `TerritoryCaptureConfig`(dwell·완충·중립도로HP 등 5종), `GrowthConfig.Default`(AI 성장), SpawnSystem
  `BuildingDefaultHealth`(상수 500), janitor `BaseSeedRadius`(상수 8), HP바/경고 비주얼 치수(상수),
  수리 비용/시간(미정) → 도메인별 config 싱글톤으로 정리 + Test.cs 인스펙터 push 일원화.

### 🧭 방향성 메모: 현재 AI 성장은 발판(placeholder) — 진짜는 욕구 주도 배치 (2026-07-01 합의)
- `AiCityGrowthSystem`의 무조건 격자 확장은 **임시 골격**. 최종형: **시민 욕구(창고·식당·일자리·접근성)가
  "무엇을 어디에"를 결정** — 단절 대응도 정책이 아니라 욕구에서 창발(로컬 대체 시설 = 따로 성장 vs
  도로 요구 = 재연결, 비용 비교의 결과. 자연 재결합도 가능).
- **기반으로 살아남는 것**: capture=파괴/dwell, janitor(위생 계층), **RoadSpur = '도로 요구'의 원형**(욕구
  시스템의 출력 단자로 승격 예정), stamp/CivilianBFS(단절 → 커버리지 자동 절단 → 미충족 욕구 '저절로' 발생).
- **placeholder 정책으로 격하**: "앵커 베이스-연결만" 게이트 — 욕구 시대엔 "플래너가 지정한 구역 도로면 앵커
  허용"으로 완화("베이스 연결" 행위 규칙 → "수요-공급 네트워크 연결"로 일반화). "재연결=병행 과제"도 동일.
- ✅ **죽은 참조 복구(2026-07-01)** — [DeadReferenceReclaimSystem.cs](Assets/_game/scripts/RunTime/Systems/DeadReferenceReclaimSystem.cs):
  1초 주기로 시민의 `Home/Work`(죽음→Null+**UnassignedTag 재부착**=재배정 큐 복귀), `ServiceTarget.Supplier`
  (→Null=재탐색), `CurrentBuilding`(→Null+Idle), 죽은 참조+Traveling(→Idle=여행 취소) 복구. 예외는 원래 안
  났지만(가드 룩업) '영구 고아'를 방지. **캐리어는 불필요**(목적지가 엔티티 아닌 셀 경로 — 순수 비주얼).
- ⬜ 남은 리스크(설계 노트): 연쇄 함락 눈덩이(거주 파괴→인구↓→더 함락 — dwell로 완충, 과하면 거주건물 예외 검토) /
  dwell 상태는 저장/로드 시 리셋 수용.
- ✅ **성능 수술(2026-07-01, 전맵 정복 시 1초마다 ~1500ms 스파이크)** — 원인 2개:
  ① **TerritorySystem reach = 셀×거주지 브루트포스 O(W×R)** → **체임퍼(chamfer 3×3) 거리변환 O(W) +
  거리 버킷(0.25 양자화) 카운팅 선별 O(W)** 로 교체 — 거주지 수와 무관. 거리 근사(팔각형 ~4%)로 외곽이
  미세하게 각질 수 있음(수용). 오버플로 버킷 내 순서는 스캔 순(극단에서만, 결정적).
  ② **아웃라인/F7 채움이 매 프레임 전체 레이어 재구축** → `TerritoryVersion` 싱글톤(재계산마다 +1) 신규,
  두 렌더 시스템이 버전+토글 캐시 키로 **바뀔 때만 메시 재구축**(DrawMesh 제출만 매 프레임). 예전 예고 항목.
  성능 참고: 256×256 4플레이어 평시 CPU ~10ms초반(예산 내).
- ⬜ **AI 재연결(건물 딸린 고립 섬 → 본망 잇기)** — `RoadPathRequest` 재사용이 정석이나 **`BlockOps.FindRoadPath`가
  모든 자기 도로 셀(고립 섬 포함)을 소스로 시딩**해 섬을 타겟으로 주면 즉시 도달로 종료 → 그대론 불가.
  시드 필터(베이스-연결 셀만) 변형 필요 — 다음 세션. (건물 없는 섬 '파괴'는 janitor가 이미 수행.)
- ✅ **경고 비주얼(2026-07-01)** — ① [CaptureDoomWarningSystem.cs](Assets/_game/scripts/RunTime/Systems/CaptureDoomWarningSystem.cs):
  파괴 예정 footprint에 **펄스 점멸 테두리 띠 + 옅은 채움**(임박할수록 점멸 1.2→5Hz 가속, 주황→빨강).
  DrawMesh 투명 큐(UI 아래)·ZTest LessEqual. ② HP바 아래 **카운트다운 게이지**(남은 dwell 비율, 주황→빨강,
  [HealthBarRenderSystem.cs](Assets/_game/scripts/RunTime/Systems/HealthBarRenderSystem.cs) — 건물 전용, 도로는 ①만).
  `CaptureDoom.DwellSeconds` 필드 추가(비율 계산용). 사면 시 자동 소멸.

### 구현 진행 (2026-06-30)
- ✅ **[A] 건물 전투 타겟화** — `SpawnSystem`의 HasFootprint 건물에 `CombatTargetable(Building)`+`CombatHealth`
  (균일 기본 500, 임시)+`CombatDestroyOnDeath`+`TeamInfoData`(owner 팀) 부착 → 유닛이 적 건물을 공격·파괴 가능.
  `CombatTargetBounds`는 생략(없으면 `ResolveAimPosition`이 transform 위치로 폴백, 가드됨). TODO: 체력 per-building이면 BuildingAuthoring 베이킹.
- ✅ **[B] 전투 사망 그리드 정리** — [BuildingDeathCleanupSystem.cs](Assets/_game/scripts/RunTime/Systems/BuildingDeathCleanupSystem.cs):
  `CombatDamageApplySystem`→이 시스템→`CombatDeathSystem` 순서. `CombatDeadTag`+`BuildingFootprint`인 건물의
  footprint를 OccupancyLayer/GridMap에서 제거(땅 회수) + StampDirtyEvent. destroy는 CombatDeathSystem이.
- ✅ **건물 전투 통합 보강(2026-06-30, 테스트로 데미지 확인됨)**:
  - **타겟 픽킹** — 테스트 컨트롤러가 건물을 우클릭 지정 못 하던 문제: `GetCombatTargetWorldPickRadius`에
    `BuildingFootprint` 케이스 추가(반-대각선 반경, cellSize는 GridSettings 조회). [UnitSelectionTestController.cs](Assets/_game/scripts/Unit/auth/UnitSelectionTestController.cs)
  - **footprint-인지 사거리** — 건물 transform이 footprint 중심이라 큰 건물은 가장자리에 붙어도 '중심까지' 거리가
    사거리 밖→발사 못 하고 +x,+z 사분면 편향까지 발생. `CombatWeaponUtility.NearestTargetPoint`(건물=AABB 최근접 표면점,
    회전은 `RotSteps&1` 인라인) 신규. **engagement·ready-state·BodyForwardWeaponAim·CombatWeaponSetup** 네 잡이 표면 거리
    사용(각 잡에 BuildingFootprint 룩업+CellSize 배선). 발사 게이트(ready-state/aim)는 `range+3×tol` 여유. 유닛-유닛 불변.
    [CombatWeaponBakingSystem.cs](Assets/_game/scripts/Unit/System/CombatWeaponBakingSystem.cs)
  - **기존 버그 수정** — `CombatEngagementDecisionSystem`의 `Blocked` NativeArray가 LOS 그리드 없을 때 미할당(default)인 채
    잡 스케줄 → 예외. 더미 1칸 할당으로 해소(전투를 처음 돌리며 노출됨, 제 로직 결함 아님).
  - ✅ **근접 공격 시 데미지 정상** — 유닛이 건물 가까이 가면 파괴 가능(확인됨).
  - ⚠ **미해결(다음 세션 별도로): 강제공격 원거리 접근이 발사까지 안정적으로 안 됨.** 유닛이 사거리 경계 근처에서
    멈춰 발사 안 되거나(=`Range` 차단), 접근-깊이 보정 시 멈춤↔이동 진동. **원인**: 접근 정지 임계값(shouldApproach)과
    발사 사거리(OutOfRange)가 같은 값이라 유닛이 경계에 서고, 정지 슬롭/프레임 순서 지터로 미세하게 밖에 섬.
    **정답**: 접근에 **히스테리시스**(사거리 안쪽까지 붙고 크게 벗어날 때만 재접근) — 상태 추가 필요. 이번엔 접근 원복 +
    발사 게이트 여유(3tol)로 완화만 함. **자동 획득(auto-engage)·터릿 셋업**도 거리 검사 중심 기준 남음.
    → **유닛 이동/타겟팅 정밀화는 별도 세션에서.**
- ❌ **[C] 도로 정리 스윕 — 은퇴(2026-07-01)** — 연결성→근접 모델까지 갔으나 v2(capture=파괴)가 역할을 완전
  대체해 `RoadOrphanSweepSystem`·`BuildingDestroyedEvent` 삭제. 위 "설계 개정 v2" 섹션 참조.
- ✅ **[D] 땅 주인 불도저 게이트(2026-06-30, 게이트만·입력 미연결)** — [RoadSystem.cs](Assets/_game/scripts/RunTime/Systems/RoadSystem.cs)
  철거 권한 = 강제(Forced) / 자기 도로 / **내 팀 영토 위의 도로**(`cellTeam==teams.Get(remover)`). 마지막이 불도저 —
  캡처한 땅의 적 도로를 소유 무관 철거(전투 아님·룰 OK). ⚠ **플레이어 도로-철거 입력 경로가 아직 없음**(현재 `RemoveRoadCommand`
  발행처 = 스윕(Forced)뿐) → 게이트는 준비됐으나 트리거할 UI/입력 필요(데몰리시 툴: 호버 셀 → `RemoveRoadCommand{OwnerLocalId=player, Forced=0}`).
- ✅ **HP바(테스트용)** — [HealthBarRenderSystem.cs](Assets/_game/scripts/RunTime/Systems/HealthBarRenderSystem.cs):
  CombatHealth 가진 모든 엔티티 위 빌보드 바(빨강→노랑→초록), DrawMesh 투명 큐(UI 아래). `Camera.main` 필요.
  상시 표시 → 클러스터 시 'frac<1만'/'선택만'으로 좁힐 수 있음.
- ⚠ 컴파일/동작 검증은 에디터에서. [A]로 **모든 footprint 건물이 전투 타겟**이 됨(베이스 포함) — 필요 시 후속 특수처리.

---

## 🟢 영역/구획 개편 — 덩이 1+2 (2026-06-29)
> 용어: **영역(territory)**=인구로 점유한 셀 / **구획(parcel)**=도로로 갇힌 빈 구역.
> 결정: C1 골목분할 / D1 flood 파생 / 혼합 plot / capture는 파괴 대신 표시(타팀 구조물 아이콘+후속효과는 나중) /
> 3+ 경합은 최강소유+완충밴드. ⚠ 컴파일은 에디터에서(이 환경 미검증).

### 덩이 1 — 영역 표시화 + 확장 중첩차단
- ✅ **capture 파괴 폐기** — `TerritorySystem`이 적 영역 건물/도로 파괴 안 함(`RazeAreaCommand`/`RemoveRoadCommand` 제거).
- ✅ **영역(reach) ≠ 영향력(influence) + 팀 모델** — 영역 = `floor(인구/PopPerCell)`만큼 최근접 셀(물리 범위),
  각 셀을 그 플레이어의 **팀**으로 태깅. `TerritoryLayer`가 이제 **팀 id**를 담음. 같은 팀끼리는 경합 아님(동맹 공유).
  - **영향력 = 플레이어별 스칼라(입력)**, 같은 팀은 **합산**(동맹 연합). placeholder — 추후 행복도/팩션으로 대체.
  - **경합 구역(연결요소 T칸)**: 승자팀=영향력1등, `K = floor(T×(승자−2등)/승자)` 칸을 **승자 거주지 가까운 순**으로
    차지, 나머지 **중립**. **동률→K=0(전부 중립)**. 3+ 경합은 2등이 세져 K↓로 자연 반영(연합 가정 없음).
  - 입력: [TerritoryComponents.cs](Assets/_game/scripts/RunTime/Components/TerritoryComponents.cs)에
    `PlayerInfluenceConfig`+`PlayerInfluenceElement{Influence,Team}` 버퍼(인덱스=LocalId). Test.cs가 매 프레임 채움.
  - ✅ **동맹 게이트(team-aware) 완료(2026-06-30)** — 게이트가 셀값(팀)을 LocalId가 아니라 **내 팀**과 비교.
    신규 `TeamTable` 싱글톤(LocalId→팀, [TeamTableSystem.cs](Assets/_game/scripts/RunTime/Systems/TeamTableSystem.cs)이
    `PlayerInfluenceElement.Team` 버퍼에서 매 프레임 미러링, 없으면 Identity). `TerritoryOps.InEnemyTerritory`/
    `FootprintInEnemyTerritory`가 `in TeamTable`을 받아 `셀 팀 ≠ teams.Get(myOwner)` 판정 → **동맹(같은 팀)·내 영역
    오판 해소**(예전엔 team≠localId면 자기 땅도 적 영역으로 막힘). 게이트 5곳(건물/도로 배치·컨트롤러·AI×2) 전부 배선.
    team=localId 기본에선 동작 불변.
- ✅ **#1 도로 베이스 연결 필수** — `RoadBuildController`에 `FilterConnectedToNetwork`/`ComputeAttached` 재도입.
  유저 드래그는 **내 기존 도로망(=베이스에서 이어짐)에 연결된 구간만** 발행, 떠다니는 도로 차단. 프리뷰도 미연결=회색.
  (이전 'free 배치' 폐기 — 재반영. AI/라우터는 원래 기존망에서 출발해 무영향.)
- ✅ **#2 건물 입구 = 자기 도로 (재적용, AI 일치)** — `EntranceOps.IsEntranceOnOwnRoad`(입구 도로셀 owner==배치자) 신규.
  `BuildingPlacement` 게이트 + `BuildingPlaceController`(RequireRoadAccess=true) + **AI 사전검사(PackOnGrid/채우기)도 own-road**로
  통일 → 예전엔 AI가 any-road로 발행 후 own-road 게이트에 거부돼 깨졌던 것 수정. 인간은 입구 프리뷰로 정렬.
- ✅ **프리뷰 도구** — ① **입구 표시**: 입구 향하는 도로셀을 `PreviewStatus.Entrance`(밝은 청록 사각 테두리).
  ② **[ / ] 회전**(R도). ③ **페이딩 그리드**: `RoadBuildPreviewState.Center/HasCenter` → 프리뷰 중심 ±8셀 그리드 거리비례 알파.
  ④ **고스트 건물**: `BuildingPlaceController`가 실제 위치식(CellCenter+Offset)·회전으로 프리팹 인스턴스를 호버에 띄움
  (게임플레이 컴포넌트 제거=시각 전용, 키 바뀌면 재생성, 모드 종료 시 파괴).
- ✅ **#3 경합지(Contested) ≠ 중립** — `TerritoryLayer` 값에 **`-2`(경합지, 잠김)** 추가(`TerritoryOps.Contested`/`IsContested`).
  미배분 경합 칸은 absent(중립·열림)이 아니라 **-2로 마킹** → **누구도 건설/도로 불가**(건물·도로·AI 게이트 전원 차단).
  시각: **경합지=흰색 테두리**(팀=팀색, 중립=테두리 없음 — 셋 구분). 동률/박빙 경합지도 -2로 잠김.
- ✅ **영역 아웃라인 상시** — [TerritoryOutlineRenderSystem.cs](Assets/_game/scripts/RunTime/Systems/TerritoryOutlineRenderSystem.cs):
  소유자 다른 이웃과 맞닿은 **경계 변만** 소유팀 색 GL 렌더(상시). F7 fill(`TerritoryDebugSystem`)은 별개 유지.
  - ✅ **경합지 강조(2026-06-30)** — 경합지 경계 = **폭 있는 quad 띠**(셀 10%, 상시) + **F8 토글 사선 해치**
    (대각선 1줄→45° 줄무늬, 기본 OFF). 팀 경계는 얇은 라인 유지(셋 구분 보존).
    `GL.LineWidth`는 플랫폼별 무시라 띠는 직접 quad strip, 해치는 GL 라인.
    · F8 토글은 `TerritoryOutlineRenderSystem`이 소유(이전의 `TerritoryDebugSystem` 마젠타 전체채움은 폐기 — 색 대신 사선).
    · **렌더 경로 교체: GL 즉시모드(endCameraRendering) → 동적 Mesh + `Graphics.DrawMesh`(renderQueue=Transparent).**
      원인: Unity 6 URP(17, Deferred+RenderGraph)는 Screen Space Overlay UI를 파이프라인 내부 패스로 그린 뒤
      endCameraRendering 콜백이 실행 → 거기서 GL을 그리면 **UI 위로 올라옴**(ZTest로 못 고침, UI는 깊이 없음).
      투명 큐 메시는 투명 패스(=UI 패스보다 앞)에서 그려져 **Overlay UI가 위에** 오고, **ZTest=LessEqual**이라
      **유닛/건물이 앞이면 가린다**. (라인=MeshTopology.Lines, 경합지 띠=삼각형 strip, `Hidden/Internal-Colored` 머티리얼.)
      `TerritoryDebugSystem`(F7 영역 채움)·`RoadBuildPreviewRenderSystem`(도로 프리뷰)도 동일 경로로 이행(2026-06-30)
      → 세 GL 오버레이 전부 DrawMesh 투명 큐로 통일, UI 아래. (도로 프리뷰는 빌드 툴이라 ZTest=Always 유지=항상 보임,
      나머지는 LessEqual=유닛이 가림.) ⚠ 남은 GL-endCameraRendering 오버레이 없음.
- ✅ **A: 확장 바깥-전용** — `GrowOneBlock` 후보 내부가 enclosed 포켓이면 거부(`InteriorExterior`) → 비워진 8×8 안 4×4 링 중첩 안 함.

### 덩이 2 — 구획 격자패킹 + 골목 (DevelopParcels)
- ✅ **D1 구획 파생** — enclosed empty(=outside 아님·buildable·Land) 셀을 4-연결 연결요소(구획)로 묶음.
- ✅ **B 격자 패킹** — 구획 bbox 원점 기준 plot 격자로 배치. 큰 plot(stock) 먼저 → 작은(farm) 나중 = **혼합**.
  격자 정렬이라 **4×4에 2×2 4개 정확히**(첫 도로옆 greedy의 3개 낭비 해결). 입구 도로닿음 필수, claimed 중복방지.
- ✅ **C1 골목 분할** — 구획 최소 변 ≥6이면 중앙 직선 골목(1줄 도로) 발행 → 다음 틱 분할 → 깊은 셀이 도로에 닿아 채워짐(8×8 안쪽).
- ✅ OnUpdate: `DevelopParcels`(구획 채움)가 1순위, 개발할 갇힌 구획 없으면 `GrowOneBlock` 바깥 확장 1회.
- ✅ 정리: `TryFillBesideRoad`(구 first-fit 채우기) 데드코드 삭제(2026-06-30). 격자 패킹(`DevelopParcels`)으로 대체됨.
  `FootprintTouchesTeamRoad`/`IsTeamRoad`는 `DevelopParcels`·도로 링에서 여전히 쓰여 유지.
- ⬜ 한계/튜닝: C1 트리거 `min변≥6` 휴리스틱 / 골목-링 BFS연결은 그린모델 OR 의존(입구엔 충분, 시민보행 추후검증) /
  혼합 패킹 stock→farm 2패스(최적 빈패킹 아님). 타팀 구조물 아이콘+후속효과는 나중.

---

## 🟢 AI 채우기-우선 성장 + 인구 베이킹값 존중 (2026-06-28)
- ✅ **건물 단독 배치(채우기) 신규 — 폐쇄구역 중첩 해결** — 기존엔 성장 단위가 `DevelopBlock`
  (도로 링+건물) 하나뿐이라 이미 도로로 둘러싸인 빈 땅에도 또 링을 쳤음. 신규 `TryFillBesideRoad`:
  **기존 도로에 인접한 빈 셀에 '건물만' 단독 배치**(새 도로 X, 입구가 기존 도로에 닿는 회전 선택,
  footprint 평탄·Land·비점유·비적영역). `OnUpdate`는 한 틱에 `BuildPerTick`(기본 6)채까지 —
  **① 채우기 우선**(가능한 한 많이, `claimed` 셋으로 같은 틱 중복 방지) **② 채울 자리 없을 때만
  확장(DevelopBlock) 1회**. → 8×8 폐쇄구역 안에 2×2가 도로에 맞닿아 채워지고, 자리 다 차면 확장.
  AI는 farm_h(1004)·stock_h(1005)만 짓고 둘 다 거주가 아니라 territory를 안 만들어 자기 영역에 안 막힘.
- ✅ **자기-포위(stop) 수정 — 채우기는 enclosed 안쪽만** — 증상: 채우기가 바깥 빈 땅까지 양옆으로
  메워 도로가 더 못 뻗고 멈춤(베이스 안쪽 꽉→바깥 ㄴ자→정지). 원인: 채우기가 프런티어(열린 땅)를
  점유해 도로 확장 자리를 막음. 해결: `ComputeEnclosureOutside` — 팀 도로/건물을 벽으로 막고 도로
  bbox±8 테두리에서 flood → '도시 바깥' 집합 산출. `TryFillBesideRoad`는 **enclosed(바깥 아님) 셀만**
  채우고, **바깥 열린 땅은 도로 확장(DevelopBlock)용으로 보존**. → 안쪽 채우고 → 막히면 새 링으로
  바깥 확장 → 새 링이 새 포켓을 가둠 → 다음 틱 그 포켓 채움. 계속 자람(정지 해소).
- ❓ **구역(zone) 레이어 없음** — 예전 `CityZones`는 maintenance 폐기 때 삭제, `GridLayers.BlockLayer`는
  정의만 있고 미사용. 채우기는 '도로 인접'으로 충분해 레이어 불필요. 명시적 지구 추적이 필요하면 추가.
- ✅ **인구 = 베이킹값 존중 확인/수정** — TerritorySystem은 인스턴스별 `BuildingOccupancy`
  (Current>0?Current:Capacity)를 읽음(전역 아님). `Capacity`는 `BuildingAuthoring`이 프리팹별 베이킹.
  "일괄 적용" 원인 = **Test.cs 우클릭이 Capacity를 50으로 덮어쓰던 것** → 수정: 베이킹된
  `BuildingOccupancy`가 있으면 그 값 존중, 없을 때만 `TestResidenceCapacity`(기본 10) 부여.
- ⬜ 건물 종류 다양화(거주/식당 섞기)·수요 기반 선택은 추후. 명시적 지구 예약형(reserve→fill→expand
  상태머신)도 원하면 추가 — 현재는 스코어러 emergent.

## 🟢 Territory v2 — 중첩 전파 + 초단위 재계산 + AI 구역밖 확장 (2026-06-28)
- ✅ **중첩 전파(nearest-N)로 교체** — 기존 고정 디스크(겹쳐도 경계 안 커짐)를 폐기.
  소유자별로 모든 거주지의 셀 수(인구/PopPerCell)를 **합산한 예산**만큼 거주지 중심에서
  **가장 가까운 셀**을 채운다(다중소스). → 거주지가 겹치면 예산이 합쳐져 경계가 바깥으로
  밀려난다(중첩=확장). 셀 경합(다른 팀)은 **더 가까운 쪽**이 가짐(net by proximity).
  [TerritorySystem.cs](Assets/_game/scripts/RunTime/Systems/TerritorySystem.cs).
- ✅ **초단위 전체 재계산** — `HourChanged` 게이트 → `SystemAPI.Time.ElapsedTime` 1초 간격.
  매번 클리어 후 재작성이라 **기존 결정 셀도 재결정**(PopPerCell 바꾸면 곧 반영).
- ✅ **셀당 인구수 런타임 필드** — `TerritoryConfig`를 IComponentData 싱글톤으로 변경,
  [Test.cs](Assets/_game/scripts/Test.cs)에 `PopPerCell` 인스펙터 필드 추가 → 매 프레임 싱글톤에 push.
  없으면 TerritorySystem이 Default(PopPerCell=5, MaxRadius=64).
- ✅ **AI 확장은 적 영역만 회피** (`AiCityGrowthSystem.BlockValid`) — 처음엔 '내부는 어떤 영역이라도
  거부(`InAnyTerritory`)'로 했으나, **자기 베이스가 영역을 만들면 자기 영역 위에서 확장이 막혀 정지**하는
  버그가 있어 **`InEnemyTerritory`(적 영역만)로 완화** (2026-06-28). 자기 영역엔 자유롭게 채우고 확장.
  채우기·도로 링과 동일 규칙. ("구역 없을 때만 확장"의 의도는 '적 구역 침범 금지'로 재해석.)
- ⬜ 한계: 영향력 '합산(net)'은 거리 기반 근사(같은 팀은 합산 안 함). 윈도우 스캔+정렬은 1초 1회라
  비용 OK. 영역=in-bounds 셀(물 포함) — Land 한정은 추후.

---

## 🟢 건설 탭 건물 배치 UI (테스트용, 2026-06-28)
> 물류·생산·영역 등을 손으로 건물 깔아 확인하려고 건설탭에 건물 버튼 + 배치 도구 추가.

- ✅ **[BuildingPlaceController.cs](Assets/_game/scripts/RunTime/Systems/BuildingPlaceController.cs)**
  (`RoadBuildController`의 건물판): `EnterMode(mainKey)`/`ExitMode`, 마우스 호버 → footprint
  (`PrefabMeta.Size`+회전) 프리뷰(RoadBuildPreview 싱글톤·렌더 재사용, 도로빌드와 상호배타),
  **R = 90° 회전**, **좌클릭 = `PlaceBuildingRequest`**(`RequireRoadAccess=false` 자유배치).
  프리뷰 유효성은 힌트(점유/범위/자원/적영역/단차) — 진짜 검증은 `BuildingPlacementSystem`.
- ✅ **[GameHUD.cs](Assets/_game/scripts/RunTime/UI/GameHUD.cs) 건설탭**: `_registries`(SO)의
  Building 카테고리 항목(V0, MainKey>0)마다 버튼 **자동 생성**(`_buildButtonTemplate` 복제 →
  `_buildButtonContainer`). 클릭 → 그 MainKey로 배치 모드. Escape 해제, 탭 전환 시 모드 해제,
  도로/건물 모드 상호배타. 호버 라벨(`_lblHoverStatus`) 공유.
- 현재 Origin.asset 건물 = **1000 resitetal · 1001 residental · 1002 restarant · 1003 human_stock**
  (DLC1.asset도 `_registries`에 넣으면 같이 버튼화).
- ⬜ **Unity 와이어링**: GameHUD에 `_buildController`(BuildingPlaceController 부착 오브젝트)·
  `_registries`(Origin/DLC1)·`_buildButtonContainer`(레이아웃 그룹)·`_buildButtonTemplate`(Button+자식 TMP_Text)
  지정. PanelBuild 안에 컨테이너+템플릿 배치.
- ❓ 회전/배치 프리뷰 색은 RoadBuildPreview enum 재사용(적영역=빨강 Occupied로 표시). 전용 색 필요시 enum 추가.

---

## 🟢 도로 maintenance 폐기 + Territory/Influence v1 (2026-06-27)
> 아래 "🔴 방향 전환" 섹션의 결정(B)을 실행한 세션. **maintenance/zone 전체 제거 +
> 인구 기반 영역 시스템 1차 구현**을 같이 했다. ⚠ **컴파일 검증은 Unity 에디터에서**
> (이 환경엔 컴파일러 없음). 복구 기준점: git `road-maintenance-complete`(`ee368ae`).

### ✅ maintenance/zone 제거 완료 (제거 스펙대로)
- **파일 삭제(+meta)**: `RoadDecaySystem.cs` `RoadDecayTelegraphSystem.cs`
  `RoadMaintenanceDebugSystem.cs` `DepotPlaceController.cs` `ZoneComponents.cs`
  `NetworkRepairSystem.cs` `CityZoneInitSystem.cs`.
- **필드 체인 제거**: `IsRoadMaintenance`/`MaintenanceMaxDist`(`RegistryItem`·`BakedPrefabEntry`·
  `PrefabMeta`·`SpawnRequest`·authoring·`PrefabLookupBuildSystem`·`SpawnSystem`·`BuildingPlacement`·
  `PrefabRegistryWindow`) / `StampKind.RoadMaintenance` + `RoadMaintenanceDepot` + 도장 루프
  (`StampRebuildSystem`) / `MaintenanceMaxDistOverride` 전 체인.
- **시스템 정리**: `FactionBaseSpawnSystem`(베이스 depot + decay Exempt 등록 제거) /
  `AiCityGrowthSystem`(depot coverage Phase5 + `RegisterZone` + `GrowthConfig.Maintenance*` 제거,
  `CityZones` 의존 제거 — 블록 선택엔 안 썼으므로 안전) / `RazeSystem`(zone 해체·NetworkRepair 제거,
  **건물 파괴 + StampDirty만 남김**) / `BlockOps.FindReconnectPath` 제거(`FindRoadPath`는 유지) /
  `GameHUD` 관리소 토글 제거.
- **도로 연속성 제거**: `RoadBuildController.FilterConnectedToNetwork`/`ComputeAttached` 폐기 →
  **유저 도로 자유 배치**(베이스 연결 강제 없음, 설계 점5/7). 프리뷰 `Disconnected`는 미적용(attached=null).
- **★ 유지**: 공급자/창고 stamp 코어(`StampSupplier`/`WarehouseTag`/`StampLayers`/`StampRebuildSystem`)
  + `RoadCoverageOps.Flood`(그 stamp가 사용) + `RazeAreaCommand`/`RemoveRoadCommand` + 그린-방향 도로 모델.
  - grep 잔여 0건 확인. `PreviewStatus`의 Coverage/CoverageExisting/DepotExisting enum 값은 inert로 남겨둠
    (제거하려면 renumber + ToText/StatusColor 손봐야 해 보류 — 무해).

### ✅ Territory & Influence v1 구현 (설계 7점 1차)
- **[TerritoryComponents.cs](Assets/_game/scripts/RunTime/Components/TerritoryComponents.cs)**:
  `TerritoryConfig`(plain struct, `.Default` — PopPerCell=5, MaxRadius=24) + `TerritoryOps`
  (`InEnemyTerritory`/`FootprintInEnemyTerritory` — `GridLayers.TerritoryLayer`만 읽는 순수 게이트 헬퍼).
- **[TerritorySystem.cs](Assets/_game/scripts/RunTime/Systems/TerritorySystem.cs)** (메인스레드, `HourChanged` 게이트,
  `[UpdateBefore(RazeSystem)]`): ① 거주건물(`ResidenceBuilding`+`BuildingFootprint`+`BuildingOccupancy`)마다
  인구→셀수(`pop/PopPerCell`)→원형(디스크 r=√(N/π)) 영향력을 셀별 **8슬롯 누적 struct**(CellAccum,
  중첩컨테이너 회피)로 합산 → 순 최대 팀이 셀 소유 → `TerritoryLayer` 클리어 후 재작성(유일 writer).
  ② **capture**: 적 영역에 든 적 건물→`RazeAreaCommand`, 적 도로→`RemoveRoadCommand{Forced=1}`(설계 점1·6).
  - 인구원: `Current>0 ? Current : Capacity`(시민 스폰 전엔 정원=잠재인구로 가시화. 스폰 붙으면 Current가 진짜).
- **빌드 게이트(휴먼·AI 공통)**: 건물 `ValidateCells`(`PlacementFailCode.EnemyTerritory=8`) +
  `RoadSystem` 배치 + AI `BlockValid` 전부 적 영역 셀 거부(설계 점1·5).
- **[TerritoryDebugSystem.cs](Assets/_game/scripts/RunTime/Systems/TerritoryDebugSystem.cs)** (에디터/개발빌드,
  **F7 토글**, 기본 OFF): `TerritoryLayer`를 소유자별 색 GL 반투명 채움(RoadBuildPreview GL 패턴).
- **시스템 순서**: `TerritorySystem → RazeSystem → RoadSystem`(같은 프레임에 capture 명령 실행).
- 재사용: `GridLayers.TerritoryLayer`(GridInit가 alloc/dispose, 이전엔 미사용) → 이제 TerritorySystem 전용.

### ⬜ 다음 단계 / 한계 (v1)
- ⚠ **에디터 컴파일 검증 필수** (이 환경 미검증). 거주건물에 `ResidenceBuilding` 태그 + `BuildingOccupancy.Capacity`
  설정돼야 영역이 보임(없으면 빈 영역 — 게이트는 안전하게 no-op). F7로 확인.
- 근사: 디스크 전파(정확 nearest-N 아님) / 선형 영향력 감쇠 / 영역은 in-bounds 셀만(물 셀도 현재 포함 — 추후 Land 한정 검토).
- 영향력 순값 저장 안 함(현재 owner만 `TerritoryLayer`에 기록). 행복도·다용도(설계 점3·6) 미구현 → 추후 InfluenceLayer.
- capture가 `HourChanged`마다 적 점유물 재스캔(파괴는 같은 프레임 1회). 잦은 전투에선 즉시성 위해 이벤트 구동 고려.
- 멀티셀 도로(roadSize>1) capture는 셀당 RemoveRoadCommand(RoadSystem이 footprint 통째 제거로 근사).
- 시민 초기 스폰 트리거(여전히 미착수)가 붙으면 인구원 `Current`로 자연 전환(공식 동일).

---

## 프리팹 메타데이터 아키텍처 정리 (2026-06-25, 설계 합의)
> 의견교환 세션. 코드 변경 없음, 골격 확정 + 문서화. 상세는 `CLAUDE.md`
> "프리팹 메타데이터 3역할" 절. 여기엔 결론 요약 + 다음 작업만.

### 확정된 골격 (유닛·건물·도로 공통)
- ✅ **3역할 분리**: 해석(key→엔티티, 공유 단일 SO) / 결정(상황→key, 결정자별 독점) /
  능력(인스턴스가 뭘 하나, **엔티티 컴포넌트**). 새 데이터는 반드시 한 역할에 배치.
- ✅ **능력 = 컴포넌트** (SO/레지스트리 아님). 접근은 **Entity 핸들 하나로 통일** —
  인스턴스 전엔 `PrefabLookup`이 준 프리팹 엔티티의 컴포넌트, 후엔 인스턴스의 같은 컴포넌트.
  레지스트리는 핸들만 해석, 사실은 저장 안 함.
- ✅ **읽는다 ≠ 소유한다**: 결정이 사실(Size 등)을 읽어도 그 사실은 능력으로 남음.
  크기는 결정요소 아님(기능은 수용량에 달림). 모양≠값: 통일은 타입·접근이지 값 동결 아님.
- ✅ **능력 per-MainKey** (변종 무관), MainKey 조인은 **베이크 시점에만**. 런타임 엔티티는 자기완결.
- ✅ **업그레이드 = 능력의 쓰기 측**: 머티리얼라이즈(직접 수정), 읽기 무지, base는 SO 보존,
  `IEnableableComponent` 토글 우선. 건물에도 동일. (`NeedMaps→L2→UpgradePatchSystem` 일반화)
- ✅ **데이터 주도 부착 결론**: 값은 문서 주도 OK / 구조(어떤 컴포넌트)는 타입드 슈퍼셋 +
  enable로. 게임데이터 SO는 bake/쓰기 소스로만(런타임 직접 조회 금지). 분할 authoring 권장,
  미러링 SO 지양. enum-배열 부착은 가능하나 베이스 값은 베이크가 이득.

### 다음 작업 (실행 전환 시, 우선순위순)
- ⬜ **`Validate()` 교차검증** — 모든 `NeedMaps` 행 MainKey가 `IsSupplier && ReliefRaw ⊇ NeedMask`인
  프리팹을 갖는지. drift/누락을 에러로(가장 싼 안전망).
- ⬜ **유닛 완성도 검증기** — 카테고리별 필수 컴포넌트 집합 정의 → 베이크/Validate가 누락·잉여 플래그.
  "이 유닛에 뭐 빠졌나"를 명시적 에러로. (가시성 니즈 = 검증으로 해결, 부착방식 변경 불필요)
- ⬜ **건물 능력 → 컴포넌트 이행** — `BuildingAuthoring` 베이킹, 배치는 프리팹 엔티티 컴포넌트 읽기.
  `RegistryItem`은 해석(키+프리팹+분류)만 남김.
- ⬜ **`ReliefMask`↔`ReliefRaw` 이중 출처 해소** — 컴포넌트 단일화로.
- ❓ 중앙 게임데이터 SO(밸런싱 테이블) 도입 여부 — 유닛 종류 많아지면. 단일 출처·bake 전용 조건.

---

## 맵에디터 개선 (2026-06-20)
- ✅ **Shift+드래그 연속 배치**: 배치 탭에서 Shift+좌드래그 시 드래그 시작 셀 기준
  오브젝트 크기(`ValidSize`) 격자에 스냅해 겹치지 않게 연속 배치 (`PlaceSnappedDuringDrag`).
  size=1(도로)이면 매 셀 연속 드로잉. `TryPlace`가 점유/경계 재검증.
- ✅ **높이 0 미표시**: `TerrainLayerPainter.TryGetHeightLabel` — `Height>0`일 때만 라벨 출력.
- ✅ **자원 카탈로그 SO**: `ResourceCatalog`(RunTime/Registry) — 자원 종류/이름/색을 데이터로 정의.
  에디터(`ResourceLayerPainter`)는 팔레트·색을 카탈로그에서 읽고 셀엔 **양만** 칠함.
  `MapResourceDefs`는 카탈로그 우선 + enum 폴백. `ResourceDebugVisualizer`도 카탈로그 색 사용
  (`Resources/ResourceCatalog` 자동 로드, 없으면 HSV 폴백).
  - ⬜ Unity에서 `Create > CitySim > Resource Catalog`로 에셋 생성 + 항목 채우기.
    런타임 디버그 색까지 쓰려면 에셋을 `Resources/` 폴더에 이름 `ResourceCatalog`로 둘 것.
- ✅ **Debug 폴드아웃 로그 뷰어**: 접힘 기본(클릭 시 펼침) + Application.logMessageReceived
  캡처를 **고정 높이(120px) 스크롤 박스**에 누적 → 메시지가 쌓여도 창 레이아웃이 안 밀림.
  (캡 200줄, 자동 최신 스크롤, Clear 버튼)

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
- ❓ 물-육지 경계 삼거리 이슈 — `StampTestBootstrap`(이미 삭제됨)이 원인으로 추정, 미확인
- ✅ **(2026-06-20) 도로 연결 모델 = 셀 축(axis) 기반 통일 — 평행 분리 + 의도적 교차** (이전 "평행 도로 가로지르기 불가" 미결 해결)
  - **원칙**: 각 셀은 자기를 지나는 세그먼트의 축을 가짐(`RoadCell.Axis`: EW / NS / Any=교차). **인접 두 셀은 공통으로 가진 축 방향으로만 연결**(축-AND). → 평행 도로(둘 다 NS)는 1칸 옆이어도 영원히 분리, 가로지른 셀만 4방향.
  - **`RoadSystem.ComputeDirections` 축-AND로 교체** — `myAxis` 인자 추가. `AxisAllows`(축이 그 방향 허용?), `CombineAxis`(겹치면 Any로 승격) 헬퍼. 이게 권위 데이터 `RoadCell.Directions`를 결정 → 시각·보행·물류 전부 자동 일관.
  - **도로 위 도로 = 거부가 아니라 병합(교차로 승격)** — `RoadSystem` 배치: footprint 1×1 + 같은 소유자 도로 셀이면 그 셀 축에 새 축 `|=`(→Any), 기존 엔티티 dirty, 새 엔티티 안 만듦(`originFresh`). 다른 소유자/건물/물/단차는 기존대로 거부. (size>1 부분 겹침은 모델 한계로 미지원 — roadSize=1이라 무관.)
  - **시각**: size==1은 셀 `Directions` 그대로 사용(매크로 축-OR 함수 은퇴, size>1만 사용).
  - **`StampRebuildSystem` BFS가 `Directions` 따름** — 기존엔 4-이웃 존재만 봐 평행 도로로 물류가 새어나감. 이제 `cell.Directions` 비트 + 이웃 반대 비트(양방향)를 확인 → 물류 도달이 보행과 동일.
  - **AI 링/유저 드래그 둘 다 지원**: AI 링은 변=EW/NS·코너=Any(직전 수정), 유저는 `RoadBuildController`가 드래그축으로 EW/NS 부여 + 같은소유자·1×1 교차를 이미 허용(`SegmentBlockStatus`/`EvaluateCell`). → 두 평행 도로를 가로지르면 양쪽 다 사거리 + 데이터 정확.
  - ✅ **원클릭 도로 = 무효** — `RoadBuildController.Confirm`이 2칸 미만 세그먼트는 발행 생략. 이유: 원클릭은 축(방향)이 없어 세그먼트를 정의 못 하고, "인접만으로 연결 예단 안 함" 원칙과 일관(예외 두면 (0,0) 옆 클릭 같은 모호한 연결 판정 발생). 연결하려면 도로→도로로 드래그(통과). 1칸 틈 메우기·교차도 짧은 겹침 드래그로 표현 가능.
  - ⬜ **교차로 철거 의미는 근사(가)**: 한 셀에 두 축이 겹친 교차로에서 한 세그먼트만 철거해도 셀 통째 제거(남은 축 복구 안 함). 정확한 (나)(축별 카운트로 남은 축 유지)는 추후. AI는 철거 안 써 현재 무영향.

### 도로 연결 모델 = "그린-방향(연속성)"으로 전환 — 유저 드래그 (2026-06-21)
> **확정 원칙(영구)**: 도로 연결은 **셀이 겹쳐야만** 일어난다. 인접만 한 도로는 영원히 분리
> → 의도적으로 끊어진 도로를 만들 수 있다. 유저 드래그는 **연속성**이 기본(한 번 꺾는 ㄴ).
> 기존 도로 위로 겹쳐 지나가면 진입/탈출 방향이 그 셀에 OR로 추가돼 기존 모양이 바뀐다
> (직선→T/사거리). BFS·물류는 이미 비트 상호일치 모델이라 무수정 호환.

- ✅ **모델**: 각 도로 셀 `Directions` = 드래그 경로가 실제로 이어준 방향 비트.
  각 셀 = (이전 셀 방향 if 있음) | (다음 셀 방향 if 있음). 시작=다음만, 끝=이전만,
  중간/코너=둘 다. → **경로 이웃을 향한 비트만 담겨 상호 비트 불변식이 자동 성립**
  → 경계 전파(`UpdateFootprintBoundaryDirections`)·축 스캔 불필요(그린 셀에 한해).
- ✅ **레거시 축(Axis) 모델과 무누수 공존 (브리지)** — AI 링/베이스/맵 도로는 아직 축 모델.
  `RoadCell.Explicit`(신규 bool) = 그린-방향 셀 표식. 축 재계산(`ComputeDirections`)과
  경계 전파가 Explicit 셀을 **건너뛰어** 그린 권위값을 보존. `ComputeDirections`의 이웃
  판정을 `NeighborAllows`로 교체: Explicit 이웃은 "그 셀에 반대 비트가 있나"(그린 비트),
  레거시 이웃은 기존 `AxisAllows`. → 그린 도로 옆에 레거시 도로가 와도 **겹치지 않으면
  연결 안 됨**(평행 분리 일관). `PlaceRoadCommand.Directions`(신규) 비-None = 명시 모델.
- ✅ **RoadSystem 배치 분기**: `cmd.Directions != None`이면 새 셀 set / 기존 같은-소유자 셀
  OR(겹침=교차/T 승격), `Explicit=true`, FlowAxis 갱신, 경계 전파 생략, dirty 발행 후 continue.
  레거시(Directions=None, AI/베이스/맵)는 기존 축 경로 그대로.
- ✅ **`RoadBuildController`**: `BuildPath`를 한 번 꺾는 ㄴ(L)로 교체(X 다리→Z 다리, 코너=(ex,start.y)).
  `EmitDrawnDirections`가 경로 인접에서 셀별 비트 산출(여러 세그먼트 겹치면 OR 누적) →
  `Confirm`이 셀당 `PlaceRoadCommand{Directions}` 1개 발행. 비주얼은 무수정(Directions→프리팹).
- ✅ **정책 유지**: 2칸 미만(원클릭) 무효 / **한 셀이라도 무효면 세그먼트 전체 무효**.
- ✅ **(2026-06-21) AI 블록 링도 그린-방향 전환** — 증상: AI 블록이 **겹친 교차로에서만**
  연결되고(축 `CombineAxis`), 이음매·T 등 비교차 접점은 축 불일치로 분리됨. 원인: AI 링이
  레거시 축 모델. 해결: `AiCityGrowthSystem.DevelopBlock`이 Road==1(기본 `DefaultSize`=1)일 때
  `CollectRingRoadsDrawn`로 **연속 루프 그린-방향** 발행 — 각 링 셀 Directions = 같은 링 4-이웃
  비트(코너=수직2, 변=직선2). 링 전체(새+공유 셀)를 모두 발행 → RoadSystem이 새 셀 set/기존 OR.
  `QuadrantOrigin` 정렬로 새 블록의 근접 두 변이 기존 도로와 **셀 공유(겹침)** → OR 병합으로
  교차로 아닌 곳도 연결. 멀티셀(Road>1)은 명시 분기가 1×1만 다뤄 기존 축 폴백 유지.
- ⬜ **다음 단계(전체 은퇴)**: 베이스 외곽(`FactionBaseSpawnSystem.EmitPerimeterRoads`)·맵 로드
  도로를 그린-방향으로 전환(현재는 AI 링이 겹침 OR로 베이스와 연결되므로 동작은 함) →
  `RoadPlacedAxis`/`AxisAllows`/`CombineAxis`/`ComputeAxisFilteredMacroDirections`·구 `CollectRingRoads`
  은퇴. 멀티셀(roadSize>1) 그린-방향 처리도 그때(유저 드래그·기본 AI는 1×1이라 현재 무관).
- ⬜ 프리뷰 색칠은 아직 `SegmentAxis`(축) 기반 — ㄴ 코너 경고색이 약간 부정확(비차단·미관). 추후 정리.

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

### 테스트 부트스트랩 정리 (2026-06-20)
- ✅ **유령(안 보이는) 도로 원인 제거** — `LogisticsTestBootstrap.cs`가 `RoadSystem`을 우회해 `RoadLayer`에 `(10,10)→(14,10)` 5칸을 직접 등록(owner=0, 비주얼 없음). 게이트가 `GridLayers`/`StampLayers`뿐이라 **매 에디터 플레이마다 자동 실행** → slot 0 도로 확장 시 안 보이는 도로에 연결되던 근본 원인. 파일 삭제(+.meta).
- ✅ `ProductionTestBootstrap.cs`도 삭제 — 잉여 방앗간 엔티티 + GameClock 없을 때 `TimeScale=1000` 생성(시간 오염) + 활성 `Debug.Log`. 게이트 `GameClock`뿐이라 자동 실행.
- ✅ `StampTestBootstrap.cs`는 이미 삭제돼 있었음(코드 0건) — CLAUDE.md 디렉토리 트리에서 세 항목 모두 정리.
- 참고: `RoadLayer` **쓰기는 `RoadSystem` 단 한 곳**이 정상. 테스트용으로 레이어를 직접 쓰는 부트스트랩은 자동 실행되지 않게 게이트하거나 확인 후 즉시 삭제할 것.

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

- ✅ **(2026-06-22) 확장 편향(expansion bias) 최우선 도입 + 평행 seam 거부 — 오목 끌개 / 단일 방향 쏠림 수정**
  - 증상(사용자 관찰): ① 오목 우선이 **하드 버킷 우선순위**라 도시 반대편 작은 노치가 평탄 프런티어 전진보다 항상 이김 → 거친 구역이 끌개가 되고(오목→오목 자기강화) "우연히 평평해진 곳"은 영영 밀려 **확장이 한쪽으로 쏠림**. ② 드물게 기존 도로와 공유 없이 한 칸 평행으로 깔리는 블록(seam).
  - 해결 ①: **확장 편향을 선택 최우선 키로.** 고정 `CityGrid.Anchor` 기준 축별 확장량(`extX/extZ`) 측정 → 덜 자란 축(`lagIsX`)을 최우선으로 키움. `Bal = min(밀린 축 reach, leadExt)`(따라잡으면 cap, 스파이크/오버슈트 방지). `|extX-extZ| ≤ BalanceDeadband`면 균형으로 보고 편향 끄고 품질로 densify(데드밴드로 좌우 thrash 방지). **이동 centroid 아닌 고정 Anchor**라 양의 피드백 회피.
  - **3버킷(오목/코너볼록/직선T) 하드 우선순위 폐지** → 유효 후보 전부 한 풀(`Cand`)에 모아 단일 비교자 `Better`로 랭크. 오목은 절대 우선이 아니라 **카테고리 2차 키**(오목2>코너볼록1>직선T0)로 강등 → 편향이 요구하면 평탄/볼록 프런티어도 발전(끌개 해소). 우선순위: **편향 → 카테고리 → share → 닿는 변 → 근접**. DevelopBlock 실패 시 차선 후보로 폴백(기존 다중버킷 폴백 의미 보존).
  - 해결 ②: **`IsParallelSeam`** — 블록 '새' 도로 변 바로 바깥에 같은 팀 도로가 평행하게 붙으면(공유 없이 한 칸 어긋남) 후보 거부. 변이 이미 공유면 '새 변' 아니라 제외(정상 삼거리/사거리 통과), 바깥이 빈 땅인 프런티어 확장도 통과(편향 교정과 양립). 코너 오판 방지로 내부 폭 K만 스캔. `RejectParallelSeam` 노브(기본 on).
  - `GrowthConfig`: `ConvexBias` 제거(끌개 해소로 불필요) → `BalanceDeadband`(기본 8), `RejectParallelSeam`(기본 true) 추가.
  - ⬜ **실측 튜닝 필요**: `BalanceDeadband`(반응성↔thrash), seam 거부 과도 시 "성장 자리 없음" 빈도 관찰(건물크기 폴백 + 그날 스킵 안전장치 있음).

---

### 🟢 현재 확정 동작 (2026-06-22 세이브) — AI 도시 성장 = 확장 편향 우선 + 모서리 앵커 가변 블록
> 위 긴 트레일은 디버깅 과정. **현재 코드의 동작은 이 블록만 읽으면 됨.** (`AiCityGrowthSystem.cs` 전면이 이 모델)

- **모델**: 매 게임일(`DayChanged`) AI팀마다 블록 1개 성장. 블록 = 건물을 담는 {4,6,8}셀 정사각형 + 도로 링(roadSize). 크기 균일 강제 없음(건물별 가변).
- **앵커 = 모든 프런티어 도로 셀**(빈 이웃 보유): 연결 형태(`RoadNeighborMask`)로 후보 종류 결정.
  - 끝점(1)·L자 꺾임(직각 2) → 볼록·오목 둘 다 가능.
  - 삼거리(3)·사거리(4)=`junction` → **오목만 가능, 볼록 불가**(통과축 있어 외부 안 꺾임).
  - 직선 통과(마주보는 2연결, `IsStraightThrough`) → **오목이면 OK(직선변 포켓 채움), 볼록은 T분기 폴백 전용**.
- ✅ **(2026-06-20) 직선 앵커 도입 + 건물 크기 폴백** — 두 증상 수정:
  - ① **오목 부족**: 직선 도로 중간에 생긴 포켓(notch)은 그 변에 직선 셀뿐이라 예전엔 앵커 0 → 못 메움. 이제 직선 셀도 앵커에 포함하되 **오목(닿는 변 ≥2)이면 발행** → 직선변 노치 채움.
  - ② **긴 직선+최악 지형 교착**: 굴곡 없는 직선은 앵커가 양 끝 2개뿐 → 끝이 막히면 정지. 이제 직선 셀 **T분기(볼록 1변)** 를 `bestSt` 버킷에 모아 **폴백 전용**으로 발행(오목·코너볼록 전부 실패 시만). 평소엔 안 쓰여 빗살 난립 없음 + 한 번 분기하면 새 코너 생겨 자기-제한.
  - ② **건물 크기 폴백**: 그날 뽑은 건물(`PickBuilding`)이 안 들어가면 다른 키(보통 더 작은 K)로 한 번 더 `GrowOneBlock` 시도 → 큰 블록만 막히는 빡빡한 지형 완화.
  - (2026-06-22 갱신) 세 버킷 하드 우선순위 폐지 → **단일 풀(`Cand`) + 단일 비교자**. 오목/코너볼록/직선T는 카테고리 키(2/1/0)로만 구분. 선택 최우선은 **확장 편향**(아래).
- **앵커에서 4사분면 블록**(`QuadrantOrigin`): 근접 링이 모서리 도로와 정확히 일치 → 어긋남 없는 삼거리/사거리. 공유변 길이는 달라도 됨(겹치는 만큼 공유).
- **오목/볼록 판정**(`SideMassMask`+`IsConcave`): 블록 4변이 도시(팀 도로/건물)에 닿는지 **내부 폭 K만** 스캔(코너 돌출 제외 → 오판 방지). 닿는 변 ≥2 = 오목(노치), ≤1 = 볼록.
- **선택 (2026-06-22, 편향 최우선)**: 단일 비교자 `Better` 우선순위 = ① **확장 편향**(고정 Anchor 기준 덜 자란 축을 키우는가, `Bal=min(reach,leadExt)`, `|extX-extZ|≤BalanceDeadband`면 off) → ② **카테고리**(오목2>코너볼록1>직선T0) → ③ **링 share**(`RingShareScore`, 기존 도로 재사용=정렬) → ④ 닿는 변 多 → ⑤ 베이스 근접. 균형 상태에선 편향 off → 오목/share 우선으로 densify. `ConvexBias` 제거됨(끌개 해소로 불필요).
  - **평행 seam 거부**(`IsParallelSeam`, `RejectParallelSeam` 기본 on): 공유 없이 한 칸 평행으로 깔리는 블록 후보 차단(정상 공유 삼거리/사거리·빈 땅 프런티어 확장은 통과).
  - ✅ **(2026-06-20) 평행 도로 사거리 떡칠 수정 — AI 링 도로에 축 부여** — seam(블록 격자가 1칸 어긋나 평행 도로가 생기는 것) 자체는 수용. 문제는 AI가 링 도로를 전부 `Axis=Any`로 발행해 평행 도로가 셀마다 자동 연결돼 사거리 폭발. → `CollectRingRoads`가 셀별 축 부여: 위/아래 행=EW, 좌/우 열=NS, 코너=Any. 평행 도로는 축이 안 맞아 시각적으로 연결 안 됨(`ComputeAxisFilteredMacroDirections`가 막음). 실제 공유변은 같은 셀이라 정상 연결. (베이스캠프 외곽 도로 축 부여와 동일 원리.) ※ BFS 보행 연결(`ComputeDirections`)은 축 무관이라 영향 없음 — 시각 문제만 해결.
  - ✅ **(2026-06-20) '한 칸 밀림' 근본 수정 — 앵커를 도로 footprint 원점으로** — `QuadrantOrigin`이 도로 **셀 하나(`kv.Key`)** 기준이라, roadSize≥2일 때 그 셀이 footprint 안에서 밀린 만큼(최대 roadSize-1칸) 어긋났음(roadSize=2 → 한 칸 밀림, 모서리/오목 무관). 이제 `kv.Value.FootprintOrigin` 기준 + footprint당 1회(`seenRoad` dedup) + 매크로 이웃 마스크(`RoadFootprintMask`)·프런티어(`HasEmptyNeighborFootprint`)도 footprint 단위로 판정. 셀 단위 `RoadNeighborMask`/`HasEmptyNeighbor`/`DirOff` 제거.
  - ✅ **(2026-06-20) 오목 확장 '밀림' 수정** — 선택 1순위를 링 share로. 예전엔 `닿는 변+거리`로만 골라 도로 링이 기존 도로 옆에 평행하게 어긋나는 자리가 자주 선택됨(밀림). 이제 기존 도로를 최대한 재사용(공유)하는 자리를 우선 → 삼거리/사거리로 깔끔히 맞물림. 세 버킷(Cc/Ex/St) 공통 적용.
- **유효성**(`BlockValid`): footprint(내부 K + 도로 링) 전체가 맵 안·같은 높이(단차 거부)·Land(물 거부)·내부 빈땅·링은 빈땅/기존 팀 도로. → **해변·단차·자원 위엔 안 깖**(자원은 `CellBuildable`이 차단).
- **랜덤 시드**: `CityGrid.Seed`(베이스 생성 시 `UnityEngine.Random`으로 세션마다 다름)를 RNG에 XOR → **새 게임마다 다른 패턴**, 한 게임 내 결정적.
- **시스템 순서**: `AiCityGrowth → RoadSystem → BuildingPlacement`(같은 프레임, 도로 먼저 깔린 뒤 건물 입구 검증).
- ⬜ **다음 단계 후보**: 진단 `Debug.Log` 제거 / `BlockOps.cs` 미사용 정리 / 블록 내부 여백(yard) 활용 / 수요(시민) 연동으로 건물 선택 대체 / 큰 블록 내부 alley 세분화.
- ⚠ **미사용 정리 대상**: `BlockOps.cs` 전체(grep 0 호출), `GrowthConfig`에 남은 옛 필드 없음 확인.

---

## 특수 목적 도로 연장 (Destination Road) — 재사용 라우터 (2026-06-23)
> 목적(항구·자원 등) 무관한 **재사용 메커니즘만** 구현. 목적/타겟 선정·물 위 건물은 다음 세션.

- ✅ **`RoadPathRequest`** (단발 이벤트, [RoadComponents.cs](Assets/_game/scripts/RunTime/Components/RoadComponents.cs)): `{Target, OwnerLocalId, FactionId, StopAdjacent}`. 항구/자원 등 목적 로직(미구현) 또는 테스트가 발행.
- ✅ **`BlockOps.FindRoadPath`** (순수 fact, 다중 소스 BFS): 모든 팀 도로 셀을 시작점으로 **같은 높이·Land(물 제외)·빈땅(환경물 치움)·자원/건물/도로 아님**으로만 4방향 확장 → Target(또는 인접) **셀 최단 경로**. 반환 경로 = `[소스 도로 셀, c1, …, goal]`. 단차/물은 자동 회피(그린 모델 연결성 보존 — 경로가 한 평지에 머묾).
- ✅ **`RoadPathSystem`** (`[UpdateBefore(RoadSystem)]`, `RequireForUpdate<RoadPathRequest>` → 요청 있을 때만 가동): 경로를 **그린-방향 `PlaceRoadCommand`**로 발행. **소스(기존 도로) 셀에도 그린 비트 발행 → RoadSystem이 OR 병합**(교차/T 승격)으로 기존 네트워크에 연결(상호 비트 성립). 전부 기존 도로면(이미 연결) 발행 생략.
- ✅ 유저 드래그·AI 링과 **동일 그린-방향 모델** → BFS·물류·비주얼 자동 호환. **현재 1×1 전용**(멀티셀 추후).
- ⬜ **다음 세션**: 목적 로직(항구=물가 접근지 선정+물 위 건물 / 자원=최근접 미연결 자원), 멀티셀(roadSize>1) 경로, 도메인 모델 1단계.
- 테스트: `RoadPathRequest` 엔티티 1개 생성으로 확인(디버그 키/스니펫).

---

## 도로/도시 파괴 (Raze & Orphan Prune) — 2026-06-23
> 적의 공격으로 도로 파괴 가능(AI는 의도적 철거 안 함). **플레이어·적·AI 동일 적용(공평).**

- ✅ **`RemoveRoadCommand.Forced`** — 1이면 소유자 일치 가드 우회(비소유 강제 철거). RoadSystem이 **실제 도로 소유자** 기준으로 footprint/시각/이웃/StampDirty 정리. 평소 철거(Forced=0)는 소유자만.
- ✅ **`RazeAreaCommand {Min,Max}`** — 영역 광역 파괴(소유 무관=공평). [`RazeSystem`](Assets/_game/scripts/RunTime/Systems/RazeSystem.cs)이 처리:
  - ① 사각형과 겹친 건물 파괴 — 엔티티 destroy + `OccupancyLayer`/`GridMap` 점유 해제(**땅 재사용 가능**).
  - ② orphan 도로 수집 → ③ 강제 `RemoveRoadCommand` 발행(RoadSystem이 실행).
- ✅ **`BlockOps.CollectOrphanRoads`** — **파괴 건물 인접 셀에서 시드 → 파면 leaf-prune**:
  - 시드: 파괴된 건물(`razedCells`)에 인접한 도로 중 **(다른) 라이브 건물에 안 닿는** 셀 = 그 건물만 쓰던 링. degree 무관 제거(닫힌 링을 끊음). 공유 변(라이브 이웃에 닿음)은 시드 제외 → 유지.
  - 파면 leaf-prune: 시드 절단면에서 전파, 라이브 미접촉 + 연결 ≤1 말단만. **★ 연결 ≥2(살아있는 통과/교차로) 보존**, 파괴 건물과 무관한 도로는 시드에 안 잡혀 안전.
  - 전제: AI **블록당 건물 1개** → 건물 죽으면 블록이 빔 → 프런티어 블록 링은 풀리고, 공유/통과 도로는 유지.
  - ✅ **(2026-06-23) 두 차례 수정**: ① rect 기반 Pass A가 통과 교차로까지 지움(과다 제거) → 폐기. ② 전역 leaf-prune은 링 도시에서 막단이 없어 **아무것도 안 지움** → **파괴 건물 인접 시드 방식**으로 교체(링을 끊어 풀어냄).
  - ⬜ 블록당 건물 여러 개가 되면 "마지막 건물 죽을 때만 링 해체"는 건물 카운트/블록 추적 필요(현재는 1건물=1블록 전제).
- ✅ **시스템 순서**: `RazeSystem [UpdateBefore RoadSystem]` — 같은 프레임에 도로 철거 실행.
- ✅ **(2026-06-23 수정) 철거 시 살아남은 경계 도로 모양 갱신** — 증상: 비워진 블록 경계의 T(3거리)가 가지(branch)를 향한 비트를 그대로 가져 **직선이 안 됨**(비주얼도 안 바뀜). 원인: `UpdateFootprintBoundaryDirections`가 그린(Explicit) 셀을 무조건 스킵(배치용 권위값 보존 규칙). 해결: `removing` 파라미터 추가 — **철거 시** 그린 이웃의 **제거된 footprint 쪽 비트만 제거**(T→직선) + `DirtyRoadTag`로 비주얼 교체. 배치는 기존대로 보존. 레거시 이웃은 `ComputeDirections` 재계산으로 자동.
- 테스트: [Test.cs](Assets/_game/scripts/Test.cs) — **마우스 좌클릭으로 가리킨 건물 1개 파괴**(클릭 셀 포함 footprint → RazeAreaCommand). 한 구역 건물을 하나씩 부수며 도로가 라이브 건물에 안 닿는 순간 풀려 사라지는지 점진 관찰. (이전 키 R bbox 1/4 raze 폐지.)
- ⬜ **다음**: 전투(건물 `CombatDeadTag` 사망)에서 **자동** 구역 해체 트리거(현재는 `RazeAreaCommand` 명시 발행). 멀티셀 도로(현재 cell 단위 판정, size>1은 footprint 통째 제거로 근사). CombatDeathSystem 사망 시 점유 정리(현재 RazeSystem 경로만 정리).

### 🟢 구역(Zone) 기반 도로 재구성 + 자동 재연결 — 2026-06-23 (degree-prune 폐기, 현행)
> 사용자 지적: orphan-prune이 **막힌 도로를 남발**(격자/링은 degree≥2라 빈 블록 링이 영원히 남음)
> + AI는 도로를 **재구성·복구할 수단이 없어** 한 번 공격당하면 도시가 깨진 채 방치 → 취약·불공평.
> **사람이 손으로 하는 정리+재연결을 AI가 자동화**하도록 전환. degree(토폴로지)가 아니라 **구역 소유/공유**로 판단.

- ✅ **구역 레지스트리** ([ZoneComponents.cs](Assets/_game/scripts/RunTime/Components/ZoneComponents.cs)): `CityZones` 싱글톤 = `Zones`(블록 원점 O→`ZoneRecord`) + `InteriorZone`(내부 셀→O) + `RingRef`(도로 링 셀→**살아있는 구역 수**). 키=블록 원점이라 넘버 카운터 불필요. [`CityZoneInitSystem`](Assets/_game/scripts/RunTime/Systems/CityZoneInitSystem.cs)이 Persistent 수명주기(GridInit 패턴).
- ✅ **등록(`ZoneOps.RegisterZone`)** — [`AiCityGrowthSystem.DevelopBlock`](Assets/_game/scripts/RunTime/Systems/AiCityGrowthSystem.cs)이 블록 조성 성공 시 호출: 내부 셀→O 매핑 + **링 셀 refcount +1**. 링 셀 집합(`EnumRingBegin`)은 `CollectRingRoadsDrawn`/`CollectRingRoads`와 동일 산출 → 누수 없음. 베이스 블록은 매 팀 1회 **영구(permanent) 구역**으로 등록 → 베이스 링 셀 +1 박아 인접 구역이 죽어도 베이스 링이 0으로 안 떨어짐(연결 루트도 됨).
- ✅ **해체(`ZoneOps.AttributeDeath`/`ReleaseZone`)** — [`RazeSystem`](Assets/_game/scripts/RunTime/Systems/RazeSystem.cs): 파괴 건물을 `InteriorZone`로 구역에 귀속(`BuildingCount −1`). 0이 되면 그 구역 링 셀 **refcount −1 → 0이 된 셀(공유 안 됨)만** 강제 `RemoveRoadCommand`. **공유 변·통과 도로는 refcount≥1로 보존**. permanent(베이스)는 절대 해체 안 함. → 격자/링 도시에서도 **빈 블록 링이 정확히 풀림**(degree-prune이 못 하던 것). `CollectOrphanRoads`(+`AdjacentToLive`/`EnqueueRoadNeighbors`) 폐기.
- ✅ **자동 재연결** ([`NetworkRepairSystem`](Assets/_game/scripts/RunTime/Systems/NetworkRepairSystem.cs), `[UpdateAfter RoadSystem]`): 도로 제거가 일어난 팀에 `RazeSystem`이 `NetworkRepairRequest` 발행 → ① 베이스 블록 근처 팀 도로 시드로 BFS=`baseSet`(베이스 연결) ② 나머지 팀 도로를 4-인접 컴포넌트=**단절 섬** ③ 섬마다 [`BlockOps.FindReconnectPath`](Assets/_game/scripts/RunTime/Systems/BlockOps.cs)(baseSet→섬 다중소스 BFS, 빈 평지만)로 다리 경로 → **그린-방향 `PlaceRoadCommand`**(양 끝 OR 병합 = 베이스·섬에 교차로로 붙음). 물/단차/적영토로 막히면 둠(=사람도 못 잇는 상황, 공평). → **AI가 전투 피해를 스스로 치유**.
- ✅ **플레이어 도로는 구역 미등록 = 자동 정리·재연결 안 함**(수동 관리, "사람은 손으로" 설계 의도와 일관). AI/베이스만 자동.
- ✅ **시스템 순서**: `AiCityGrowth → RazeSystem → RoadSystem → NetworkRepair`. RazeSystem이 구역 해체+제거 발행 → RoadSystem이 제거 실행 → NetworkRepair가 **제거 반영된** RoadLayer로 단절 판정·다리 발행(다음 프레임 RoadSystem이 깖, 1프레임 지연 허용).
- ⬜ **다음/한계**: ① 멀티셀 도로(roadSize>1) 다리는 1×1 그린 전용 — `FindReconnectPath`/링 refcount가 1×1 가정. ② **블록당 건물 여러 개**가 되면 `RegisterZone`이 건물 수만큼 `BuildingCount` 증가시키도록 확장 필요(현재 1건물=1블록 전제, =1). ③ 건물 배치가 사후 실패하면(드묾, DevelopBlock 검증과 BuildingPlacement 정합) **유령 구역**(건물 없는 등록) 가능 — 도로가 안 지워질 뿐 오제거는 없음(보수적). ④ 베이스 링 셀 집합은 `EnumRingBegin(baseO,Block,Road)`가 `FactionBaseSpawnSystem` 외곽 링과 일치한다는 전제(CityGrid 규약상 일치).
- 테스트: [Test.cs](Assets/_game/scripts/Test.cs) 좌클릭으로 한 구역을 **가운데부터** 부수기 → 빈 블록 링이 공유 변만 남기고 풀리는지 / 그 너머 구역이 단절되면 콘솔 `[NetRepair]`와 함께 새 다리 도로가 깔리는지 확인. 콘솔 `[Raze] … 구역 N 해체, 도로 M 셀 철거`.

---

## 건설 클레임(영역) 게이트 + 도로 연속성 — 2026-06-24
> 동기: 건설에 **공간적 소유권이 없어** ① 적 빈 땅에 도로 도배/건물 plop(카펫 그리핑) ② 도로를
> 지형 끝-끝 이어 적을 **완전 봉쇄**(확장 불능) — 둘 다 가능했음. 셋(벽·카펫·plop)이 한 뿌리.
> **결정(영구)**: 영토/영향권 기반으로 가되, 전체 영토 시스템 없이 **라이트 클레임 게이트**로 시작.
> (현행: 도로는 유닛 이동을 막지 않음 = `BuildBlockedGrid`가 장애물 footprint만 봄. 막는 건 '건설'뿐.)

- ✅ **클레임 정의 = "내 건물 + 마진 M칸"**. 규칙: **다른 플레이어 클레임 안엔 못 짓는다**
  = 셀이 적 건물에서 M칸(체비셰프) 이내면 거부. [ClaimOps](Assets/_game/scripts/RunTime/Systems/ClaimOps.cs)
  (`InEnemyClaim` 셀별 / `RegionHasEnemy` 박스 1패스). **중립(owner<0)·환경물·도로·자기 건물은 클레임 아님.**
  M 기본 3(`ClaimOps.DefaultMargin`, 튜닝용). `TerritoryLayer` 캐시 없이 OccupancyLayer 직접 스캔(비핫패스).
  - ✅ **(2026-06-24) 도로 제외 — claim 소스 = 건물만 (영구 결정)**. 초기엔 도로도 claim에 넣었으나,
    도로는 선형이라 적이 **내 확장 방향에 도로 한 줄만 깔아도 M칸 '띠'가 봉쇄 구역**이 됐다(작은 맵 치명적
    — 봉쇄를 막으려던 게 더 강한 봉쇄 도구가 됨). 도로는 영역이 아닌 인프라 → **건물만 영역을 정의.**
    적 도로 옆/근처엔 자유 건설(도로 위에만 못 올림 = RoadSystem impassability 별개). 개발된 구역(건물 보유)은
    여전히 carpet/plop 차단, 빈 땅만 contested.
- ✅ **게이트 적용(휴먼·AI 공통)**: 건물 [ValidateCells](Assets/_game/scripts/RunTime/Systems/BuildingPlacement.cs)
  (`ClaimedByOther` 신규) + 도로 [RoadSystem 배치](Assets/_game/scripts/RunTime/Systems/RoadSystem.cs) +
  AI 성장 [BlockValid](Assets/_game/scripts/RunTime/Systems/AiCityGrowthSystem.cs)(박스 1패스 = 후보 조기 탈락) +
  프리뷰 [SegmentBlockStatus](Assets/_game/scripts/RunTime/Systems/RoadBuildController.cs)(`PreviewStatus.ClaimBlocked` 신규, 진보라).
- ✅ **효과**: 적 땅 도배·base plop 차단. 봉쇄 벽도 적 영역 M칸 밖에만 → 갇혀도 내 M-링으로 숨통 +
  전투 raze(소유 무관 강제 철거)로 탈출. 자원 도로/재연결 다리도 적 클레임은 못 통과(공평).
- ✅ **도로 연속성(contiguity)** — 유저 드래그는 **내 기존 도로망에서 시작(겹쳐)해야** 발행.
  [RoadBuildController.Confirm](Assets/_game/scripts/RunTime/Systems/RoadBuildController.cs)의
  `FilterConnectedToNetwork`: 기존 내 도로와 겹치는 pending 셀을 시드로 4-인접 flood → 닿은 세그먼트만
  건설, 떠다니는 도로는 제거(+경고 로그). 그린 모델(겹쳐야 연결)과 일치. 카펫 도배 한 겹 더 차단 +
  떠다니는 도로 0 → orphan/구역 정리도 깔끔. **AI/라우터/재연결은 본래 기존망에서 출발하므로 무영향.**
- ✅ **(2026-06-24) 연속성 프리뷰 반영** — `ComputeAttached`(Confirm·프리뷰 공용)로 pending 세그먼트+현재
  드래그를 합쳐 연결 셀 집합 산출. 미연결 셀은 `PreviewStatus.Disconnected`(회색)로 실시간 표시 →
  떠다니는 드래그가 그릴 때부터 회색, Confirm 때 빠지는 것과 시각 일치. 단일 호버는 미적용(아직 도로 아님).
- ⬜ **다음/한계**: ② 클레임은 OccupancyLayer 스캔이라 큰 맵·잦은 배치 시 `TerritoryLayer`에 캐시 고려.
  ③ 베이스 스폰이 클레임 게이트를 타면 스타트포인트가 M칸 내로 붙은 **퇴화 맵**에서 base 충돌 가능
  (정상 맵은 무관). ④ M=3은 실측 튜닝 대상. ⑤ 프리뷰 `ComputeAttached`가 매 프레임 HashSet/Queue 할당
  (빌드 모드 한정·소량이라 무시 가능, 잦으면 컨테이너 재사용).
- ❓ **교차(crossing) 허용**(남의 도로 가로지르기)은 보류 — 클레임 게이트로 벽/카펫이 풀려 당장 불필요.
  영토를 본격 도입하거나 경계에서 도로가 만나는 케이스가 문제되면 그때 도입.

---

## 도로 관리시설 (Road Maintenance) — 설계 확정, 구현 대기 (2026-06-24)
> **동기**: 도로 악용 — 시작하자마자 적 근처까지 도로를 깔아 상대 도시확장을 봉쇄. 어제(2026-06-23~24)
> 클레임 게이트 + raze로 접근했으나 **접근 방식 전환**: 도로는 "관리시설의 도달 범위" 안에서만 유지된다.
> 범위 밖 도로는 시간 경과로 파괴 → 적 근처로 도로를 끌려면 관리소를 보급선처럼 연쇄 배치해야 함
> (공짜 그리핑 → 방어해야 하는 전략적 약속). 관리소는 전투로 파괴 가능.

- ✅ **핵심 = 기존 `Stamp BFS` 인프라 재사용** ([StampComponents.cs](Assets/_game/scripts/RunTime/Components/StampComponents.cs)).
  `StampKind`에 `RoadMaintenance` 한 종류 추가 → "관리시설이 도로망 BFS로 닿는 범위"가 곧 coverage.
  플레이어(LocalId)별 독립 슬롯·dirty 재빌드·`HourChanged` 게이팅 전부 재사용. **미관리 도로 = 어떤
  RoadMaintenance 도장도 안 찍힌 도로셀.**
- ✅ **coverage 모델 = 영역(도로망 BFS)** (확정, 2026-06-24 정정 — 이전 '하이브리드'에서 단순화):
  관리시설 입구 도로셀에서 **도로망을 따라 `MaxDist`칸 이내** 도로셀이 covered. 용량(N) 개념 폐기 —
  순수 거리 기반. 테마상 '순찰 반경', BFS 한 번으로 결정적. (Stamp BFS와 동일 다중소스 확산 구조.)
- ✅ **파괴 = 유예 decay** (확정): `RoadCell`에 미관리 누적 시간 카운터 → K일 지속 시
  `RemoveRoadCommand{Forced=1}` 발행(기존 RoadSystem 경로). covered 복귀 시 카운터 리셋. 살릴 여지 +
  "금 간 도로" 경고 telegraph 가능. 즉시 파괴 대비 체감 부드러움.
- ✅ **클레임 게이트 완전 제거** (확정, 2026-06-24 — 이전 '보류' 해제): 어제 만든 건설 클레임 게이트
  (위 2026-06-24 섹션)는 전부 은퇴 — `ClaimOps` + 모든 호출처(건물 `ValidateCells`의 `ClaimedByOther`,
  `RoadSystem` 배치, AI `BlockValid` 박스 1패스, 프리뷰 `ClaimBlocked`). 도로 봉쇄·grief는 maintenance
  decay가 대체. ⚠ **부작용 명시**: 클레임이 막던 **건물 carpet/plop 보호도 함께 사라짐** — 필요하면
  추후 별도 메커니즘으로(maintenance와 무관). zone-raze / 전투 복구(`NetworkRepair`)는 별개라 유지.
- ✅ **베이스 링 도로 = 영구(decay 예외)** (확정, 2026-06-24): 관리소를 잃어도 베이스 외곽 링은
  decay 안 함(zone permanent와 동일 취지) → 베이스 brick 방지. 확장 도로만 관리 대상. Phase 3에서 반영.
- 🔧 **구현 골격** (Phase 0부터 단계 진행 — 사용자 단계확인 방식):
  0. ✅ **클레임 게이트 제거 완료** (2026-06-24) — `ClaimOps.cs`(+.meta) 삭제 + 호출처 6곳 정리:
     `RoadBuildController.SegmentBlockStatus`, `RoadSystem` 배치, `BuildingPlacement.ValidateCells`
     (+`PlacementFailCode.ClaimedByOther` 제거), `AiCityGrowthSystem.BlockValid`(박스스캔 제거),
     `RoadBuildPreview`(`PreviewStatus.ClaimBlocked` 제거 → `Disconnected` 9→8 재번호, IsBlocking/ToText/색).
     grep 잔여 0건. ⚠ 컴파일 검증은 Unity 에디터에서(이 환경엔 컴파일러 없음).
     ⚠ **부작용 발효**: 건물 carpet/plop 보호 사라짐(설계대로). 봉쇄 방지는 Phase 1~4 maintenance가 대체 예정.
  1. ✅ **관리시설 컴포넌트 + authoring 체인 완료** (2026-06-24) — `IsSupplier` 경로 미러링:
     `RoadMaintenanceDepot{OwnerLocalId, MaxDist}` 컴포넌트(`StampRebuildSystem.cs`, `StampSupplier` 옆) +
     `RegistryItem.IsRoadMaintenance/MaintenanceMaxDist` 플래그 → `BakedPrefabEntry` → Baker →
     `PrefabMeta` → `PrefabLookupBuildSystem` → `SpawnRequest` → `BuildingPlacement.EmitSingle` →
     `SpawnSystem`이 `IsRoadMaintenance`면 depot 태그 부착. ⬜ Unity에서 관리시설 프리팹 SO 항목에
     `IsRoadMaintenance=true`, `MaintenanceMaxDist=N` 설정 필요(에디터 수작업).
  2. ✅ **`StampKind.RoadMaintenance` + 런타임 coverage stamp 완료** (2026-06-25):
     `StampKind.RoadMaintenance` 추가([StampComponents.cs](Assets/_game/scripts/RunTime/Components/StampComponents.cs)).
     [`StampRebuildSystem`](Assets/_game/scripts/RunTime/Systems/StampRebuildSystem.cs)에 공급자/창고 옆
     **③-c depot 도장 루프** 추가 — `RoadMaintenanceDepot`+`BuildingFootprint`+`BuildingEntrance` 소유자별로
     입구 도로셀에서 도로망 BFS(`MaxDist`) → `StampLayers` 슬롯에 `Kind=RoadMaintenance` 도장. dirty/
     라운드로빈/`HourChanged` 게이팅·road 변경 dirty(RoadSystem)·depot 배치 dirty(BuildingPlacement) 전부
     기존 인프라 그대로 재사용. **`StampOne`의 인라인 BFS를 공용 `RoadCoverageOps.Flood`로 교체** →
     공급자/창고/관리시설 + 배치 프리뷰가 **단일 BFS fact 공유**(프리뷰 ≡ 런타임 보장, `IsOwnedRoad` 제거).
  3. ✅ **decay 시스템 완료** (2026-06-25) — [RoadDecaySystem.cs](Assets/_game/scripts/RunTime/Systems/RoadDecaySystem.cs):
     `RoadDecayState` 싱글톤(`Unmaintained: NativeHashMap<int2,int>` + `GraceDays`(기본 3))을 `RoadDecayInitSystem`이
     Persistent 수명주기(GridInit 패턴)로 관리. **카운터는 RoadCell이 아니라 별도 맵** — RoadSystem/RoadCell 무수정
     (결합 0, 셀 사라지면 orphan 정리 패스로 비움). `RoadDecaySystem`(`[UpdateBefore RoadSystem]`, `DayChanged` 게이트):
     ① 면제 셀 = `RoadDecayState.Exempt`(베이스 외곽 링) →
     ② 모든 도로셀 순회 — 소유없음/면제/covered(owner 슬롯 stamp에 `RoadMaintenance` 도장)면 카운터 리셋, 아니면 +1,
     `GraceDays` 도달 시 `RemoveRoadCommand{Forced=1}` 발행 → ③ orphan 카운터 정리. `GraceDays<=0`이면 decay off.
     - ✅ **(2026-06-25 수정) 베이스 면제를 모든 팀으로** — 처음엔 `CityZones.Permanent`(영구 구역) 링으로 면제했으나
       영구 구역 등록이 `AiCityGrowthSystem`(**AI 전용**)에서만 일어나 **휴먼 베이스가 면제에서 빠져 decay로 날아감**.
       → `RoadDecayState.Exempt`(`NativeHashSet<int2>`) 신규 + `FactionBaseSpawnSystem.EmitPerimeterRoads`가 베이스 링
       footprint 셀을 **모든 팀(휴먼·AI) 공통으로** 등록. zone 의존 제거(`ZoneOps.CollectRingCells` 도로 미사용→삭제).
     - ✅ **(2026-06-26 수정) 건물 파괴 시 stamp 무효화** — 증상: depot 건물을 파괴해도 그 도로가 decay 안 됨.
       원인: `RazeSystem`이 건물을 destroy하면서 **`StampDirtyEvent`를 안 쏨**(도로 제거만 RoadSystem 경유로 dirty). depot만
       죽고 도로는 남으면 stamp 재빌드가 안 돼 죽은 depot의 coverage 도장이 영구 잔존 → 영원히 '관리됨'. 해결:
       `RazeSystem`이 파괴 건물의 `OwnerLocalId`를 모아 `StampDirtyEvent` 발행 → 다음 재빌드에서 도장 제거 → 미관리 도로 decay.
       (⬜ 전투 사망 `CombatDeathSystem`도 건물 파괴 시 동일 dirty 필요 — 그 경로 구현 시 반영.)
     - ✅ **(2026-06-26) 전역 day 일괄 → 셀별 연속 타이머 + telegraph** — 사용자 요구: 건설 중 갱신주기가 겹치면
       도로가 *일괄* 파괴되는 게 거슬림. 신규 도로·depot 파괴 도로 모두 **미관리가 된 시각부터 셀별 타이머**를 타게.
       해결: `Unmaintained`를 `<int2,int>(누적 일수)` → `<int2,double>(미관리 시작 게임시각 TotalSeconds)`로 변경.
       `RoadDecaySystem` 게이트 `DayChanged`→`HourChanged`(점검 주기), 만료 = `(now − since) ≥ GraceDays×SecondsPerDay`
       (연속 시각). 셀마다 제 시점에 만료 → **자연 분산**(전역 일괄 제거). [RoadDecayTelegraphSystem.cs](Assets/_game/scripts/RunTime/Systems/RoadDecayTelegraphSystem.cs)
       신규(항상 ON, GL): 미관리 도로를 **진행도 노랑→빨강 + 임박 시 펄스**로 칠해 "곧 부서짐" 예고(압박감). depot 깔면
       covered 복귀로 즉시 꺼짐. ⬜ 프로토타입 오버레이 — 추후 도로 균열 셰이더/디칼/VFX로 대체 가능.
     ⚠ Phase 4(베이스 관리소) 전엔 **베이스 외곽 링만 자동 면제** — 그 밖의 도로는 depot coverage 없으면 K일 후 철거됨
     (테스트 시 depot로 덮은 도로만 생존). 시스템 순서 `RoadDecay → RoadSystem`(같은 프레임 철거 실행).
  4. ✅ **베이스 자동 관리시설 완료** (2026-06-26) — [FactionBaseSpawnSystem.cs](Assets/_game/scripts/RunTime/Systems/FactionBaseSpawnSystem.cs):
     팀별 베이스 빌딩 발행 시 **첫 '입구 있는' 건물 하나를 관리시설로 지정**(`MaintenanceMaxDistOverride`로 태그+범위
     부여, `EntranceLookup.Has`로 입구 보유 확인) → 그 입구 도로셀에서 coverage가 퍼져 **베이스 링+인근 확장 도로
     decay 방지**. 거리=`GrowthConfig.MaintenanceMaxDist`(공용 튜닝, 기본 6). **0이면 비활성**(전용 depot 프리팹/
     베이스 config 사용 시). 별도 배치 없이 기존 베이스 빌딩의 검증된 위치·회전 재사용(점유/회전 문제 회피).
     ⚠ override 기반 = 테스트 경로 — 전용 depot 프리팹(IsRoadMaintenance) 준비되면 `MaintenanceMaxDist=0`으로 전환.
  5. ✅ **AI 관리소(커버리지 기반=방식2) 완료** (2026-06-26) — [AiCityGrowthSystem.cs](Assets/_game/scripts/RunTime/Systems/AiCityGrowthSystem.cs):
     `DevelopBlock`이 블록 발행 시 **그 블록이 기존 depot coverage 밖이면 블록 건물을 관리시설로**(override) 만든다.
     판정: 블록 링 중 **연결되는(기존 팀 도로인) 셀**의 `RoadMaintenance` 도장 거리 `d`(`MinMaintenanceDist`, `ownerStamp`)
     를 보고 `d + K + Road ≤ maintMaxDist`면 이미 도달=depot 불필요, 아니면 depot 발행 → **프런티어가 coverage 밖으로
     나갈 때만 depot 추가(보급선식)**. depot이 ~MaxDist마다 연쇄. 입구 있는 건물만(BFS 시작점 필요).
     `StampLayers`(팀 슬롯)·`GrowthConfig.MaintenanceMaxDist`(공용, 기본 6)를 `OnUpdate→GrowOneBlock→DevelopBlock` 체인으로 전달.
     "블록당 1개"(방식1)는 MaxDist>블록 시 과잉이라 폐기. ⚠ override 기반(테스트) — 전용 depot 프리팹(`MaintenanceDepotKey`)
     준비되면 블록 건물 대신 전용 depot 발행으로 전환(현재는 성장 건물이 depot 겸함).
- ❓ **튜닝**: `MaxDist`(순찰 반경), decay `K` — 골격 후 실측.

### 도로탭 배치 + 배치-시점 커버리지 프리뷰 (2026-06-25)
> **사용자 결정**: 관리소는 일반 건물이 아니라 **도로 인프라** → 건물탭이 아니라 **도로탭에서 배치**.
> 그리고 배치할 때 **그 관리소가 커버할 도로(도로망 BFS 도달 범위)를 실시간으로 보여준다**
> (보급선처럼 관리소를 이어 깔아야 하는 전략을 눈으로 계획).

- ✅ **공용 커버리지 BFS fact** — [`RoadCoverageOps.Flood`](Assets/_game/scripts/RunTime/Systems/RoadCoverageOps.cs):
  시작 도로셀에서 같은 소유자 도로망을 `MaxDist`칸 BFS(셀 `Directions` 비트 + 이웃 반대 비트 양방향 =
  `StampRebuildSystem.StampOne`과 **동일 규칙**). `covered`(셀→거리) 반환. **프리뷰와 Phase 2 런타임 stamp가
  같은 fact 공유 → 항상 일치.** (Phase 2는 이 결과를 `StampKind.RoadMaintenance` 도장으로 변환만 하면 됨.)
- ✅ **`PreviewStatus.Coverage`(청색, 비차단)** 추가 — [RoadBuildPreview.cs](Assets/_game/scripts/RunTime/Systems/RoadBuildPreview.cs)
  (색/`ToText`/`IsBlocking=false`). 기존 `RoadBuildPreviewRenderSystem` GL 마커 그대로 재사용.
- ✅ **`DepotPlaceController`** (신규, [DepotPlaceController.cs](Assets/_game/scripts/RunTime/Systems/DepotPlaceController.cs)):
  도로탭 안 관리소 배치 모드. 호버 → meta/입구 조회 → **입구가 내 도로 향하도록 회전 자동선택** →
  footprint 검증(`ValidateCells`와 동치: 범위/점유/자원/단차/지형타입) → 입구 도로셀에서
  `RoadCoverageOps.Flood` → `PreviewCell` 버퍼에 footprint 마커 + 청색 커버리지. 좌클릭 시
  `PlaceBuildingRequest{관리소 MainKey, RequireRoadAccess=false}` 발행(Phase 1 배선 그대로 동작).
  `RoadBuildController`와 **같은 프리뷰 싱글톤 공유**(동시 하나만 활성).
- ✅ **(2026-06-25) 배치 중 기존 관리소·연결성 오버레이** — 배치 모드에서 내 **모든 기존 관리소 위치
  (금색 마커)** + **그들의 coverage union(초록=현재 관리되는 도로 연결성)** 을 같이 그림. 위에 새 depot의
  footprint + 도달 범위(청색)를 얹어 **빈틈/중복/연쇄 배치를 눈으로 계획**. 기존 관리소는 매 프레임
  `RoadMaintenanceDepot` 쿼리 → 각자 `RoadCoverageOps.Flood`로 재계산(항상 최신, stamp 타이밍 무관).
  렌더 순서 = 기존coverage→새footprint→새coverage→기존관리소위치(최상단). 상태 추가:
  `PreviewStatus.CoverageExisting`(초록)·`DepotExisting`(금색). 테스트 override `MaintenanceMaxDistOverride`로
  프리팹 미설정 상태에서도 유한 반경 확인(프리뷰·배치 동일 값, `PlaceBuildingRequest`로 얇게 전달).
- ✅ **(2026-06-26) 영구 면제(베이스 링) 도로도 coverage 초록으로** — depot coverage가 아니라 `RoadDecayState.Exempt`
  로 보호되는 베이스 외곽 링도 **"decay 안 되는 도로"라 같은 초록**으로 합쳐 표시(혼란 방지, 사용자 결정).
  `GatherExistingDepots`가 `Exempt`를 읽어 내 소유 도로만 `_existingCovered`에 추가(별도 색/enum 없음).
- ✅ **GameHUD 도로탭에 "관리소 배치" 토글** — [GameHUD.cs](Assets/_game/scripts/RunTime/UI/GameHUD.cs):
  도로 건설과 **상호배타**(한쪽 진입 시 다른쪽 해제), Escape 해제, 호버 라벨에 활성 도구 상태 표시.
- ✅ **설계 원칙 유지**: 입구가 도로에 안 닿아도 **차단 않고** 빈 커버리지+경고만(인간 배치는 정보만 제공).
  단, 커버리지가 의미 있도록 입구-도로 향 회전을 자동 선택.
- ⬜ **Unity 수작업**: ① 관리소 프리팹 `RegistryItem`에 `IsRoadMaintenance=true`/`MaintenanceMaxDist=N`
  + **입구(Entrances[]) 정의**(BFS 시작점 — 없으면 커버리지/Phase 2 BFS 모두 스킵). ② 도로 패널에
  "관리소 배치" 버튼 + 라벨 배치. ③ `GameHUD._depotController`/`_btnDepotToggle`/`_lblDepotToggle` 와이어링 +
  `DepotPlaceController.DepotMainKey`(=관리소 MainKey) 지정. ④ 컴파일 검증은 에디터에서(이 환경엔 컴파일러 없음).
- ❓ `DepotPlaceController`의 ECS접근/팩션해소/레이캐스트는 `RoadBuildController` 복사 — 추후 공용 베이스 추출 가능.

### 🟢 Road Maintenance Phase 0~5 완료 — ⚠ **(2026-06-26) 폐기 결정: Territory로 대체** (맨 아래 "방향 전환" 섹션 참조)
> ⚠ 이 maintenance 시스템 **전체 폐기 예정**. 복구는 git `road-maintenance-complete` 브랜치/태그(`ee368ae`).
> 아래 내용은 *무엇을 만들었었나*의 기록일 뿐 — **현행 방향은 PROGRESS 끝의 "🔴 방향 전환" 섹션**.
> (참고) 그동안의 동작: **런타임 도로 유지 루프 end-to-end**(배치→도장→decay→베이스 면제→AI 자가유지).

- **완료**: Phase 0(클레임 게이트 제거) · Phase 1(depot 컴포넌트+authoring) · Phase 2(`StampKind.RoadMaintenance`
  런타임 도장, BFS는 공용 `RoadCoverageOps.Flood`) · Phase 3(`RoadDecaySystem` 미관리 decay + 베이스 링 면제) ·
  Phase 4(`FactionBaseSpawnSystem` 베이스 자동 depot=첫 입구 건물) ·
  Phase 5(`AiCityGrowthSystem` 커버리지 기반 AI depot=프런티어가 coverage 밖이면 블록 건물을 depot으로) ·
  도로탭 배치 UI(`DepotPlaceController`) + 배치-시점 커버리지 프리뷰 + **기존 관리소·연결성 오버레이**.
- **확정 동작 흐름**: 도로탭 "관리소 배치" → 호버 시 footprint+청색 coverage(+기존 관리소 금색/기존 범위 초록) →
  좌클릭 `PlaceBuildingRequest` → `SpawnSystem`이 `RoadMaintenanceDepot` 부착 → `StampRebuildSystem`이
  도로망에 `RoadMaintenance` 도장 → `RoadDecaySystem`이 미관리(도장 없음) 도로를 `GraceDays`(3) 후 강제 철거.
  **베이스 외곽 링은 면제**(`FactionBaseSpawnSystem`이 모든 팀 등록 — zone 의존 폐기, 휴먼 베이스 날아가던 버그 수정) +
  **베이스 첫 입구 건물이 자동 관리시설**이라 베이스 인근 확장 도로도 초기 coverage.
- 🔧 **디버그 비주얼라이저** — [RoadMaintenanceDebugSystem.cs](Assets/_game/scripts/RunTime/Systems/RoadMaintenanceDebugSystem.cs)
  (에디터/개발빌드 전용, **F6 토글**, 기본 OFF): `StampLayers`의 `RoadMaintenance` 도장을 **소유자별 색**으로 GL 렌더 →
  런타임 coverage가 실제로 찍히는지 확인(Phase 5 검증). **플레이어 본인 제외(AI/적 전용)** — 본인 coverage는 배치
  오버레이가 보여주고 겹치면 혼란이라. 팔레트는 배치 오버레이 색(초록/금색/청색)과 안 겹치게 선택. `RoadBuildPreviewRenderSystem` GL 패턴.
- ⚠ **테스트 스캐폴딩 — 프리팹 정리 후 제거할 것**:
  - `DepotPlaceController.MaintenanceMaxDistOverride`(기본 6) + `PlaceBuildingRequest.MaintenanceMaxDistOverride`
    + `BuildingPlacement.EmitSingle`의 override 분기. **`>0`이면 배치를 강제로 관리시설화**(IsRoadMaintenance=true +
    MaxDist=override) → 프리팹 미설정 상태에서 풀 루프 테스트용. 프리팹에 `IsRoadMaintenance`/`MaintenanceMaxDist`를
    설정·베이크한 뒤엔 override를 **0으로 되돌리면** 프리팹 메타 그대로 사용.
- ⬜ **다음 단계**:
  - **테스트 스캐폴딩 → 프로덕션 전환**: 전용 관리소 프리팹(`IsRoadMaintenance=true` + 입구 + MainKey)을 만들고
    `GrowthConfig.MaintenanceDepotKey` 지정 → AI는 블록 건물 override 대신 전용 depot 발행, 베이스/유저도 전용 depot 사용,
    `MaintenanceMaxDistOverride`/`GrowthConfig.MaintenanceMaxDist` override 경로 0으로 정리.
  - **성장 제한(선택)**: 현재는 "coverage 밖이면 depot 추가"로 자가유지. 더 보수적으로 하려면 성장 후보를
    coverage(또는 +depot 1개 사거리) 안으로 명시 제한 추가 가능(현재 미적용 — 자가보정으로 충분).
  - **Unity 수작업**: 관리소 프리팹 `RegistryItem`에 `IsRoadMaintenance=true`/`MaintenanceMaxDist=N` + 입구 정의
    (윈도우에 "Road Maintenance" 필드 추가됨, 베이크 필요) / 도로 패널에 "관리소 배치" 버튼·라벨 + GameHUD 와이어링.
  - **튜닝(실측)**: `MaintenanceMaxDist`(순찰 반경), `RoadDecayState.GraceDays`(유예 일수).
  - **폴리시/한계**: 멀티셀 도로(roadSize>1) 미대응 / Supply 필드(`IsSupplier`/`Relief`/`SupplyMaxDist`)는 등록 윈도우에
    아직 없음(필요 시 추가) / telegraph는 프로토타입 GL 오버레이(추후 균열 셰이더/VFX) · 현재 모든 팀 표시(owner 필터 가능).

### ❓ 오픈 설계 결정 (2026-06-26, 사용자 숙고 중 — 다음 세션 이어받기)
> 도로 제거 정책 + AI 회복력. 두 항목은 한 묶음(은퇴할 자리에 새 회복 메커니즘이 들어감).

1. **구역 prune/NetworkRepair 은퇴 → decay 단일화** (방향 합의됨, 미구현)
   - "건물 파괴 시 공유 안 되는 도로 즉시 제거"(`RazeSystem`의 zone 로직 + `ZoneOps.ReleaseZone` + `CityZones` +
     `NetworkRepairSystem` + `CityZoneInitSystem` + `AiCityGrowthSystem.RegisterZone`)를 폐기, 도로 제거를 **decay 단일화**.
   - 근거: decay의 coverage-보존이 prune의 sharing-보존을 근사(공유 도로=관리됨=유지 / 전용 도로=coverage 잃음=decay).
     즉시→서서히(telegraph), 분위기↑·복잡도↓. `RazeSystem`은 건물 파괴 + StampDirty만 남김.
   - 잔여 고려: ① **유령 링**(죽은 구역 링이 이웃 depot에 덮여 남음) — 거슬리면 "인접 라이브 건물 없으면 decay 가속" 규칙 추가.
     ② NetworkRepair(연결 회복) 상실 → 아래 2번(coverage 회복)이 그 역할 대체.
2. **AI coverage 수복** (depot-스나이핑 대응, 검토 중)
   - 문제: 적이 **depot만 의도적으로 파괴** → 넓은 coverage 상실 → 건물 대량 고아. **현재 AI는 회복 수단 없음**(취약점).
     Phase 5는 *새 블록 성장 시에만* depot을 달아 기존 도시 수복은 못 함.
   - 제안: AI 하루 점검 → 미관리(uncovered) 내 도로 감지 → 그 구역의 **입구가 살아있는 도로를 향하는 기존 건물 하나를
     depot으로 승격**(`RoadMaintenanceDepot` 부착, 새 배치·도로재건 불필요) → coverage 복원. `GraceDays` 안에 반응하면 무손실.
   - 밸런스: 하루 1회·행동 소모(즉시·무료 아님) → 스나이핑은 AI를 압박하는 유효 전술로 남되 한 방 붕괴는 불가.
   - 단계: (1) **decay 전 수복**=핵심 방어 / (2) 도로 이미 삭은 뒤 재건=어려움, Phase 5 재성장이 일부 흡수(추후).
   - 권장 순서: **1(은퇴) → 2(수복)**.

---

## 🔴 방향 전환 — 도로 maintenance 폐기 → 영역/영향력(Territory & Influence) (2026-06-26)
> ✅ **(2026-06-27) 실행 완료** — 제거 + Territory v1 둘 다. 상세·다음단계는 **PROGRESS 최상단
>   "🟢 도로 maintenance 폐기 + Territory/Influence v1" 섹션** 참조. 아래는 그 설계 원문.
> **결정**: 위 Road Maintenance(Phase 0~5) **전체 폐기**. "누가 무엇을 지키고/파괴되나"를 **영역(Territory)** 이 대신.
> 도로 decay·depot·zone-prune 모두 제거. 복구: git **`road-maintenance-complete`** 브랜치/태그(`ee368ae`).
> **진행 결정(B)**: 제거 + Territory 구축을 **새 세션에서 함께**(같은 파일을 어차피 다시 건드림 → churn·리스크↓, 에디터 컴파일로 검증).

### 영역/영향력 설계 (사용자, 2026-06-26)
1. **영역 = 플레이어 고유 셀.** 적은 영역 안 신규 건설 불가. 영역에 새로 먹힌 적 기존 건물·도로는 파괴.
2. **영역 원천 = 인구.** 거주건물 인구 ÷ 셀당 기준 = 영역 셀 수. 거주지 중심 **원형 전파**.
   겹치는 셀은 합산, 잔여값은 가장 가까운 셀로 전파. (예: 인구 50, 셀당 5 → 10셀.)
3. **영향력(Influence) = 영역의 힘.** 시민 행복도 등(팩션별 상이 가능). 향후 다용도(점6).
4. **영역 겹침**: 영향력 가감 → 이긴 팀이 셀 소유, 영향력은 순값.
5. **도로 자유 배치** — 베이스 연결 규칙 폐기(어디나). 단 적 영역엔 불가.
6. **유일한 강제 파괴 = 남의 영역의 도로·건설물** (decay·zone-prune 없음).
7. **AI 확장 = 기존 블록 방식 유지**(maintenance 추가분만 제거).
- 재사용 자산: `GridLayers.TerritoryLayer`(int2→LocalId) **이미 존재** → 영역 셀 저장에 재활용.
  폐기했던 Phase 0 클레임 게이트(`ClaimOps`)의 풍부판 = 인구 기반 + 영향력(되살리지 말고 새로).

### 새 세션 제거 스펙 (걷어낼 것 / ★남길 것)
- **삭제(파일)**: `RoadDecaySystem.cs` `RoadDecayTelegraphSystem.cs` `RoadMaintenanceDebugSystem.cs`
  `DepotPlaceController.cs` · zone: `ZoneComponents.cs` `NetworkRepairSystem.cs` `CityZoneInitSystem.cs`.
- **제거(코드)**: `StampKind.RoadMaintenance` + `RoadMaintenanceDepot` + depot 도장 루프(`StampRebuildSystem`) /
  `IsRoadMaintenance`·`MaintenanceMaxDist`·`MaintenanceMaxDistOverride` 전 체인(`RegistryItem`·`PrefabMeta`·
  `BakedPrefabEntry`·authoring·`PrefabLookupBuildSystem`·`SpawnRequest`·`SpawnSystem`·`BuildingPlacement`·
  `PrefabRegistryWindow`) / Phase 4(`FactionBaseSpawnSystem` 베이스 depot + `RoadDecayState.Exempt` 등록) /
  Phase 5(`AiCityGrowthSystem` depot coverage 검사 + stamp 전달 + `GrowthConfig`의 Maintenance 필드) /
  `GameHUD` 관리소 토글 / zone(`RazeSystem`의 AttributeDeath·ReleaseZone·removeCells·NetworkRepairRequest,
  `AiCityGrowthSystem.RegisterZone` + `CityZones` 사용).
- **변경**: `RoadBuildController`의 `FilterConnectedToNetwork`/`ComputeAttached`(도로 연속성) 제거 → 자유 배치(점5/7).
- **★ 남길 것 (maintenance 아님 — 타 시스템이 씀)**: 공급자·창고 stamp(`StampSupplier`/`WarehouseTag`/
  `StampLayers`/`StampRebuildSystem` 코어) · **`RoadCoverageOps.Flood`**(그 stamp BFS가 사용) ·
  `RazeSystem`의 건물 파괴 + **StampDirty 발행**(공급자/창고 coverage 갱신) · 도로/그리드/AI 블록성장/베이스스폰 코어.
- **검증**: `AiCityGrowthSystem`이 `CityZones`를 블록 *선택*에 쓰는지 확인 — raze 정리용 외 안 쓰면 제거 안전.

---

## 도메인 통합 모델 (Domain) — 로드맵 (2026-06-23 설계 확정, 구현은 단계별·다른 세션)

> 배치·이동·타겟을 **하나의 `Domain` 비트마스크**로 통일. 모든 관계 = `maskA & maskB != 0`.
> 이번 세션은 "특수 목적 도로 연장 라우터"까지만. 본 모델 구현은 다른 세션.

### Domain enum (현 `TerrainMask` 확장)
```csharp
[Flags] enum Domain : byte {
    Ground=1<<0, WaterSurface=1<<1, Underwater=1<<2, Mountain=1<<3, Air=1<<4,
}
```
- 기존 `TerrainMask{Land,Water,Any}` 대체. byte(5비트 사용, 3비트 여유).

### 셀: `TerrainCategory`(단일) → `DomainMask`(다중)  ★핵심 마이그레이션
- 한 셀이 여러 도메인 동시 보유: 물=`WaterSurface|Underwater|Air`, 땅=`Ground|Air`, 산=`Mountain|Air`.
- `CellTypeDefinition.TerrainCategory` → `DomainMask`. `CellTypeInfo`/`CellTypeLookup`도 마스크로.
- Air는 거의 모든 셀 → 저장 대신 **계산** 고려(높은 장애물만 차단).

### 한 어휘, 분리된 소비 필드 (규칙 = `mask & mask != 0`)
| 소비자 | 필드 | 규칙 |
|---|---|---|
| 건물 | `BuildableOn` (기존 확장) | footprint 전 셀 `& cell.DomainMask` |
| 유닛 이동 | `Locomotion`(신규) | `& cell.DomainMask` |
| 유닛 피탐 | `Presence`(신규) | 표적 시그니처 |
| 무기 | `CanTarget`(신규) | `& target.Presence` |
- **Presence ≠ Locomotion**: 부상 잠수함(이동 Underwater / 피탐 WaterSurface), 수륙양용 등 → 필드 분리, enum 공유.
- 도로 = `Ground` 인프라. 다리 = 물 위에 Ground 부여(미래).

### 직교 축 (도메인에 넣지 말 것)
- 탐지/은신(잠수함은 탐지 유닛만 타겟), 사거리·시야·명중/회피 = 도메인 밖 별도 축. 도메인은 "어느 레이어를 때릴 수 있나" 게이트만.

### 자원 — 두 채취 모델
- **덮는(overlay)**: 추출 건물이 자원 셀 위에 앉음 → 현재 `ResourceBlocked` 규칙에 `RequiresResource` 예외 필요. 고갈 시 유휴/철거.
- **채취소+일꾼(post)**: 채취소 근처 자원을 시민이 경로이동→수집→운반. 기존 시민 직업+물류+stamp/BFS 재사용.

### 구현 단계 (순서)
1. `Domain` enum + 셀 `DomainMask` + 건물 `BuildableOn` 검증 교체 (기존 동작 보존; 항구/시추선 물 위 배치 가능)
2. 유닛 `Locomotion` + 도메인 인지 길찾기(`UnitPathfinding.BuildBlockedGrid` 도메인화)
3. 유닛 `Presence` + 무기 `CanTarget` (타겟 획득 게이트)
4. 자원 두 채취 모델 + 항구/시추선 건물

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
