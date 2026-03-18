using RoR2;
using RoR2.UI;
using TMPro;
using UnityEngine;
using System.Collections;

namespace Rainflayer
{
    /// <summary>
    /// Instantiates the game's own EnemyInfoPanel prefab into the HUD's RightInfoBar —
    /// identical to how the Void Fields panel appears, minus the monster body icons.
    ///
    /// The "Monsters' Items" header label is replaced with "[Survivor]'s Items".
    /// ItemInventoryDisplay is subscribed directly to the adversary's Inventory so
    /// icons appear immediately and update live.
    ///
    /// Call Show(inventory, survivorDisplayName) after GiveItems().
    /// Panel is destroyed via OnDestroy when this component is cleaned up by CounterbossController.
    ///
    /// Multiplayer: RegisterBossGroupHook() hooks BossGroup.UpdateBossMembers so every client
    /// that has the mod installed gets the panel when a survivor-type body appears in the
    /// teleporter's boss group — no cross-process signaling required.
    /// </summary>
    public class AdversaryItemDisplay : MonoBehaviour
    {
        // Shared prefab loaded once at plugin init — same approach as EnemyInfoPanel itself
        private static GameObject s_panelPrefab;

        private GameObject _panelInstance;

        // Called from RainflayerPlugin.Awake (or wherever plugin init happens).
        // Mirrors exactly how EnemyInfoPanel.Init() caches the prefab.
        public static void LoadPrefab()
        {
            LegacyResourcesAPI.LoadAsyncCallback<GameObject>("Prefabs/UI/EnemyInfoPanel", result =>
            {
                s_panelPrefab = result;
                RainflayerPlugin.Instance?.Log("[AdversaryItemDisplay] EnemyInfoPanel prefab loaded");
            });

            // Hook TeleporterInteraction charging — fires on all clients when the teleporter
            // activates. We subscribe to that specific squad's onMemberDiscovered so we catch
            // the adversary being added to the boss group.
            TeleporterInteraction.onTeleporterBeginChargingGlobal += OnTeleporterBeginCharging;
        }

        private static void OnTeleporterBeginCharging(TeleporterInteraction tp)
        {
            var squad = tp?.bossGroup?.combatSquad;
            if (squad == null) return;

            // Subscribe to this squad's instance event — fires on all clients when any master
            // joins, including the adversary added by the host's SpawnCoroutine.
            squad.onMemberDiscovered += OnSquadMemberDiscovered;
        }

        private static void OnSquadMemberDiscovered(CharacterMaster master)
        {
            if (master == null) return;

            var helper = master.gameObject.GetComponent<AdversaryItemDisplayHelper>();
            if (helper == null)
                helper = master.gameObject.AddComponent<AdversaryItemDisplayHelper>();
            helper.TryShowPanel(master);
        }

        private void OnDestroy()
        {
            if (_panelInstance != null)
                Destroy(_panelInstance);
        }

        public void Show(Inventory inventory, string survivorDisplayName)
        {
            if (inventory == null) return;
            StartCoroutine(ShowCoroutine(inventory, survivorDisplayName));
        }

        private IEnumerator ShowCoroutine(Inventory inventory, string survivorDisplayName)
        {
            var Log = RainflayerPlugin.Instance;

            // Wait for prefab if it hasn't loaded yet (async)
            float waited = 0f;
            while (s_panelPrefab == null && waited < 5f)
            {
                yield return new WaitForSeconds(0.1f);
                waited += 0.1f;
            }
            if (s_panelPrefab == null)
            {
                Log?.LogError("[AdversaryItemDisplay] EnemyInfoPanel prefab never loaded");
                yield break;
            }
            Log?.Log($"[AdversaryItemDisplay] Prefab ready. HUD count={HUD.readOnlyInstanceList.Count}");

            // Dump HUD state for diagnostics
            foreach (HUD hud in HUD.readOnlyInstanceList)
            {
                Log?.Log($"[AdversaryItemDisplay]   HUD={hud.name} gameModeUiInstance={(hud.gameModeUiInstance != null ? hud.gameModeUiInstance.name : "NULL")}");
                if (hud.gameModeUiInstance != null)
                {
                    var locator = hud.gameModeUiInstance.GetComponent<ChildLocator>();
                    Log?.Log($"[AdversaryItemDisplay]     ChildLocator={(locator != null ? "found" : "NULL")}");
                    if (locator != null)
                    {
                        Transform bar = locator.FindChild("RightInfoBar");
                        Log?.Log($"[AdversaryItemDisplay]     RightInfoBar={(bar != null ? bar.name : "NULL")}");
                    }
                }
            }

            // Wait for HUD and gameModeUiInstance to be ready
            waited = 0f;
            Transform rightInfoBar = null;
            while (rightInfoBar == null && waited < 5f)
            {
                rightInfoBar = FindRightInfoBar();
                if (rightInfoBar == null)
                {
                    yield return new WaitForSeconds(0.1f);
                    waited += 0.1f;
                }
            }
            if (rightInfoBar == null)
            {
                Log?.LogError("[AdversaryItemDisplay] RightInfoBar not found after 5s");
                yield break;
            }
            Log?.Log($"[AdversaryItemDisplay] RightInfoBar found: {rightInfoBar.name}, parent={rightInfoBar.parent?.name}");

            // Ensure the RightInfoBar and its ancestors are active — on moon2 the game
            // hides this branch since there's no teleporter UI, so we must re-enable it.
            Transform t = rightInfoBar;
            while (t != null)
            {
                if (!t.gameObject.activeSelf)
                {
                    Log?.Log($"[AdversaryItemDisplay] Activating inactive parent: {t.name}");
                    t.gameObject.SetActive(true);
                }
                t = t.parent;
            }

            // Instantiate the real EnemyInfoPanel prefab — same as SetDisplayingOnHud() in the base game
            _panelInstance = Instantiate(s_panelPrefab, rightInfoBar);
            _panelInstance.SetActive(true);
            Log?.Log($"[AdversaryItemDisplay] Panel instantiated: {_panelInstance.name}, active={_panelInstance.activeInHierarchy}");

            EnemyInfoPanel panel = _panelInstance.GetComponent<EnemyInfoPanel>();
            if (panel == null)
            {
                Log?.LogError("[AdversaryItemDisplay] No EnemyInfoPanel on prefab");
                Destroy(_panelInstance);
                _panelInstance = null;
                yield break;
            }

            Log?.Log($"[AdversaryItemDisplay] monsterBodiesContainer={(panel.monsterBodiesContainer != null ? "found" : "NULL")} inventoryContainer={(panel.inventoryContainer != null ? "found" : "NULL")} inventoryDisplay={(panel.inventoryDisplay != null ? "found" : "NULL")}");

            // Hide the monster body portraits — we only want the item grid
            if (panel.monsterBodiesContainer != null)
                panel.monsterBodiesContainer.SetActive(false);

            // Show the inventory container
            if (panel.inventoryContainer != null)
                panel.inventoryContainer.SetActive(true);

            // Replace the header label text if one exists in the prefab
            ReplaceHeaderText(_panelInstance, survivorDisplayName);

            // Wait one frame so the panel and inventoryDisplay are fully active/enabled
            // before subscribing — OnInventoryChanged guards on isActiveAndEnabled
            yield return null;

            Log?.Log($"[AdversaryItemDisplay] After yield: panel.activeInHierarchy={_panelInstance.activeInHierarchy} inventoryDisplay.isActiveAndEnabled={(panel.inventoryDisplay != null ? panel.inventoryDisplay.isActiveAndEnabled.ToString() : "N/A")} inventory.itemCount={inventory.itemAcquisitionOrder.Count}");

            // Subscribe to the adversary's inventory — live icon updates from here on
            if (panel.inventoryDisplay != null)
                panel.inventoryDisplay.SetSubscribedInventory(inventory);

            Log?.Log($"[AdversaryItemDisplay] SetSubscribedInventory done for {survivorDisplayName}");
        }

