using RoR2;
using RoR2.CharacterAI;
using UnityEngine;

namespace Rainflayer
{
    /// <summary>
    /// Handles combat logic for AIController.
    /// Manages targeting, movement, skills, and camera control during combat.
    /// </summary>
    public class CombatController
    {
        private readonly AIController controller;
        private readonly EntityDetector entityDetector;

        // Survivor types with specific skill handling
        private enum SurvivorType
        {
            Default,
            Huntress,   // Arrow Rain / Ballista: 2-stage special (confirm with primary)
            Toolbot,    // Mul-T: Special (Retool) just switches weapon — never auto-use
            Engineer,   // Turret placement: 2-stage special (confirm with primary held)
            Captain,    // Orbital Strike / Supply Drop: 2-stage utility (confirm with primary)
        }

        // Combat state
        private string currentStrategy = "balanced";
        private GameObject currentTarget;
        private bool isHuntress = false;       // Can sprint while shooting
        private SurvivorType currentSurvivor = SurvivorType.Default;
        private bool characterDetected = false; // true once body name has been resolved

        // Aim-confirm pulse state -----------------------------------------------
        // After activating a 2-stage skill, EntityStates read InputBank directly
        // (e.g. PlaceTurret checks skill4.justPressed, BaseArrowBarrage checks
        // skill1.justPressed || skill4.justPressed).  We pulse the *same* button that
        // activated the skill each FixedUpdate so the EntityState receives the justPressed
        // events it waits for — matching the approach used by PlayerBots / AutoPlay.
        private bool aimConfirmActive = false;
        private float aimConfirmTimer = 0f;
        private bool aimConfirmPulse = false;   // toggled each frame for justPressed events
        private int aimConfirmSkillIndex = 4;   // 1=skill1(primary), 2, 3, 4
        private const float AIM_CONFIRM_DEFAULT_DURATION = 2.5f;

        // Which slot indices (1-4) require an aim-confirm pulse after activation,
        // keyed by survivor type.  The pulse uses the *same* slot button that was
        // pressed, which is what the relevant EntityStates check.
        //   Huntress special  (4): BaseArrowBarrage checks skill1.justPressed || skill4.justPressed
        //   Engineer secondary(2): missile painter needs confirm on skill2
        //   Engineer special  (4): PlaceTurret checks skill1.down || skill4.justPressed
        //   Captain utility   (3): SetupAirstrike / SetupSupplyDrop check skill3
        private static readonly System.Collections.Generic.Dictionary<SurvivorType, System.Collections.Generic.HashSet<int>>
            AimConfirmSlots = new System.Collections.Generic.Dictionary<SurvivorType, System.Collections.Generic.HashSet<int>>
        {
            { SurvivorType.Huntress, new System.Collections.Generic.HashSet<int> { 4 } },
            { SurvivorType.Engineer, new System.Collections.Generic.HashSet<int> { 2, 4 } },
            { SurvivorType.Captain,  new System.Collections.Generic.HashSet<int> { 3 } },
        };

        // Aim smoothing
        private Vector3 smoothedAimDirection;
        private const float AIM_SMOOTH_FACTOR = 0.12f; // Lower = smoother, higher = faster

        // Sprint blocking system (from RTAutoSprintEx)
        private System.Collections.Generic.HashSet<string> sprintDisableList = new System.Collections.Generic.HashSet<string>();
        private System.Collections.Generic.HashSet<string> sprintDelayList = new System.Collections.Generic.HashSet<string>();
        private float sprintDelayTimer = 0f;

        // Roam waypoints with exploration tracking
        private Vector3? roamWaypoint = null;
        private const float ROAM_WAYPOINT_DISTANCE = 30f; // How far to explore before picking new waypoint
        private const float ROAM_SPEED = 0.7f; // Walking speed while roaming

