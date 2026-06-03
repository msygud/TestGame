using Unity.Collections;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════
    //  EntranceOps  — 건물 입구 ↔ 도로 정렬 판정 헬퍼 (순수 함수)
    //
    //  설계 원칙(BlockOps와 동일): "헬퍼는 사실/가능성을 나열한다. 결정은 시스템이 진다."
    //    · "이 입구가 도로에 닿는가" (사실)            → IsEntranceOnRoad / AreAllEntrancesOnRoad
    //    · "어느 회전이면 닿는가"   (가능성 나열)      → FindRoadFacingRotation
    //    · "탈락시킬지 / 정보만 보여줄지 / 거부할지"는 호출 시스템의 정책.
    //
    //  세 호출자가 같은 사실을 공유한다 (검증 로직 단일 소스):
    //    · FactionBaseSpawnSystem — SO의 RotationY 그대로, 검증만 (디자이너 실수 방어).
    //    · AiCityGrowthSystem     — FindRoadFacingRotation 으로 회전 탐색 후 발행.
    //    · 인간 프리뷰             — IsEntranceOnRoad 로 닿는지 시각화 (강제 아님).
    //    · BuildingPlacementSystem— AI 요청의 최후 방어선 검증.
    //
    //  좌표 규약:
    //    · entranceOffset = 프리팹 footprint 원점 기준 상대 셀 (EntranceLookup 값).
    //    · "입구셀"        = origin + Rotate(entranceOffset, rot).
    //    · "입구가 도로에 닿는다" = 입구셀에 인접한 4방 중 하나가 RoadLayer에 존재.
    //      (입구셀 자신은 건물 footprint일 수 있으므로, 입구셀의 바깥 인접을 본다.)
    //
    //  회전 규약:
    //    · BuildingPlacementSystem.EmitSingle 이 quaternion.RotateY(radians(RotationY))
    //      를 쓰므로, 입구 오프셋도 동일하게 Y축(+CCW) 90° 단위로 회전시킨다.
    //    · rotSteps = 0/1/2/3 = 0°/90°/180°/270°.
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
            // 음수·360 초과도 안전하게 0~3로 환원.
            int q = (int)math.round(rotationYDeg / 90f);
            return ((q % 4) + 4) & 3;
        }

        // ──────────────────────────────────────────────────────────────
        //  오프셋 회전: footprint 원점 기준 상대 셀을 Y축 90°×steps 회전.
        //
        //  +Z=N, +X=E 규약에서 CCW(+) 90° 회전: (x,z) → (-z, x).
        //  steps 번 적용. RotationY가 시계/반시계 어느 쪽이든 EmitSingle의
        //  quaternion.RotateY와 동일 부호를 쓰므로 시각/논리가 일치한다.
        // ──────────────────────────────────────────────────────────────
        public static int2 RotateOffset(int2 offset, int steps)
        {
            int2 o = offset;
            for (int i = 0; i < (steps & 3); i++)
                o = new int2(-o.y, o.x);   // CCW 90°
            return o;
        }

        // ──────────────────────────────────────────────────────────────
        //  단일 입구가 도로에 닿는가 — "사실" 1개.
        //
        //  입구셀 = origin + RotateOffset(entranceOffset, rotSteps).
        //  그 입구셀의 4방 인접 중 하나라도 RoadLayer에 존재하면 true.
        // ──────────────────────────────────────────────────────────────
        public static bool IsEntranceOnRoad(
            int2 origin,
            int2 entranceOffset,
            int  rotSteps,
            in NativeHashMap<int2, RoadCell> roadLayer)
        {
            int2 entranceCell = origin + RotateOffset(entranceOffset, rotSteps);

            for (int d = 0; d < 4; d++)
            {
                int2 adj = entranceCell + RoadDirOps.Offsets[d];
                if (roadLayer.ContainsKey(adj))
                    return true;
            }
            return false;
        }

        // ──────────────────────────────────────────────────────────────
        //  모든 입구가 도로에 닿는가 — 건물 단위 판정.
        //
        //  정책 선택지를 위해 mode를 둔다:
        //    · RequireAll = true  : 입구가 여러 개면 전부 도로에 닿아야 통과.
        //    · RequireAll = false : 하나라도 닿으면 통과(기본).
        //
        //  입구 정의가 없으면(EntranceLookup.Has == false) 도로 접근 제약을
        //  적용할 수 없으므로 true 반환(제약 없음). 입구 없는 건물은 도로 검증
        //  대상이 아니라는 의미.
        // ──────────────────────────────────────────────────────────────
        public static bool AreEntrancesOnRoad(
            int2 origin,
            int  rotSteps,
            in FixedList64Bytes<int2> entranceOffsets,
            in NativeHashMap<int2, RoadCell> roadLayer,
            bool requireAll = false)
        {
            if (entranceOffsets.Length == 0)
                return true;   // 입구 정의 없음 → 제약 없음

            bool anyOnRoad = false;

            for (int i = 0; i < entranceOffsets.Length; i++)
            {
                bool onRoad = IsEntranceOnRoad(
                    origin, entranceOffsets[i], rotSteps, in roadLayer);

                if (requireAll)
                {
                    if (!onRoad) return false;   // 하나라도 미달이면 실패
                }
                else
                {
                    anyOnRoad |= onRoad;
                }
            }

            return requireAll ? true : anyOnRoad;
        }

        // ──────────────────────────────────────────────────────────────
        //  회전 탐색 — "어느 90° 회전이면 입구가 도로에 닿는가?" (가능성 나열)
        //
        //  AI가 작은 건물을 구획에 넣을 때 입구가 경계 도로를 향하도록 회전을
        //  고르는 데 쓴다. 0→1→2→3 순으로 처음 통과하는 steps를 반환.
        //
        //  반환: 통과하는 rotSteps(0~3). 어느 회전으로도 안 닿으면 -1.
        //  주의: 회전은 입구 방향만 바꾼다. 정사각이 아닌 footprint의 점유/범위
        //        검증(회전 시 Size.x↔Size.y 교환)은 이 헬퍼의 책임이 아니다 —
        //        호출 시스템이 ValidateCells 등으로 별도 처리한다.
        // ──────────────────────────────────────────────────────────────
        public static int FindRoadFacingRotation(
            int2 origin,
            in FixedList64Bytes<int2> entranceOffsets,
            in NativeHashMap<int2, RoadCell> roadLayer,
            bool requireAll = false)
        {
            if (entranceOffsets.Length == 0)
                return 0;   // 입구 없음 → 회전 무관, 기본 0 반환

            for (int steps = 0; steps < 4; steps++)
            {
                if (AreEntrancesOnRoad(
                        origin, steps, in entranceOffsets, in roadLayer, requireAll))
                    return steps;
            }
            return -1;   // 어느 회전으로도 도로에 닿지 않음
        }

        /// <summary>rotSteps(0~3) → RotationY(도). 발행 시 PlaceBuildingRequest에 실음.</summary>
        public static float StepsToRotationY(int steps) => ((steps & 3) * 90f);

        // ──────────────────────────────────────────────────────────────
        //  회전 유효 footprint 크기.
        //
        //  90°/270°(steps 홀수)에서 Size.x ↔ Size.y 교환.
        //  0°/180°(steps 짝수)는 원본 유지.
        //
        //  규약: origin은 회전과 무관하게 "최소 코너"로 유지하고 +x/+z 방향으로만
        //  펼친다. 따라서 크기 교환만으로 점유/검증 셀 집합이 정확해진다.
        //  (입구 오프셋도 동일 origin 기준으로 회전되므로 서로 정합.)
        // ──────────────────────────────────────────────────────────────
        public static int2 RotateSize(int2 size, int steps)
            => (steps & 1) == 1 ? new int2(size.y, size.x) : size;

        // ──────────────────────────────────────────────────────────────
        //  회전 탐색 (footprint 인지 버전) — 비정사각 건물용.
        //
        //  FindRoadFacingRotation은 입구가 도로에 닿는 회전만 본다. 비정사각
        //  footprint는 회전 시 점유 셀 집합이 바뀌므로(Size.x↔y 교환), 입구가
        //  닿더라도 그 회전에서 footprint가 범위 밖이거나 점유 충돌일 수 있다.
        //
        //  이 오버로드는 각 회전 후보에 대해:
        //    ① 입구가 도로에 닿는가 (AreEntrancesOnRoad)
        //    ② 회전된 footprint가 비어 있고 범위 안인가 (footprintFree 콜백)
        //  를 함께 만족하는 첫 회전을 반환한다.
        //
        //  footprintFree(origin, effectiveSize) → bool : 호출 시스템이 제공.
        //    (점유/범위/지형 판정은 시스템 책임 — 헬퍼는 도로 정렬만 안다는
        //     원칙을 지키되, footprint 충돌 여부는 콜백으로 위임받는다.)
        //
        //  정사각 footprint(Size.x==Size.y)면 ①만 보는 FindRoadFacingRotation과
        //  결과가 같다 — 그 경우 이 오버로드를 쓸 필요는 없다.
        //
        //  ⚠ Burst 비호환: System.Func 델리게이트를 받으므로 이 오버로드는 Burst
        //    컴파일 경로에서 호출할 수 없다. AI 성장은 DayChanged 1회 저빈도라
        //    문제없으나, Burst 시스템에서 쓰려면 콜백 대신 footprint 데이터를
        //    직접 넘기는 형태로 별도 오버로드를 만들어야 한다.
        // ──────────────────────────────────────────────────────────────
        public static int FindRoadFacingRotation(
            int2 origin,
            int2 size,
            in FixedList64Bytes<int2> entranceOffsets,
            in NativeHashMap<int2, RoadCell> roadLayer,
            System.Func<int2, int2, bool> footprintFree,
            bool requireAll = false)
        {
            for (int steps = 0; steps < 4; steps++)
            {
                int2 eff = RotateSize(size, steps);

                if (footprintFree != null && !footprintFree(origin, eff))
                    continue;   // 이 회전에선 footprint가 안 맞음

                bool entranceOk = entranceOffsets.Length == 0
                    || AreEntrancesOnRoad(origin, steps, in entranceOffsets, in roadLayer, requireAll);

                if (entranceOk)
                    return steps;
            }
            return -1;
        }
    }
}
