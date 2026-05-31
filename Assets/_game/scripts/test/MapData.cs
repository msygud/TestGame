using System;
using System.Collections.Generic;
using UnityEngine;

namespace CitySim.MapEditor
{
    // ══════════════════════════════════════════════════════════════
    //  MapSettings  — 맵 기본 설정
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public struct MapSettings
    {
        public float CellSize;  // 월드 단위 / 셀
        public int   Width;     // X 셀 수
        public int   Height;    // Z 셀 수
    }

    // ══════════════════════════════════════════════════════════════
    //  직렬화용 셀 데이터
    // ══════════════════════════════════════════════════════════════

    [Serializable]
    public struct TerrainCellData
    {
        public int  X, Y;
        public int  TypeId;
        public byte Height;
    }

    [Serializable]
    public struct ResourceCellData
    {
        public int X, Y;
        public int TypeId;
        public int Amount;
    }

    /// <summary>팀 시작 위치. 위치 정보만 저장한다 (베이스 배치 없음).</summary>
    [Serializable]
    public struct StartPointData
    {
        public int        TeamIndex;
        public Vector2Int Cell;
    }

    // ══════════════════════════════════════════════════════════════
    //  배치 데이터 — 에디터 저장 / 인게임 로드용
    // ══════════════════════════════════════════════════════════════

    /// <summary>Single 모드 프리팹 배치 정보.</summary>
    [Serializable]
    public class SinglePlacement
    {
        public int   MainKey;
        public int   VariantKey;
        public int   CellX, CellZ;  // 좌하단 셀 (Size > 1x1인 경우 기준점)
        public float PositionY;     // 월드 Y 좌표 (에디터 높이 조절, 단위: CellSize * 0.5)
        public float RotationY;     // Y축 회전 (도)
        public float Scale;         // 배치 스케일 (1 = 기본)
    }

    /// <summary>Multi 모드 프리팹 배치 정보.</summary>
    [Serializable]
    public class MultiPlacement
    {
        public int   MainKey;
        public int   VariantKey;
        public int   CellX, CellZ;
        public float Height;        // 월드 Y 좌표 (단위: CellSize * 0.5)
        public int   RandomSeed;    // 0이면 좌표 기반 자동 시드
    }

    /// <summary>도로 배치 정보.</summary>
    [Serializable]
    public class RoadPlacement
    {
        public int MainKey;         // 프리팹 레지스트리 MainKey
        public int CellX, CellZ;
        // VariantKey(비트마스크)는 저장하지 않음.
        // 인게임 RoadSystem이 인접 셀 기반으로 재계산.
    }

    // ══════════════════════════════════════════════════════════════
    //  MapData  — 에디터가 메모리에 보유하는 전체 맵 데이터
    //
    //  에디터 저장 항목:
    //    Settings, StartPoints, TerrainCells, ResourceCells,
    //    Singles, Multis, Roads, RequiredDlcs
    //
    //  인게임 시작 시 생성 (저장 안 함):
    //    OccupancyLayer, RoadLayer, TerritoryLayer
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class MapData
    {
        public string      MapName  = "NewMap";
        public MapSettings Settings;

        // ── 시작 위치 ─────────────────────────────────────────────
        public List<StartPointData> StartPoints = new();

        // ── 지형 / 자원 ───────────────────────────────────────────
        public List<TerrainCellData>  TerrainCells  = new();
        public List<ResourceCellData> ResourceCells = new();

        // ── 배치물 ────────────────────────────────────────────────
        public List<SinglePlacement> Singles = new();
        public List<MultiPlacement>  Multis  = new();
        public List<RoadPlacement>   Roads   = new();

        // ── DLC 의존성 ────────────────────────────────────────────
        /// <summary>
        /// 이 맵을 플레이하기 위해 필요한 DLC ID 목록.
        /// SaveMap() 시 BuildMeta()로 자동 계산되어 저장된다.
        /// </summary>
        public List<int> RequiredDlcs = new();

        // ── 에디터 전용 캐시 (직렬화 제외) ────────────────────────
        [NonSerialized] public Dictionary<Vector2Int, TerrainCellData>  TerrainDict  = new();
        [NonSerialized] public Dictionary<Vector2Int, ResourceCellData> ResourceDict = new();

        // ── 캐시 동기화 ───────────────────────────────────────────

        /// <summary>List → Dict 캐시 빌드. 로드 후 또는 에디터 시작 시 호출.</summary>
        public void RebuildDicts()
        {
            TerrainDict.Clear();
            foreach (var c in TerrainCells)
                TerrainDict[new Vector2Int(c.X, c.Y)] = c;

            ResourceDict.Clear();
            foreach (var c in ResourceCells)
                ResourceDict[new Vector2Int(c.X, c.Y)] = c;
        }

        /// <summary>Dict → List 동기화. 저장 전 호출.</summary>
        public void FlushDicts()
        {
            TerrainCells.Clear();
            foreach (var kv in TerrainDict)
                TerrainCells.Add(kv.Value);

            ResourceCells.Clear();
            foreach (var kv in ResourceDict)
                ResourceCells.Add(kv.Value);
        }

        // ── DLC 메타 빌드 ─────────────────────────────────────────

        /// <summary>
        /// MapMeta 생성. RequiredDlcIds를 Singles/Multis/Roads의 DlcKey로부터 계산.
        /// registries가 null이면 RequiredDlcs 필드를 그대로 사용.
        /// </summary>
        public MapMeta BuildMeta(List<GamePrefabRegistry> registries)
        {
            var dlcIds = new HashSet<int>(RequiredDlcs);

            if (registries != null)
            {
                // Singles
                foreach (var p in Singles)
                    CollectDlcId(p.MainKey, p.VariantKey, registries, dlcIds);

                // Multis
                foreach (var p in Multis)
                    CollectDlcId(p.MainKey, p.VariantKey, registries, dlcIds);

                // Roads
                foreach (var p in Roads)
                    CollectDlcId(p.MainKey, 0, registries, dlcIds);
            }

            dlcIds.Remove(0); // Origin은 항상 보유 → 불필요

            return new CitySim.MapMeta
            {
                MapId          = MapName,
                DisplayName    = MapName,
                Width          = Settings.Width,
                Height         = Settings.Height,
                CellSize       = Settings.CellSize,
                RequiredDlcIds = new List<int>(dlcIds),
            };
        }

        static void CollectDlcId(
            int mainKey, int variantKey,
            List<GamePrefabRegistry> registries,
            HashSet<int> result)
        {
            foreach (var reg in registries)
            {
                var item = reg?.GetItem(mainKey, variantKey);
                if (item == null) continue;
                int dlcId = item.DlcKey != 0 ? item.DlcKey : reg.dlcId;
                result.Add(dlcId);
                break;
            }
        }
    }
}