        // Explored area tracking (prefer unexplored areas)
        private System.Collections.Generic.List<Vector3> visitedPositions = new System.Collections.Generic.List<Vector3>();
        private const float VISITED_POSITIONS_GRID_SIZE = 10f; // Record position every 10m
        private Vector3 lastRecordedPosition = Vector3.zero;

        // Camera controller
        private AICameraController cameraController;
        private bool cameraInitialized = false;

        public CombatController(AIController controller, EntityDetector entityDetector)
        {
            this.controller = controller;
            this.entityDetector = entityDetector;
            InitializeSprintBlockingLists();
        }

        /// <summary>
        /// Initialize sprint blocking lists based on RTAutoSprintEx mod.
        /// These EntityStates block sprinting either completely or with a delay.
        /// </summary>
        private void InitializeSprintBlockingLists()
        {
            // SPRINT DISABLE LIST - These EntityStates completely block sprinting
            // MUL-T
            sprintDisableList.Add("EntityStates.Toolbot.ToolbotDualWield");
            sprintDisableList.Add("EntityStates.Toolbot.ToolbotDualWieldBase");
            sprintDisableList.Add("EntityStates.Toolbot.ToolbotDualWieldStart");
            sprintDisableList.Add("EntityStates.Toolbot.FireNailgun");
            sprintDisableList.Add("EntityStates.Toolbot.AimStunDrone");

            // Artificer
            sprintDisableList.Add("EntityStates.Mage.Weapon.Flamethrower");
            sprintDisableList.Add("EntityStates.Mage.Weapon.PrepWall");

            // Bandit
            sprintDisableList.Add("EntityStates.Bandit2.Weapon.BasePrepSidearmRevolverState");
            sprintDisableList.Add("EntityStates.Bandit2.Weapon.PrepSidearmResetRevolver");
            sprintDisableList.Add("EntityStates.Bandit2.Weapon.PrepSidearmSkullRevolver");

            // Engineer
            sprintDisableList.Add("EntityStates.Engi.EngiMissilePainter.Paint");

            // Rex
            sprintDisableList.Add("EntityStates.Treebot.Weapon.AimMortar");
            sprintDisableList.Add("EntityStates.Treebot.Weapon.AimMortar2");
            sprintDisableList.Add("EntityStates.Treebot.Weapon.AimMortarRain");

            // Captain
            sprintDisableList.Add("EntityStates.Captain.Weapon.SetupAirstrike");
            sprintDisableList.Add("EntityStates.Captain.Weapon.SetupAirstrikeAlt");
            sprintDisableList.Add("EntityStates.Captain.Weapon.SetupSupplyDrop");

            // Railgunner (scopes)
            sprintDisableList.Add("EntityStates.Railgunner.Scope.WindUpScopeLight");
            sprintDisableList.Add("EntityStates.Railgunner.Scope.ActiveScopeLight");
            sprintDisableList.Add("EntityStates.Railgunner.Scope.WindUpScopeHeavy");
            sprintDisableList.Add("EntityStates.Railgunner.Scope.ActiveScopeHeavy");

            // Void Survivor
            sprintDisableList.Add("EntityStates.VoidSurvivor.Weapon.FireCorruptHandBeam");

            // SPRINT DELAY LIST - These have short delays before sprinting resumes
            // Artificer
            sprintDelayList.Add("EntityStates.Mage.Weapon.FireFireBolt");
            sprintDelayList.Add("EntityStates.Mage.Weapon.FireLaserbolt");

            // Bandit
            sprintDelayList.Add("EntityStates.Bandit2.Weapon.Bandit2FirePrimaryBase");
            sprintDelayList.Add("EntityStates.Bandit2.Weapon.FireShotgun2");
            sprintDelayList.Add("EntityStates.Bandit2.Weapon.Bandit2FireRifle");

            // Engineer
            sprintDelayList.Add("EntityStates.Engi.EngiWeapon.FireMines");
            sprintDelayList.Add("EntityStates.Engi.EngiWeapon.FireSeekerGrenades");

            // MUL-T
            sprintDelayList.Add("EntityStates.Toolbot.FireGrenadeLauncher");

            // Rex
            sprintDelayList.Add("EntityStates.Treebot.Weapon.FireSyringe");

            // Commando
            sprintDelayList.Add("EntityStates.Commando.CommandoWeapon.FirePistol2");

            // Loader
            sprintDelayList.Add("EntityStates.Loader.SwingComboFist");

            // Acrid
            sprintDelayList.Add("EntityStates.Croco.Slash");

            // Void Survivor
            sprintDelayList.Add("EntityStates.VoidSurvivor.Weapon.FireHandBeam");
            sprintDelayList.Add("EntityStates.VoidSurvivor.Weapon.ChargeCorruptHandBeam");

            // Railgunner
            sprintDelayList.Add("EntityStates.Railgunner.Weapon.FirePistol");
        }

