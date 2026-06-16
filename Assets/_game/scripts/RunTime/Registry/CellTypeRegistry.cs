using System.Collections.Generic;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  CellTypeRegistry  (ScriptableObject)
    //
    //  모든 CellTypeDefinition SO를 한 곳에서 관리.
    //  CellTypeRegistryAuthoring이 이 SO를 베이킹에 사용.
    //
    //  메뉴: Assets > Create > CitySim > Cell Type Registry
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Cell Type Registry",
        fileName = "CellTypeRegistry")]
    public class CellTypeRegistry : ScriptableObject
    {
        public List<CellTypeDefinition> Types = new();
    }
}
