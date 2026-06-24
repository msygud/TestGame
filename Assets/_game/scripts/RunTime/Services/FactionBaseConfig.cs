using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
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
        /// <summary>도로 관리시설 MainKey. 0 = 자동 배치 안 함.</summary>
        public int  MaintenanceMainKey;
        /// <summary>관리시설 배치 오프셋 — buildOrigin 기준 상대 셀.</summary>
        public int2 MaintenanceOffset;
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
    //  FactionBaseSpawnDone  (태그 컴포넌트)
    //
    //  FactionBaseSpawnSystem 1회 실행 완료 마커.
    //  이 태그가 존재하면 시스템이 재실행되지 않는다.
    //  다른 시스템이 베이스 스폰 완료 여부를 확인할 때도 사용.
    // ══════════════════════════════════════════════════════════════
    public struct FactionBaseSpawnDone : IComponentData { }
}
