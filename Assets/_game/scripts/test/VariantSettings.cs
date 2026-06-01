using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  SlotController  — 베리언트 적용 대상 플래그
    //
    //  베리언트 선택 창(VariantSelectionWindow)에서
    //  개별 유닛/건물의 외형을 누구에게 적용할지 지정.
    //
    //  User | AI 동시 선택 가능 (Both).
    // ══════════════════════════════════════════════════════════════
    [Flags]
    public enum SlotController : byte
    {
        None = 0,
        User = 1 << 0,  // 플레이어(유저) 조작 유닛·건물
        AI   = 1 << 1,  // AI 조작 유닛·건물
        Both = User | AI,
    }

    // ══════════════════════════════════════════════════════════════
    //  VariantEntry  — 유닛 1개 그룹의 베리언트 설정
    //
    //  같은 MainKey에 대해 User / AI 가 서로 다른 VariantKey를
    //  사용할 수 있으므로, 항목이 두 개가 될 수 있다.
    //  (예: User=V2, AI=V1 → 각각 별도 VariantEntry)
    // ══════════════════════════════════════════════════════════════
    [Serializable]
    public struct VariantEntry
    {
        /// <summary>유닛/건물 종류 식별자.</summary>
        public int            MainKey;
        /// <summary>선택한 외형 번호. 0 = 기본.</summary>
        public int            VariantKey;
        /// <summary>이 설정을 적용할 컨트롤러 (User / AI / Both).</summary>
        public SlotController ApplyTo;
    }

    // ══════════════════════════════════════════════════════════════
    //  VariantSettings  (ScriptableObject)
    //
    //  세션 단위 베리언트 설정.
    //  VariantSelectionWindow에서 편집 → SkirmishLobby가 참조.
    //  유저가 변경하지 않는 한 기본값(VariantKey=0)을 따른다.
    //
    //  메뉴: Assets > Create > CitySim > Variant Settings
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Variant Settings",
        fileName = "VariantSettings")]
    public class VariantSettings : ScriptableObject
    {
        public List<VariantEntry> Entries = new();

        // ── 조회 ─────────────────────────────────────────────────

        /// <summary>
        /// mainKey / who 에 맞는 VariantKey 반환.
        /// 일치하는 항목이 없으면 0 (기본 외형).
        /// </summary>
        public int Resolve(int mainKey, SlotController who)
        {
            foreach (var e in Entries)
                if (e.MainKey == mainKey && (e.ApplyTo & who) != 0)
                    return e.VariantKey;
            return 0;
        }

        // ── 편집 ─────────────────────────────────────────────────

        /// <summary>
        /// mainKey + applyTo 조합의 VariantKey 설정.
        ///
        /// 기존에 동일 applyTo가 포함된 항목이 있으면 해당 컨트롤러를
        /// 분리(remaining)하거나 제거한 뒤 새 항목 추가.
        /// variantKey = 0이면 기본값이므로 항목을 저장하지 않는다.
        /// </summary>
        public void Set(int mainKey, int variantKey, SlotController applyTo)
        {
            // 겹치는 기존 항목 정리
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                var e = Entries[i];
                if (e.MainKey != mainKey) continue;

                var overlap   = e.ApplyTo & applyTo;
                if (overlap == SlotController.None) continue;

                var remaining = e.ApplyTo & ~applyTo;
                if (remaining == SlotController.None)
                    Entries.RemoveAt(i);
                else
                    Entries[i] = new VariantEntry
                    {
                        MainKey    = e.MainKey,
                        VariantKey = e.VariantKey,
                        ApplyTo    = remaining,
                    };
            }

            // 기본값(0)은 저장하지 않음 — Resolve에서 기본 반환
            if (applyTo != SlotController.None && variantKey != 0)
            {
                Entries.Add(new VariantEntry
                {
                    MainKey    = mainKey,
                    VariantKey = variantKey,
                    ApplyTo    = applyTo,
                });
            }
        }

        /// <summary>mainKey 의 모든 항목 제거 (기본값으로 리셋).</summary>
        public void Clear(int mainKey)
            => Entries.RemoveAll(e => e.MainKey == mainKey);

        /// <summary>전체 초기화.</summary>
        public void ClearAll()
            => Entries.Clear();
    }

    // ══════════════════════════════════════════════════════════════
    //  VariantProfile  (ECS 싱글톤)
    //
    //  VariantSettings SO → 게임 시작(SkirmishLobby) 시 변환.
    //  User / AI 각각 NativeHashMap<MainKey, VariantKey> 보관.
    //
    //  흐름:
    //    VariantSelectionWindow → VariantSettings SO (저장)
    //    SkirmishLobby → VariantProfile 엔티티 생성
    //    FactionBaseSpawnSystem / SpawnByNeedSystem 등 → Resolve 호출
    // ══════════════════════════════════════════════════════════════
    public struct VariantProfile : IComponentData
    {
        /// <summary>User 조작 유닛·건물: MainKey → VariantKey.</summary>
        public NativeHashMap<int, int> UserTable;
        /// <summary>AI 조작 유닛·건물: MainKey → VariantKey.</summary>
        public NativeHashMap<int, int> AITable;

        /// <summary>
        /// mainKey + who 에 맞는 VariantKey 반환.
        /// 테이블에 없으면 0 (기본 외형).
        /// </summary>
        public int Resolve(int mainKey, SlotController who)
        {
            if ((who & SlotController.User) != 0 &&
                UserTable.TryGetValue(mainKey, out int uVk))
                return uVk;

            if ((who & SlotController.AI) != 0 &&
                AITable.TryGetValue(mainKey, out int aVk))
                return aVk;

            return 0;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  VariantProfileSystem  — VariantProfile 생명주기 관리
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct VariantProfileSystem : ISystem
    {
        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<VariantProfile>()) return;
            var p = SystemAPI.GetSingleton<VariantProfile>();
            if (p.UserTable.IsCreated) p.UserTable.Dispose();
            if (p.AITable.IsCreated)   p.AITable.Dispose();
        }
    }
}
