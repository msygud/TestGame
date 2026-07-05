using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CitizenVisualPrefabAuthoring — 시민 보행 비주얼 프리팹 연결
    //
    //  SubScene 안의 GameObject에 이 컴포넌트를 붙이고 CitizenPrefab에 소형
    //  프리팹(캡슐 등)을 할당하면 베이킹 시 CitizenVisualPrefabSingleton이 생성된다.
    //  (CarrierPrefabAuthoring과 동일 패턴 — 같은 GameObject에 둘 다 붙여도 됨.)
    //
    //  사용:
    //    1. SubScene 내에 빈 GameObject 생성(또는 기존 캐리어 오브젝트 재사용).
    //    2. 이 컴포넌트 부착.
    //    3. CitizenPrefab 필드에 소형 메시 프리팹(예: 0.3m 캡슐) 할당.
    //  미할당/미배치 시: 시민 이동은 타이머만(비주얼 생략) — 기능 저하 없음.
    // ══════════════════════════════════════════════════════════════════════════
    public class CitizenVisualPrefabAuthoring : MonoBehaviour
    {
        [Tooltip("시민 보행 비주얼 프리팹. 소형 캡슐 등 단순 메시 추천(운반자와 색 구분 권장).")]
        public GameObject CitizenPrefab;

        class Baker : Baker<CitizenVisualPrefabAuthoring>
        {
            public override void Bake(CitizenVisualPrefabAuthoring a)
            {
                if (a.CitizenPrefab == null) return;
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent(e, new CitizenVisualPrefabSingleton
                {
                    Prefab = GetEntity(a.CitizenPrefab, TransformUsageFlags.Dynamic),
                });
            }
        }
    }
}
