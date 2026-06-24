using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════
    //  Stamp 인프라 — "도달 범위 다시 그리기" 자료구조 골격
    // ──────────────────────────────────────────────────────────────────────
    //  설계 뿌리 (2026-06-04 결정):
    //  - stamp = 연결을 "맺는" 게 아니라, 공급자가 자기 입구 도로셀에서 BFS로
    //    도달 가능한 도로셀마다 SupplierRef "도장"을 찍어 도달 범위를 그리는 것.
    //    수급자는 자기 도로셀을 읽어 어떤 공급자가 닿는지 안다 (다중소스 확산).
    //  - 소유는 LocalId 단위. stamp 테이블도 플레이어(LocalId)별로 완전 독립.
    //    재빌드 = maps[P].Clear() 후 P만 BFS (라운드로빈).
    //  - 무효화 회피 = 낡은 stamp를 패치하지 않고 매번 새로 그림(클리어 후 재BFS).
    //    재빌드 트리거 = dirty 플래그 (그 플레이어 도로/공급자 변경 시 ON).
    //
    //  ※ 이 파일은 "그릇"만 정의한다. BFS 도장 / 수급자 조회는 후속 단계.
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>도장 종류. 같은 stamp 레이어에 공급자/창고/관리시설이 함께 찍힌다.</summary>
    public enum StampKind : byte
    {
        Supplier = 0,    // 욕구 공급자(Relief 의미 있음). ServiceSearch가 매칭.
        Warehouse,       // 물류 창고(Relief=None). pull/push가 품목별로 사용.
        RoadMaintenance, // 도로 관리시설(Relief=None). 도달 범위 = 관리되는 도로.
                         //   RoadDecaySystem(Phase 3)이 이 도장 없는 도로를 미관리로 판정.
    }

    /// <summary>
    /// 도로셀에 찍히는 도장 한 개. "이 셀에 이 시설(Kind)이 dist 거리로 닿는다."
    /// 한 셀에 여럿이 겹칠 수 있으므로 MultiHashMap(int2 → SupplierRef)로 보관.
    /// </summary>
    public struct SupplierRef
    {
        /// <summary>시설 건물 엔티티(공급자 또는 창고).</summary>
        public Entity Supplier;

        /// <summary>욕구 공급자가 해소하는 욕구 비트(.Raw). 창고는 None. 수급자 매칭용.</summary>
        public NeedType Relief;

        /// <summary>시설 입구에서 이 셀까지의 BFS 도로 거리(셀 수). 가까운 것 우선용.</summary>
        public int Dist;

        /// <summary>도장 종류(공급자/창고). 소비처는 Kind로 걸러 읽는다.</summary>
        public StampKind Kind;
    }

    /// <summary>
    /// 플레이어(LocalId)별 독립 stamp 테이블 + 재빌드 dirty 플래그.
    ///
    /// 중첩 네이티브 컨테이너 금지를 피하기 위해 슬롯 맵을 배열/중첩이 아닌
    /// 개별 필드 8개로 펼친다(_0.._7). 슬롯 접근은 인덱서로 감싼다.
    /// MaxPlayers = 8 (슬롯 0~7, SkirmishLobby 슬롯 모델과 일치).
    /// </summary>
    public struct StampLayers : IComponentData
    {
        public const int MaxPlayers = 8;

        // ── 슬롯별 독립 맵 (int2 도로셀 → SupplierRef, 다중) ────────────────
        NativeParallelMultiHashMap<int2, SupplierRef> _0;
        NativeParallelMultiHashMap<int2, SupplierRef> _1;
        NativeParallelMultiHashMap<int2, SupplierRef> _2;
        NativeParallelMultiHashMap<int2, SupplierRef> _3;
        NativeParallelMultiHashMap<int2, SupplierRef> _4;
        NativeParallelMultiHashMap<int2, SupplierRef> _5;
        NativeParallelMultiHashMap<int2, SupplierRef> _6;
        NativeParallelMultiHashMap<int2, SupplierRef> _7;

        /// <summary>
        /// 슬롯별 재빌드 필요 플래그. 비트 i = LocalId i의 stamp가 낡았다(재BFS 필요).
        /// byte 한 개로 8슬롯 충분. 순수 시간기반 아님 — 변화 직후 우선 재빌드용.
        /// </summary>
        public byte DirtyMask;

        // ── 슬롯 맵 접근 인덱서 ────────────────────────────────────────────
        public NativeParallelMultiHashMap<int2, SupplierRef> this[int localId]
        {
            get
            {
                switch (localId)
                {
                    case 0:  return _0;
                    case 1:  return _1;
                    case 2:  return _2;
                    case 3:  return _3;
                    case 4:  return _4;
                    case 5:  return _5;
                    case 6:  return _6;
                    case 7:  return _7;
                    default: return default;
                }
            }
            set
            {
                switch (localId)
                {
                    case 0:  _0 = value; break;
                    case 1:  _1 = value; break;
                    case 2:  _2 = value; break;
                    case 3:  _3 = value; break;
                    case 4:  _4 = value; break;
                    case 5:  _5 = value; break;
                    case 6:  _6 = value; break;
                    case 7:  _7 = value; break;
                }
            }
        }

        // ── dirty 플래그 헬퍼 ──────────────────────────────────────────────
        public void MarkDirty(int localId)
        {
            if ((uint)localId < MaxPlayers)
                DirtyMask |= (byte)(1 << localId);
        }

        public void ClearDirty(int localId)
        {
            if ((uint)localId < MaxPlayers)
                DirtyMask &= (byte)~(1 << localId);
        }

        public bool IsDirty(int localId)
            => (uint)localId < MaxPlayers && (DirtyMask & (1 << localId)) != 0;

        /// <summary>
        /// 한 슬롯 맵을 비운다. 재빌드 직전 호출 (clear 후 그 슬롯만 재BFS).
        /// </summary>
        public void ClearSlot(int localId)
        {
            var map = this[localId];
            if (map.IsCreated) map.Clear();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  StampInitSystem — StampLayers 싱글톤 alloc/dispose
    //  (GridInitSystem과 동일한 수명주기 패턴)
    // ══════════════════════════════════════════════════════════════════════
    public partial struct StampInitSystem : ISystem
    {
        // 슬롯당 초기 용량. 도로셀 도달 범위 규모에 맞춰 잡은 시작값(가변).
        const int InitialCapacityPerSlot = 1024;

        public void OnCreate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<StampLayers>())
                return;

            var stamp = new StampLayers { DirtyMask = 0 };
            for (int p = 0; p < StampLayers.MaxPlayers; p++)
            {
                stamp[p] = new NativeParallelMultiHashMap<int2, SupplierRef>(
                    InitialCapacityPerSlot, Allocator.Persistent);
            }

            var e = state.EntityManager.CreateEntity(typeof(StampLayers));
            state.EntityManager.SetComponentData(e, stamp);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<StampLayers>())
                return;

            var stamp = SystemAPI.GetSingleton<StampLayers>();
            for (int p = 0; p < StampLayers.MaxPlayers; p++)
            {
                var map = stamp[p];
                if (map.IsCreated) map.Dispose();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  StampDirtyEvent — "이 플레이어의 도로/공급자가 변했다" 단발성 이벤트
    // ──────────────────────────────────────────────────────────────────────
    //  도로/건물 변경 지점(RoadSystem, BuildingPlacement)은 StampLayers를 직접
    //  들지 않는다. 대신 이 이벤트 엔티티만 발행하고, 수집 시스템이 모아서
    //  DirtyMask에 반영한다(의존성 분리 + 한 틱 다중 변경 일괄 처리).
    // ══════════════════════════════════════════════════════════════════════
    public struct StampDirtyEvent : IComponentData
    {
        public int OwnerLocalId;
    }

    /// <summary>
    /// StampDirtyEvent를 모아 StampLayers.DirtyMask에 반영하고 이벤트를 소비한다.
    /// 실제 재BFS는 후속 재빌드 시스템이 DirtyMask를 보고 수행(라운드로빈).
    /// </summary>
    public partial struct StampDirtyCollectSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<StampLayers>())
                return;

            // 이벤트가 하나도 없으면 싱글톤 쓰기조차 생략(불필요한 변경 방지).
            bool any = false;
            foreach (var _ in SystemAPI.Query<RefRO<StampDirtyEvent>>())
            {
                any = true;
                break;
            }
            if (!any)
                return;

            var stamp = SystemAPI.GetSingleton<StampLayers>();
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (evt, e) in
                     SystemAPI.Query<RefRO<StampDirtyEvent>>().WithEntityAccess())
            {
                stamp.MarkDirty(evt.ValueRO.OwnerLocalId);
                ecb.DestroyEntity(e);
            }

            // DirtyMask는 값 필드 → 수정본을 싱글톤에 다시 써야 반영된다.
            SystemAPI.SetSingleton(stamp);

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
