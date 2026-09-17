using UnityEngine;
using UnityEngine.InputSystem;

namespace reromanlee.Transvoxel.Samples
{
    /// <summary>
    /// Editor-style fly camera for the demo, on the Input System.
    ///
    /// Actions are built in code rather than shipped as an .inputactions asset: the demo's
    /// controls are fixed, and an asset would be one more thing to keep in sync with a
    /// sample whose whole point is to be readable in one file. Copy the bindings below into
    /// your own action asset when you build real controls.
    ///
    /// The cursor is only captured while the look button is held, so the on-screen UI panel
    /// stays clickable the rest of the time.
    /// </summary>
    [AddComponentMenu("Transvoxel/Demo Fly Camera")]
    public sealed class TransvoxelFlyCamera : MonoBehaviour
    {
        [Tooltip("Metres per second at normal speed.")]
        public float moveSpeed = 24f;

        [Tooltip("Multiplier while the boost button is held.")]
        public float fastMultiplier = 5f;

        [Tooltip("Degrees of rotation per unit of mouse movement.")]
        public float lookSensitivity = 0.12f;

        [Tooltip("Degrees per second at full gamepad stick deflection.")]
        public float gamepadLookSpeed = 180f;

        InputAction move;
        InputAction look;
        InputAction lookHold;
        InputAction boost;

        float yaw;
        float pitch;

        /// <summary>True while the camera has captured the cursor (UI must ignore input).</summary>
        public bool IsLooking { get; private set; }

        void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            yaw = euler.y;
            pitch = euler.x > 180f ? euler.x - 360f : euler.x;

            move = new InputAction("Fly", InputActionType.Value);
            // Digital keyboard composite: every key contributes a full unit.
            move.AddCompositeBinding("3DVector")
                .With("Forward", "<Keyboard>/w")
                .With("Backward", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d")
                .With("Up", "<Keyboard>/e")
                .With("Down", "<Keyboard>/q");
            // Analog stick composite (mode=2), so partial deflection flies slowly.
            move.AddCompositeBinding("3DVector(mode=2)")
                .With("Forward", "<Gamepad>/leftStick/up")
                .With("Backward", "<Gamepad>/leftStick/down")
                .With("Left", "<Gamepad>/leftStick/left")
                .With("Right", "<Gamepad>/leftStick/right")
                .With("Up", "<Gamepad>/buttonSouth")
                .With("Down", "<Gamepad>/buttonEast");

            look = new InputAction("Look", InputActionType.Value);
            look.AddBinding("<Mouse>/delta");
            look.AddBinding("<Gamepad>/rightStick");

            lookHold = new InputAction("LookHold", InputActionType.Button);
            lookHold.AddBinding("<Mouse>/rightButton");

            boost = new InputAction("Boost", InputActionType.Button);
            boost.AddBinding("<Keyboard>/leftShift");
            boost.AddBinding("<Keyboard>/rightShift");
            boost.AddBinding("<Gamepad>/leftStickPress");

            move.Enable();
            look.Enable();
            lookHold.Enable();
            boost.Enable();
        }

        void OnDisable()
        {
            move?.Disable();
            look?.Disable();
            lookHold?.Disable();
            boost?.Disable();
            move?.Dispose();
            look?.Dispose();
            lookHold?.Dispose();
            boost?.Dispose();
            ReleaseCursor();
        }

        void Update()
        {
            bool holding = lookHold.IsPressed();
            if (holding != IsLooking)
            {
                IsLooking = holding;
                Cursor.lockState = holding ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !holding;
            }

            Vector2 lookDelta = look.ReadValue<Vector2>();
            // The mouse reports pixels since the last frame; a stick reports a constant
            // deflection, so it has to be scaled by time to be frame-rate independent.
            bool fromGamepad = Gamepad.current != null
                               && Gamepad.current.rightStick.ReadValue().sqrMagnitude > 0.01f;
            if (fromGamepad)
            {
                yaw += lookDelta.x * gamepadLookSpeed * Time.deltaTime;
                pitch -= lookDelta.y * gamepadLookSpeed * Time.deltaTime;
            }
            else if (IsLooking)
            {
                yaw += lookDelta.x * lookSensitivity;
                pitch -= lookDelta.y * lookSensitivity;
            }
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

            Vector3 input = move.ReadValue<Vector3>();
            Vector3 direction = transform.right * input.x
                                + Vector3.up * input.y
                                + transform.forward * input.z;
            float speed = moveSpeed * (boost.IsPressed() ? fastMultiplier : 1f);
            transform.position += direction * (speed * Time.deltaTime);
        }

        void ReleaseCursor()
        {
            IsLooking = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
