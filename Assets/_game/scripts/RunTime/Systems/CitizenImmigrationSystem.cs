using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CitizenImmigrationSystem — 시민 유입 (초기 스폰 트리거)
    // ──────────────────────────────────────────────────────────────────────────
    //  "빈 거주 정원이 곧 유입 수요": 거주건물의 빈자리(Capacity−Current)만큼 시민이
    //  이민으로 들어온다. 게임-시간(HourChanged)마다 owner당 상한(CitizenConfig)으로
    //  점진 유입 → 집 짓는 속도에 인구가 따라붙고, 파괴로 집을 잃으면 유입이 멈춘다.
    //  인구가 실존하게 되므로 TerritorySystem의 인구원도 Capacity(잠재)에서
    //  Current(실제)로 자연 전환된다(공식 동일: Current>0 ? Current : Capacity).
    //
    //  회계: 거주 정원(Current)은 '배정 시점'에 예약된다(CitizenAssignmentSystem).
    //  이미 스폰됐지만 아직 집을 못 받은 대기자(UnassignedTag + Home==Null)를 빼고
    //  스폰해야 과잉 유입이 없다: want = min(시간당 상한, 빈 정원 합 − 대기자 수).
    //  (집은 있고 직장만 없는 시민은 태그가 남지만 정원을 이미 예약 → 대기자 아님.)
    //
    //  발행: SpawnCitizenRequest → CitizenSpawnSystem([UpdateBefore]로 같은 프레임 소비)
    //  → CitizenAssignmentSystem이 집·직장 배정. 스폰 위치 = 빈자리 있는 거주건물
    //  footprint 중심 셀(현재 이동이 타이머 전용이라 코스메틱 — 배정 시 그 집에 앉음).
    //
    //  ※ 메인스레드 사유: 게임-시간당 1회 + 건물/대기자 수백 순회 + 요청 몇 개 생성.
    //    이벤트성 소량 — CLAUDE.md "소수 엔티티 갱신은 ECB 단일 패스로 충분" 예외.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(CitizenSpawnSystem))]
    public partial struct CitizenImmigrationSystem : ISystem
    {
        const int MP = 8;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!clock.HourChanged) return;

            int perHour = (SystemAPI.TryGetSingleton<CitizenConfig>(out var cfg)
                ? cfg : CitizenConfig.Default).ImmigrantsPerHourPerPlayer;
            if (perHour <= 0) return;

            float cs = SystemAPI.TryGetSingleton<GridSettings>(out var gs) ? gs.CellSize : 1f;
            if (cs <= 0f) cs = 1f;

            // ── owner별 빈 정원 합 + 스폰 후보(빈자리 있는 거주건물) ────────────
            var freeCap = new NativeArray<int>(MP, Allocator.Temp);
            var slots   = new NativeList<HomeSlot>(64, Allocator.Temp);
            foreach (var (occ, bf) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                              .WithAll<ResidenceBuilding>())
            {
                int owner = bf.ValueRO.OwnerLocalId;
                if ((uint)owner >= MP) continue;
                int free = occ.ValueRO.Capacity - occ.ValueRO.Current;
                if (free <= 0) continue;
                freeCap[owner] += free;
                int2 eff = EntranceOps.RotateSize(bf.ValueRO.Size, bf.ValueRO.RotSteps);
                slots.Add(new HomeSlot
                { Owner = owner, Center = bf.ValueRO.Origin + eff / 2, Free = free });
            }
            if (slots.IsEmpty) { freeCap.Dispose(); slots.Dispose(); return; }

            // ── owner별 대기자(스폰됐지만 집 미배정 — 다음 배정에서 정원을 차지할 인원) ──
            var pending = new NativeArray<int>(MP, Allocator.Temp);
            foreach (var (res, owner) in
                     SystemAPI.Query<RefRO<CitizenResidence>, CitizenOwner>()
                              .WithAll<CitizenTag, UnassignedTag>())
            {
                if (res.ValueRO.Home != Entity.Null) continue;   // 집 있음(직장 대기) → 정원 예약됨
                if ((uint)owner.LocalId < MP) pending[owner.LocalId]++;
            }

            // ── 발행: owner당 want = min(상한, 빈 정원 − 대기자) 를 건물 순서로 분배 ──
            var want = new NativeArray<int>(MP, Allocator.Temp);
            for (int o = 0; o < MP; o++)
                want[o] = math.min(perHour, freeCap[o] - pending[o]);

            var ecb  = new EntityCommandBuffer(Allocator.Temp);
            int hour = (int)(clock.DayProgress01 * 24f);
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                int n = math.min(want[s.Owner], s.Free);
                for (int k = 0; k < n; k++)
                {
                    var e = ecb.CreateEntity();
                    ecb.AddComponent(e, new SpawnCitizenRequest
                    {
                        LocalId    = s.Owner,
                        Position   = new float3((s.Center.x + 0.5f) * cs, 0f, (s.Center.y + 0.5f) * cs),
                        Seed       = math.hash(new int4(s.Center, clock.Day * 24 + hour, k + 1)) | 1u,
                        InitialJob = JobType.Unemployed,
                    });
                }
                if (n > 0) want[s.Owner] -= n;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            freeCap.Dispose(); pending.Dispose(); slots.Dispose(); want.Dispose();
        }

        struct HomeSlot { public int Owner; public int2 Center; public int Free; }
    }
}
