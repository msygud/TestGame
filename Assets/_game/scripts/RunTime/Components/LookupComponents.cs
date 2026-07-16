using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  SO 확장 — GamePrefabRegistry에 추가
    //
    //  기존 items[]     → L1 소스 (MainKey, VariantKey, Prefab)
    //  신규 NeedMaps[]  → L2 소스 (NeedMask, MainKey, FactionFlags)
    //
    //  L1은 기존 PrefabLookup 싱글톤을 공유한다.
    //  L2는 팩션 엔티티당 독립 NeedLookupL2 컴포넌트로 보관.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GamePrefabRegistry SO에 추가할 니드 매핑 엔트리.
    /// NeedMask 조합 → 어떤 MainKey 건물/유닛이 그 니드를 충족하는가.
    /// FactionFlags == 0  → 모든 팩션 공통 적용.
    /// FactionFlags != 0  → 해당 팩션 비트가 켜진 팩션에만 적용.
    /// </summary>
    [Serializable]
    public class NeedMappingEntry
    {
        public int  MainKey;      // 어떤 프리팹 그룹이 이 니드를 충족하는가
        public uint NeedMask;     // 니드 비트 조합 (업그레이드로 바뀌는 대상)
        public uint FactionFlags; // 적용 팩션 (0 = 공통)
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  L2 — 팩션별 니드 룩업 (동적, 업그레이드 시 수정)
    //
    //  팩션 엔티티 하나당 독립 보유.
    //  Key : NeedMask (uint)
    //  Value : MainKey (int)
    //
    //  L1(PrefabLookup)을 전혀 모른다. MainKey만 넘겨줄 뿐.
    // ══════════════════════════════════════════════════════════════════════════
    public struct NeedLookupL2 : IComponentData
    {
        /// <summary>NeedMask → MainKey</summary>
        public NativeHashMap<uint, int> Table;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  팩션 식별
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 팩션 엔티티에 붙는 ID.
    /// 0 = 공통(Common) — 모든 플레이어가 공유하는 니드 매핑.
    /// 1~8 = 개별 팩션.
    /// </summary>
    public struct FactionId : IComponentData
    {
        public int Value;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ProducerLookup — 생산/창고 파생 결정 테이블 (다지기 ③·④, 2026-07-11)
    //
    //  "무엇이 이 commodity를 만드나 / 창고는 어느 키인가"를 프리팹 **능력**
    //  (ProductionJob.RecipeOutput / WarehouseTag — 다지기 ①이 베이크)에서 파생.
    //  LookupBuildSystem이 1회 구성(싱글톤), AI 건설 결정(AiCityGrowth)이 소비.
    //  구 HardcodedCommodityProducer / 창고 1005 하드코딩의 정식 대체 —
    //  프리팹 미베이크 시 소비처가 하드코딩 폴백(전환기).
    // ══════════════════════════════════════════════════════════════════════════
    public struct ProducerLookup : IComponentData
    {
        /// <summary>(int)Commodity → 생산 건물 MainKey. 복수 생산자는 MainKey 오름차순 선승.</summary>
        public NativeHashMap<int, int> Table;

        /// <summary>창고 프리팹 MainKey(WarehouseTag 보유 최소 키). 0 = 파생 실패(폴백).</summary>
        public int WarehouseMainKey;

        /// <summary>오라형 공급자 MainKey → 오라 반경(셀) — AI 배치의 지구 슬롯 선호 +
        /// **도달 가드**(앵커에서 반경 밖 배치 금지 — 못 덮는 오라 연쇄 차단)에 사용
        /// (커버형 욕구 v1, 2026-07-12).</summary>
        public NativeHashMap<int, int> AuraKeys;

        /// <summary>MainKey → 근무 정원(프리팹 WorkplaceBuilding+BuildingOccupancy.Capacity 파생,
        /// 2026-07-17 노동 게이트). AI 수요 건설이 "무직 노동력 ≥ 정원"일 때만 그 건물을 후보로 —
        /// 일할 사람 없는 도시가 무근무 건물만 늘리는 역전 차단(인구 주도 성장). 무인 건물
        /// (공원 등, WorkplaceBuilding 없음)은 미등록 = 게이트 없음.</summary>
        public NativeHashMap<int, int> WorkerNeeds;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  플레이어 베리언트 선택 (싱글플레이 싱글톤)
    //
    //  유저가 베리언트 선택창에서 고른 값.
    //  LookupHelper가 L1 조회 시 VariantKey로 사용.
    //  게임 중 언제든 변경 가능 → 신규 스폰에 즉시 반영.
    // ══════════════════════════════════════════════════════════════════════════
    public struct PlayerVariantSetting : IComponentData
    {
        public int VariantKey;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  베이크 전달용 버퍼 — SO → ECS 서브씬 경유 데이터
    //
    //  NeedMappingAuthoring이 SO의 NeedMaps[]를 이 버퍼로 구워 넣는다.
    //  LookupBuildSystem이 게임 시작 시 이 버퍼를 읽어 L2를 구성한다.
    // ══════════════════════════════════════════════════════════════════════════
    [InternalBufferCapacity(32)]
    public struct BakedNeedMapping : IBufferElementData
    {
        public int  MainKey;
        public uint NeedMask;
        public uint FactionFlags; // 0 = 공통
    }
}
