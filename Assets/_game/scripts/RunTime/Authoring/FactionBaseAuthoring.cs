using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  FactionBaseAuthoring
    //
    //  서브씬에 하나만 배치.
    //  등록된 모든 FactionBaseDefinition SO를
    //  하나의 BakedFactionBase 버퍼에 통합하여 굽는다.
    //
    //  SO 추가/변경 후 Re-bake 필요.
    // ══════════════════════════════════════════════════════════════
    public class FactionBaseAuthoring : MonoBehaviour
    {
        [Tooltip("모든 팩션의 FactionBaseDefinition SO 목록.\n" +
                 "팩션 추가 시 여기에 SO를 등록하고 Re-bake.")]
        public List<FactionBaseDefinition> Definitions = new();

        class Baker : Baker<FactionBaseAuthoring>
        {
            public override void Bake(FactionBaseAuthoring a)
            {
                var e    = GetEntity(TransformUsageFlags.None);
                var buf  = AddBuffer<BakedFactionBase>(e);
                var meta = AddBuffer<BakedFactionMeta>(e);

                foreach (var def in a.Definitions)
                {
                    if (def == null) continue;

                    meta.Add(new BakedFactionMeta
                    {
                        FactionId    = def.FactionId,
                        BaseCampSize = math.max(3, def.BaseCampSize),
                        MaintenanceMainKey = def.MaintenanceMainKey,
                        MaintenanceOffset  = new int2(
                            def.MaintenanceCellOffset.x,
                            def.MaintenanceCellOffset.y),
                    });

                    foreach (var entry in def.BaseEntries)
                    {
                        if (entry == null) continue;

                        buf.Add(new BakedFactionBase
                        {
                            FactionId          = def.FactionId,
                            MainKey            = entry.MainKey,
                            VariantKeyOverride = entry.VariantKeyOverride,
                            CellOffset         = new int2(
                                entry.CellOffset.x,
                                entry.CellOffset.y),
                            RotationY          = entry.RotationY,
                        });
                    }
                }
            }
        }
    }
}
