using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  FactionConfigAuthoring
    //
    //  FactionConfig(ECS 싱글톤)은 NativeHashMap을 담고 있어 베이킹 시
    //  서브씬에 직렬화할 수 없다 (포인터 포함 → ArgumentException).
    //  그래서 Baker는 데이터를 굽지 않고, 실제 싱글톤 생성은
    //  FactionConfigSystem.OnCreate에서 코드로 수행한다
    //  (GridInitSystem 등과 동일한 싱글톤 수명주기 패턴).
    //
    //  이 컴포넌트는 에디터에서 프로젝트의 FactionDefinition SO들을
    //  한눈에 모아보기 위한 참조 컨테이너 용도로만 남겨둔다.
    // ══════════════════════════════════════════════════════════════
    public class FactionConfigAuthoring : MonoBehaviour
    {
        [Tooltip("프로젝트의 모든 FactionDefinition SO 목록 (참조용, 베이킹되지 않음).\n" +
                 "실제 슬롯 값은 SkirmishLobby가 런타임에 채운다.")]
        public FactionDefinition[] FactionDefinitions;

        class Baker : Baker<FactionConfigAuthoring>
        {
            public override void Bake(FactionConfigAuthoring authoring) { }
        }
    }
}
