using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  LookupBuildSystem
    //
    //  처리(다지기 ②③④, 2026-07-11 개편 — "자동생성 + 얇은 오버라이드"):
    //  1) BakedNeedMapping 버퍼(SO NeedMaps[] — **명시 오버라이드**) 수집 + Validate
    //     (행의 MainKey 프리팹이 공급 능력 ⊇ NeedMask인지 교차검증 — CLAUDE.md 오픈 이슈).
    //  2) **자동 파생**: PrefabLookup의 프리팹 엔티티 능력(StampSupplier/AuraSupplier의
    //     Relief — 다지기 ①이 베이크)에서 need비트→MainKey 행 생성. 능력이 곧 매핑의
    //     원천이므로 SO 없이도 L2가 성립(이중 출처 해소 — SO 행은 예외 지정용만 남음).
    //     같은 스캔에서 ProducerLookup(commodity→생산자, 창고 키)도 파생(다지기 ③④).
    //  3) 팩션 엔티티마다 NeedLookupL2 부착 — 명시 행 먼저(우선), 자동 행이 빈틈 채움.
    //     - FactionFlags==0 항목 → 모든 팩션에 복사 / !=0 → 해당 팩션 비트만.
    //     - 같은 비트 경합: 명시 > 자동, 자동끼리는 MainKey 오름차순 선승(결정적).
    //
    //  실행 모델(2026-07-11 픽스 2건):
    //   · "1회 실행 후 비활성화" 은퇴 — 팩션 엔티티가 없으면 영영 잠들어 ProducerLookup까지
    //     죽었다(유저 실측: 로그 부재). L2는 "L2 없는 팩션"이 나타날 때마다 부착.
    //   · **스트리밍 레이스 픽스** — PrefabLookup 싱글톤은 프레임 0에 빈 채로 생성되고
    //     서브씬 BakedPrefabEntry가 뒤에 채운다(유저 실측: 전부 0 동결). 등록 수(Table.Count)가
    //     지난 빌드와 다르면 **전체 재파생**(ProducerLookup 교체 + 기존 팩션 L2 재구성) —
    //     서브씬/DLC가 몇 배치로 들어와도 최종 상태로 수렴. 평시 검사 비용 = 카운트 비교뿐.
    //
    //  L1은 PrefabLookup 싱글톤(PrefabLookupBuildSystem이 이미 구성)을 그대로 공유.
    //  L1과 L2는 서로를 전혀 모른다 — 조합은 LookupHelper만 안다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(PrefabLookupBuildSystem))]
    public partial struct LookupBuildSystem : ISystem
    {
        EntityQuery _pendingFactions;   // FactionId 있고 NeedLookupL2 없는 팩션(부착 대기)
        int _builtPrefabCount;          // 지난 빌드 시점의 PrefabLookup 등록 수(변화 = 재파생)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PrefabLookup>();
            _pendingFactions = SystemAPI.QueryBuilder()
                .WithAll<FactionId>().WithNone<NeedLookupL2>().Build();
            _builtPrefabCount = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── 할 일 검사(상시, 저렴) ──
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            int prefabCount = lookup.Table.IsCreated ? lookup.Table.Count : 0;
            if (prefabCount == 0) return;   // 레지스트리 스트리밍 전 — 빈 스캔 동결 금지

            bool needProducer   = !SystemAPI.HasSingleton<ProducerLookup>();
            bool needFactions   = !_pendingFactions.IsEmpty;
            bool prefabsChanged = prefabCount != _builtPrefabCount;
            if (!needProducer && !needFactions && !prefabsChanged) return;
            _builtPrefabCount = prefabCount;

            var em  = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // ── 1) 명시 행(BakedNeedMapping — SO NeedMaps[]) 수집 ─────────────
            var commonEntries  = new NativeList<BakedNeedMapping>(32, Allocator.Temp);
            var factionEntries = new NativeList<BakedNeedMapping>(64, Allocator.Temp);

            foreach (var buf in SystemAPI.Query<DynamicBuffer<BakedNeedMapping>>())
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    var entry = buf[i];
                    if (entry.FactionFlags == 0) commonEntries.Add(entry);
                    else                          factionEntries.Add(entry);
                }
            }

            // ── 2) 프리팹 능력 수집(MainKey → relief 비트합) — 자동 파생 + Validate 공용 ──
            //   능력은 per-MainKey(변종 동일 규약)이므로 MainKey당 첫 변종만 읽는다.
            var capability = new NativeHashMap<int, ulong>(128, Allocator.Temp);
            var production = new NativeHashMap<int, int>(128, Allocator.Temp);   // MainKey → (int)Commodity
            var auraKeys   = new NativeHashMap<int, int>(16, Allocator.Persistent);   // 오라 키→반경(③④와 동거)
            var workerNeeds = new NativeHashMap<int, int>(32, Allocator.Persistent);  // 키→근무 정원(노동 게이트)
            int warehouseKey = 0;                                                 // WarehouseTag 보유 최소 키
            foreach (var kv in lookup.Table)
            {
                int mainKey = kv.Key.x;
                if (capability.ContainsKey(mainKey)) continue;
                ulong relief = 0;
                if (em.HasComponent<StampSupplier>(kv.Value))
                    relief |= (ulong)em.GetComponentData<StampSupplier>(kv.Value).Relief;
                if (em.HasComponent<AuraSupplier>(kv.Value))
                {
                    var auraCap = em.GetComponentData<AuraSupplier>(kv.Value);
                    relief |= (ulong)auraCap.Relief;
                    auraKeys.TryAdd(mainKey, auraCap.Radius);
                }
                // 근무 정원 파생(노동 게이트, 2026-07-17) — 유인 건물만(무인 = 미등록).
                if (em.HasComponent<WorkplaceBuilding>(kv.Value)
                    && em.HasComponent<BuildingOccupancy>(kv.Value))
                    workerNeeds.TryAdd(mainKey,
                        em.GetComponentData<BuildingOccupancy>(kv.Value).Capacity);
                capability.TryAdd(mainKey, relief);

                // 다지기 ③·④ 파생 원천: 생산 출력 / 창고 여부(능력 컴포넌트).
                if (em.HasComponent<ProductionJob>(kv.Value))
                {
                    var pj = em.GetComponentData<ProductionJob>(kv.Value);
                    if (pj.RecipeOutput != Commodity.None)
                        production.TryAdd(mainKey, (int)pj.RecipeOutput);
                }
                if (em.HasComponent<WarehouseTag>(kv.Value)
                    && (warehouseKey == 0 || mainKey < warehouseKey))
                    warehouseKey = mainKey;
            }

            // ── 2-b) Validate(다지기 ②): 명시 행의 MainKey가 능력 ⊇ NeedMask인 프리팹을
            //   갖는지 교차검증. 실패해도 행은 적용(데이터 수정은 유저 몫) — 에러로 가시화.
            ValidateEntries(in commonEntries,  in capability);
            ValidateEntries(in factionEntries, in capability);

            // ── 2-c) 자동 파생 행: 능력의 각 비트 → (1<<bit) → MainKey. MainKey 오름차순(결정적).
            //   + 같은 정렬 패스에서 commodity→producer 테이블(다지기 ③)도 구성.
            var autoRows      = new NativeList<BakedNeedMapping>(64, Allocator.Temp);
            var producerTable = new NativeHashMap<int, int>(16, Allocator.Persistent);
            {
                var keys = capability.GetKeyArray(Allocator.Temp);
                keys.Sort();
                for (int i = 0; i < keys.Length; i++)
                {
                    ulong relief = capability[keys[i]];
                    while (relief != 0)
                    {
                        int bit = math.tzcnt(relief);
                        relief &= relief - 1;
                        if (bit >= 32) continue;   // L2 키는 uint — 상위 비트는 테이블 확장 때
                        autoRows.Add(new BakedNeedMapping
                        { MainKey = keys[i], NeedMask = 1u << bit, FactionFlags = 0 });
                    }
                    if (production.TryGetValue(keys[i], out int cmd))
                        producerTable.TryAdd(cmd, keys[i]);   // 복수 생산자 = 최소 키 선승(결정적)
                }
                keys.Dispose();
            }

            // ── 2-d) ProducerLookup 싱글톤(다지기 ③·④ + 오라 키) — 생성 또는 재파생 교체 ──
            int producerCount = producerTable.Count;
            if (!SystemAPI.HasSingleton<ProducerLookup>())
            {
                var pe = em.CreateEntity(typeof(ProducerLookup));
                em.SetComponentData(pe, new ProducerLookup
                {
                    Table = producerTable, WarehouseMainKey = warehouseKey,
                    AuraKeys = auraKeys, WorkerNeeds = workerNeeds,
                });
            }
            else if (prefabsChanged)
            {
                ref var pl = ref SystemAPI.GetSingletonRW<ProducerLookup>().ValueRW;
                if (pl.Table.IsCreated)       pl.Table.Dispose();
                if (pl.AuraKeys.IsCreated)    pl.AuraKeys.Dispose();
                if (pl.WorkerNeeds.IsCreated) pl.WorkerNeeds.Dispose();
                pl.Table            = producerTable;
                pl.WarehouseMainKey = warehouseKey;
                pl.AuraKeys         = auraKeys;
                pl.WorkerNeeds      = workerNeeds;
            }
            else { producerTable.Dispose(); auraKeys.Dispose(); workerNeeds.Dispose(); }   // 팩션만 추가된 갱신 — 기존 유지

            // ── 3-a) 재파생 시 기존 팩션 L2 재구성(테이블 교체, 누수 없음) ──
            int rebuilt = 0;
            if (prefabsChanged)
            {
                foreach (var (fid, l2rw) in
                         SystemAPI.Query<RefRO<FactionId>, RefRW<NeedLookupL2>>())
                {
                    if (l2rw.ValueRO.Table.IsCreated) l2rw.ValueRW.Table.Dispose();
                    l2rw.ValueRW.Table = BuildFactionTable(
                        fid.ValueRO.Value, in commonEntries, in factionEntries, in autoRows);
                    rebuilt++;
                }
            }

            // ── 3-b) L2 미보유 팩션 엔티티에 신규 부착 ──
            int attached = 0;
            foreach (var (fid, e) in
                     SystemAPI.Query<RefRO<FactionId>>().WithNone<NeedLookupL2>().WithEntityAccess())
            {
                ecb.AddComponent(e, new NeedLookupL2
                {
                    Table = BuildFactionTable(
                        fid.ValueRO.Value, in commonEntries, in factionEntries, in autoRows),
                });
                attached++;
            }

            UnityEngine.Debug.Log($"[LookupBuild] 프리팹 {prefabCount}개 스캔: 명시 "
                + $"{commonEntries.Length + factionEntries.Length}행 + 자동파생 {autoRows.Length}행"
                + $" → L2 팩션 신규 {attached}/재구성 {rebuilt}"
                + $" / 생산자 파생 {producerCount}종 / 창고 키={warehouseKey}");

            ecb.Playback(em);
            ecb.Dispose();
            commonEntries.Dispose();
            factionEntries.Dispose();
            autoRows.Dispose();
            capability.Dispose();
            production.Dispose();
        }

        public void OnDestroy(ref SystemState state)
        {
            // L2 해제 — 팩션 엔티티별
            foreach (var lookup in SystemAPI.Query<RefRW<NeedLookupL2>>())
            {
                if (lookup.ValueRO.Table.IsCreated)
                    lookup.ValueRW.Table.Dispose();
            }
            if (SystemAPI.HasSingleton<ProducerLookup>())
            {
                var pl = SystemAPI.GetSingleton<ProducerLookup>();
                if (pl.Table.IsCreated)       pl.Table.Dispose();
                if (pl.AuraKeys.IsCreated)    pl.AuraKeys.Dispose();
                if (pl.WorkerNeeds.IsCreated) pl.WorkerNeeds.Dispose();
            }
        }

        // ── 헬퍼 ───────────────────────────────────────────────────────────

        // 팩션 1개의 L2 테이블 구성 — 명시(공통→팩션) 먼저 = 오버라이드 우선, 자동이 빈틈 채움.
        static NativeHashMap<uint, int> BuildFactionTable(int factionId,
            in NativeList<BakedNeedMapping> commonEntries,
            in NativeList<BakedNeedMapping> factionEntries,
            in NativeList<BakedNeedMapping> autoRows)
        {
            var table = new NativeHashMap<uint, int>(64, Allocator.Persistent);

            // 명시 공통 항목 — 모든 팩션에 복사(중복 = 데이터 실수 → 경고).
            for (int i = 0; i < commonEntries.Length; i++)
                TryAddOrWarn(ref table, commonEntries[i].NeedMask, commonEntries[i].MainKey);

            // 명시 팩션 전용 항목 — 내 비트가 켜진 것만.
            uint myBit = FactionBit(factionId);
            for (int i = 0; i < factionEntries.Length; i++)
            {
                var entry = factionEntries[i];
                if ((entry.FactionFlags & myBit) != 0)
                    TryAddOrWarn(ref table, entry.NeedMask, entry.MainKey);
            }

            // 자동 파생 — 조용히 빈틈만(명시와의 중복 = 오버라이드 승리가 정상,
            //   자동끼리 중복은 MainKey 오름차순 선승 — 복수 공급 건물은 후속 분화 대상).
            for (int i = 0; i < autoRows.Length; i++)
                table.TryAdd(autoRows[i].NeedMask, autoRows[i].MainKey);

            return table;
        }

        // 명시 행 교차검증: MainKey 프리팹 존재 + 공급 능력(방문형|오라형 Relief) ⊇ NeedMask.
        //   NeedMaps(결정: 욕구→key)와 능력(건물→푸는 욕구)은 반대 방향의 한 쌍 — 일치해야 함.
        static void ValidateEntries(
            in NativeList<BakedNeedMapping> entries, in NativeHashMap<int, ulong> capability)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (!capability.TryGetValue(entry.MainKey, out ulong relief))
                    UnityEngine.Debug.LogError(
                        $"[LookupBuild] Validate 실패: NeedMaps 행 MainKey={entry.MainKey} — " +
                        "프리팹이 레지스트리에 없음");
                else if ((relief & entry.NeedMask) != entry.NeedMask)
                    UnityEngine.Debug.LogError(
                        $"[LookupBuild] Validate 실패: MainKey={entry.MainKey} 능력({relief:X})이 " +
                        $"NeedMask({entry.NeedMask:X})를 못 덮음 — 프리팹 BuildingAuthoring의 " +
                        "Relief에 해당 비트를 추가하거나 NeedMaps 행을 수정할 것");
            }
        }

        static uint FactionBit(int factionId) =>
            factionId > 0 ? (1u << (factionId - 1)) : 0u;

        static void TryAddOrWarn(ref NativeHashMap<uint, int> table, uint needMask, int mainKey)
        {
            if (!table.TryAdd(needMask, mainKey))
            {
                UnityEngine.Debug.LogWarning(
                    $"[LookupBuildSystem] NeedMask {needMask:X8} 중복 등록. " +
                    $"기존 MainKey={table[needMask]}, 신규 MainKey={mainKey} 무시됨.");
            }
        }
    }
}
