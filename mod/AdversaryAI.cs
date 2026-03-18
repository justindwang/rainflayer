// AdversaryAI.cs — Skill driver injection for Counterboss adversary survivors.
//
// AI skill driver patterns adapted from RoR2-PlayerBots fork by Rampage45 of PlayerBots mod by Meledy (original creator) 
// Source: https://github.com/Rampage45/RoR2-PlayerBots
// License: MIT — see https://github.com/Rampage45/RoR2-PlayerBots/blob/master/LICENSE
//
// Modifications for Rainflayer:
//   - Removed PlayerBotController dependency (standalone, no bot manager).
//   - Removed CurrentLeader / group movement drivers (adversary targets the player only).
//   - Stripped leash drivers (adversary has no owner to return to).
//   - Merged AiSkillHelper base + per-survivor helpers into one file.
//   - Added AdversaryAI.Inject(bodyName, gameObject, ai) dispatcher used by
//     CounterbossController.SetupAdversaryAI().
//
// Covered survivors: Commando, Huntress, Bandit2, MUL-T, Engineer, Artificer,
//   Mercenary, REX, Loader, Acrid, Captain, Railgunner, VoidSurvivor (Void Fiend),
//   Heretic, Seeker, FalseSon, Drifter, Chef.
// Unknown survivors: fall back to DefaultSkillHelper (primary-only).

using RoR2;
using RoR2.CharacterAI;
using System.Collections.Generic;
using UnityEngine;

namespace Rainflayer
{
    /// <summary>
    /// Entry point: call Inject() from CounterbossController.SetupAdversaryAI()
    /// to wire up the best available skill drivers for the chosen survivor.
    /// </summary>
    internal static class AdversaryAI
    {
        // Map body prefab names → helper factory (lazily built once)
        private static Dictionary<string, System.Action<GameObject, BaseAI>> _helpers;

        private static void EnsureHelpers()
        {
            if (_helpers != null) return;
            _helpers = new Dictionary<string, System.Action<GameObject, BaseAI>>
            {
                // body name (as returned by CharacterBody.name, typically "XxxBody(Clone)")
                // We match on prefix so "(Clone)" suffix doesn't matter.
                { "CommandoBody",      (go, ai) => InjectCommando(go, ai)     },
                { "HuntressBody",      (go, ai) => InjectHuntress(go, ai)     },
                { "Bandit2Body",       (go, ai) => InjectBandit(go, ai)       },
                { "ToolbotBody",       (go, ai) => InjectToolbot(go, ai)      },
                { "EngiBody",          (go, ai) => InjectEngineer(go, ai)     },
                { "MageBody",          (go, ai) => InjectArtificer(go, ai)    },
                { "MercBody",          (go, ai) => InjectMercenary(go, ai)    },
                { "TreebotBody",       (go, ai) => InjectREX(go, ai)          },
                { "LoaderBody",        (go, ai) => InjectLoader(go, ai)       },
                { "CrocoBody",         (go, ai) => InjectAcrid(go, ai)        },
                { "CaptainBody",       (go, ai) => InjectCaptain(go, ai)      },
                { "RailgunnerBody",    (go, ai) => InjectRailgunner(go, ai)   },
                { "VoidSurvivorBody",  (go, ai) => InjectVoidSurvivor(go, ai) },
                { "HereticBody",       (go, ai) => InjectDefault(go, ai)      }, // Heretic uses unique skill system
                { "SeekerBody",        (go, ai) => InjectSeeker(go, ai)       },
                { "FalseSonBody",      (go, ai) => InjectFalseSon(go, ai)     },
                { "DrifterBody",       (go, ai) => InjectDrifter(go, ai)      },
                { "ChefBody",          (go, ai) => InjectChef(go, ai)         },
            };
        }

