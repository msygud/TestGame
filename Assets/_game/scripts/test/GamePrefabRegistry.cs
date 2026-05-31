using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  열거형
    // ══════════════════════════════════════════════════════════════


    // ══════════════════════════════════════════════════════════════
    //  TerrainMask  — 건물이 배치 가능한 지형 종류 (비트마스크)
    // ══════════════════════════════════════════════════════════════
    [Flags]
    public enum TerrainMask : byte
    {
        None  = 0,
        Land  = 1 << 0,
        Water = 1 << 1,
        Any   = Land | Water,
    }

    public enum PrefabCategory
    {
        Terrain    = 0,  // 지형·장식물 (나무, 바위, 지형물 등)
        Unit       = 1,  // 유닛
        Projectile = 2,  // 투사체
        Effect     = 3,  // 이펙트 (파티클, VFX 등)
        Road       = 4,  // 도로
        Other      = 99, // 그 외
    }

    public enum PrefabSpawnMode
    {
        Single = 0,  // 셀 크기(Size)만큼 점유, 1개 인스턴싱
        Multi  = 1,  // 셀 1개 강제, 셀 내 N개 랜덤 배치
    }

    [Flags]
    public enum PrefabUsage
    {
        None       = 0,
        MapEditor  = 1 << 0,  // 맵에디터 배치 대상
        Ingame     = 1 << 1,  // 인게임 런타임 스폰 대상
        StartPoint = 1 << 2,  // 팀 스타트 포인트 배치 대상
        Campaign   = 1 << 3,  // 캠페인 전용
        Both       = MapEditor | Ingame,

        // 이전 호환용 별칭
        Runtime = Ingame,
    }

    // ══════════════════════════════════════════════════════════════
    //  검증 관련 타입
    // ══════════════════════════════════════════════════════════════

    public enum ValidationLevel { Info, Warning, Error }

    public struct ValidationIssue
    {
        public ValidationLevel Level;
        public string          Message;
    }

    // ══════════════════════════════════════════════════════════════
    //  RegistryItem  — SO 내 개별 프리팹 항목
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class RegistryItem
    {
        [Header("Keys")]
        public int MainKey;    // 종류 식별자 (같은 MainKey = 같은 종류)
        public int VariantKey; // 외형 변형 (0 = 기본, 1+ = 베리언트)
                               // 도로: 1~15 = RoadDir 비트마스크

        [Header("Info")]
        public string         Name;
        public PrefabCategory Category;
        public PrefabSpawnMode SpawnMode;
        public PrefabUsage    Usage;

        [Header("Prefab")]
        public GameObject Prefab;

        [Header("Placement")]
        public Vector2Int Size        = new(1, 1);            // XZ 셀 단위 (Single만 의미 있음)
        public Vector3    Offset      = Vector3.zero;
        [Tooltip("배치 가능한 지형. Land=땅 전용, Water=물 전용, Any=모두 가능.")]
        public TerrainMask BuildableOn = TerrainMask.Land;

        [Header("Road")]
        // Category == Road 인 항목: RoadDir 비트마스크 (1~15).
        // None(0)이면 도로가 아님.
        public RoadDir RoadMask;

        [Header("Multi")]
        public int   MultiCountPerCell = 5;
        public float MultiItemSize     = 0.5f;

        [Header("DLC")]
        // 0이면 레지스트리의 dlcId를 따름.
        public int DlcKey;

        [Header("State")]
        public bool IsDeleted;

        // ── 헬퍼 ─────────────────────────────────────────────────
        public bool IsRoad  => Category == PrefabCategory.Road;
        public bool IsMulti => SpawnMode == PrefabSpawnMode.Multi;
        public bool IsValid => Prefab != null && !IsDeleted;
    }

    // ══════════════════════════════════════════════════════════════
    //  GamePrefabRegistry  (ScriptableObject)
    //
    //  DLC 하나당 하나의 SO.
    //  인게임에서는 SubScene 베이킹을 통해 ECS Entity로 제공된다.
    //  에디터에서는 맵에디터 프리팹 목록과 Addressables 등록에 사용.
    //
    //  메뉴: Assets > Create > CitySim > Game Prefab Registry
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Game Prefab Registry",
        fileName = "GamePrefabRegistry")]
    public class GamePrefabRegistry : ScriptableObject
    {
        [Header("DLC Identity")]
        // 소문자 필드명: Editor의 FindProperty("dlcId") 등과 일치
        public int    dlcId;
        public string dlcName;
        public string displayName; // UI 표시용 (비면 dlcName 사용)

        [Header("Export")]
        public string jsonExportPath; // JSON 내보내기 경로

        [Header("Items")]
        public List<RegistryItem> items = new();

        [Header("Need Mappings")]
        // NeedLayer L2 구성 소스. NeedMappingAuthoring이 BakedNeedMapping 버퍼로 베이킹.
        public List<NeedMappingEntry> NeedMaps = new();

        // ── 프로퍼티 (외부 접근 편의) ─────────────────────────────
        // PrefabRegistryWindow / GamePrefabRegistryEditor 에서 사용
        public int    DlcId   => dlcId;
        public string DlcName => dlcName;
        public List<RegistryItem> Items => items;
        public string JsonExportPath => jsonExportPath;

        // ── 런타임 캐시 ──────────────────────────────────────────────
        Dictionary<(int main, int variant), RegistryItem> _cache;

        /// <summary>(MainKey, VariantKey)로 항목 조회.</summary>
        public RegistryItem GetItem(int mainKey, int variantKey)
        {
            BuildCacheIfNeeded();
            _cache.TryGetValue((mainKey, variantKey), out var item);
            return item;
        }

        /// <summary>같은 MainKey의 모든 베리언트 반환.</summary>
        public List<RegistryItem> GetVariants(int mainKey)
        {
            var result = new List<RegistryItem>();
            foreach (var item in items)
                if (!item.IsDeleted && item.MainKey == mainKey)
                    result.Add(item);
            return result;
        }

        /// <summary>새 항목 추가 후 반환.</summary>
        public RegistryItem AddItem()
        {
            var item = new RegistryItem { DlcKey = dlcId };
            items.Add(item);
            InvalidateCache();
            return item;
        }

        // ── 검증 ────────────────────────────────────────────────────

        /// <summary>항목 정합성 검사. 이슈 목록 반환.</summary>
        public List<ValidationIssue> Validate()
        {
            var issues = new List<ValidationIssue>();
            var seen   = new HashSet<(int, int)>();

            foreach (var item in items)
            {
                if (item.IsDeleted) continue;
                if (item.Prefab == null)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Warning,
                        Message = $"[{item.Name}] ({item.MainKey},{item.VariantKey}): Prefab is null.",
                    });
                }
                var key = (item.MainKey, item.VariantKey);
                if (!seen.Add(key))
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Error,
                        Message = $"[{item.Name}] ({item.MainKey},{item.VariantKey}): Duplicate key!",
                    });
                }
                if (item.MainKey == 0 && item.VariantKey == 0 && !item.IsRoad)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Warning,
                        Message = $"[{item.Name}]: MainKey=0, VariantKey=0 is the null key — consider using 1+.",
                    });
                }
            }
            return issues;
        }

        // ── JSON 내보내기 ───────────────────────────────────────────

        /// <summary>
        /// 경량 JSON (키/이름만) 내보내기.
        /// 인게임에서는 SubScene 베이킹을 사용하므로 이 JSON은 DLC 카탈로그용.
        /// </summary>
        public bool ExportJson(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(jsonExportPath))
            {
                error = "jsonExportPath가 비어있습니다.";
                return false;
            }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"dlcId\": {dlcId},");
                sb.AppendLine($"  \"dlcName\": \"{dlcName}\",");
                sb.AppendLine($"  \"displayName\": \"{(string.IsNullOrEmpty(displayName) ? dlcName : displayName)}\",");
                sb.AppendLine("  \"items\": [");

                int valid = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.IsDeleted || item.Prefab == null) continue;

                    bool last = (i == items.Count - 1);
                    sb.AppendLine(
                        $"    {{\"mainKey\":{item.MainKey}," +
                        $"\"variantKey\":{item.VariantKey}," +
                        $"\"name\":\"{item.Name}\"," +
                        $"\"category\":{(int)item.Category}}}" +
                        (last ? "" : ","));
                    valid++;
                }

                sb.AppendLine("  ]");
                sb.AppendLine("}");

                Directory.CreateDirectory(Path.GetDirectoryName(jsonExportPath)!);
                File.WriteAllText(jsonExportPath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ── 내부 ────────────────────────────────────────────────────

        void BuildCacheIfNeeded()
        {
            if (_cache != null) return;
            _cache = new Dictionary<(int, int), RegistryItem>(items.Count);
            foreach (var item in items)
            {
                if (item.IsDeleted || item.Prefab == null) continue;
                _cache.TryAdd((item.MainKey, item.VariantKey), item);
            }
        }

        public void InvalidateCache() => _cache = null;

        void OnValidate() => InvalidateCache();
    }
}