        /// <summary>
        /// Reset all combat state (called on new run or respawn).
        /// Clears camera references so ControlCamera() re-initializes for the new scene.
        /// </summary>
        public void Reset()
        {
            // Disable the old camera override before releasing the reference
            if (cameraController != null)
            {
                try { cameraController.Disable(); } catch { }
                cameraController = null;
            }
            cameraInitialized = false;

            // Reset aim smoothing so it re-initializes from the new body's direction
            smoothedAimDirection = Vector3.zero;

            // Cancel any in-progress aim-confirm sequence
            aimConfirmActive = false;
            aimConfirmTimer = 0f;
            aimConfirmPulse = false;

            // Force re-detection on next update (body changes between runs)
            characterDetected = false;

            Log("[RESET] CombatController state cleared (camera + aim smoothing)");
        }

        public void SetStrategy(string strategy)
        {
            currentStrategy = strategy.ToLower();
            Log($"Strategy set to: {currentStrategy}");

            // Apply strategy-specific settings
            ApplyStrategy();
        }

        public void DetectCharacterClass()
        {
            CharacterBody body = RainflayerPlugin.GetPlayerBody();
            if (body == null || body.bodyIndex == BodyIndex.None)
                return; // body not ready yet — will retry next frame via lazy check

            string bodyName = BodyCatalog.GetBodyName(body.bodyIndex)?.ToLower() ?? "";
            if (string.IsNullOrEmpty(bodyName))
                return;

            // Sprint-while-shooting characters (can keep sprinting during primary fire)
            isHuntress = bodyName.Contains("huntress") || bodyName.Contains("mage") || bodyName.Contains("merc");

            // Survivor type — drives aim-confirm config and any suppressed skills
            if (bodyName.Contains("huntress"))
                currentSurvivor = SurvivorType.Huntress;
            else if (bodyName.Contains("toolbot"))
                currentSurvivor = SurvivorType.Toolbot;
            else if (bodyName.Contains("engi"))
                currentSurvivor = SurvivorType.Engineer;
            else if (bodyName.Contains("captain"))
                currentSurvivor = SurvivorType.Captain;
            else
                currentSurvivor = SurvivorType.Default;

            characterDetected = true;
            Log($"[CHARACTER] Detected: {bodyName}, Survivor: {currentSurvivor}, Sprint-while-shooting: {isHuntress}");
        }

        /// <summary>
        /// Apply strategy-specific AI settings.
        /// </summary>
        private void ApplyStrategy()
        {
            BaseAI ai = RainflayerPlugin.PlayerAI;

            if (ai == null)
                return;

            switch (currentStrategy)
            {
                case "aggressive":
                    ai.aimVectorDampTime = 0.08f;    // Much slower than before (was 0.005f - aimbot)
                    ai.aimVectorMaxSpeed = 180f;     // Reduced max speed (was 360f)
                    ai.enemyAttentionDuration = 10f; // Focus on enemies longer
                    break;

                case "defensive":
                    ai.aimVectorDampTime = 0.15f;    // Even slower for defensive (was 0.02f)
                    ai.aimVectorMaxSpeed = 120f;     // Slower speed (was 90f but smoother now)
                    ai.enemyAttentionDuration = 3f;  // Switch targets more cautiously
                    break;

                case "support":
                    ai.aimVectorDampTime = 0.1f;     // Moderate speed (was 0.01f)
                    ai.aimVectorMaxSpeed = 150f;     // Moderate speed (was 180f)
                    ai.enemyAttentionDuration = 5f;
                    break;

                default: // balanced
                    ai.aimVectorDampTime = 0.1f;     // Moderate (was 0.01f - too fast)
                    ai.aimVectorMaxSpeed = 150f;     // Moderate speed (was 180f)
                    ai.enemyAttentionDuration = 5f;
                    break;
            }
        }

