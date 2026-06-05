using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════
    //  SupplierStamp  — 공급자 도달성 "도장" 골격
    //
    //  설계 핵심 (메모리 ⑤):
    //    · stamp = "연결 맺기"가 아니라 "도달 범위 다시 그리기".
    //      공급자가 입구 도로셀에서 BFS로 도로망에 SupplierRef를 찍고,
    //      수급자(시민/건물)는 자기 도로셀을 읽어 누가 닿는지 안다.
    //    · 플레이어별 독립 테이블. 재빌드 = maps[P].Clear() 후 P만 BFS (라운드로빈).
    //    · 목적지 불필요 (다중 소스 확산), 출발지(공급자 도로셀)만 선행.
    //
    //  보관 방식 (B안):
    //    설계 문구의 NativeHashMap<LocalID, MultiHashMap<…>> 중첩은
    //    Unity Collections에서 safety/dispose가 까다롭다.
    //    플레이어가 슬롯 0~7 고정 8개이므로, 길이 8 배열로 분리 보관한다.
    //    · maps[localId] = 그 플레이어의 도로셀 → SupplierRef 다중맵.
    //    · Clear/Dispose가 플레이어 단위로 자명. 중첩 컨테이너 없음.
    //
    //  임시 단계 주의:
    //    SupplierRef.Relief 는 지금 공급자 엔티티에 직접 박은 값을 BFS가
    //    읽어 싣는다. 나중에 (ResourceType/NeedMask → 테이블) 참조로 리팩터.
    // ══════════════════════════════════════════════════════════════════

    /// <summary>플레이어 슬롯 수 (LocalId 0~7 고정).</summary>
    public static class StampConfig
    {
        public const int MaxPlayers = 8;

        /// <summary>맵 초기 용량 힌트 (셀 기준, 자동 증가하므로 대략치).</summary>
        public const int InitialCapacity = 1024;
    }

    // ──────────────────────────────────────────────────────────────────
    //  SupplierRef  — 도로셀 한 칸에 찍히는 공급자 도장 1개.
    //
    //  한 셀에 여러 공급자가 닿을 수 있으므로 MultiHashMap의 값으로 들어가
    //  같은 키(int2)에 여러 SupplierRef가 쌓인다.
    // ──────────────────────────────────────────────────────────────────
    public struct SupplierRef
    {
        /// <summary>이 도장을 찍은 공급자 건물 엔티티.</summary>
        public Entity Supplier;

        /// <summary>이 공급자가 해소하는 Need 조합 (임시: 엔티티에 직접 박은 값).</summary>
        public NeedType Relief;

        /// <summary>공급자 도로셀에서 이 셀까지의 BFS 거리(도로 칸 수). 가까운 공급자 우선용.</summary>
        public int Dist;
    }

    // ──────────────────────────────────────────────────────────────────
    //  SupplierStampMaps  — 플레이어별 stamp 맵 8개를 보관하는 싱글톤.
    //
    //  IComponentData 안에 NativeContainer를 직접 배열로 둘 수 없으므로
    //  (managed 배열 금지), 고정 8개를 개별 필드로 펼친다.
    //  인덱싱은 Get(localId)로 추상화.
    // ──────────────────────────────────────────────────────────────────
    public struct SupplierStampMaps : IComponentData
    {
        public NativeParallelMultiHashMap<int2, SupplierRef> P0;
        public NativeParallelMultiHashMap<int2, SupplierRef> P1;
        public NativeParallelMultiHashMap<int2, SupplierRef> P2;
        public NativeParallelMultiHashMap<int2, SupplierRef> P3;
        public NativeParallelMultiHashMap<int2, SupplierRef> P4;
        public NativeParallelMultiHashMap<int2, SupplierRef> P5;
        public NativeParallelMultiHashMap<int2, SupplierRef> P6;
        public NativeParallelMultiHashMap<int2, SupplierRef> P7;

        /// <summary>localId(0~7)에 해당하는 맵 반환. ref 반환으로 Clear/Add 직접 가능.</summary>
        public ref NativeParallelMultiHashMap<int2, SupplierRef> Get(int localId)
        {
            switch (localId)
            {
                case 0: return ref P0;
                case 1: return ref P1;
                case 2: return ref P2;
                case 3: return ref P3;
                case 4: return ref P4;
                case 5: return ref P5;
                case 6: return ref P6;
                case 7: return ref P7;
                default:
                    // 슬롯 범위 밖은 P0로 폴백 (상위에서 0~7 보장).
                    return ref P0;
            }
        }

        /// <summary>OnCreate에서 8개 맵 일괄 할당.</summary>
        public void AllocateAll(int capacity, Allocator allocator)
        {
            P0 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
            P1 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
            P2 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
            P3 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
            P4 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
            P5 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
            P6 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
            P7 = new NativeParallelMultiHashMap<int2, SupplierRef>(capacity, allocator);
        }

        /// <summary>OnDestroy에서 8개 맵 일괄 해제.</summary>
        public void DisposeAll()
        {
            if (P0.IsCreated) P0.Dispose();
            if (P1.IsCreated) P1.Dispose();
            if (P2.IsCreated) P2.Dispose();
            if (P3.IsCreated) P3.Dispose();
            if (P4.IsCreated) P4.Dispose();
            if (P5.IsCreated) P5.Dispose();
            if (P6.IsCreated) P6.Dispose();
            if (P7.IsCreated) P7.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  StampDirty  — 플레이어별 재빌드 필요 플래그 (싱글톤).
    //
    //  메모리 ⑤-보충: 순수 시간기반 아님. 그 플레이어의 도로/공급자가
    //  변경되면 해당 비트 ON → 다음 저빈도 틱에서 우선 재BFS.
    //  bit i = localId i 의 dirty 여부.
    // ──────────────────────────────────────────────────────────────────
    public struct StampDirty : IComponentData
    {
        /// <summary>localId별 dirty 비트 (bit0 = P0 …). 초기엔 모두 dirty로 1회 빌드.</summary>
        public byte Mask;

        public bool IsDirty(int localId) => (Mask & (1 << localId)) != 0;
        public void SetDirty(int localId) => Mask |= (byte)(1 << localId);
        public void ClearDirty(int localId) => Mask &= unchecked((byte)~(1 << localId));
        public bool AnyDirty => Mask != 0;
    }
}
