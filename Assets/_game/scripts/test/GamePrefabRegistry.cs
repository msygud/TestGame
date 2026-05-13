using System;
using System.Collections.Generic;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  GamePrefabRegistry
    //
    //  게임의 모든 프리팹(메인 + 베리언트)을 한 곳에 등록하는 SO.
    //  DLC별로 별도 SO를 둠 (Origin, DLC1, DLC2, ...).
    //
    //  진실원천:
    //    - 맵에디터가 이 SO 직접 참조
    //    - 서브씬 Authoring이 이 SO 참조 → 베이크
    //
    //  메인/베리언트 구분 없음:
    //    - VariantKey == 0 = 기본 외형
    //    - VariantKey > 0  = 추가 베리언트
    //    - 같은 MainKey의 VariantKey는 0부터 결번 없이 +1 순차 (통합 기준)
    //
    //  도로:
    //    - RoadShape != NotRoad 인 항목 = 도로
    //    - 5종류만 등록: Straight, DeadEnd, Corner, T, Cross
    //    - Size는 1x1 강제 (도로는 한 셀)
    //
    //  전부 작성자 책임. 검증은 경고만, 강제 ❌.
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(menuName = "CitySim/Game Prefab Registry", fileName = "GamePrefabRegistry")]
    public class GamePrefabRegistry : ScriptableObject
    {
        [SerializeField] private int    dlcId        = 0;
        [SerializeField] private string dlcName      = "Origin";
        [SerializeField] private string displayName  = "";
        [SerializeField] private string jsonExportPath = "";
        [SerializeField] private List<RegistryItem> items = new();

        public int    DlcId          => dlcId;
        public string DlcName        => dlcName;
        public string DisplayName    => string.IsNullOrEmpty(displayName) ? dlcName : displayName;
        public string JsonExportPath => jsonExportPath;
        public IReadOnlyList<RegistryItem> Items => items;

        // ══════════════════════════════════════════════════════════
        //  조회
        // ══════════════════════════════════════════════════════════

        public RegistryItem Find(int mainKey, int variantKey)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (!it.IsDeleted && it.MainKey == mainKey && it.VariantKey == variantKey)
                    return it;
            }
            return null;
        }

        public IEnumerable<RegistryItem> GetVariants(int mainKey)
        {
            var list = new List<RegistryItem>();
            foreach (var it in items)
                if (!it.IsDeleted && it.MainKey == mainKey) list.Add(it);
            list.Sort((a, b) => a.VariantKey.CompareTo(b.VariantKey));
            return list;
        }

        // ══════════════════════════════════════════════════════════
        //  편집 (에디터 전용)
        // ══════════════════════════════════════════════════════════
