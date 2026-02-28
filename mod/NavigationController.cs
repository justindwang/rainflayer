using RoR2;
using RoR2.CharacterAI;
using RoR2.Navigation;
using UnityEngine;
using System;

namespace Rainflayer
{
    /// <summary>
    /// Handles navigation logic for AIController.
    /// Manages GOTO navigation, jump detection, and drop pod exit.
    /// </summary>
    public class NavigationController
    {
        private readonly AIController controller;
        private readonly EntityDetector entityDetector;

        // Navigation state
        public GameObject gotoTarget = null;
        public InteractableInfo gotoInteractable = null;
        public bool isNavigating = false;
        private Vector3 lastPosition;
        private float stuckTimer = 0f;
        private const float STUCK_THRESHOLD = 1.0f;  // Must move at least 1 m/s to count as progress (compared against per-frame displacement * Time.fixedDeltaTime)
        private const float STUCK_TIMEOUT = 5f;

        // Jump cooldown to prevent spam-jumping
        private float lastJumpTime = 0f;

        // NodeGraph pathfinding
        private PathFollower pathFollower = new PathFollower();
        private NodeGraph.PathRequest pathRequest = new NodeGraph.PathRequest();
        private float pathRecomputeTimer = 0f;
        private const float PATH_RECOMPUTE_INTERVAL = 2f;
        private bool lastPathWasReachable = false;
        private Vector3 lastNavTargetPosition = Vector3.zero;
        private GameObject lastGotoTarget = null;
        private InteractableInfo lastGotoInteractable = null;

        // Smooth direction interpolation during navigation (prevents camera jolts on waypoint transitions)
        private Vector3 smoothedNavMoveDir = Vector3.zero;
        private Vector3 smoothedNavAimDir = Vector3.zero;
        private const float NAV_MOVE_SMOOTH = 0.15f;  // Move direction lerp factor per fixed frame
        private const float NAV_AIM_SMOOTH = 0.07f;   // Aim/camera lerp factor (slower = smoother camera)

        // Enhanced stuck detection with frustration
        private float frustration = 0f;  // Increases when stuck, resets when making progress
        private const float FRUSTRATION_THRESHOLD = 10f;  // Give up after frustration reaches 10
        private const float FRUSTRATION_INCREASE_RATE = 1f;  // Increase by 1 per second while stuck
        public bool attemptStuck = false;  // Mark current target as potentially unreachable

        // Drop pod tracking
        private bool hasExitedDropPod = false;
        private float dropPodExitTimer = 0f;
        private const float DROP_POD_EXIT_DELAY = 5.0f; // Wait 5 seconds then auto-exit (animation takes time)
        private bool dropPodInteractionStarted = false; // Track if we've started the interaction (one-shot)
        private GameObject dropPodDoor = null; // Cache the drop pod door object

        // Interaction holding state
        private GameObject interactionTarget = null;
        private float interactionHoldTimer = 0f;
        private const float INTERACT_HOLD_DURATION = 1.5f;  // Hold interact for 1.5 seconds
        private const float INTERACT_DISTANCE_THRESHOLD = 2.5f;  // RoR2 interaction range is ~2-3m

        // Settle timer: how long the character must be within range before interaction fires.
        // Prevents overshoot with fast movement items (Goat Hoof, Energy Drink, etc.)
        private float interactSettleTimer = 0f;
        private const float INTERACT_SETTLE_DURATION = 0.5f;  // Must be within range for 0.5s before interacting

        // Interaction result (read by AIController after HandleInteractionHolding to send action_complete)
        public bool lastInteractionSucceeded = false;
        public string lastInteractionCommand = null;  // e.g. "FIND_AND_INTERACT:chest"

        public NavigationController(AIController controller, EntityDetector entityDetector)
        {
            this.controller = controller;
            this.entityDetector = entityDetector;
        }

