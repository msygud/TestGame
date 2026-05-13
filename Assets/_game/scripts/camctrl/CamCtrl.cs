using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.AddressableAssets;

namespace Game.CamCtrl
{
    public class CamCtrl : MonoBehaviour
    {
        public static CamCtrl Instance { get; private set; }
        private static CamCtrl _instance;

        public float moveSpeed = 10f;
        public float rotateSpeed = 100f;

        public Transform camRoot;
        public AssetLabelReference l;
        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            _instance = this;
        }

        // Update is called once per frame
        void Update()
        {
            if (Keyboard.current.wKey.isPressed)
            {
                camRoot.position += camRoot.forward * moveSpeed * Time.deltaTime;
            }
            if (Keyboard.current.sKey.isPressed)
            {
                camRoot.position -= camRoot.forward * moveSpeed * Time.deltaTime;
            }
            if (Keyboard.current.aKey.isPressed)
            {
                camRoot.position -= camRoot.right * moveSpeed * Time.deltaTime;
            }
            if (Keyboard.current.dKey.isPressed)
            {
                camRoot.position += camRoot.right * moveSpeed * Time.deltaTime;
            }

            if (Keyboard.current.qKey.isPressed)
            {
                camRoot.rotation *= Quaternion.Euler(0, -rotateSpeed * Time.deltaTime, 0);
            }
            else if (Keyboard.current.eKey.isPressed)
            {
                camRoot.rotation *= Quaternion.Euler(0, rotateSpeed * Time.deltaTime, 0);
            }

            if(Keyboard.current.leftShiftKey.isPressed)
            {
                camRoot.position += camRoot.up * moveSpeed * Time.deltaTime;
            }
            else if (Keyboard.current.leftCtrlKey.isPressed)
            {
                camRoot.position -= camRoot.up * moveSpeed * Time.deltaTime;
            }
        }
    }
}
