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
    //  팩션 메타 정보 (이름/색상).
    //  프리팹 목록은 NeedMappingEntry(FactionFlags)로 관리.
    //  베리언트 설정은 VariantSettings SO + VariantProfile로 분리.
    //
    //  메뉴: Assets > Create > CitySim > Faction Definition
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Faction Definition",
        fileName = "FactionDef_New")]
    public class FactionDefinition : ScriptableObject
    {
        [Tooltip("전역 고유 팩션 ID.\n" +
                 "NeedMappingEntry.FactionFlags 및\n" +
                 "FactionBaseDefinition.FactionId와 반드시 일치.")]
        public int FactionId;

        [Tooltip("이 팩션이 속한 DLC 식별자.")]
        public int DlcId = 0;

        [Tooltip("팩션 이름 (UI 표시용).")]
        public string FactionName = "New Faction";

        [Tooltip("팩션 대표 색상 (미니맵/팀 표시).")]
        public Color FactionColor = Color.white;

        // VariantKey는 VariantSettings SO + VariantProfile로 이관.
        // (VariantSelectionWindow에서 유닛별 User/AI 독립 설정)
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionConfig  (ECS 싱글톤)
    //
    //  게임 시작(로비) 시 확정된 팀→팩션 배정 정보를 보관.
    //  key   = TeamIndex (0~7)
    //  value = FactionSlot (FactionId)
    //
    //  VariantKey는 VariantProfile 싱글톤으로 분리됨.
    //  FactionBaseSpawnSystem / NeedResolver 등이 FactionId 조회에 사용.
    // ══════════════════════════════════════════════════════════════
    public struct FactionConfig : IComponentData
    {
        /// <summary>
        /// TeamIndex → FactionSlot 매핑.
        /// 게임 시작(SkirmishLobby.CreateEntities) 시 채워진다.
        /// </summary>
        public NativeHashMap<int, FactionSlot> Slots;
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionSlot  — 팀 1개의 팩션 배정 정보
    // ══════════════════════════════════════════════════════════════
    public struct FactionSlot
    {
        /// <summary>배정된 팩션 ID. -1 = 미배정.</summary>
        public int FactionId;
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionConfigAuthoring
    // ══════════════════════════════════════════════════════════════
    public class FactionConfigAuthoring : UnityEngine.MonoBehaviour
    {
        [Tooltip("프로젝트의 모든 FactionDefinition SO 목록.\n" +
                 "Baker가 참조만 하며, 실제 슬롯 값은 SkirmishLobby가 런타임에 채운다.")]
        public FactionDefinition[] FactionDefinitions;

        class Baker : Unity.Entities.Baker<FactionConfigAuthoring>
        {
            public override void Bake(FactionConfigAuthoring authoring)
            {
                var e      = GetEntity(TransformUsageFlags.None);
                var config = new FactionConfig
                {
                    // 최대 8팀. 실제 FactionId는 SkirmishLobby에서 채움.
                    Slots = new NativeHashMap<int, FactionSlot>(8, Allocator.Persistent),
                };

                // 기본값: 전부 미배정 (-1)
                for (int i = 0; i < 8; i++)
                    config.Slots[i] = new FactionSlot { FactionId = -1 };

                AddComponent(e, config);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  FactionConfigSystem  — FactionConfig 생명주기 관리
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
