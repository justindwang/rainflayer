using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Rainflayer
{
    /// <summary>
    /// LLM Counterboss — spawns an AI-controlled adversary survivor at the teleporter.
    ///
    /// Flow:
    ///   1. Player picks up item → C# sends item_picked_up to Python (if brain running)
    ///   2. Python CounterbossWorker calls LLM → sends COUNTERBOSS_SPAWN back immediately
    ///   3. HandleCounterbossSpawn (SocketBridge) caches items here via CacheCounterbuild()
    ///   4. Teleporter fires → SpawnWithCachedOrRandom() uses cache (or random if no brain)
    ///
    /// Works with EnableAIControl=false. Python brain enhances with LLM build; without
    /// it, the adversary gets a random build from the available drop lists.
    /// </summary>
    public class CounterbossController : MonoBehaviour
    {
        private CharacterMaster adversaryMaster = null;
        public CharacterMaster AdversaryMaster => adversaryMaster;
        private bool adversaryAlive = false;

        // True once SpawnCoroutine runs — blocks any further spawns this stage,
        // including LLM callbacks that arrive after the teleporter starts charging.
        private bool _spawnedThisStage = false;
        public bool SpawnedThisStage => _spawnedThisStage;

        // Build cached by Python brain via CacheCounterbuild(). Null = use random.
        private List<(string name, int count)> _cachedItems = null;
        private string _cachedReasoning = null;
        private string _cachedSurvivor = null;  // null = use config default

        private SocketBridge socketBridge = null;

        void Awake()
        {
            socketBridge = GetComponent<SocketBridge>();
        }

        void OnEnable()
        {
            TeleporterInteraction.onTeleporterBeginChargingGlobal += OnTeleporterBeginCharging;
            On.RoR2.Stage.Start += OnStageStart;
            On.RoR2.BossGroup.DropRewards += BossGroup_DropRewards;
        }

        void OnDisable()
        {
            TeleporterInteraction.onTeleporterBeginChargingGlobal -= OnTeleporterBeginCharging;
            On.RoR2.Stage.Start -= OnStageStart;
            On.RoR2.BossGroup.DropRewards -= BossGroup_DropRewards;
        }

        // ------------------------------------------------------------------
        // Stage reset
        // ------------------------------------------------------------------

        private IEnumerator OnStageStart(On.RoR2.Stage.orig_Start orig, Stage self)
        {
            _cachedItems = null;
            _cachedReasoning = null;
            _cachedSurvivor = null;
            _capturedBossMaxHp = 0f;
            _spawnedThisStage = false;
            CleanupAdversary();
            return orig(self);
        }

        // ------------------------------------------------------------------
        // Boss drop suppression
        // ------------------------------------------------------------------

        /// <summary>
        /// Suppress the teleporter boss item drop when our adversary replaced the normal boss.
        /// When the adversary is (was) the only combat squad member, skip orig entirely —
        /// the player earns items by stealing from the adversary on kill instead.
        /// </summary>
        private void BossGroup_DropRewards(On.RoR2.BossGroup.orig_DropRewards orig, BossGroup self)
        {
            if (!RainflayerPlugin.EnableCounterboss.Value)
            {
                orig(self);
                return;
            }

            // Check if every member of this squad is (or was) our adversary.
            // readOnlyMembersList may be empty by the time DropRewards fires (members removed
            // on death), so we also check if adversaryMaster was ever part of this group.
            TeleporterInteraction tp = TeleporterInteraction.instance;
            if (tp?.bossGroup == self)
            {
                Log("[Counterboss] Suppressing teleporter boss item drop (adversary replaced boss)");
                return;  // Skip orig — no drop, player gets an item via steal mechanic instead
            }

            orig(self);
        }

        // ------------------------------------------------------------------
        // Cache from Python brain (called by SocketBridge.HandleCounterbossSpawn)
        // ------------------------------------------------------------------

        /// <summary>
        /// Store the LLM-generated build from Python. Called on main thread.
        /// If the teleporter is already charging, spawn immediately.
        /// </summary>
        public void CacheCounterbuild(List<(string name, int count)> items, string reasoning, string survivor = null)
        {
            _cachedItems = items;
            _cachedReasoning = reasoning;
            _cachedSurvivor = survivor;
            Log($"[Counterboss] Counterbuild cached from Python: {items.Count} item types, survivor={survivor ?? "default"}");

            // If the teleporter is already charging (Python was slow), spawn now —
            // but only if we haven't already spawned and the build actually has items.
            // A 0-item build (e.g. stage-start cache before any pickups) should not
            // be used here — SpawnWithCachedOrRandom already handles that via random fallback.
            if (!_spawnedThisStage &&
                items.Count > 0 &&
                TeleporterInteraction.instance != null &&
                TeleporterInteraction.instance.activationState == TeleporterInteraction.ActivationState.Charging)
            {
                Log("[Counterboss] Teleporter already charging — spawning with LLM build now");
                StartCoroutine(SpawnCoroutine(items, reasoning, survivor));
                _cachedItems = null;
                _cachedReasoning = null;
                _cachedSurvivor = null;
            }
        }

        // ------------------------------------------------------------------
        // Teleporter hook — main entry point
        // ------------------------------------------------------------------

        private void OnTeleporterBeginCharging(TeleporterInteraction tp)
        {
            if (!RainflayerPlugin.EnableCounterboss.Value) return;
            if (!NetworkServer.active) return;
            if (!IsInGame()) return;

            StartCoroutine(SpawnWithCachedOrRandom());
        }

        private IEnumerator SpawnWithCachedOrRandom()
        {
            // Give Python one frame to deliver a pre-cached build via CacheCounterbuild()
            // (in case COUNTERBOSS_SPAWN was in-flight in the socket queue at the exact
            // moment the teleporter fired). Anything beyond one frame = use random.
            yield return null;

            // Re-check after yield — CacheCounterbuild's immediate path may have raced and already spawned
            if (_spawnedThisStage) yield break;

            if (_cachedItems != null && _cachedItems.Count > 0)
            {
                Log("[Counterboss] Using LLM build from Python brain");
                var items = _cachedItems;
                var reasoning = _cachedReasoning ?? "LLM counter-build";
                var survivor = _cachedSurvivor;
                _cachedItems = null;
                _cachedReasoning = null;
                _cachedSurvivor = null;
                StartCoroutine(SpawnCoroutine(items, reasoning, survivor));
            }
            else
            {
                // No cache, or stage-start cache with 0 items (player hadn't picked anything up yet)
                _cachedItems = null;
                _cachedReasoning = null;
                _cachedSurvivor = null;
                Log("[Counterboss] No cached build (or empty stage-start build) — using random");
                var items = BuildRandomItemList();
                string reasoning = BuildItemListChatString(items, "Random counter-build");
                StartCoroutine(SpawnCoroutine(items, reasoning, null));
            }
        }

        private List<(string name, int count)> BuildRandomItemList()
        {
            CharacterBody playerBody = RainflayerPlugin.GetPlayerBody();
            int targetCount = 0;
            if (playerBody?.inventory != null)
            {
                foreach (var idx in playerBody.inventory.itemAcquisitionOrder)
                    targetCount += playerBody.inventory.GetItemCountPermanent(idx);
            }
            // If player has 0 items, adversary gets 0 items too
            if (targetCount <= 0) return new List<(string, int)>();

            var pool = new List<PickupIndex>();
            if (Run.instance != null)
            {
                // Weight toward greens (tier2 x4), some whites (tier1 x2), few reds (tier3 x1)
                pool.AddRange(Run.instance.availableTier1DropList);
                pool.AddRange(Run.instance.availableTier1DropList);
                pool.AddRange(Run.instance.availableTier2DropList);
                pool.AddRange(Run.instance.availableTier2DropList);
                pool.AddRange(Run.instance.availableTier2DropList);
                pool.AddRange(Run.instance.availableTier2DropList);
                pool.AddRange(Run.instance.availableTier3DropList);
            }
            if (pool.Count == 0) return new List<(string, int)>();

            var chosen = new Dictionary<string, int>();
            for (int i = 0; i < targetCount; i++)
            {
                PickupIndex pick = pool[UnityEngine.Random.Range(0, pool.Count)];
                PickupDef def = PickupCatalog.GetPickupDef(pick);
                if (def == null || def.itemIndex == ItemIndex.None) continue;
                ItemDef itemDef = ItemCatalog.GetItemDef(def.itemIndex);
                if (itemDef == null) continue;
                string name = itemDef.name;
                chosen[name] = chosen.ContainsKey(name) ? chosen[name] + 1 : 1;
            }

            var result = new List<(string, int)>();
            foreach (var kvp in chosen)
                result.Add((kvp.Key, kvp.Value));
            return result;
        }

        // ------------------------------------------------------------------
        // Spawn coroutine
        // ------------------------------------------------------------------

        private IEnumerator SpawnCoroutine(List<(string name, int count)> items, string reasoning, string survivorOverride)
        {
            // Lock immediately — any LLM callback that arrives after this (e.g. from a
            // post-teleporter item pickup) must not trigger a second spawn.
            _spawnedThisStage = true;

            // Wait for the boss combat squad to be populated before killing.
            // On stages 2+ the boss spawns slightly after the teleporter begins charging,
            // so readOnlyMembersList may be empty the first frame. Poll up to 3 seconds.
            {
                float waited = 0f;
                while (waited < 5f)
                {
                    TeleporterInteraction tp = TeleporterInteraction.instance;
                    if (tp?.bossGroup?.combatSquad != null &&
                        tp.bossGroup.combatSquad.readOnlyMembersList.Count > 0)
                        break;
                    yield return new WaitForSeconds(0.1f);
                    waited += 0.1f;
                }
            }

            // Kill existing teleporter boss(es) — suppress their item drops.
            // Do a second pass 2s later to catch late-spawning pack members (e.g. Clay Dunestrider pairs).
            KillTeleporterBosses(captureHp: true);
            yield return new WaitForSeconds(2f);
            KillTeleporterBosses(captureHp: false);

            Vector3 spawnPos = GetSpawnPosition();

            // Use LLM-chosen survivor, fall back to Commando if none provided
            string survivorName = !string.IsNullOrEmpty(survivorOverride) ? survivorOverride : "Commando";
            string survivorMasterName = survivorName + "MonsterMaster";
            GameObject masterPrefab = MasterCatalog.FindMasterPrefab(survivorMasterName);
            if (masterPrefab == null)
                masterPrefab = MasterCatalog.FindMasterPrefab(survivorName + "Master");
            if (masterPrefab == null)
            {
                masterPrefab = MasterCatalog.FindMasterPrefab("CommandoMonsterMaster");
                LogError($"[Counterboss] Master '{survivorMasterName}' not found, falling back to Commando");
            }
            if (masterPrefab == null)
            {
                LogError("[Counterboss] Could not find any master prefab — aborting");
                yield break;
            }

            GameObject masterObj = Instantiate(masterPrefab, spawnPos, Quaternion.identity);
            adversaryMaster = masterObj.GetComponent<CharacterMaster>();
            if (adversaryMaster == null)
            {
                LogError("[Counterboss] No CharacterMaster on instantiated prefab");
                Destroy(masterObj);
                yield break;
            }

            adversaryMaster.teamIndex = TeamIndex.Monster;
            NetworkServer.Spawn(masterObj);
            adversaryMaster.SpawnBody(spawnPos, Quaternion.identity);
            yield return null;

            CharacterBody adversaryBody = adversaryMaster.GetBody();
            if (adversaryBody == null)
            {
                LogError("[Counterboss] Body failed to spawn");
                yield break;
            }

            if (adversaryBody.teamComponent != null)
                adversaryBody.teamComponent.teamIndex = TeamIndex.Monster;

            // Prevent fall damage — adversary AI can walk off edges and get one-shot
            adversaryBody.bodyFlags |= CharacterBody.BodyFlags.IgnoreFallDamage;

            // Boss scaling — match the replaced teleporter boss's HP, scale damage down
            if (_capturedBossMaxHp > 0f)
            {
                // Set max HP to match the killed boss (already stage/difficulty scaled), modified by config
                adversaryBody.baseMaxHealth = _capturedBossMaxHp * RainflayerPlugin.CounterbossHPMultiplier.Value;
            }
            // Reduce damage so the adversary is a DPS race, not an instakill threat
            adversaryBody.baseDamage *= RainflayerPlugin.CounterbossDamageMultiplier.Value;
            adversaryBody.levelDamage *= RainflayerPlugin.CounterbossDamageMultiplier.Value;

            // Scale up model to make it visually distinct as a boss.
            // Multiply natural scale rather than overriding with absolute Vector3.one * 2,
            // so large-bodied survivors (Toolbot, Acrid) don't become gargantuan.
            if (adversaryBody.modelLocator?.modelTransform != null)
                adversaryBody.modelLocator.modelTransform.localScale *= 2f;

            // Recalculate stats so baseMaxHealth change takes effect, then set health directly.
            // Direct assignment bypasses HealthComponent_Heal (and its CounterbossHealMultiplier)
            // which is only meant for in-combat self-healing, not spawn initialization.
            adversaryBody.MarkAllStatsDirty();
            yield return null;  // wait one fixed update for RecalculateStats to run
            if (adversaryBody.healthComponent != null)
                adversaryBody.healthComponent.health = adversaryBody.healthComponent.fullHealth;

            // Give items
            GiveItems(adversaryMaster, items);

            SetupAdversaryAI(adversaryMaster, adversaryBody);

            // Wait for the killed boss(es) to be fully removed from the combatSquad before
            // registering the adversary. BossGroup.FixedUpdate sums fullHealth across all members;
            // if a dead boss lingers in the list, its max HP inflates the total making the bar
            // appear at ~50% when the adversary first appears. Poll until only the adversary
            // (or nothing) remains, with a 2s timeout so we don't stall indefinitely.
            {
                float waited2 = 0f;
                TeleporterInteraction tp2 = TeleporterInteraction.instance;
                if (tp2?.bossGroup?.combatSquad != null)
                {
                    while (waited2 < 2f)
                    {
                        bool onlyAdversaryOrEmpty = true;
                        foreach (var m in tp2.bossGroup.combatSquad.readOnlyMembersList)
                        {
                            if (m != null && m != adversaryMaster)
                            {
                                onlyAdversaryOrEmpty = false;
                                break;
                            }
                        }
                        if (onlyAdversaryOrEmpty) break;
                        yield return new WaitForSeconds(0.1f);
                        waited2 += 0.1f;
                    }
                }
            }

            RegisterWithBossGroup(adversaryMaster);

            adversaryAlive = true;
            GlobalEventManager.onCharacterDeathGlobal += OnCharacterDeath;

            // Show item panel on HUD and broadcast reasoning to chat.
            // Clients with the mod also get the panel via BossGroup.UpdateBossMembers hook in AdversaryItemDisplay.
            string survivorDisplayName = adversaryBody.GetDisplayName();
            var itemDisplay = adversaryMaster.gameObject.AddComponent<AdversaryItemDisplay>();
            itemDisplay.Show(adversaryMaster.inventory, survivorDisplayName);
            BroadcastToChat(reasoning);

            Log($"[Counterboss] Adversary spawned: {adversaryBody.GetDisplayName()} at {spawnPos}");
        }

        // ------------------------------------------------------------------
        // Boss killing — suppress drops by zeroing out the drop table
        // ------------------------------------------------------------------

        // Captured from the teleporter boss before we kill it — used to set adversary HP
        private float _capturedBossMaxHp = 0f;

        private void KillTeleporterBosses(bool captureHp = true)
        {
            TeleporterInteraction tp = TeleporterInteraction.instance;
            if (tp?.bossGroup?.combatSquad == null) return;

            if (captureHp) _capturedBossMaxHp = 0f;
            var members = new List<CharacterMaster>(tp.bossGroup.combatSquad.readOnlyMembersList);
            foreach (var master in members)
            {
                if (master == null) continue;
                // Never kill our own adversary — that triggers the steal mechanic prematurely
                if (master == adversaryMaster) continue;
                CharacterBody body = master.GetBody();
                if (body?.healthComponent == null || !body.healthComponent.alive) continue;

                // Capture the highest boss HP to use as adversary HP baseline (first pass only)
                if (captureHp && body.healthComponent.fullHealth > _capturedBossMaxHp)
                    _capturedBossMaxHp = body.healthComponent.fullHealth;

                // Zero out gold reward — item drop is suppressed by BossGroup_DropRewards hook.
                // Do NOT touch deathState: overwriting it with Uninitialized breaks GenericCharacterDeath
                // and leaves the body frozen in place forever instead of playing its death animation.
                var deathRewards = body.GetComponent<DeathRewards>();
                if (deathRewards != null)
                    deathRewards.goldReward = 0;

                body.healthComponent.Suicide();
                Log($"[Counterboss] Killed teleporter boss: {body.GetDisplayName()} (HP: {body.healthComponent.fullHealth:F0})");
            }
            if (captureHp) Log($"[Counterboss] Captured boss HP baseline: {_capturedBossMaxHp:F0}");
        }

        // ------------------------------------------------------------------
        // Spawn position
        // ------------------------------------------------------------------

        private Vector3 GetSpawnPosition()
        {
            TeleporterInteraction tp = TeleporterInteraction.instance;
            if (tp != null)
                return tp.transform.position + new Vector3(5f, 1f, 5f);

            CharacterBody player = RainflayerPlugin.GetPlayerBody();
            if (player != null)
                return player.transform.position + new Vector3(10f, 1f, 0f);

            return Vector3.zero;
        }

        // ------------------------------------------------------------------
        // Inventory
        // ------------------------------------------------------------------

        private void GiveItems(CharacterMaster master, List<(string name, int count)> items)
        {
            if (master?.inventory == null) return;
            int given = 0;
            foreach (var (itemName, count) in items)
            {
                ItemIndex idx = ItemCatalog.FindItemIndex(itemName);
                if (idx == ItemIndex.None)
                    idx = TryFindItemByPartialName(itemName);
                if (idx == ItemIndex.None)
                {
                    LogDebug($"[Counterboss] Unknown item '{itemName}' — skipping");
                    continue;
                }
                master.inventory.GiveItem(idx, count);
                given += count;
            }
            Log($"[Counterboss] Gave adversary {given} total items");
        }

        private ItemIndex TryFindItemByPartialName(string partial)
        {
            string lower = partial.ToLower();
            foreach (var idx in ItemCatalog.allItems)
            {
                ItemDef def = ItemCatalog.GetItemDef(idx);
                if (def?.name.ToLower().Contains(lower) == true)
                    return idx;
            }
            return ItemIndex.None;
        }

        // ------------------------------------------------------------------
        // AI setup
        // ------------------------------------------------------------------

        private void SetupAdversaryAI(CharacterMaster master, CharacterBody body)
        {
            try
            {
                BaseAI ai = master.GetComponent<BaseAI>();
                if (ai == null)
                    ai = master.gameObject.AddComponent<PlayerAI>() as BaseAI;

                AdversaryAI.Inject(body.name, master.gameObject, ai);

                // Point the AI at the player immediately
                CharacterBody playerBody = RainflayerPlugin.GetPlayerBody();
                if (playerBody != null && ai.currentEnemy != null)
                    ai.currentEnemy.gameObject = playerBody.gameObject;

                Log("[Counterboss] AI configured via AdversaryAI.Inject");
            }
            catch (Exception e)
            {
                LogError($"[Counterboss] SetupAdversaryAI failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------
        // BossGroup registration
        // ------------------------------------------------------------------

        private void RegisterWithBossGroup(CharacterMaster master)
        {
            try
            {
                TeleporterInteraction tp = TeleporterInteraction.instance;
                if (tp?.bossGroup?.combatSquad == null)
                {
                    Log("[Counterboss] No teleporter BossGroup — adversary won't show in boss bar");
                    return;
                }

                BossNamePriority priority = master.gameObject.GetComponent<BossNamePriority>();
                if (priority == null)
                    priority = master.gameObject.AddComponent<BossNamePriority>();
                priority.priority = 10;

                tp.bossGroup.combatSquad.AddMember(master);
                Log("[Counterboss] Registered with teleporter BossGroup");
            }
            catch (Exception e)
            {
                LogError($"[Counterboss] RegisterWithBossGroup failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Chat broadcast — reasoning + item list with display names
        // ------------------------------------------------------------------

        private void BroadcastToChat(string reasoning)
        {
            try
            {
                if (!NetworkServer.active) return;

                // Always log the full reasoning to mod logs
                Log($"[Counterboss] REASONING: {reasoning}");

                // Chat: truncate reasoning to 200 chars so it fits on screen
                string safe = reasoning.Replace("<", "[").Replace(">", "]");
                string chatReasoning = safe.Length > 200 ? safe.Substring(0, 197) + "..." : safe;
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = $"<color=#ff4040>[ADVERSARY]</color> {chatReasoning}"
                });
            }
            catch (Exception e)
            {
                LogError($"[Counterboss] BroadcastToChat failed: {e.Message}");
            }
        }

        private string BuildItemListChatString(List<(string name, int count)> items, string prefix)
        {
            var parts = new List<string>();
            foreach (var (itemName, count) in items)
            {
                string displayName = GetItemDisplayName(itemName);
                parts.Add(count > 1 ? $"{displayName} x{count}" : displayName);
            }
            string list = string.Join(", ", parts);
            return prefix != null ? $"{prefix}: {list}" : list;
        }

        private string GetItemDisplayName(string internalName)
        {
            ItemIndex idx = ItemCatalog.FindItemIndex(internalName);
            if (idx == ItemIndex.None)
                idx = TryFindItemByPartialName(internalName);
            if (idx == ItemIndex.None)
                return internalName;

            ItemDef def = ItemCatalog.GetItemDef(idx);
            if (def == null) return internalName;

            string display = Language.GetString(def.nameToken);
            return string.IsNullOrEmpty(display) ? internalName : display;
        }

        // ------------------------------------------------------------------
        // Death — item steal
        // ------------------------------------------------------------------

        private void OnCharacterDeath(DamageReport report)
        {
            if (!adversaryAlive) return;
            if (report.victimMaster != adversaryMaster) return;

            adversaryAlive = false;
            GlobalEventManager.onCharacterDeathGlobal -= OnCharacterDeath;

            StealItemFromAdversary();
            socketBridge?.SendCounterbossDiedEvent();
            adversaryMaster = null;
        }

        private void StealItemFromAdversary()
        {
            try
            {
                CharacterMaster playerMaster = RainflayerPlugin.LocalPlayerMaster;
                if (playerMaster == null || adversaryMaster?.inventory == null) return;

                var candidates = new List<ItemIndex>();
                foreach (var idx in adversaryMaster.inventory.itemAcquisitionOrder)
                {
                    if (adversaryMaster.inventory.GetItemCountPermanent(idx) <= 0) continue;
                    ItemDef def = ItemCatalog.GetItemDef(idx);
                    if (def == null) continue;
                    if (def.tier == ItemTier.Lunar || def.tier == ItemTier.VoidTier1 ||
                        def.tier == ItemTier.VoidTier2 || def.tier == ItemTier.VoidTier3 ||
                        def.tier == ItemTier.VoidBoss) continue;
                    candidates.Add(idx);
                }

                if (candidates.Count == 0)
                {
                    Log("[Counterboss] No stealable items on adversary");
                    return;
                }

                ItemIndex stolen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                adversaryMaster.inventory.RemoveItem(stolen, 1);
                playerMaster.inventory.GiveItem(stolen, 1);

                string displayName = GetItemDisplayName(ItemCatalog.GetItemDef(stolen)?.name ?? "Unknown");
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = $"<color=#ffcc00>[STOLEN]</color> You claimed <color=#aaffaa>{displayName}</color> from the adversary!"
                });
                Log($"[Counterboss] Stole {displayName} → player");
            }
            catch (Exception e)
            {
                LogError($"[Counterboss] StealItemFromAdversary failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Cleanup
        // ------------------------------------------------------------------

        private void CleanupAdversary()
        {
            if (adversaryMaster != null)
            {
                GlobalEventManager.onCharacterDeathGlobal -= OnCharacterDeath;
                adversaryAlive = false;
                adversaryMaster = null;
            }
        }

        private bool IsInGame() => RainflayerPlugin.GetPlayerBody() != null && Run.instance != null;

        private void Log(string msg) => RainflayerPlugin.Instance?.Log(msg);
        private void LogDebug(string msg) => RainflayerPlugin.Instance?.LogDebug(msg);
        private void LogError(string msg) => RainflayerPlugin.Instance?.LogError(msg);
    }
}
