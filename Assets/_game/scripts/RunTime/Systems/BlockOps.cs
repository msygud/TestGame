using Unity.Collections;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════
    //  BlockOps  — 구획(Block) 레이어 조작 헬퍼 (순수 함수)
    //
    //  설계 원칙: "헬퍼는 사실/가능성을 나열한다. 결정은 시스템이 진다."
    //    · 어느 자리가 '가능한가', 공유 변이 '몇 개인가', 빈 셀이 '어디인가'
    //      같은 사실(mechanism)만 반환한다.
    //    · "어느 후보가 best인가" 같은 정책(policy)은 호출 시스템의 몫.
    //
    //  Burst 호환 정적 함수 모음.
    //    · 어트리뷰트 없이도, Burst 컴파일된 ISystem/IJob 안에서 호출되면
    //      자동으로 함께 Burst 컴파일된다 (호출자가 진입점).
    //    · managed 타입 없음
    //    · NativeHashMap 은 in/ref 로 받음
    //    · 출력 목록은 호출자가 할당한 NativeList 에 채움 (Allocator.Temp 등)
    //
    //  좌표 규약:
    //    · realCell  = 실해상도 셀 좌표 (OccupancyLayer/RoadLayer/TerrainLayer 키)
    //    · blockCell = 저해상도 셀 좌표 (BlockLayer 키, = realCell / BlockGrid.UNIT)
    //    · 저해상도 1칸 = BlockGrid.UNIT × UNIT 실셀
    // ══════════════════════════════════════════════════════════════════
    public static class BlockOps
    {
        // ──────────────────────────────────────────────────────────────
        //  1. 배치 가능성 — "이 저해상도 위치에 이 크기 구획이 들어갈 수 있나?"
        //
        //  판정: 구획이 덮는 모든 저해상도 셀이
        //    (a) BlockLayer 에 미등록이고
        //    (b) 그 실셀 범위가 TerrainLayer 안(맵 내부)이며 OccupancyLayer 에서 비어있다.
        //  지형 타입(땅/물 등) 검증은 하지 않는다 — 그건 건물 단위 정책이라
        //  최종 PlaceBuildingRequest 검증(BuildingPlacementSystem)에 맡긴다.
        // ──────────────────────────────────────────────────────────────
        public static bool CanPlaceBlock(
            in NativeHashMap<int2, BlockCell>     blockLayer,
            in NativeHashMap<int2, OccupancyCell> occupancyLayer,
            in NativeHashMap<int2, TerrainCell>   terrainLayer,
            in NativeHashMap<int2, ResourceCell>  resourceLayer,
            int2 blockPos, int2 blockSize)
        {
            bool firstCell = true; byte baseHeight = 0;

            for (int bx = 0; bx < blockSize.x; bx++)
            for (int bz = 0; bz < blockSize.y; bz++)
            {
                int2 bc = blockPos + new int2(bx, bz);

                // (a) 이미 구획으로 등록된 저해상도 셀이면 불가
                if (blockLayer.ContainsKey(bc)) return false;

                // (b) 이 저해상도 셀이 덮는 실셀들이 전부 맵 안 + 배치 가능해야 함
                //     (점유/자원 검사 — 건물 검증 ValidateCells와 일치시켜 유령 구획 방지)
                if (!IsRealCellRangeFree(occupancyLayer, terrainLayer, resourceLayer, bc))
                    return false;

                // (c) 단차 거부 — 구획 전체 실셀이 같은 지형 높이여야 함
                //     (건물 ValidateCells의 HeightMismatch와 일치)
                int2 ro = BlockGrid.ToReal(bc);
                for (int dx = 0; dx < BlockGrid.UNIT; dx++)
                for (int dz = 0; dz < BlockGrid.UNIT; dz++)
                {
                    byte h = terrainLayer.TryGetValue(ro + new int2(dx, dz), out var tc)
                        ? tc.Height : (byte)0;
                    if (firstCell) { baseHeight = h; firstCell = false; }
                    else if (h != baseHeight) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 저해상도 셀 1칸(UNIT×UNIT 실셀)이 전부 맵 안이고 배치 가능한가.
        /// 건물 배치 검증(BuildingPlacementSystem.ValidateCells)과 동일 기준:
        ///   · 환경물(나무/바위)은 배치 시 치우므로 비어있는 것으로 본다.
        ///   · 채취 자원(Amount>0)은 불가 (자원 보존).
        /// </summary>
        static bool IsRealCellRangeFree(
            in NativeHashMap<int2, OccupancyCell> occupancyLayer,
            in NativeHashMap<int2, TerrainCell>   terrainLayer,
            in NativeHashMap<int2, ResourceCell>  resourceLayer,
            int2 blockCell)
        {
            int2 origin = BlockGrid.ToReal(blockCell);
            for (int dx = 0; dx < BlockGrid.UNIT; dx++)
            for (int dz = 0; dz < BlockGrid.UNIT; dz++)
            {
                int2 rc = origin + new int2(dx, dz);

                // 맵 범위 밖 (지형이 정의돼 있어야 맵 내부)
                if (!terrainLayer.ContainsKey(rc)) return false;

                // 점유됨 (환경물은 제외 — 건물이 치움)
                if (occupancyLayer.TryGetValue(rc, out var occ) && !occ.IsEmpty
                    && occ.Type != OccupantType.Environment)
                    return false;

                // 채취 자원 위 (자원 보존)
                if (resourceLayer.IsCreated
                    && resourceLayer.TryGetValue(rc, out var res) && res.Amount > 0)
                    return false;
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        //  2. 후보 수집 — "팀 도로의 옆면(인접)에 있는 빈 저해상도 자리들"
        //
        //  도로 옆면 전체가 건물 후보다 (특이점만이 아님). 이래야 도로 한 줄에
        //  건물이 줄지어 붙어 도시가 조밀하게 차오른다.
        //  같은 소유자(ownerLocalId) 도로만 본다 — 자기 도로변을 채움.
        //  반환: 빈 저해상도 셀 좌표 목록 (중복 제거됨).
        //  '어느 후보가 좋은가'는 판단하지 않는다 — 호출 시스템이 점수로 거른다.
        // ──────────────────────────────────────────────────────────────
        public static void CollectRoadsideCandidates(
            int ownerLocalId,
            in NativeHashMap<int2, RoadCell>      roadLayer,
            in NativeHashMap<int2, BlockCell>     blockLayer,
            in NativeHashMap<int2, OccupancyCell> occupancyLayer,
            in NativeHashMap<int2, TerrainCell>   terrainLayer,
            in NativeHashMap<int2, ResourceCell>  resourceLayer,
            ref NativeList<int2>                  outCandidates)
        {
            // 중복 방지용 임시 집합
            var seen = new NativeHashSet<int2>(64, Allocator.Temp);

            foreach (var kv in roadLayer)
            {
                int2     roadCell = kv.Key;
                RoadCell road     = kv.Value;

                // 내 도로만 (다른 플레이어 도로변엔 안 지음)
                if (road.OwnerLocalId != ownerLocalId) continue;

                // 도로 셀의 4방향 인접 실셀 → 그 셀이 속한 저해상도 셀을 후보로
                for (int d = 0; d < 4; d++)
                {
                    int2 nReal  = roadCell + RoadDirOps.Offsets[d];
                    int2 nBlock = BlockGrid.ToBlock(nReal);

                    if (seen.Contains(nBlock)) continue;

                    // 빈 저해상도 셀(미등록 + 실셀 배치가능)만 후보
                    if (!blockLayer.ContainsKey(nBlock) &&
                        IsRealCellRangeFree(occupancyLayer, terrainLayer, resourceLayer, nBlock))
                    {
                        seen.Add(nBlock);
                        outCandidates.Add(nBlock);
                    }
                }
            }

            seen.Dispose();
        }

        // ──────────────────────────────────────────────────────────────
        //  3. 공유 변 카운트 — "이 자리에 이 크기 구획을 놓으면 기존 구획과 변을 몇 칸 공유하나?"
        //
        //  밀도 점수의 '재료'. 클수록 기존 덩어리에 밀착(응집).
        //  점수로 환산해 고르는 것은 시스템의 정책.
        //  반환: 구획 둘레 저해상도 변 중 등록된 구획과 맞닿은 칸 수.
        // ──────────────────────────────────────────────────────────────
        public static int CountSharedEdges(
            in NativeHashMap<int2, BlockCell> blockLayer,
            int2 blockPos, int2 blockSize)
        {
            int shared = 0;

            // 둘레 각 셀의 바깥 인접이 등록된 구획이면 +1
            for (int bx = 0; bx < blockSize.x; bx++)
            for (int bz = 0; bz < blockSize.y; bz++)
            {
                // 둘레(경계) 셀만 검사
                bool isBorder = bx == 0 || bz == 0
                             || bx == blockSize.x - 1 || bz == blockSize.y - 1;
                if (!isBorder) continue;

                int2 bc = blockPos + new int2(bx, bz);

                for (int d = 0; d < 4; d++)
                {
                    int2 nb = bc + RoadDirOps.Offsets[d];

                    // 자기 구획 내부면 스킵
                    if (nb.x >= blockPos.x && nb.x < blockPos.x + blockSize.x &&
                        nb.y >= blockPos.y && nb.y < blockPos.y + blockSize.y)
                        continue;

                    if (blockLayer.ContainsKey(nb)) shared++;
                }
            }
            return shared;
        }

        // ──────────────────────────────────────────────────────────────
        //  4. 구획 내 빈 실셀 — "이 구획(BlockOrigin) 안에서 아직 비어있는 실셀들"
        //
        //  구획 안에 건물을 더 채울 수 있나? 의 답.
        //  BlockLayer 가 아니라 OccupancyLayer(단일 소스)를 조회한다.
        // ──────────────────────────────────────────────────────────────
        public static void GetEmptyCellsInBlock(
            in NativeHashMap<int2, OccupancyCell> occupancyLayer,
            int2 blockOrigin, int2 blockSize,
            ref NativeList<int2> outCells)
        {
            int2 realOrigin = BlockGrid.ToReal(blockOrigin);
            int2 realSize   = BlockGrid.RealSize(blockSize);

            for (int dx = 0; dx < realSize.x; dx++)
            for (int dz = 0; dz < realSize.y; dz++)
            {
                int2 rc = realOrigin + new int2(dx, dz);
                if (!occupancyLayer.TryGetValue(rc, out var occ) || occ.IsEmpty)
                    outCells.Add(rc);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  5. 구획 등록 — 결정의 '실행' (사실을 기록할 뿐, 무엇을 등록할지는 시스템이 결정)
        //
        //  blockPos..blockPos+blockSize 범위의 모든 저해상도 셀에
        //  같은 BlockOrigin(=blockPos)을 기록해 하나의 구획으로 묶는다.
        //  실제 건물/도로 스폰은 하지 않는다 — 시스템이 PlaceBuildingRequest/
        //  PlaceRoadCommand 를 ECB로 발행한다.
        // ──────────────────────────────────────────────────────────────
        public static void RegisterBlock(
            ref NativeHashMap<int2, BlockCell> blockLayer,
            int2 blockPos, int2 blockSize, int ownerLocalId)
        {
            var cell = new BlockCell
            {
                OwnerLocalId = ownerLocalId,
                BlockOrigin = blockPos,
                BlockSize   = blockSize,
            };

            for (int bx = 0; bx < blockSize.x; bx++)
            for (int bz = 0; bz < blockSize.y; bz++)
            {
                int2 bc = blockPos + new int2(bx, bz);
                blockLayer[bc] = cell;   // 덮어쓰기 (이미 CanPlaceBlock 로 검증된 상태 가정)
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  6. 구획 해제 — 구획 전체를 빈 격자로 되돌림
        //
        //  주의: 도로는 건드리지 않는다 (append-only, 영구).
        //        OccupancyLayer 의 건물 점유 해제도 이 헬퍼의 책임이 아니다
        //        (건물 파괴 시스템이 별도로 처리). 여기서는 BlockLayer 등록만 해제.
        // ──────────────────────────────────────────────────────────────
        public static void UnregisterBlock(
            ref NativeHashMap<int2, BlockCell> blockLayer,
            int2 blockOrigin, int2 blockSize)
        {
            for (int bx = 0; bx < blockSize.x; bx++)
            for (int bz = 0; bz < blockSize.y; bz++)
            {
                int2 bc = blockOrigin + new int2(bx, bz);
                blockLayer.Remove(bc);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  7. 도로 확장 후보 — "내 도로에서 빈 평지로 뻗을 수 있는 최선의 직선 stub"
        //
        //  팀 소유 도로 footprint 원점에서 4방향으로, 인접 footprint(같은 크기)가
        //  비어있고 평탄(원점과 같은 높이)하면 그 방향으로 몇 칸까지 뻗을 수 있는지 잰다.
        //  가장 길게 뻗는 방향을 best로 반환 (정책=발행은 호출 시스템이).
        //
        //  단차/자원/점유(환경 제외)/맵 경계를 만나면 거기서 정지 → 유기적 성장.
        //  반환 found. outOrigin=첫 확장 footprint 원점, outDir=방향(0~3), outSteps=뻗을 footprint 수.
        // ──────────────────────────────────────────────────────────────
        public static bool FindRoadExtension(
            in NativeHashMap<int2, RoadCell>      roadLayer,
            in NativeHashMap<int2, OccupancyCell> occupancyLayer,
            in NativeHashMap<int2, TerrainCell>   terrainLayer,
            in NativeHashMap<int2, ResourceCell>  resourceLayer,
            int ownerLocalId, byte roadSize, int maxSteps,
            out int2 outOrigin, out int outDir, out int outSteps)
        {
            outOrigin = default; outDir = 0; outSteps = 0;
            int bestSteps = 0;
            int s = math.max(1, roadSize);

            foreach (var kv in roadLayer)
            {
                var rc = kv.Value;
                if (rc.OwnerLocalId != ownerLocalId) continue;
                if (!kv.Key.Equals(rc.FootprintOrigin)) continue;  // footprint 원점만 (중복 제거)

                int2 O   = rc.FootprintOrigin;
                byte myH  = CellHeight(O, terrainLayer);

                for (int d = 0; d < 4; d++)
                {
                    int2 off = RoadDirOps.Offsets[d];
                    int2 cur = O + off * s;     // 첫 인접 footprint 원점
                    int steps = 0;
                    while (steps < maxSteps &&
                           RoadFootprintFree(cur, s, myH,
                               occupancyLayer, terrainLayer, resourceLayer, roadLayer))
                    {
                        steps++;
                        cur += off * s;
                    }
                    if (steps > bestSteps)
                    {
                        bestSteps = steps;
                        outOrigin = O + off * s;
                        outDir    = d;
                    }
                }
            }

            outSteps = bestSteps;
            return bestSteps > 0;
        }

        // 도로 footprint(size×size)가 비어있고 평탄(reqHeight)하며 맵 안인가.
        // (도로 배치 규칙과 일치: 환경물은 비어있는 것으로 봄, 자원/점유/단차/경계는 막음)
        static bool RoadFootprintFree(
            int2 origin, int size, byte reqHeight,
            in NativeHashMap<int2, OccupancyCell> occupancyLayer,
            in NativeHashMap<int2, TerrainCell>   terrainLayer,
            in NativeHashMap<int2, ResourceCell>  resourceLayer,
            in NativeHashMap<int2, RoadCell>      roadLayer)
        {
            for (int dx = 0; dx < size; dx++)
            for (int dz = 0; dz < size; dz++)
            {
                int2 c = origin + new int2(dx, dz);
                if (!terrainLayer.ContainsKey(c)) return false;   // 맵 경계
                if (roadLayer.ContainsKey(c))     return false;   // 이미 도로
                if (occupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty
                    && occ.Type != OccupantType.Environment) return false;
                if (resourceLayer.IsCreated
                    && resourceLayer.TryGetValue(c, out var res) && res.Amount > 0) return false;
                if (CellHeight(c, terrainLayer) != reqHeight) return false; // 단차
            }
            return true;
        }

        static byte CellHeight(int2 c, in NativeHashMap<int2, TerrainCell> terrainLayer)
            => terrainLayer.IsCreated && terrainLayer.TryGetValue(c, out var tc)
                ? tc.Height : (byte)0;
    }
}
