using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Game.Utility;

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
    //  MapLoadCommand를 받아:
    //    1. JSON 파일 읽기
    //    2. RequiredDlcs 검증 (PrefabLookup에 DLC 키 있는지)
    //    3. 검증 통과 시:
    //       - Singles  → SpawnRequest 엔티티
    //       - Multis   → MultiSpawnRequest 엔티티
    //       - Roads    → RoadSpawnRequest 엔티티 (PlaceRoadCommand로 변환)
    //       - StartPoints → BaseSingles/BaseRoads도 동일하게 처리
    //    4. MapLoadState 싱글톤 갱신
    //
    //  인게임 RoadSystem.cs가 PlaceRoadCommand를 처리하므로
    //  도로는 RoadSpawnRequest → PlaceRoadCommand 변환.
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PrefabLookupInitSystem))]
    public partial struct MapLoaderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // MapLoadState 싱글톤 초기화
            var e = state.EntityManager.CreateEntity(typeof(MapLoadState));
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status = MapLoadStatus.Idle,
            });
        }

        public void OnUpdate(ref SystemState state)
        {
            // MapLoadCommand 처리
            foreach (var (cmd, cmdEntity) in
                SystemAPI.Query<RefRO<MapLoadCommand>>().WithEntityAccess())
            {
                var path = cmd.ValueRO.JsonPath.ToString();
                ProcessLoadCommand(ref state, path);
                state.EntityManager.DestroyEntity(cmdEntity);
            }
        }

        void ProcessLoadCommand(ref SystemState state, string path)
        {
            // 로드 상태 → Loading
            SetStatus(ref state, MapLoadStatus.Loading);

            // 파일 읽기
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
                mapData = UnityEngine.JsonUtility.FromJson<MapData>(json);
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

            // DLC 검증
            if (!SystemAPI.HasSingleton<PrefabLookup>())
            {
                Debug.LogError("[MapLoaderSystem] PrefabLookup not ready.");
                SetStatus(ref state, MapLoadStatus.Failed);
                return;
            }

            var lookup = SystemAPI.GetSingleton<PrefabLookup>();

            if (mapData.RequiredDlcs != null)
            {
                foreach (int dlcId in mapData.RequiredDlcs)
                {
                    if (dlcId == 0) continue; // Origin은 항상 보유

                    // DlcLookup에 해당 DLC의 항목이 하나라도 있는지 확인
                    if (!IsDlcAvailable(lookup, dlcId))
                    {
                        Debug.LogWarning(
                            $"[MapLoaderSystem] Missing DLC: {dlcId}. Map load aborted.");
                        SetStatusMissingDlc(ref state, dlcId);
                        return;
                    }
                }
            }

            // 기존 맵 오브젝트 정리 (MapLoaded 태그 가진 엔티티들)
            var em = state.EntityManager;
            var oldMapQ = SystemAPI.QueryBuilder().WithAll<MapLoaded>().Build();
            em.DestroyEntity(oldMapQ);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // Singles 처리
            if (mapData.Singles != null)
            {
                foreach (var p in mapData.Singles)
                    EmitSingleSpawn(ecb, p);
            }

            // MetaLookup 조회
            var metaLookup = SystemAPI.HasSingleton<PrefabMetaLookup>()
                ? SystemAPI.GetSingleton<PrefabMetaLookup>()
                : default;

            // Multis 처리
            if (mapData.Multis != null)
            {
                foreach (var p in mapData.Multis)
                    EmitMultiSpawn(ecb, p, mapData.Settings.CellSize, lookup, metaLookup);
            }

            // Roads 처리
            if (mapData.Roads != null)
            {
                foreach (var p in mapData.Roads)
                    EmitRoadSpawn(ecb, p);
            }

            // StartPoints 처리
            if (mapData.StartPoints != null)
            {
                foreach (var sp in mapData.StartPoints)
                {
                    if (sp.BaseSingles != null)
                        foreach (var p in sp.BaseSingles)
                            EmitSingleSpawn(ecb, p);

                    if (sp.BaseMultis != null)
                        foreach (var p in sp.BaseMultis)
                            EmitMultiSpawn(ecb, p, mapData.Settings.CellSize, lookup, metaLookup);

                    if (sp.BaseRoads != null)
                        foreach (var p in sp.BaseRoads)
                            EmitRoadSpawn(ecb, p);
                }
            }

            ecb.Playback(em);
            ecb.Dispose();

            SetStatus(ref state, MapLoadStatus.Done);
            Debug.Log($"[MapLoaderSystem] Map loaded: {mapData.MapName}");
        }

        // ── 요청 발행 헬퍼 ────────────────────────────────────────

        static void EmitSingleSpawn(EntityCommandBuffer ecb, SinglePlacement p)
        {
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new SpawnRequest
            {
                MainKey    = p.MainKey,
                VariantKey = p.VariantKey,
                Position   = p.Position,
                Rotation   = quaternion.RotateY(math.radians(p.RotationY)),
                Scale      = p.Scale > 0f ? p.Scale : 1f,
            });
            ecb.AddComponent<MapLoaded>(e); // 나중에 정리하기 위한 태그
        }

        static void EmitMultiSpawn(
            EntityCommandBuffer ecb,
            MultiPlacement p,
            float cellSize,
            PrefabLookup lookup,
            PrefabMetaLookup metaLookup)
        {
            // MetaLookup에서 Count, ItemSize 조회
            int   count    = 5;    // 기본값
            float itemSize = 0.5f;
            var   size     = new int2(1, 1);
            float yOffset  = 0f;
            if (metaLookup.TryGetMeta(p.MainKey, p.VariantKey, out var meta))
            {
                count    = meta.MultiCount;
                itemSize = meta.MultiItemSize;
                size     = math.max(meta.Size, new int2(1, 1));
                yOffset  = meta.YOffset;
            }

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new MultiSpawnRequest
            {
                MainKey    = p.MainKey,
                VariantKey = p.VariantKey,
                Cell       = p.Cell,
                Position   = ResolveMultiPosition(p, cellSize, size, yOffset),
                CellSize   = cellSize,
                Height     = GameUtility.GetHeightWorldPosition(p.Height, cellSize, yOffset),
                Seed       = p.Seed,
                Count      = count,
                ItemSize   = itemSize,
                Scale      = p.Scale > 0f ? p.Scale : 1f,
            });
            ecb.AddComponent<MapLoaded>(e);
        }

        static float3 ResolveMultiPosition(MultiPlacement p, float cellSize, int2 size, float yOffset)
        {
            float y = GameUtility.GetHeightWorldPosition(p.Height, cellSize, yOffset);
            return GameUtility.GetFootprintCenterWorldPosition(p.Cell, size, cellSize, y);
        }


        /// <summary>
        /// 테스트
        /// </summary>
        /// <param name="ecb"></param>
        /// <param name="p"></param>
        static void EmitRoadSpawn(EntityCommandBuffer ecb, RoadPlacement p)
        {
            // RoadSpawnRequest → PlaceRoadCommand로 변환
            // RoadSystem이 Cell 기반으로 비트마스크 재계산
            var e = ecb.CreateEntity();
            //ecb.AddComponent(e, new PlaceRoadCommand { Cell = p.Cell });
            // MapLoaded 태그는 RoadSystem이 생성하는 Road 엔티티에 추가 필요
            // 단순화: 요청 엔티티에만 태그
            //ecb.AddComponent<MapLoaded>(e);
        }

        // ── DLC 검증 헬퍼 ─────────────────────────────────────────

        static bool IsDlcAvailable(PrefabLookup lookup, int dlcId)
        {
            // DlcLookup에 해당 DLC의 항목이 하나라도 있으면 보유 판정
            // NativeHashMap 순회 (값이 dlcId인 것 찾기)
            foreach (var kv in lookup.DlcLookup)
            {
                if (kv.Value == dlcId) return true;
            }
            return false;
        }

        // ── 상태 변경 헬퍼 ────────────────────────────────────────

        void SetStatus(ref SystemState state, MapLoadStatus status)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status       = status,
                MissingDlcId = 0,
            });
        }

        void SetStatusMissingDlc(ref SystemState state, int dlcId)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status       = MapLoadStatus.DlcMissing,
                MissingDlcId = dlcId,
            });
        }
    }
}
