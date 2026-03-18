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
    /// Mithrix Herald — replaces Phase 1 of the Brother (Mithrix) encounter with a
    /// survivor adversary counterbuild, while Mithrix himself stands frozen in his
    /// ThroneSpawnState (head bowed, immune) as a visual "powering up" backdrop.
    ///
    /// Flow:
    ///   1. Phase1.OnEnter fires → we hook OnMemberAddedServer on the Phase1 combatSquad.
    ///   2. Mithrix spawns into that squad → our callback intercepts him:
    ///        - Remove him from the Phase1 combatSquad (so the phase-end check ignores him).
    ///        - Add Immune buff + disable his BaseAI.
    ///        - Force his EntityStateMachine into a looping ThroneSpawnState.
    ///        - Spawn the adversary survivor and add it to the Phase1 combatSquad instead.
    ///   3. Adversary dies → combatSquad.memberCount == 0 → Phase1 advances to Phase2 normally.
    ///   4. Phase2.OnEnter → we unfreeze Mithrix (remove Immune, re-enable BaseAI).
    ///
    /// Health bar: only the adversary is in phaseBossGroup.combatSquad, so only its bar
    /// shows. Mithrix's bar never appears during Phase 1.
    /// </summary>
    public class MithrixHeraldController : MonoBehaviour
    {
        // Set externally by CounterbossController before Phase1 fires
        // (same LLM cache path as teleporter bosses)
        private List<(string name, int count)> _cachedItems = null;
        private string _cachedReasoning = null;
        private string _cachedSurvivor = null;

        // State
        private CharacterMaster _adversaryMaster = null;
        public CharacterMaster AdversaryMaster => _adversaryMaster;
        private CharacterMaster _mithrixMaster = null;
        private float _capturedMithrixHp = 0f;
        private bool _adversaryAlive = false;
        private bool _heraldActive = false;   // true while we are intercepting Phase 1
        private bool _spawnedThisStage = false;
        public bool SpawnedThisStage => _spawnedThisStage;

        private SocketBridge _socketBridge;

        void Awake()
        {
            _socketBridge = GetComponent<SocketBridge>();
        }

        void OnEnable()
        {
            On.EntityStates.Missions.BrotherEncounter.Phase1.OnEnter += Phase1_OnEnter;
            On.EntityStates.Missions.BrotherEncounter.Phase2.OnEnter += Phase2_OnEnter;
            On.RoR2.Stage.Start += OnStageStart;
        }

        void OnDisable()
        {
            On.EntityStates.Missions.BrotherEncounter.Phase1.OnEnter -= Phase1_OnEnter;
            On.EntityStates.Missions.BrotherEncounter.Phase2.OnEnter -= Phase2_OnEnter;
            On.RoR2.Stage.Start -= OnStageStart;
        }

        // ------------------------------------------------------------------
        // Stage reset
        // ------------------------------------------------------------------

        private IEnumerator OnStageStart(On.RoR2.Stage.orig_Start orig, Stage self)
        {
            _cachedItems = null;
            _cachedReasoning = null;
            _cachedSurvivor = null;
            _adversaryMaster = null;
            _mithrixMaster = null;
            _capturedMithrixHp = 0f;
            _adversaryAlive = false;
            _heraldActive = false;
            _spawnedThisStage = false;
            return orig(self);
        }

        // ------------------------------------------------------------------
        // Cache from Python brain (called by SocketBridge / CounterbossController)
        // ------------------------------------------------------------------

        public void CacheCounterbuild(List<(string name, int count)> items, string reasoning, string survivor = null)
        {
            _cachedItems = items;
            _cachedReasoning = reasoning;
            _cachedSurvivor = survivor;
            Log($"[Herald] Counterbuild cached: {items.Count} item types, survivor={survivor ?? "default"}");
        }

        // ------------------------------------------------------------------
        // Phase 1 hook — intercept when Mithrix spawns
        // ------------------------------------------------------------------

        private void Phase1_OnEnter(On.EntityStates.Missions.BrotherEncounter.Phase1.orig_OnEnter orig,
            EntityStates.Missions.BrotherEncounter.Phase1 self)
        {
            orig(self);

            if (!RainflayerPlugin.EnableCounterboss.Value) return;
            if (!RainflayerPlugin.EnableMithrixHerald.Value) return;
            if (!NetworkServer.active) return;

            // Grab the Phase1 ScriptedCombatEncounter from the base class field via reflection
            // (phaseScriptedCombatEncounter is protected, not public)
            ScriptedCombatEncounter encounter = GetPhaseEncounter(self);
            if (encounter == null)
            {
                LogError("[Herald] Could not find Phase1 ScriptedCombatEncounter — aborting");
                return;
            }

            _heraldActive = true;
            Log("[Herald] Phase1 entered — subscribing to combatSquad member events");

            // Server: intercept Mithrix to replace him with the herald adversary
            encounter.combatSquad.onMemberAddedServer += (master) => OnMithrixAddedToSquad(master, encounter.combatSquad);

            // All clients (including host): show item panel when survivor-type body joins the squad.
            // onMemberDiscovered fires client-side for every networked member added to the squad.
            encounter.combatSquad.onMemberDiscovered += (master) =>
            {
                var helper = master?.gameObject.GetComponent<AdversaryItemDisplayHelper>();
                if (helper == null)
                    helper = master?.gameObject.AddComponent<AdversaryItemDisplayHelper>();
                helper?.TryShowPanel(master);
            };
        }

        private void OnMithrixAddedToSquad(CharacterMaster master, CombatSquad phase1Squad)
        {
            if (!_heraldActive) return;

            CharacterBody body = master.GetBody();
            if (body == null)
            {
                // Body spawns one frame after master — retry next frame
                StartCoroutine(WaitForBodyThenIntercept(master, phase1Squad));
                return;
            }

            // Confirm this is actually Mithrix (BrotherBody)
            if (!body.name.Contains("Brother"))
            {
                Log($"[Herald] Non-Mithrix member added to Phase1 squad ({body.name}) — ignoring");
                return;
            }

            StartCoroutine(InterceptMithrixCoroutine(master, body, phase1Squad));
        }

        private IEnumerator WaitForBodyThenIntercept(CharacterMaster master, CombatSquad phase1Squad)
        {
            float waited = 0f;
            while (waited < 3f)
            {
                yield return new WaitForSeconds(0.05f);
                waited += 0.05f;
                CharacterBody body = master.GetBody();
                if (body != null)
                {
                    if (body.name.Contains("Brother"))
                        StartCoroutine(InterceptMithrixCoroutine(master, body, phase1Squad));
                    yield break;
                }
            }
            LogError("[Herald] Timed out waiting for Mithrix body");
        }

        private IEnumerator InterceptMithrixCoroutine(CharacterMaster mithrixMaster, CharacterBody mithrixBody, CombatSquad phase1Squad)
        {
            _mithrixMaster = mithrixMaster;

            Log("[Herald] Mithrix detected — killing Phase 1 instance and spawning herald adversary");

            // Capture Mithrix's HP as the herald's baseline.
            // Wait one fixed frame first so RecalculateStats has run and fullHealth is accurate.
            // Phase 2 will spawn a fresh Mithrix independently via its own ScriptedCombatEncounter.
            yield return new WaitForFixedUpdate();
            if (mithrixBody.healthComponent != null)
                _capturedMithrixHp = mithrixBody.healthComponent.fullHealth;
            Log($"[Herald] Captured Mithrix HP baseline: {_capturedMithrixHp:F0}");

            // Suppress Phase 1 Mithrix's item drop (he normally drops nothing, but belt-and-suspenders)
            var deathRewards = mithrixBody.GetComponent<DeathRewards>();
            if (deathRewards != null) deathRewards.goldReward = 0;

            // Force ThroneSpawnState so he strikes the "powering up" pose for a moment
            // before dying — purely cosmetic; he will die shortly after.
            EntityStateMachine bodyEsm = null;
            foreach (var esm in mithrixBody.GetComponents<EntityStateMachine>())
            {
                if (esm.customName == "Body")
                {
                    bodyEsm = esm;
                    break;
                }
            }
            if (bodyEsm != null)
            {
                EntityStates.BrotherMonster.ThroneSpawnState.duration = 1.5f;
                EntityStates.BrotherMonster.ThroneSpawnState.initialDelay = 0f;
                bodyEsm.SetNextState(new EntityStates.BrotherMonster.ThroneSpawnState());
            }

            // Brief dramatic pause — player sees Mithrix bow his head, then herald appears
            yield return new WaitForSeconds(1.5f);

            // Kill Phase 1 Mithrix — his removal from combatSquad happens automatically
            // via his OnDestroy callback registered in CombatSquad.AddMember.
            // We don't need to remove him manually.
            if (NetworkServer.active && mithrixBody.healthComponent != null && mithrixBody.healthComponent.alive)
                mithrixBody.healthComponent.Suicide();

            yield return new WaitForSeconds(0.3f);

            // Spawn the herald adversary
            yield return StartCoroutine(SpawnHeraldCoroutine(phase1Squad));
        }

        // ------------------------------------------------------------------
        // Spawn the adversary herald
        // ------------------------------------------------------------------

        private IEnumerator SpawnHeraldCoroutine(CombatSquad phase1Squad)
        {
            // Lock immediately — suppress any further LLM callbacks from post-spawn item pickups
            _spawnedThisStage = true;

            // Use cached LLM build or fall back to random
            List<(string name, int count)> items;
            string reasoning;
            string survivorOverride;

            if (_cachedItems != null && _cachedItems.Count > 0)
            {
                items = _cachedItems;
                reasoning = _cachedReasoning ?? "Herald counterbuild";
                survivorOverride = _cachedSurvivor;
                _cachedItems = null;
                _cachedReasoning = null;
                _cachedSurvivor = null;
                Log("[Herald] Using cached LLM build for herald");
            }
            else
            {
                items = BuildRandomItemList();
                reasoning = "Random herald build (no LLM cache)";
                survivorOverride = null;
                Log("[Herald] No cached build — using random");
            }

            // Spawn position: center of the Mithrix arena, slightly in front of throne
            Vector3 spawnPos = GetArenaSpawnPosition();

            string survivorName = !string.IsNullOrEmpty(survivorOverride) ? survivorOverride : "Commando";
            string survivorMasterName = survivorName + "MonsterMaster";
            GameObject masterPrefab = MasterCatalog.FindMasterPrefab(survivorMasterName);
            if (masterPrefab == null)
                masterPrefab = MasterCatalog.FindMasterPrefab(survivorName + "Master");
            if (masterPrefab == null)
            {
                masterPrefab = MasterCatalog.FindMasterPrefab("CommandoMonsterMaster");
                LogError($"[Herald] Master '{survivorMasterName}' not found, falling back to Commando");
            }
            if (masterPrefab == null)
            {
                LogError("[Herald] Could not find any master prefab — aborting");
                yield break;
            }

            GameObject masterObj = Instantiate(masterPrefab, spawnPos, Quaternion.identity);
            _adversaryMaster = masterObj.GetComponent<CharacterMaster>();
            if (_adversaryMaster == null)
            {
                LogError("[Herald] No CharacterMaster on prefab");
                Destroy(masterObj);
                yield break;
            }

            _adversaryMaster.teamIndex = TeamIndex.Monster;
            NetworkServer.Spawn(masterObj);
            _adversaryMaster.SpawnBody(spawnPos, Quaternion.identity);
            yield return null;

            CharacterBody adversaryBody = _adversaryMaster.GetBody();
            if (adversaryBody == null)
            {
                LogError("[Herald] Herald body failed to spawn");
                yield break;
            }

            if (adversaryBody.teamComponent != null)
                adversaryBody.teamComponent.teamIndex = TeamIndex.Monster;

            adversaryBody.bodyFlags |= CharacterBody.BodyFlags.IgnoreFallDamage;

            // HP: use captured Mithrix max HP as the baseline (already difficulty-scaled)
            if (_capturedMithrixHp > 0f)
            {
                adversaryBody.baseMaxHealth = _capturedMithrixHp * RainflayerPlugin.CounterbossHPMultiplier.Value;
                Log($"[Herald] Set herald HP to {adversaryBody.baseMaxHealth:F0} (Mithrix HP × multiplier)");
            }

            adversaryBody.baseDamage *= RainflayerPlugin.CounterbossDamageMultiplier.Value;
            adversaryBody.levelDamage *= RainflayerPlugin.CounterbossDamageMultiplier.Value;

            // 2× scale for visual boss feel
            if (adversaryBody.modelLocator?.modelTransform != null)
                adversaryBody.modelLocator.modelTransform.localScale *= 2f;

            // Give items before MarkAllStatsDirty so item-based max HP bonuses are included
            GiveItems(_adversaryMaster, items);

            adversaryBody.MarkAllStatsDirty();
            yield return null;
            if (adversaryBody.healthComponent != null)
                adversaryBody.healthComponent.Networkhealth = adversaryBody.healthComponent.fullHealth;

            // AI drivers
            SetupAdversaryAI(_adversaryMaster, adversaryBody);

            // Register with Phase1 combatSquad so phase completion triggers correctly
            RegisterWithPhase1Squad(_adversaryMaster, phase1Squad);

            _adversaryAlive = true;
            GlobalEventManager.onCharacterDeathGlobal += OnCharacterDeath;

            string displayName = adversaryBody.GetDisplayName();

            // Chat first so the player sees the herald announcement immediately
            BroadcastHeraldChat(reasoning, displayName);

            // HUD item panel — attach to the adversary master's GameObject, same as CounterbossController.
            // This ensures the component lives in the scene hierarchy (isActiveAndEnabled=true)
            // so ItemInventoryDisplay.UpdateDisplay() fires correctly.
            // Panel is destroyed when the master is cleaned up by RoR2 on death.
            var itemDisplay = _adversaryMaster.gameObject.AddComponent<AdversaryItemDisplay>();
            itemDisplay.Show(_adversaryMaster.inventory, displayName);

            Log($"[Herald] Herald spawned: {displayName} at {spawnPos}");
        }

        // ------------------------------------------------------------------
        // Phase 2 hook — unfreeze Mithrix
        // ------------------------------------------------------------------

        private void Phase2_OnEnter(On.EntityStates.Missions.BrotherEncounter.Phase2.orig_OnEnter orig,
            EntityStates.Missions.BrotherEncounter.Phase2 self)
        {
            orig(self);

            if (!_heraldActive) return;
            _heraldActive = false;

            // Phase 1 Mithrix was killed; Phase 2 spawns a fresh Mithrix via its own
            // ScriptedCombatEncounter — no unfreezing needed, just mark herald as done.
            Log("[Herald] Phase2 entered — herald phase complete, Phase 2 Mithrix will spawn normally");
        }

        // ------------------------------------------------------------------
        // Herald death — item steal + notify socket
        // ------------------------------------------------------------------

        private void OnCharacterDeath(DamageReport report)
        {
            if (!_adversaryAlive) return;
            if (report.victimMaster != _adversaryMaster) return;

            _adversaryAlive = false;
            GlobalEventManager.onCharacterDeathGlobal -= OnCharacterDeath;

            StealItemFromAdversary();
            _socketBridge?.SendCounterbossDiedEvent();
            _adversaryMaster = null;
        }

        private void StealItemFromAdversary()
        {
            try
            {
                CharacterMaster playerMaster = RainflayerPlugin.LocalPlayerMaster;
                if (playerMaster == null || _adversaryMaster?.inventory == null) return;

                var candidates = new List<ItemIndex>();
                foreach (var idx in _adversaryMaster.inventory.itemAcquisitionOrder)
                {
                    if (_adversaryMaster.inventory.GetItemCountPermanent(idx) <= 0) continue;
                    ItemDef def = ItemCatalog.GetItemDef(idx);
                    if (def == null) continue;
                    if (def.tier == ItemTier.Lunar || def.tier == ItemTier.VoidTier1 ||
                        def.tier == ItemTier.VoidTier2 || def.tier == ItemTier.VoidTier3 ||
                        def.tier == ItemTier.VoidBoss) continue;
                    candidates.Add(idx);
                }

                if (candidates.Count == 0) { Log("[Herald] No stealable items on herald"); return; }

                ItemIndex stolen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                _adversaryMaster.inventory.RemoveItem(stolen, 1);
                playerMaster.inventory.GiveItem(stolen, 1);

                string displayName = GetItemDisplayName(ItemCatalog.GetItemDef(stolen)?.name ?? "Unknown");
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = $"<color=#ffcc00>[STOLEN]</color> You claimed <color=#aaffaa>{displayName}</color> from the herald!"
                });
                Log($"[Herald] Stole {displayName} → player");
            }
            catch (Exception e) { LogError($"[Herald] StealItemFromAdversary failed: {e.Message}"); }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private Vector3 GetArenaSpawnPosition()
        {
            // Mithrix arena is at roughly Y=491 in Commencement.
            // Spawn the herald in front of the throne, facing the player entry point.
            CharacterBody playerBody = RainflayerPlugin.GetPlayerBody();
            if (playerBody != null)
                return playerBody.transform.position + (playerBody.transform.forward * 20f) + Vector3.up * 1f;

            // Fallback: fixed arena position
            return new Vector3(0f, 492f, 15f);
        }

        private void SetupAdversaryAI(CharacterMaster master, CharacterBody body)
        {
            try
            {
                BaseAI ai = master.GetComponent<BaseAI>();
                if (ai == null)
                    ai = master.gameObject.AddComponent<PlayerAI>() as BaseAI;

                AdversaryAI.Inject(body.name, master.gameObject, ai);

                CharacterBody playerBody = RainflayerPlugin.GetPlayerBody();
                if (playerBody != null && ai.currentEnemy != null)
                    ai.currentEnemy.gameObject = playerBody.gameObject;

                Log("[Herald] AI configured via AdversaryAI.Inject");
            }
            catch (Exception e) { LogError($"[Herald] SetupAdversaryAI failed: {e.Message}"); }
        }

        private void RegisterWithPhase1Squad(CharacterMaster master, CombatSquad phase1Squad)
        {
            try
            {
                // BossNamePriority makes the herald name show in the boss bar
                BossNamePriority priority = master.gameObject.GetComponent<BossNamePriority>();
                if (priority == null)
                    priority = master.gameObject.AddComponent<BossNamePriority>();
                priority.priority = 10;

                phase1Squad.AddMember(master);
                Log("[Herald] Registered with Phase1 combatSquad");
            }
            catch (Exception e) { LogError($"[Herald] RegisterWithPhase1Squad failed: {e.Message}"); }
        }

        private List<(string name, int count)> BuildRandomItemList()
        {
            CharacterBody playerBody = RainflayerPlugin.GetPlayerBody();
            int targetCount = 0;
            if (playerBody?.inventory != null)
                foreach (var idx in playerBody.inventory.itemAcquisitionOrder)
                    targetCount += playerBody.inventory.GetItemCountPermanent(idx);

            if (targetCount <= 0) return new List<(string, int)>();

            var pool = new List<PickupIndex>();
            if (Run.instance != null)
            {
                pool.AddRange(Run.instance.availableTier1DropList);
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

        private void GiveItems(CharacterMaster master, List<(string name, int count)> items)
        {
            if (master?.inventory == null) return;
            int given = 0;
            foreach (var (itemName, count) in items)
            {
                ItemIndex idx = ItemCatalog.FindItemIndex(itemName);
                if (idx == ItemIndex.None) idx = TryFindItemByPartialName(itemName);
                if (idx == ItemIndex.None) { Log($"[Herald] Unknown item '{itemName}' — skipping"); continue; }
                master.inventory.GiveItem(idx, count);
                given += count;
            }
            Log($"[Herald] Gave herald {given} total items");
        }

        private ItemIndex TryFindItemByPartialName(string partial)
        {
            string lower = partial.ToLower();
            foreach (var idx in ItemCatalog.allItems)
            {
                ItemDef def = ItemCatalog.GetItemDef(idx);
                if (def?.name.ToLower().Contains(lower) == true) return idx;
            }
            return ItemIndex.None;
        }

        private string GetItemDisplayName(string internalName)
        {
            ItemIndex idx = ItemCatalog.FindItemIndex(internalName);
            if (idx == ItemIndex.None) idx = TryFindItemByPartialName(internalName);
            if (idx == ItemIndex.None) return internalName;
            ItemDef def = ItemCatalog.GetItemDef(idx);
            if (def == null) return internalName;
            string display = Language.GetString(def.nameToken);
            return string.IsNullOrEmpty(display) ? internalName : display;
        }

        private void BroadcastHeraldChat(string reasoning, string heraldName)
        {
            try
            {
                if (!NetworkServer.active) return;
                Log($"[Herald] REASONING: {reasoning}");

                string safe = reasoning.Replace("<", "[").Replace(">", "]");
                string truncated = safe.Length > 200 ? safe.Substring(0, 197) + "..." : safe;

                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = $"<color=#ff4040>[HERALD]</color> {heraldName}: {truncated}"
                });
            }
            catch (Exception e) { LogError($"[Herald] BroadcastHeraldChat failed: {e.Message}"); }
        }

        /// <summary>
        /// Reads the protected phaseScriptedCombatEncounter field from BrotherEncounterPhaseBaseState
        /// via reflection, since it's not exposed publicly.
        /// </summary>
        private static ScriptedCombatEncounter GetPhaseEncounter(EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState state)
        {
            try
            {
                var field = typeof(EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState)
                    .GetField("phaseScriptedCombatEncounter",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                return field?.GetValue(state) as ScriptedCombatEncounter;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Herald] Reflection failed for phaseScriptedCombatEncounter: {e.Message}");
                return null;
            }
        }

        private void Log(string msg) => RainflayerPlugin.Instance?.Log(msg);
        private void LogError(string msg) => RainflayerPlugin.Instance?.LogError(msg);
    }
}