        /// <summary>
        /// Inject skill drivers and configure BaseAI for the adversary.
        /// Call once after the adversary body has spawned.
        /// </summary>
        public static void Inject(string bodyName, GameObject masterObj, BaseAI ai)
        {
            EnsureHelpers();

            // Clear any existing AISkillDrivers first (master prefab may have stale ones).
            // Must use DestroyImmediate — Object.Destroy is deferred to end-of-frame, so
            // stale drivers would still appear in GetComponents() at the end of this method
            // and get flushed back into BaseAI.skillDrivers alongside the new ones.
            foreach (var old in masterObj.GetComponents<AISkillDriver>())
                Object.DestroyImmediate(old);

            // Configure BaseAI parameters (from AiSkillHelper.OptimizeMovement)
            ai.aimVectorDampTime    = 0.0005f;
            ai.aimVectorMaxSpeed    = 18000f;
            ai.enemyAttentionDuration = 3f;
            ai.fullVision           = true;  // no LoS required for target acquisition

            // Find and run the survivor-specific helper; fall back to default
            System.Action<GameObject, BaseAI> helper = null;
            foreach (var kvp in _helpers)
            {
                if (bodyName.StartsWith(kvp.Key))
                {
                    helper = kvp.Value;
                    break;
                }
            }

            if (helper != null)
            {
                RainflayerPlugin.Instance?.Log($"[AdversaryAI] Using survivor-specific drivers for body='{bodyName}'");
                helper(masterObj, ai);
            }
            else
            {
                RainflayerPlugin.Instance?.Log($"[AdversaryAI] No specific helper for body='{bodyName}' — using default drivers");
                InjectDefault(masterObj, ai);
            }

            // Flush updated skill drivers into BaseAI via reflection (same pattern as vanilla PlayerBots)
            var drivers = masterObj.GetComponents<AISkillDriver>();
            RainflayerPlugin.Instance?.Log($"[AdversaryAI] Flushing {drivers.Length} skill drivers into BaseAI: {string.Join(", ", System.Array.ConvertAll(drivers, d => d.customName))}");
            var prop = typeof(BaseAI).GetProperty("skillDrivers",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);
            if (prop == null)
                RainflayerPlugin.Instance?.LogError("[AdversaryAI] Could not find BaseAI.skillDrivers property via reflection — drivers NOT flushed");
            else
                prop.SetValue(ai, drivers);
        }

        // -----------------------------------------------------------------------
        // Shared movement fallback — appended after every survivor's skill drivers.
        // Replaces PlayerBots' AddDefaultSkills but strips CurrentLeader drivers.
        // -----------------------------------------------------------------------

        private static void AddAdversaryMovementDrivers(GameObject go, float minChaseDistance)
        {
            // Chase enemy when out of primary range but target visible
            var chase = go.AddComponent<AISkillDriver>();
            chase.customName                      = "ChaseEnemy";
            chase.skillSlot                       = SkillSlot.None;
            chase.requireSkillReady               = false;
            chase.moveTargetType                  = AISkillDriver.TargetType.CurrentEnemy;
            chase.minDistance                     = minChaseDistance;
            chase.maxDistance                     = float.PositiveInfinity;
            chase.selectionRequiresTargetLoS      = true;
            chase.activationRequiresTargetLoS     = false;
            chase.activationRequiresAimConfirmation = false;
            chase.movementType                    = AISkillDriver.MovementType.ChaseMoveTarget;
            chase.aimType                         = AISkillDriver.AimType.AtMoveTarget;
            chase.shouldSprint                    = true;
            chase.driverUpdateTimerOverride        = 0.25f;

            // Wander when no target — keeps adversary mobile
            var wander = go.AddComponent<AISkillDriver>();
            wander.customName                     = "Wander";
            wander.skillSlot                      = SkillSlot.None;
            wander.requireSkillReady              = false;
            wander.moveTargetType                 = AISkillDriver.TargetType.NearestFriendlyInSkillRange;
            wander.minDistance                    = 0;
            wander.maxDistance                    = float.PositiveInfinity;
            wander.selectionRequiresTargetLoS     = false;
            wander.activationRequiresTargetLoS    = false;
            wander.activationRequiresAimConfirmation = false;
            wander.movementType                   = AISkillDriver.MovementType.ChaseMoveTarget;
            wander.aimType                        = AISkillDriver.AimType.AtMoveTarget;
            wander.shouldSprint                   = true;
            wander.driverUpdateTimerOverride       = 0.25f;
        }

