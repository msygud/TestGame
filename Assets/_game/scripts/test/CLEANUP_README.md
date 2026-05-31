# 파일 정리 가이드

## ❌ 삭제할 파일 (프로젝트에서 제거)

| 파일 | 이유 |
|---|---|
| `RoadShapeMapping.cs` | 회전 제거 결정. 완전 불필요. |
| `PrefabRegistryComponents.cs` | 중복 타입 정의 (PrefabLookup, PrefabMetaLookup). → `SpawnComponents.cs`로 교체. |
| `PrefabLookupSystem.cs` | 구버전 빌드 시스템. `PrefabLookupBuildSystem.cs`로 대체됨. |
| `PrefabRegistryAuthoring.cs` | 구버전 Authoring. `GamePrefabRegistryAuthoring.cs`로 대체됨. |
| `UnifiedPrefabRegistry.cs` | 미채택 설계안. `GamePrefabRegistry.cs`와 충돌. |
| `UnifiedPrefabLookup.cs` | 미채택 설계안. |

## ✅ 유지할 파일 (변경 없음)

```
AddressableGroupUtility.cs
CellTypeDefinition.cs
CellTypeSystem.cs
CivilianBFS.cs
CubeMapVisualizer.cs
DlcAddressConfig.cs
DlcBootstrap.cs
DlcOwnershipService.cs
DlcSubSceneManifest.cs
FactionConfig.cs
GridInitSystem.cs
GridLayers.cs
LayerPainters.cs
MapBoundaryGizmo.cs
MapData.cs
MapEditorWindow.cs
MapLoadComponents.cs
MapLoadSystem.cs
MapMeta.cs
NeedMappingAuthoring.cs
NeedType.cs
TeamStartPoint.cs
```

## 🔄 교체할 파일 (이 폴더의 파일로 덮어쓰기)

| 파일 | 주요 변경 내용 |
|---|---|
| `RoadDirection.cs` | **신규** — `RoadDir` [Flags] enum + `RoadDirOps` static class |
| `SpawnComponents.cs` | **신규** — `PrefabRegistryComponents.cs` 정리본 (SpawnRequest 등 유효 타입만) |
| `GamePrefabRegistry.cs` | 필드 추가 (`NeedMaps`, 각 항목에 `DlcKey`/`MultiCountPerCell`/`MultiItemSize`), `Validate()`/`ExportJson()` 메서드 추가, 소문자 직렬화 필드명 (`dlcId`, `dlcName`, `items`) |
| `GamePrefabRegistryAuthoring.cs` | `DlcId`, `MultiCount`, `MultiItemSize` 베이킹 추가 |
| `GamePrefabRegistryEditor.cs` | 소문자 필드명 접근 (`reg.dlcId`, `reg.items` 등) |
| `PrefabLookup.cs` | `BakedPrefabEntry`에 `DlcId`/`MultiCount`/`MultiItemSize` 추가; `PrefabLookup`에 `LoadedDlcIds` + `HasDlc()` 추가; `PrefabMeta` 통합 (MultiCount 포함); `PrefabMetaLookup`에 `TryGetMeta()` 추가 |
| `PrefabLookupBuildSystem.cs` | `LoadedDlcIds` 생성/채우기; `PrefabMeta`에 MultiCount 반영 |
| `LookupComponents.cs` | `PrefabLookupL1` 제거 (→ `PrefabLookup` 공유로 대체) |
| `LookupBuildSystem.cs` | L1 빌드 코드 제거, `PrefabEntry` 버퍼 제거, `[UpdateAfter(PrefabLookupBuildSystem)]` 추가 |
| `LookupHelper.cs` | `PrefabLookupL1` → `PrefabLookup` 로 교체 |
| `RoadComponents.cs` | `Road` + `PlaceRoadCommand`에 `int MainKey` 추가 |
| `RoadSystem.cs` | `RoadPrefabLookup` → `PrefabLookup.GetRoad()` 교체 |
| `SpawnSystem.cs` | `.TryGet()` → `.Get()` API 교체 |
| `MapLoaderSystem.cs` | `[UpdateAfter(PrefabLookupInitSystem)]` → `[UpdateAfter(PrefabLookupBuildSystem)]`; `IsDlcAvailable` → `lookup.HasDlc()`; `PlaceRoadCommand.MainKey` 설정 추가 |
| `PrefabRegistryWindow.cs` | `RoadShape`/`RoadShape.NotRoad` → `RoadDir`/`RoadDir.None`; `FindProperty("RoadShape")` → `FindProperty("RoadMask")`; 도로 판정 Category 기반으로 변경; `GamePrefabRegistryEditor` 클래스 제거 (별도 파일로 분리됨) |

## 주요 아키텍처 결정 요약

```
GamePrefabRegistry (SO)
  └─ GamePrefabRegistryAuthoring (SubScene Baker)
       └─ BakedPrefabEntry (DynamicBuffer)
            └─ PrefabLookupBuildSystem (InitializationSystemGroup)
                 ├─ PrefabLookup { Table, LoadedDlcIds } (싱글톤 L1)
                 └─ PrefabMetaLookup { Table } (싱글톤 메타)

NeedMappingAuthoring (SubScene Baker)
  └─ BakedNeedMapping (DynamicBuffer)
       └─ LookupBuildSystem (InitializationSystemGroup, 1회)
            └─ NeedLookupL2 (팩션 엔티티별 컴포넌트)

LookupHelper.TryGetPrefab(needMask, factionL2, ref state, out prefab)
  → L2: NeedMask → MainKey
  → L1(PrefabLookup): (MainKey, VariantKey) → Entity

도로: 비트마스크(1~15) = VariantKey = RoadMask
  PrefabLookup.GetRoad(mainKey, dirMask) → Entity
  회전 없음. 15개 프리팹 1:1 매핑.
```
