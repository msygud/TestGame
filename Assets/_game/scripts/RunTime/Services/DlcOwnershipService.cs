using System.Collections.Generic;
using System.Linq;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  DlcOwnershipService
    //
    //  DLC 보유 여부를 판단하는 서비스.
    //
    //  현재: 로컬 플래그 기반 스텁.
    //  실배포: Steam/Epic 등 플랫폼 SDK 콜백으로 교체.
    //
    //  교체 지점은 RegisterOwned() 하나뿐이므로
    //  나머지 코드 변경 없이 SDK 연동 가능.
    //
    //  DlcId 규칙:
    //    0 = Origin (본편, 항상 보유)
    //    1+ = DLC (GamePrefabRegistry.DlcId와 동일)
    // ══════════════════════════════════════════════════════════════
    public static class DlcOwnershipService
    {
        // Origin(0)은 항상 보유
        static readonly HashSet<int> _owned = new() { 0 };

        // ── 조회 ─────────────────────────────────────────────────────

        /// <summary>단일 DLC 보유 여부.</summary>
        public static bool Owns(int dlcId) => _owned.Contains(dlcId);

        /// <summary>
        /// 지정한 DLC를 모두 보유하는가.
        /// null 또는 빈 목록은 조건 없음으로 간주 → true.
        /// </summary>
        public static bool CanAccess(IEnumerable<int> requiredDlcIds)
        {
            if (requiredDlcIds == null) return true;
            foreach (int id in requiredDlcIds)
                if (!_owned.Contains(id)) return false;
            return true;
        }

        /// <summary>
        /// 미보유 DLC 목록 반환 (UI 안내용).
        /// </summary>
        public static List<int> MissingDlcs(IEnumerable<int> required)
        {
            var missing = new List<int>();
            if (required == null) return missing;
            foreach (int id in required)
                if (!_owned.Contains(id)) missing.Add(id);
            return missing;
        }

        /// <summary>현재 보유 중인 DLC ID 읽기 전용 집합.</summary>
        public static IReadOnlyCollection<int> OwnedDlcIds => _owned;

        // ── 등록 (스텁 → SDK 교체 지점) ─────────────────────────────

        /// <summary>
        /// DLC 보유 등록.
        /// 스텁: 직접 호출.
        /// 실배포: 플랫폼 SDK 콜백에서 호출.
        /// </summary>
        public static void RegisterOwned(int dlcId) => _owned.Add(dlcId);

        /// <summary>여러 DLC 일괄 등록.</summary>
        public static void RegisterOwned(IEnumerable<int> dlcIds)
        {
            foreach (int id in dlcIds) _owned.Add(id);
        }

        /// <summary>보유 DLC 초기화 (테스트용). Origin(0)은 유지.</summary>
        public static void Reset()
        {
            _owned.Clear();
            _owned.Add(0);
        }
    }
}