        /// <summary>
        /// Determine if AI should sprint based on character, EntityState, and situation.
        /// Based on RTAutoSprintEx logic - always sprint when allowed.
        /// </summary>
        public bool ShouldSprint(CharacterBody body)
        {
            if (body == null || body.inputBank == null)
                return false;

            // Wait for sprint delay timer (e.g., after firing certain skills)
            if (sprintDelayTimer > 0f)
            {
                sprintDelayTimer -= Time.fixedDeltaTime;
                return false; // Wait for delay to pass
            }

            // Check EntityState blocking (from RTAutoSprintEx)
            if (IsSprintBlockedByEntityState(body))
                return false;

            // Huntress/Artificer/Mercenary can sprint while shooting
            if (isHuntress)
                return true;

            // Check aim/move angle - don't sprint if moving too far from aim direction
            // This prevents unnecessary sprinting when making small adjustments
            Vector3 aimDirection = body.inputBank.aimDirection;
            aimDirection.y = 0f;
            aimDirection.Normalize();
            Vector3 moveVector = body.inputBank.moveVector;
            moveVector.y = 0f;
            if (moveVector != Vector3.zero)
            {
                moveVector.Normalize();
                float dot = Vector3.Dot(aimDirection, moveVector);
                // Sprint requires movement to be somewhat aligned with aim (dot > 0.3)
                if (dot < 0.3f)
                    return false;
            }

            // Default: Sprint when not blocked by EntityState
            return true;
        }

        /// <summary>
        /// Check if the current EntityState blocks sprinting.
        /// Also sets sprint delay timer for delayed skills.
        /// </summary>
        private bool IsSprintBlockedByEntityState(CharacterBody body)
        {
            if (body == null)
                return false;

            // Get all EntityStates on this body
            EntityStateMachine[] stateMachines = body.GetComponents<EntityStateMachine>();
            bool isBlocked = false;
            bool hasDelay = false;

            foreach (EntityStateMachine machine in stateMachines)
            {
                if (machine.state == null)
                    continue;

                string stateName = machine.state.ToString();

                // Check if this state blocks sprinting completely
                if (sprintDisableList.Contains(stateName))
                {
                    isBlocked = true;
                }

                // Check if this state has a sprint delay
                if (sprintDelayList.Contains(stateName))
                {
                    hasDelay = true;
                    // Set delay timer (0.15s default like RTAutoSprintEx)
                    sprintDelayTimer = 0.15f;
                }
            }

            return isBlocked;
        }

