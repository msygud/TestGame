using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  FactionDefinition  (ScriptableObject)
    //
    //  팩션 메타 정보 (이름/색상/기본 VariantKey).
    //  기존 FactionRegistry의 팩션 식별 부분만 분리.
    //  프리팹 목록은 UnifiedPrefabRegistry로 이동.
    //
    //  메뉴: Assets > Create > CitySim > Faction Definition
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Faction Definition",
        fileName = "FactionDef_New")]
    public class FactionDefinition : ScriptableObject
    {
        [Tooltip("전역 고유 팩션 ID. UnifiedPrefabRegistry의 FactionId와 매핑.")]
        public int FactionId;

        [Tooltip("DLC 식별자.")]
        public int DlcId = 0;

        [Tooltip("팩션 이름.")]
        public string FactionName = "New Faction";

        [Tooltip("팩션 대표 색상 (미니맵/UI).")]
        public Color FactionColor = Color.white;

        [Tooltip("이 팩션의 기본 VariantKey.\n" +
                 "게임 시작 시 FactionConfig 싱글톤에 등록되어\n" +
                 "NeedResolver가 프리팹 선택 시 참조.")]
        public int DefaultVariantKey = 0;
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionConfig  (ECS 싱글톤)
    //
    //  게임 시작(로비) 시 확정된 팩션 설정을 보관.
    //  팀 인덱스 → (FactionId, VariantKey) 매핑.
    //
    //  NeedResolver가 프리팹 선택 시 이 싱글톤을 참조해
    //  현재 팩션의 VariantKey를 결정.
    // ══════════════════════════════════════════════════════════════
    public struct FactionConfig : IComponentData
    {
        /// <summary>
        /// key   = TeamIndex (0~7)
        /// value = FactionSlot (FactionId + VariantKey)
        /// 게임 시작 시 로비 데이터로 채워짐.
        /// </summary>
        public NativeHashMap<int, FactionSlot> Slots;
    }

    public struct FactionSlot
    {
        public int FactionId;    // -1 = 미배정
        public int VariantKey;   // 게임 시작 시 고정
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionConfigAuthoring
    // ══════════════════════════════════════════════════════════════
    public class FactionConfigAuthoring : UnityEngine.MonoBehaviour
    {
        [Tooltip("프로젝트의 모든 FactionDefinition SO 목록.")]
        public FactionDefinition[] FactionDefinitions;

        class Baker : Unity.Entities.Baker<FactionConfigAuthoring>
        {
            public override void Bake(FactionConfigAuthoring authoring)
            {
                var e      = GetEntity(TransformUsageFlags.None);
                var config = new FactionConfig
                {
                    // 최대 8팀. 실제 값은 로비에서 채움.
                    Slots = new NativeHashMap<int, FactionSlot>(8, Allocator.Persistent),
                };

                // 기본값으로 미배정 초기화
                for (int i = 0; i < 8; i++)
                    config.Slots[i] = new FactionSlot { FactionId = -1, VariantKey = 0 };

                AddComponent(e, config);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionConfigSystem  — 생명주기 관리
    // ══════════════════════════════════════════════════════════════
    [Unity.Entities.UpdateInGroup(typeof(Unity.Entities.InitializationSystemGroup))]
    public partial struct FactionConfigSystem : Unity.Entities.ISystem
    {
        public void OnCreate(ref Unity.Entities.SystemState state)
        {
            state.RequireForUpdate<FactionConfig>();
        }

        public void OnUpdate(ref Unity.Entities.SystemState state) { }

        public void OnDestroy(ref Unity.Entities.SystemState state)
        {
            if (!SystemAPI.HasSingleton<FactionConfig>()) return;
            var config = SystemAPI.GetSingleton<FactionConfig>();
            if (config.Slots.IsCreated) config.Slots.Dispose();
        }
    }
}
