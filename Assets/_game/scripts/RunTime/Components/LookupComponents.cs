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
