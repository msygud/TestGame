using System.IO;
using System.Text;
using CitySim.MapEditor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  MapLoadCommand — 맵 로드 요청 (이벤트 엔티티)
    //
    //  런타임에서 맵을 로드하려면:
    //    var e = em.CreateEntity();
    //    em.AddComponentData(e, new MapLoadCommand { JsonPath = "..." });
    // ══════════════════════════════════════════════════════════════
    public struct MapLoadCommand : IComponentData
    {
        public FixedString512Bytes JsonPath;
    }

    // ══════════════════════════════════════════════════════════════
    //  MapLoaderSystem
    //
    //  MapLoadCommand(JSON 경로)를 받아:
    //    1. JSON 파일 읽기 → MapData 파싱
    //    2. RequiredDlcs 검증 (PrefabLookup.HasDlc)
    //    3. 검증 통과 시:
    //       - Singles → SpawnRequest 엔티티
    //       - Multis  → MultiSpawnRequest 엔티티
    //       - Roads   → PlaceRoadCommand 엔티티 (RoadSystem 처리)
    //    4. MapLoadState 싱글톤 갱신
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PrefabLookupBuildSystem))]
    public partial struct MapLoaderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var e = state.EntityManager.CreateEntity(typeof(MapLoadState));
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status = MapLoadStatus.Idle,
            });
            state.RequireForUpdate<MapLoadCommand>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── PrefabLookup 준비 대기 ────────────────────────────────
            // SubScene 비동기 로드 완료 전에 처리하면 Table이 비어있어
            // 스폰이 전부 실패함. Table에 항목이 들어올 때까지 대기.
            if (!SystemAPI.HasSingleton<PrefabLookup>()) return;
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            if (!lookup.Table.IsCreated || lookup.Table.IsEmpty) return;

            // 1. 처리할 커맨드 수집 (foreach 안에서 structural change 금지)
            var toProcess = new NativeList<(FixedString512Bytes Path, Entity Entity)>(Allocator.Temp);
            foreach (var (cmd, entity) in
                SystemAPI.Query<RefRO<MapLoadCommand>>().WithEntityAccess())
                toProcess.Add((cmd.ValueRO.JsonPath, entity));

            // 2. foreach 밖에서 처리 + 엔티티 파괴
            for (int i = 0; i < toProcess.Length; i++)
            {
                ProcessLoadCommand(ref state, toProcess[i].Path.ToString());
                state.EntityManager.DestroyEntity(toProcess[i].Entity);
            }

            toProcess.Dispose();
        }

        // ── 메인 로직 ─────────────────────────────────────────────

        void ProcessLoadCommand(ref SystemState state, string path)
        {
            Debug.Log("start load map");
            SetStatus(ref state, MapLoadStatus.Loading);

            if (!File.Exists(path))
            {
                Debug.LogError($"[MapLoaderSystem] File not found: {path}");
                SetStatus(ref state, MapLoadStatus.Failed);
                return;
            }

            MapData mapData;
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                mapData = JsonUtility.FromJson<MapData>(json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MapLoaderSystem] JSON parse failed: {ex.Message}");
                SetStatus(ref state, MapLoadStatus.Failed);
                return;
            }

            if (mapData == null)
            {
                Debug.LogError("[MapLoaderSystem] MapData is null after parse.");
                SetStatus(ref state, MapLoadStatus.Failed);
                return;
            }

            // PrefabLookup은 OnUpdate에서 이미 준비 확인됨
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();

            // DLC 검증
            if (mapData.RequiredDlcs != null)
            {
                foreach (int dlcId in mapData.RequiredDlcs)
                {
                    if (dlcId == 0) continue;
                    if (!lookup.HasDlc(dlcId))
                    {
                        Debug.LogWarning($"[MapLoaderSystem] Missing DLC: {dlcId}. Load aborted.");
                        SetStatusMissingDlc(ref state, dlcId);
                        return;
                    }
                }
            }

            // 기존 맵 오브젝트 정리
            var em = state.EntityManager;
            var oldMapQ = SystemAPI.QueryBuilder().WithAll<MapLoaded>().Build();
            em.DestroyEntity(oldMapQ);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            var metaLookup = SystemAPI.HasSingleton<PrefabMetaLookup>()
                ? SystemAPI.GetSingleton<PrefabMetaLookup>()
                : default;

            float cs = mapData.Settings.CellSize;

            // Singles
            if (mapData.Singles != null)
                foreach (var p in mapData.Singles)
                    EmitSingleSpawn(ecb, p, cs);

            // Multis
            if (mapData.Multis != null)
                foreach (var p in mapData.Multis)
                    EmitMultiSpawn(ecb, p, cs, metaLookup);

            // Roads
            if (mapData.Roads != null)
                foreach (var p in mapData.Roads)
                    EmitRoadSpawn(ecb, p);

            ecb.Playback(em);
            ecb.Dispose();

            SetStatus(ref state, MapLoadStatus.Done);
            Debug.Log($"[MapLoaderSystem] Map loaded: {mapData.MapName}");
        }

        // ── 요청 발행 헬퍼 ────────────────────────────────────────

        static void EmitSingleSpawn(EntityCommandBuffer ecb, SinglePlacement p, float cs)
        {
            var position = new float3(
                p.CellX * cs + cs * 0.5f,
                p.PositionY,
                p.CellZ * cs + cs * 0.5f);

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new SpawnRequest
            {
                MainKey = p.MainKey,
                VariantKey = p.VariantKey,
                Position = position,
                Rotation = quaternion.RotateY(math.radians(p.RotationY)),
                Scale = p.Scale > 0f ? p.Scale : 1f,
            });
            ecb.AddComponent<MapLoaded>(e);
        }

        static void EmitMultiSpawn(
            EntityCommandBuffer ecb,
            MultiPlacement p,
            float cs,
            PrefabMetaLookup metaLookup)
        {
            int count = 5;
            float itemSize = 0.5f;

            if (metaLookup.TryGetMeta(p.MainKey, p.VariantKey, out var meta))
            {
                count = meta.MultiCount > 0 ? meta.MultiCount : count;
                itemSize = meta.MultiItemSize;
            }

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new MultiSpawnRequest
            {
                MainKey = p.MainKey,
                VariantKey = p.VariantKey,
                Cell = new int2(p.CellX, p.CellZ),
                CellSize = cs,
                Height = p.Height,
                Seed = p.RandomSeed,
                Count = count,
                ItemSize = itemSize,
                Scale = 1f,
            });
            ecb.AddComponent<MapLoaded>(e);
        }

        static void EmitRoadSpawn(EntityCommandBuffer ecb, RoadPlacement p)
        {
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new PlaceRoadCommand
            {
                Cell = new int2(p.CellX, p.CellZ),
                OwnerLocalId = 0,
                LaneCount = 2,
                FactionId = 0,   // 맵에 미리 깔린 도로 = 중립/공통(FactionId 0)
            });
            ecb.AddComponent<MapLoaded>(e);
        }

        // ── 상태 변경 헬퍼 ────────────────────────────────────────

        void SetStatus(ref SystemState state, MapLoadStatus status)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status = status,
                MissingDlcId = 0,
            });
        }

        void SetStatusMissingDlc(ref SystemState state, int dlcId)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status = MapLoadStatus.DlcMissing,
                MissingDlcId = dlcId,
            });
        }
    }
}
