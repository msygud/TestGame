using Unity.Collections;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════
    //  EntranceOps  — 건물 입구 ↔ 도로 정렬 판정 헬퍼 (순수 함수, 단일 입구)
    //
    //  설계 원칙: "헬퍼는 사실/가능성을 나열한다. 결정은 시스템이 진다."
    //    · "이 입구가 도로에 닿는가" (사실)       → IsEntranceOnRoad
    //    · "어느 회전이면 닿는가"   (가능성 나열)  → FindRoadFacingRotation
    //    · "탈락/표시/거부"는 호출 시스템의 정책.
    //
    //  세 호출자가 같은 사실을 공유한다 (검증 로직 단일 소스):
    //    · FactionBaseSpawnSystem — SO의 RotationY 그대로, 검증만 (디자이너 방어).
    //    · AiCityGrowthSystem     — FindRoadFacingRotation 으로 회전 탐색 후 발행.
    //    · 인간 프리뷰             — IsEntranceOnRoad 로 닿는지 시각화 (강제 아님).
    //    · BuildingPlacementSystem— AI 요청의 최후 방어선 검증.
    //
    //  단일 입구 규약 (단일 = 다중의 특수형):
    //    · 입구 = (Offset 셀, Dir 방향).
    //      - Offset : footprint 원점(좌하단=최소코너) 기준 상대 셀. 0 ≤ Offset < Size.
    //                 즉 footprint 안의 경계 셀(비음수).
    //      - Dir    : 입구가 바라보는 바깥 방향 (RoadDir 단일 비트 N/E/S/W).
    //    · 입구셀   = origin + RotateOffset(Offset, Size, rot).
    //    · 도로셀   = 입구셀 + RotateDirOffset(Dir, rot).   (입구가 향하는 한 칸 바깥)
    //    · "입구가 도로에 닿는다" = 그 도로셀이 RoadLayer에 존재.
    //      (4방 전체가 아니라 Dir 방향 1셀만 본다 → 건물이 도로에 '정면으로' 붙음.)
    //
    //  회전 규약 (CCW, 최소코너 유지):
    //    · BuildingPlacementSystem.EmitSingle 이 quaternion.RotateY(radians(RotationY))
    //      를 쓰므로, 입구 오프셋/방향도 동일 Y축(+CCW) 90° 단위로 회전.
    //    · rotSteps = 0/1/2/3 = 0°/90°/180°/270°.
    //    · RotateOffset은 footprint 셀이므로 회전 후 "최소코너=(0,0)"으로 정규화
    //      (음수 사분면으로 넘어가지 않음 → RotateSize의 +x/+z 펼침 규약과 정합).
    //    · RotateDirOffset은 단위 방향벡터이므로 정규화 없이 순수 CCW 회전
    //      (방향은 음수 성분을 가질 수 있다 — 정상).
    //
    //  Burst 호환: managed 타입 없음, NativeHashMap은 in으로 받음.
    // ══════════════════════════════════════════════════════════════════
    public static class EntranceOps
    {
        // ──────────────────────────────────────────────────────────────
        //  회전: RotationY(도) → 90° 스텝(0~3). 가장 가까운 직각으로 양자화.
        // ──────────────────────────────────────────────────────────────
        public static int RotationToSteps(float rotationYDeg)
        {
            int q = (int)math.round(rotationYDeg / 90f);
            return ((q % 4) + 4) & 3;
        }

        /// <summary>rotSteps(0~3) → RotationY(도). 발행 시 PlaceBuildingRequest에 실음.</summary>
        public static float StepsToRotationY(int steps) => ((steps & 3) * 90f);

        // ──────────────────────────────────────────────────────────────
        //  footprint 셀 오프셋 회전 — 최소코너 유지 정규화 버전.
        //
        //  순수 CCW: (x,z) → (-z, x). 그대로 쓰면 음수 사분면으로 넘어가므로,
        //  회전된 footprint의 최소코너가 다시 (0,0)이 되도록 평행이동한다.
        //  size는 회전 "전" 원본 Size (회전 후 크기는 RotateSize로 구함).
        //
        //  보장: 입력이 0 ≤ p < size 이면 출력도 0 ≤ out < RotateSize(size,steps).
        // ──────────────────────────────────────────────────────────────
        public static int2 RotateOffset(int2 p, int2 size, int steps)
        {
            switch (steps & 3)
            {
                case 1: return new int2(size.y - 1 - p.y, p.x);            // 90  CCW
                case 2: return new int2(size.x - 1 - p.x, size.y - 1 - p.y); // 180
                case 3: return new int2(p.y, size.x - 1 - p.x);            // 270 CCW
                default: return p;                                          // 0
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  방향 단위벡터 회전 — 정규화 없는 순수 CCW.
        //
        //  Dir(RoadDir 단일 비트) → 단위 오프셋 → CCW steps 회전 → 단위 오프셋.
        //  방향은 footprint가 아니므로 음수 성분 허용(예: W=(-1,0)).
        // ──────────────────────────────────────────────────────────────
        public static int2 RotateDirOffset(RoadDir dir, int steps)
        {
            int2 d = DirToOffset(dir);
            for (int i = 0; i < (steps & 3); i++)
                d = new int2(-d.y, d.x);   // CCW 90°
            return d;
        }

        /// <summary>RoadDir 단일 비트 → 단위 셀 오프셋. None/복합비트는 (0,0).</summary>
        public static int2 DirToOffset(RoadDir dir) => dir switch
        {
            RoadDir.N => new int2(0, 1),   // +Z
            RoadDir.E => new int2(1, 0),   // +X
            RoadDir.S => new int2(0, -1),   // -Z
            RoadDir.W => new int2(-1, 0),   // -X
            _ => int2.zero,
        };

        // ──────────────────────────────────────────────────────────────
        //  회전 유효 footprint 크기. 90°/270°에서 Size.x ↔ Size.y 교환.
        // ──────────────────────────────────────────────────────────────
        public static int2 RotateSize(int2 size, int steps)
            => (steps & 1) == 1 ? new int2(size.y, size.x) : size;

        // ──────────────────────────────────────────────────────────────
        //  단일 입구가 도로에 닿는가 — "사실" 1개.
        //
        //  입구셀 = origin + RotateOffset(entrance.Offset, size, rot).
        //  도로셀 = 입구셀 + RotateDirOffset(entrance.Dir, rot).
        //  그 도로셀이 RoadLayer에 존재하면 true.
        //
        //  size: 건물 원본 footprint Size (회전 정규화에 필요).
        // ──────────────────────────────────────────────────────────────
        public static bool IsEntranceOnRoad(
            int2 origin,
            int2 size,
            in EntranceInfo entrance,
            int rotSteps,
            in NativeHashMap<int2, RoadCell> roadLayer)
        {
            int2 entranceCell = origin + RotateOffset(entrance.Offset, size, rotSteps);
            int2 roadCell = entranceCell + RotateDirOffset((RoadDir)entrance.Dir, rotSteps);
            return roadLayer.ContainsKey(roadCell);
        }

        // ──────────────────────────────────────────────────────────────
        //  회전 탐색 — "어느 90° 회전이면 입구가 도로를 향하는가?" (가능성 나열)
        //
        //  0→1→2→3 순으로 처음 통과하는 steps 반환. 어느 회전으로도 안 닿으면 -1.
        //
        //  ⚠ 비정사각 footprint는 회전 시 점유 셀 집합이 바뀌므로(Size.x↔y),
        //    입구가 닿더라도 그 회전에서 footprint가 범위 밖/충돌일 수 있다.
        //    그 경우 footprintFree 콜백 오버로드를 쓴다. 정사각이면 이 버전으로 충분.
        // ──────────────────────────────────────────────────────────────
        public static int FindRoadFacingRotation(
            int2 origin,
            int2 size,
            in EntranceInfo entrance,
            in NativeHashMap<int2, RoadCell> roadLayer)
        {
            for (int steps = 0; steps < 4; steps++)
            {
                if (IsEntranceOnRoad(origin, size, in entrance, steps, in roadLayer))
                    return steps;
            }
            return -1;
        }

        // ──────────────────────────────────────────────────────────────
        //  회전 탐색 (footprint 인지 버전) — 비정사각 건물용.
        //
        //  각 회전 후보에 대해:
        //    ① 회전된 footprint가 비어 있고 범위 안인가 (footprintFree 콜백)
        //    ② 입구가 도로를 향하는가 (IsEntranceOnRoad)
        //  를 함께 만족하는 첫 회전 반환. 없으면 -1.
        //
        //  footprintFree(origin, effectiveSize) → bool : 호출 시스템이 제공.
        //  ⚠ Burst 비호환: System.Func 델리게이트 사용. 저빈도(DayChanged 1회)라 무방.
        // ──────────────────────────────────────────────────────────────
        public static int FindRoadFacingRotation(
            int2 origin,
            int2 size,
            in EntranceInfo entrance,
            in NativeHashMap<int2, RoadCell> roadLayer,
            System.Func<int2, int2, bool> footprintFree)
        {
            for (int steps = 0; steps < 4; steps++)
            {
                int2 eff = RotateSize(size, steps);

                if (footprintFree != null && !footprintFree(origin, eff))
                    continue;

                if (IsEntranceOnRoad(origin, size, in entrance, steps, in roadLayer))
                    return steps;
            }
            return -1;
        }
    }
}
