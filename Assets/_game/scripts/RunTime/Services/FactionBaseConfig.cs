using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  BaseSpawnEntry  — 팩션 초기 베이스 배치 항목 1개
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class BaseSpawnEntry
    {
        [Tooltip("배치할 프리팹의 MainKey (GamePrefabRegistry 기준).")]
        public int MainKey;

        [Tooltip("0 = VariantProfile에서 해결 (유저·AI 설정 따름).\n" +
                 "1 이상 = 이 값으로 고정 (팩션 고유 외형 강제).")]
        public int VariantKeyOverride;

        [Tooltip("팀 스타트포인트 셀 기준 상대 오프셋 (XZ 그리드).")]
        public Vector2Int CellOffset;

        [Tooltip("Y축 회전 (도).")]
        public float RotationY;
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionBaseDefinition  (ScriptableObject)
    //
    //  팩션 하나의 게임 시작 초기 베이스 배치 정의.
    //  FactionId 하나당 하나의 SO를 생성한다.
    //
    //  FactionBaseAuthoring에 이 SO 목록을 등록하면
    //  Baker가 BakedFactionBase 버퍼로 굽는다.
    //
    //  메뉴: Assets > Create > CitySim > Faction Base Definition
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Faction Base Definition",
        fileName = "FactionBaseDef_New")]
    public class FactionBaseDefinition : ScriptableObject
    {
        [Tooltip("FactionDefinition의 FactionId와 반드시 일치해야 한다.")]
        public int FactionId;

        [Tooltip("초기 베이스캠프 영역 크기 (N×N 셀). 스타트포인트를 좌하단 기준으로 N×N 외곽 도로를 생성한다.")]
        [Min(3)]
        public int BaseCampSize = 8;

        [Tooltip("게임 시작 시 팀 스타트포인트 기준으로 배치될 건물·유닛 목록.")]
        public List<BaseSpawnEntry> BaseEntries = new();
    }

    // ══════════════════════════════════════════════════════════════
    //  BakedFactionMeta  (IBufferElementData)
    //
    //  팩션당 1항목. 베이스캠프 크기 등 항목 단위가 아닌 팩션 단위 메타.
    //  BakedFactionBase와 동일 엔티티에 두 버퍼로 공존한다.
    // ══════════════════════════════════════════════════════════════
    [InternalBufferCapacity(8)]
    public struct BakedFactionMeta : IBufferElementData
    {
        public int FactionId;
        public int BaseCampSize;
    }

    // ══════════════════════════════════════════════════════════════
    //  BakedFactionBase  (IBufferElementData)
    //
    //  FactionBaseDefinition SO → ECS 버퍼 변환 결과.
    //  FactionBaseAuthoring.Baker가 채운다.
    //  FactionBaseSpawnSystem이 읽어 PlaceBuildingRequest를 발행한다.
    // ══════════════════════════════════════════════════════════════
    [InternalBufferCapacity(16)]
    public struct BakedFactionBase : IBufferElementData
    {
        /// <summary>어느 팩션의 베이스인지. FactionDefinition.FactionId와 대응.</summary>
        public int   FactionId;
        /// <summary>배치할 프리팹 MainKey.</summary>
        public int   MainKey;
        /// <summary>0 = VariantProfile 해결. 1+ = 강제 고정 VariantKey.</summary>
        public int   VariantKeyOverride;
        /// <summary>스타트포인트 셀 기준 XZ 상대 오프셋.</summary>
        public int2  CellOffset;
        /// <summary>배치 Y축 회전 (도).</summary>
        public float RotationY;
    }

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

    // ══════════════════════════════════════════════════════════════
    //  FactionBaseSpawnDone  (태그 컴포넌트)
    //
    //  FactionBaseSpawnSystem 1회 실행 완료 마커.
    //  이 태그가 존재하면 시스템이 재실행되지 않는다.
    //  다른 시스템이 베이스 스폰 완료 여부를 확인할 때도 사용.
    // ══════════════════════════════════════════════════════════════
    public struct FactionBaseSpawnDone : IComponentData { }
}
