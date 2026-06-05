using Unity.Collections;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  CivilianBFS
    //
    //  민간 유닛(물류/시민) 전용 길찾기.
    //  도로 레이어 그리드 기반, 우측통행 강제.
    //
    //  우측통행 규칙:
    //    직선 도로(Horizontal/Vertical)
    //      → 진행 방향 기준 우측 차선으로만 이동
    //      → Horizontal(EW축): E 방향 이동 = 남쪽 차선(S half)
    //                          W 방향 이동 = 북쪽 차선(N half)
    //      → Vertical(NS축):   N 방향 이동 = 동쪽 차선(E half)
    //                          S 방향 이동 = 서쪽 차선(W half)
    //    교차로(Cross)
    //      → 직진/우회전 허용, 좌회전 허용 (교차로에서만)
    //      → U턴 불가
    //
    //  팀 소유 체크:
    //    myTeam과 다른 팀 소유 도로는 진입 불가.
    //
    //  결과:
    //    outPath에 시작 → 도착 셀 순서로 저장.
    //    경로 없으면 false 반환.
    // ══════════════════════════════════════════════════════════════
    public static class CivilianBFS
    {
        /// <summary>
        /// 우측통행 BFS.
        /// start/goal은 도로 셀이어야 한다.
        /// myTeam = -1이면 팀 체크 생략 (에디터 프리뷰용).
        /// </summary>
        public static bool FindPath(
            int2 start,
            int2 goal,
            in NativeHashMap<int2, RoadCell> roadLayer,
            int myTeam,
            ref NativeList<int2> outPath,
            Allocator scratchAlloc)
        {
            outPath.Clear();

            if (!roadLayer.TryGetValue(start, out var startCell)) return false;
            if (!roadLayer.TryGetValue(goal,  out _))             return false;

            if (!IsPassable(startCell, myTeam)) return false;

            if (start.Equals(goal))
            {
                outPath.Add(start);
                return true;
            }

            // BFS 상태: (셀, 진입 방향 인덱스)
            // 진입 방향을 함께 추적해야 우측통행 제한 가능
            var came  = new NativeHashMap<int2, int2>(64, scratchAlloc);   // 셀 → 이전 셀
            var queue = new NativeQueue<BFSNode>(scratchAlloc);
            var seen  = new NativeHashSet<int2>(64, scratchAlloc);

            queue.Enqueue(new BFSNode { Cell = start, FromDirIndex = -1 });
            seen.Add(start);
            bool found = false;

            while (queue.TryDequeue(out var cur))
            {
                if (cur.Cell.Equals(goal)) { found = true; break; }

                if (!roadLayer.TryGetValue(cur.Cell, out var curRoadCell)) continue;

                for (int i = 0; i < 4; i++)
                {
                    // 우측통행 규칙 체크
                    if (!IsMovementAllowed(cur.Cell, cur.FromDirIndex, i, curRoadCell))
                        continue;

                    var nCell = cur.Cell + RoadDirOps.Offsets[i];
                    if (seen.Contains(nCell)) continue;
                    if (!roadLayer.TryGetValue(nCell, out var nCell_data)) continue;
                    if (!IsPassable(nCell_data, myTeam)) continue;

                    // 진입 방향 양방향 검증
                    // (내가 i방향으로 나가면, 이웃은 반대방향(i+2)%4 으로 연결돼야 함)
                    var oppDir = RoadDirOps.FromIndex((i + 2) & 3);
                    if ((nCell_data.Directions & oppDir) == 0) continue;

                    seen.Add(nCell);
                    came[nCell] = cur.Cell;
                    queue.Enqueue(new BFSNode { Cell = nCell, FromDirIndex = i });
                }
            }

            if (found)
            {
                var temp = new NativeList<int2>(32, scratchAlloc);
                var c = goal;
                temp.Add(c);
                while (!c.Equals(start))
                {
                    c = came[c];
                    temp.Add(c);
                }
                for (int i = temp.Length - 1; i >= 0; i--)
                    outPath.Add(temp[i]);
                temp.Dispose();
            }

            came.Dispose();
            queue.Dispose();
            seen.Dispose();
            return found;
        }

        // ── 우측통행 규칙 ────────────────────────────────────────────

        /// <summary>
        /// 현재 셀에서 dirIndex 방향으로 이동 가능한가?
        /// fromDirIndex = 이 셀에 진입한 방향 (-1 = 시작점).
        /// </summary>
        static bool IsMovementAllowed(
            int2 cell,
            int fromDirIndex,
            int toDirIndex,
            RoadCell roadCell)
        {
            // 연결 비트 체크
            if ((roadCell.Directions & RoadDirOps.FromIndex(toDirIndex)) == 0)
                return false;

            // 시작점은 제한 없음
            if (fromDirIndex < 0) return true;

            // U턴 불가 (항상)
            int oppFrom = (fromDirIndex + 2) & 3;
            if (toDirIndex == oppFrom) return false;

            switch (roadCell.FlowAxis)
            {
                case RoadFlowAxis.Cross:
                    // 교차로: 직진/우회전/좌회전 모두 허용, U턴만 불가
                    return true;

                case RoadFlowAxis.Horizontal:
                    // EW 직선: 같은 축 방향만 허용 (직진)
                    // 우측통행 — E로 왔으면 E만, W로 왔으면 W만
                    return toDirIndex == fromDirIndex;

                case RoadFlowAxis.Vertical:
                    // NS 직선: 같은 축 방향만 허용 (직진)
                    return toDirIndex == fromDirIndex;

                default:
                    return true;
            }
        }

        /// <summary>팀 소유 + 도로 존재 체크.</summary>
        static bool IsPassable(RoadCell cell, int ownerid)
        {
            if (ownerid < 0) return true; // 팀 체크 생략 (프리뷰용)
            return cell.OwnerLocalId == ownerid;
        }

        struct BFSNode
        {
            public int2 Cell;
            public int  FromDirIndex; // 이 셀에 진입한 방향 인덱스 (-1 = 시작)
        }
    }
}
