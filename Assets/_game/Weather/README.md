# Weather ECS - Unity 6 + Entities 1.x

눈/비/스플래시/눈 누적 + 자국 효과를 위한 ECS 기반 시스템입니다.

## 폴더 구조

```
WeatherECS/
├── Scripts/
│   ├── Components/        # IComponentData (눈, 비, 추적 타겟)
│   ├── Systems/           # ISystem / SystemBase
│   ├── Authoring/         # MonoBehaviour → ECS 변환 (Baker)
│   └── Managed/           # 카메라 추적 드라이버, 누적 리소스
├── Shaders/               # SnowGround, WetGround 셰이더
└── ComputeShaders/        # SnowAccumulation.compute
```

## 필수 패키지

- `com.unity.entities` (1.3+)
- `com.unity.entities.graphics`
- `com.unity.render-pipelines.universal` (URP)

## 셋업 단계

### 1) 씬에 SubScene 만들기
SubScene 안에 아래 GameObject들을 배치합니다.

### 2) Snow 셋업
1. **Snowflake Prefab**: 작은 Quad나 Sphere에 `SnowflakeAuthoring` 추가. URP Lit 머티리얼.
2. 빈 GameObject에 `SnowSpawnerAuthoring`을 붙이고 SnowflakePrefab을 할당.
3. 빈 GameObject에 `SnowAccumulationAuthoring`을 붙이고 `SnowAccumulation.compute`를 할당.
4. 빈 GameObject에 `GroundInfoAuthoring`을 붙이고 바닥 Y값 설정 (예: 0).

### 3) Snow Ground 셋업
- 64m × 64m 정도로 **분할이 잘 된 Plane**을 준비 (Subdivision 100+ 권장).
- `SnowGround.shader`를 사용하는 머티리얼 적용.
- 시스템이 자동으로 `_GlobalSnowHeightMap`을 셰이더에 전역 바인딩합니다.

### 4) Rain 셋업
1. **RainDrop Prefab**: 길게 늘인 Quad (0.02 × 0.4) + URP Unlit 머티리얼 + `RainDropAuthoring`.
2. **Splash Prefab**: 작은 ring/quad mesh + `RainSplashAuthoring`.
3. 빈 GameObject에 `RainSpawnerAuthoring` 추가, 두 prefab 할당.

### 5) 자국(Footprint) 셋업
- 캐릭터의 발 위치(또는 발 본)에 빈 GameObject를 자식으로 만들고 `SnowDeformerAuthoring` 추가.
- Radius 0.3-0.5, Depth 0.3 정도가 자연스럽습니다.
- **여러 개**(왼발/오른발/엉덩이 등) 붙여도 됩니다 - 시스템이 자동으로 모두 수집.

### 6) 카메라 추적
- 메인 카메라(또는 플레이어)에 `WeatherFollowTargetDriver` MonoBehaviour 추가.
- 이 위치를 중심으로 눈/비가 스폰되고 누적 텍스처가 따라다닙니다.

### 7) Wet Ground (선택)
- 바닥 Plane 바로 위에 또 하나의 Plane을 배치하고 `WetGround.shader` 머티리얼 적용.
- 비가 올 때만 활성화하면 됩니다.

## 작동 원리 요약

### 눈/비 입자
ECS Entity 하나당 눈송이/빗방울 하나입니다. `IJobEntity` + Burst로 병렬 처리합니다. 카메라 주변에만 스폰하고 바닥에 닿거나 수명을 다하면 ECB로 destroy.

### 눈 누적 (Heightmap)
- 512×512 RFloat RenderTexture가 카메라 주변 64m × 64m를 커버.
- `CSAccumulate`: 매 프레임 약간씩 눈 높이 증가 (눈이 다시 쌓이는 효과).
- `CSDeform`: 모든 `SnowDeformer` 위치를 ComputeBuffer로 GPU에 전달, 해당 영역의 높이를 차감.
- 텍스처는 **텍셀 단위로 스냅**되어 카메라 이동 시 sub-pixel sliding 없음.
- 셰이더가 이 heightmap을 읽어 vertex displacement + 노멀 계산.

### 자국이 메워지는 효과
`CSAccumulate` 안의 `_RecoveryRate`가 deform된 영역도 천천히 채웁니다. 발자국이 영구적이지 않게 하려면 이 값을 키우면 됩니다.

### 빗방울 충돌 효과
- **개별 splash entity**: 빗방울이 바닥에 닿는 순간 `RainFallJob`이 splash prefab을 instantiate. `RainSplashJob`이 0.35초 동안 scale을 키우다가 destroy.
- **표면 ripple**: `WetGround.shader`가 절차적으로 무수한 잔물결을 그립니다 (텍스처 불필요). `_RainAmount` 파라미터로 강도 조절.

## 성능 팁

| 항목 | 권장값 | 비고 |
|------|--------|------|
| Snow SpawnRate | 1500-3000 | Entity 수가 ~10000 넘으면 VFX Graph 고려 |
| Rain SpawnRate | 3000-6000 | 빗방울은 수명이 짧아 동시 존재 수는 적음 |
| Heightmap Resolution | 512 | 1024는 컴퓨트 비용 4배 |
| WorldSize | 64m | 카메라가 빨리 움직이면 키우기 |
| Deformer 수 | <64 | 캐릭터 1명 기준 발 2개면 충분 |

## 확장 아이디어

- **눈송이 하나하나가 deformer**가 되어 실제로 쌓이게 하려면 `SnowFallJob`에서 ground hit 시 SnowDeformer 컴포넌트를 추가하는 짧은 lifetime entity를 spawn하면 됩니다 (단, deformer 수 제한 주의).
- **차량 타이어 자국**: 바퀴마다 SnowDeformer 추가, depth 더 깊게.
- **캐릭터에 눈 묻기**: SkinnedMeshRenderer 셰이더에서 worldNormal.y > 0.7 영역에 눈 텍스처 블렌드.
- **VFX Graph 하이브리드**: 메인 입자는 VFX Graph로, 게임플레이 영향(IK, 사운드 트리거)이 있는 입자만 ECS로.
