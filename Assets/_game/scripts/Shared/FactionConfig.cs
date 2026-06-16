using Unity.Collections;
using Unity.Entities;

namespace CitySim
{
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
    //  FactionConfigSystem  — FactionConfig 생명주기 관리
    //
    //  NativeHashMap은 베이킹 시 직렬화 불가(포인터 포함)하므로
    //  Baker가 아닌 여기 OnCreate에서 코드로 싱글톤을 생성한다.
    //  (GridInitSystem/StampInitSystem과 동일한 패턴.)
    // ══════════════════════════════════════════════════════════════
    [Unity.Entities.UpdateInGroup(typeof(Unity.Entities.InitializationSystemGroup))]
    public partial struct FactionConfigSystem : Unity.Entities.ISystem
    {
        public void OnCreate(ref Unity.Entities.SystemState state)
        {
            if (SystemAPI.HasSingleton<FactionConfig>()) return;

            var config = new FactionConfig
            {
                // 최대 8팀. 실제 FactionId는 SkirmishLobby에서 채움.
                Slots = new NativeHashMap<int, FactionSlot>(8, Allocator.Persistent),
            };

            // 기본값: 전부 미배정 (-1)
            for (int i = 0; i < 8; i++)
                config.Slots[i] = new FactionSlot { FactionId = -1 };

            var e = state.EntityManager.CreateEntity(typeof(FactionConfig));
            state.EntityManager.SetComponentData(e, config);
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
