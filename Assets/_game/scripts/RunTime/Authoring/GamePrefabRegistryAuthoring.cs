using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════
    //  GamePrefabRegistryAuthoring
    //
    //  DLC 하나당 하나의 SubScene에 배치.
    //  Baker가 GamePrefabRegistry SO를 두 버퍼로 변환한다:
    //    items[]     → BakedPrefabEntry
    //    Entrances[] → BakedEntranceEntry (평탄화)
    //
    //  런타임 흐름:
    //    SubScene 로드 → Baker 결과 버퍼 생성
    //    → PrefabLookupBuildSystem이 수집
    //      → PrefabLookup / PrefabMetaLookup / EntranceLookup 반영
    // ══════════════════════════════════════════════════════════════
    public class GamePrefabRegistryAuthoring : MonoBehaviour
    {
        [Tooltip("이 SubScene이 대표하는 DLC의 GamePrefabRegistry SO.")]
        public GamePrefabRegistry Registry;

        class Baker : Baker<GamePrefabRegistryAuthoring>
        {
            public override void Bake(GamePrefabRegistryAuthoring authoring)
            {
                if (authoring.Registry == null)
                {
                    Debug.LogError(
                        $"[GamePrefabRegistryAuthoring] '{authoring.gameObject.name}'에 " +
                        $"Registry SO가 연결되어 있지 않습니다.");
                    return;
                }

                DependsOn(authoring.Registry);

                var e = GetEntity(TransformUsageFlags.None);

                // ── ① 프리팹 항목 베이킹 ──────────────────────────
                var buffer = AddBuffer<BakedPrefabEntry>(e);

                int baked   = 0;
                int skipped = 0;
                int dlcId   = authoring.Registry.dlcId;

                foreach (var item in authoring.Registry.items)
                {
                    if (item.IsDeleted || item.Prefab == null)
                    {
                        skipped++;
                        continue;
                    }

                    var prefabEntity = GetEntity(item.Prefab, TransformUsageFlags.Dynamic);

                    int itemDlcId = item.DlcKey != 0 ? item.DlcKey : dlcId;

                    buffer.Add(new BakedPrefabEntry
                    {
                        MainKey       = item.MainKey,
                        VariantKey    = item.VariantKey,
                        Prefab        = prefabEntity,
                        Size          = new int2(item.Size.x, item.Size.y),
                        Offset        = item.Offset,
                        RoadMask      = (byte)item.RoadMask,
                        MultiCount    = item.MultiCountPerCell,
                        MultiItemSize = item.MultiItemSize,
                        DlcId         = itemDlcId,
                        BuildableOn   = item.BuildableOn,
                        Category      = item.Category,
                        IsSupplier    = item.IsSupplier,
                        Relief        = item.Relief,
                        SupplyMaxDist = item.SupplyMaxDist,
                    });

                    baked++;
                }

                // ── ② 입구 베이킹 (단일 Offset + Dir) ────────────
                // 버퍼는 입구가 없어도 항상 생성 (BuildSystem 조회 일관성).
                var entranceBuffer = AddBuffer<BakedEntranceEntry>(e);

                int entranceCount = 0;
                foreach (var ent in authoring.Registry.Entrances)
                {
                    if (ent == null) continue;

                    entranceBuffer.Add(new BakedEntranceEntry
                    {
                        MainKey = ent.MainKey,
                        Offset = new int2(ent.Offset.x, ent.Offset.y),
                        Dir = (byte)ent.Dir,
                    });
                    entranceCount++;
                }

                Debug.Log(
                    $"[Baker] '{authoring.Registry.dlcName}' 베이킹 완료: " +
                    $"프리팹 {baked}개 등록, {skipped}개 스킵, " +
                    $"입구 {entranceCount}개 " +
                    $"(총 {authoring.Registry.items.Count}개 항목)");
            }
        }
    }
}
