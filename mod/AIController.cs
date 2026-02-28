using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;

namespace Rainflayer
{
    /// <summary>
    /// Executes AI commands received from the bridge.
    /// Controls targeting, movement, and skill usage.
    /// </summary>
    public class AIController : MonoBehaviour
    {
        private EntityDetector entityDetector;

        // Refactored controllers
        private QueryHandlers queryHandlers;
        private NavigationController navigationController;
        private CombatController combatController;

        private GameObject currentTarget;
        private bool isFollowing = false;
        private string currentMode = "roam";  // roam, combat, follow, wait

        // Expose controllers for external access (if needed)
        public NavigationController GetNavigationController() => navigationController;
        public CombatController GetCombatController() => combatController;

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

        // Helper to check if a Component is still valid
        private static bool IsComponentValid<T>(T component) where T : Component
        {
            if (component == null) return false;
            try
            {
                return component.gameObject != null;
            }
            catch
            {
                return false;
            }
        }

        private bool waitingForPickup = false;  // Waiting for items to spawn and be picked up after chest opens
        private const float PICKUP_RANGE = 1.0f;  // Need to be within 1m for magnetic pickup to work

        // Follow mode
        private const float FOLLOW_STOP_DISTANCE = 5f;  // Stop following when within 5m of ally
        private float followUpdateTimer = 0f;            // Timer for follow target refresh (seconds)

        // Socket bridge for event push (same TCP connection as queries)
        private SocketBridge socketBridge;
        private float lastHealth = 100f;
        private bool wasInCombat = false;

        // Drop pod tracking is now managed by NavigationController

        // Combat vs looting priority
        private const float COMBAT_PRIORITY_RANGE = 10f;  // If enemies within 10m, prioritize combat over looting (was 30m, reduced to allow chest opening with nearby enemies)

        public void SetEntityDetector(EntityDetector detector)
        {
            entityDetector = detector;
            string status = detector != null ? "OK" : "NULL";
            Log($"EntityDetector set: {status}");

            // Initialize refactored controllers
            if (detector != null)
            {
                queryHandlers = new QueryHandlers(this);
                navigationController = new NavigationController(this, detector);
                combatController = new CombatController(this, detector);
                Log("Initialized refactored controllers");
            }
        }

        public void ResetDropPodState()
        {
            if (navigationController != null)
            {
                navigationController.ResetDropPodState();
                Log("[Drop Pod] State reset for new stage");
            }
        }

        /// <summary>
        /// Reset ALL AI state (called on new run, stage, or respawn).
        /// This avoids needing to restart the game when starting a new run!
        /// </summary>
        public void ResetAllAIState()
        {
            Log("[RESET] ========================================");
            Log("[RESET] RESETTING ALL AI STATE");
            Log("[RESET] ========================================");

            // EntityDetector references are intentionally NOT updated here.
            // At the time Run_Start fires, PlayerAI/LocalPlayerMaster still point to the OLD run's
            // (soon-to-be-destroyed) objects. Updating EntityDetector with those stale refs would
            // break targeting. Instead, RainflayerPlugin.Update() detects when the new body
            // becomes available and refreshes EntityDetector references at that point.
            if (entityDetector != null)
            {
                Log("[RESET] ✓ EntityDetector references will refresh when new body spawns");
            }

            // Reset drop pod state
            if (navigationController != null)
            {
                navigationController.ResetDropPodState();
                Log("[RESET] ✓ Drop pod state reset");
            }

            // Reset navigation state
            if (navigationController != null)
            {
                navigationController.isNavigating = false;
                navigationController.gotoTarget = null;
                navigationController.gotoInteractable = null;
                navigationController.ClearInteractionTarget();
                Log("[RESET] ✓ Navigation state reset");
            }

            // Reset combat state
            currentTarget = null;
            currentMode = "roam";
            isFollowing = false;
            followUpdateTimer = 0f;
            Log("[RESET] ✓ Combat state reset");

            // Reset CombatController - clears stale camera refs and aim smoothing
            // Without this, cameraInitialized stays true and the new run's camera rig is never set up
            if (combatController != null)
            {
                combatController.Reset();
                Log("[RESET] ✓ CombatController reset (camera + aim)");
            }

            // Reset event tracking
            lastHealth = 100f;
            wasInCombat = false;
            waitingForPickup = false;
            Log("[RESET] ✓ Event tracking reset");

            Log("[RESET] ========================================");
            Log("[RESET] AI STATE RESET COMPLETE");
            Log("[RESET] ========================================");
        }

