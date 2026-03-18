using RoR2;
using RoR2.CharacterAI;
using RoR2.Navigation;
using UnityEngine;
using System;
using System.Collections.Generic;

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
        // Gate kept open for the duration of active navigation (physical traversal requires it)
        private string activeGate = null;

        // Smooth direction interpolation during navigation (prevents camera jolts on waypoint transitions)
        private Vector3 smoothedNavMoveDir = Vector3.zero;
        private Vector3 smoothedNavAimDir = Vector3.zero;
        private const float NAV_MOVE_SMOOTH = 0.15f;  // Move direction lerp factor per fixed frame
        private const float NAV_AIM_SMOOTH = 0.07f;   // Aim/camera lerp factor (slower = smoother camera)

        // Set to true after the mod teleports the player out of the Mithrix arena to the blood
        // room landing spot. Switches FIND_AND_INTERACT:ship from Phase 1 (arena escape teleport)
        // to Phase 2 (navigate Moon2ShipChain to the rescue ship).
        public bool WasArenaTeleported = false;

        // Enhanced stuck detection with frustration
        private float frustration = 0f;  // Increases when stuck, resets when making progress
        private const float FRUSTRATION_THRESHOLD = 10f;  // Give up after frustration reaches 10
        private const float FRUSTRATION_INCREASE_RATE = 1f;  // Increase by 1 per second while stuck
        public bool attemptStuck = false;  // Mark current target as potentially unreachable

        // Two-phase navigation state (interactables with a NavWaypoint)
        private bool navWaypointConsumed = false;
        private const float NAV_WAYPOINT_ARRIVAL_THRESHOLD = 25f;  // Larger arrival radius for intermediate stop

        // Waypoint chain navigation (multi-hop through disconnected subgraphs, e.g. moon2 pillar islands)
        private int waypointChainIndex = 0;
        private const float WAYPOINT_CHAIN_ARRIVAL = 5f;       // Arrival radius for intermediate chain waypoints
        private const float WAYPOINT_CHAIN_FINAL_ARRIVAL = 5f; // Arrival for last chain waypoint — large enough to fire before JumpVolume trigger

        // A* path-node stall detection: PathFollower advances nodes only when XZ dist ≤ 2-4m AND
        // Y diff ≤ 2m.  On moon2's sloped terrain, nodes can have Y offsets that silently fail the
        // Y check, causing the bot to circle the same intermediate node indefinitely even while the
        // overall stuck/frustration counter stays low (bot IS moving, just orbiting).
        // When the same node is targeted for PATHNODE_STALL_TIMEOUT seconds while within
        // PATHNODE_STALL_DIST XZ metres, we force a path reset so the bot re-routes.
        private Vector3 pathNodeStallPos = Vector3.zero;
        private float pathNodeStallTimer = 0f;
        private const float PATHNODE_STALL_TIMEOUT = 3.5f;
        private const float PATHNODE_STALL_DIST = 7f;

        // Beeline wall-following state: when a wall blocks the direct path to the next waypoint,
        // the bot steers around it by following the clearer side (left or right).
        // steerSide: +1 = right, -1 = left.  steerTimer accumulates while blocked;
        // after STEER_FLIP_TIMEOUT we flip sides in case the first choice was wrong.
        private float beeline_steerTimer = 0f;
        private int beeline_steerSide = 0;   // 0=unchosen, +1=right, -1=left
        private const float STEER_FLIP_TIMEOUT = 3f;  // flip steer side after this many seconds blocked

        // Which pillar island the bot is currently on.
        // Set when the bot arrives at a pillar and begins interacting with it.
        // Used by PrependZoneReturnToTarget to dynamically build the return chain
        // without storing a separate copy — the chain is derived from currentIsland
        // at command time so it's always consistent with the bot's actual location.
        // Cleared by GOTO:cancel (CancelNavigation) or on stage/respawn (ClearIslandTracking).
        public string currentIsland = null;

        // True when the bot is on a pillar island (currentIsland != null).
        public bool IsInPillarZone => currentIsland != null;

        // Length of the return portion prepended onto the current gotoInteractable chain.
        // Used to detect when the bot has finished the return leg and is on the main platform.
        // Cleared when the bot advances past all return waypoints or navigation resets.
        private int activeReturnChainLength = 0;

        public void ClearIslandTracking()
        {
            currentIsland = null;
            activeReturnChainLength = 0;
        }

        // Kept for backward compatibility — no-op now that zone return is derived dynamically.
        public void ClearZoneReturn() { }
        public void SetZoneReturnChain(Vector3[] chain, int? skipCap = null, System.Collections.Generic.HashSet<int> beelineIndices = null) { }

        /// <summary>
        /// If we are currently in a pillar zone, prepend the zone return chain onto
        /// <paramref name="target"/>'s WaypointChain (merging navigation overrides).
        /// No-op if not in a pillar zone.  Shared by FIND_AND_INTERACT:pillar/jump_pad
        /// and GOTO island commands.
        /// </summary>
        public void PrependZoneReturnToTarget(InteractableInfo target)
        {
            if (!IsInPillarZone) return;  // currentIsland == null → already on main platform

            // Build the return chain dynamically from currentIsland.
            // This avoids storing a stale cached chain — we always use the authoritative
            // chain for whichever island we're actually on.
            Vector3[] returnChain = null;
            int? returnSkipCap = null;
            System.Collections.Generic.HashSet<int> returnBeelineIdx = null;

            if (Moon2PillarReturnChains.TryGetValue(currentIsland, out var customReturn))
            {
                returnChain = customReturn;
            }
            else if (Moon2PillarChains.TryGetValue(currentIsland, out var fwdChain))
            {
                returnChain = (Vector3[])fwdChain.Clone();
                System.Array.Reverse(returnChain);
            }

            if (returnChain == null || returnChain.Length == 0)
            {
                Log($"[WAYPOINT] zone-return: no return chain for island '{currentIsland}' — navigating directly to '{target.Name}'");
                return;
            }

            CharacterBody body = RainflayerPlugin.GetPlayerBody();

            // Mass island return overrides (same as before, now computed here instead of at arrival).
            if (string.Equals(currentIsland, "mass", System.StringComparison.OrdinalIgnoreCase))
            {
                returnSkipCap = 1;
                returnBeelineIdx = new System.Collections.Generic.HashSet<int> { 2, 3, 4 };
            }

            // Design pillar return: the hardcoded chain starts at room2 [0], but the pillar may be
            // in the shallow section (post-ledge 2 [3]).  Probe both entry points at the time we
            // actually need to return so we use the current player position, not the arrival position.
            // Design return chain: [0]=room2, [1]=bridge end, [2]=bridge start,
            //                      [3]=post-ledge 2, [4]=pre-ledge 2, [5]=mid-ledge, [6]=main platform
            if (string.Equals(currentIsland, "design", System.StringComparison.OrdinalIgnoreCase)
                && returnChain.Length >= 4 && body != null)
            {
                bool shallowReachable = IsReachable(body, body.transform.position, returnChain[3]);
                bool deepReachable    = IsReachable(body, body.transform.position, returnChain[0]);
                int startIdx = (!deepReachable && shallowReachable) ? 3 : 0;
                if (startIdx > 0)
                {
                    returnChain = returnChain[startIdx..];
                    Log($"[WAYPOINT] design return: shallow section — trimming to {returnChain.Length} waypoints");
                }
                else
                {
                    Log($"[WAYPOINT] design return: deep section (deepReachable={deepReachable} shallowReachable={shallowReachable})");
                }
            }

            // If the target is reachable from the current position without leaving the island
            // (same subgraph), skip the return chain — both pillars are on the same island.
            if (body != null && IsReachable(body, body.transform.position, target.Position))
            {
                Log($"[WAYPOINT] zone-return: '{target.Name}' reachable from current position on '{currentIsland}' island — skipping return chain");
                return;
            }

            Vector3[] forwardChain = target.WaypointChain ?? new Vector3[0];
            Vector3[] merged = new Vector3[returnChain.Length + forwardChain.Length];
            System.Array.Copy(returnChain, merged, returnChain.Length);
            System.Array.Copy(forwardChain, 0, merged, returnChain.Length, forwardChain.Length);
            target.WaypointChain = merged;

            if (returnSkipCap.HasValue)
            {
                int existing = target.WaypointChainSkipCap ?? int.MaxValue;
                target.WaypointChainSkipCap = System.Math.Min(returnSkipCap.Value, existing);
            }
            if (returnBeelineIdx != null && returnBeelineIdx.Count > 0)
            {
                if (target.WaypointChainBeelineIndices == null)
                    target.WaypointChainBeelineIndices = new System.Collections.Generic.HashSet<int>(returnBeelineIdx);
                else
                    foreach (int idx in returnBeelineIdx) target.WaypointChainBeelineIndices.Add(idx);
            }

            // Record how many waypoints are the return leg so we know when we've crossed back.
            activeReturnChainLength = returnChain.Length;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[WAYPOINT] zone-return prepend (from '{currentIsland}'): {returnChain.Length} return + {forwardChain.Length} forward to '{target.Name}':");
            for (int i = 0; i < returnChain.Length; i++)
                sb.Append($"\n  [return {i}] {returnChain[i]:F0}");
            for (int i = 0; i < forwardChain.Length; i++)
                sb.Append($"\n  [fwd {i}] {forwardChain[i]:F0}");
            Log(sb.ToString());
        }

        /// <summary>
        /// Start a pure island-travel navigation:
        ///   <paramref name="returnIsland"/>  - island to return FROM (null = already on main platform)
        ///   <paramref name="destIsland"/>    - island to go TO after returning (null = stop at main)
        /// Updates currentIsland and kicks off waypoint-chain navigation.
        /// Returns false if no chains could be found.
        /// </summary>
        public bool StartIslandNavigation(string returnIsland, string destIsland)
        {
            Vector3[] returnChain = null;
            if (!string.IsNullOrEmpty(returnIsland))
            {
                if (Moon2PillarReturnChains.TryGetValue(returnIsland, out var custom))
                    returnChain = custom;
                else if (Moon2PillarChains.TryGetValue(returnIsland, out var fwd))
                {
                    returnChain = new Vector3[fwd.Length];
                    System.Array.Copy(fwd, returnChain, fwd.Length);
                    System.Array.Reverse(returnChain);
                }
            }

            Vector3[] forwardChain = null;
            if (!string.IsNullOrEmpty(destIsland))
                Moon2PillarChains.TryGetValue(destIsland, out forwardChain);

            if (returnChain == null && forwardChain == null)
            {
                Log($"[GOTO-ISLAND] No chains found for return='{returnIsland}' dest='{destIsland}'");
                return false;
            }

            int returnLen = returnChain?.Length ?? 0;
            int forwardLen = forwardChain?.Length ?? 0;
            Vector3[] merged = new Vector3[returnLen + forwardLen];
            if (returnChain != null) System.Array.Copy(returnChain, merged, returnLen);
            if (forwardChain != null) System.Array.Copy(forwardChain, 0, merged, returnLen, forwardLen);

            Vector3 finalPos = merged[merged.Length - 1];

            // Name = original args (e.g. "blood-main", "soul") so action_complete command matches the ledger entry.
            string navName = string.IsNullOrEmpty(returnIsland)
                ? destIsland                                          // "GOTO:soul"
                : string.IsNullOrEmpty(destIsland)
                    ? $"{returnIsland}-main"                          // "GOTO:blood-main"
                    : $"{returnIsland}-{destIsland}";                 // "GOTO:mass-design"
            var navTarget = new InteractableInfo
            {
                GameObject = null,
                Type = "island_nav",
                Name = navName,
                Position = finalPos,
                WaypointChain = merged,
                // Don't allow skip-ahead past the end of the return portion —
                // the bot must physically traverse the return before going forward.
                WaypointChainSkipCap = returnLen > 0 ? (int?)(returnLen - 1) : null,
            };

            // Update island tracking
            currentIsland = string.IsNullOrEmpty(destIsland) ? null : destIsland;
            // Clear stored zone-return since we're building a fresh combined chain
            ClearZoneReturn();

            gotoTarget = null;
            gotoInteractable = navTarget;
            isNavigating = true;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[GOTO-ISLAND] Starting '{navTarget.Name}': {merged.Length} waypoints");
            for (int i = 0; i < merged.Length; i++)
                sb.Append($"\n  [{i}] {merged[i]:F0}" + (i < returnLen ? " [return]" : " [fwd]"));
            sb.Append($"\n  [final] {finalPos:F0}");
            Log(sb.ToString());

            return true;
        }

        // Waypoint recorder (RecordWaypoints config)
        private float waypointRecordTimer = 0f;
        private const float WAYPOINT_RECORD_INTERVAL = 1f;
        private Vector3 waypointShipOrigin = Vector3.zero;
        private bool waypointOriginCaptured = false;

        // Hardcoded waypoint chains per moon2 pillar type.
        // Recorded with RecordWaypoints=true — last reachable=True pos before graph gap,
        // then island landing zones.  A* handles each hop if reachable; beeline otherwise.
        public static readonly Dictionary<string, Vector3[]> Moon2PillarChains =
            new Dictionary<string, Vector3[]>(System.StringComparer.OrdinalIgnoreCase)
        {
            // Blood: all waypoints reachable from main graph — guides bot through wall opening
            { "blood", new Vector3[]
                {
                    new Vector3(-286f, -175f, -4f),   // [1] pre-wall
                    new Vector3(-415f, -175f,  0f),   // [2] post-wall
                    new Vector3(-601f, -170f, 35f),   // [3] blood room / pillar
                }
            },
            // Soul: floating rock chain — mostly unreachable, requires beeline + jumps
            { "soul", new Vector3[]
                {
                    new Vector3(-121f, -187f,  95f),  // [1] main platform approach
                    // new Vector3(-130f, -180f, 103f),  // [-] first floating rock (reachable)
                    // new Vector3(-137f, -174f, 109f),  // [-] second floating rock — jump needed
                    new Vector3(-141f, -185f, 99f),  // [2] main soul island
                    new Vector3(-151f, -170f, 110f),  // [2.5] main soul island
                    new Vector3(-128f, -153f, 184f),  // [3] pre-ledge 1
                    // new Vector3(-167f, -187f, 60f),   // [2a] main platform 2
                    // new Vector3(-177f, -187f, 55f),   // [3a] rock
                    // new Vector3(-188f, -186f, 55f),   // [4a] post rock
                    // new Vector3(-198f, -182f, 50f),   // [5a] post rock 2
                    // new Vector3(-194f, -171f, 75f),  // [6a] jump to island
                    // new Vector3(-146f, -158f, 138f),   // [7a] pre-ledge 1
                    // new Vector3(-118f, -162f, 157f),  // [8a] post-ledge 1 
                    // new Vector3(-128f, -153f, 184f),  // [9a] redirect to open area
                    // new Vector3(-146f, -153f, 192f),  // [-] post-ledge 1
                    // new Vector3(-148f, -144f, 227f),  // [-] pre-ledge 2
                    // new Vector3(-125f, -154f, 225f),  // [-] post-ledge 2 — optimal sprint jump
                    // new Vector3( -88f, -147f, 222f),  // [-] post-ledge 3
                    new Vector3(-79f, -155f, 186f),  // [X] pre-ledge 2
                    new Vector3(-74f, -150f, 204f),  // [Y] post-ledge 2
                    new Vector3( -18f, -120f, 274f),  // [10] outside room
                    new Vector3(-109f, -105f, 319f),  // [11] soul room / pillar
                }
            },
            // Mass: chain bridge crossing — waypoint 3 needs a small jump
            { "mass", new Vector3[]
                {
                    new Vector3(  64f, -191f,   8f),  // [1] main platform approach (reachable)
                    new Vector3(  77f, -187f,   1f),  // [2] closer approach (reachable)
                    new Vector3(  81f, -182f,   3f),  // [3] ledge — needs jump
                    new Vector3( 173f, -184f,  15f),  // [4] chain bridge start
                    new Vector3( 252f, -193f,  30f),  // [5] chain bridge end
                    new Vector3( 275f, -177f,  40f),  // [6] outside pillar (do not skip + override with beeline)
                    new Vector3( 319f, -168f, -11f),  // [7] pillar room
                }
            },
            // Design: two ledge hops into disconnected subgraph
            { "design", new Vector3[]
                {
                    new Vector3(-229f, -169f,  -42f), // [1] main platform approach (reachable)
                    new Vector3(-229f, -172f,  -59f), // [2] jump to first ledge
                    new Vector3(-255f, -167f, -144f), // [3] pre-ledge 2
                    new Vector3(-263f, -168f, -155f), // [4] post-ledge 2
                    new Vector3(-342f, -158f, -231f), // [5] design room / pillar
                }
            },
        };

        // Waypoint chain for the Mithrix jump pad (shares first hops with mass, then diverges).
        public static readonly Vector3[] Moon2JumpPadChain = new Vector3[]
        {
            new Vector3(  64f, -191f,   8f),  // [1] same as mass [1]
            new Vector3(  77f, -187f,   1f),  // [2] same as mass [2]
            new Vector3(  81f, -182f,   3f),  // [3] ledge — needs jump
            new Vector3( 173f, -184f,  15f),  // [4] chain bridge start
            new Vector3( 252f, -193f,  30f),  // [5] chain bridge end
            new Vector3( 248f, -170f,  59f),  // [6] pre-ledge (do not skip)
            new Vector3( 222f, -182f,  84f),  // [7] post-ledge — on teleporter (override with beeline)
        };

        // Waypoint chain for Phase 2 of FIND_AND_INTERACT:ship (post-arena escape).
        // Phase 1 mod-teleports the player directly to the blood room ([1] below).
        // Phase 2 follows this chain back through the blood passage to the main platform, then to the ship.
        public static readonly Vector3[] Moon2ShipChain = new Vector3[]
        {
            new Vector3(-601f, -170f,  35f),  // [1] blood room (post-orb landing area)
            new Vector3(-415f, -175f,   0f),  // [2] post-wall (blood return)
            new Vector3(-286f, -175f,  -4f),  // [3] pre-wall / back on main platform
            new Vector3( 302f, -171f, 384f),  // [4] rescue ship / moon exit
        };

        // Custom return paths for pillar zones where reversing the forward chain doesn't work.
        // Blood/soul/mass return = forward chain reversed (same ledges, same jumps in reverse).
        // Design is one-way (you jump DOWN into it) so a separate ascent path is recorded.
        // EntityDetector.FindMoonPillars builds ReturnChain at runtime:
        //   - design: looks up this table
        //   - others: Array.Reverse(forwardChain)
        public static readonly Dictionary<string, Vector3[]> Moon2PillarReturnChains =
            new Dictionary<string, Vector3[]>(System.StringComparer.OrdinalIgnoreCase)
        {
            // Design room forward path is all downward jumps → can't reverse.
            // These waypoints climb back up from second island with chain and land on the main platform.
            { "design", new Vector3[]
                {
                    new Vector3(-228f, -190f, -311f),   // [1] room2 
                    new Vector3(-252f, -180f, -323f),   // [2] bridge end
                    new Vector3(-279f, -164f, -281f),   // [3] bridge start
                    new Vector3(-263f, -168f, -155f), // [4] post-ledge 2
                    new Vector3(-255f, -167f, -144f), // [5] pre-ledge 2
                    new Vector3(-173f, -155f, -77f),   // [6] mid-ledge         reachable=False
                    new Vector3(-167f, -187f, -59f),   // [7] main platform     reachable=True
                }
            },
        };

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

                    // Interaction failed and door isn't disabled — in multiplayer this likely
                    // means we grabbed a teammate's pod (ConditionNotMet). Clear the cached
                    // door reference so FindDropPodDoor re-scans and picks the correct one.
                    dropPodDoor = null;
                    if (Time.frameCount % 30 == 0)
                    {
                        Log($"[Drop Pod] Interaction failed (possibly teammate's pod), re-scanning next frame...");
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
        /// Find the drop pod door that belongs to us.
        /// In multiplayer there are multiple pods (one per player) all nearby.
        /// We find all survivor pod doors and return the one whose IInteractable
        /// reports Available for our Interactor — that's our door, not a teammate's.
        /// Falls back to the closest pod if none report Available yet (timing edge case).
        /// </summary>
        private GameObject FindDropPodDoor(CharacterBody body)
        {
            if (body == null)
            {
                Log("[Drop Pod] Body is null!");
                return null;
            }

            var interactor = body.GetComponent<Interactor>();
            MonoBehaviour[] allComponents = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();

            // Collect all nearby survivor pod doors, sorted by distance
            var candidates = new System.Collections.Generic.List<(GameObject go, float dist, IInteractable interactable)>();

            foreach (MonoBehaviour component in allComponents)
            {
                if (component == null || component.gameObject == null)
                    continue;

                IInteractable interactable = component as IInteractable;
                if (interactable == null)
                    continue;

                string nameLower = component.gameObject.name.ToLower();

                // Must be a survivor pod door
                if (!nameLower.Contains("survivorpod"))
                    continue;

                // Exclude non-door sub-components
                if (nameLower.Contains("battery") || nameLower.Contains("panel") ||
                    nameLower.Contains("batterypanel") || nameLower.Contains("fuel"))
                    continue;

                float distance = Vector3.Distance(body.transform.position, component.transform.position);
                if (distance <= 20f)
                    candidates.Add((component.gameObject, distance, interactable));
            }

            if (candidates.Count == 0)
                return null;

            // Sort by distance (our pod is the one we're standing inside, so closest)
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            // Prefer a door that is Available for our interactor (our pod, not a teammate's)
            if (interactor != null)
            {
                foreach (var (go, dist, interactable) in candidates)
                {
                    Interactability state = interactable.GetInteractability(interactor);
                    if (state == Interactability.Available || state == Interactability.Disabled)
                    {
                        Log($"[Drop Pod] ===== SELECTED (state={state}): {go.name} at {dist:F1}m =====");
                        return go;
                    }
                }
            }

            // Fallback: return closest pod (single-player, or timing edge case before pod opens)
            var closest = candidates[0];
            Log($"[Drop Pod] ===== SELECTED (fallback/closest): {closest.go.name} at {closest.dist:F1}m =====");
            return closest.go;
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
        ///
        /// When chainTarget is provided (waypoint-chain beeline phase), two extra triggers are added:
        ///   (a) Target waypoint is >1.5 m above current position (elevation jump).
        ///   (b) Ground disappears ~1.5 m ahead (gap jump — sprint to carry across).
        /// </summary>
        public Vector3 ApplyJumpDetection(CharacterBody body, Vector3 direction, Vector3? chainTarget = null)
        {
            // Normalize and flatten direction
            direction = direction.normalized;
            direction.y = 0;

            // === JUMP / WALL DETECTION ===
            // Fire three horizontal rays (shin, waist, chest) to detect obstacles ahead.
            // For each hit, probe downward from above the hit point to find the TRUE top of the
            // obstacle — because the rays are horizontal, hit.point.y just reflects the ray's
            // own Y origin, not the wall's actual height.
            bool obstacleDetected = false;
            bool shouldJump = false;
            float wallTopHeight = 0f;  // highest confirmed top of any detected obstacle
            float minHitNormalY = 1f;  // lowest Y component of any hit normal (1 = flat ground, 0 = vertical wall)

            float[] rayHeights = new float[] { 0.3f, 1.0f, 1.8f };
            const float PROBE_DIST = 2.5f;
            const float MAX_JUMPABLE = 2.8f;  // CharacterMotor max jump height approx

            foreach (float rh in rayHeights)
            {
                Vector3 rayOrigin = body.transform.position + Vector3.up * rh;
                RaycastHit hit;
                if (Physics.Raycast(rayOrigin, direction, out hit, PROBE_DIST, LayerMask.GetMask("World")))
                {
                    obstacleDetected = true;
                    if (hit.normal.y < minHitNormalY)
                        minHitNormalY = hit.normal.y;

                    // Probe downward from 5 m above the hit XZ position to find the wall's actual top.
                    Vector3 probeTop = new Vector3(hit.point.x, body.transform.position.y + 5f, hit.point.z);
                    RaycastHit topHit;
                    float topY;
                    if (Physics.Raycast(probeTop, Vector3.down, out topHit, 5f, LayerMask.GetMask("World")))
                        topY = topHit.point.y;
                    else
                        topY = body.transform.position.y + MAX_JUMPABLE + 1f;  // assume unjumpable if no surface found above

                    float topAboveBody = topY - body.transform.position.y;
                    if (topAboveBody > wallTopHeight)
                        wallTopHeight = topAboveBody;

                    if (Time.frameCount % 60 == 0)
                        Log($"[JUMP] obstacle at {hit.distance:F1}m rh={rh:F1}  wallTop={topAboveBody:F1}m  normal.y={hit.normal.y:F2}");
                }
            }

            if (obstacleDetected)
            {
                // Jumpable: obstacle top is reachable by a single jump.
                // Normal check: a sloped hill has a mostly-upward normal (normal.y > 0.4) and can
                // be walked over without jumping. Only jump when the face is near-vertical (normal.y < 0.4)
                // — a true ledge or wall that physically blocks forward movement.
                bool isTrueWall = minHitNormalY < 0.4f;
                if (wallTopHeight > 0.25f && wallTopHeight <= MAX_JUMPABLE && isTrueWall)
                    shouldJump = true;
                // else: sloped surface (walk over it), or wall too tall to jump → fall through to steer logic
            }

            // === CHAIN WAYPOINT JUMP (gap + elevation) ===
            // Only active during beeline phase of waypoint-chain navigation.
            // Must be grounded to avoid double-jump / air-jump spam.
            // === CHAIN MIDAIR WALL-SCALE ===
            // While airborne during chain beeline and a true wall (near-vertical normal) is still
            // blocking us, keep jumping to scale it — same wall-scale behaviour as the old
            // obstacle-raycast midair trigger but scoped to chain navigation only.
            if (!shouldJump && chainTarget.HasValue && body.characterMotor != null && !body.characterMotor.isGrounded
                && obstacleDetected && minHitNormalY < 0.4f)
            {
                shouldJump = true;
                if (Time.frameCount % 60 == 0)
                    Log($"[CHAIN-JUMP] Midair wall-scale (normal.y={minHitNormalY:F2})");
            }

            if (!shouldJump && chainTarget.HasValue && body.characterMotor != null && body.characterMotor.isGrounded)
            {
                Vector3 pos = body.transform.position;
                float distToWaypoint = Vector3.Distance(pos, chainTarget.Value);

                // (a) Target is noticeably above AND we're already close to it.
                // Guard: only within 6 m so we don't bunny-hop on flat terrain 20 m away from a ledge.
                // The obstacle raycasts handle the actual ledge approach; this catches cases where
                // the ledge top is inside the arrival radius and raycasts miss it.
                // Threshold raised to 4.0m to avoid jumping over small hills/slopes (e.g. soul island mini-hill).
                if (distToWaypoint < 6f && chainTarget.Value.y - pos.y > 4.0f)
                {
                    shouldJump = true;
                    if (Time.frameCount % 60 == 0)
                        Log($"[CHAIN-JUMP] Elevation: +{chainTarget.Value.y - pos.y:F1}m at {distToWaypoint:F1}m to waypoint");
                }

                // (b) Ground disappears 2 m ahead → sprint-jump to cross the gap.
                // Probe 5 m down (was 3 m) to avoid false positives on hilly terrain — real
                // gaps on moon2 are 10 m+ deep, gentle slopes still hit ground within 5 m.
                if (!shouldJump)
                {
                    Vector3 probeOrigin = pos + direction * 2f + Vector3.up * 0.5f;
                    if (!Physics.Raycast(probeOrigin, Vector3.down, 5f, LayerMask.GetMask("World")))
                    {
                        shouldJump = true;
                        if (Time.frameCount % 60 == 0)
                            Log($"[CHAIN-JUMP] Gap ahead at {distToWaypoint:F1}m to waypoint {chainTarget.Value:F0}");
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
                // Reset wall-follow state — steering must not corrupt jump trajectory.
                // Return unmodified forward direction so momentum carries the jump straight.
                beeline_steerTimer = 0f;
                beeline_steerSide = 0;
                return direction;
            }

            // === WALL FOLLOWING (non-jumpable obstacle) ===
            // If the obstacle is too tall to jump, steer around it by picking the clearer side
            // (left or right) and blending progressively more perpendicular movement over time.
            // After STEER_FLIP_TIMEOUT seconds on one side we try the opposite.
            // Only steer when grounded — don't fight momentum while airborne from a jump.
            bool isGrounded = body.characterMotor == null || body.characterMotor.isGrounded;
            if (!isGrounded)
                return direction;  // Airborne (from recent jump) — hold direction, don't steer
            if (obstacleDetected && !shouldJump)
            {
                beeline_steerTimer += Time.fixedDeltaTime;

                // On first block (or after flip timeout), choose / re-choose a side.
                if (beeline_steerSide == 0 || beeline_steerTimer >= STEER_FLIP_TIMEOUT)
                {
                    if (beeline_steerTimer >= STEER_FLIP_TIMEOUT)
                    {
                        beeline_steerSide = -beeline_steerSide;  // flip
                        beeline_steerTimer = 0f;
                        Log($"[WAYPOINT] wall-follow: flipping steer side to {(beeline_steerSide > 0 ? "right" : "left")}");
                    }
                    else
                    {
                        // Pick the side with more clearance: cast two lateral rays and choose the one
                        // that has a hit further away (or no hit = fully clear).
                        Vector3 right = Vector3.Cross(direction, Vector3.up);  // +1 = right
                        float rightDist = float.MaxValue, leftDist = float.MaxValue;
                        RaycastHit sideHit;
                        Vector3 checkOrigin = body.transform.position + Vector3.up * 1.0f;
                        if (Physics.Raycast(checkOrigin,  right, out sideHit, 4f, LayerMask.GetMask("World")))
                            rightDist = sideHit.distance;
                        if (Physics.Raycast(checkOrigin, -right, out sideHit, 4f, LayerMask.GetMask("World")))
                            leftDist = sideHit.distance;

                        beeline_steerSide = (rightDist >= leftDist) ? 1 : -1;
                        Log($"[WAYPOINT] wall-follow: chose {(beeline_steerSide > 0 ? "right" : "left")} " +
                            $"(rightDist={rightDist:F1}m leftDist={leftDist:F1}m)");
                    }
                }

                // Blend steering: starts at 50%, ramps to 100% pure sideways at ~2s.
                float steerBlend = Mathf.Clamp01(0.5f + beeline_steerTimer / (STEER_FLIP_TIMEOUT * 2f));
                Vector3 steerDir = Vector3.Cross(direction, Vector3.up) * beeline_steerSide;
                direction = Vector3.Lerp(direction, steerDir, steerBlend).normalized;

                if (Time.frameCount % 60 == 0)
                    Log($"[WAYPOINT] wall-follow: side={beeline_steerSide}  blend={steerBlend:F2}  blocked={beeline_steerTimer:F1}s");
            }
            else if (!obstacleDetected)
            {
                // Clear path — reset wall-follow state so next block starts fresh
                beeline_steerTimer = 0f;
                beeline_steerSide = 0;
            }

            return direction;
        }

        /// <summary>
        /// During chain navigation, check if we can skip ahead to a later waypoint or
        /// directly to the final target. Probes from the final target backwards to
        /// currentIndex+1 and advances waypointChainIndex to the farthest reachable point.
        /// Called on each path recompute cycle (every ~2s) so it doesn't add per-frame cost.
        /// maxSkipIndex: skip-ahead will not advance past this chain index (use chain.Length-2 to
        /// protect a final waypoint that must be arrived at sequentially, e.g. jump pad [7]).
        /// Returns true if the index was advanced.
        /// </summary>
        /// <summary>
        /// Synchronous NodeGraph reachability probe from <paramref name="from"/> to <paramref name="to"/>.
        /// Closes the Arena gate for the duration so open-gate topology does not corrupt the result.
        /// Pass body for hull classification and move speed; from/to are explicit so callers can
        /// probe from any origin (player position, ship origin, etc.).
        /// </summary>
        public bool IsReachable(CharacterBody body, Vector3 from, Vector3 to)
        {
            if (body == null || !SceneInfo.instance) return false;
            NodeGraph groundGraph = SceneInfo.instance.GetNodeGraph(MapNodeGroup.GraphType.Ground);
            if (groundGraph == null) return false;
            bool gateWasOpen = IsArenaGateOpen();
            if (gateWasOpen) SceneInfo.instance.SetGateState("Arena", false);
            var req = new NodeGraph.PathRequest
            {
                startPos = from,
                endPos = to,
                hullClassification = body.hullClassification,
                maxJumpHeight = float.PositiveInfinity,
                maxSpeed = body.moveSpeed,
                path = new Path(groundGraph),
            };
            bool result = groundGraph.ComputePath(req).wasReachable;
            if (gateWasOpen) SceneInfo.instance.SetGateState("Arena", true);
            return result;
        }

        private bool CheckChainSkipAhead(CharacterBody body, Vector3[] chain, int currentIndex, Vector3 finalTarget, int maxSkipIndex = int.MaxValue)
        {
            if (SceneInfo.instance?.GetNodeGraph(MapNodeGroup.GraphType.Ground) == null) return false;

            // cap so we never skip past the caller's protected index
            int skipCap = System.Math.Min(maxSkipIndex, chain.Length - 1);

            // Check final target first — only if skip cap allows exiting chain entirely
            if (maxSkipIndex >= chain.Length && IsReachable(body, body.transform.position, finalTarget))
            {
                Log($"[WAYPOINT] skip-ahead: final target reachable, exiting chain at wp={currentIndex}/{chain.Length - 1}");
                waypointChainIndex = chain.Length;  // exits chain phase → goes to finalTarget
                pathFollower.Reset();
                pathRecomputeTimer = 0f;
                return true;
            }

            // Check chain waypoints from skipCap back to currentIndex+1
            for (int i = skipCap; i > currentIndex; i--)
            {
                if (IsReachable(body, body.transform.position, chain[i]))
                {
                    Log($"[WAYPOINT] skip-ahead: wp={currentIndex} → {i} ({Vector3.Distance(body.transform.position, chain[i]):F1}m to new target)");
                    waypointChainIndex = i;
                    pathFollower.Reset();
                    pathRecomputeTimer = 0f;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Compute a ground NodeGraph A* path from the character to targetPosition.
        /// If requiredGate is set, that gate is opened for the duration of ComputePath and
        /// closed immediately after — the baked waypoint list remains valid even with the gate closed.
        /// Synchronous - path is ready immediately after this call.
        /// </summary>
        private void RecomputePath(CharacterBody body, Vector3 targetPosition, string requiredGate = null)
        {
            if (!SceneInfo.instance) return;

            pathRequest.startPos = body.transform.position;
            pathRequest.endPos = targetPosition;
            pathRequest.hullClassification = body.hullClassification;
            pathRequest.maxJumpHeight = float.PositiveInfinity;
            pathRequest.maxSpeed = body.moveSpeed;

            pathRecomputeTimer = PATH_RECOMPUTE_INTERVAL;
            lastNavTargetPosition = targetPosition;

            NodeGraph groundGraph = SceneInfo.instance.GetNodeGraph(MapNodeGroup.GraphType.Ground);
            if (groundGraph != null)
            {
                // Open the required gate and KEEP IT OPEN for the full navigation session.
                // The gate may control physical geometry (bridge access) that must remain open
                // while the bot physically traverses it — not just during A* computation.
                // ClearFullNavigationState() is responsible for closing it when nav ends.
                if (!string.IsNullOrEmpty(requiredGate))
                {
                    SceneInfo.instance.SetGateState(requiredGate, true);
                    activeGate = requiredGate;
                }

                // If the arena gate is open it corrupts the NodeGraph by making disconnected
                // island nodes appear reachable.  Close it just for the A* call, then reopen.
                bool arenaWasOpen = IsArenaGateOpen();
                if (arenaWasOpen) SceneInfo.instance.SetGateState("Arena", false);

                pathRequest.path = new Path(groundGraph);
                PathTask task = groundGraph.ComputePath(pathRequest);

                if (arenaWasOpen) SceneInfo.instance.SetGateState("Arena", true);

                if (task.wasReachable)
                {
                    lastPathWasReachable = true;
                    pathFollower.SetPath(pathRequest.path);
                    RainflayerPlugin.Instance?.Log($"[PATH] OK: {pathRequest.path.waypointsCount} waypoints → {targetPosition:F0}{(activeGate != null ? $" (gate '{activeGate}' open)" : "")}");
                    return;
                }
            }

            lastPathWasReachable = false;
            RainflayerPlugin.Instance?.Log($"[PATH] UNREACHABLE → {targetPosition:F0}");
        }

        /// <summary>
        /// Handle GOTO navigation.
        /// Returns true if arrived at destination.
        /// </summary>
        public bool HandleGotoNavigation(CharacterBody body)
        {
            if (body == null)
                return false;

            // If the nav target changed since last frame, reset lastPosition so the teleport detector
            // doesn't false-fire on the first frame of the new session.
            if (gotoTarget != lastGotoTarget || gotoInteractable != lastGotoInteractable)
            {
                lastPosition = Vector3.zero;
            }

            // Teleport detection: if the bot moved an impossibly large distance in one fixed frame
            // (e.g. launched by a JumpVolume), cancel navigation so it doesn't try to pathfind back.
            const float TELEPORT_DETECT_DIST = 20f;
            if (lastPosition != Vector3.zero &&
                Vector3.Distance(body.transform.position, lastPosition) > TELEPORT_DETECT_DIST)
            {
                Log($"[GOTO] Teleport detected (moved {Vector3.Distance(body.transform.position, lastPosition):F1}m in one frame) — clearing nav");
                // No special teleport handling needed for ship — arena escape is done via mod teleport.
                ClearFullNavigationState("teleported");
                return false;
            }

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
                // Chain navigation takes priority: consume waypoints in order before final approach.
                // Each hop tries A* first; falls back to direct movement across subgraph gaps.
                Vector3[] chain = gotoInteractable.WaypointChain;
                if (chain != null && chain.Length > 0 && waypointChainIndex < chain.Length)
                    targetPosition = chain[waypointChainIndex];
                // Two-phase navigation: navigate to NavWaypoint (staging area) first,
                // then proceed to the final Position. Used when the target is on a
                // disconnected graph component (e.g. moon2 battery pillars).
                else if (gotoInteractable.NavWaypoint.HasValue && !navWaypointConsumed)
                    targetPosition = gotoInteractable.NavWaypoint.Value;
                else
                    targetPosition = gotoInteractable.Position;
            }
            else
            {
                // No target
                isNavigating = false;
                return false;
            }

            // Arrival check — chain/NavWaypoint phases use different radii than final interact range.
            Vector3[] _chain = gotoInteractable?.WaypointChain;
            bool isChainPhase = _chain != null && _chain.Length > 0 && waypointChainIndex < _chain.Length;
            bool isNavWaypointPhase = !isChainPhase && gotoInteractable != null && gotoInteractable.NavWaypoint.HasValue && !navWaypointConsumed;
            bool isLastChainWaypoint = isChainPhase && waypointChainIndex == _chain.Length - 1;
            float arrivalThreshold = isChainPhase ? (isLastChainWaypoint ? WAYPOINT_CHAIN_FINAL_ARRIVAL : WAYPOINT_CHAIN_ARRIVAL)
                                   : isNavWaypointPhase ? NAV_WAYPOINT_ARRIVAL_THRESHOLD
                                   : INTERACT_DISTANCE_THRESHOLD;
            float distance = Vector3.Distance(body.transform.position, targetPosition);
            if (distance < arrivalThreshold)
            {
                if (isChainPhase)
                {
                    int nextIdx = waypointChainIndex + 1;
                    string nextDesc = nextIdx < _chain.Length
                        ? $"wp[{nextIdx}] {_chain[nextIdx]:F0}"
                        : $"[final] {gotoInteractable?.Position:F0}";
                    Log($"[WAYPOINT] [{waypointChainIndex}] {targetPosition:F0} reached ({distance:F1}m) → {nextDesc}");
                    waypointChainIndex++;
                    pathFollower.Reset();
                    pathRecomputeTimer = 0f;
                    frustration = 0f;
                    stuckTimer = 0f;
                    beeline_steerTimer = 0f;
                    beeline_steerSide = 0;
                    // If a return chain was prepended, clear island tracking once we advance past
                    // all its waypoints — we're back on the main platform heading to the next pillar.
                    // Also lift the return-chain navigation overrides so the forward portion runs normally.
                    if (activeReturnChainLength > 0 && waypointChainIndex >= activeReturnChainLength)
                    {
                        Log($"[WAYPOINT] zone-return complete — back on main platform (advanced past {activeReturnChainLength} return waypoints), clearing island tracking");
                        currentIsland = null;
                        activeReturnChainLength = 0;
                        if (gotoInteractable != null)
                        {
                            gotoInteractable.WaypointChainSkipCap = null;
                            gotoInteractable.WaypointChainBeelineIndices = null;
                        }
                    }
                    return false;
                }
                if (isNavWaypointPhase)
                {
                    Log($"[GOTO] Arrived at NavWaypoint ({distance:F1}m), switching to final target");
                    navWaypointConsumed = true;
                    pathFollower.Reset();
                    pathRecomputeTimer = 0f;
                    frustration = 0f;
                    stuckTimer = 0f;
                    return false;  // Continue navigating to final Position next frame
                }
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
                    Log($"[GOTO] Giving up on unreachable target after {stuckTimer:F1}s (frustration: {frustration:F1})");
                    ClearFullNavigationState("frustrated, giving up");
                    return false;
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
                navWaypointConsumed = false;  // Reset two-phase state for new target
                waypointChainIndex = 0;       // Always start at beginning — chains are winding,
                                              // Euclidean nearest ≠ correct next hop
                if (gotoInteractable?.WaypointChain != null)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[WAYPOINT] New target '{gotoInteractable.Name}': {gotoInteractable.WaypointChain.Length} waypoints");
                    for (int i = 0; i < gotoInteractable.WaypointChain.Length; i++)
                        sb.Append($"\n  [{i}] {gotoInteractable.WaypointChain[i]:F0}");
                    sb.Append($"\n  [final] {gotoInteractable.Position:F0}");
                    Log(sb.ToString());
                }
            }

            // Recompute NodeGraph path when interval elapses or target has drifted significantly
            pathRecomputeTimer -= Time.fixedDeltaTime;
            if (pathRecomputeTimer <= 0f || Vector3.Distance(targetPosition, lastNavTargetPosition) > 3f)
            {
                // Chain skip-ahead: check if we can jump to a later waypoint or the final target.
                // Runs every PATH_RECOMPUTE_INTERVAL seconds so cost is negligible.
                if (isChainPhase && _chain != null && gotoInteractable != null)
                {
                    // For the jump pad chain, never skip the second-to-last waypoint directly to
                    // the last one ([6]→[7]) — the game reports [7] as reachable from [5] when the
                    // arena gate is open, but A* cannot actually route there.  Must arrive at [6]
                    // first and step sequentially to [7].
                    // WaypointChainSkipCap overrides this for chains with known false-positive
                    // reachability (e.g. mass return [5]=275f appears reachable but isn't).
                    int skipCap;
                    if (gotoInteractable.WaypointChainSkipCap.HasValue)
                        skipCap = gotoInteractable.WaypointChainSkipCap.Value;
                    else if (gotoInteractable.Type == "jump_pad")
                        skipCap = _chain.Length - 2;
                    else
                        skipCap = int.MaxValue;
                    if (CheckChainSkipAhead(body, _chain, waypointChainIndex, gotoInteractable.Position, skipCap))
                    {
                        // Index advanced — refresh targetPosition before calling RecomputePath
                        targetPosition = waypointChainIndex < _chain.Length
                            ? _chain[waypointChainIndex]
                            : gotoInteractable.Position;
                    }
                }

                // Force beeline for waypoints that are physically unreachable via A* (false
                // positive reachability).  Skip RecomputePath entirely so the bot moves direct.
                bool forcedBeeline = isChainPhase
                    && gotoInteractable?.WaypointChainBeelineIndices != null
                    && gotoInteractable.WaypointChainBeelineIndices.Contains(waypointChainIndex);
                if (forcedBeeline)
                {
                    pathFollower.Reset();
                    Log($"[WAYPOINT] beeline override at chain index {waypointChainIndex} ({targetPosition:F0}) — skipping A*");
                }
                else
                {
                    string requiredGate = gotoInteractable?.RequiredGate;
                    RecomputePath(body, targetPosition, requiredGate);
                }
            }

            // Navigate via NodeGraph path
            pathFollower.UpdatePosition(body.transform.position);
            Vector3? nextWaypoint = pathFollower.GetNextPosition();

            // === PATH-NODE STALL DETECTION ===
            // PathFollower advances nodes via XZ dist ≤ 2-4m AND Y diff ≤ 2m.  On moon2's
            // sloped terrain the Y check can silently fail, leaving the bot circling the same
            // intermediate graph node indefinitely.  The overall stuck/frustration system
            // doesn't catch this because the bot IS moving (just orbiting).
            // Solution: if the same node has been targeted for PATHNODE_STALL_TIMEOUT seconds
            // while we're already within PATHNODE_STALL_DIST m (XZ), force a path reset.
            if (!pathFollower.isFinished && nextWaypoint.HasValue)
            {
                float nodeDeltaSq = (nextWaypoint.Value - pathNodeStallPos).sqrMagnitude;
                if (nodeDeltaSq < 0.01f)  // same node as last frame
                {
                    float xzDist = new Vector3(
                        body.transform.position.x - nextWaypoint.Value.x, 0f,
                        body.transform.position.z - nextWaypoint.Value.z).magnitude;
                    if (xzDist < PATHNODE_STALL_DIST)
                        pathNodeStallTimer += Time.fixedDeltaTime;
                    else
                        pathNodeStallTimer = 0f;

                    if (pathNodeStallTimer >= PATHNODE_STALL_TIMEOUT)
                    {
                        Log($"[PATH] Node stall {pathNodeStallTimer:F1}s at {nextWaypoint.Value:F0} " +
                            $"({xzDist:F1}m XZ) — forcing path reset");
                        pathFollower.Reset();
                        pathRecomputeTimer = 0f;
                        pathNodeStallTimer = 0f;
                        pathNodeStallPos = Vector3.zero;
                        nextWaypoint = null;  // fall through to beeline this frame
                    }
                }
                else
                {
                    // Advanced to a new node — reset stall tracking
                    pathNodeStallPos = nextWaypoint.Value;
                    pathNodeStallTimer = 0f;
                }
            }
            else
            {
                pathNodeStallTimer = 0f;
            }

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

            // Whether we fell through to the beeline branch (A* unavailable/finished).
            // Chain jump triggers only fire when beelining — A* path jumps are handled
            // by pathFollower.nextWaypointNeedsJump above.
            bool isBeelining = pathFollower.isFinished || !nextWaypoint.HasValue;

            // === OBSTACLE AVOIDANCE ===
            // ApplyJumpDetection raycasts ahead at multiple heights and:
            // - Jumps over climbable obstacles (0.3-3m tall) via CharacterMotor.Jump()
            // - Steers around non-jumpable obstacles by blending in a perpendicular component
            // Chain jump triggers (elevation + gap) only when beelining and not already
            // very close to the waypoint (prevents spam-jumping near the arrival sphere).
            bool passChainTarget = isChainPhase && isBeelining && distance > arrivalThreshold;
            Vector3? chainJumpTarget = passChainTarget ? (Vector3?)targetPosition : null;
            direction = ApplyJumpDetection(body, direction, chainJumpTarget);

            // === SMOOTH DIRECTION CHANGES ===
            // Raw direction snaps instantly when PathFollower advances to the next waypoint,
            // causing visible camera and sprite jolts. Slerp interpolates smoothly instead.
            // In chain beeline phase use faster lerp factors so the bot reacts quickly to
            // waypoint advances and doesn't orbit (circle) around the arrival point.
            float moveLerp = (isChainPhase && isBeelining) ? 0.4f : NAV_MOVE_SMOOTH;
            float aimLerp  = (isChainPhase && isBeelining) ? 0.25f : NAV_AIM_SMOOTH;

            if (smoothedNavMoveDir == Vector3.zero)
                smoothedNavMoveDir = direction;
            smoothedNavMoveDir = Vector3.Slerp(smoothedNavMoveDir, direction, moveLerp);

            if (smoothedNavAimDir == Vector3.zero)
                smoothedNavAimDir = direction;
            smoothedNavAimDir = Vector3.Slerp(smoothedNavAimDir, direction, aimLerp);

            body.inputBank.moveVector = smoothedNavMoveDir;
            body.inputBank.aimDirection = smoothedNavAimDir;

            // Debug: log chain progress every ~2 seconds
            if (isChainPhase && Time.frameCount % 120 == 0)
            {
                Log($"[WAYPOINT] progress wp={waypointChainIndex}/{_chain.Length - 1}  pos={targetPosition:F0}  dist={distance:F1}m  " +
                    $"beeline={isBeelining}  grounded={body.characterMotor?.isGrounded}");
            }

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
            // Close any gate that was held open for this navigation session
            if (!string.IsNullOrEmpty(activeGate) && SceneInfo.instance != null)
            {
                SceneInfo.instance.SetGateState(activeGate, false);
                Log($"[NAV] Closed gate '{activeGate}'");
                activeGate = null;
            }
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
            navWaypointConsumed = false;
            waypointChainIndex = 0;
            pathNodeStallPos = Vector3.zero;
            pathNodeStallTimer = 0f;
            beeline_steerTimer = 0f;
            beeline_steerSide = 0;
            activeReturnChainLength = 0;
            // Reset lastPosition so the teleport detector doesn't false-fire on the first frame
            // of the next navigation session (bot may have moved far while not navigating).
            lastPosition = Vector3.zero;
            // currentIsland is NOT cleared here — it persists after pillar charge so the next
            // FIND_AND_INTERACT:pillar command knows to build the return chain from currentIsland.
            // Cleared by: CancelNavigation (GOTO:cancel), ClearIslandTracking (stage/respawn),
            // or when the bot finishes traversing the return portion of the merged chain.
            Log($"[NAV] Navigation state cleared - {reason}");
        }

        /// <summary>
        /// Full public reset — used by GOTO:cancel to ensure all nav state is wiped.
        /// Also clears island tracking so stale currentIsland doesn't affect the next command.
        /// </summary>
        public void CancelNavigation(string reason)
        {
            ClearFullNavigationState(reason);
            ClearIslandTracking();
        }

        /// <summary>
        /// Reset run-scoped flags that survive individual navigation sessions but must be cleared
        /// when the run resets or the player dies. Call this from ResetAllAIState().
        /// </summary>
        public void ResetRunFlags()
        {
            WasArenaTeleported = false;
            Log("[NAV] Run flags reset (WasArenaTeleported)");
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

                // Check if we're close enough to interact.
                // Use the stored ground-snap position (gotoInteractable.Position) when available —
                // the raw game object transform may be high up in the pillar mesh and unreachable.
                Vector3 interactPos = gotoInteractable?.Position ?? interactionTarget.transform.position;
                distance = Vector3.Distance(body.transform.position, interactPos);
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

                    Vector3 interactApproachPos = gotoInteractable?.Position ?? interactionTarget.transform.position;
                    Vector3 direction = (interactApproachPos - body.transform.position).normalized;
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
                // IInteractable may be on a child OR parent (moon pillar "Activate" uses entity state on root)
                IInteractable interactable = interactionTarget.GetComponent<IInteractable>()
                                          ?? interactionTarget.GetComponentInChildren<IInteractable>()
                                          ?? interactionTarget.GetComponentInParent<IInteractable>();
                // HoldoutZoneController indicates a pillar: press E to activate, then stand in zone to charge
                HoldoutZoneController holdout = interactionTarget.GetComponent<HoldoutZoneController>()
                                             ?? interactionTarget.GetComponentInChildren<HoldoutZoneController>();

                // Check charge completion — pillar only, NOT teleporter.
                // TeleporterInteraction also has a HoldoutZoneController (charge reaches 1.0 after charging),
                // but requires a separate AttemptInteraction call in its ChargedState to trigger stage departure.
                TeleporterInteraction teleporter = interactionTarget.GetComponent<TeleporterInteraction>()
                                                ?? interactionTarget.GetComponentInChildren<TeleporterInteraction>();
                if (holdout != null && holdout.charge >= 1.0f && teleporter == null)
                {
                    lastInteractionSucceeded = true;
                    ClearFullNavigationState($"pillar '{interactionTarget.name}' fully charged");
                    return;
                }

                if (interactor != null && interactable != null)
                {
                    Interactability state = interactable.GetInteractability(interactor);
                    if (state == Interactability.Available)
                    {
                        // Use the proper RoR2 interaction pipeline — goes through PerformInteraction
                        // which handles all IInteractable components and GlobalEventManager callbacks.
                        interactor.AttemptInteraction(interactionTarget);
                        body.inputBank.interact.PushState(true);
                        if (holdout != null)
                        {
                            // Pillar: press-E done, now stand in zone until charge >= 1.0
                            // Don't clear state — holdout.charge check above fires success next frames
                            Log($"[INTERACT] Pillar '{interactionTarget.name}' activated, standing in zone ({holdout.charge * 100f:F0}%)");
                            body.inputBank.moveVector = Vector3.zero;
                        }
                        else
                        {
                            // Regular interactable (chest, shrine, teleporter) — done on press
                            lastInteractionSucceeded = true;
                            ClearFullNavigationState($"interaction activated on {interactionTarget.name}");
                        }
                    }
                    else if (holdout != null)
                    {
                        // Pillar already activated (IInteractable no longer Available) — stand in zone.
                        // If charge hasn't started, press E again via InputBank in case the component
                        // search found a stale/wrong IInteractable.
                        body.inputBank.moveVector = Vector3.zero;
                        if (holdout.charge < 0.01f && Time.frameCount % 30 == 0)
                            body.inputBank.interact.PushState(true);
                        if (Time.frameCount % 60 == 0)
                            Log($"[INTERACT] Pillar '{interactionTarget.name}' charging: {holdout.charge * 100f:F0}%");
                    }
                    else
                    {
                        // Not interactable anymore (chest already opened, shrine used, etc.)
                        ClearFullNavigationState($"target no longer interactable (state: {state})");
                    }
                }
                else if (holdout != null)
                {
                    // IInteractable not found on object/children/parents — press E via InputBank.
                    // This handles entity-state-based "Activate Pillar" interactions where the
                    // IInteractable is implemented by the entity state, not a standalone component.
                    body.inputBank.moveVector = Vector3.zero;
                    if (Time.frameCount % 30 == 0)
                        body.inputBank.interact.PushState(true);
                    if (Time.frameCount % 60 == 0)
                        Log($"[INTERACT] Pillar '{interactionTarget.name}' pressing E via InputBank (charge: {holdout.charge * 100f:F0}%)");
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

        public void ClearInteractionTarget(bool clearZoneReturn = false)
        {
            interactionTarget = null;
            interactionHoldTimer = 0f;
            interactSettleTimer = 0f;
            // Also clear nav state so the AI doesn't get stuck in an arrived-but-nothing-to-do loop
            isNavigating = false;
            gotoTarget = null;
            gotoInteractable = null;
            // zoneReturnChain is preserved by default so a combat interrupt mid-pillar-charge
            // doesn't lose the return path needed for the next FIND_AND_INTERACT:pillar command.
            // Only clear on a true full reset (stage change, death) via clearZoneReturn=true.
            if (clearZoneReturn)
                ClearZoneReturn();
        }

        /// <summary>
        /// Log player position + NodeGraph reachability from ship origin every ~2.5s.
        /// Only active when RainflayerPlugin.RecordWaypoints is true.
        /// The last reachable=True position before it flips False = subgraph boundary.
        /// Copy [WP] lines from LogOutput.log into Moon2PillarChains.
        /// </summary>
        public void TickWaypointRecorder(CharacterBody body)
        {
            if (body == null) return;

            // Capture drop-zone as ship origin on first call (safe — only called after drop pod exit)
            if (!waypointOriginCaptured)
            {
                waypointShipOrigin = body.transform.position;
                waypointOriginCaptured = true;
                RainflayerPlugin.Instance?.Log(
                    $"[WP] Origin captured: ({waypointShipOrigin.x:F0}, {waypointShipOrigin.y:F0}, {waypointShipOrigin.z:F0})");
            }

            waypointRecordTimer += Time.fixedDeltaTime;
            if (waypointRecordTimer < WAYPOINT_RECORD_INTERVAL) return;
            waypointRecordTimer = 0f;

            Vector3 pos = body.transform.position;
            bool reachable = IsReachable(body, waypointShipOrigin, pos);

            RainflayerPlugin.Instance?.Log(
                $"[WP] pos=({pos.x:F0}, {pos.y:F0}, {pos.z:F0}) reachable={reachable}");
        }

        /// <summary>
        /// Reset recorder state on new stage so ship origin is re-captured at the new drop zone.
        /// </summary>
        public void ResetWaypointRecorder()
        {
            waypointOriginCaptured = false;
            waypointRecordTimer = 0f;
        }

        // Helper methods

        /// <summary>
        /// Returns true when all moon batteries are charged and the arena gate has opened.
        /// After this point the NodeGraph reports previously-disconnected nodes as reachable,
        /// so A* paths and skip-ahead checks are unreliable and must be disabled.
        /// </summary>
        private static bool IsArenaGateOpen()
        {
            var ctrl = MoonBatteryMissionController.instance;
            return ctrl != null && ctrl.numChargedBatteries >= ctrl.numRequiredBatteries;
        }

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
