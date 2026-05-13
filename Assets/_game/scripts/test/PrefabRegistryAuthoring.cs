using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  PrefabRegistryAuthoring
    //
    //  서브씬 안에 하나 배치. SO를 참조해 베이크 시 DynamicBuffer 생성.
    //
    //  사용법:
    //    1. DLC별 서브씬 생성 (Origin/PrefabRegistry.unity 등)
    //    2. 빈 GameObject에 이 컴포넌트 추가
    //    3. Source 슬롯에 GamePrefabRegistry SO 드래그&드롭
    //    4. 베이크 시 Source.Items가 모두 ECS 데이터로 변환됨
    // ══════════════════════════════════════════════════════════════
    public class PrefabRegistryAuthoring : MonoBehaviour
    {
        [Tooltip("이 서브씬이 베이크할 SO. 진실원천.")]
        public GamePrefabRegistry Source;

        // ══════════════════════════════════════════════════════════
        //  Baker
        // ══════════════════════════════════════════════════════════
        class Baker : Baker<PrefabRegistryAuthoring>
        {
            public override void Bake(PrefabRegistryAuthoring a)
            {
                if (a.Source == null) return;

                DependsOn(a.Source);

                var e       = GetEntity(TransformUsageFlags.None);
                var buf     = AddBuffer<PrefabRegistryEntry>(e);
                var metaBuf = AddBuffer<PrefabMetaEntry>(e);

                int skipped = 0;

                foreach (var item in a.Source.Items)
                {
                    if (item.IsDeleted) continue;
                    if (item.Prefab == null) { skipped++; continue; }

                    var prefabEntity = GetEntity(item.Prefab, TransformUsageFlags.Dynamic);

                    buf.Add(new PrefabRegistryEntry
                    {
                        MainKey    = item.MainKey,
                        VariantKey = item.VariantKey,
                        DlcKey     = item.DlcKey,
                        Prefab     = prefabEntity,
                    });

                    // 메타 버퍼 (Multi 정보 포함)
                    metaBuf.Add(new PrefabMetaEntry
                    {
                        MainKey       = item.MainKey,
                        VariantKey    = item.VariantKey,
                        MultiCount    = item.MultiCountPerCell,
                        MultiItemSize = item.MultiItemSize,
                    });
                }

                if (skipped > 0)
                    Debug.LogWarning(
                        $"[PrefabRegistryAuthoring] '{a.Source.name}': " +
                        $"skipped {skipped} items with null Prefab.");
            }
        }
    }
}
