using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  물류 운반자 비주얼 시스템
    //
    //  LogisticsPullSystem이 창고→건물 즉시 이전과 동시에
    //  LogisticsCarrierRequest를 발행한다.
    //  CarrierSpawnSystem이 BFS 경로를 계산해 CarrierTag 엔티티를 스폰,
    //  CarrierMoveSystem이 도로 위를 실시간으로 이동시킨 뒤 소멸.
    //  재고에는 영향 없음(순수 비주얼).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>물류 운반자 비주얼 스폰 요청. 재고 이전 직후 LogisticsPullSystem이 발행.</summary>
    public struct LogisticsCarrierRequest : IComponentData
    {
        public int2 SourceRoadCell;
        public int2 DestRoadCell;
        public int  OwnerLocalId;
    }

    /// <summary>운반자 엔티티 식별 태그.</summary>
    public struct CarrierTag : IComponentData { }

    /// <summary>운반자 이동 상태.</summary>
    public struct CarrierState : IComponentData
    {
        /// <summary>현재 경유 중인 경로 인덱스 (path[PathIndex] → path[PathIndex+1]).</summary>
        public int   PathIndex;
        /// <summary>현재 셀 → 다음 셀 보간 진행도 [0, 1).</summary>
        public float MoveTimer;
    }

    /// <summary>운반자 경로 버퍼. 도로 셀 좌표(그리드) 순서열.</summary>
    [InternalBufferCapacity(32)]
    public struct CarrierPathCell : IBufferElementData
    {
        public int2 Cell;
    }

    /// <summary>
    /// 운반자 프리팹 싱글톤.
    /// CarrierPrefabAuthoring Baker가 SubScene 베이킹 시 채운다.
    /// </summary>
    public struct CarrierPrefabSingleton : IComponentData
    {
        public Entity Prefab;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  시민 보행 비주얼 (2026-07-06) — Carrier 이동 인프라 재사용
    //
    //  CitizenMovementSystem(논리 이동 = 타이머)이 출발/귀가 시 요청을 발행하면
    //  CitizenWalkerSpawnSystem이 BFS 경로로 보행자 비주얼을 스폰한다.
    //  이동·소멸은 CarrierTag/CarrierState/CarrierPathCell + CarrierMoveSystem을
    //  그대로 재사용(= Carrier 인프라는 "범용 도로 보행 비주얼"). 순수 비주얼 —
    //  시민 엔티티(논리)와 분리되어 시뮬레이션에 영향 없음.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>시민 보행 비주얼 스폰 요청. 시민 출발(집→공급자)/귀가(공급자→집) 시 발행.</summary>
    public struct CitizenWalkerRequest : IComponentData
    {
        public int2 FromRoadCell;
        public int2 ToRoadCell;
        public int  OwnerLocalId;
    }

    /// <summary>
    /// 시민 보행 비주얼 프리팹 싱글톤. CitizenVisualPrefabAuthoring Baker가 채운다.
    /// 없으면 시민 이동은 타이머만(비주얼 생략) — 기능 저하 없이 동작.
    /// </summary>
    public struct CitizenVisualPrefabSingleton : IComponentData
    {
        public Entity Prefab;
    }
}