        /// <summary>
        /// Search the panel hierarchy for any TextMeshProUGUI that contains the word "Monster"
        /// (the baked "Monsters' Items" label) and replace it with our survivor-specific text.
        /// Falls back to replacing the first TMP label found if no "Monster" label exists.
        /// </summary>
        private static void ReplaceHeaderText(GameObject panelRoot, string survivorDisplayName)
        {
            string newLabel = string.IsNullOrEmpty(survivorDisplayName)
                ? "Adversary's Items"
                : $"{survivorDisplayName}'s Items";

            var labels = panelRoot.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true);
            TextMeshProUGUI target = null;

            foreach (var label in labels)
            {
                if (label.text.Contains("Monster") || label.text.Contains("Item"))
                {
                    target = label;
                    break;
                }
            }
            // Fallback: just take the first label
            if (target == null && labels.Length > 0)
                target = labels[0];

            if (target != null)
                target.text = newLabel;
        }

        private static Transform FindRightInfoBar()
        {
            foreach (HUD hud in HUD.readOnlyInstanceList)
            {
                if (hud.gameModeUiInstance == null) continue;
                var locator = hud.gameModeUiInstance.GetComponent<ChildLocator>();
                if (locator == null) continue;
                Transform bar = locator.FindChild("RightInfoBar");
                if (bar != null) return bar;
            }
            return null;
        }
    }

    /// <summary>
    /// Short-lived helper attached to the adversary master GO. Waits for the body to be
    /// ready, then shows the AdversaryItemDisplay panel on this client if the body is a
    /// survivor-type adversary. Destroys itself once done (or after a timeout).
    /// </summary>
    internal class AdversaryItemDisplayHelper : MonoBehaviour
    {
        public void TryShowPanel(CharacterMaster master)
        {
            StartCoroutine(WaitAndShow(master));
        }

        private IEnumerator WaitAndShow(CharacterMaster master)
        {
            // Wait up to 5s for the body to spawn
            float waited = 0f;
            CharacterBody body = null;
            while (waited < 5f)
            {
                body = master.GetBody();
                if (body != null) break;
                yield return new WaitForSeconds(0.1f);
                waited += 0.1f;
            }

            if (body == null)
            {
                Destroy(this);
                yield break;
            }

            // Vanilla teleporter bosses have no SurvivorDef — only show for survivor-type bodies
            if (SurvivorCatalog.FindSurvivorDefFromBody(body.gameObject) == null)
            {
                Destroy(this);
                yield break;
            }

            // Don't double-add: host already attached AdversaryItemDisplay to the master GO
            if (body.gameObject.GetComponent<AdversaryItemDisplay>() != null ||
                master.gameObject.GetComponent<AdversaryItemDisplay>() != null)
            {
                Destroy(this);
                yield break;
            }

            Inventory inv = master.inventory;
            if (inv == null) { Destroy(this); yield break; }

            string displayName = body.GetDisplayName();
            var display = body.gameObject.AddComponent<AdversaryItemDisplay>();
            display.Show(inv, displayName);
            RainflayerPlugin.Instance?.Log($"[AdversaryItemDisplay] Client panel shown for: {displayName}");
            Destroy(this);
        }
    }
}
