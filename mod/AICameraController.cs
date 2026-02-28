using RoR2;
using UnityEngine;

namespace Rainflayer
{
    /// <summary>
    /// Implements ICameraStateProvider to control the camera for AI gameplay.
    /// Based on PhotoMode's approach but adapted for third-person AI control.
    /// </summary>
    public class AICameraController : MonoBehaviour, ICameraStateProvider
    {
        private CameraRigController cameraRig;
        private CharacterBody targetBody;
        private Vector3 currentAimDirection;

        // Camera positioning parameters (dynamic based on pitch angle)
        private const float CAMERA_DISTANCE_MIN = 5f;      // Close when looking up
        private const float CAMERA_DISTANCE_MAX = 12f;     // Far when looking down (bird's eye)
        private const float CAMERA_DISTANCE_NORMAL = 8f;   // Default distance
        private const float CAMERA_HEIGHT_MIN = 1.5f;      // Low height when looking up
        private const float CAMERA_HEIGHT_MAX = 4f;        // High height when looking down (bird's eye)
        private const float CAMERA_HEIGHT_OFFSET = 2f;     // Normal height
        private const float MIN_CAMERA_DISTANCE = 2f;      // Minimum distance when colliding

        // Camera smoothing (only rotation, position follows 1:1)
        private Quaternion smoothedRotation;
        private bool isInitialized = false;
        private const float ROTATION_SMOOTH_TIME = 0.25f; // Slower for smoother camera panning

        public void Initialize(CameraRigController rig, CharacterBody body)
        {
            cameraRig = rig;
            targetBody = body;

            // Register as camera override
            if (cameraRig != null)
            {
                cameraRig.SetOverrideCam(this, 0.5f); // 0.5s blend time
                RainflayerPlugin.Instance.LogDebug("[AICameraController] Registered camera override");
            }
        }

        public void SetAimDirection(Vector3 aimDirection)
        {
            currentAimDirection = aimDirection;
        }

        public void GetCameraState(CameraRigController cameraRigController, ref CameraState cameraState)
        {
            if (targetBody == null || targetBody.inputBank == null)
                return;

            // Use the aim direction from InputBank (which AI sets)
            Vector3 aimDir = targetBody.inputBank.aimDirection;
            if (aimDir.magnitude < 0.1f)
                aimDir = targetBody.transform.forward; // Fallback

            // Player position (use corePosition for better centering)
            Vector3 playerPos = targetBody.corePosition;

            // Calculate pitch angle (vertical component) for dynamic camera behavior
            float pitch = Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) * Mathf.Rad2Deg; // -90 to +90 degrees

            // DYNAMIC DISTANCE & HEIGHT based on pitch (vanilla RoR2 behavior)
            float cameraDistance;
            float cameraHeight;

            if (pitch < -10f) // Looking down
            {
                // Increase distance AND height (rise into sky for bird's eye view)
                float downFactor = Mathf.Clamp01((-pitch - 10f) / 50f); // 0 to 1 as pitch goes -10 to -60
                cameraDistance = Mathf.Lerp(CAMERA_DISTANCE_NORMAL, CAMERA_DISTANCE_MAX, downFactor);
                cameraHeight = Mathf.Lerp(CAMERA_HEIGHT_OFFSET, CAMERA_HEIGHT_MAX, downFactor); // RISE UP
            }
            else if (pitch > 30f) // Looking up
            {
                // Clamp at 45 degrees max, slightly decrease distance and height
                float upFactor = Mathf.Clamp01((pitch - 30f) / 15f);
                cameraDistance = Mathf.Lerp(CAMERA_DISTANCE_NORMAL, CAMERA_DISTANCE_MIN, upFactor);
                cameraHeight = Mathf.Lerp(CAMERA_HEIGHT_OFFSET, CAMERA_HEIGHT_MIN, upFactor); // LOWER DOWN

                // TODO: Make character semi-transparent when looking up (requires material access)
            }
            else // Normal forward view
            {
                cameraDistance = CAMERA_DISTANCE_NORMAL;
                cameraHeight = CAMERA_HEIGHT_OFFSET;
            }

            // Calculate desired camera position
            // Position camera behind and above the player, opposite to aim direction
            Vector3 horizontalAimDir = aimDir;
            horizontalAimDir.y = 0; // Remove vertical component for horizontal offset
            if (horizontalAimDir.magnitude < 0.1f)
                horizontalAimDir = targetBody.transform.forward; // Fallback to facing direction
            horizontalAimDir.Normalize();

            // Camera offset: behind horizontally, plus height offset (now dynamic!)
            Vector3 desiredCameraOffset = -horizontalAimDir * cameraDistance;
            desiredCameraOffset.y = cameraHeight;

            Vector3 desiredCameraPos = playerPos + desiredCameraOffset;

            // Collision detection: raycast from player to camera position
            // If terrain is in the way, move camera closer
            float actualDistance = cameraDistance;
            RaycastHit hit;
            Vector3 rayDirection = desiredCameraOffset.normalized;
            float rayDistance = desiredCameraOffset.magnitude;

            // Raycast from player toward camera position
            if (Physics.Raycast(playerPos, rayDirection, out hit, rayDistance, LayerMask.GetMask("World")))
            {
                // Collision detected - place camera just before the hit point
                actualDistance = Mathf.Max(hit.distance - 0.5f, MIN_CAMERA_DISTANCE);
                desiredCameraPos = playerPos + rayDirection * actualDistance;
            }

            // Camera looks in the aim direction (not at the player)
            // This ensures crosshair aligns with where shots go
            Quaternion desiredCameraRotation = Quaternion.LookRotation(aimDir);

            // Initialize smoothed rotation on first frame
            if (!isInitialized)
            {
                smoothedRotation = desiredCameraRotation;
                isInitialized = true;
            }

            // SMOOTH camera rotation only (for smooth panning/aiming)
            // Position follows player 1:1 with no lag
            smoothedRotation = Quaternion.Slerp(smoothedRotation, desiredCameraRotation, ROTATION_SMOOTH_TIME);

            // Set camera state - position is instant, rotation is smoothed
            cameraState.position = desiredCameraPos;
            cameraState.rotation = smoothedRotation;
        }

        public bool IsUserLookAllowed(CameraRigController cameraRigController)
        {
            // Don't allow user to look around - AI controls the camera
            return false;
        }

        public bool IsUserControlAllowed(CameraRigController cameraRigController)
        {
            // Don't allow user to control camera - AI controls it
            return false;
        }

        public bool IsHudAllowed(CameraRigController cameraRigController)
        {
            // Allow HUD to display normally
            return true;
        }

        public void Disable()
        {
            if (cameraRig != null)
            {
                cameraRig.SetOverrideCam(null, 0.5f);
                RainflayerPlugin.Instance.LogDebug("[AICameraController] Disabled camera override");
            }
        }

        void OnDestroy()
        {
            Disable();
        }
    }
}
