# 프로젝트 컨텍스트

## 기술 스택
- Unity 6 + URP
- ECS / DOTS (Entities 1.x)
- 솔로 개발

## 게임 개요
도시 건설 메커니즘과 실시간 전략 전투를 결합한 전략 게임.
시민 시뮬레이션이 군사 효율성으로 직접 연결되는 구조.

---

# 작업 규칙

## 세션 시작 시
1. 루트의 `PROGRESS.md`를 **반드시 먼저 읽을 것**. 현재 진행 상황과 다음 단계가 여기 있음.
2. 해당 주제의 관련 소스 파일을 디스크에서 다시 읽어 최신 상태를 확인할 것.
   (다른 세션이나 에디터에서 수정됐을 수 있음 — 컨텍스트의 스냅샷을 신뢰하지 말 것)

## 세션 종료 시
- `PROGRESS.md`의 해당 섹션을 갱신할 것: 완료한 것, 미결정 사항, 다음 단계.

## 작업 범위
- `Assets/Scripts/` 하위만 수정.
- `Library/`, `Temp/`, `obj/`, `Logs/` 는 절대 건드리지 말 것.

---

# 코딩 원칙

- **Idiomatic ECS 우선.** GameObject 하이브리드 접근은 지양.
- **성능 우선 아키텍처**: Burst, Jobs, 캐시 효율, GPU 파이프라인을 항상 염두.
- **명확한 관심사 분리**: 시스템 간 책임을 깨끗하게 나눌 것.
- 수학적으로 우아하고 구조적으로 깔끔한 해법을 ad-hoc 방식보다 선호.

## ECS 세부 관례 (확립된 패턴)
- `state.Dependency` 할당은 선택이 아닌 필수.
- 청크 마이그레이션을 피하려면 `IEnableableComponent` 사용 (구조적 변경 대신 토글).
- ECB `ParallelWriter` sort key는 버퍼 슬롯이 아니라 정렬용 태그.
- 의도적 Write access 선언으로 시스템 간 의존성 강제 가능 (확립된 기법).
- ECS→UI 통신: `GameDataStore` + C# 이벤트 + `NativeQueue` 브리지.
- "눈에만 안 보이게": `MaterialMeshInfo.Mesh` 조작 (비구조적, Burst 병렬).
- 비균일 스케일: `PostTransformMatrix` (SDF 셰이더에서 X/Z 다를 때 UV 보정 필요).
