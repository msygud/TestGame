using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  맵 데이터 (JSON 직렬화)
    //
    //  무거운 데이터 보관 ❌. 키 + Transform 정보만.
    //  RequiredDlcs는 맵 저장 시 자동 수집 → 로드 시 보유 검증.
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class MapData
    {
        public string      Version = "1.0";
        public string      MapName;
        public MapSettings Settings;

        /// <summary>이 맵이 사용한 DLC ID 목록. 저장 시 자동 수집.</summary>
        public List<int> RequiredDlcs = new();

        public List<SinglePlacement> Singles     = new();
        public List<MultiPlacement>  Multis      = new();
        public List<RoadPlacement>   Roads       = new();
        public List<TeamStartPoint>  StartPoints = new();
        public List<TerrainCellData> TerrainCells = new();
    }

    [Serializable]
    public struct MapSettings
    {
        public float CellSize;
        public int   Width;
        public int   Height;
    }

    [Serializable]
    public struct TerrainCellData
    {
        public int2 Cell;
        public byte Height;
        public MapTerrainType Terrain;
        [Obsolete("Legacy only. Placement now uses Terrain/Height and RegistryItem.AllowedTerrains.")]
        public MapBuildFlags BuildFlags;
        [Obsolete("Legacy only. Placement now uses Terrain/Height and RegistryItem.AllowedTerrains.")]
        public MapTerrainFlags Flags;

        public MapTerrainLayer TerrainLayer
            => new(Height, Terrain);

        public static TerrainCellData Create(
            int2 cell,
            byte height,
            MapTerrainType terrain)
            => new()
            {
                Cell = cell,
                Height = height,
                Terrain = terrain,
            };
    }

    // ══════════════════════════════════════════════════════════════
    //  Single 인스턴스 — 정확한 위치/회전/스케일
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public struct SinglePlacement
    {
        public int     MainKey;
        public int     VariantKey;
        public int2    Cell;
        public Vector3 Position;
        public int     Height;
        public float   RotationY;
        public float   Scale;
    }

    // ══════════════════════════════════════════════════════════════
    //  Multi 인스턴스 — 셀 좌표 + 시드 (런타임 결정적 랜덤)
    //
    //  RegistryItem의 Size 영역 안에서 MultiCountPerCell 만큼 랜덤 배치.
    //  Seed로 매번 같은 결과 보장.
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public struct MultiPlacement
    {
        public int  MainKey;
        public int  VariantKey;
        public int2 Cell;            // 좌하단 셀
        public Vector3 Position;
        public int  Height;          // 셀 0.5 단위
        public int  Seed;            // 결정적 랜덤 시드
        public float Scale;
    }

    // ══════════════════════════════════════════════════════════════
    //  Road 인스턴스
    //
    //  Cell 1x1 점유. RoadDir 비트마스크로 모양 결정.
    //  RotationY는 (Shape, Directions)로 계산 가능하지만 에디터 편의를 위해 저장.
    //
    //  인게임에서는 RoadSystem이 Cell만 보고 자체 계산 (Directions/RotationY 무시).
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public struct RoadPlacement
    {
        public int   MainKey;       // 5개 도로 모양 중 어느 것
        public int   VariantKey;    // 외형 베리언트 (도로는 일괄)
        public int2  Cell;
        public Vector3 Position;
        public int   Height;        // 셀 0.5 단위
        public byte  Directions;    // RoadDir 비트마스크
        public float RotationY;     // (Shape, Directions)에서 결정, 에디터 편의용 저장
        public float Scale;
    }

    // ══════════════════════════════════════════════════════════════
    //  팀 스타트 포인트
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public struct TeamStartPoint
    {
        public int     Number;
        public Vector3 Position;
        public List<SinglePlacement> BaseSingles;
        public List<MultiPlacement>  BaseMultis;
        public List<RoadPlacement>   BaseRoads;
    }
}
