using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════
    //  GamePrefabRegistryAuthoring
    //
    //  DLC 하나당 하나의 SubScene에 배치.
    //  Baker가 GamePrefabRegistry SO의 모든 항목을
    //  BakedPrefabEntry DynamicBuffer로 변환한다.
    //
    //  SubScene 구성 예:
    //    Origin SubScene
    //      └─ GameObject "RegistryBaker"
    //           └─ GamePrefabRegistryAuthoring (Registry = Origin SO)
    //
    //    DLC1 SubScene
    //      └─ GameObject "RegistryBaker"
    //           └─ GamePrefabRegistryAuthoring (Registry = DLC1 SO)
    //
    //  런타임 흐름:
    //    SubScene 로드 → Baker 결과 엔티티 + BakedPrefabEntry 버퍼 생성
    //    → PrefabLookupBuildSystem이 수집 → PrefabLookup/PrefabMetaLookup 반영
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

                var e      = GetEntity(TransformUsageFlags.None);
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

                    // 개별 DlcKey가 설정되어 있으면 우선, 아니면 레지스트리 DlcId 사용
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
                        SpawnMode     = item.SpawnMode,
                    });

                    baked++;
                }

                Debug.Log(
                    $"[Baker] '{authoring.Registry.dlcName}' 베이킹 완료: " +
                    $"{baked}개 등록, {skipped}개 스킵 " +
                    $"(총 {authoring.Registry.items.Count}개)");
            }
        }
    }
}