        /// <summary>
        /// Apply movement control during combat.
        /// </summary>
        public void ApplyMovementControl(CharacterBody body, Vector3 directionToTarget, float distanceToTarget)
        {
            // Defensive null check
            if (body == null || body.inputBank == null)
                return;

            // ALWAYS KEEP MOVING - kiting and strafing for survival
            Vector3 moveDirection;

            // Optimal combat range based on strategy
            float optimalRange = GetOptimalRange();

            if (distanceToTarget > optimalRange + 5f)
            {
                // Too far - move towards target
                moveDirection = directionToTarget;
                moveDirection.y = 0; // Keep on ground plane
                moveDirection = controller.GetNavigationController().ApplyJumpDetection(body, moveDirection);
                body.inputBank.moveVector = moveDirection.normalized;
            }
            else if (distanceToTarget < optimalRange - 5f)
            {
                // Too close - back away while kiting (all strategies)
                moveDirection = -directionToTarget;
                moveDirection.y = 0;

                // Add perpendicular component for kiting movement
                Vector3 strafeDirection = Vector3.Cross(directionToTarget, Vector3.up);
                if (Time.frameCount % 120 < 60)
                    strafeDirection = -strafeDirection;

                moveDirection += strafeDirection * 0.5f; // Mix retreat + strafe
                moveDirection = controller.GetNavigationController().ApplyJumpDetection(body, moveDirection);
                body.inputBank.moveVector = moveDirection.normalized;
            }
            else
            {
                // In optimal range - ALWAYS STRAFE (never stand still)
                Vector3 strafeDirection = Vector3.Cross(directionToTarget, Vector3.up);

                // Alternate strafe direction every 2 seconds
                if (Time.frameCount % 120 < 60)
                    strafeDirection = -strafeDirection;

                // Add slight forward/backward component based on strategy
                if (currentStrategy == "aggressive")
                {
                    // Aggressive: strafe with slight forward pressure
                    moveDirection = strafeDirection + directionToTarget * 0.3f;
                    moveDirection = controller.GetNavigationController().ApplyJumpDetection(body, moveDirection);
                    body.inputBank.moveVector = moveDirection.normalized * 0.85f;
                }
                else if (currentStrategy == "defensive")
                {
                    // Defensive: strafe with slight backward pressure
                    moveDirection = strafeDirection - directionToTarget * 0.3f;
                    moveDirection = controller.GetNavigationController().ApplyJumpDetection(body, moveDirection);
                    body.inputBank.moveVector = moveDirection.normalized * 0.75f;
                }
                else
                {
                    // Balanced: pure strafing
                    moveDirection = controller.GetNavigationController().ApplyJumpDetection(body, strafeDirection);
                    body.inputBank.moveVector = moveDirection.normalized * 0.8f;
                }
            }
        }

        /// <summary>
        /// Apply aim control during combat.
        /// </summary>
        public void ApplyAimControl(CharacterBody body, Vector3 directionToTarget, Vector3 targetPos, Vector3 playerPos)
        {
            // Defensive null check
            if (body == null || body.inputBank == null)
                return;

            // Compute aim from the actual bullet origin (aimOriginTransform = head/eye height) to the target.
            // Using body.transform.position (capsule center, ~1m lower) causes overshooting on elevated targets
            // because the bullet fires from a higher point than the direction was calculated from.
            // CameraTargetParams.Awake() confirms aimOriginTransform is the canonical aim pivot in RoR2.
            Vector3 aimOrigin = body.aimOriginTransform ? body.aimOriginTransform.position : playerPos;
            Vector3 desiredAimDirection = (targetPos - aimOrigin).normalized;

            // NOTE: No vertical lead applied. Commando's primary is hitscan (no gravity drop),
            // and for other characters the error from adding a fixed lead exceeds the benefit.
            // The old +0.1 vertical bias was compounding the aim-too-high problem, not fixing it.

            // Initialize smoothed aim on first frame
            if (smoothedAimDirection == Vector3.zero)
                smoothedAimDirection = body.inputBank.aimDirection;

            // SMOOTH AIM TRANSITION: Prevents instant snap/aimbot behavior
            smoothedAimDirection = Vector3.Slerp(smoothedAimDirection, desiredAimDirection, AIM_SMOOTH_FACTOR);

            // Write smoothed aim to InputBank (bypasses mouse!)
            body.inputBank.aimDirection = smoothedAimDirection;

            // Control camera to match smoothed aim direction
            ControlCamera(body, smoothedAimDirection);
        }

