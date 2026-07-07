using UnityEngine;

namespace reromanlee.Transvoxel.Demo
{
    /// <summary>
    /// Minimal editor-style fly camera for the demo: hold the right mouse button to look
    /// around, WASD to move, Q/E to descend/ascend, Shift to go fast.
    /// </summary>
    public class FlyCamera : MonoBehaviour
    {
        public float moveSpeed = 24f;
        public float fastMultiplier = 5f;
        public float lookSensitivity = 2.4f;

        float yaw;
        float pitch;

        void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            yaw = euler.y;
            pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        void Update()
        {
            bool looking = Input.GetMouseButton(1);
            Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !looking;

            if (looking)
            {
                yaw += Input.GetAxis("Mouse X") * lookSensitivity;
                pitch -= Input.GetAxis("Mouse Y") * lookSensitivity;
                pitch = Mathf.Clamp(pitch, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += transform.forward;
            if (Input.GetKey(KeyCode.S)) move -= transform.forward;
            if (Input.GetKey(KeyCode.D)) move += transform.right;
            if (Input.GetKey(KeyCode.A)) move -= transform.right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;

            float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1f);
            transform.position += move * (speed * Time.deltaTime);
        }
#endif
    }
}
