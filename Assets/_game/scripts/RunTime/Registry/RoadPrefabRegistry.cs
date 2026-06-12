using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadPrefabRegistry — 도로 전용 (FactionId, dirMask) → MainKey 매핑 SO
    //
    //  도로는 일반 프리팹 조회(욕구→MainKey)와 다른 경로를 쓴다.
    //    · 의미는 하나("도로")인데 형태가 dirMask 1~15로 여럿이고,
    //    · 형태(dirMask)는 의도가 아니라 실제 연결 상태에서 RoadSystem이 계산한다.
    //  그래서 "팩션+방향 → MainKey"를 이 SO에서 직접 매핑해 둔다.
    //
    //  ── 흐름 ──
    //    RoadSystem이 셀의 dirMask 계산
    //      → 이 레지스트리에서 (FactionId, dirMask) → MainKey 조회
    //      → 기존 PrefabLookup.Get(MainKey, VariantKey)로 최종 프리팹 인스턴스
    //        (여기서부터는 일반 프리팹과 완전히 동일한 경로)
    //
    //  ── 설계 메모 ──
    //    · MainKey는 반드시 MainKeyRange.Road(1~999) 범위. Validate에서 강제.
    //    · dirMask 0(None)은 도로 아님 → 매핑하지 않는다. 1~15만 유효.
    //    · 같은 팩션의 도로 15형태는 서로 다른 MainKey를 가질 수 있고(형태별 프리팹),
    //      VariantKey는 외형 베리언트 자리로 그대로 남는다(충돌 없음).
    //    · 팩션이 늘면 Entries에 행만 추가. 시스템 코드 변경 불필요.
    // ══════════════════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        fileName = "RoadPrefabRegistry",
        menuName = "CitySim/Road Prefab Registry")]
    public class RoadPrefabRegistry : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("0 = 공통(모든 팩션 폴백), 1~8 = 개별 팩션")]
            public int FactionId;

            [Tooltip("도로 방향 비트마스크 (1~15). N=1,E=2,S=4,W=8 조합.")]
            public RoadDir Dir;

            [Tooltip("이 (팩션,방향)에 대응하는 도로 MainKey. Road 범위(1~999).")]
            public int MainKey;

            [Tooltip("메모용 이름 (선택)")]
            public string Note;
        }

        [Tooltip("팩션·방향당 MainKey 매핑. 인스펙터에서 직접 채운다.")]
        public List<Entry> Entries = new();

        // ── 검증 ────────────────────────────────────────────────────
        /// <summary>정합성 검사. 이슈 목록 반환 (에디터 윈도우에서 표시).</summary>
        public List<ValidationIssue> Validate()
        {
            var issues = new List<ValidationIssue>();
            var seen   = new HashSet<(int, RoadDir)>();

            foreach (var e in Entries)
            {
                // dirMask 유효성: 1~15만
                if (e.Dir == RoadDir.None)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Error,
                        Message = $"(Faction={e.FactionId}) Dir=None(0)은 도로가 아님. 1~15만 매핑.",
                    });
                }

                // MainKey가 Road 범위인지 강제
                if (e.MainKey != MainKeyRange.NullKey
                    && !MainKeyRange.IsInRange(e.MainKey, PrefabCategory.Road))
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Error,
                        Message = $"(Faction={e.FactionId}, Dir={e.Dir}) MainKey={e.MainKey}: "
                                + $"Road 범위[{MainKeyRange.RoadMin}~{MainKeyRange.RoadMax}] 위반.",
                    });
                }

                if (e.MainKey == MainKeyRange.NullKey)
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Warning,
                        Message = $"(Faction={e.FactionId}, Dir={e.Dir}) MainKey=0(무효). 실제 키를 지정하세요.",
                    });
                }

                // (팩션,방향) 중복
                var key = (e.FactionId, e.Dir);
                if (!seen.Add(key))
                {
                    issues.Add(new ValidationIssue
                    {
                        Level   = ValidationLevel.Error,
                        Message = $"(Faction={e.FactionId}, Dir={e.Dir}) 중복 매핑.",
                    });
                }
            }
            return issues;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RoadKeyLookup — 런타임 도로 키 룩업 (ECS 싱글톤)
    //
    //  Key   : (FactionId, dirMask) 를 하나의 int로 패킹
    //  Value : MainKey
    //
    //  도로 시스템이 dirMask 계산 후 이 룩업으로 MainKey를 얻고,
    //  그 MainKey를 PrefabLookup으로 넘긴다.
    //  (FactionId, dirMask) 미발견 시 FactionId=0(공통) 폴백을 시도.
    // ══════════════════════════════════════════════════════════════════════════
    public struct RoadKeyLookup : IComponentData
    {
        /// <summary>packed(FactionId, dirMask) → MainKey</summary>
        public NativeHashMap<int, int> Table;

        /// <summary>(FactionId, dirMask)를 단일 정수 키로 패킹. dir는 0~15.</summary>
        public static int Pack(int factionId, RoadDir dir)
            => (factionId << 4) | ((int)dir & 0xF);

        /// <summary>
        /// (FactionId, dirMask) → MainKey. 없으면 Faction 0(공통) 폴백.
        /// 둘 다 없으면 false.
        /// </summary>
        public bool TryGet(int factionId, RoadDir dir, out int mainKey)
        {
            if (Table.TryGetValue(Pack(factionId, dir), out mainKey)) return true;
            if (factionId != 0 && Table.TryGetValue(Pack(0, dir), out mainKey)) return true;
            mainKey = 0;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  베이크 전달용 버퍼 — SO → ECS 서브씬 경유
    //
    //  RoadKeyAuthoring이 RoadPrefabRegistry SO의 Entries를 이 버퍼로 구워 넣고,
    //  RoadKeyBuildSystem이 게임 시작 시 읽어 RoadKeyLookup 싱글톤을 구성한다.
    //  (NeedMapping 베이킹 패턴과 동일.)
    // ══════════════════════════════════════════════════════════════════════════
    [InternalBufferCapacity(64)]
    public struct BakedRoadKey : IBufferElementData
    {
        public int     FactionId;
        public RoadDir  Dir;
        public int      MainKey;
    }
}
