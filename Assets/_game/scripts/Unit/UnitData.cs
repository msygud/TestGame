using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Unit
{
    public class UnitData
    {

    }
    [Flags]
    public enum PurposeType : ushort
    {
        None = 0,
        Residential = 1 << 0,
        Commercial = 1 << 1,
        Industrial = 1 << 2,
        All = Residential | Commercial | Industrial
    }
    public struct PrefabInfoElemental : IBufferElementData
    {
        public Entity Prefab;
        public int ID;
    }
    public struct InstanceIDData : IComponentData
    {
        public uint InstanceID;
    }
    public struct UnitName : IComponentData
    {
        public FixedString64Bytes Name;
    }

    public struct RequestUnit : IBufferElementData
    {
        public bool IsUser;
        public int ID;
        public int LocalID;

        //임시
        public int SpawnCount;
    }

    public struct UnitTag : IComponentData
    {

    }
    public struct GridPositionData : IComponentData
    {
        public int2 Index;
        public int FlatIndex;
        public int2 PreviousIndex;
        public int PreviousFlatIndex;

        public float3 CurrentPos;
        public float3 PreviousPos;
        public float3 MoveDir;
        public bool IsMoved;
        public bool IsIndexMoved;
    }
    public struct GeneratedInstanceIdData : IComponentData
    {
        public uint CurrentID;
    }
    public struct PlayerTeam : IComponentData { }
    public struct NonePlayerTeam:IComponentData
    {
        
    }
    public struct DetectedBySight : IComponentData
    {
        public TeamMask DetectedBy;
        public void Reset()
        {
            DetectedBy = 0;
        }
    }
    public struct DetectedByRadar:IComponentData
    {
        public TeamMask FullHide;
        public TeamMask FakeDetected;
        public float3 FakeValue;
        public float3 DetectedPosition(TeamMask byteam)
        {
            float3 tmp = (FakeDetected & byteam) == byteam ? FakeValue : float3.zero;
            return tmp;
        }
        public void Reset()
        {
            FullHide = 0;
            FakeDetected = 0;
            FakeValue = 0;
        }
    }
    public struct VisibleOnScreenData : IComponentData,IEnableableComponent
    {
        
    }
    public struct  VisibleOnMinimapData:IComponentData,IEnableableComponent
    {
        
    }
    public struct OriginalMeshInfo:IComponentData
    {
        public int MeshID;
    }
    public struct RendererOwner:IComponentData
    {
        public Entity Root;
    }

    public struct RequestRendarable : IComponentData { public bool IsVisible; }
    

    // ══════════════════════════════════════════════════════════════
    //  시야 (Vision)
    //
    //  유닛/건물이 자기 주변을 보는 능력.
    //  실제 시야 계산(셀 노출, 안개)은 VisionSystem에서 처리.
    //  여기선 능력 데이터만 보관.
    // ══════════════════════════════════════════════════════════════
    public struct Vision : IComponentData
    {
        public float Range;       // 시야 반경
        public bool BlockedByTerrain; // 지형에 의해 차단되는가 (true가 일반적)
    }

    // ══════════════════════════════════════════════════════════════
    //  레이더 (Radar)
    //
    //  업그레이드/디버프로 런타임에 변경 가능한 동적 능력.
    //  Mode와 Capabilities는 게임 중 바뀔 수 있음.
    //  안티레이더에대항
    // ══════════════════════════════════════════════════════════════
    public struct Radar : IComponentData
    {
        public bool IsActivate;
        public float Range;
        public TeamMask AttackJammerTeam;
        public byte Strength;
        public RadarMode Mode;          // 정밀도
        public RadarFlags Capabilities;  // 다중 특성
    }
    public struct CanSee : IComponentData { }
    public enum RadarMode : byte
    {
        BlipOnly = 0,   // 위치 블립만
        UnitType = 1,   // 위치 + 종류
        Full = 2,   // 시야와 동일한 정보
    }

    [System.Flags]
    public enum RadarFlags : byte
    {
        None = 0,
        DetectsStealth = 1 << 0,  // 스텔스 감지
        DetectsAir = 1 << 1,  // 공중 유닛 감지
        DetectsUnderground = 1 << 2,  // 지하 유닛 감지
        JammerResistant = 1 << 3,  // 재머 저항 (Strength 비교 시 우위)
        IgnoresTerrain = 1 << 4,  // 지형에 차단되지 않음
    }

    // ══════════════════════════════════════════════════════════════
    //  스텔스 (Stealth)
    //
    //  적의 시야/레이더에서 어떻게 회피되는가.
    //  비트가 독립적이라 4가지 조합 자유롭게 표현 가능.
    //  디텍터에 대항
    // ══════════════════════════════════════════════════════════════
    public struct Stealth : IComponentData
    {
        public TeamMask RegistTeam;
        public float BreakDuration;  // 공격 시 노출되는 시간 (초)
    }
    
    [System.Flags]
    public enum StealthFlags : byte
    {
        None = 0,
        InvisibleToVision = 1 << 0,  // 시야에 안 보임
        InvisibleToRadar = 1 << 1,  // 레이더에 안 잡힘
        InvisibleWhenIdle = 1 << 2,  // 정지 중에만 은신
        InvisibleInForest = 1 << 3,  // 특정 지형에서만 은신
    }

    // ══════════════════════════════════════════════════════════════
    //  재머 (Jammer)
    //
    //  타겟별로 효과를 분리:
    //    Protective : 자기 영역 안의 아군에게 효과 부여
    //    Offensive  : 적 레이더에 가하는 영향
    //
    //  한 재머가 양쪽 모두 가질 수도, 한쪽만 가질 수도 있음.
    //  레이더에 대항.
    // ══════════════════════════════════════════════════════════════
    public struct Jammer : IComponentData
    {
        public TeamMask BeatenTeam;
        public float Range;
        public byte Strength;     // 0~255. JammerResistant 레이더와의 대결용.
        public ProtectiveJamming Offensive;

        internal void Reset()
        {
            BeatenTeam = 0;
        }
    }
    public struct ManagedJamState:IBufferElementData
    {
        public TeamMask TargetTeam;
        public int DurationTick;
    }
    /// <summary>아군을 보호하는 효과 (재머 범위 안의 아군에게 적용).</summary>
    [System.Flags]
    public enum ProtectiveJamming : byte
    {
        None = 0,
        HideFromRadar = 1 << 0,  // 아군이 적 레이더에 안 보임
        //HideFromVision = 1 << 1,// 아군이 적 시야에 안 보임 (스텔스 필드)
        SpoofPosition =1<<1,//적 레이더에 가짜 위치 표시
    }

    /// <summary>적 레이더에 가하는 공격 효과.</summary>
    [System.Flags]
    public enum OffensiveJamming : byte
    {
        None = 0,
        DisableRadar = 1 << 0,  // 적 레이더 자체 무력화
        ReduceRange = 1 << 1,  // 적 레이더 범위 축소
    }

    // ══════════════════════════════════════════════════════════════
    //  디텍터 (Detector)
    //
    //  스텔스를 무력화하는 능력.
    //  Counters에 명시된 StealthFlags만 무효화.
    //  (예: InvisibleToVision만 카운터하는 디텍터 = 시각 디텍터)
    // ══════════════════════════════════════════════════════════════
    public struct Detector : IComponentData
    {
        public float Range;
        public byte Strength;
    }
    public struct AntiRadar:IComponentData
    {
        public float Range;
        public float Strength;
        public float Duration;
        public OffensiveJamming JamingType;
    }
}