        /// <summary>
        /// Apply skill control during combat.
        /// Routes to survivor-specific logic and handles 2-stage aim-confirm sequences.
        /// </summary>
        public void ApplySkillControl(CharacterBody body, float distanceToTarget)
        {
            SkillLocator skills = body.skillLocator;
            if (skills == null) return;

            // Lazy character detection — retry each frame until body name resolves
            if (!characterDetected)
                DetectCharacterClass();

            // --- Aim-confirm pulse block ---
            // After activating a 2-stage skill we pulse the *same* InputBank button that
            // triggered it.  EntityStates like PlaceTurret and BaseArrowBarrage read
            // InputBank directly, so this is the only way to send them the confirmation.
            // Pulsing the same slot button is exactly what PlayerBots / AutoPlay do via
            // activationRequiresAimConfirmation + buttonPressType = TapContinuous.
            if (aimConfirmActive)
            {
                aimConfirmTimer -= Time.fixedDeltaTime;

                if (aimConfirmTimer <= 0f)
                {
                    aimConfirmActive = false;
                    PushAimConfirmButton(body, false);
                    return;
                }

                // Alternate true/false each frame so the EntityState sees a fresh
                // justPressed event every other tick rather than a held-down button.
                aimConfirmPulse = !aimConfirmPulse;
                PushAimConfirmButton(body, aimConfirmPulse);
                return; // skip normal skill logic while confirming
            }

            // --- Per-survivor skill dispatch ---
            if (currentSurvivor == SurvivorType.Toolbot)
                ApplyToolbotSkills(body, skills, distanceToTarget);
            else
                ApplyDefaultSkills(body, skills, distanceToTarget);
        }

        // -----------------------------------------------------------------------
        // Survivor-specific skill methods
        // -----------------------------------------------------------------------

        /// <summary>
        /// Mul-T: Special (Retool) only switches weapon stances — never auto-use it.
        /// All other skills fire normally with aim-confirm where configured.
        /// </summary>
        private void ApplyToolbotSkills(CharacterBody body, SkillLocator skills, float distanceToTarget)
        {
            (float primary, float secondary, float utility, _) = GetStrategyRanges();

            UseSkillIfReady(body, skills.primary,   1, distanceToTarget, primary);
            UseSkillIfReady(body, skills.secondary, 2, distanceToTarget, secondary);
            UseSkillIfReady(body, skills.utility,   3, distanceToTarget, utility);
            // Special (Retool) intentionally omitted
        }

        /// <summary>
        /// Default skill rotation used by all survivors except Toolbot.
        /// Aim-confirm is applied automatically for slots listed in AimConfirmSlots
        /// for the current survivor (e.g. slot 4 for Huntress special, slots 2+4 for
        /// Engineer secondary/special, slot 3 for Captain utility).
        /// </summary>
        private void ApplyDefaultSkills(CharacterBody body, SkillLocator skills, float distanceToTarget)
        {
            (float primary, float secondary, float utility, float special) = GetStrategyRanges();

            UseSkillIfReady(body, skills.primary,   1, distanceToTarget, primary);
            UseSkillIfReady(body, skills.secondary, 2, distanceToTarget, secondary);
            UseSkillIfReady(body, skills.utility,   3, distanceToTarget, utility);
            UseSkillIfReady(body, skills.special,   4, distanceToTarget, special);
        }

        // -----------------------------------------------------------------------
        // Aim-confirm helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Execute a skill if in range and ready.  If the current survivor has this
        /// slot listed in AimConfirmSlots, immediately start pulsing that same button
        /// so 2-stage EntityStates receive the justPressed events they need.
        /// </summary>
        private void UseSkillIfReady(CharacterBody body, GenericSkill skill, int slotIndex,
                                     float distanceToTarget, float maxRange)
        {
            if (skill == null || !skill.IsReady() || distanceToTarget > maxRange) return;

            skill.ExecuteIfReady();

            // Trigger aim-confirm for slots that need it on this survivor
            if (AimConfirmSlots.TryGetValue(currentSurvivor, out var slots) && slots.Contains(slotIndex))
                TriggerAimConfirm(slotIndex);
        }

