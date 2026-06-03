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

    // ══════════════════════════════════════════════════════════════
    //  MainKeyRange — 카테고리별 MainKey 허용 범위
    //
    //  종류마다 MainKey 구역을 강제한다. 안 막아두면 키가 뒤섞여
    //  나중에 고생하므로, 등록 시점(Validate)에서 위반을 잡는다.
    //
    //  배정 (프리팹 많을 종류 = 큰 구역):
    //    0            : 무효(null) 키 — 미지정 표식 전용
    //    1   ~ 999    : Road        (철도/고속도로/보도 + 팩션 다수 대비)
    //    1000~ 4999   : Building    (종류 가장 많음)
    //    5000~ 6999   : Environment (나무/바위/장식 등)
    //    7000~ 8999   : CombatUnit  (팩션별 병종)
    //    9000~ 9499   : Projectile
    //    9500~ 9999   : Effect
    //    10000+       : Other       (자유)
    //
    //  ※ Road의 dirMask(1~15)는 VariantKey 자리에 들어간다.
    //    MainKey 자체는 이 범위(1~999)를 따른다.
    // ══════════════════════════════════════════════════════════════
    public static class MainKeyRange
    {
        public const int NullKey = 0;

        public const int RoadMin        = 1,     RoadMax        = 999;
        public const int BuildingMin    = 1000,  BuildingMax    = 4999;
        public const int EnvironmentMin = 5000,  EnvironmentMax = 6999;
        public const int CombatUnitMin  = 7000,  CombatUnitMax  = 8999;
        public const int ProjectileMin  = 9000,  ProjectileMax  = 9499;
        public const int EffectMin      = 9500,  EffectMax      = 9999;
        public const int OtherMin       = 10000; // 상한 없음

        /// <summary>해당 카테고리의 허용 [min,max] 반환. Other는 max=int.MaxValue.</summary>
        public static (int min, int max) For(PrefabCategory cat) => cat switch
        {
            PrefabCategory.Road        => (RoadMin,        RoadMax),
            PrefabCategory.Building    => (BuildingMin,    BuildingMax),
            PrefabCategory.Environment => (EnvironmentMin, EnvironmentMax),
            PrefabCategory.CombatUnit  => (CombatUnitMin,  CombatUnitMax),
            PrefabCategory.Projectile  => (ProjectileMin,  ProjectileMax),
            PrefabCategory.Effect      => (EffectMin,      EffectMax),
            _                          => (OtherMin,       int.MaxValue),
        };

        /// <summary>mainKey가 cat의 허용 범위 안인가.</summary>
        public static bool IsInRange(int mainKey, PrefabCategory cat)
        {
            var (min, max) = For(cat);
            return mainKey >= min && mainKey <= max;
        }

        /// <summary>mainKey가 속한 카테고리 추론(범위 기반). 못 찾으면 Other.</summary>
        public static PrefabCategory CategoryOf(int mainKey)
        {
            if (mainKey >= RoadMin        && mainKey <= RoadMax)        return PrefabCategory.Road;
            if (mainKey >= BuildingMin    && mainKey <= BuildingMax)    return PrefabCategory.Building;
            if (mainKey >= EnvironmentMin && mainKey <= EnvironmentMax) return PrefabCategory.Environment;
            if (mainKey >= CombatUnitMin  && mainKey <= CombatUnitMax)  return PrefabCategory.CombatUnit;
            if (mainKey >= ProjectileMin  && mainKey <= ProjectileMax)  return PrefabCategory.Projectile;
            if (mainKey >= EffectMin      && mainKey <= EffectMax)      return PrefabCategory.Effect;
            return PrefabCategory.Other;
        }
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
    //  EntranceEntry  — MainKey 단위 입구 정의 (Building 전용, 단일 입구)
    //
    //  Offset: footprint 원점(좌하단=최소코너) 기준 상대 셀. 0 ≤ Offset < Size.
    //          이 셀은 건물 footprint에 속하는 경계 셀이다.
    //  Dir   : 입구가 바라보는 바깥 방향 (N/E/S/W 중 단일 비트).
    //          도로 접합 셀 = origin + Rotate(Offset) + DirOffset(Rotate(Dir)).
    //
    //  단일 입구 확정 (단일 = 다중의 특수형). 외형(VariantKey) 무관 — 게임플레이 속성.
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class EntranceEntry
    {
        public int MainKey;
        public Vector2Int Offset;          // 최소코너 기준 상대 셀 (비음수, footprint 내)
        public RoadDir Dir = RoadDir.S; // 입구가 향하는 단일 방향 (기본 남쪽)
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

                // ── MainKey 범위 강제 (카테고리별 구역 위반 검사) ──
                if (item.MainKey != MainKeyRange.NullKey
                    && !MainKeyRange.IsInRange(item.MainKey, item.Category))
                {
                    var (min, max) = MainKeyRange.For(item.Category);
                    var actualCat  = MainKeyRange.CategoryOf(item.MainKey);
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Error,
                        Message = $"[{item.Name}] MainKey={item.MainKey}: "
                                + $"{item.Category} 범위[{min}~{max}] 위반 "
                                + $"(이 키는 {actualCat} 구역). 카테고리나 MainKey를 맞추세요.",
                    });
                }
            }

            // 입구는 Building MainKey에만 의미가 있음
            foreach (var ent in Entrances)
            {
                RegistryItem building = null;
                foreach (var item in items)
                {
                    if (item.IsDeleted) continue;
                    if (item.MainKey == ent.MainKey && item.IsBuilding) { building = item; break; }
                }
                if (building == null)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level = ValidationLevel.Warning,
                        Message = $"Entrance(MainKey={ent.MainKey}): 대응하는 Building 항목이 없습니다.",
                    });
                    continue;
                }

                // Dir이 단일 비트(N/E/S/W 중 하나)인지
                if (RoadDirOps.PopCount(ent.Dir) != 1)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level = ValidationLevel.Error,
                        Message = $"Entrance(MainKey={ent.MainKey}): Dir은 N/E/S/W 중 정확히 하나여야 합니다 (현재 {ent.Dir}).",
                    });
                }

                // Offset이 footprint(Size) 범위 안인지 (회전 전 기준)
                if (ent.Offset.x < 0 || ent.Offset.y < 0
                    || ent.Offset.x >= building.Size.x || ent.Offset.y >= building.Size.y)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level = ValidationLevel.Error,
                        Message = $"Entrance(MainKey={ent.MainKey}): Offset {ent.Offset}이 "
                                + $"footprint(Size={building.Size.x}x{building.Size.y}) 범위를 벗어났습니다.",
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
