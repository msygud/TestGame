using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CitySim
{
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

    // ══════════════════════════════════════════════════════════════
    //  PrefabCategory  — 스폰(인스턴싱) 방식 기준 분류
    //
    //  카테고리가 스폰 방식을 완전히 결정한다 (별도 SpawnMode 불필요).
    //
    //  ── 건설 (건설 UI에서 유저가 배치) ──
    //    Road        1:N 연결성 드래그 (비트마스크 셰이프 자동)
    //    Building    1:1 단일, footprint 점유, 입구 보유
    //    Environment 1:N 랜덤 다중 (셀 내), 단일+드래그
    //
    //  ── 생산 (건물이 생산, 직접 배치 아님) ──
    //    CombatUnit  생산 건물 → 유닛 선택 → 스폰
    //
    //  ── 런타임 전용 (시스템 스폰, 배치 UI 미노출) ──
    //    Projectile  투사체
    //    Effect      이펙트
    //
    //  ※ 기존 Terrain/Unit 제거됨. 기존 SO 항목은 재분류 필요.
    //     (Terrain → Building 또는 Environment, Unit → CombatUnit)
    // ══════════════════════════════════════════════════════════════
    public enum PrefabCategory
    {
        // 건설
        Road        = 0,
        Building    = 1,
        Environment = 2,

        // 생산
        CombatUnit  = 3,

        // 런타임 전용
        Projectile  = 10,
        Effect      = 11,

        Other       = 99,
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
    //  RegistryItem  — SO 내 개별 프리팹 항목 (MainKey + VariantKey 단위)
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
        public PrefabUsage    Usage;

        [Header("Prefab")]
        public GameObject Prefab;

        [Header("Placement")]
        public Vector2Int Size        = new(1, 1);            // XZ 셀 단위 (Building만 의미 있음)
        public Vector3    Offset      = Vector3.zero;
        [Tooltip("배치 가능한 지형. Land=땅 전용, Water=물 전용, Any=모두 가능.")]
        public TerrainMask BuildableOn = TerrainMask.Land;

        [Header("Road")]
        // Category == Road 인 항목: RoadDir 비트마스크 (1~15).
        // None(0)이면 도로가 아님.
        public RoadDir RoadMask;

        [Header("Environment (Multi)")]
        public int   MultiCountPerCell = 5;
        public float MultiItemSize     = 0.5f;

        [Header("DLC")]
        // 0이면 레지스트리의 dlcId를 따름.
        public int DlcKey;

        [Header("State")]
        public bool IsDeleted;

        // ── 헬퍼 (스폰 방식·속성은 Category에서 파생) ─────────────
        public bool IsRoad         => Category == PrefabCategory.Road;
        public bool IsBuilding     => Category == PrefabCategory.Building;
        public bool IsMulti        => Category == PrefabCategory.Environment; // 셀 내 랜덤 다중
        public bool IsCombatUnit   => Category == PrefabCategory.CombatUnit;
        public bool HasEntrance    => Category == PrefabCategory.Building;
        public bool IsConstructable
            => Category is PrefabCategory.Road
                        or PrefabCategory.Building
                        or PrefabCategory.Environment;
        public bool IsValid => Prefab != null && !IsDeleted;
    }

    // ══════════════════════════════════════════════════════════════
    //  EntranceEntry  — MainKey 단위 입구 정의 (Building 전용)
    //
    //  NeedMappingEntry와 동일하게 MainKey 키로 별도 관리.
    //  외형(VariantKey) 무관 — 게임플레이 속성.
    //
    //  Offsets: footprint 원점(좌하단) 기준 상대 셀.
    //           배치 시 origin + offset 셀이 도로면 연결 성립.
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class EntranceEntry
    {
        public int               MainKey;
        public List<Vector2Int>  Offsets = new();
    }

    // ══════════════════════════════════════════════════════════════
    //  GamePrefabRegistry  (ScriptableObject)
    //
    //  DLC 하나당 하나의 SO.
    //  인게임에서는 SubScene 베이킹을 통해 ECS Entity로 제공된다.
    //
    //  MainKey 단위 매핑 리스트 (NeedMaps와 동일 패턴):
    //    items[]     — L1 소스 (MainKey, VariantKey, Prefab)
    //    NeedMaps[]  — L2 소스 (NeedMask → MainKey)
    //    Entrances[] — 입구 소스 (MainKey → 입구 오프셋들)
    //
    //  메뉴: Assets > Create > CitySim > Game Prefab Registry
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Game Prefab Registry",
        fileName = "GamePrefabRegistry")]
    public class GamePrefabRegistry : ScriptableObject
    {
        [Header("DLC Identity")]
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

        [Header("Entrances")]
        // 입구 구성 소스 (Building MainKey 전용).
        // GamePrefabRegistryAuthoring이 BakedEntranceEntry 버퍼로 베이킹.
        public List<EntranceEntry> Entrances = new();

        // ── 프로퍼티 (외부 접근 편의) ─────────────────────────────
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

        /// <summary>MainKey의 입구 정의 조회. 없으면 null.</summary>
        public EntranceEntry GetEntrance(int mainKey)
        {
            foreach (var e in Entrances)
                if (e.MainKey == mainKey) return e;
            return null;
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

            // 입구는 Building MainKey에만 의미가 있음
            foreach (var ent in Entrances)
            {
                bool isBuilding = false;
                foreach (var item in items)
                {
                    if (item.IsDeleted) continue;
                    if (item.MainKey == ent.MainKey && item.IsBuilding) { isBuilding = true; break; }
                }
                if (!isBuilding)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Warning,
                        Message = $"Entrance(MainKey={ent.MainKey}): 대응하는 Building 항목이 없습니다.",
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