        /// <summary>
        /// Start pulsing InputBank button <paramref name="skillIndex"/> for up to
        /// <paramref name="duration"/> seconds.  Alternating true/false generates a
        /// fresh justPressed event every cycle — matching TapContinuous behaviour.
        /// </summary>
        private void TriggerAimConfirm(int skillIndex, float duration = AIM_CONFIRM_DEFAULT_DURATION)
        {
            aimConfirmActive = true;
            aimConfirmTimer = duration;
            aimConfirmSkillIndex = skillIndex;
            aimConfirmPulse = false; // flips to true on first pulse frame
        }

        private void PushAimConfirmButton(CharacterBody body, bool state)
        {
            if (body.inputBank == null) return;
            switch (aimConfirmSkillIndex)
            {
                case 1: body.inputBank.skill1.PushState(state); break;
                case 2: body.inputBank.skill2.PushState(state); break;
                case 3: body.inputBank.skill3.PushState(state); break;
                case 4: body.inputBank.skill4.PushState(state); break;
            }
        }

        /// <summary>
        /// Returns (primary, secondary, utility, special) distance ranges for the current strategy.
        /// </summary>
        private (float primary, float secondary, float utility, float special) GetStrategyRanges()
        {
            switch (currentStrategy)
            {
                case "aggressive": return (50f, 30f, 20f, 40f);
                case "defensive":  return (30f, 35f, float.MaxValue, 30f);
                default:           return (40f, 25f, 20f, 25f); // balanced
            }
        }

        /// <summary>
        /// Apply idle behavior when no target is available.
        /// </summary>
        public void ApplyIdleBehavior(CharacterBody body)
        {
            // EXPLORATION-BASED ROAMING: Track visited positions and prefer unexplored areas
            Vector3 playerPos = body.transform.position;

            // Track visited positions (record every 10m of movement)
            if (Vector3.Distance(playerPos, lastRecordedPosition) > VISITED_POSITIONS_GRID_SIZE)
            {
                visitedPositions.Add(playerPos);
                lastRecordedPosition = playerPos;

                // Keep only recent positions (last 50) to avoid memory issues
                if (visitedPositions.Count > 50)
                    visitedPositions.RemoveAt(0);
            }

            // Pick new waypoint if needed
            if (!roamWaypoint.HasValue || Vector3.Distance(playerPos, roamWaypoint.Value) < 5f)
            {
                // Try multiple random directions and pick one furthest from visited areas
                Vector3 bestWaypoint = Vector3.zero;
                float bestScore = -1f;

                // Sample 8 directions around a circle
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * 45f; // 0, 45, 90, 135, 180, 225, 270, 315 degrees
                    float distance = UnityEngine.Random.Range(25f, 45f); // Explore further

                    Vector3 direction = new Vector3(
                        Mathf.Cos(angle * Mathf.Deg2Rad),
                        0f,
                        Mathf.Sin(angle * Mathf.Deg2Rad)
                    );

                    Vector3 candidateWaypoint = playerPos + direction * distance;

                    // Check if waypoint is reachable (has ground)
                    Vector3 groundCheck = candidateWaypoint + Vector3.up * 10f;
                    RaycastHit hit;
                    if (Physics.Raycast(groundCheck, Vector3.down, out hit, 20f, LayerMask.GetMask("World")))
                    {
                        candidateWaypoint = hit.point;

                        // Score this waypoint based on distance from visited positions
                        float minDistanceToVisited = float.MaxValue;
                        foreach (Vector3 visited in visitedPositions)
                        {
                            float dist = Vector3.Distance(candidateWaypoint, visited);
                            if (dist < minDistanceToVisited)
                                minDistanceToVisited = dist;
                        }

                        // If no visited positions yet, score is distance from current position
                        if (visitedPositions.Count == 0)
                            minDistanceToVisited = distance;

                        // Add some randomness so we don't always go in perfect directions
                        float score = minDistanceToVisited + UnityEngine.Random.Range(-5f, 5f);

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestWaypoint = candidateWaypoint;
                        }
                    }
                }