#if UNITY_EDITOR
        public RegistryItem AddItem()
        {
            var item = new RegistryItem
            {
                Name       = "",
                MainKey    = 0,
                VariantKey = 0,
                DlcKey     = dlcId,
                Usage      = PrefabUsage.MapEditor | PrefabUsage.Runtime,
                Category   = PrefabCategory.Other,
                SpawnMode  = PrefabSpawnMode.Single,
                Size       = new Vector2Int(1, 1),
                Offset     = Vector3.zero,
                RoadShape  = RoadShape.NotRoad,
            };
            items.Add(item);
            UnityEditor.EditorUtility.SetDirty(this);
            return item;
        }

        public void MarkDeleted(RegistryItem item)
        {
            if (item == null) return;
            item.IsDeleted = true;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        public void Restore(RegistryItem item)
        {
            if (item == null) return;
            item.IsDeleted = false;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        // ── JSON 내보내기 ──────────────────────────────────────────

        public bool ExportJson(out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(jsonExportPath))
            {
                error = "JsonExportPath is empty.";
                return false;
            }

            try
            {
                var data = new RegistryJsonData
                {
                    dlcId       = dlcId,
                    dlcName     = dlcName,
                    displayName = DisplayName,
                    items       = new List<RegistryItemJsonData>(),
                };

                foreach (var it in items)
                {
                    if (it.IsDeleted) continue;
                    data.items.Add(new RegistryItemJsonData
                    {
                        mainKey    = it.MainKey,
                        variantKey = it.VariantKey,
                        dlcKey     = it.DlcKey,
                        name       = it.Name,
                    });
                }

                string json = UnityEngine.JsonUtility.ToJson(data, prettyPrint: true);

                string dir = System.IO.Path.GetDirectoryName(jsonExportPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                System.IO.File.WriteAllText(jsonExportPath, json, System.Text.Encoding.UTF8);
                UnityEditor.AssetDatabase.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
#endif

        // ══════════════════════════════════════════════════════════
        //  단일 SO 검증
        // ══════════════════════════════════════════════════════════
        public List<ValidationIssue> Validate()
        {
            var issues   = new List<ValidationIssue>();
            var seenKeys = new HashSet<(int, int)>();

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.IsDeleted) continue;

                if (string.IsNullOrEmpty(it.Name))
                    issues.Add(new ValidationIssue(ValidationLevel.Warning, i,
                        $"({it.MainKey},{it.VariantKey}): Name is empty."));

                if (it.Prefab == null)
                    issues.Add(new ValidationIssue(ValidationLevel.Warning, i,
                        $"({it.MainKey},{it.VariantKey}): Prefab is null."));

                if (!seenKeys.Add((it.MainKey, it.VariantKey)))
                    issues.Add(new ValidationIssue(ValidationLevel.Error, i,
                        $"Duplicate key in this SO: ({it.MainKey}, {it.VariantKey})"));

                if (it.DlcKey != dlcId)
                    issues.Add(new ValidationIssue(ValidationLevel.Warning, i,
                        $"({it.MainKey},{it.VariantKey}): " +
                        $"DlcKey {it.DlcKey} differs from registry DlcId {dlcId}."));

                // Size 검사 (도로는 1x1 강제)
                if (it.RoadShape != RoadShape.NotRoad
                    && (it.Size.x != 1 || it.Size.y != 1))
                {
                    issues.Add(new ValidationIssue(ValidationLevel.Warning, i,
                        $"({it.MainKey},{it.VariantKey}): Road must have Size (1,1). Forced."));
                }

                // Size 양수 검사
                if (it.Size.x < 1 || it.Size.y < 1)
                    issues.Add(new ValidationIssue(ValidationLevel.Warning, i,
                        $"({it.MainKey},{it.VariantKey}): Size must be at least (1,1)."));
            }

            return issues;
        }

        // ══════════════════════════════════════════════════════════
        //  통합 검증
        // ══════════════════════════════════════════════════════════
        public static List<ValidationIssue> ValidateMerged(
            IEnumerable<GamePrefabRegistry> registries)
        {
            var issues = new List<ValidationIssue>();

            var allItems = new List<(RegistryItem item, GamePrefabRegistry src, int localIdx)>();
            foreach (var reg in registries)
            {
                if (reg == null) continue;
                for (int i = 0; i < reg.Items.Count; i++)
                {
                    var it = reg.Items[i];
                    if (!it.IsDeleted) allItems.Add((it, reg, i));
                }
            }

            // 1. (MainKey, VariantKey) 통합 중복
            var seenKeys = new Dictionary<(int, int), (string regName, int idx)>();
            foreach (var (it, src, li) in allItems)
            {
                var key = (it.MainKey, it.VariantKey);
                if (seenKeys.TryGetValue(key, out var prev))
                    issues.Add(new ValidationIssue(ValidationLevel.Error, li,
                        $"[Merged] Duplicate key ({it.MainKey},{it.VariantKey}): " +
                        $"'{src.DlcName}'[{li}] conflicts with '{prev.regName}'[{prev.idx}]."));
                else
                    seenKeys[key] = (src.DlcName, li);
            }

            // 2. VariantKey 순차성
            var variantsByMain = new Dictionary<int,
                List<(int vk, string regName, int idx)>>();

            foreach (var (it, src, li) in allItems)
            {
                if (!variantsByMain.TryGetValue(it.MainKey, out var list))
                    variantsByMain[it.MainKey] = list = new();
                list.Add((it.VariantKey, src.DlcName, li));
            }

            foreach (var kv in variantsByMain)
            {
                int mainKey  = kv.Key;
                var variants = kv.Value;
                variants.Sort((a, b) => a.vk.CompareTo(b.vk));

                if (variants[0].vk != 0)
                    issues.Add(new ValidationIssue(ValidationLevel.Warning, variants[0].idx,
                        $"[Merged] MainKey {mainKey}: VariantKey must start at 0 " +
                        $"(found {variants[0].vk} in '{variants[0].regName}')."));

                for (int i = 1; i < variants.Count; i++)
                {
                    int expected = variants[i - 1].vk + 1;
                    int actual   = variants[i].vk;
                    if (actual == expected) continue;

                    string missing = expected == actual - 1
                        ? $"{expected}" : $"{expected}~{actual - 1}";

                    issues.Add(new ValidationIssue(ValidationLevel.Warning, variants[i].idx,
                        $"[Merged] MainKey {mainKey}: VariantKey gap. " +
                        $"Expected {expected}, found {actual} " +
                        $"(in '{variants[i].regName}'). Missing: {missing}."));
                }
            }

            return issues;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  RegistryItem
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class RegistryItem
    {
        public string Name       = "";
        public int    MainKey;
        public int    VariantKey;
        public int    DlcKey;

        public GameObject Prefab;

        public PrefabSpawnMode SpawnMode = PrefabSpawnMode.Single;
        public PrefabUsage     Usage     = PrefabUsage.MapEditor | PrefabUsage.Runtime;
        public PrefabCategory  Category  = PrefabCategory.Other;

        // ── 사이즈 + 오프셋 ────────────────────────────────────────
        [Tooltip("점유할 셀 영역 (XZ). 도로는 1x1 강제.")]
        public Vector2Int Size = new Vector2Int(1, 1);

        [Tooltip("인스턴싱 시 위치에 더해질 오프셋. 살짝 띄우거나 보정용.")]
        public Vector3 Offset = Vector3.zero;

        // ── 도로 모양 ─────────────────────────────────────────────
        [Tooltip("도로면 모양 지정. 일반 프리팹은 NotRoad.")]
        public RoadShape RoadShape = RoadShape.NotRoad;

        // ── Multi 전용 ────────────────────────────────────────────
        public float MultiItemSize     = 0.5f;
        public int   MultiCountPerCell = 5;

        public bool IsDeleted;
    }

    // ══════════════════════════════════════════════════════════════
    //  JSON 직렬화
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public class RegistryJsonData
    {
        public int    dlcId;
        public string dlcName;
        public string displayName;
        public List<RegistryItemJsonData> items;
    }

    [Serializable]
    public class RegistryItemJsonData
    {
        public int    mainKey;
        public int    variantKey;
        public int    dlcKey;
        public string name;
    }

    // ══════════════════════════════════════════════════════════════
    //  Enums
    // ══════════════════════════════════════════════════════════════

    public enum PrefabSpawnMode : byte { Single, Multi }

    [Flags]
    public enum PrefabUsage : byte
    {
        None       = 0,
        MapEditor  = 1 << 0,
        Runtime    = 1 << 1,
        StartPoint = 1 << 2,
        Campaign   = 1 << 3,
    }

    public enum PrefabCategory : byte
    {
        Terrain, Road, Building, Other,
    }

    /// <summary>
    /// 도로 모양. 5종류 + NotRoad.
    /// 비트마스크와의 매핑은 RoadShapeMapping에서 처리.
    /// </summary>
    public enum RoadShape : byte
    {
        NotRoad   = 0,    // 도로 아님 (일반 프리팹)
        Straight  = 1,    // 직선 (N+S 또는 E+W)
        DeadEnd   = 2,    // 막다른 길 (한 방향)
        Corner    = 3,    // ㄱ자 (두 인접 방향)
        T         = 4,    // T자 (3방향)
        Cross     = 5,    // + (4방향)
    }

    public enum ValidationLevel { Info, Warning, Error }

    public struct ValidationIssue
    {
        public ValidationLevel Level;
        public int             Index;
        public string          Message;

        public ValidationIssue(ValidationLevel level, int index, string msg)
        {
            Level = level; Index = index; Message = msg;
        }
    }
}