        // -----------------------------------------------------------------------
        // Default — primary attack only (fallback for unknown / moddded survivors)
        // -----------------------------------------------------------------------
        private static void InjectDefault(GameObject go, BaseAI ai)
        {
            var skill1 = go.AddComponent<AISkillDriver>();
            skill1.customName                     = "Shoot";
            skill1.skillSlot                      = SkillSlot.Primary;
            skill1.requireSkillReady              = true;
            skill1.moveTargetType                 = AISkillDriver.TargetType.CurrentEnemy;
            skill1.minDistance                    = 0;
            skill1.maxDistance                    = 50;
            skill1.selectionRequiresTargetLoS     = true;
            skill1.activationRequiresTargetLoS    = true;
            skill1.activationRequiresAimConfirmation = true;
            skill1.movementType                   = AISkillDriver.MovementType.StrafeMovetarget;
            skill1.aimType                        = AISkillDriver.AimType.AtMoveTarget;
            skill1.shouldSprint                   = false;
            skill1.driverUpdateTimerOverride       = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Commando (CommandoBody)
        // -----------------------------------------------------------------------
        private static void InjectCommando(GameObject go, BaseAI ai)
        {
            // Utility: dash away when too close
            var util_flee = go.AddComponent<AISkillDriver>();
            util_flee.customName                  = "UtilityDefensive";
            util_flee.skillSlot                   = SkillSlot.Utility;
            util_flee.requireSkillReady           = true;
            util_flee.moveTargetType              = AISkillDriver.TargetType.CurrentEnemy;
            util_flee.minDistance                 = 0;  util_flee.maxDistance = 20;
            util_flee.selectionRequiresTargetLoS  = true;
            util_flee.activationRequiresTargetLoS = false;
            util_flee.activationRequiresAimConfirmation = false;
            util_flee.movementType                = AISkillDriver.MovementType.FleeMoveTarget;
            util_flee.aimType                     = AISkillDriver.AimType.MoveDirection;
            util_flee.shouldSprint                = true;
            util_flee.driverUpdateTimerOverride    = 0.25f;

            // Utility: dash toward enemy when far
            var util_chase = go.AddComponent<AISkillDriver>();
            util_chase.customName                 = "UtilityChase";
            util_chase.skillSlot                  = SkillSlot.Utility;
            util_chase.requireSkillReady          = true;
            util_chase.moveTargetType             = AISkillDriver.TargetType.CurrentEnemy;
            util_chase.minDistance                = 50; util_chase.maxDistance = 100;
            util_chase.selectionRequiresTargetLoS = true;
            util_chase.activationRequiresTargetLoS = true;
            util_chase.activationRequiresAimConfirmation = false;
            util_chase.movementType               = AISkillDriver.MovementType.ChaseMoveTarget;
            util_chase.aimType                    = AISkillDriver.AimType.MoveDirection;
            util_chase.shouldSprint               = true;
            util_chase.driverUpdateTimerOverride   = 0.25f;

            // Special: Suppressive Fire
            var special = go.AddComponent<AISkillDriver>();
            special.customName                    = "Special";
            special.skillSlot                     = SkillSlot.Special;
            special.requireSkillReady             = true;
            special.moveTargetType                = AISkillDriver.TargetType.CurrentEnemy;
            special.minDistance                   = 0; special.maxDistance = 35;
            special.selectionRequiresTargetLoS    = true;
            special.activationRequiresTargetLoS   = true;
            special.activationRequiresAimConfirmation = true;
            special.movementType                  = AISkillDriver.MovementType.StrafeMovetarget;
            special.aimType                       = AISkillDriver.AimType.AtMoveTarget;
            special.shouldSprint                  = false;
            special.driverUpdateTimerOverride      = 0.25f;

            // Secondary: Phase Round
            var sec = go.AddComponent<AISkillDriver>();
            sec.customName                        = "Secondary";
            sec.skillSlot                         = SkillSlot.Secondary;
            sec.requireSkillReady                 = true;
            sec.moveTargetType                    = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance                       = 0; sec.maxDistance = 40;
            sec.selectionRequiresTargetLoS        = true;
            sec.activationRequiresTargetLoS       = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType                      = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType                           = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint                      = false;
            sec.driverUpdateTimerOverride          = 0.25f;

            // Primary: Double Tap
            var prim = go.AddComponent<AISkillDriver>();
            prim.customName                       = "Shoot";
            prim.skillSlot                        = SkillSlot.Primary;
            prim.requireSkillReady                = true;
            prim.moveTargetType                   = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance                      = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS       = true;
            prim.activationRequiresTargetLoS      = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType                     = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType                          = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint                     = false;
            prim.driverUpdateTimerOverride         = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Huntress (HuntressBody)
        // -----------------------------------------------------------------------
        private static void InjectHuntress(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 20;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 90;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = true;
            spec.movementType = AISkillDriver.MovementType.Stop;
            spec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 50;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            prim.shouldSprint = true; prim.driverUpdateTimerOverride = 0.25f;

            spec.nextHighPriorityOverride = prim;
            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Bandit (Bandit2Body)
        // -----------------------------------------------------------------------
        private static void InjectBandit(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 80;
            util.maxUserHealthFraction = .4f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 5;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            sec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            sec.ignoreNodeGraph = true;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var util_alt = go.AddComponent<AISkillDriver>();
            util_alt.customName = "Utility Alt"; util_alt.skillSlot = SkillSlot.Utility;
            util_alt.requireSkillReady = true;
            util_alt.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util_alt.minDistance = 0; util_alt.maxDistance = 5;
            util_alt.selectionRequiresTargetLoS = true;
            util_alt.activationRequiresTargetLoS = false;
            util_alt.activationRequiresAimConfirmation = false;
            util_alt.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util_alt.aimType = AISkillDriver.AimType.MoveDirection;
            util_alt.shouldSprint = true; util_alt.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 60;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = true;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Shoot"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            prim.buttonPressType = AISkillDriver.ButtonPressType.TapContinuous;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 10);
        }

        // -----------------------------------------------------------------------
        // MUL-T (ToolbotBody)
        // -----------------------------------------------------------------------
        private static void InjectToolbot(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 30;
            util.maxUserHealthFraction = .5f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 40;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 30;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Engineer (EngiBody)
        // -----------------------------------------------------------------------
        private static void InjectEngineer(GameObject go, BaseAI ai)
        {
            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "DeployTurret"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 60;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.ignoreNodeGraph = true;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            // Utility: Shield targets CurrentEnemy (simpler than ally-targeting for adversary)
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "BubbleShield"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 20;
            util.maxUserHealthFraction = .5f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.noRepeat = true; util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "PlaceMine"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 25;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Shoot"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 40;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Artificer (MageBody)
        // -----------------------------------------------------------------------
        private static void InjectArtificer(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 50;
            util.maxTargetHealthFraction = .5f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = true;
            util.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            util.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            util.shouldSprint = false; util.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 60;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 15;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = true;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            spec.buttonPressType = AISkillDriver.ButtonPressType.Hold;
            spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Mercenary (MercBody)
        // -----------------------------------------------------------------------
        private static void InjectMercenary(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 10; util.maxDistance = 50;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            util.aimType = AISkillDriver.AimType.AtMoveTarget;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 25;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.shouldSprint = true; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 10;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = true; sec.driverUpdateTimerOverride = 0.25f;

            var chase = go.AddComponent<AISkillDriver>();
            chase.customName = "ChaseTarget"; chase.skillSlot = SkillSlot.None;
            chase.requireSkillReady = false;
            chase.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            chase.minDistance = 10; chase.maxDistance = 60;
            chase.selectionRequiresTargetLoS = true;
            chase.activationRequiresTargetLoS = true;
            chase.activationRequiresAimConfirmation = false;
            chase.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            chase.aimType = AISkillDriver.AimType.AtMoveTarget;
            chase.shouldSprint = true; chase.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 10;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = false;
            prim.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.ignoreNodeGraph = true;
            prim.shouldSprint = true; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 0);
        }

        // -----------------------------------------------------------------------
        // REX (TreebotBody)
        // -----------------------------------------------------------------------
        private static void InjectREX(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 40;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            util.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            util.shouldSprint = false; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 50;
            spec.maxUserHealthFraction = .9f;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = true;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 60;
            sec.minUserHealthFraction = .6f;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Shoot"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Loader (LoaderBody)
        // -----------------------------------------------------------------------
        private static void InjectLoader(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 40;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            util.aimType = AISkillDriver.AimType.AtMoveTarget;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 60;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.shouldSprint = true; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 15; sec.maxDistance = 80;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = true; sec.driverUpdateTimerOverride = 0.25f;

            var chase = go.AddComponent<AISkillDriver>();
            chase.customName = "ChaseTarget"; chase.skillSlot = SkillSlot.None;
            chase.requireSkillReady = false;
            chase.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            chase.minDistance = 10; chase.maxDistance = 60;
            chase.selectionRequiresTargetLoS = true;
            chase.activationRequiresTargetLoS = true;
            chase.activationRequiresAimConfirmation = false;
            chase.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            chase.aimType = AISkillDriver.AimType.AtMoveTarget;
            chase.shouldSprint = true; chase.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 10;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = false;
            prim.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.ignoreNodeGraph = true;
            prim.shouldSprint = true; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 0);
        }

        // -----------------------------------------------------------------------
        // Acrid (CrocoBody)
        // M1: Vicious Wounds (melee slash)  M2: Spit (ranged poison)  Util: Caustic Leap  Spec: Epidemic
        // -----------------------------------------------------------------------
        private static void InjectAcrid(GameObject go, BaseAI ai)
        {
            // Epidemic (Special) — long-range AoE, use off cooldown
            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 150f;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = true;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            spec.ignoreNodeGraph = true;
            spec.noRepeat = true;
            spec.shouldSprint = true;
            spec.buttonPressType = AISkillDriver.ButtonPressType.TapContinuous;

            // Caustic Leap (Utility) — gap closer, 0-30m
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0f; util.maxDistance = 30f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = true;
            util.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            util.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            util.ignoreNodeGraph = true;
            util.noRepeat = true;
            util.shouldSprint = true;
            util.buttonPressType = AISkillDriver.ButtonPressType.TapContinuous;

            // Spit (Secondary) — ranged poison projectile; minDistance=8 avoids firing at melee range
            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 8f; sec.maxDistance = 150f;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            sec.ignoreNodeGraph = false;
            sec.noRepeat = false;
            sec.shouldSprint = true;
            sec.buttonPressType = AISkillDriver.ButtonPressType.TapContinuous;

            // Vicious Wounds (Primary) — hold at point-blank; driverUpdateTimerOverride prevents re-eval spam
            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0f; prim.maxDistance = 5f;
            prim.selectionRequiresTargetLoS = false;
            prim.activationRequiresTargetLoS = false;
            prim.activationRequiresAimConfirmation = false;
            prim.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            prim.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            prim.ignoreNodeGraph = true;
            prim.noRepeat = false;
            prim.shouldSprint = false;
            prim.buttonPressType = AISkillDriver.ButtonPressType.Hold;
            prim.driverUpdateTimerOverride = 0.5f;

            // Chase-while-attacking driver (5-10m, closing into Vicious Wounds range)
            var primChase = go.AddComponent<AISkillDriver>();
            primChase.customName = "PrimaryChase"; primChase.skillSlot = SkillSlot.Primary;
            primChase.requireSkillReady = true;
            primChase.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            primChase.minDistance = 0f; primChase.maxDistance = 10f;
            primChase.selectionRequiresTargetLoS = false;
            primChase.activationRequiresTargetLoS = false;
            primChase.activationRequiresAimConfirmation = false;
            primChase.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            primChase.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            primChase.ignoreNodeGraph = true;
            primChase.noRepeat = false;
            primChase.shouldSprint = false;
            primChase.buttonPressType = AISkillDriver.ButtonPressType.Hold;
            primChase.driverUpdateTimerOverride = 0.5f;

            AddAdversaryMovementDrivers(go, 0);
        }

        // -----------------------------------------------------------------------
        // Captain (CaptainBody)
        // -----------------------------------------------------------------------
        private static void InjectCaptain(GameObject go, BaseAI ai)
        {
            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 50;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 20;
            util.maxUserHealthFraction = .4f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 30;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Railgunner (RailgunnerBody)
        // -----------------------------------------------------------------------
        private static void InjectRailgunner(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 20;
            util.maxUserHealthFraction = .5f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 80;
            spec.maxTargetHealthFraction = .5f;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = true;
            spec.movementType = AISkillDriver.MovementType.Stop;
            spec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 80;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.Stop;
            sec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 80;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Void Fiend (VoidSurvivorBody)
        // -----------------------------------------------------------------------
        private static void InjectVoidSurvivor(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 20;
            util.maxUserHealthFraction = .4f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 35;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 40;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Seeker (SeekerBody)
        // -----------------------------------------------------------------------
        private static void InjectSeeker(GameObject go, BaseAI ai)
        {
            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 30;
            spec.maxUserHealthFraction = .5f;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            spec.aimType = AISkillDriver.AimType.MoveDirection;
            spec.noRepeat = true; spec.shouldSprint = true; spec.driverUpdateTimerOverride = 0.25f;

            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 40;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            util.aimType = AISkillDriver.AimType.AtMoveTarget;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 50;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint = true; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // False Son (FalseSonBody)
        // -----------------------------------------------------------------------
        private static void InjectFalseSon(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 30;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            util.aimType = AISkillDriver.AimType.AtMoveTarget;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 60;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = true;
            spec.activationRequiresAimConfirmation = true;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 40;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtCurrentEnemy;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Drifter (DrifterBody)
        // -----------------------------------------------------------------------
        private static void InjectDrifter(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 20;
            util.maxUserHealthFraction = .4f;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = false;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.FleeMoveTarget;
            util.aimType = AISkillDriver.AimType.MoveDirection;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 40;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 40;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 50;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }

        // -----------------------------------------------------------------------
        // Chef (ChefBody)
        // -----------------------------------------------------------------------
        private static void InjectChef(GameObject go, BaseAI ai)
        {
            var util = go.AddComponent<AISkillDriver>();
            util.customName = "Utility"; util.skillSlot = SkillSlot.Utility;
            util.requireSkillReady = true;
            util.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            util.minDistance = 0; util.maxDistance = 25;
            util.selectionRequiresTargetLoS = true;
            util.activationRequiresTargetLoS = true;
            util.activationRequiresAimConfirmation = false;
            util.movementType = AISkillDriver.MovementType.ChaseMoveTarget;
            util.aimType = AISkillDriver.AimType.AtMoveTarget;
            util.shouldSprint = true; util.driverUpdateTimerOverride = 0.25f;

            var spec = go.AddComponent<AISkillDriver>();
            spec.customName = "Special"; spec.skillSlot = SkillSlot.Special;
            spec.requireSkillReady = true;
            spec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            spec.minDistance = 0; spec.maxDistance = 30;
            spec.selectionRequiresTargetLoS = true;
            spec.activationRequiresTargetLoS = false;
            spec.activationRequiresAimConfirmation = false;
            spec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            spec.aimType = AISkillDriver.AimType.AtMoveTarget;
            spec.noRepeat = true; spec.shouldSprint = false; spec.driverUpdateTimerOverride = 0.25f;

            var sec = go.AddComponent<AISkillDriver>();
            sec.customName = "Secondary"; sec.skillSlot = SkillSlot.Secondary;
            sec.requireSkillReady = true;
            sec.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            sec.minDistance = 0; sec.maxDistance = 20;
            sec.selectionRequiresTargetLoS = true;
            sec.activationRequiresTargetLoS = true;
            sec.activationRequiresAimConfirmation = true;
            sec.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            sec.aimType = AISkillDriver.AimType.AtMoveTarget;
            sec.shouldSprint = false; sec.driverUpdateTimerOverride = 0.25f;

            var prim = go.AddComponent<AISkillDriver>();
            prim.customName = "Primary"; prim.skillSlot = SkillSlot.Primary;
            prim.requireSkillReady = true;
            prim.moveTargetType = AISkillDriver.TargetType.CurrentEnemy;
            prim.minDistance = 0; prim.maxDistance = 30;
            prim.selectionRequiresTargetLoS = true;
            prim.activationRequiresTargetLoS = true;
            prim.activationRequiresAimConfirmation = true;
            prim.movementType = AISkillDriver.MovementType.StrafeMovetarget;
            prim.aimType = AISkillDriver.AimType.AtMoveTarget;
            prim.shouldSprint = false; prim.driverUpdateTimerOverride = 0.25f;

            AddAdversaryMovementDrivers(go, 20);
        }
    }
}