        void Start()
        {
            socketBridge = GetComponent<SocketBridge>();
            combatController.DetectCharacterClass();
        }

        /// <summary>
        /// Execute a command from the bridge.
        /// </summary>
        public void ExecuteCommand(string command, string args)
        {
            if (!RainflayerPlugin.EnableAIControl.Value)
                return;

            try
            {
                switch (command)
                {
                    // Strategy commands
                    case "STRATEGY":
                        HandleStrategyCommand(args);
                        break;

                    // Query commands (can be called from Brain model or via SocketBridge)
                    // These handlers send their own socket responses, so return early to avoid duplicate
                    case "QUERY_INVENTORY":
                        queryHandlers?.HandleQueryInventory();
                        return; // Don't send duplicate response

                    case "QUERY_INTERACTABLES":
                        queryHandlers?.HandleQueryInteractables();
                        return; // Don't send duplicate response

                    case "QUERY_ALLIES":
                        queryHandlers?.HandleQueryAllies();
                        return; // Don't send duplicate response

                    case "QUERY_OBJECTIVE":
                        queryHandlers?.HandleQueryObjective();
                        return; // Don't send duplicate response

                    case "QUERY_COMBAT_STATUS":
                        queryHandlers?.HandleQueryCombatStatus();
                        return; // Don't send duplicate response

                    // New GOTO commands
                    case "GOTO":
                        HandleGotoCommand(args);
                        break;

                    // New MODE commands
                    case "MODE":
                        HandleModeCommand(args);
                        break;

                    // New interaction commands
                    case "BUY_SHOP_ITEM":
                        HandleBuyShopItem(args);
                        break;

                    // Combined command: Find nearest object and interact with it
                    case "FIND_AND_INTERACT":
                        HandleFindAndInteract(args);
                        break;

                    // Debug command: List all chests in scene
                    case "DEBUG_CHESTS":
                        HandleDebugChests();
                        break;

                    default:
                        RainflayerPlugin.Instance.LogError($"Unknown command: {command}");
                        break;
                }

            }
            catch (Exception e)
            {
                RainflayerPlugin.Instance.LogError($"Error executing command {command}: {e.Message}");
            }
        }

        #region Command Handlers

        void HandleStrategyCommand(string args)
        {
            combatController.SetStrategy(args);
        }

