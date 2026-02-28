using BepInEx;
using BepInEx.Configuration;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rainflayer
{
    /// <summary>
    /// Main BepInEx plugin for Rainflayer — RoR2 AI brain integration.
    /// Enables AI to control a player character via BaseAI and external commands.
    /// </summary>
    [BepInPlugin("justindwang.rainflayer", "Rainflayer", "1.0.0")]
    public class RainflayerPlugin : BaseUnityPlugin
    {
        // Static references for easy access
        public static RainflayerPlugin Instance { get; private set; }
        public static CharacterMaster LocalPlayerMaster { get; private set; }
        public static BaseAI PlayerAI { get; private set; }
        public static GameObject CustomTargetObject { get; private set; }

        // Configuration
        public static ConfigEntry<bool> EnableAIControl { get; private set; }
        public static ConfigEntry<bool> DebugMode { get; private set; }

        // Components
        public AIController aiController;  // Made public for SocketBridge access
        private EntityDetector entityDetector;

        // Flag to retry EntityDetector reference update when body becomes available.
        // Set to true on new run/death; cleared once update succeeds.
        private bool entityDetectorNeedsRefresh = false;

        // Damage i-frames (prevent repeated contact/AOE damage)
        private static Dictionary<GameObject, float> recentDamageSources = new Dictionary<GameObject, float>();
        private const float DAMAGE_COOLDOWN = 0.5f; // 500ms i-frames per damage source


        void Awake()
        {
            Instance = this;

            // Load configuration
            LoadConfig();

            Log("Rainflayer loading...");

            // Set up hooks
            SetupHooks();

            // Initialize components
            InitializeComponents();

            Log("Rainflayer loaded!");
        }

        void LoadConfig()
        {
            EnableAIControl = Config.Bind(
                "General",
                "EnableAIControl",
                true,
                "Enable AI control of the local player character."
            );

            DebugMode = Config.Bind(
                "Debug",
                "DebugMode",
                true,
                "Enable debug logging."
            );
        }

        void SetupHooks()
        {
            // Hook PlayerCharacterMasterController to allow camera updates but override input
            On.RoR2.PlayerCharacterMasterController.Update += PlayerCharacterMasterController_Update;

            // Hook stage start to reset drop pod state
            On.RoR2.Stage.Start += Stage_Start;

            // Hook run start to reset ALL AI state (avoid game restart)
            On.RoR2.Run.Start += Run_Start;

            // Hook damage to track what's damaging the player
            On.RoR2.HealthComponent.TakeDamage += HealthComponent_TakeDamage;

            // Hook death to track player death AND reset state
            GlobalEventManager.onCharacterDeathGlobal += OnCharacterDeathGlobal;

            Log("Hooks setup (AI control + camera override mode + damage tracking + run/stage/death resets)");
        }

        private void PlayerCharacterMasterController_Update(On.RoR2.PlayerCharacterMasterController.orig_Update orig, RoR2.PlayerCharacterMasterController self)
        {
            // Check if this is the local player and AI control is enabled
            if (self.master == LocalPlayerMaster && EnableAIControl.Value)
            {
                CharacterBody body = GetPlayerBody();
                if (body != null && body.inputBank != null)
                {
                    // Save current AI-controlled InputBank values
                    Vector3 aiMoveVector = body.inputBank.moveVector;
                    Vector3 aiAimDirection = body.inputBank.aimDirection;
                    bool aiSprinting = body.isSprinting;

                    // Run the original Update (includes camera logic)
                    orig(self);

                    // Immediately restore AI control values (overwrite player input)
                    body.inputBank.moveVector = aiMoveVector;
                    body.inputBank.aimDirection = aiAimDirection;
                    body.isSprinting = aiSprinting;

                    return;
                }
            }

            // Not AI controlled - run normal player input
            orig(self);
        }


        private System.Collections.IEnumerator Stage_Start(On.RoR2.Stage.orig_Start orig, Stage self)
        {
            // Reset drop pod state AND camera when entering a new stage
            if (aiController != null)
            {
                aiController.ResetDropPodState();
                aiController.GetCombatController()?.Reset(); // Re-initialize camera for new scene's CameraRigController
                Log("[Stage] New stage - reset drop pod state and camera");
            }

            // Call original
            return orig(self);
        }

        private void Run_Start(On.RoR2.Run.orig_Start orig, Run self)
        {
            // Call original first
            orig(self);

            // Reset ALL AI state when starting a new run (avoids game restart!)
            Log("[Run] ========================================");
            Log("[Run] NEW RUN DETECTED - Resetting AI state");
            Log("[Run] ========================================");

            if (aiController != null)
            {
                aiController.ResetAllAIState();
                Log("[Run] ✓ All AI state reset");
            }

            // Reset local player reference (will be re-initialized in Update)
            // This ensures we pick up the new run's player
            LocalPlayerMaster = null;
            PlayerAI = null;
            entityDetectorNeedsRefresh = true;  // EntityDetector needs fresh refs once new body spawns
            Log("[Run] ✓ Player references cleared (will reinitialize, EntityDetector refresh queued)");
        }

        private void HealthComponent_TakeDamage(On.RoR2.HealthComponent.orig_TakeDamage orig, HealthComponent self, DamageInfo damageInfo)
        {
            // Check if the player is taking damage
            if (self.body == LocalPlayerMaster?.GetBody())
            {
                // I-FRAMES: Check if this damage source recently hit us (prevent contact/AOE spam)
                if (damageInfo.attacker != null)
                {
                    float currentTime = Time.time;
                    float lastDamageTime;

                    if (recentDamageSources.TryGetValue(damageInfo.attacker, out lastDamageTime))
                    {
                        // This attacker damaged us recently - check if i-frames are active
                        if (currentTime - lastDamageTime < DAMAGE_COOLDOWN)
                        {
                            // I-FRAMES ACTIVE: Cancel this damage instance
                            LogDebug($"[I-FRAMES] Blocked damage from {damageInfo.attacker.name} (cooldown: {(DAMAGE_COOLDOWN - (currentTime - lastDamageTime)) * 1000:F0}ms)");
                            return; // Don't call orig() - damage is blocked!
                        }
                    }

                    // Update last damage time
                    recentDamageSources[damageInfo.attacker] = currentTime;

                    // Clean up old entries (performance)
                    List<GameObject> toRemove = new List<GameObject>();
                    foreach (var kvp in recentDamageSources)
                    {
                        if (currentTime - kvp.Value > DAMAGE_COOLDOWN * 2)
                            toRemove.Add(kvp.Key);
                    }
                    foreach (var key in toRemove)
                        recentDamageSources.Remove(key);
                }

                string attackerName = "Unknown";
                string damageType = damageInfo.damageType.ToString();

                if (damageInfo.attacker != null)
                {
                    CharacterBody attackerBody = damageInfo.attacker.GetComponent<CharacterBody>();
                    attackerName = attackerBody != null ? attackerBody.GetDisplayName() : damageInfo.attacker.name;
                }

                LogDebug($"[DAMAGE] Player taking {damageInfo.damage:F1} damage from {attackerName} (Type: {damageType})");

                // Log if damage is lethal
                if (self.health - damageInfo.damage <= 0)
                {
                    Log($"[DEATH INCOMING] Player about to die from {attackerName}! Damage: {damageInfo.damage:F1}, Current HP: {self.health:F1}");
                }
            }

            // Call original
            orig(self, damageInfo);
        }

        private void OnCharacterDeathGlobal(DamageReport damageReport)
        {
            // Check if the player died
            if (damageReport.victimBody == LocalPlayerMaster?.GetBody())
            {
                string killerName = "Unknown";
                string damageType = damageReport.damageInfo.damageType.ToString();

                if (damageReport.attackerBody != null)
                {
                    killerName = damageReport.attackerBody.GetDisplayName();
                }
                else if (damageReport.damageInfo.attacker != null)
                {
                    killerName = damageReport.damageInfo.attacker.name;
                }
                else
                {
                    // Check if this is environmental damage (The Planet)
                    string damageTypeStr = damageReport.damageInfo.damageType.ToString();
                    if (damageTypeStr.Contains("FallDamage") || damageTypeStr.Contains("Fall"))
                    {
                        killerName = "The Planet (Fall Damage)";
                    }
                    else if (damageTypeStr.Contains("VoidDeath") || damageTypeStr.Contains("Void"))
                    {
                        killerName = "The Void (Out of Bounds)";
                    }
                    else
                    {
                        killerName = "The Planet (Environmental)";
                    }
                }

                Log($"[DEATH] ========================================");
                Log($"[DEATH] Player killed by: {killerName}");
                Log($"[DEATH] Damage: {damageReport.damageInfo.damage:F1}");
                Log($"[DEATH] Damage Type: {damageType}");
                Log($"[DEATH] Position: {damageReport.victimBody.transform.position}");
                Log($"[DEATH] ========================================");

                // Reset AI state on death (will respawn shortly)
                if (aiController != null)
                {
                    aiController.ResetAllAIState();
                    entityDetectorNeedsRefresh = true;  // Body will be re-created on respawn
                    Log("[DEATH] ✓ AI state reset (will respawn, EntityDetector refresh queued)");
                }
            }
        }

        void InitializeComponents()
        {
            // Add AI Controller component
            aiController = gameObject.AddComponent<AIController>();

            // Add Socket Bridge component (handles all Python↔C# communication)
            gameObject.AddComponent<SocketBridge>();

            Log("Components initialized: AI Controller + Socket Bridge");
        }

        void InitializePlayerAI(CharacterMaster master)
        {
            LocalPlayerMaster = master;

            // Get existing AI component
            PlayerAI = master.GetComponent<BaseAI>();

            if (PlayerAI == null)
            {
                // Add our custom PlayerAI component (like PlayerBots adds PlayerBotBaseAI)
                PlayerAI = master.gameObject.AddComponent<PlayerAI>() as BaseAI;
                Log("Added PlayerAI component to player.");
            }

            // Try to initialize entity detector (may fail if body not ready yet)
            TryInitializeEntityDetector();

            // CRITICAL: If entity detector already exists from previous run, update its references!
            // This fixes the "movement works but no targeting" bug after respawn/new run
            if (entityDetector != null && aiController != null)
            {
                CharacterBody body = LocalPlayerMaster.GetBody();
                if (body != null && PlayerAI != null)
                {
                    entityDetector.UpdateReferences(PlayerAI, body);
                    entityDetectorNeedsRefresh = false;
                    Log("[EntityDetector] Updated references for new run/respawn");
                }
                else
                {
                    // Body not ready yet - Update() will retry when body spawns
                    entityDetectorNeedsRefresh = true;
                    Log("[EntityDetector] Body not ready yet, queued for refresh in Update()");
                }
            }

            // Create custom target object for position targeting
            if (CustomTargetObject == null)
            {
                CustomTargetObject = new GameObject("PlayerAICustomTarget");
                CustomTargetObject.transform.position = Vector3.zero;
                DontDestroyOnLoad(CustomTargetObject);
            }

            // CRITICAL: Initialize currentEnemy and customTarget manually (null for player characters by default)
            InitializeCurrentEnemy();
            InitializeCustomTarget();

            // Note: CharacterMaster finds AI components directly via GetComponent<BaseAI>() calls,
            // so no manual aiComponents registration needed.

            // Add skill drivers for autonomous control (like PlayerBots' InjectSkillDrivers)
            AddDefaultSkillDrivers();

            // Log customTarget status for debugging
            LogDebug($"customTarget is null: {PlayerAI.customTarget == null}");
            LogDebug($"currentEnemy is null: {PlayerAI.currentEnemy == null}");

            Log("Player AI initialized!");
        }

        void InitializeCurrentEnemy()
        {
            if (PlayerAI.currentEnemy != null)
            {
                LogDebug("currentEnemy already initialized");
                return;
            }

            try
            {
                // Get the currentEnemy field
                var currentEnemyField = typeof(BaseAI).GetField("currentEnemy",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (currentEnemyField != null)
                {
                    // Check if it's null
                    var currentValue = currentEnemyField.GetValue(PlayerAI);
                    if (currentValue == null)
                    {
                        // Create a new Target instance
                        // currentEnemy is of type BaseAI.Target, which is likely a serializable class
                        // We need to instantiate it properly
                        var targetType = currentEnemyField.FieldType;
                        var targetInstance = System.Activator.CreateInstance(targetType);

                        // Set the field
                        currentEnemyField.SetValue(PlayerAI, targetInstance);
                        Log("Initialized currentEnemy via reflection");
                    }
                    else
                    {
                        LogDebug($"currentEnemy already exists: {currentValue.GetType().FullName}");
                    }
                }
                else
                {
                    LogError("Could not find currentEnemy field");
                }
            }
            catch (System.Exception e)
            {
                LogError($"Failed to initialize currentEnemy: {e.Message}");
            }
        }

        void InitializeCustomTarget()
        {
            if (PlayerAI.customTarget != null)
            {
                LogDebug("customTarget already initialized");
                return;
            }

            try
            {
                // Get the customTarget field
                var customTargetField = typeof(BaseAI).GetField("customTarget",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (customTargetField != null)
                {
                    // Check if it's null
                    var currentValue = customTargetField.GetValue(PlayerAI);
                    if (currentValue == null)
                    {
                        // Create a new Target instance
                        var targetType = customTargetField.FieldType;
                        var targetInstance = System.Activator.CreateInstance(targetType);

                        // Set the field
                        customTargetField.SetValue(PlayerAI, targetInstance);
                        Log("Initialized customTarget via reflection");
                    }
                    else
                    {
                        LogDebug($"customTarget already exists: {currentValue.GetType().FullName}");
                    }
                }
                else
                {
                    LogError("Could not find customTarget field");
                }
            }
            catch (System.Exception e)
            {
                LogError($"Failed to initialize customTarget: {e.Message}");
            }
        }

        void AddDefaultSkillDrivers()
        {
            // Add basic skill drivers for autonomous combat
            // Based on PlayerBots' AiSkillHelper.AddDefaultSkills
            try
            {
                GameObject playerObj = LocalPlayerMaster.gameObject;

                // Add a simple combat skill driver
                AISkillDriver combatDriver = playerObj.AddComponent<AISkillDriver>();
                combatDriver.customName = "PlayerCombat";
                combatDriver.skillSlot = SkillSlot.Primary;
                combatDriver.requireSkillReady = true;
                combatDriver.minDistance = 0f;
                combatDriver.maxDistance = float.PositiveInfinity;
                combatDriver.moveTargetType = AISkillDriver.TargetType.Custom;
                combatDriver.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
                combatDriver.aimType = AISkillDriver.AimType.AtMoveTarget;
                combatDriver.activationRequiresTargetLoS = false;
                combatDriver.selectionRequiresTargetLoS = false;
                combatDriver.activationRequiresAimConfirmation = false;
                combatDriver.resetCurrentEnemyOnNextDriverSelection = true;
                combatDriver.driverUpdateTimerOverride = 3;
                combatDriver.shouldSprint = true;

                // Update the BaseAI's skillDrivers property using reflection
                // MMHOOK_RoR2 exposes the property, but we still need reflection to set it
                var skillDrivers = playerObj.GetComponents<AISkillDriver>();
                var skillDriversProperty = typeof(BaseAI).GetProperty("skillDrivers",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

                if (skillDriversProperty != null)
                {
                    skillDriversProperty.SetValue(PlayerAI, skillDrivers);
                    Log($"Updated skillDrivers: {skillDrivers.Length} drivers");
                }
                else
                {
                    LogError("Could not find skillDrivers property (MMHOOK not working?)");
                }

                Log("Added default skill drivers for autonomous control");
            }
            catch (System.Exception e)
            {
                LogError($"Failed to add skill drivers: {e.Message}");
            }
        }

        void TryInitializeEntityDetector()
        {
            if (LocalPlayerMaster == null || entityDetector != null)
                return;

            CharacterBody body = LocalPlayerMaster.GetBody();
            if (body == null)
                return;

            // Create entity detector
            entityDetector = new EntityDetector(PlayerAI, body);
            aiController.SetEntityDetector(entityDetector);
            Log("Entity detector initialized.");
        }

        void Update()
        {
            // Check for local player if not initialized
            if (LocalPlayerMaster == null)
            {
                TryInitializeLocalPlayer();
            }

            // Try to initialize entity detector if body wasn't ready yet
            if (LocalPlayerMaster != null && entityDetector == null)
            {
                TryInitializeEntityDetector();
            }

            // Refresh EntityDetector references when body becomes available after new run/death.
            // This handles the timing gap where body isn't spawned yet during InitializePlayerAI().
            // Without this, EntityDetector.GetBody() returns null (stale old-run master) and
            // FindBestTarget() always returns null → no targeting, no combat.
            if (entityDetectorNeedsRefresh && LocalPlayerMaster != null && entityDetector != null && PlayerAI != null)
            {
                CharacterBody body = LocalPlayerMaster.GetBody();
                if (body != null)
                {
                    entityDetector.UpdateReferences(PlayerAI, body);
                    entityDetectorNeedsRefresh = false;
                    Log("[EntityDetector] References refreshed - body now available after new run/respawn");

                    // Re-detect character class now that we have the new body
                    // (class may differ between runs, affects sprint-while-shooting and other combat behavior)
                    aiController?.GetCombatController()?.DetectCharacterClass();
                }
            }

            // Update AI controller
            if (aiController != null)
            {
                aiController.UpdateAI();
            }
        }

        void TryInitializeLocalPlayer()
        {
            // Find local player master via NetworkUser
            foreach (NetworkUser networkUser in NetworkUser.readOnlyInstancesList)
            {
                if (networkUser.isLocalPlayer && networkUser.master != null && LocalPlayerMaster != networkUser.master)
                {
                    InitializePlayerAI(networkUser.master);
                    break;
                }
            }
        }

        void FixedUpdate()
        {
            // Fixed update for AI operations
            if (aiController != null && PlayerAI != null && EnableAIControl.Value)
            {
                aiController.FixedUpdateAI();

                // Debug: Log AI state every 60 frames (~1 second)
                if (Time.frameCount % 60 == 0)
                {
                    CharacterBody body = GetPlayerBody();
                    if (body != null)
                    {
                        LogDebug($"[AI Debug] Alive={body.healthComponent.alive}, " +
                                $"CurrentEnemy={PlayerAI.currentEnemy != null}, " +
                                $"SkillDrivers={PlayerAI.skillDrivers?.Length ?? 0}, " +
                                $"Position={body.transform.position}");
                    }
                }
            }
        }

        /// <summary>
        /// Set the aim target for the player's AI.
        /// </summary>
        public static void SetAimTarget(GameObject target)
        {
            if (PlayerAI == null)
            {
                Instance?.LogError("Cannot set target: PlayerAI is null");
                return;
            }

            if (PlayerAI.customTarget == null)
            {
                Instance?.LogError($"Cannot set target: customTarget is null (target={target?.name ?? "null"})");
                // Try to reinitialize customTarget
                Instance?.InitializeCustomTarget();
                if (PlayerAI.customTarget == null)
                {
                    Instance?.LogError("Failed to initialize customTarget after retry");
                    return;
                }
            }

            if (target == null)
            {
                // Clear target - move custom target to invalid position
                if (CustomTargetObject != null)
                    CustomTargetObject.transform.position = Vector3.zero;
                if (PlayerAI.customTarget != null)
                    PlayerAI.customTarget.gameObject = null;
                Instance?.LogDebug("Cleared aim target");
                return;
            }

            // CRITICAL: Validate GameObject is not destroyed before setting
            // Unity objects can be "not null" but destroyed
            bool isTargetValid = false;
            try
            {
                isTargetValid = target != null && target.GetInstanceID() != 0;
            }
            catch
            {
                isTargetValid = false;
            }

            if (!isTargetValid)
            {
                Instance?.LogError($"Cannot set target: target is null or destroyed (name={target?.name ?? "null"})");
                return;
            }

            try
            {
                PlayerAI.customTarget.gameObject = target;
                Instance?.LogDebug($"Set aim target to: {target.name}");
            }
            catch (System.Exception e)
            {
                Instance?.LogError($"Failed to set target: {e.Message}");
            }
        }

        /// <summary>
        /// Set the aim target for the player's AI at a specific position.
        /// </summary>
        public static void SetAimTarget(Vector3 position)
        {
            if (CustomTargetObject != null && PlayerAI?.customTarget != null)
            {
                CustomTargetObject.transform.position = position;
                PlayerAI.customTarget.gameObject = CustomTargetObject;
                Instance?.LogDebug($"Set aim target position: {position}");
            }
            else
            {
                Instance?.LogError($"Cannot set position: CustomTargetObject={CustomTargetObject != null}, PlayerAI={PlayerAI != null}, customTarget={PlayerAI?.customTarget != null}");
            }
        }

        /// <summary>
        /// Clear the current aim target.
        /// </summary>
        public static void ClearAimTarget()
        {
            if (PlayerAI?.customTarget != null)
            {
                PlayerAI.customTarget.gameObject = null;
                Instance?.LogDebug("Cleared aim target.");
            }
        }

        /// <summary>
        /// Get the current player's body.
        /// </summary>
        public static CharacterBody GetPlayerBody()
        {
            return LocalPlayerMaster?.GetBody();
        }

        public void Log(string message)
        {
            Logger.LogInfo(message);
        }

        public void LogDebug(string message)
        {
            if (DebugMode.Value)
            {
                Logger.LogInfo($"[DEBUG] {message}");
            }
        }

        public void LogError(string message)
        {
            Logger.LogError(message);
        }
    }
}
