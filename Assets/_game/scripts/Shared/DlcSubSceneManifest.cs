using Unity.Entities.Serialization;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  DlcSubSceneManifest  (ScriptableObject)
    //
    //  DLC 하나의 SubScene 참조를 보관한다.
    //  DlcBootstrap이 이 SO를 Addressables로 로드한 뒤
    //  SceneSystem을 통해 SubScene을 동적으로 불러온다.
    //
    //  설정:
    //    DlcId         = GamePrefabRegistry.DlcId와 동일
    //    DlcName       = "Origin", "DLC1" 등
    //    SceneReference = 해당 .unity SubScene 파일을 드래그
    //
    //  Addressables:
    //    주소  = DlcAddressConfig.SubScene(DlcName)
    //    라벨  = DlcAddressConfig.LabelDlc(DlcName)
    //    그룹  = DlcName (Origin = Local, DLC = Remote)
    //
    //  메뉴: Assets > Create > CitySim > DLC SubScene Manifest
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/DLC SubScene Manifest",
        fileName = "DlcSubSceneManifest")]
    public class DlcSubSceneManifest : ScriptableObject
    {
        [Header("DLC Identity")]
        public int    DlcId;
        public string DlcName;

        [Header("SubScene")]
        [Tooltip("베이킹된 ECS SubScene 파일(.unity)을 여기에 연결합니다.")]
        public EntitySceneReference SceneReference;
    }
}