        public void ResetDropPodState()
        {
            hasExitedDropPod = false;
            dropPodExitTimer = 0f;
            dropPodInteractionStarted = false;
            dropPodDoor = null;
            Log("[Drop Pod] State reset for new stage");
        }

        /// <summary>
        /// Handle drop pod exit (critical for autonomous play).
        /// Uses ONE-SHOT interaction pattern - same as chest interactions.
        /// </summary>
        public void HandleDropPodExit(CharacterBody body)
        {
            // Increment timer
            dropPodExitTimer += Time.fixedDeltaTime;

            // Wait a bit for drop pod animation to settle
            if (dropPodExitTimer < DROP_POD_EXIT_DELAY)
            {
                // Log every half second
                if (Time.frameCount % 30 == 0)
                {
                    Log($"[Drop Pod] Waiting to exit... ({dropPodExitTimer:F1}s / {DROP_POD_EXIT_DELAY}s)");
                }
                return;
            }

            // If we've already started the interaction, we're done - don't spam
            if (dropPodInteractionStarted)
            {
                // Wait a bit after starting interaction, then mark as complete
                if (dropPodExitTimer >= DROP_POD_EXIT_DELAY + 1.0f)
                {
                    hasExitedDropPod = true;
                    Log("[Drop Pod] ✓ Successfully exited drop pod (one-shot interaction complete)");
                }
                return;
            }

            // Log when we start trying (first time only)
            Log("[Drop Pod] Timer expired, attempting exit now...");

            // Try to find and activate drop pod door (ONE TIME ONLY)
            GameObject foundDoor = FindDropPodDoor(body);

            if (foundDoor != null)
            {
                // Cache the door reference
                dropPodDoor = foundDoor;

                // Perform ONE interaction attempt
                if (TryActivateDropPodDoorOnce(dropPodDoor, body))
                {
                    dropPodInteractionStarted = true;
                    Log($"[Drop Pod] ✓ One-shot interaction started with {foundDoor.name}");
                }
                else
                {
                    // Door found but interaction failed (might be disabled = already open)
                    // Check if it's disabled - if so, we're already out!
                    var interactor = body.GetComponent<Interactor>();
                    var interactable = foundDoor.GetComponent<IInteractable>();

                    if (interactable != null && interactor != null)
                    {
                        Interactability state = interactable.GetInteractability(interactor);
                        if (state == Interactability.Disabled)
                        {
                            // Door is disabled = already opened
                            dropPodInteractionStarted = true;
                            hasExitedDropPod = true;
                            Log("[Drop Pod] ✓ Door already open, skipping interaction");
                            return;
                        }
                    }

                    // Interaction failed but door not disabled - log and retry next frame
                    if (Time.frameCount % 30 == 0)
                    {
                        Log($"[Drop Pod] Found door but interaction failed, will retry...");
                    }
                }
            }
            else
            {
                // Keep trying every second
                if (Time.frameCount % 60 == 0)
                {
                    Log($"[Drop Pod] Still looking for drop pod exit... (timer: {dropPodExitTimer:F1}s)");
                }

                // Give up after 10 seconds (probably not in a drop pod)
                if (dropPodExitTimer > 10f)
                {
                    hasExitedDropPod = true;
                    Log("[Drop Pod] Timeout - assuming no drop pod or already exited");
                }
            }
        }

        public bool HasExitedDropPod()
        {
            return hasExitedDropPod;
        }