                // If we found a good waypoint, use it
                if (bestWaypoint != Vector3.zero)
                {
                    roamWaypoint = bestWaypoint;
                    if (Time.frameCount % 120 == 0) // Log every 2 seconds
                    {
                        Log($"[ROAM] New waypoint: exploring unexplored area (score: {bestScore:F1}m from visited)");
                    }
                }
                else
                {
                    // Fallback: pick random closer waypoint
                    float randomAngle = UnityEngine.Random.Range(0f, 360f);
                    Vector3 randomDirection = new Vector3(
                        Mathf.Cos(randomAngle * Mathf.Deg2Rad),
                        0f,
                        Mathf.Sin(randomAngle * Mathf.Deg2Rad)
                    );
                    roamWaypoint = playerPos + randomDirection * 20f;
                }
            }

            // Calculate direction to waypoint
            Vector3 directionToWaypoint = (roamWaypoint.Value - playerPos);
            directionToWaypoint.y = 0; // Keep horizontal
            float distanceToWaypoint = directionToWaypoint.magnitude;

            // Check for immediate obstacles
            Vector3 eyeLevel = playerPos + Vector3.up * 1.5f;
            bool obstacleAhead = Physics.Raycast(eyeLevel, directionToWaypoint.normalized, 3f, LayerMask.GetMask("World"));

            // Check for cliffs
            Vector3 groundCheckPos = playerPos + directionToWaypoint.normalized * 3f;
            bool groundAhead = Physics.Raycast(groundCheckPos + Vector3.up * 2f, Vector3.down, 5f, LayerMask.GetMask("World"));

            // If obstacle or cliff, pick new waypoint
            if (obstacleAhead || !groundAhead)
            {
                roamWaypoint = null; // Force new waypoint selection next frame
            }
            else if (distanceToWaypoint > 1f)
            {
                // Apply jump detection to navigate over obstacles
                Vector3 moveDirection = controller.GetNavigationController().ApplyJumpDetection(body, directionToWaypoint.normalized);

                // Move toward waypoint
                body.inputBank.moveVector = moveDirection * ROAM_SPEED;

                // Smooth aim toward waypoint
                if (smoothedAimDirection == Vector3.zero)
                    smoothedAimDirection = body.inputBank.aimDirection;

                smoothedAimDirection = Vector3.Slerp(smoothedAimDirection, directionToWaypoint.normalized, 0.02f);
                body.inputBank.aimDirection = smoothedAimDirection;
            }
            else
            {
                // Reached waypoint - will pick new one next frame
                body.inputBank.moveVector = Vector3.zero;
            }
        }

        /// <summary>
        /// Control camera to match aim direction.
        /// </summary>
        private void ControlCamera(CharacterBody body, Vector3 aimDirection)
        {
            // Use ICameraStateProvider to take full camera control (PhotoMode approach)
            if (!cameraInitialized)
            {
                // Find camera rig for this body
                foreach (CameraRigController cameraRig in CameraRigController.readOnlyInstancesList)
                {
                    if (cameraRig.target == body.gameObject)
                    {
                        // Create camera controller component
                        cameraController = cameraRig.gameObject.AddComponent<AICameraController>();
                        cameraController.Initialize(cameraRig, body);
                        cameraInitialized = true;

                        Log("[Camera] Initialized AI camera controller");
                        break;
                    }
                }
            }

            // Update aim direction for camera
            if (cameraController != null)
            {
                cameraController.SetAimDirection(aimDirection);
            }
        }

        /// <summary>
        /// Get optimal combat range based on strategy.
        /// </summary>
        private float GetOptimalRange()
        {
            switch (currentStrategy)
            {
                case "aggressive": return 15f;
                case "defensive": return 25f;
                default: return 20f;
            }
        }

        // Helper methods
        private void Log(string message)
        {
            RainflayerPlugin.Instance?.LogDebug($"[CombatController] {message}");
        }
    }
}
