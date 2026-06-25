using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  도시 구역(Zone) 추적 — AI 도로 재구성용
    //
    //  사람은 공격으로 건물이 죽으면 못 쓰게 된 도로를 손으로 정리·재연결한다.
    //  AI는 그 행동을 자동화해야 공평하다. 이를 위해:
    //    ① AI가 블록을 조성할 때마다 '구역'으로 등록(넘버링 = 블록 내부 원점 O를 키로).
    //       구역의 둘러싼 도로 링 셀들에 refcount +1, 내부 셀 → 구역 매핑 기록.
    //    ② 구역의 건물이 모두 죽으면 그 구역 링 셀의 refcount −1.
    //       0이 된 셀 = 더는 어떤 '살아있는' 구역도 안 쓰는 도로 → 제거(RemoveRoadCommand).
    //       공유 변(이웃 구역도 가진 셀)은 refcount ≥1로 남아 보존된다.
    //    ③ 제거로 단절된 구역은 NetworkRepairSystem이 라우터로 베이스에 재연결.
    //
    //  degree(토폴로지)가 아니라 '구역 소유/공유'로 판단하므로 격자·링 도시에서도
    //  빈 블록 링이 정확히 풀린다(degree-prune이 못 하던 것).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>한 구역(블록) 기록. 키 = 블록 내부 원점 O.</summary>
    public struct ZoneRecord
    {
        /// <summary>블록 내부 한 변 셀 수.</summary>
        public byte K;
        /// <summary>도로 footprint 한 변 셀 수(roadSize).</summary>
        public byte Road;
        /// <summary>소유 팀 LocalId.</summary>
        public int  Owner;
        /// <summary>팩션 ID(재연결 도로 발행에 필요).</summary>
        public int  FactionId;
        /// <summary>살아있는 건물 수. 0이 되면 구역 해체(링 refcount 감소).</summary>
        public int  BuildingCount;
        /// <summary>true = 베이스 블록. 절대 해체·도로 제거 안 함(건물이 죽어도 보호).</summary>
        public bool Permanent;
    }

    /// <summary>
    /// 도시 구역 레지스트리 싱글톤. CityZoneInitSystem이 수명주기 관리(Persistent).
    ///   · Zones      : 블록 내부 원점 O → ZoneRecord.
    ///   · InteriorZone: 블록 내부 셀 → 그 셀이 속한 구역 O (건물 사망 → 구역 역참조).
    ///   · RingRef    : 도로 링 셀 → 그 셀을 링으로 쓰는 '살아있는 구역' 수(공유 판정).
    /// 키는 블록 원점(셀)이라 구역마다 유일 — 별도 카운터/넘버 불필요.
    /// </summary>
    public struct CityZones : IComponentData
    {
        public NativeHashMap<int2, ZoneRecord> Zones;
        public NativeHashMap<int2, int2>       InteriorZone;
        public NativeHashMap<int2, int>        RingRef;
    }

    /// <summary>
    /// 도로망 재연결 요청(단발성). 구역 해체로 도로가 제거된 팀에 대해 RazeSystem이 발행.
    /// NetworkRepairSystem이 RoadSystem 이후(제거 반영된 RoadLayer)에 베이스 연결을 검증하고,
    /// 단절된 도로 섬을 라우터로 다시 잇는다.
    /// </summary>
    public struct NetworkRepairRequest : IComponentData
    {
        public int OwnerLocalId;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ZoneOps — 구역 등록/사망귀속/해체 순수 연산 (RazeSystem·AiCityGrowth·전투 공용)
    //
    //  CityZones의 맵 '내용'만 변경하므로(구조체 필드 불변) SetSingleton 불필요 —
    //  NativeHashMap은 힙 데이터를 가리키는 핸들이라 in/by-value로 받아도 내용 변경이 공유됨.
    // ══════════════════════════════════════════════════════════════════════════
    public static class ZoneOps
    {
        /// <summary>
        /// 구역 등록(이미 있으면 무시). 내부 셀 → O 매핑, 링 셀 refcount +1.
        ///   permanent=true(베이스)면 건물 사망에도 해체되지 않는다.
        /// </summary>
        public static void RegisterZone(
            CityZones cz, int2 O, int K, int Road, int owner, int faction, bool permanent)
        {
            if (cz.Zones.ContainsKey(O)) return;

            cz.Zones.Add(O, new ZoneRecord
            {
                K = (byte)K, Road = (byte)Road, Owner = owner, FactionId = faction,
                BuildingCount = 1, Permanent = permanent,
            });

            // 내부 셀 → 구역 O (건물 사망 시 셀로 구역 역참조)
            for (int dz = 0; dz < K; dz++)
            for (int dx = 0; dx < K; dx++)
                cz.InteriorZone[O + new int2(dx, dz)] = O;

            // 링 셀 refcount +1
            EnumRingBegin(O, K, Road, out int xa, out int xb, out int za, out int zb);
            for (int z = za; z <= zb; z += Road)
            for (int x = xa; x <= xb; x += Road)
            {
                if (!(x == xa || x == xb || z == za || z == zb)) continue;   // 링(둘레)만
                for (int dz = 0; dz < Road; dz++)
                for (int dx = 0; dx < Road; dx++)
                {
                    int2 c = new int2(x + dx, z + dz);
                    if (cz.RingRef.TryGetValue(c, out int n)) cz.RingRef[c] = n + 1;
                    else                                      cz.RingRef.Add(c, 1);
                }
            }
        }

        /// <summary>
        /// 건물 사망을 구역에 귀속(BuildingCount −1). 반환 true = 구역이 비었음(해체 대상).
        ///   permanent 구역(베이스)·미등록 셀은 false.
        /// </summary>
        public static bool AttributeDeath(
            CityZones cz, int2 buildingCell, out int2 zoneO, out int owner)
        {
            zoneO = default; owner = -1;
            if (!cz.InteriorZone.TryGetValue(buildingCell, out int2 O)) return false;
            if (!cz.Zones.TryGetValue(O, out ZoneRecord rec))            return false;
            owner = rec.Owner;
            if (rec.Permanent) return false;

            rec.BuildingCount -= 1;
            cz.Zones[O] = rec;
            zoneO = O;
            return rec.BuildingCount <= 0;
        }

        /// <summary>
        /// 빈 구역 해체. 링 셀 refcount −1 → 0이 된 셀(공유 안 됨)을 outRemove에 모은다.
        ///   내부 셀 매핑·구역 레코드 제거. 호출자가 outRemove를 RemoveRoadCommand로 발행.
        /// </summary>
        public static void ReleaseZone(CityZones cz, int2 O, ref NativeList<int2> outRemove)
        {
            if (!cz.Zones.TryGetValue(O, out ZoneRecord rec)) return;
            int K = rec.K, Road = rec.Road <= 1 ? 1 : rec.Road;

            // 내부 셀 매핑 제거
            for (int dz = 0; dz < K; dz++)
            for (int dx = 0; dx < K; dx++)
                cz.InteriorZone.Remove(O + new int2(dx, dz));

            // 링 셀 refcount −1 → 0이면 제거 대상
            EnumRingBegin(O, K, Road, out int xa, out int xb, out int za, out int zb);
            for (int z = za; z <= zb; z += Road)
            for (int x = xa; x <= xb; x += Road)
            {
                if (!(x == xa || x == xb || z == za || z == zb)) continue;
                for (int dz = 0; dz < Road; dz++)
                for (int dx = 0; dx < Road; dx++)
                {
                    int2 c = new int2(x + dx, z + dz);
                    if (!cz.RingRef.TryGetValue(c, out int n)) continue;
                    if (n <= 1) { cz.RingRef.Remove(c); outRemove.Add(c); }
                    else        { cz.RingRef[c] = n - 1; }
                }
            }

            cz.Zones.Remove(O);
        }

        // 링 enumerate 경계. 링 셀 = [O-Road, O+K] 둘레(폭 Road).
        //   Road==1 → border of [O.x-1, O.x+K]² (CollectRingRoadsDrawn와 동일 셀 집합).
        //   Road>1  → step Road 격자 둘레의 Road×Road footprint (CollectRingRoads와 동일).
        static void EnumRingBegin(int2 O, int K, int Road, out int xa, out int xb, out int za, out int zb)
        {
            xa = O.x - Road; xb = O.x + K; za = O.y - Road; zb = O.y + K;
        }
    }
}
