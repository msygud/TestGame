using Unity.Entities;
using UnityEngine;
using CitySim;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Stage A — 건물 Authoring
    //
    //  건물 종류·정원은 프리팹의 본질적 속성이며 거의 안 바뀐다(콜드).
    //  → 프리팹에 baker로 구워 둔다. SpawnSystem이 Instantiate하면
    //    ResidenceBuilding / WorkplaceBuilding / ServiceBuilding + BuildingOccupancy가
    //    자동으로 따라온다. 스폰 경로를 수정할 필요 없음.
    //
    //  배치:
    //    각 건물 프리팹(SubScene에서 베이크되는 프리팹)의 루트에 이 컴포넌트를 추가.
    //    Kind를 고르고 Capacity를 설정.
    //
    //  주의(§0):
    //    BuildingOccupancy는 "정원/예약" 카운트지 시민 명단이 아니다.
    //    - 집:   Capacity = 거주 정원(몇 명 사는가). 거주 점유는 장기 유지.
    //    - 직장: Capacity = 일자리 수.            소속 점유는 장기 유지.
    //    - 서비스: Capacity = 동시 수용(몇 명 들르나). 방문 예약은 출발 시 ++, 떠날 때 --.
    // ══════════════════════════════════════════════════════════════════════════

    public enum BuildingKind : byte
    {
        None = 0,
        Residence,   // 집
        Workplace,   // 직장
        Service,     // 서비스(식당·광장 등) — 욕구 해소 공급자
    }

    public class BuildingAuthoring : MonoBehaviour
    {
        [Header("공통")]
        [Tooltip("건물 종류.")]
        public BuildingKind Kind = BuildingKind.None;

        [Tooltip("정원(집=거주 정원, 직장=일자리 수, 서비스=동시 수용).")]
        public int Capacity = 1;

        [Header("직장 전용")]
        [Tooltip("Kind=Workplace일 때, 이 직장이 제공하는 직업.")]
        public JobType ProvidedJob = JobType.Unemployed;

        [Header("서비스 전용")]
        [Tooltip("Kind=Service일 때, 이 건물이 해소하는 욕구 조합(NeedType 비트마스크의 ulong 값).\n" +
                 "예: Hunger=1, Homeless=2, Unemployed=4, LowEntertainment=8. 여러 개면 합(OR).")]
        public ulong ReliefRaw = 0;

        // NeedType : ulong 은 Unity 직렬화(베이킹) 미지원 → ulong 백킹 필드 + 프로퍼티로 우회
        //   (RegistryItem.ReliefRaw/Relief와 동일 패턴). Baker는 이 프로퍼티로 읽는다.
        public NeedType ReliefMask => (NeedType)ReliefRaw;

        [Tooltip("Kind=Service일 때, 공급 영향력(도로 칸수). 반경 BFS 깊이.")]
        public int Influence = 5;

        class Baker : Baker<BuildingAuthoring>
        {
            public override void Bake(BuildingAuthoring a)
            {
                if (a.Kind == BuildingKind.None) return;

                var e = GetEntity(TransformUsageFlags.Dynamic);

                // 정원 카운트(모든 분류 공통)
                AddComponent(e, new BuildingOccupancy
                {
                    Current  = 0,
                    Capacity = a.Capacity < 1 ? 1 : a.Capacity,
                });

                switch (a.Kind)
                {
                    case BuildingKind.Residence:
                        AddComponent<ResidenceBuilding>(e);
                        break;

                    case BuildingKind.Workplace:
                        AddComponent(e, new WorkplaceBuilding
                        {
                            ProvidedJob = a.ProvidedJob,
                        });
                        break;

                    case BuildingKind.Service:
                        AddComponent(e, new ServiceBuilding
                        {
                            ReliefMask = a.ReliefMask,
                            Influence  = a.Influence < 0 ? 0 : a.Influence,
                        });
                        break;
                }
            }
        }
    }
}
