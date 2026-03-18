using RoR2;
using RoR2.CharacterAI;
using RoR2.Navigation;
using EntityStates;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Rainflayer
{
    /// <summary>
    /// Handles entity detection via RoR2's BaseAI system.
    /// Provides methods to find and select enemies, bosses, and allies.
    /// </summary>
    public class EntityDetector
    {
        private BaseAI ai;
        private CharacterMaster master;
        private bool debug = false;  // Enable debug logging for interactable detection

        // Lock to prevent reentrant calls to FindInteractablesInRange
        // (e.g., if called from both FixedUpdate path and a socket-dispatched command in the same frame)
        private readonly object interactableLock = new object();

        public EntityDetector(BaseAI ai, CharacterBody body)
        {
            this.ai = ai;
            // Get the master from the body
            this.master = body?.master;
        }

        /// <summary>
        /// Update the AI and master references (e.g., after respawn/new run).
        /// This is critical because EntityDetector holds these references and they become stale
        /// when the player respawns or starts a new run.
        /// </summary>
        public void UpdateReferences(BaseAI newAi, CharacterBody newBody)
        {
            this.ai = newAi;
            this.master = newBody?.master;
        }

        /// <summary>
        /// Log a message to the BepInEx console (same as AIController).
        /// </summary>
        private void Log(string message)
        {
            if (RainflayerPlugin.DebugMode.Value)
            {
                RainflayerPlugin.Instance.LogDebug($"[EntityDetector] {message}");
            }
        }

        /// <summary>
        /// Log an error message.
        /// </summary>
        private void LogError(string message)
        {
            RainflayerPlugin.Instance.LogError($"[EntityDetector] {message}");
        }

        /// <summary>
        /// Get the current player body from the master
        /// </summary>
        private CharacterBody GetBody()
        {
            // Try to get body from master first (most reliable)
            if (master != null)
            {
                CharacterBody body = master.GetBody();
                if (body != null)
                    return body;
            }

            // Fallback to AI body
            return ai?.body;
        }

        /// <summary>
        /// Find the closest enemy to the player.
        /// </summary>
        public GameObject FindClosestEnemy()
        {
            CharacterBody body = GetBody();
            if (body == null)
                return null;

            // Get all character bodies
            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;

            GameObject closest = null;
            float closestDistance = float.MaxValue;

            foreach (CharacterBody enemyBody in allBodies)
            {
                if (enemyBody == null || enemyBody == body)
                    continue;

                // === VALIDATE AND EXTRACT DATA IN ONE SHOT ===
                GameObject enemyObj;
                float distance;
                try
                {
                    enemyObj = enemyBody.gameObject;
                    if (enemyObj == null || enemyObj.GetInstanceID() == 0)
                        continue;

                    // Check team while object is valid
                    if (enemyBody.teamComponent == null || enemyBody.teamComponent.teamIndex == body.teamComponent.teamIndex)
                        continue;

                    // Extract distance NOW
                    distance = Vector3.Distance(body.transform.position, enemyBody.transform.position);
                }
                catch
                {
                    continue; // Object destroyed during extraction
                }

                // === WORK WITH LOCAL COPIES ===
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = enemyObj;
                }
            }

            return closest;
        }

        /// <summary>
        /// Find all bosses in the scene.
        /// </summary>
        public GameObject FindBoss()
        {
            CharacterBody body = GetBody();
            if (body == null)
                return null;

            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;

            GameObject closestBoss = null;
            float closestDistance = float.MaxValue;

            foreach (CharacterBody enemyBody in allBodies)
            {
                if (enemyBody == null || enemyBody == body)
                    continue;

                // === VALIDATE AND EXTRACT DATA IN ONE SHOT ===
                GameObject enemyObj;
                float distance;
                try
                {
                    enemyObj = enemyBody.gameObject;
                    if (enemyObj == null || enemyObj.GetInstanceID() == 0)
                        continue;

                    if (enemyBody.teamComponent == null || enemyBody.teamComponent.teamIndex == body.teamComponent.teamIndex)
                        continue;

                    if (!enemyBody.isBoss)
                        continue;

                    distance = Vector3.Distance(body.transform.position, enemyBody.transform.position);
                }
                catch
                {
                    continue;
                }

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestBoss = enemyObj;
                }
            }

            return closestBoss;
        }

        /// <summary>
        /// Find all elite enemies in the scene.
        /// </summary>
        public GameObject[] FindAllElites()
        {
            CharacterBody body = GetBody();
            if (body == null)
                return new GameObject[0];

            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;
            List<GameObject> elites = new List<GameObject>();

            foreach (CharacterBody enemyBody in allBodies)
            {
                if (enemyBody == null || enemyBody == body)
                    continue;

                // === VALIDATE AND EXTRACT DATA IN ONE SHOT ===
                GameObject enemyObj;
                try
                {
                    enemyObj = enemyBody.gameObject;
                    if (enemyObj == null || enemyObj.GetInstanceID() == 0)
                        continue;

                    if (enemyBody.teamComponent == null || enemyBody.teamComponent.teamIndex == body.teamComponent.teamIndex)
                        continue;

                    if (!enemyBody.isElite)
                        continue;
                }
                catch
                {
                    continue;
                }

                elites.Add(enemyObj);
            }

            return elites.ToArray();
        }

        /// <summary>
        /// Find the closest elite enemy.
        /// </summary>
        public GameObject FindClosestElite()
        {
            GameObject[] elites = FindAllElites();

            if (elites == null || elites.Length == 0)
                return null;

            CharacterBody body = GetBody();
            if (body == null)
                return null;

            GameObject closest = null;
            float closestDistance = float.MaxValue;

            foreach (GameObject elite in elites)
            {
                if (elite == null)
                    continue;

                float distance = Vector3.Distance(body.transform.position, elite.transform.position);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = elite;
                }
            }

            return closest;
        }

        /// <summary>
        /// Find all enemies within a certain range.
        /// </summary>
        public GameObject[] FindEnemiesInRange(float range)
        {
            CharacterBody body = GetBody();
            if (body == null)
                return new GameObject[0];

            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;
            List<GameObject> enemiesInRange = new List<GameObject>();

            foreach (CharacterBody enemyBody in allBodies)
            {
                if (enemyBody == null || enemyBody == body)
                    continue;

                // === VALIDATE AND EXTRACT DATA IN ONE SHOT ===
                GameObject enemyObj;
                float distance;
                try
                {
                    enemyObj = enemyBody.gameObject;
                    if (enemyObj == null || enemyObj.GetInstanceID() == 0)
                        continue;

                    if (enemyBody.teamComponent == null || enemyBody.teamComponent.teamIndex == body.teamComponent.teamIndex)
                        continue;

                    distance = Vector3.Distance(body.transform.position, enemyBody.transform.position);
                }
                catch
                {
                    continue;
                }

                if (distance <= range)
                    enemiesInRange.Add(enemyObj);
            }

            return enemiesInRange.ToArray();
        }

        /// <summary>
        /// Find the weakest enemy (lowest health) within range.
        /// </summary>
        public GameObject FindWeakestEnemy(float range = 100f)
        {
            CharacterBody body = GetBody();
            if (body == null)
                return null;

            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;

            GameObject weakest = null;
            float weakestHealth = float.MaxValue;

            foreach (CharacterBody enemyBody in allBodies)
            {
                if (enemyBody == null || enemyBody == body)
                    continue;

                // === VALIDATE AND EXTRACT DATA IN ONE SHOT ===
                GameObject enemyObj;
                float distance;
                float health;
                try
                {
                    enemyObj = enemyBody.gameObject;
                    if (enemyObj == null || enemyObj.GetInstanceID() == 0)
                        continue;

                    if (enemyBody.teamComponent == null || enemyBody.teamComponent.teamIndex == body.teamComponent.teamIndex)
                        continue;

                    distance = Vector3.Distance(body.transform.position, enemyBody.transform.position);

                    if (enemyBody.healthComponent == null)
                        continue;

                    health = enemyBody.healthComponent.health;
                }
                catch
                {
                    continue;
                }

                if (distance > range)
                    continue;

                if (health < weakestHealth)
                {
                    weakestHealth = health;
                    weakest = enemyObj;
                }
            }

            return weakest;
        }

        /// <summary>
        /// Find all allies.
        /// </summary>
        public GameObject[] FindAllies()
        {
            CharacterBody body = GetBody();
            if (body == null)
                return new GameObject[0];

            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;
            List<GameObject> allyList = new List<GameObject>();

            foreach (CharacterBody allyBody in allBodies)
            {
                if (allyBody == null || allyBody == body)
                    continue;

                // CRITICAL: Validate GameObject is not destroyed before accessing properties
                if (allyBody.gameObject == null)
                    continue;

                bool isValid = false;
                try
                {
                    isValid = allyBody.gameObject.GetInstanceID() != 0;
                }
                catch
                {
                    continue; // GameObject is destroyed
                }

                if (!isValid)
                    continue;

                if (allyBody.teamComponent == null || allyBody.teamComponent.teamIndex != body.teamComponent.teamIndex)
                    continue;

                allyList.Add(allyBody.gameObject);
            }

            return allyList.ToArray();
        }

        /// <summary>
        /// Find the closest ally.
        /// </summary>
        public GameObject FindClosestAlly()
        {
            GameObject[] allies = FindAllies();

            if (allies == null || allies.Length == 0)
                return null;

            CharacterBody body = GetBody();
            if (body == null)
                return null;

            GameObject closest = null;
            float closestDistance = float.MaxValue;

            foreach (GameObject ally in allies)
            {
                if (ally == null)
                    continue;

                // CRITICAL: Validate GameObject is not destroyed before accessing properties
                bool isValid = false;
                try
                {
                    isValid = ally.GetInstanceID() != 0;
                }
                catch
                {
                    isValid = false;
                }

                if (!isValid)
                    continue;

                float distance = Vector3.Distance(body.transform.position, ally.transform.position);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = ally;
                }
            }

            return closest;
        }

        /// <summary>
        /// Find the closest human player (excludes drones, turrets, and other AI minions).
        /// Uses PlayerCharacterMasterController.instances which only contains networked human players.
        /// Returns null if no other human players are alive (e.g., solo play).
        /// </summary>
        public GameObject FindClosestHumanPlayer()
        {
            CharacterBody ownBody = GetBody();
            if (ownBody == null)
                return null;

            GameObject closest = null;
            float closestDistance = float.MaxValue;

            foreach (PlayerCharacterMasterController playerController in PlayerCharacterMasterController.instances)
            {
                if (playerController == null)
                    continue;

                // Skip self
                if (playerController.master == null || playerController.master == master)
                    continue;

                // Skip if no alive body
                if (!playerController.master.hasBody)
                    continue;

                CharacterBody playerBody = playerController.master.GetBody();
                if (playerBody == null)
                    continue;

                // CRITICAL: Validate before accessing properties
                bool isValid = false;
                try
                {
                    isValid = playerBody.gameObject != null && playerBody.gameObject.GetInstanceID() != 0;
                }
                catch
                {
                    continue;
                }

                if (!isValid)
                    continue;

                if (playerBody.healthComponent == null || !playerBody.healthComponent.alive)
                    continue;

                float distance = Vector3.Distance(ownBody.transform.position, playerBody.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = playerBody.gameObject;
                }
            }

            return closest;
        }

        /// <summary>
        /// DEBUG METHOD: List ALL GameObjects with "chest" in the name.
        /// This is a brute-force search to verify chests exist in the scene.
        /// </summary>
        public void DebugListAllChests()
        {
            Log($"=== BRUTE FORCE CHEST SEARCH ===");

            // Method 1: Find all GameObjects (slow but thorough)
            GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();
            int chestCount = 0;

            foreach (GameObject obj in allObjects)
            {
                if (obj == null || string.IsNullOrEmpty(obj.name))
                    continue;

                // CRITICAL: Validate GameObject is not destroyed before accessing properties
                bool isValid = false;
                try
                {
                    isValid = obj.GetInstanceID() != 0;
                }
                catch
                {
                    isValid = false;
                }

                if (!isValid)
                    continue;

                string lowerName = obj.name.ToLower();
                if (lowerName.Contains("chest"))
                {
                    chestCount++;
                    CharacterBody body = GetBody();
                    float distance = body != null ? Vector3.Distance(body.transform.position, obj.transform.position) : 0f;

                    Log($"FOUND CHEST: {obj.name} at {obj.transform.position} (dist: {distance:F1}m)");

                    // Log components
                    Component[] components = obj.GetComponents<Component>();
                    Log($"  Components: {string.Join(", ", System.Linq.Enumerable.Select(components, c => c.GetType().Name))}");
                }
            }

            Log($"=== BRUTE FORCE SEARCH COMPLETE: Found {chestCount} chests ===");

            // Method 2: Try FindObjectsOfType with ChestBehavior
            Log($"Trying FindObjectsOfType<ChestBehavior>()...");
            try
            {
                ChestBehavior[] chestBehaviors = GameObject.FindObjectsOfType<ChestBehavior>();
                Log($"FindObjectsOfType<ChestBehavior> returned {chestBehaviors?.Length ?? 0} objects");
            }
            catch (System.Exception e)
            {
                LogError($"FindObjectsOfType<ChestBehavior> failed: {e.Message}");
            }

            // Method 3: Try FindObjectsOfType with PurchaseInteraction
            Log($"Trying FindObjectsOfType<PurchaseInteraction>()...");
            try
            {
                PurchaseInteraction[] purchases = GameObject.FindObjectsOfType<PurchaseInteraction>();
                Log($" FindObjectsOfType<PurchaseInteraction> returned {purchases?.Length ?? 0} objects");

                // List them
                if (purchases != null)
                {
                    foreach (var p in purchases)
                    {
                        if (p != null && p.gameObject != null)
                        {
                            string name = p.gameObject.name;
                            Log($"   PurchaseInteraction: {name} (cost: {p.cost})");
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($" FindObjectsOfType<PurchaseInteraction> failed: {e.Message}");
            }
        }

        /// <summary>
        /// Get information about an enemy entity.
        /// </summary>
        public EnemyInfo GetEnemyInfo(GameObject enemy)
        {
            if (enemy == null)
                return null;

            // CRITICAL: Validate GameObject is not destroyed before accessing properties
            bool isValid = false;
            try
            {
                isValid = enemy.GetInstanceID() != 0;
            }
            catch
            {
                return null; // GameObject is destroyed
            }

            if (!isValid)
                return null;

            CharacterBody enemyBody = enemy.GetComponent<CharacterBody>();
            if (enemyBody == null)
                return null;

            CharacterBody body = GetBody();

            Vector3 enemyPosition;
            try
            {
                enemyPosition = enemy.transform.position;
            }
            catch
            {
                return null; // Transform is invalid
            }

            return new EnemyInfo
            {
                GameObject = enemy,
                Name = enemyBody.GetDisplayName() ?? "Unknown",
                IsBoss = enemyBody.isBoss,
                IsElite = enemyBody.isElite,
                Health = enemyBody.healthComponent != null ? enemyBody.healthComponent.health : 0,
                MaxHealth = enemyBody.healthComponent != null ? enemyBody.healthComponent.fullHealth : 0,
                Position = enemyPosition,
                Distance = body != null ? Vector3.Distance(body.transform.position, enemyPosition) : 0
            };
        }

        /// <summary>
        /// Count enemies in range.
        /// </summary>
        public int CountEnemiesInRange(float range)
        {
            return FindEnemiesInRange(range).Length;
        }

        /// <summary>
        /// Check if target is within field of view.
        /// </summary>
        private bool IsInFieldOfView(Vector3 fromPosition, Vector3 fromForward, Vector3 targetPosition, float fovDegrees = 120f)
        {
            Vector3 directionToTarget = (targetPosition - fromPosition).normalized;
            float angle = Vector3.Angle(fromForward, directionToTarget);
            return angle <= fovDegrees / 2f;
        }

        /// <summary>
        /// Check if there's a clear line of sight to target (no walls/terrain blocking).
        /// Uses multiple raycast heights to avoid false positives from small obstacles.
        /// </summary>
        private bool HasLineOfSight(Vector3 fromPosition, Vector3 targetPosition)
        {
            Vector3 direction = targetPosition - fromPosition;
            float distance = direction.magnitude;

            // Try multiple raycast heights to be more lenient with small obstacles
            // Chest-level (0.5m), waist-level (1.0m), eye-level (1.5m), head-level (2.0m)
            float[] heights = new float[] { 0.5f, 1.0f, 1.5f, 2.0f };

            foreach (float height in heights)
            {
                Vector3 rayOrigin = fromPosition + Vector3.up * height;
                Vector3 rayTarget = targetPosition + Vector3.up * height;

                // Check if this height has clear line of sight
                RaycastHit hit;
                if (!Physics.Raycast(rayOrigin, (rayTarget - rayOrigin).normalized, out hit, distance, LayerMask.GetMask("World")))
                {
                    // At least one raycast succeeded - target is reachable
                    return true;
                }
            }

            // All raycasts failed - target is probably blocked by terrain/walls
            return false;
        }

        /// <summary>
        /// Find the best target based on threat priority.
        /// Considers distance, threat level (boss/elite), health, and current aim.
        /// </summary>
        public GameObject FindBestTarget(float maxRange = 50f, float fovDegrees = 120f, bool requireLineOfSight = true)
        {
            CharacterBody body = GetBody();
            if (body == null)
                return null;

            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;

            GameObject bestTarget = null;
            float bestScore = 0f;

            Vector3 aimDirection = body.inputBank?.aimDirection ?? body.transform.forward;
            Vector3 eyePosition = body.corePosition + Vector3.up * 1.5f;

            foreach (CharacterBody enemyBody in allBodies)
            {
                if (enemyBody == null || enemyBody == body)
                    continue;

                // Validate GameObject is not destroyed
                if (enemyBody.gameObject == null)
                    continue;

                try
                {
                    // Test if GameObject is valid
                    if (enemyBody.gameObject.GetInstanceID() == 0)
                        continue;
                }
                catch
                {
                    continue;
                }

                if (enemyBody.teamComponent == null || enemyBody.teamComponent.teamIndex == body.teamComponent.teamIndex)
                    continue;

                Vector3 enemyPosition;
                float distance;
                try
                {
                    enemyPosition = enemyBody.corePosition;
                    distance = Vector3.Distance(body.corePosition, enemyPosition);
                }
                catch
                {
                    continue; // corePosition or transform is invalid
                }

                // Range check
                if (distance > maxRange)
                    continue;

                // FOV check (skip if 360° - everything is in view)
                if (fovDegrees < 360f && !IsInFieldOfView(body.corePosition, aimDirection, enemyPosition, fovDegrees))
                    continue;

                // Line of sight check (if required)
                if (requireLineOfSight && !HasLineOfSight(eyePosition, enemyPosition))
                    continue;

                // Calculate threat score
                float score = 0f;

                // Closer = higher priority (normalized to 0-1, inverted)
                score += (1f - (distance / maxRange)) * 30f;

                // Boss = high priority
                if (enemyBody.isBoss)
                    score += 50f;

                // Elite = medium priority
                if (enemyBody.isElite)
                    score += 30f;

                // Low health = easier kill = higher priority
                if (enemyBody.healthComponent != null)
                {
                    float healthPercent = enemyBody.healthComponent.combinedHealthFraction;
                    score += (1f - healthPercent) * 20f; // More points for lower health
                }

                // Already in crosshair = highest priority (stick to current target)
                Vector3 dirToEnemy = (enemyPosition - body.corePosition).normalized;
                float angleToEnemy = Vector3.Angle(aimDirection, dirToEnemy);
                if (angleToEnemy < 10f) // Within 10 degrees of current aim
                    score += 40f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = enemyBody.gameObject;
                }
            }

            return bestTarget;
        }

        /// <summary>
        /// Find all interactables within range.
        /// Uses direct type lookups (like PlayerBots) instead of generic IInteractable scanning.
        /// Uses lock to prevent reentrant calls within the same frame.
        /// </summary>
        public InteractableInfo[] FindInteractablesInRange(float range = 100f, float fovDegrees = 360f, Vector3 aimDirection = default, bool requireLineOfSight = false, bool debug = false)
        {
            // Lock to prevent reentrant calls (e.g., FixedUpdate + dispatched socket command in same frame)
            lock (interactableLock)
            {
                // IMMEDIATE LOG - This must always show up
                Log($" FindInteractablesInRange CALLED - range={range}, fov={fovDegrees}, los={requireLineOfSight}, debug={debug}");

                CharacterBody body = GetBody();
                if (body == null)
                {
                    LogError($" FindInteractablesInRange FAILED - body is null!");
                    return new InteractableInfo[0];
                }

                List<InteractableInfo> interactables = new List<InteractableInfo>();
                Vector3 playerPos = body.transform.position;

                Log($" Player position: {playerPos}, body name: {body.name}");

                // Get aim direction for FOV check
                if (aimDirection == default)
                {
                    if (body.inputBank != null && body.inputBank.aimDirection != default)
                    {
                        aimDirection = body.inputBank.aimDirection;
                    }
                    else
                    {
                        aimDirection = body.transform.forward;
                    }
                }

            try
            {
                // === CHESTS (using ChestBehavior - direct type lookup like PlayerBots) ===
                ChestBehavior[] chests = GameObject.FindObjectsOfType<ChestBehavior>();
                if (chests != null && debug) Log($" Found {chests.Length} chests via ChestBehavior");

                if (chests != null)
                {
                    foreach (ChestBehavior chest in chests)
                    {
                        // CRITICAL: Null check at start of EVERY iteration
                        // Unity can destroy objects between iterations in multithreaded environment
                        if (chest == null)
                            continue;

                        // Validate component is not destroyed before accessing gameObject
                        GameObject chestObj = null;
                        try
                        {
                            chestObj = chest.gameObject;
                            if (chestObj == null || chestObj.GetInstanceID() == 0)
                                continue;
                        }
                        catch
                        {
                            // Component destroyed before we could access gameObject
                            continue;
                        }

                        // === VALIDATE AND EXTRACT ALL DATA IN ONE SHOT ===
                        // Reduces TOCTOU window - if object is destroyed mid-extraction, we skip the whole thing
                        Vector3 chestPos;
                        string chestName;
                        bool isOpened;

                        try
                        {
                            // Extract all data we need RIGHT NOW while object is valid
                            // Use local references and extract in one atomic-like block
                            chestPos = chest.transform.position;
                            chestName = chestObj.name;
                            isOpened = IsChestOpened(chestObj);
                        }
                        catch
                        {
                            // Object destroyed during data extraction - skip it
                            continue;
                        }

                        // === NOW WORK WITH LOCAL COPIES (safe from destruction) ===
                        if (isOpened)
                        {
                            if (debug) Log($"  ✓ Skipping opened chest: {chestName}");
                            continue;
                        }

                        // Skip lunar chests - they require pressing E to interact
                        string chestNameLower = chestName.ToLower();
                        if (chestNameLower.Contains("lunarchest") || (chestNameLower.Contains("lunar") && chestNameLower.Contains("chest")))
                        {
                            if (debug) Log($"  ✓ Skipping lunar chest: {chestName}");
                            continue;
                        }

                        // Skip equipment barrels - they require pressing E to interact
                        if (chestNameLower.Contains("equipmentbarrel"))
                        {
                            if (debug) Log($"  ✓ Skipping equipment barrel: {chestName}");
                            continue;
                        }

                        float distance = Vector3.Distance(playerPos, chestPos);
                        if (distance > range)
                            continue;

                        // FOV check
                        if (fovDegrees < 360f)
                        {
                            Vector3 directionToTarget = (chestPos - playerPos).normalized;
                            float angle = Vector3.Angle(aimDirection, directionToTarget);
                            if (angle > fovDegrees / 2f)
                                continue;
                        }

                        // LOS check
                        if (requireLineOfSight)
                        {
                            Vector3 eyePosition = body.corePosition + Vector3.up * 1.5f;
                            if (!HasLineOfSight(eyePosition, chestPos))
                            {
                                if (debug) Log($"  ✓ Skipping chest (no LOS): {chestName}");
                                continue;
                            }
                        }

                        // Final conversion - one more access to GameObject, but much safer now
                        // Revalidate GameObject is still valid before final access
                        try
                        {
                            // Double-check GameObject is still valid
                            if (chestObj != null && chestObj.GetInstanceID() != 0)
                            {
                                InteractableInfo info = CreateChestInfo(chestObj, body, distance);
                                if (info != null)
                                {
                                    interactables.Add(info);
                                    if (debug) Log($" ✓ Chest: {info.Name} at {distance:F1}m (cost: {info.Cost})");
                                }
                            }
                        }
                        catch
                        {
                            // Object destroyed during final conversion - rare but possible
                            continue;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($" Error finding chests: {e.Message}");
            }

            try
            {
                // === SHRINES & CHESTS (using PurchaseInteraction - direct lookup) ===
                PurchaseInteraction[] allPurchases = GameObject.FindObjectsOfType<PurchaseInteraction>();
                if (allPurchases != null && debug) Log($" Found {allPurchases.Length} PurchaseInteraction objects");

                if (allPurchases != null)
                {
                    foreach (PurchaseInteraction purchase in allPurchases)
                    {
                        // CRITICAL: Null check at start of EVERY iteration
                        if (purchase == null)
                            continue;

                        // Validate component is not destroyed before accessing gameObject
                        GameObject purchaseObj = null;
                        try
                        {
                            purchaseObj = purchase.gameObject;
                            if (purchaseObj == null || purchaseObj.GetInstanceID() == 0)
                                continue;
                        }
                        catch
                        {
                            // Component destroyed before we could access gameObject
                            continue;
                        }

                        // === VALIDATE AND EXTRACT ALL DATA IN ONE SHOT ===
                        Vector3 purchasePos;
                        string objName;
                        bool isAvailable;
                        bool hasChestBehavior;
                        bool isShrine;
                        bool isChest;
                        bool isOpened;

                        try
                        {
                            // Extract all properties NOW while object is valid
                            purchasePos = purchase.transform.position;
                            objName = purchaseObj.name.ToLower();
                            isAvailable = purchase.available;
                            hasChestBehavior = purchaseObj.GetComponent<ChestBehavior>() != null;
                            isShrine = objName.Contains("shrine");
                            isChest = objName.Contains("chest") || objName.Contains("category") || objName.Contains("barrel");
                            isOpened = isChest && IsChestOpened(purchaseObj);
                        }
                        catch
                        {
                            // Object destroyed during extraction
                            continue;
                        }

                        // === NOW WORK WITH LOCAL COPIES ===
                        // Skip if already handled by ChestBehavior loop
                        if (hasChestBehavior)
                            continue;

                        // Skip unavailable interactables
                        if (!isAvailable)
                        {
                            if (debug) Log($"  ✓ Skipping unavailable purchase: {objName}");
                            continue;
                        }

                        // Skip opened chests
                        if (isOpened)
                        {
                            if (debug) Log($"  ✓ Skipping opened chest: {objName}");
                            continue;
                        }

                        // Skip lunar chests - they require pressing E to interact
                        if (isChest && (objName.Contains("lunarchest") || (objName.Contains("lunar") && objName.Contains("chest"))))
                        {
                            if (debug) Log($"  ✓ Skipping lunar chest: {objName}");
                            continue;
                        }

                        // Skip equipment barrels - they require pressing E to interact
                        if (objName.Contains("equipmentbarrel"))
                        {
                            if (debug) Log($"  ✓ Skipping equipment barrel: {objName}");
                            continue;
                        }

                        // Only handle shrines and chests
                        if (!isShrine && !isChest)
                            continue;

                        float distance = Vector3.Distance(playerPos, purchasePos);
                        if (distance > range)
                            continue;

                        // FOV check
                        if (fovDegrees < 360f)
                        {
                            Vector3 directionToTarget = (purchasePos - playerPos).normalized;
                            float angle = Vector3.Angle(aimDirection, directionToTarget);
                            if (angle > fovDegrees / 2f)
                                continue;
                        }

                        // LOS check
                        if (requireLineOfSight)
                        {
                            Vector3 eyePosition = body.corePosition + Vector3.up * 1.5f;
                            if (!HasLineOfSight(eyePosition, purchasePos))
                            {
                                if (debug) Log($"  ✓ Skipping {objName} (no LOS)");
                                continue;
                            }
                        }

                        // Final conversion - one more access to GameObject
                        // Revalidate GameObject is still valid before final access
                        try
                        {
                            // Double-check GameObject is still valid
                            if (purchaseObj != null && purchaseObj.GetInstanceID() != 0)
                            {
                                InteractableInfo info = null;
                                if (isShrine)
                                    info = CreateShrineInfo(purchaseObj, body, distance);
                                else if (isChest)
                                    info = CreateChestInfo(purchaseObj, body, distance);

                                if (info != null)
                                {
                                    interactables.Add(info);
                                    if (debug) Log($" ✓ {info.Type}: {info.Name} at {distance:F1}m");
                                }
                            }
                        }
                        catch
                        {
                            // Object destroyed during final conversion
                            continue;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($" Error finding shrines/chests: {e.Message}");
            }

            try
            {
                // === TELEPORTER ===
                // Check singleton instance is not null first
                if (TeleporterInteraction.instance != null)
                {
                    GameObject teleporterObj = null;
                    Vector3 teleporterPos = Vector3.zero;
                    bool validTeleporter = false;

                    try
                    {
                        // Validate GameObject and extract data atomically
                        teleporterObj = TeleporterInteraction.instance.gameObject;
                        if (teleporterObj != null && teleporterObj.GetInstanceID() != 0)
                        {
                            teleporterPos = TeleporterInteraction.instance.transform.position;
                            validTeleporter = true;
                        }
                    }
                    catch
                    {
                        // GameObject destroyed or transform invalid
                        validTeleporter = false;
                    }

                    if (validTeleporter)
                    {
                        float distance = Vector3.Distance(playerPos, teleporterPos);

                        // NOTE: No range limit for teleporter - always include it so Brain can always navigate to it
                        // (range cap of ~500f may be re-added in future once nav is proven reliable)
                        // if (distance <= range)
                        try
                        {
                            // Revalidate before final access
                            if (teleporterObj != null && teleporterObj.GetInstanceID() != 0)
                            {
                                InteractableInfo info = CreateTeleporterInfo(teleporterObj, body, distance);
                                if (info != null)
                                {
                                    interactables.Add(info);
                                    if (debug) Log($" ✓ Teleporter at {distance:F1}m (charged: {info.Charged})");
                                }
                            }
                        }
                        catch
                        {
                            // CreateTeleporterInfo failed (GameObject destroyed)
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($" Error finding teleporter: {e.Message}");
            }

            try
            {
                // === SHOPS (MultiShopController) ===
                MultiShopController[] shops = GameObject.FindObjectsOfType<MultiShopController>();
                if (shops != null && debug) Log($" Found {shops.Length} multishops");

                if (shops != null)
                {
                    foreach (MultiShopController shop in shops)
                    {
                        // CRITICAL: Null check at start of EVERY iteration
                        if (shop == null)
                            continue;

                        // Get terminal GameObjects from the shop via public API
                        ShopTerminalBehavior[] terminalBehaviors = shop.GetComponentsInChildren<ShopTerminalBehavior>(true);
                        if (terminalBehaviors == null || terminalBehaviors.Length == 0)
                            continue;

                        GameObject[] terminals = new GameObject[terminalBehaviors.Length];
                        for (int ti = 0; ti < terminalBehaviors.Length; ti++)
                            terminals[ti] = terminalBehaviors[ti].gameObject;

                        foreach (GameObject terminal in terminals)
                        {
                            // CRITICAL: Null check at start of EVERY iteration
                            if (terminal == null)
                                continue;

                            // === VALIDATE AND EXTRACT ALL DATA IN ONE SHOT ===
                            Vector3 terminalPos;
                            try
                            {
                                // Validate GameObject is not destroyed
                                if (terminal.GetInstanceID() == 0)
                                    continue;

                                // Extract position NOW while valid
                                terminalPos = terminal.transform.position;
                            }
                            catch
                            {
                                // Object destroyed during extraction
                                continue;
                            }

                            // === NOW WORK WITH LOCAL COPIES ===
                            float distance = Vector3.Distance(playerPos, terminalPos);
                            if (distance > range)
                                continue;

                            // FOV check
                            if (fovDegrees < 360f)
                            {
                                Vector3 directionToTarget = (terminalPos - playerPos).normalized;
                                float angle = Vector3.Angle(aimDirection, directionToTarget);
                                if (angle > fovDegrees / 2f)
                                    continue;
                            }

                            // LOS check
                            if (requireLineOfSight)
                            {
                                Vector3 eyePosition = body.corePosition + Vector3.up * 1.5f;
                                if (!HasLineOfSight(eyePosition, terminalPos))
                                    continue;
                            }

                            // Final conversion - revalidate GameObject is still valid
                            try
                            {
                                // Double-check GameObject is still valid
                                if (terminal != null && terminal.GetInstanceID() != 0)
                                {
                                    InteractableInfo info = CreateShopInfo(terminal, body, distance);
                                    if (info != null)
                                    {
                                        interactables.Add(info);
                                        if (debug) Log($" ✓ Shop at {distance:F1}m");
                                    }
                                }
                            }
                            catch
                            {
                                // Object destroyed during final conversion
                                continue;
                            }
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($" Error finding shops: {e.Message}");
            }

            try
            {
                // === MOON BATTERIES / PILLARS (moon2 scene only) ===
                if (SceneManager.GetActiveScene().name == "moon2")
                {
                    InteractableInfo[] pillars = FindMoonPillars();
                    interactables.AddRange(pillars);
                    if (debug) Log($" Found {pillars.Length} uncharged moon pillars");
                }
            }
            catch (System.Exception e)
            {
                LogError($" Error finding moon pillars: {e.Message}");
            }

                if (debug)
                {
                    Log($" Total interactables found: {interactables.Count}");
                }

                return interactables.ToArray();
            } // End lock
        }

        /// <summary>
        /// Check if a PurchaseInteraction is currently locked (e.g., during the teleporter event).
        /// Locked interactables show a padlock and cannot be used until unlocked.
        /// </summary>
        private bool IsInteractableLocked(PurchaseInteraction purchase)
        {
            if (purchase == null)
                return false;
            try
            {
                return purchase.lockGameObject != null && purchase.lockGameObject.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if a chest has already been opened.
        /// Opened chests are destroyed or have their PurchaseInteraction disabled.
        /// </summary>
        private bool IsChestOpened(GameObject chestObj)
        {
            if (chestObj == null)
                return true; // Treat null as opened

            // Method 1: Check if GameObject is active (opened chests are often destroyed or deactivated)
            if (!chestObj.activeInHierarchy)
                return true;

            // Method 2: Check PurchaseInteraction component (opened chests have this removed or disabled)
            PurchaseInteraction purchase = chestObj.GetComponent<PurchaseInteraction>();
            if (purchase == null)
                return true; // No PurchaseInteraction = already opened or not a chest

            // Method 3: Check if PurchaseInteraction is available (some opened chests keep the component but mark it unavailable)
            if (!purchase.available)
                return true;

            // Method 4: Check name for "Open" or "Opened" (some chests get renamed when opened)
            if (chestObj.name.ToLower().Contains("open"))
                return true;

            // Method 5: Check if chest name contains "ChestBehavior" but the actual behavior is gone
            // (this happens when RoR2 cleans up opened chests)
            ChestBehavior chestBehavior = chestObj.GetComponent<ChestBehavior>();
            if (chestBehavior == null)
            {
                // Some chests don't have ChestBehavior (like category chests), so only check if name suggests it should
                string objName = chestObj.name.ToLower();
                if (objName.Contains("chest") || objName.Contains("barrel"))
                {
                    // If it's named like a chest but has no ChestBehavior, it might be opened
                    // But we can't be 100% sure, so we rely on PurchaseInteraction.available check above
                }
            }

            return false; // Chest appears to be unopened
        }

        /// <summary>
        /// Create InteractableInfo for a chest.
        /// </summary>
        private InteractableInfo CreateChestInfo(GameObject chestObj, CharacterBody playerBody, float distance)
        {
            if (chestObj == null)
                return null;

            // Skip opened chests
            if (IsChestOpened(chestObj))
            {
                if (debug) Log($"  ✓ Filtering opened chest: {chestObj.name}");
                return null;
            }

            string objName = chestObj.name.ToLower();
            string displayName = "Basic Chest";
            int cost = 0;

            // Determine chest type by name
            if (objName.Contains("large"))
                displayName = "Large Chest";
            else if (objName.Contains("legendary"))
                displayName = "Legendary Chest";
            else if (objName.Contains("medium"))
                displayName = "Medium Chest";

            // Get cost and lock status from PurchaseInteraction
            PurchaseInteraction purchase = chestObj.GetComponent<PurchaseInteraction>();
            bool isLocked = false;
            if (purchase != null)
            {
                cost = (int)purchase.cost;
                isLocked = IsInteractableLocked(purchase);
            }

            if (isLocked && debug) Log($"  [LOCKED] Chest is locked: {chestObj.name}");

            return new InteractableInfo
            {
                Name = displayName,
                Type = "chest",
                Distance = distance,
                GameObject = chestObj,
                Cost = cost,
                Charged = false,
                ChargePercent = 0f,
                BossActive = false,
                IsLocked = isLocked
            };
        }

        /// <summary>
        /// Create InteractableInfo for a shrine.
        /// </summary>
        private InteractableInfo CreateShrineInfo(GameObject shrineObj, CharacterBody playerBody, float distance)
        {
            if (shrineObj == null)
                return null;

            string objName = shrineObj.name.ToLower();
            string displayName = "Shrine";
            int cost = 0;

            // Determine shrine type by name
            if (objName.Contains("combat"))
                displayName = "Shrine of Combat";
            else if (objName.Contains("blood"))
                displayName = "Shrine of Blood";
            else if (objName.Contains("mountain"))
                displayName = "Shrine of the Mountain";
            else if (objName.Contains("chance"))
                displayName = "Shrine of Chance";
            else if (objName.Contains("gold"))
                displayName = "Shrine of Gold";
            else if (objName.Contains("rest"))
                displayName = "Shrine of Rest";
            else if (objName.Contains("order"))
                displayName = "Shrine of Order";

            // Get cost and lock status from PurchaseInteraction
            PurchaseInteraction purchase = shrineObj.GetComponent<PurchaseInteraction>();
            bool isLocked = false;
            if (purchase != null)
            {
                cost = (int)purchase.cost;
                isLocked = IsInteractableLocked(purchase);
            }

            if (isLocked && debug) Log($"  [LOCKED] Shrine is locked: {shrineObj.name}");

            return new InteractableInfo
            {
                Name = displayName,
                Type = "shrine",
                Distance = distance,
                GameObject = shrineObj,
                Cost = cost,
                Charged = false,
                ChargePercent = 0f,
                BossActive = false,
                IsLocked = isLocked
            };
        }

        /// <summary>
        /// Create InteractableInfo for the teleporter.
        /// </summary>
        private InteractableInfo CreateTeleporterInfo(GameObject teleporterObj, CharacterBody playerBody, float distance)
        {
            if (teleporterObj == null)
                return null;

            TeleporterInteraction teleporter = teleporterObj.GetComponent<TeleporterInteraction>();
            if (teleporter == null)
                return null;

            bool charged = teleporter.isCharged;
            bool bossActive = teleporter.activationState == TeleporterInteraction.ActivationState.Charging ||
                             teleporter.activationState == TeleporterInteraction.ActivationState.Charged;

            // Get actual charge fraction via HoldoutZoneController (0-100%)
            float chargePercent = charged ? 100f : 0f;
            HoldoutZoneController holdoutZone = teleporterObj.GetComponent<HoldoutZoneController>();
            if (holdoutZone != null)
                chargePercent = holdoutZone.charge * 100f;

            return new InteractableInfo
            {
                Name = "Teleporter",
                Type = "teleporter",
                Distance = distance,
                GameObject = teleporterObj,
                Cost = 0,
                Charged = charged,
                ChargePercent = chargePercent,
                BossActive = bossActive
            };
        }

        /// <summary>
        /// Create InteractableInfo for a shop terminal.
        /// </summary>
        private InteractableInfo CreateShopInfo(GameObject shopObj, CharacterBody playerBody, float distance)
        {
            if (shopObj == null)
                return null;

            ShopTerminalBehavior shop = shopObj.GetComponent<ShopTerminalBehavior>();
            int cost = 0;

            bool isLocked = false;
            if (shop != null)
            {
                PurchaseInteraction purchase = shop.GetComponent<PurchaseInteraction>();
                if (purchase != null)
                {
                    // Skip terminals already purchased - same check as IsChestOpened uses
                    if (!purchase.available)
                        return null;
                    cost = (int)purchase.cost;
                    isLocked = IsInteractableLocked(purchase);
                }
            }

            if (isLocked && debug) Log($"  [LOCKED] Shop terminal is locked: {shopObj.name}");

            return new InteractableInfo
            {
                Name = "Shop Terminal",
                Type = "shop",
                Distance = distance,
                GameObject = shopObj,
                Cost = cost,
                Charged = false,
                ChargePercent = 0f,
                BossActive = false,
                IsLocked = isLocked
            };
        }

        /// <summary>
        /// Find ally players with detailed info.
        /// </summary>
        public AllyInfo[] GetAllies()
        {
            CharacterBody body = GetBody();
            if (body == null)
                return new AllyInfo[0];

            ReadOnlyCollection<CharacterBody> allBodies = CharacterBody.readOnlyInstancesList;
            List<AllyInfo> allyList = new List<AllyInfo>();

            foreach (CharacterBody allyBody in allBodies)
            {
                if (allyBody == null || allyBody == body)
                    continue;

                // CRITICAL: Validate GameObject is not destroyed before accessing properties
                if (allyBody.gameObject == null)
                    continue;

                bool isValid = false;
                try
                {
                    isValid = allyBody.gameObject.GetInstanceID() != 0;
                }
                catch
                {
                    continue; // GameObject is destroyed
                }

                if (!isValid)
                    continue;

                if (allyBody.teamComponent == null || allyBody.teamComponent.teamIndex != body.teamComponent.teamIndex)
                    continue;

                float distance;
                Vector3 allyPosition;
                try
                {
                    distance = Vector3.Distance(body.transform.position, allyBody.transform.position);
                    allyPosition = allyBody.transform.position;
                }
                catch
                {
                    continue; // Transform is invalid
                }

                bool isDowned = !allyBody.healthComponent.alive;

                allyList.Add(new AllyInfo
                {
                    GameObject = allyBody.gameObject,
                    Name = allyBody.GetDisplayName() ?? "Ally",
                    Health = allyBody.healthComponent != null ? allyBody.healthComponent.health : 0,
                    MaxHealth = allyBody.healthComponent != null ? allyBody.healthComponent.fullHealth : 0,
                    Position = allyPosition,
                    Distance = distance,
                    IsDowned = isDowned
                });
            }

            return allyList.ToArray();
        }

        /// <summary>
        /// Find an ally by name.
        /// </summary>
        public AllyInfo FindAllyByName(string name)
        {
            AllyInfo[] allies = GetAllies();
            foreach (AllyInfo ally in allies)
            {
                if (ally.Name.ToLower().Contains(name.ToLower()))
                    return ally;
            }
            return null;
        }

        /// <summary>
        /// Samples positions along the vector from startPos to targetPos and returns the
        /// furthest position that the ground NodeGraph A* can reach from startPos.
        /// Used to build an intermediate NavWaypoint when the final target is on a disconnected
        /// graph component (e.g. moon2 battery pillars on floating islands).
        /// Returns null if even the closest sample is unreachable, or if the graph is unavailable.
        /// </summary>
        public Vector3? FindFurthestReachableGroundPosition(Vector3 startPos, Vector3 targetPos, int samples = 6)
        {
            if (SceneInfo.instance == null) return null;
            NodeGraph groundGraph = SceneInfo.instance.GetNodeGraph(MapNodeGroup.GraphType.Ground);
            if (groundGraph == null) return null;

            CharacterBody body = GetBody();
            HullClassification hull = body?.hullClassification ?? HullClassification.Human;

            NodeGraph.PathRequest req = new NodeGraph.PathRequest();
            req.startPos = startPos;
            req.hullClassification = hull;
            req.maxJumpHeight = float.PositiveInfinity;
            req.maxSpeed = body?.moveSpeed ?? 7f;

            Vector3? best = null;
            for (int i = 1; i <= samples; i++)
            {
                float t = (float)i / samples;
                Vector3 candidate = Vector3.Lerp(startPos, targetPos, t);

                req.endPos = candidate;
                req.path = new Path(groundGraph);
                PathTask task = groundGraph.ComputePath(req);

                if (task.wasReachable)
                    best = candidate;
                else
                    break;  // Positions further along this line are likely also unreachable
            }
            return best;
        }

        /// <summary>
        /// Snap a world position to the nearest walkable ground NodeGraph node.
        /// Use this before setting pillar / jump-pad positions as navigation targets so
        /// NodeGraph A* can always plan a valid path (targets off or above the graph beeline).
        /// Falls back to the original position if NodeGraph or SceneInfo is unavailable.
        /// </summary>
        public Vector3 SnapToNearestGroundNode(Vector3 worldPos)
        {
            try
            {
                if (SceneInfo.instance == null) return worldPos;
                NodeGraph groundGraph = SceneInfo.instance.GetNodeGraph(MapNodeGroup.GraphType.Ground);
                if (groundGraph == null) return worldPos;

                CharacterBody body = GetBody();
                HullClassification hull = body?.hullClassification ?? HullClassification.Human;

                NodeGraph.NodeIndex nodeIndex = groundGraph.FindClosestNode(worldPos, hull);
                Vector3 nodePos;
                if (groundGraph.GetNodePosition(nodeIndex, out nodePos))
                    return nodePos;
            }
            catch { }
            return worldPos;
        }

        /// <summary>
        /// Find uncharged moon battery pillars on the moon2 (Commencement) map.
        /// Scans HoldoutZoneControllers whose name contains "Battery" — the four pillars
        /// (Mass, Soul, Blood, Design). Returns only those not yet fully charged.
        /// Interaction via OnInteractionBegin triggers charging.
        /// </summary>
        public InteractableInfo[] FindMoonPillars()
        {
            CharacterBody body = GetBody();
            if (body == null) return new InteractableInfo[0];

            var results = new List<InteractableInfo>();
            Vector3 playerPos = body.transform.position;

            // Find the rescue ship — it sits on the main open platform and is always reachable
            // from the battery islands via the Arena gate. Used as:
            //   (a) probe anchor (more reliable than spawn pos)
            //   (b) nav waypoint when bot is still in spawn room and can't reach islands directly
            Vector3? shipNavWaypoint = null;
            GameObject rescueShip = GameObject.Find("RescueshipMoon");
            if (rescueShip != null)
                shipNavWaypoint = SnapToNearestGroundNode(rescueShip.transform.position);

            // Build a reachability-test request from the player's current position.
            NodeGraph groundGraph = SceneInfo.instance?.GetNodeGraph(MapNodeGroup.GraphType.Ground);
            HullClassification hull = body.hullClassification;
            NodeGraph.PathRequest reachReq = new NodeGraph.PathRequest();
            reachReq.startPos = playerPos;
            reachReq.hullClassification = hull;
            reachReq.maxJumpHeight = float.PositiveInfinity;
            reachReq.maxSpeed = body.moveSpeed;

            string pillarRequiredGate = null;

            try
            {
                HoldoutZoneController[] holdouts = GameObject.FindObjectsOfType<HoldoutZoneController>();
                foreach (var hz in holdouts)
                {
                    if (hz == null) continue;

                    GameObject obj = null;
                    string name = "";
                    Vector3 pos = Vector3.zero;
                    float charge = 0f;

                    try
                    {
                        obj = hz.gameObject;
                        if (obj == null || obj.GetInstanceID() == 0) continue;
                        name = obj.name;
                        pos = obj.transform.position;
                        charge = hz.charge;
                    }
                    catch { continue; }

                    // Moon batteries have "Battery" in their name; exclude teleporter
                    if (!name.ToLower().Contains("battery")) continue;
                    if (obj.GetComponent<TeleporterInteraction>() != null) continue;

                    // Skip disabled pillars (game bug: >4 Battery HoldoutZoneControllers can exist;
                    // extras get disabled when the real 4 are cleared, but still appear in FindObjectsOfType)
                    if (!obj.activeInHierarchy) continue;

                    // Skip fully charged pillars
                    if (charge >= 1.0f) continue;

                    // Open gate so FindClosestNode can see actual island nodes (not main-platform fallback).
                    // Gate stays open — NavigationController.ClearFullNavigationState closes it on arrival/abort.
                    if (pillarRequiredGate != null && SceneInfo.instance != null)
                        SceneInfo.instance.SetGateState(pillarRequiredGate, true);

                    Vector3 groundSnap = SnapToNearestGroundNode(pos);

                    // Check reachability from player's current position (gate already open above).
                    bool reachableFromHere = false;
                    if (groundGraph != null)
                    {
                        reachReq.endPos = groundSnap;
                        reachReq.path = new Path(groundGraph);
                        PathTask task = groundGraph.ComputePath(reachReq);
                        reachableFromHere = task.wasReachable;
                    }

                    Vector3? navWaypoint = reachableFromHere ? null : shipNavWaypoint;
                    float dist = Vector3.Distance(playerPos, pos);

                    // Look up the hardcoded waypoint chain for this pillar type.
                    // WaypointChain takes priority over NavWaypoint in NavigationController.
                    string nameLower = name.ToLower();
                    string pillarType = nameLower.Contains("blood") ? "blood"
                                      : nameLower.Contains("soul")  ? "soul"
                                      : nameLower.Contains("mass")  ? "mass"
                                      : nameLower.Contains("design") ? "design"
                                      : null;
                    Vector3[] chain = null;
                    if (pillarType != null)
                        NavigationController.Moon2PillarChains.TryGetValue(pillarType, out chain);
                    // Only use chain if it's non-empty (TODO entries fall back to NavWaypoint)
                    if (chain != null && chain.Length == 0) chain = null;

                    // Build return chain: design uses custom recorded waypoints (forward path is
                    // all downward jumps — can't reverse).  All other pillar types use the
                    // forward chain reversed (same ledges/jumps work in both directions).
                    Vector3[] returnChain = null;
                    if (pillarType != null)
                    {
                        if (NavigationController.Moon2PillarReturnChains.TryGetValue(pillarType, out var custom))
                            returnChain = custom;
                        else if (chain != null)
                        {
                            returnChain = (Vector3[])chain.Clone();
                            System.Array.Reverse(returnChain);
                        }
                    }

                    RainflayerPlugin.Instance?.Log($"[PILLAR] Found '{name}' type={pillarType ?? "unknown"}  GroundSnap={groundSnap:F1}  Chain={chain?.Length.ToString() ?? "none"} waypoints  ReturnChain={returnChain?.Length.ToString() ?? "none"}  ReachableNow={reachableFromHere}  Charge={charge * 100f:F0}%");

                    results.Add(new InteractableInfo
                    {
                        GameObject = obj,
                        Type = "pillar",
                        Name = name,
                        Position = groundSnap,
                        RequiredGate = pillarRequiredGate,
                        NavWaypoint = chain != null ? null : navWaypoint,  // chain supersedes NavWaypoint
                        WaypointChain = chain,
                        ReturnChain = returnChain,
                        Distance = dist,
                        ChargePercent = charge * 100f,
                        Charged = false
                    });
                }
            }
            catch (System.Exception e)
            {
                LogError($"Error finding moon pillars: {e.Message}");
            }

            return results.ToArray();
        }

        /// <summary>
        /// Find the nearest active JumpVolume on the current map.
        /// On moon2 (Commencement) this is the launch pad that sends the player
        /// to the Mithrix arena after all four batteries are charged.
        /// Walking onto the trigger fires the launch — no button press needed.
        /// </summary>
        public GameObject FindMoonJumpPad()
        {
            CharacterBody body = GetBody();
            if (body == null) return null;

            Vector3 playerPos = body.transform.position;
            GameObject nearest = null;
            float nearestDist = float.MaxValue;

            try
            {
                JumpVolume[] jumpVolumes = GameObject.FindObjectsOfType<JumpVolume>();
                foreach (var jv in jumpVolumes)
                {
                    if (jv == null) continue;

                    GameObject obj = null;
                    Vector3 pos = Vector3.zero;

                    try
                    {
                        obj = jv.gameObject;
                        if (obj == null || obj.GetInstanceID() == 0) continue;
                        if (!obj.activeInHierarchy) continue; // only active pads
                        pos = obj.transform.position;
                    }
                    catch { continue; }

                    float dist = Vector3.Distance(playerPos, pos);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = obj;
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($"Error finding jump pad: {e.Message}");
            }

            if (nearest != null)
                Log($"Found jump pad '{nearest.name}' at {nearestDist:F1}m");

            return nearest;
        }

        /// <summary>
        /// Find the nearest MoonElevator escape orb that is in Ready state (Interactability.Available).
        /// MoonElevators exist in the scene from stage load but start Inactive (ConditionsNotMet).
        /// After Mithrix is defeated, TriggerOnArenaExit fires and transitions them to Ready (Available).
        /// Returns the nearest Ready elevator, or null if none are ready yet (boss still alive).
        /// </summary>
        public GameObject FindLunarTeleporterOrb()
        {
            CharacterBody body = GetBody();
            if (body == null) return null;

            Vector3 playerPos = body.transform.position;
            GameObject nearest = null;
            float nearestDist = float.MaxValue;

            try
            {
                GenericInteraction[] all = GameObject.FindObjectsOfType<GenericInteraction>();
                Log($"FindLunarTeleporterOrb: scanning {all?.Length ?? 0} GenericInteraction objects");

                foreach (var gi in all)
                {
                    if (gi == null) continue;

                    GameObject obj = null;
                    try
                    {
                        obj = gi.gameObject;
                        if (obj == null || !obj.activeInHierarchy) continue;
                    }
                    catch { continue; }

                    string name = obj.name ?? "";

                    // MoonElevator is the post-Mithrix escape orb (confirmed via EntityStates.MoonElevator source)
                    if (name.IndexOf("MoonElevator", System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // Only Ready-state elevators are interactable — Inactive ones exist all stage but are ConditionsNotMet
                    if (gi.Networkinteractability != Interactability.Available)
                    {
                        Log($"  MoonElevator '{name}': not ready (interactability={gi.Networkinteractability}), skipping");
                        continue;
                    }

                    Vector3 pos;
                    try { pos = obj.transform.position; }
                    catch { continue; }

                    float dist = Vector3.Distance(playerPos, pos);
                    Log($"  MoonElevator '{name}' READY at {pos} dist={dist:F1}m");
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = obj;
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($"Error finding MoonElevator orb: {e.Message}");
            }

            if (nearest != null)
                Log($"Found ready MoonElevator '{nearest.name}' at {nearestDist:F1}m");
            else
                Log($"FindLunarTeleporterOrb: no Ready MoonElevator found (Mithrix not defeated yet?)");

            return nearest;
        }

        /// <summary>
        /// Phase 2.5: Find nearest dropped item pickup.
        /// Uses GenericPickupController (RoR2 API) to detect items on the ground.
        /// Similar to AutoPlay's FindItemPickup() implementation.
        /// </summary>
        public GameObject FindNearestPickup(float range = 30f)
        {
            CharacterBody body = GetBody();
            if (body == null)
                return null;

            GameObject nearest = null;
            float nearestDistance = float.MaxValue;

            try
            {
                // Find all GenericPickupController objects (dropped items)
                // This is RoR2 API - same approach as AutoPlay
                GenericPickupController[] pickups = GameObject.FindObjectsOfType<GenericPickupController>();

                if (pickups != null)
                {
                    foreach (GenericPickupController pickup in pickups)
                    {
                        if (pickup == null || pickup.gameObject == null)
                            continue;

                        // Skip inactive pickups (already being picked up or despawned)
                        if (!pickup.gameObject.activeInHierarchy)
                            continue;

                        // Skip fuel array pickups - they don't magnetically attach, need manual interaction
                        if (pickup.gameObject.name.Contains("QuestVolatileBatteryWorldPickup"))
                            continue;
                        // Skip lunar coin pickups - they don't magnetically attach, need manual interaction
                        // NOTE: Name-based check is unreliable - all pickups are named "GenericPickup(Clone)"
                        // Use PickupDef.coinValue instead (RoR2's canonical way to identify lunar coins)
                        PickupDef pickupDef = PickupCatalog.GetPickupDef(pickup.pickupIndex);
                        if (pickupDef != null && pickupDef.coinValue > 0)
                            continue;

                        float distance = Vector3.Distance(body.transform.position, pickup.transform.position);

                        if (distance <= range && distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearest = pickup.gameObject;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($"Error finding pickups: {e.Message}");
            }

            if (nearest != null)
            {
                Log($"Found nearest pickup '{nearest.name}' at {nearestDistance:F1}m");
            }

            return nearest;
        }
    }

    /// <summary>
    /// Information about a dropped item pickup.
    /// </summary>
    public class PickupInfo
    {
        public GameObject GameObject { get; set; } = null;
        public string Name { get; set; } = "Unknown Item";
        public float Distance { get; set; }
        public Vector3 Position { get; set; }
    }

    /// <summary>
    /// Information about an enemy entity.
    /// </summary>
    public class EnemyInfo
    {
        public GameObject GameObject { get; set; } = null;
        public string Name { get; set; } = "Unknown";
        public bool IsBoss { get; set; }
        public bool IsElite { get; set; }
        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public Vector3 Position { get; set; }
        public float Distance { get; set; }

        public float HealthPercentage => MaxHealth > 0 ? (Health / MaxHealth) * 100 : 0;
    }

    /// <summary>
    /// Information about an interactable object.
    /// </summary>
    public class InteractableInfo
    {
        public GameObject GameObject { get; set; } = null;
        public string Type { get; set; } = "unknown";  // chest, shrine, teleporter, shop, drone, printer, portal, misc, command
        public string Name { get; set; } = "Unknown";
        public int Cost { get; set; } = 0;
        public Vector3 Position { get; set; }
        public float Distance { get; set; }

        // Additional properties for specific types
        public int UsesRemaining { get; set; } = -1;  // For shrines
        public bool Charged { get; set; } = false;  // For teleporter
        public float ChargePercent { get; set; } = 0f;  // For teleporter
        public bool BossActive { get; set; } = false;  // For teleporter
        public bool IsLocked { get; set; } = false;  // True when locked (e.g., during teleporter event)
        // Two-phase navigation: intermediate waypoint on the ground graph that the bot reaches
        // before attempting the final approach to Position (used when Position is on a
        // disconnected graph component, e.g. moon2 battery pillars on floating islands).
        public Vector3? NavWaypoint { get; set; } = null;
        // Multi-hop waypoint chain for crossing disconnected subgraphs (e.g. moon2 pillar islands).
        // When set, NavigationController consumes these in order before approaching Position.
        // Each hop tries A* first; falls back to direct movement when unreachable.
        // Takes priority over NavWaypoint when both are set.
        public Vector3[] WaypointChain { get; set; } = null;
        // How to get back from this pillar zone to the main platform.
        // Blood/soul/mass = WaypointChain reversed at runtime.  Design = custom recorded path.
        // Prepended to the next pillar's forward chain when IsInPillarZone is true.
        public Vector3[] ReturnChain { get; set; } = null;
        // NodeGraph gate that must be opened during A* computation to reach this target.
        // The gate is opened just before ComputePath and closed immediately after — no lasting side effect.
        public string RequiredGate { get; set; } = null;
        // Skip-ahead cap: CheckChainSkipAhead will not advance waypointChainIndex past this index.
        // Use to protect waypoints that appear reachable but physically are not (false positive).
        // null = no cap (default behaviour).
        public int? WaypointChainSkipCap { get; set; } = null;
        // Chain indices where A* should be bypassed and beeline used instead.
        // When waypointChainIndex is one of these values, RecomputePath is skipped so the
        // bot moves direct (beeline) to that waypoint regardless of graph reachability.
        public System.Collections.Generic.HashSet<int> WaypointChainBeelineIndices { get; set; } = null;
    }

    /// <summary>
    /// Information about an ally player.
    /// </summary>
    public class AllyInfo
    {
        public GameObject GameObject { get; set; } = null;
        public string Name { get; set; } = "Unknown";
        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public float HealthPercent => MaxHealth > 0 ? (Health / MaxHealth) * 100 : 0;
        public Vector3 Position { get; set; }
        public float Distance { get; set; }
        public bool IsDowned { get; set; } = false;
    }
}