        void HandleGotoCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args) || args == "CANCEL")
            {
                navigationController.isNavigating = false;
                navigationController.gotoTarget = null;
                navigationController.gotoInteractable = null;
                Log("[GOTO] Cancelled navigation");
            }
        }

        void HandleModeCommand(string args)
        {
            string prevMode = currentMode;
            currentMode = args.ToLower();
            Log($"[MODE] Set to: {currentMode}");

            // Update behavior based on mode
            switch (currentMode)
            {
                case "roam":
                    isFollowing = false;
                    // Clear any follow-initiated navigation so combat/roam can resume
                    if (prevMode == "follow" && navigationController != null)
                    {
                        navigationController.isNavigating = false;
                        navigationController.gotoTarget = null;
                    }
                    break;
                case "combat":
                    isFollowing = false;
                    if (prevMode == "follow" && navigationController != null)
                    {
                        navigationController.isNavigating = false;
                        navigationController.gotoTarget = null;
                    }
                    break;
                case "follow":
                    isFollowing = true;
                    followUpdateTimer = 0f;  // Refresh immediately on entry
                    break;
                case "wait":
                    isFollowing = false;
                    if (prevMode == "follow" && navigationController != null)
                    {
                        navigationController.isNavigating = false;
                        navigationController.gotoTarget = null;
                    }
                    break;
            }
        }

        void HandleBuyShopItem(string itemName)
        {
            Log($"[BUY] Looking for shop item: {itemName}");

            // Find nearest shop
            if (entityDetector == null)
            {
                Log("[BUY] EntityDetector is null!");
                return;
            }

            InteractableInfo[] interactables = entityDetector.FindInteractablesInRange(50f);
            var shops = interactables.Where(i => i.Type == "shop").ToArray();

            if (shops.Length == 0)
            {
                Log("[BUY] No shops found in range");
                return;
            }

            // Navigate to nearest shop and buy
            var nearestShop = shops.OrderBy(s => s.Distance).First();
            navigationController.gotoTarget = nearestShop.GameObject;
            navigationController.gotoInteractable = nearestShop;
            navigationController.isNavigating = true;

            Log($"[BUY] Navigating to shop at {nearestShop.Distance:F1}m to buy: {itemName}");
        }

        void HandleFindAndInteract(string args)
        {
            /* Combined command: Find nearest object and interact with it.
             * Usage: FIND_AND_INTERACT:nearest_chest, FIND_AND_INTERACT:chest
             * This is simpler than sending GOTO + INTERACT separately.
             */
            Log($"[FIND_AND_INTERACT] Starting search for '{args}'");

            if (entityDetector == null)
            {
                Log("[FIND_AND_INTERACT] EntityDetector is null!");
                return;
            }

            CharacterBody body = RainflayerPlugin.GetPlayerBody();
            if (body == null)
            {
                Log("[FIND_AND_INTERACT] Player body is null!");
                return;
            }

            // Parse args (e.g., "nearest_chest" or just "chest")
            string targetType = args.ToLower().Trim();

            // Find nearest chest (or whatever target type)
            // Range: 100m (search wider), FOV: 360° (all directions), LOS: true (improved multi-height check)
            // DEBUG: Enable logging to see what's being found
            Log($"[FIND_AND_INTERACT] Calling FindInteractablesInRange with improved multi-height LOS check...");
            InteractableInfo[] interactables = entityDetector.FindInteractablesInRange(100f, fovDegrees: 360f, requireLineOfSight: true, debug: true);
            Log($"[FIND_AND_INTERACT] Found {interactables.Length} total interactables (with improved LOS check)");
            InteractableInfo target = null;

            if (targetType == "nearest_chest" || targetType == "chest")
            {
                target = interactables.Where(i => i.Type == "chest").OrderBy(i => i.Distance).FirstOrDefault();

                // LOCKED CHECK: If chest is locked (e.g., during teleporter event), don't navigate
                if (target != null && target.IsLocked)
                {
                    Log($"[FIND_AND_INTERACT] LOCKED: {target.Name} is locked (teleporter event?)");

                    socketBridge?.SendEvent("action_failed",
                        ("command", $"FIND_AND_INTERACT:{targetType}"),
                        ("reason", "locked"),
                        ("target", target.Name)
                    );

                    return;
                }

                // GOLD CHECK: Make sure we have enough money before targeting a chest
                if (target != null)
                {
                    int currentGold = (int)(body.master?.money ?? 0);
                    if (currentGold < target.Cost)
                    {
                        Log($"[FIND_AND_INTERACT] NOT ENOUGH GOLD: Have {currentGold}, need {target.Cost} for {target.Name}");

                        // Report failure to action ledger
                        socketBridge?.SendEvent("action_failed",
                            ("command", $"FIND_AND_INTERACT:{targetType}"),
                            ("reason", "no_gold"),
                            ("target", target.Name),
                            ("cost", target.Cost.ToString()),
                            ("current_gold", currentGold.ToString())
                        );

                        return; // Don't start navigation if we can't afford it
                    }
                    else
                    {
                        Log($"[FIND_AND_INTERACT] Gold check passed: Have {currentGold}, need {target.Cost}");
                    }
                }
            }
            else if (targetType == "shrine")
            {
                target = interactables.Where(i => i.Type == "shrine").OrderBy(i => i.Distance).FirstOrDefault();

                // LOCKED CHECK: If shrine is locked (e.g., during teleporter event), don't navigate
                if (target != null && target.IsLocked)
                {
                    Log($"[FIND_AND_INTERACT] LOCKED: {target.Name} is locked (teleporter event?)");

                    socketBridge?.SendEvent("action_failed",
                        ("command", $"FIND_AND_INTERACT:{targetType}"),
                        ("reason", "locked"),
                        ("target", target.Name)
                    );

                    return;
                }

                // GOLD CHECK: Also check shrines (some cost money like Shrine of Blood)
                if (target != null && target.Cost > 0)
                {
                    int currentGold = (int)(body.master?.money ?? 0);
                    if (currentGold < target.Cost)
                    {
                        Log($"[FIND_AND_INTERACT] NOT ENOUGH GOLD: Have {currentGold}, need {target.Cost} for {target.Name}");

                        socketBridge?.SendEvent("action_failed",
                            ("command", $"FIND_AND_INTERACT:{targetType}"),
                            ("reason", "no_gold"),
                            ("target", target.Name),
                            ("cost", target.Cost.ToString()),
                            ("current_gold", currentGold.ToString())
                        );

                        return;
                    }
                }
            }
            else if (targetType == "teleporter")
            {
                target = interactables.FirstOrDefault(i => i.Type == "teleporter");
            }
            else if (targetType == "shop")
            {
                target = interactables.Where(i => i.Type == "shop").OrderBy(i => i.Distance).FirstOrDefault();

                // LOCKED CHECK: If shop is locked (e.g., during teleporter event), don't navigate
                if (target != null && target.IsLocked)
                {
                    Log($"[FIND_AND_INTERACT] LOCKED: {target.Name} is locked (teleporter event?)");

                    socketBridge?.SendEvent("action_failed",
                        ("command", $"FIND_AND_INTERACT:{targetType}"),
                        ("reason", "locked"),
                        ("target", target.Name)
                    );

                    return;
                }

                // GOLD CHECK: Make sure we have enough money before targeting a shop terminal
                if (target != null)
                {
                    int currentGold = (int)(body.master?.money ?? 0);
                    if (currentGold < target.Cost)
                    {
                        Log($"[FIND_AND_INTERACT] NOT ENOUGH GOLD: Have {currentGold}, need {target.Cost} for {target.Name}");

                        socketBridge?.SendEvent("action_failed",
                            ("command", $"FIND_AND_INTERACT:{targetType}"),
                            ("reason", "no_gold"),
                            ("target", target.Name),
                            ("cost", target.Cost.ToString()),
                            ("current_gold", currentGold.ToString())
                        );

                        return;
                    }
                    else
                    {
                        Log($"[FIND_AND_INTERACT] Gold check passed: Have {currentGold}, need {target.Cost}");
                    }
                }
            }
            else
            {
                // Default: find any interactable
                target = interactables.OrderBy(i => i.Distance).FirstOrDefault();
            }

            if (target != null && target.GameObject != null)
            {
                // Set as GOTO target - NodeGraph navigation will take us there, then
                // FixedUpdateAI calls StartInteraction on arrival (do NOT call it here,
                // as that would bypass NodeGraph pathfinding by triggering HandleInteractionHolding immediately)
                navigationController.gotoTarget = target.GameObject;
                navigationController.gotoInteractable = target;
                navigationController.isNavigating = true;

                // Notify Brain that we found a target and are starting navigation
                socketBridge?.SendEvent("action_started",
                    ("command", $"FIND_AND_INTERACT:{targetType}"),
                    ("target", target.Name),
                    ("distance", ((int)target.Distance).ToString()),
                    ("cost", target.Cost.ToString())
                );

                Log($"[FIND_AND_INTERACT] Going to {target.Name} at {target.Distance:F1}m (cost: {target.Cost}) and will interact when close");
            }
            else
            {
                Log($"[FIND_AND_INTERACT] No {targetType} found (with LOS check)");
                socketBridge?.SendEvent("action_failed",
                    ("command", $"FIND_AND_INTERACT:{targetType}"),
                    ("reason", "not_found")
                );
            }
        }

        void HandleDebugChests()
        {
            /* Debug command: List all chests in the scene.
             * Usage: DEBUG_CHESTS
             * This will brute-force search for all GameObjects with "chest" in the name.
             */
            Log("[DEBUG_CHESTS] Starting brute force chest search...");

            if (entityDetector == null)
            {
                Log("[DEBUG_CHESTS] EntityDetector is null!");
                return;
            }

            entityDetector.DebugListAllChests();
            Log("[DEBUG_CHESTS] Search complete - check Unity console for output");
        }

        #endregion

        #region AI Update

        // ========== NULL SAFE HELPERS ==========
        // Unity objects can be destroyed mid-frame. Always check before use.
        private bool TrySetMoveVector(CharacterBody body, Vector3 value)
        {
            if (body?.inputBank != null)
            {
                body.inputBank.moveVector = value;
                return true;
            }
            return false;
        }

        private bool TrySetAimDirection(CharacterBody body, Vector3 value)
        {
            if (body?.inputBank != null)
            {
                body.inputBank.aimDirection = value;
                return true;
            }
            return false;
        }

        private bool TrySetSprint(CharacterBody body, bool value)
        {
            if (body?.inputBank?.sprint != null)
            {
                body.inputBank.sprint.PushState(value);
                return true;
            }
            return false;
        }
        // ==========================================

        public void UpdateAI()
        {
            // Nothing to do in regular Update
        }

        public void FixedUpdateAI()
        {
            try
            {
                BaseAI ai = RainflayerPlugin.PlayerAI;
                CharacterBody body = RainflayerPlugin.GetPlayerBody();

                // AGGRESSIVE NULL CHECKING - Unity objects can be destroyed mid-frame
                if (ai == null || body == null)
                {
                    if (Time.frameCount % 150 == 0)
                    {
                        Log($"[FixedUpdate] Missing core: ai={ai != null}, body={body != null}");
                    }
                    return;
                }

                // CRITICAL: Check if body GameObject is still valid (Unity's destruction check)
                if (body.gameObject == null)
                {
                    if (Time.frameCount % 150 == 0)
                    {
                        Log($"[FixedUpdate] Body GameObject is null/destroyed!");
                    }
                    return;
                }

                // Only proceed if inputBank exists (created in Unity, can be destroyed)
                if (body.inputBank == null)
                {
                    if (Time.frameCount % 150 == 0)
                    {
                        Log($"[FixedUpdate] inputBank is null!");
                    }
                    return;
                }

                // Log entry into FixedUpdate - helps identify which frame crashes
                if (Time.frameCount % 30 == 0) // Every 0.5 seconds
                {
                    Log($"[FixedUpdate] Frame start: {Time.frameCount}");
                }

                // Check if player is alive
                if (body.healthComponent == null || !body.healthComponent.alive)
                {
                    return; // Don't do anything if dead
                }

                // NOTE: Sprint state is set at the END of FixedUpdateAI, after all movement logic
                // This ensures ShouldSprint() checks the CURRENT frame's moveVector, not the previous frame's

                // Handle drop pod exit (critical for autonomous play)
                if (navigationController != null && !navigationController.HasExitedDropPod())
                {
                    navigationController.HandleDropPodExit(body);
                    return; // Don't do other AI behavior until we've exited drop pod
                }

                // === PHASE 2.5: AUTOMATIC DROPPED ITEM PICKUP ===
                // Check for nearby dropped items and automatically collect them
                // Priority: Only pick up items when not in combat or waiting for chest pickup
                if (!waitingForPickup && entityDetector != null)
                {
                    int nearbyEnemies = entityDetector.CountEnemiesInRange(COMBAT_PRIORITY_RANGE);

                    // Only look for items when relatively safe (no nearby enemies)
                    if (nearbyEnemies == 0)
                    {
                        // Check periodically (every 2 seconds = 120 frames)
                        if (Time.frameCount % 120 == 0)
                        {
                            GameObject nearestPickup = entityDetector.FindNearestPickup(range: 30f);

                            if (nearestPickup != null)
                            {
                                // Double-check this isn't a lunar coin (defensive check)
                                // NOTE: Name-based check is unreliable - all pickups are named "GenericPickup(Clone)"
                                // Use PickupDef.coinValue instead (RoR2's canonical way to identify lunar coins)
                                var pickupCtrl = nearestPickup.GetComponent<GenericPickupController>();
                                if (pickupCtrl != null)
                                {
                                    PickupDef pickupDef = PickupCatalog.GetPickupDef(pickupCtrl.pickupIndex);
                                    if (pickupDef != null && pickupDef.coinValue > 0)
                                    {
                                        Log($"[AUTO PICKUP] Skipping lunar coin - requires manual interaction");
                                        nearestPickup = null;
                                    }
                                }
                            }

                            if (nearestPickup != null)
                            {
                                float distanceToPickup = Vector3.Distance(body.transform.position, nearestPickup.transform.position);

                                // Only navigate to pickup if we're not already close enough for magnetic pickup
                                if (distanceToPickup > PICKUP_RANGE)
                                {
                                    // Set as GOTO target to navigate to it
                                    navigationController.gotoTarget = nearestPickup;
                                    navigationController.gotoInteractable = null;
                                    navigationController.isNavigating = true;

                                    Log($"[AUTO PICKUP] Navigating to '{nearestPickup.name}' at {distanceToPickup:F1}m");
                                }
                                else
                                {
                                    // Already close enough - magnetic pickup will collect it automatically
                                    Log($"[AUTO PICKUP] '{nearestPickup.name}' in magnetic range ({distanceToPickup:F1}m)");
                                }
                            }
                        }
                    }
                }

                // === EVENT TRACKING ===
                TrackEvents(body);

                // === COMBAT PRIORITY OVER LOOTING ===
                // If enemies appear nearby while interacting, cancel and fight
                if (navigationController.GetInteractionTarget() != null && entityDetector != null)
                {
                    int nearbyEnemies = entityDetector.CountEnemiesInRange(COMBAT_PRIORITY_RANGE);
                    if (nearbyEnemies > 0)
                    {
                        Log($"[COMBAT PRIORITY] Interrupting interaction - {nearbyEnemies} enemies nearby");
                        navigationController.ClearInteractionTarget();
                    }
                }

                // === INTERACTION HOLDING ===
                if (navigationController.GetInteractionTarget() != null)
                {
                    navigationController.HandleInteractionHolding(body);
                    // Detect completion this frame (lastInteractionSucceeded is reset each call,
                    // so it's only true on the exact frame OnInteractionBegin fires)
                    if (navigationController.lastInteractionSucceeded)
                    {
                        socketBridge?.SendEvent("action_complete",
                            ("command", navigationController.lastInteractionCommand ?? "FIND_AND_INTERACT:unknown"),
                            ("status", "success")
                        );
                    }
                    // CRITICAL: Skip all combat/targeting behavior while interacting
                    // This prevents getting distracted by enemies
                    // NavigationController handles sprint state (precision mode)
                    return;
                }

                // === FOLLOW MODE: Keep ally as pathfinding target ===
                // Periodically refresh the nav target so the NodeGraph pathfinder tracks the ally
                // as they move. This replaces the old beeline approach and handles terrain, jumps, etc.
                if (isFollowing && entityDetector != null)
                {
                    followUpdateTimer -= Time.fixedDeltaTime;
                    if (followUpdateTimer <= 0f)
                    {
                        followUpdateTimer = 0.5f;  // Re-evaluate every 0.5 seconds

                        // Don't hijack navigation when already going to an interactable (chest/shrine)
                        if (navigationController.gotoInteractable == null)
                        {
                            // Use human players only (excludes drones, turrets, AI minions)
                            GameObject ally = entityDetector.FindClosestHumanPlayer();
                            if (ally != null && IsGameObjectValid(ally))
                            {
                                float distToAlly = Vector3.Distance(body.transform.position, ally.transform.position);
                                if (distToAlly > FOLLOW_STOP_DISTANCE)
                                {
                                    // Engage pathfinding toward the ally
                                    navigationController.gotoTarget = ally;
                                    navigationController.gotoInteractable = null;
                                    navigationController.isNavigating = true;
                                    Log($"[FOLLOW] Navigating to ally at {distToAlly:F1}m");
                                }
                                else
                                {
                                    // Close enough - stop follow-nav so combat can run normally
                                    if (navigationController.isNavigating && navigationController.gotoInteractable == null)
                                    {
                                        navigationController.isNavigating = false;
                                        navigationController.gotoTarget = null;
                                        Log($"[FOLLOW] Reached ally ({distToAlly:F1}m), stopping nav");
                                    }
                                }
                            }
                        }
                    }
                }

                // === GOTO NAVIGATION ===
                if (navigationController.isNavigating)
                {
                    bool arrived = navigationController.HandleGotoNavigation(body);
                    if (arrived)
                    {
                        if (currentMode == "follow")
                        {
                            // Arrived at ally - clear nav so combat logic can run this frame
                            navigationController.isNavigating = false;
                            navigationController.gotoTarget = null;
                            // Fall through to combat logic below
                        }
                        else
                        {
                            // Arrived at non-follow destination (chest, shrine, etc.)
                            if (navigationController.gotoInteractable != null)
                            {
                                // Store command string so action_complete can match the ledger entry
                                navigationController.lastInteractionCommand = $"FIND_AND_INTERACT:{navigationController.gotoInteractable.Type}";
                                navigationController.StartInteraction(navigationController.gotoInteractable.GameObject);
                                navigationController.gotoInteractable = null;
                                // Clear nav state so if enemies interrupt via ClearInteractionTarget(),
                                // isNavigating=true + gotoTarget=chest won't create an infinite arrived-but-nothing loop
                                navigationController.isNavigating = false;
                                navigationController.gotoTarget = null;
                            }
                            return;
                        }
                    }
                    else
                    {
                        // Still navigating - pathfinding has priority over combat movement.
                        // Returning here prevents combat logic from overwriting the movement
                        // direction set by NodeGraph pathfinding in HandleGotoNavigation.
                        return;
                    }
                }

                // === MODE BEHAVIOR ===
                if (currentMode == "wait")
                {
                    body.inputBank.moveVector = Vector3.zero;
                    if (body.inputBank != null)
                        body.inputBank.sprint.PushState(false);
                    body.isSprinting = false;
                    return;  // Don't do anything else in wait mode
                }

                // DIRECT CONTROL: Write to InputBank instead of relying on BaseAI skill drivers
                // This bypasses keyboard/mouse entirely
                if (entityDetector != null)
                {
                    // Use 360° detection (can "hear" enemies behind) but smooth aim prevents instant snapping
                    // Range-limited and threat-prioritized targeting
                    // CRITICAL: Only target enemies with Line of Sight (don't shoot through walls!)
                    GameObject target = entityDetector.FindBestTarget(
                        maxRange: 50f,      // Don't shoot at things >50m away
                        fovDegrees: 360f,   // 360 degree detection (can hear enemies)
                        requireLineOfSight: true  // MUST have LOS to attack
                    );

                    // CRITICAL: Validate target BEFORE accessing any properties
                    // Unity objects can appear "not null" even when destroyed
                    if (target != null && !IsGameObjectValid(target))
                    {
                        if (Time.frameCount % 120 == 0)
                        {
                            Log($"[TARGETING] FindBestTarget returned destroyed GameObject!");
                        }
                        target = null;
                    }

                    // Debug logging for targeting - SAFE to access properties now
                    if (Time.frameCount % 120 == 0) // Every 2 seconds
                    {
                        if (target != null)
                        {
                            CharacterBody targetBody = target.GetComponent<CharacterBody>();
                            float dist = Vector3.Distance(body.transform.position, target.transform.position);
                            Log($"[TARGETING] Found target with LOS: {targetBody?.GetDisplayName() ?? "Unknown"} at {dist:F1}m");
                        }
                        else
                        {
                            // Count enemies in range to debug
                            int enemyCount = entityDetector.CountEnemiesInRange(50f);
                            Log($"[TARGETING] No target with LOS. Enemies in 50m range: {enemyCount}");
                        }
                    }

                    if (target != null)
                    {
                        CharacterBody enemyBody = target.GetComponent<CharacterBody>();
                        if (enemyBody == null || !IsGameObjectValid(enemyBody.gameObject))
                        {
                            // Target became invalid, skip this frame
                            currentTarget = null;
                            return;
                        }

                        Vector3 targetPos = target.transform.position;
                        Vector3 playerPos = body.transform.position;
                        float distanceToTarget = Vector3.Distance(playerPos, targetPos);

                        // Calculate direction to target
                        Vector3 directionToTarget = (targetPos - playerPos).normalized;

                        // === MOVEMENT CONTROL (Direct InputBank write) ===
                        combatController.ApplyMovementControl(body, directionToTarget, distanceToTarget);

                        // === AIM CONTROL (Direct InputBank write - bypasses mouse!) ===
                        combatController.ApplyAimControl(body, directionToTarget, targetPos, playerPos);

                        // === SKILL CONTROL (Direct skill execution) ===
                        combatController.ApplySkillControl(body, distanceToTarget);

                        // Update current target for tracking
                        currentTarget = target;

                        // Debug logging
                        if (Time.frameCount % 60 == 0)
                        {
                            Log($"[AI Control] Target: {enemyBody?.GetDisplayName() ?? "Unknown"}, " +
                                $"Distance: {distanceToTarget:F1}, " +
                                $"Aim: {body.inputBank.aimDirection}, " +
                                $"Move: {body.inputBank.moveVector}");
                        }
                    }
                    else
                    {
                        // No target with LOS - just roam and wait for enemies to come to us
                        // Don't chase enemies we can't see - causes buggy behavior
                        combatController.ApplyIdleBehavior(body);
                        currentTarget = null;
                    }
                }
                else
                {
                    // Entity detector not ready
                    body.inputBank.moveVector = Vector3.zero;
                }

                // === FINAL SPRINT STATE SETTING ===
                // Set sprint state AFTER all movement logic, so ShouldSprint() checks current frame's moveVector
                // This matches RTAutoSprintEx's approach: update sprint once per frame, at the end
                // Note: Wait mode and interactions return early above, so this only handles normal combat/roam
                bool shouldSprint = combatController.ShouldSprint(body);
                if (body.inputBank != null)
                    body.inputBank.sprint.PushState(shouldSprint);
                body.isSprinting = shouldSprint;
            }
            catch (System.Exception e)
            {
                // Log crash details
                RainflayerPlugin.Instance.LogError($"[FixedUpdate] CRASH: {e.Message}\n{e.StackTrace}");
            }
        }

        #endregion

        void TrackEvents(CharacterBody body)
        {
            if (body == null || body.healthComponent == null)
                return;

            float currentHealth = body.healthComponent.health;
            bool inCombat = entityDetector?.CountEnemiesInRange(50f) > 0;

            // Check for damage taken
            if (currentHealth < lastHealth - 5f)  // More than 5 HP damage
            {
                float damage = lastHealth - currentHealth;
                socketBridge?.SendEvent("damage_taken",
                    ("amount", ((int)damage).ToString()),
                    ("remaining", ((int)currentHealth).ToString())
                );
            }

            lastHealth = currentHealth;

            // Check for combat state changes
            if (inCombat && !wasInCombat)
            {
                int enemyCount = entityDetector.CountEnemiesInRange(50f);
                socketBridge?.SendEvent("combat_entered",
                    ("enemy_count", enemyCount.ToString())
                );
            }
            else if (!inCombat && wasInCombat)
            {
                socketBridge?.SendEvent("combat_cleared");
            }

            wasInCombat = inCombat;

            // Check for low health
            if (currentHealth < body.healthComponent.fullHealth * 0.3f)
            {
                socketBridge?.SendEvent("low_health",
                    ("threshold", "30"),
                    ("current", ((int)currentHealth).ToString())
                );
            }
        }

        void Log(string message)
        {
            if (RainflayerPlugin.DebugMode.Value)
            {
                RainflayerPlugin.Instance.LogDebug($"[AIController] {message}");
            }
        }
    }
}