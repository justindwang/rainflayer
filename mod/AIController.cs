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
        private string currentMode = "roam";  // roam, combat, follow, wait, defend_zone

        // defend_zone mode state
        // One of these will be set (HoldoutZoneController for teleporter/pillars, EscapeSequenceExtractionZone for moon escape)
        private HoldoutZoneController defendZone = null;
        private EscapeSequenceExtractionZone defendEscapeZone = null;
        private Vector3 defendZoneCenter = Vector3.zero;
        private float defendZoneRadius = 0f;

        // Expose controllers for external access (if needed)
        public NavigationController GetNavigationController() => navigationController;
        public CombatController GetCombatController() => combatController;

        /// <summary>
        /// Returns true when the player is in the Mithrix arena (Y >= 400).
        /// Used to suppress pillar/jump_pad commands that are only relevant on the moon surface.
        /// More reliable than WasLaunchedByJumpPad which could false-positive on fall/respawn teleports.
        /// </summary>
        private static bool IsInMithrixArena(CharacterBody body)
        {
            return body != null && body.transform.position.y >= 400f;
        }

        /// <summary>
        /// Returns true when the moon escape sequence is actively running
        /// (EscapeSequenceMainState = Mithrix beaten, countdown started).
        /// False before Mithrix spawns, during the fight, and on the jump pad.
        /// </summary>
        private static bool IsEscapeSequenceActive()
        {
            EscapeSequenceController esc = UnityEngine.Object.FindObjectOfType<EscapeSequenceController>();
            if (esc == null) return false;
            return esc.mainStateMachine?.state is EscapeSequenceController.EscapeSequenceMainState;
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

        // Jump pad dwell: after arriving at the teleporter waypoint, hold position for
        // JUMP_PAD_DWELL_DURATION seconds before sending action_complete:success.
        // The Arena gate is already open at this point (all pillars charged = game opens it).
        // The dwell lets the teleporter trigger volume actually launch the bot.
        private float jumpPadDwellTimer = -1f;  // -1 = inactive; counts down when active
        private const float JUMP_PAD_DWELL_DURATION = 4f;

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
                navigationController.ResetWaypointRecorder();
                Log("[Drop Pod] State reset for new stage");
            }
        }

        public void TickWaypointRecorder(CharacterBody body)
        {
            navigationController?.TickWaypointRecorder(body);
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
                navigationController.ClearInteractionTarget(clearZoneReturn: true);
                navigationController.ClearIslandTracking();  // currentIsland + zoneReturnChain (full)
                navigationController.ResetRunFlags();  // WasArenaTeleported
                Log("[RESET] ✓ Navigation state reset (including island tracking + run flags)");
            }

            // Reset combat state
            currentTarget = null;
            currentMode = "roam";
            isFollowing = false;
            followUpdateTimer = 0f;
            defendZone = null;
            defendEscapeZone = null;
            defendZoneCenter = Vector3.zero;
            defendZoneRadius = 0f;
            combatController?.ClearZoneLeash();
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

                    case "QUERY_PILLARS":
                        queryHandlers?.HandleQueryPillars();
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
            if (string.IsNullOrWhiteSpace(args) || args.ToUpper() == "CANCEL")
            {
                // Full state reset — clears path follower, waypoint index, gates, smoothing,
                // stuck timers, interaction target, etc.  Previous code only cleared 3 fields
                // leaving stale path/smoothing/waypoint state that corrupted the next command.
                navigationController.CancelNavigation("GOTO:cancel");
                navigationController.ClearIslandTracking();
                // Force wait mode so the bot actually stops moving after cancel.
                currentMode = "wait";
                isFollowing = false;
                Log("[GOTO] Cancelled navigation — entering wait mode");
                return;
            }

            // Moon2 island navigation:
            //   GOTO:blood-main     → return from blood island to main platform
            //   GOTO:mass-design    → return from mass to main, then go to design
            //   GOTO:design         → go to design from main platform (no return needed)
            // First segment = "from" island (where we currently are).
            // Optional second segment after '-' = destination island ("main" = stop at platform).
            string lower = args.ToLower().Trim();
            int dashIdx = lower.IndexOf('-');
            string fromIsland = dashIdx >= 0 ? lower.Substring(0, dashIdx) : lower;
            string toIsland   = dashIdx >= 0 ? lower.Substring(dashIdx + 1) : null;

            if (toIsland == "main") toIsland = null;

            bool fromValid = string.IsNullOrEmpty(fromIsland)
                || NavigationController.Moon2PillarChains.ContainsKey(fromIsland);
            bool toValid = string.IsNullOrEmpty(toIsland)
                || NavigationController.Moon2PillarChains.ContainsKey(toIsland);

            if (!fromValid || !toValid)
            {
                Log($"[GOTO] Unknown island name(s): from='{fromIsland}' to='{toIsland}' — valid: blood, soul, mass, design");
                return;
            }

            // Fall back to tracked island if no "from" was specified
            if (string.IsNullOrEmpty(fromIsland))
                fromIsland = navigationController.currentIsland;

            Log($"[GOTO] Island navigation: from='{fromIsland ?? "main"}' to='{toIsland ?? "main"}'");

            if (!navigationController.StartIslandNavigation(fromIsland, toIsland))
                Log($"[GOTO] Failed to start island navigation");
        }

        void HandleModeCommand(string args)
        {
            string prevMode = currentMode;
            currentMode = args.ToLower();
            Log($"[MODE] Set to: {currentMode}");

            // Clear zone leash whenever leaving defend_zone
            if (prevMode == "defend_zone" && currentMode != "defend_zone")
            {
                combatController?.ClearZoneLeash();
                defendZone = null;
                defendEscapeZone = null;
            }

            // Update behavior based on mode
            switch (currentMode)
            {
                case "roam":
                    isFollowing = false;
                    // Clear any follow/zone-initiated navigation so combat/roam can resume
                    if ((prevMode == "follow" || prevMode == "defend_zone") && navigationController != null)
                    {
                        navigationController.isNavigating = false;
                        navigationController.gotoTarget = null;
                    }
                    break;
                case "combat":
                    isFollowing = false;
                    if ((prevMode == "follow" || prevMode == "defend_zone") && navigationController != null)
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
                    if ((prevMode == "follow" || prevMode == "defend_zone") && navigationController != null)
                    {
                        navigationController.isNavigating = false;
                        navigationController.gotoTarget = null;
                    }
                    break;
                case "defend_zone":
                {
                    isFollowing = false;
                    if (prevMode == "follow" && navigationController != null)
                    {
                        navigationController.isNavigating = false;
                        navigationController.gotoTarget = null;
                    }
                    CharacterBody modeBody = RainflayerPlugin.GetPlayerBody();
                    Vector3 modePos = modeBody != null ? modeBody.transform.position : Vector3.zero;
                    float nearestDist = float.MaxValue;
                    Vector3 chosenCenter = Vector3.zero;
                    float chosenRadius = 0f;
                    HoldoutZoneController chosenHZC = null;
                    EscapeSequenceExtractionZone chosenESZ = null;

                    // EscapeSequenceExtractionZone always takes priority over HoldoutZoneControllers
                    // (teleporter/pillars) — it's the final escape zone and must be defended if active.
                    // Note: EscapeSequenceExtractionZone is NOT registered with InstanceTracker,
                    // so we must use FindObjectsOfType to find it.
                    foreach (var z in UnityEngine.Object.FindObjectsOfType<EscapeSequenceExtractionZone>())
                    {
                        if (z == null || !z.isActiveAndEnabled) continue;
                        float d = Vector3.Distance(modePos, z.transform.position);
                        Log($"[MODE:defend_zone] Found EscapeSequenceExtractionZone at {z.transform.position}, radius={z.radius:F1}m, dist={d:F1}m");
                        if (d < nearestDist) { nearestDist = d; chosenCenter = z.transform.position; chosenRadius = z.radius; chosenHZC = null; chosenESZ = z; }
                    }

                    // Only fall back to HoldoutZoneControllers (teleporter, moon pillars) if no
                    // EscapeSequenceExtractionZone was found.
                    if (chosenESZ == null)
                    {
                        foreach (var z in InstanceTracker.GetInstancesList<HoldoutZoneController>())
                        {
                            if (z == null || !z.isActiveAndEnabled) continue;
                            float d = Vector3.Distance(modePos, z.transform.position);
                            if (d < nearestDist) { nearestDist = d; chosenCenter = z.transform.position; chosenRadius = z.currentRadius; chosenHZC = z; chosenESZ = null; }
                        }
                    }

                    if (chosenHZC == null && chosenESZ == null)
                    {
                        currentMode = prevMode;  // Revert mode change
                        Log("[MODE:defend_zone] No active zone found (no HoldoutZoneController or EscapeSequenceExtractionZone)");
                        socketBridge?.SendEvent("action_failed",
                            ("command", "MODE:defend_zone"),
                            ("reason", "no_active_zone")
                        );
                        return;
                    }
                    defendZone = chosenHZC;
                    defendEscapeZone = chosenESZ;
                    defendZoneCenter = chosenCenter;
                    defendZoneRadius = chosenRadius;
                    Log($"[MODE:defend_zone] Locked onto {(chosenESZ != null ? "EscapeZone" : "HoldoutZone")} at {chosenCenter}, radius={chosenRadius:F1}m, dist={nearestDist:F1}m");
                    combatController?.SetZoneLeash(chosenCenter, chosenRadius);
                    // If outside the zone, start navigating toward its center immediately
                    if (nearestDist > chosenRadius && navigationController != null)
                    {
                        navigationController.gotoInteractable = new InteractableInfo
                        {
                            Type = "zone_center",
                            Name = "DefendZoneCenter",
                            Position = chosenCenter,
                            Distance = nearestDist,
                            WaypointChain = new Vector3[] { chosenCenter },
                        };
                        navigationController.gotoTarget = null;
                        navigationController.isNavigating = true;
                    }
                    break;
                }
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

            // Ignore consecutive pillar/jump_pad commands while already chain-navigating to one
            // OR while currently holding the interaction (gotoInteractable is nulled at arrival
            // but interactionTarget is still set during the charge hold).
            // The brain re-issues these every ~4s, but each new gotoInteractable assignment
            // resets waypointChainIndex back to the nearest waypoint, discarding chain progress.
            // We keep the current navigation running until it ends naturally or a different
            // command type arrives.
            if (targetType == "pillar" || targetType == "jump_pad" || targetType == "ship")
            {
                bool alreadyNavigating = navigationController.gotoInteractable?.Type == targetType
                                         && navigationController.isNavigating;
                bool alreadyInteracting = navigationController.GetInteractionTarget() != null
                                          && navigationController.lastInteractionCommand == $"FIND_AND_INTERACT:{targetType}";
                if (alreadyNavigating || alreadyInteracting)
                {
                    Log($"[FIND_AND_INTERACT] Already {(alreadyInteracting ? "interacting with" : "navigating to")} {targetType} — ignoring redundant command");
                    return;
                }
            }

            // For jump_pad: also skip if we're already very close to the final destination
            // (navigation may have just finished, but brain fires another command before launch).
            if (targetType == "jump_pad")
            {
                Vector3[] jpChainCheck = NavigationController.Moon2JumpPadChain;
                if (jpChainCheck.Length > 0)
                {
                    Vector3 jpFinalPos = jpChainCheck[jpChainCheck.Length - 1];
                    float distToJumpPad = Vector3.Distance(body.transform.position, jpFinalPos);
                    if (distToJumpPad < 10f)
                    {
                        Log($"[FIND_AND_INTERACT] Already at jump pad ({distToJumpPad:F1}m) — ignoring redundant command");
                        return;
                    }
                }
            }

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
            else if (targetType == "pillar")
            {
                // Pillar commands are only valid on the moon surface (Y < 400). Once in the
                // Mithrix arena the pillars don't exist and the command makes no sense.
                if (IsInMithrixArena(body))
                {
                    Log($"[FIND_AND_INTERACT] In Mithrix arena (Y={body.transform.position.y:F0}) — pillar command ignored");
                    socketBridge?.SendEvent("action_failed",
                        ("command", "FIND_AND_INTERACT:pillar"),
                        ("reason", "in_mithrix_arena")
                    );
                    return;
                }

                // Find nearest uncharged moon battery pillar (moon2 / Commencement map)
                InteractableInfo[] pillars = entityDetector.FindMoonPillars();
                target = pillars.OrderBy(p => p.Distance).FirstOrDefault();

                if (target == null)
                {
                    Log($"[FIND_AND_INTERACT] No uncharged pillars found (all charged or not on moon2?)");
                    socketBridge?.SendEvent("action_failed",
                        ("command", "FIND_AND_INTERACT:pillar"),
                        ("reason", "not_found")
                    );
                    return;
                }

                Log($"[FIND_AND_INTERACT] Found pillar '{target.Name}' at {target.Distance:F1}m (charge: {target.ChargePercent:F0}%)");

                // If the bot is inside a pillar zone it must return to the main platform first.
                // PrependZoneReturnToTarget merges the return chain onto target.WaypointChain
                // and clears the stored zone chain (it's consumed into the merged navigation).
                bool wasInPillarZone = navigationController.IsInPillarZone;
                navigationController.PrependZoneReturnToTarget(target);
                if (wasInPillarZone)
                    Log($"[WAYPOINT] zone-return: returning to main platform then navigating to '{target.Name}'");
                else
                    Log($"[WAYPOINT] zone-return: navigating to '{target.Name}' from main platform");
            }
            else if (targetType == "jump_pad")
            {
                // Jump pad is only navigable from the moon surface. If we're already in the
                // Mithrix arena (Y >= 400) there's nowhere to navigate to.
                if (IsInMithrixArena(body))
                {
                    Log($"[FIND_AND_INTERACT] In Mithrix arena (Y={body.transform.position.y:F0}) — jump_pad command ignored");
                    socketBridge?.SendEvent("action_failed",
                        ("command", "FIND_AND_INTERACT:jump_pad"),
                        ("reason", "in_mithrix_arena")
                    );
                    return;
                }

                // Find the nearest active JumpVolume — the launch pad to the Mithrix arena
                GameObject jumpPadObj = entityDetector.FindMoonJumpPad();
                if (jumpPadObj != null)
                {
                    // Use the last chain waypoint as the final Position — it's the hardcoded
                    // teleporter landing spot.  The JumpVolume's snapped node is off the pad.
                    Vector3[] jpChain = NavigationController.Moon2JumpPadChain;
                    Vector3 finalPos = jpChain.Length > 0
                        ? jpChain[jpChain.Length - 1]
                        : entityDetector.SnapToNearestGroundNode(jumpPadObj.transform.position);
                    float dist = Vector3.Distance(body.transform.position, finalPos);
                    target = new InteractableInfo
                    {
                        GameObject = jumpPadObj,
                        Type = "jump_pad",
                        Name = jumpPadObj.name,
                        Position = finalPos,
                        Distance = dist,
                        WaypointChain = jpChain.Length > 0 ? jpChain : null
                    };
                    Log($"[FIND_AND_INTERACT] Found jump pad '{jumpPadObj.name}' at {dist:F1}m (teleporter waypoint pos)");

                    // If currently on a pillar island, prepend the return chain first.
                    navigationController.PrependZoneReturnToTarget(target);
                }
                else
                {
                    Log($"[FIND_AND_INTERACT] No jump pad found (all pillars charged yet?)");
                    socketBridge?.SendEvent("action_failed",
                        ("command", "FIND_AND_INTERACT:jump_pad"),
                        ("reason", "not_found")
                    );
                    return;
                }
            }
            else if (targetType == "ship")
            {
                // Guard: Phase 1 teleport only fires once the escape sequence is actually running
                // (EscapeSequenceMainState active = Mithrix beaten, moon detonation started).
                // This correctly handles: pre-spawn, mid-fight, flying up on jump pad, and the
                // brief window after death before the escape countdown starts.
                if (!navigationController.WasArenaTeleported && !IsEscapeSequenceActive())
                {
                    Log($"[FIND_AND_INTERACT] Escape sequence not yet active — ship command ignored (defeat Mithrix first)");
                    socketBridge?.SendEvent("action_failed",
                        ("command", "FIND_AND_INTERACT:ship"),
                        ("reason", "mithrix_not_defeated")
                    );
                    return;
                }

                if (!navigationController.WasArenaTeleported)
                {
                    // Phase 1: mod-teleport the player directly to the blood room landing spot.
                    // The arena escape orbs have no interactable representation in code — they are
                    // pure scene-configured triggers with no GenericInteraction/PurchaseInteraction.
                    // We skip them entirely and teleport directly to Moon2ShipChain[0].
                    Vector3 bloodRoomLanding = NavigationController.Moon2ShipChain[0]; // (-601, -170, 35)
                    CharacterBody playerBody = RainflayerPlugin.GetPlayerBody();
                    if (playerBody != null)
                    {
                        // Spawn the standard out-of-bounds teleport effect at the origin (departure burst)
                        // then teleport, then spawn the arrival burst — same pattern as MapZone.TeleportBody.
                        GameObject tpEffect = Run.instance?.GetTeleportEffectPrefab(playerBody.gameObject);
                        if (tpEffect != null)
                            EffectManager.SimpleEffect(tpEffect, playerBody.transform.position, Quaternion.identity, transmit: true);

                        TeleportHelper.TeleportBody(playerBody, bloodRoomLanding);
                        Log($"[SHIP] Phase 1 — mod-teleported player to blood room {bloodRoomLanding}");

                        // Arrival burst at destination
                        if (tpEffect != null)
                            EffectManager.SimpleEffect(tpEffect, bloodRoomLanding, Quaternion.identity, transmit: true);
                    }
                    else
                    {
                        Log($"[SHIP] Phase 1 — player body null, cannot teleport");
                    }
                    navigationController.WasArenaTeleported = true;
                }

                // Phase 2: navigate Moon2ShipChain from blood room to rescue ship.
                Vector3[] shipChain = NavigationController.Moon2ShipChain;
                Vector3 shipPos = shipChain[shipChain.Length - 1];
                float distToShip = Vector3.Distance(body.transform.position, shipPos);
                target = new InteractableInfo
                {
                    Type = "ship",
                    Name = "RescueShip",
                    Position = shipPos,
                    Distance = distToShip,
                    WaypointChain = shipChain,
                };
                Log($"[FIND_AND_INTERACT] ship Phase 2 — navigating to ship at {shipPos} ({distToShip:F1}m)");
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

            // ship Phase 2 has no GameObject (WaypointChain-only navigation) — treat same as snapped-position types.
            bool isShipPhase2 = target != null && target.Type == "ship" && target.GameObject == null && target.WaypointChain != null;
            if (target != null && (target.GameObject != null || isShipPhase2))
            {
                // Pillar and jump_pad positions are NodeGraph-snapped (may differ from the raw
                // GameObject transform). Navigation MUST use gotoInteractable.Position, not
                // gotoTarget.transform.position. Setting gotoTarget=null forces HandleGotoNavigation
                // to use gotoInteractable.Position so A* can plan a valid path.
                // For all other types (chest, shrine, teleporter, shop) gotoTarget is set
                // normally because their GameObjects sit at ground-level NodeGraph positions.
                bool useSnappedPosition = (target.Type == "pillar" || target.Type == "jump_pad" || isShipPhase2);
                navigationController.gotoTarget = useSnappedPosition ? null : target.GameObject;
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

                // === JUMP PAD DWELL ===
                // After arriving at the teleporter waypoint, hold position for a few seconds
                // so the launch trigger can fire before we declare success.
                if (jumpPadDwellTimer >= 0f)
                {
                    jumpPadDwellTimer -= Time.fixedDeltaTime;
                    // Stop moving while dwelling
                    body.inputBank.moveVector = Vector3.zero;
                    if (jumpPadDwellTimer <= 0f)
                    {
                        jumpPadDwellTimer = -1f;
                        socketBridge?.SendEvent("action_complete",
                            ("command", "FIND_AND_INTERACT:jump_pad"),
                            ("status", "success")
                        );
                        navigationController.gotoInteractable = null;
                        navigationController.gotoTarget = null;
                        Log("[JUMP_PAD] Dwell complete — success sent, nav cleared");
                    }
                    return;  // Don't run combat/nav logic while dwelling
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
                        if (currentMode == "follow" || navigationController.gotoInteractable?.Type == "zone_center")
                        {
                            // Arrived at ally or zone center - clear nav so combat logic can run this frame
                            navigationController.isNavigating = false;
                            navigationController.gotoTarget = null;
                            navigationController.gotoInteractable = null;
                            // Fall through to combat/defend_zone logic below
                        }
                        else
                        {
                            // Arrived at non-follow destination (chest, shrine, etc.)
                            if (navigationController.gotoInteractable != null)
                            {
                                var interactable = navigationController.gotoInteractable;

                                if (interactable.Type == "island_nav")
                                {
                                    // Pure island travel complete — no interaction needed, just notify.
                                    string gotoCmd = $"GOTO:{interactable.Name}";
                                    socketBridge?.SendEvent("action_complete",
                                        ("command", gotoCmd),
                                        ("status", "success")
                                    );
                                    navigationController.isNavigating = false;
                                    navigationController.gotoTarget = null;
                                    navigationController.gotoInteractable = null;
                                    Log($"[GOTO-ISLAND] Arrived at '{interactable.Name}' — action_complete sent");
                                }
                                else if (interactable.Type == "jump_pad")
                                {
                                    // Jump pad: arrived at teleporter waypoint.
                                    // Stop navigating and dwell so the teleporter trigger can fire.
                                    // The Arena gate is already open (game opens it after all pillars charged).
                                    // action_complete:success is sent after the dwell timer expires.
                                    if (jumpPadDwellTimer < 0f)
                                    {
                                        jumpPadDwellTimer = JUMP_PAD_DWELL_DURATION;
                                        Log($"[JUMP_PAD] Arrived at teleporter, dwelling {JUMP_PAD_DWELL_DURATION}s for launch");
                                    }
                                    // Hold position — don't clear nav state yet (keep bot standing still)
                                    navigationController.isNavigating = false;
                                }
                                else if (interactable.Type == "ship" && interactable.GameObject == null)
                                {
                                    // Phase 2 of FIND_AND_INTERACT:ship — arrived at the rescue ship position.
                                    // No interaction needed; the EscapeSequenceExtractionZone radius handles the win.
                                    socketBridge?.SendEvent("action_complete",
                                        ("command", "FIND_AND_INTERACT:ship"),
                                        ("status", "success")
                                    );
                                    navigationController.gotoInteractable = null;
                                    navigationController.gotoTarget = null;
                                    navigationController.isNavigating = false;
                                    Log($"[SHIP] Arrived at rescue ship — escape complete, action_complete sent");
                                }
                                else
                                {
                                    // Store command string so action_complete can match the ledger entry
                                    navigationController.lastInteractionCommand = $"FIND_AND_INTERACT:{interactable.Type}";
                                    navigationController.StartInteraction(interactable.GameObject);
                                    // Pillar: record which island we're on so PrependZoneReturnToTarget
                                    // can dynamically build the correct return chain for the next command.
                                    if (interactable.Type == "pillar")
                                    {
                                        string pillarNameLower = interactable.Name.ToLower();
                                        string islandType = pillarNameLower.Contains("blood")  ? "blood"
                                                          : pillarNameLower.Contains("soul")   ? "soul"
                                                          : pillarNameLower.Contains("mass")   ? "mass"
                                                          : pillarNameLower.Contains("design") ? "design"
                                                          : null;
                                        if (islandType != null)
                                        {
                                            navigationController.currentIsland = islandType;
                                            Log($"[WAYPOINT] island tracking set: currentIsland='{islandType}' for '{interactable.Name}'");
                                        }
                                        else
                                        {
                                            Log($"[WAYPOINT] WARNING: could not determine island type for '{interactable.Name}' — zone return may not work");
                                        }
                                    }
                                    navigationController.gotoInteractable = null;
                                    // Clear nav state so if enemies interrupt via ClearInteractionTarget(),
                                    // isNavigating=true + gotoTarget=chest won't create an infinite arrived-but-nothing loop
                                    navigationController.isNavigating = false;
                                    navigationController.gotoTarget = null;
                                }
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

                if (currentMode == "defend_zone")
                {
                    // Re-validate zone each frame — check both zone types
                    bool zoneValid = (defendZone != null && defendZone.isActiveAndEnabled)
                                  || (defendEscapeZone != null && defendEscapeZone.isActiveAndEnabled);
                    if (!zoneValid)
                    {
                        Log("[MODE:defend_zone] Zone became inactive, reverting to roam");
                        currentMode = "roam";
                        combatController?.ClearZoneLeash();
                        defendZone = null;
                        defendEscapeZone = null;
                        if (navigationController != null)
                        {
                            navigationController.isNavigating = false;
                            navigationController.gotoTarget = null;
                            navigationController.gotoInteractable = null;
                        }
                    }
                    else
                    {
                        // Update center+radius each frame (HZC radius scales as zone charges; ESZ radius is fixed)
                        if (defendZone != null)
                        {
                            defendZoneCenter = defendZone.transform.position;
                            defendZoneRadius = defendZone.currentRadius;
                        }
                        else
                        {
                            defendZoneCenter = defendEscapeZone.transform.position;
                            defendZoneRadius = defendEscapeZone.radius;
                        }
                        combatController?.SetZoneLeash(defendZoneCenter, defendZoneRadius);
                        // Cancel the initial zone-center navigation once we've arrived inside the zone.
                        // After that, CombatController's zone leash clamps all roam/strafe movement
                        // to stay within the radius — no per-frame re-navigation needed.
                        if (navigationController != null && navigationController.isNavigating
                            && navigationController.gotoInteractable?.Type == "zone_center")
                        {
                            float distToCenter = Vector3.Distance(body.transform.position, defendZoneCenter);
                            if (distToCenter <= defendZoneRadius)
                            {
                                navigationController.isNavigating = false;
                                navigationController.gotoInteractable = null;
                                navigationController.gotoTarget = null;
                            }
                        }
                        // Fall through to normal combat logic below
                    }
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