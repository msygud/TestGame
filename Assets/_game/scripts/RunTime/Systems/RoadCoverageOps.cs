using Unity.Collections;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════
    //  RoadCoverageOps — 도로망 도달 범위 BFS (순수 fact)
    // ──────────────────────────────────────────────────────────────────────
    //  "한 도로셀에서 같은 소유자 도로망을 따라 maxDist칸 이내에 닿는 도로셀"을
    //  계산한다. StampRebuildSystem.StampOne의 확산 규칙과 **동일**:
    //    · 시작 셀이 owner 소유 도로여야 시작.
    //    · 셀의 연결 비트(RoadCell.Directions)를 따라서만 확산(보행/시각과 동일 권위).
    //    · 이웃이 반대 방향 비트로 되받아야 연결(양방향 일치) → 평행 도로는 안 건넘,
    //      교차 셀에서만 가로질러 전파(축-AND 결과 그대로 반영).
    //    · maxDist 도달 셀은 covered에 포함하되 더 확장하지 않음(0 이하=무제한).
    //
    //  쓰임:
    //    · DepotPlaceController — 관리소 배치 프리뷰 커버리지(메인스레드, Temp 컨테이너).
    //    · (예정) Phase 2 RoadMaintenance stamp 시스템 — covered를 도장으로 변환.
    //  → 프리뷰와 런타임 coverage가 같은 fact를 공유해 항상 일치한다.
    //
    //  Burst 호환: managed 타입 없음, 컨테이너는 호출자가 소유(in/ref).
    // ══════════════════════════════════════════════════════════════════════
    public static class RoadCoverageOps
    {
        /// <summary>
        /// start에서 owner 도로망을 따라 maxDist칸 이내 도로셀을 covered(셀→거리)에 채운다.
        /// queue/covered는 호출자가 할당·소유(재사용 가능). 함수가 진입 시 둘 다 Clear한다.
        /// 시작 셀이 owner 소유 도로가 아니면 빈 결과로 즉시 반환.
        /// </summary>
        public static void Flood(
            int2 start,
            int  owner,
            int  maxDist,
            in NativeHashMap<int2, RoadCell> roadLayer,
            ref NativeQueue<int2> queue,
            ref NativeHashMap<int2, int> covered)
        {
            queue.Clear();
            covered.Clear();

            // 시작 셀이 owner 소유 도로가 아니면 도달 범위 없음.
            if (!roadLayer.TryGetValue(start, out var startRc) || startRc.OwnerLocalId != owner)
                return;

            queue.Enqueue(start);
            covered[start] = 0;

            while (queue.TryDequeue(out int2 cell))
            {
                int dist = covered[cell];

                // 거리 상한 도달 시 더 확장 안 함(셀 자체는 covered에 남음).
                if (maxDist > 0 && dist >= maxDist)
                    continue;

                if (!roadLayer.TryGetValue(cell, out var curRc))
                    continue;

                for (int d = 0; d < 4; d++)
                {
                    // 현재 셀이 이 방향으로 이어지지 않으면 건너뜀.
                    if ((curRc.Directions & RoadDirOps.FromIndex(d)) == 0)
                        continue;

                    int2 next = cell + RoadDirOps.Offsets[d];
                    if (covered.ContainsKey(next))
                        continue;

                    // 이웃이 같은 소유자 도로이고, 반대 방향으로 되받아 연결돼야 함(양방향).
                    if (!roadLayer.TryGetValue(next, out var nextRc) || nextRc.OwnerLocalId != owner)
                        continue;
                    if ((nextRc.Directions & RoadDirOps.FromIndex(RoadDirOps.OppositeIndex(d))) == 0)
                        continue;

                    covered[next] = dist + 1;
                    queue.Enqueue(next);
                }
            }
        }
    }
}
