using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  OwnerShared — 소유: 개별 플레이어 단위 (모든 소유 엔티티 공통 골격)
    // ──────────────────────────────────────────────────────────────────────────
    //  소유 단위는 "개별 플레이어(LocalId 0~7)" 하나로 통일한다. 팀 개념을 쓰지
    //  않음 — 시민·건물·도로·캐리어·워커·stamp 슬롯이 모두 같은 LocalId 축으로
    //  정렬되어, 교차 참조(시민→자기 stamp 슬롯, 도로 소유 검사 등)가 한 키로 떨어진다.
    //  **모든 소유물이 이 한 골격으로 정렬된다** (예외를 만들면 확장 때 예외가 예외를 부른다).
    //
    //  SharedComponent인 이유: LocalId는 생성 후 바뀌지 않으므로(capture=파괴) 청크가
    //  플레이어별로 갈린다 → WithSharedComponentFilter(LocalId)로 한 플레이어의
    //  엔티티만 묶어 그 플레이어의 리소스(stamp 슬롯·공유 풀 등)로 일괄 처리(거의 공짜).
    //  단일 SharedComponent라 조합 폭발 없음.
    //
    //  ★필드 vs SharedComponent (이중이지만 drift 없음):
    //    · Burst 잡은 SharedComponent 값을 per-entity로 못 읽으므로, 잡 안에서 owner
    //      값이 필요한 곳은 IComponentData 필드(BuildingFootprint.OwnerLocalId 등)에서
    //      읽는다. OwnerShared는 시스템 레벨 청크 필터 전용.
    //    · 둘 다 스폰 시 같은 OwnerLocalId로 세팅되고, 소유권은 스폰 후 불변이므로
    //      두 소스가 어긋날 수 없다(capture=파괴, 소유자 변경 없음).
    // ══════════════════════════════════════════════════════════════════════════
    public struct OwnerShared : ISharedComponentData
    {
        public int LocalId;   // 소유 플레이어 슬롯 (0~7). stamp[LocalId] 등에 직접 사용.

        public OwnerShared(int localId)
        {
            LocalId = localId;
        }
    }
}