        /// <summary>
        /// Find the drop pod door GameObject (helper for one-shot interaction).
        /// Returns the door GameObject if found, null otherwise.
        /// </summary>
        private GameObject FindDropPodDoor(CharacterBody body)
        {
            if (body == null)
            {
                Log("[Drop Pod] Body is null!");
                return null;
            }

            // Find all objects with IInteractable interface
            MonoBehaviour[] allComponents = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();

            int checkedCount = 0;
            int nearbyCount = 0;
            var podObjectsFound = new System.Collections.Generic.List<string>();

            foreach (MonoBehaviour component in allComponents)
            {
                if (component == null || component.gameObject == null)
                    continue;

                // Check if this component implements IInteractable
                IInteractable interactable = component as IInteractable;
                if (interactable != null)
                {
                    checkedCount++;

                    // Check distance first
                    float distance = Vector3.Distance(body.transform.position, component.transform.position);
                    if (distance <= 15f) // Increased range to 15m
                    {
                        nearbyCount++;

                        string objNameLower = component.gameObject.name.ToLower();

                        // EXCLUDE battery panels and other non-door components
                        if (objNameLower.Contains("battery") || objNameLower.Contains("panel") ||
                            objNameLower.Contains("batterypanel") || objNameLower.Contains("fuel"))
                        {
                            continue; // Skip these, they're not the door
                        }

                        // Check if this is a drop pod door (original pattern)
                        bool isPodDoor = objNameLower.Contains("survivorpod");

                        if (isPodDoor)
                        {
    
                            Log($"[Drop Pod] ===== SELECTED: {component.gameObject.name} at {distance:F1}m =====");

                            return component.gameObject;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Try to activate the drop pod door ONCE (one-shot interaction pattern).
        /// Returns true if interaction was attempted (regardless of success).
        /// </summary>
        private bool TryActivateDropPodDoorOnce(GameObject doorObj, CharacterBody body)
        {
            if (doorObj == null || body == null)
                return false;

            var interactor = body.GetComponent<Interactor>();
            if (interactor == null)
            {
                Log("[Drop Pod] Body has no Interactor component!");
                return false;
            }

            // Check if this object has any interactable component
            var interactable = doorObj.GetComponent<IInteractable>();
            if (interactable == null)
            {
                Log($"[Drop Pod] {doorObj.name} has no IInteractable component!");
                return false;
            }

            // Check if we can interact with it
            Interactability state = interactable.GetInteractability(interactor);

            if (state == Interactability.Available)
            {
                Log($"[Drop Pod] Activating pod door: {doorObj.name}");

                try
                {
                    // Perform ONE interaction - don't call this every frame!
                    interactable.OnInteractionBegin(interactor);
                    return true; // Successfully attempted interaction
                }
                catch (System.Exception e)
                {
                    Log($"[Drop Pod] Interaction failed: {e.Message}");
                    return false;
                }
            }
            else
            {
                // Log other states occasionally
                if (Time.frameCount % 30 == 0)
                {
                    Log($"[Drop Pod] Pod door state: {doorObj.name} - {state}");
                }
            }

            return false;
        }

        /// <summary>
        /// Apply jump detection to movement direction - helps AI navigate over obstacles during ANY movement.
        /// Returns modified direction with strafing if obstacle detected but not jumpable.
        /// </summary>
        public Vector3 ApplyJumpDetection(CharacterBody body, Vector3 direction)
        {
            // Normalize and flatten direction
            direction = direction.normalized;
            direction.y = 0;

            // === JUMP DETECTION ===
            // Check for obstacles at multiple heights (like EntityDetector.HasLineOfSight)
            bool obstacleDetected = false;
            bool shouldJump = false;

            // Check multiple heights: ground level, waist, chest, head
            float[] heights = new float[] { 0.1f, 0.5f, 1.0f, 1.5f };

            foreach (float height in heights)
            {
                Vector3 rayOrigin = body.transform.position + Vector3.up * height;
                Vector3 rayDirection = direction.normalized;

                RaycastHit hit;
                if (Physics.Raycast(rayOrigin, rayDirection, out hit, 2f, LayerMask.GetMask("World")))
                {
                    obstacleDetected = true;

                    // Check if obstacle is climbable (not too tall, not too short)
                    float heightDifference = hit.point.y - body.transform.position.y;

                    // Jump if obstacle is climbable (0.3m to 3m tall)
                    if (heightDifference > 0.3f && heightDifference < 3f)
                    {
                        shouldJump = true;
                    }

                    // Log to BepInEx console (LogOutput.log)
                    if (Time.frameCount % 60 == 0) // Log every 1 second, not every frame
                    {
                        Log($"[JUMP] Obstacle detected! Height: {heightDifference:F1}m, Dist: {hit.distance:F1}m, HeightDiff: {heightDifference:F1}m, shouldJump: {shouldJump}");
                    }
                }
            }

            // === JUMP INPUT ===
            // Direct CharacterMotor.Jump() call - bypasses the input pipeline entirely
            // (PushState/bodyInputs approaches fail because orig() overwrites input state)
            if (shouldJump && Time.time - lastJumpTime > 0.5f)
            {
                if (body.characterMotor != null)
                {
                    body.characterMotor.Jump(1f, 1f, false);
                    lastJumpTime = Time.time;
                    Log("[JUMP] CharacterMotor.Jump() executed directly");
                }
            }

            // If obstacle but no jump, at least strafe around
            if (obstacleDetected && !shouldJump)
            {
                // Try to go around - add perpendicular component
                Vector3 strafeDirection = Vector3.Cross(direction, Vector3.up);
                direction = (direction + strafeDirection * 0.5f).normalized;
            }

            return direction;
        }

        /// <summary>
        /// Compute a NodeGraph A* path from the character to targetPosition.
        /// Synchronous - path is ready immediately after this call.
        /// </summary>
        private void RecomputePath(CharacterBody body, Vector3 targetPosition)
        {
            if (!SceneInfo.instance) return;
            NodeGraph groundGraph = SceneInfo.instance.GetNodeGraph(MapNodeGroup.GraphType.Ground);
            if (groundGraph == null) return;

            pathRequest.path = new Path(groundGraph);
            pathRequest.startPos = body.transform.position;  // implicit cast to PathRequestPosition
            pathRequest.endPos = targetPosition;              // implicit cast to PathRequestPosition
            pathRequest.hullClassification = body.hullClassification;
            pathRequest.maxJumpHeight = float.PositiveInfinity;  // allow all jump links
            pathRequest.maxSpeed = body.moveSpeed;

            PathTask task = groundGraph.ComputePath(pathRequest);  // synchronous A* - complete immediately
            lastPathWasReachable = task.wasReachable;
            pathRecomputeTimer = PATH_RECOMPUTE_INTERVAL;
            lastNavTargetPosition = targetPosition;

            if (task.wasReachable)
            {
                pathFollower.SetPath(pathRequest.path);
                Log($"[PATH] Computed {pathRequest.path.waypointsCount} waypoints to target");
            }
            else
            {
                Log($"[PATH] Target unreachable via NodeGraph");
            }
        }

        /// <summary>
        /// Handle GOTO navigation.
        /// Returns true if arrived at destination.
        /// </summary>
        public bool HandleGotoNavigation(CharacterBody body)
        {
            if (body == null)
                return false;

            Vector3 targetPosition;

            // Determine target position
            if (gotoTarget != null)
            {
                // CRITICAL: Validate gotoTarget before accessing properties
                if (!IsGameObjectValid(gotoTarget))
                {
                    Log($"[GOTO] gotoTarget became invalid, aborting navigation");
                    isNavigating = false;
                    gotoTarget = null;
                    return false;
                }

                // CRITICAL: Check if target is a lunar coin - they don't magnetically attach and will cause infinite loops
                // NOTE: Name-based check is unreliable - all pickups are named "GenericPickup(Clone)"
                // Use PickupDef.coinValue instead (RoR2's canonical way to identify lunar coins)
                var pickupCtrl = gotoTarget.GetComponent<GenericPickupController>();
                if (pickupCtrl != null)
                {
                    PickupDef pickupDef = PickupCatalog.GetPickupDef(pickupCtrl.pickupIndex);
                    if (pickupDef != null && pickupDef.coinValue > 0)
                    {
                        Log($"[GOTO] Abandoning lunar coin pickup '{gotoTarget.name}' - it doesn't magnetically attach!");
                        isNavigating = false;
                        gotoTarget = null;
                        gotoInteractable = null;
                        frustration = 0f;
                        stuckTimer = 0f;
                        return false;
                    }
                }

                targetPosition = gotoTarget.transform.position;
            }
            else if (gotoInteractable != null)
            {
                targetPosition = gotoInteractable.Position;
            }
            else
            {
                // No target
                isNavigating = false;
                return false;
            }

            // Check if we've arrived (need to be close to interact)
            float distance = Vector3.Distance(body.transform.position, targetPosition);
            if (distance < INTERACT_DISTANCE_THRESHOLD)  // Arrival threshold (within interaction range)
            {
                Log($"[GOTO] Arrived at destination (distance: {distance:F1}m)");
                return true;
            }

            // Phase 2.5: Enhanced stuck detection with frustration
            float movementThisFrame = Vector3.Distance(body.transform.position, lastPosition);

            if (movementThisFrame < STUCK_THRESHOLD * Time.fixedDeltaTime)  // Moving slower than STUCK_THRESHOLD m/s
            {
                // We're stuck - increase frustration
                frustration += FRUSTRATION_INCREASE_RATE * Time.fixedDeltaTime;
                stuckTimer += Time.fixedDeltaTime;

                if (frustration >= FRUSTRATION_THRESHOLD)
                {
                    // Give up on this target - it's unreachable
                    Log($"[GOTO] Giving up on unreachable target after {stuckTimer:F1}s (frustration: {frustration:F1})");

                    // Clear navigation state
                    isNavigating = false;
                    gotoTarget = null;
                    gotoInteractable = null;
                    stuckTimer = 0f;
                    frustration = 0f;
                    attemptStuck = false;

                    return false;  // Navigation failed
                }
                else if (stuckTimer > STUCK_TIMEOUT && Time.frameCount % 60 == 0)
                {
                    // Log progress every second while stuck (but don't give up yet)
                    Log($"[GOTO] Still stuck... ({stuckTimer:F1}s, frustration: {frustration:F1}/{FRUSTRATION_THRESHOLD})");
                }
            }
            else
            {
                // Making progress - reset frustration and stuck timer
                frustration = 0f;
                stuckTimer = 0f;
                attemptStuck = false;
            }

            lastPosition = body.transform.position;

            // Force path recompute when target switches
            if (gotoTarget != lastGotoTarget || gotoInteractable != lastGotoInteractable)
            {
                pathRecomputeTimer = 0f;
                pathFollower.Reset();
                lastGotoTarget = gotoTarget;
                lastGotoInteractable = gotoInteractable;
            }

            // Recompute NodeGraph path when interval elapses or target has drifted significantly
            pathRecomputeTimer -= Time.fixedDeltaTime;
            if (pathRecomputeTimer <= 0f || Vector3.Distance(targetPosition, lastNavTargetPosition) > 3f)
            {
                RecomputePath(body, targetPosition);
            }

            // Navigate via NodeGraph path
            pathFollower.UpdatePosition(body.transform.position);
            Vector3? nextWaypoint = pathFollower.GetNextPosition();
            Vector3 direction;

            if (!pathFollower.isFinished && nextWaypoint.HasValue)
            {
                direction = (nextWaypoint.Value - body.transform.position).normalized;
                direction.y = 0;

                // Jump when the NodeGraph path requires it (replaces raycast-based jump detection)
                if (pathFollower.nextWaypointNeedsJump && Time.time - lastJumpTime > 0.5f)
                {
                    if (body.characterMotor != null && body.characterMotor.isGrounded)
                    {
                        body.characterMotor.Jump(1f, 1f, false);
                        lastJumpTime = Time.time;
                        Log("[PATH] Jumping - required by NodeGraph path");
                    }
                }
            }
            else
            {
                // Path finished or unavailable - direct final approach to raw target
                direction = (targetPosition - body.transform.position).normalized;
                direction.y = 0;
            }

            // === OBSTACLE AVOIDANCE ===
            // ApplyJumpDetection raycasts ahead at multiple heights and:
            // - Jumps over climbable obstacles (0.3-3m tall) via CharacterMotor.Jump()
            // - Steers around non-jumpable obstacles by blending in a perpendicular component
            // NodeGraph paths only guarantee walkable ground - they don't account for
            // horizontal obstacles (pillars, boxes, etc.) that sit between graph nodes.
            direction = ApplyJumpDetection(body, direction);

            // === SMOOTH DIRECTION CHANGES ===
            // Raw direction snaps instantly when PathFollower advances to the next waypoint,
            // causing visible camera and sprite jolts. Slerp interpolates smoothly instead.
            if (smoothedNavMoveDir == Vector3.zero)
                smoothedNavMoveDir = direction;
            smoothedNavMoveDir = Vector3.Slerp(smoothedNavMoveDir, direction, NAV_MOVE_SMOOTH);

            // Aim direction uses a slower factor so the camera follows movement without jerking
            if (smoothedNavAimDir == Vector3.zero)
                smoothedNavAimDir = direction;
            smoothedNavAimDir = Vector3.Slerp(smoothedNavAimDir, direction, NAV_AIM_SMOOTH);

            body.inputBank.moveVector = smoothedNavMoveDir;
            body.inputBank.aimDirection = smoothedNavAimDir;

            // === SPRINT DURING NAVIGATION ===
            // HandleGotoNavigation returns early from FixedUpdateAI (before the sprint block),
            // so we must push sprint here. Always sprint when navigating to a target.
            if (body.inputBank != null)
                body.inputBank.sprint.PushState(true);
            body.isSprinting = true;

            return false;  // Not arrived yet
        }

        /// <summary>
        /// Start interaction with a target.
        /// </summary>
        public void StartInteraction(GameObject target)
        {
            if (target == null || !IsGameObjectValid(target))
            {
                Log("[INTERACT] Target is null or destroyed!");
                return;
            }

            interactionTarget = target;
            interactionHoldTimer = 0f;
            interactSettleTimer = 0f;
            Log($"[INTERACT] Starting interaction with {target.name}");
        }

        /// <summary>
        /// Clear all navigation and interaction state so the AI resumes roaming/combat.
        /// </summary>
        private void ClearFullNavigationState(string reason)
        {
            interactionTarget = null;
            interactionHoldTimer = 0f;
            interactSettleTimer = 0f;
            isNavigating = false;
            gotoTarget = null;
            gotoInteractable = null;
            frustration = 0f;
            stuckTimer = 0f;
            attemptStuck = false;
            // Reset path state so the next GOTO starts fresh
            pathFollower.Reset();
            pathRecomputeTimer = 0f;
            lastPathWasReachable = false;
            lastGotoTarget = null;
            lastGotoInteractable = null;
            // Reset smoothed directions so the next navigation starts without stale data
            smoothedNavMoveDir = Vector3.zero;
            smoothedNavAimDir = Vector3.zero;
            Log($"[NAV] Navigation state cleared - {reason}");
        }

        /// <summary>
        /// Handle interaction holding logic.
        /// </summary>
        public void HandleInteractionHolding(CharacterBody body)
        {
            if (interactionTarget == null || body == null)
                return;

            // Reset each frame so AIController only sees true on the frame the interaction fires
            lastInteractionSucceeded = false;

            // CRITICAL: Validate interactionTarget before accessing properties
            // Unity can destroy objects mid-frame, so we need try/catch
            float distance = float.MaxValue;
            try
            {
                if (interactionTarget == null || !IsGameObjectValid(interactionTarget))
                {
                    ClearFullNavigationState("interactionTarget became invalid");
                    return;
                }

                interactionHoldTimer += Time.fixedDeltaTime;

                // Check if we're close enough to interact (need to be right on top of it)
                distance = Vector3.Distance(body.transform.position, interactionTarget.transform.position);
            }
            catch (System.Exception e)
            {
                ClearFullNavigationState($"CRASH accessing interactionTarget: {e.Message}");
                return;
            }

            // Debug log every 30 frames (0.5 seconds) to see what's happening
            if (Time.frameCount % 30 == 0)
            {
                Log($"[INTERACT] Distance: {distance:F2}m, Timer: {interactionHoldTimer:F1}s, Target: {interactionTarget.name}");
            }

            if (distance > INTERACT_DISTANCE_THRESHOLD)
            {
                // Not close enough, move towards it
                // CRITICAL: Wrap transform access in try/catch for race condition safety
                try
                {
                    if (interactionTarget == null || !IsGameObjectValid(interactionTarget))
                    {
                        ClearFullNavigationState("target became invalid during approach");
                        return;
                    }

                    Vector3 direction = (interactionTarget.transform.position - body.transform.position).normalized;
                    direction.y = 0;

                    // Apply jump detection to navigate over obstacles
                    direction = ApplyJumpDetection(body, direction);

                    body.inputBank.moveVector = direction;
                    if (body.inputBank != null)
                        body.inputBank.sprint.PushState(false); // Walk when approaching interactable
                }
                catch (System.Exception e)
                {
                    ClearFullNavigationState($"CRASH during approach: {e.Message}");
                    return;
                }

                // Reset settle timer — must be continuously within range before interacting
                interactSettleTimer = 0f;
            }
            else
            {
                // Close enough - STOP moving and wait for settle timer before interacting.
                // This prevents overshoot with fast movement items (Goat Hoof, Energy Drink, etc.)
                body.inputBank.moveVector = Vector3.zero;
                if (body.inputBank != null)
                    body.inputBank.sprint.PushState(false); // Don't sprint when interacting

                interactSettleTimer += Time.fixedDeltaTime;

                // Wait until character has settled within range before firing interaction
                if (interactSettleTimer < INTERACT_SETTLE_DURATION)
                    return;

                // CRITICAL: Validate target before getting components
                if (interactionTarget == null || !IsGameObjectValid(interactionTarget))
                {
                    ClearFullNavigationState("target became invalid before interaction");
                    return;
                }

                Interactor interactor = body.GetComponent<Interactor>();
                IInteractable interactable = interactionTarget.GetComponent<IInteractable>();

                if (interactor != null && interactable != null)
                {
                    Interactability state = interactable.GetInteractability(interactor);
                    if (state == Interactability.Available)
                    {
                        // OnInteractionBegin is a one-shot call - call it once and clear state
                        interactable.OnInteractionBegin(interactor);
                        lastInteractionSucceeded = true;  // Signal AIController to send action_complete
                        ClearFullNavigationState($"interaction activated on {interactionTarget.name}");
                    }
                    else
                    {
                        // Not interactable anymore (chest already opened, shrine used, etc.)
                        ClearFullNavigationState($"target no longer interactable (state: {state})");
                    }
                }
                else
                {
                    ClearFullNavigationState("target lost Interactor or IInteractable component");
                }
            }
        }

        public GameObject GetInteractionTarget()
        {
            return interactionTarget;
        }

        public void ClearInteractionTarget()
        {
            interactionTarget = null;
            interactionHoldTimer = 0f;
            interactSettleTimer = 0f;
            // Also clear nav state so the AI doesn't get stuck in an arrived-but-nothing-to-do loop
            isNavigating = false;
            gotoTarget = null;
            gotoInteractable = null;
        }

        // Helper methods
        private void Log(string message)
        {
            RainflayerPlugin.Instance?.LogDebug($"[NavigationController] {message}");
        }

        private static bool IsGameObjectValid(GameObject obj)
        {
            // Unity objects can appear "not null" even when destroyed
            // We need to check if they're actually accessible
            if (obj == null) return false;
            try
            {
                // Try to access a safe property - will throw if object is destroyed
                return obj.GetInstanceID() != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
