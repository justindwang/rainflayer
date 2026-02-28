"""Brain Controller - Strategic decision-making loop for RoR2.

Queries game state from the mod every few seconds and issues high-level
commands. Supports two decision modes:

  Phase 1 (heuristic): rule-based fallback, no LLM required
  Phase 2 (LLM):       Llama 4 Maverick 17B via Novita.ai

Commands follow the same set exposed in prompts.py:
    STRATEGY:aggressive|defensive|balanced|support
    MODE:roam|combat|follow|wait
    FIND_AND_INTERACT:chest|shrine|teleporter
    GOTO:CANCEL
    BUY_SHOP_ITEM:<item_name>
"""
import asyncio
import logging
import time
from typing import Optional, TYPE_CHECKING
from datetime import datetime

from .bridge import Bridge
from .game_state import RoR2GameState

if TYPE_CHECKING:
    from .model import RoR2BrainModel

logger = logging.getLogger(__name__)


def ts() -> str:
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


class RoR2BrainController:
    """
    Connects Brain strategic AI to the RoR2 mod.

    Brain loop (~0.25 Hz by default):
    1. Query full game state (inventory, interactables, allies, objective, combat)
    2. Drain async events from C# (update action ledger)
    3. Generate strategic decision (LLM or heuristic)
    4. Execute decision (send commands via Bridge)
    """

    def __init__(
        self,
        bridge: Bridge,
        brain_model: Optional["RoR2BrainModel"] = None,
        update_interval: float = 4.0,
    ):
        self.bridge = bridge
        self.brain_model = brain_model
        self.update_interval = update_interval

        self.game_state = RoR2GameState()
        self.running = False
        self.iteration_count = 0
        self.last_commands: list[str] = []
        self.last_decision = None

        # Action ledger: tracks commands and their outcomes from C# events
        self.action_ledger: list[dict] = []
        self.max_ledger_entries = 10
        self.last_seen_interactables: set = set()
        self._prev_boss_active = False

        # User directive (injected externally via directive REPL or tool)
        self.user_directive: Optional[str] = None
        self.directive_timestamp: Optional[float] = None
        self.directive_type: str = "strategic"
        self.directive_ttl: float = 600.0

        # Optional callback for Soul-layer integration
        self.on_decision_callback: Optional[callable] = None

        logger.info(f"[{ts()}] [Brain] Initialized — interval={update_interval}s, "
                    f"LLM={'yes' if brain_model else 'no (heuristics)'}")

    async def start(self):
        if self.running:
            return
        self.running = True
        logger.info(f"[{ts()}] [Brain] Starting decision loop")

        # Set initial posture
        self.bridge.set_strategy("balanced")
        self.bridge.set_mode("roam")

        asyncio.create_task(self._run_decision_loop())

    async def stop(self):
        self.running = False
        logger.info(f"[{ts()}] [Brain] Stopped")

    # ------------------------------------------------------------------
    # Main loop
    # ------------------------------------------------------------------

    async def _run_decision_loop(self):
        try:
            while self.running:
                try:
                    t0 = asyncio.get_event_loop().time()

                    # Skip everything when disconnected (avoids spamming failed queries)
                    if not self.bridge.is_connected():
                        logger.debug(f"[{ts()}] [Brain] Bridge not connected — waiting for reconnect")
                        await asyncio.sleep(self.update_interval)
                        continue

                    # Query game state
                    interactables = self.bridge.query_interactables()
                    allies = self.bridge.query_allies()
                    objective = self.bridge.query_objective()
                    combat_status = self.bridge.query_combat_status()
                    inventory = self.bridge.query_inventory()

                    # Update state
                    self.game_state.update_from_interactables(interactables)
                    self.game_state.update_from_allies(allies)
                    if objective:
                        self.game_state.update_from_objective(objective)
                    if combat_status:
                        self.game_state.update_from_combat_status(combat_status)
                    if inventory:
                        self.game_state.update_from_inventory(inventory)

                    # Skip decisions when not in an active run (lobby, loading, dead/respawn)
                    if combat_status and not combat_status.in_game:
                        logger.debug(f"[{ts()}] [Brain] Not in active run — skipping decision")
                        await asyncio.sleep(self.update_interval)
                        continue

                    if self.iteration_count % 2 == 0:
                        logger.info(f"[{ts()}] [Brain] HP={self.game_state.health_percent:.0f}% "
                                    f"gold={self.game_state.money} "
                                    f"enemies={self.game_state.num_enemies} "
                                    f"tp={self.game_state.teleporter_charge:.0f}%")

                    # Drain C# events and update ledger
                    for event in self.bridge.poll_events():
                        self._process_event(event)

                    self._check_action_completion(interactables)
                    self._try_auto_clear_directive()

                    # Generate and execute decision
                    decision = await self._generate_decision()
                    await self._execute_decision(decision)

                    elapsed = (asyncio.get_event_loop().time() - t0) * 1000
                    self.iteration_count += 1

                    if self.iteration_count % 5 == 0:
                        logger.info(f"[{ts()}] [Brain] Iteration {self.iteration_count} ({elapsed:.0f}ms)")

                except Exception as e:
                    logger.error(f"[{ts()}] [Brain] Loop error: {e}", exc_info=True)

                await asyncio.sleep(self.update_interval)

        except asyncio.CancelledError:
            raise

    # ------------------------------------------------------------------
    # Decision generation
    # ------------------------------------------------------------------

    async def _generate_decision(self) -> dict:
        if self.brain_model is not None:
            return await self._generate_llm_decision()
        return await self._generate_heuristic_decision()

    async def _generate_llm_decision(self) -> dict:
        from .model import RoR2BrainInput
        try:
            game_state = self.game_state.get_summary()
            ledger_summary = self._get_ledger_summary()
            full_summary = game_state + ("\n" + ledger_summary if ledger_summary else "")

            user_directive = self.user_directive if self._is_directive_valid() else None

            brain_input = RoR2BrainInput(
                game_state_summary=full_summary,
                user_directive=user_directive,
                last_commands=self.last_commands
            )

            brain_output = await self.brain_model.generate_commands(brain_input)

            self.last_commands.extend(brain_output.commands)
            if len(self.last_commands) > 10:
                self.last_commands = self.last_commands[-10:]

            if self.on_decision_callback:
                try:
                    await self.on_decision_callback(brain_output)
                except Exception as e:
                    logger.error(f"[{ts()}] [Brain] Decision callback error: {e}")

            self.last_decision = brain_output

            return {"commands": brain_output.commands, "reasoning": brain_output.reasoning, "source": "llm"}

        except Exception as e:
            logger.error(f"[{ts()}] [Brain] LLM failed: {e} — falling back to heuristics")
            return await self._generate_heuristic_decision()

    async def _generate_heuristic_decision(self) -> dict:
        """Rule-based fallback (no LLM required). Uses same command set as LLM."""
        commands = []
        reasoning = ""

        # Priority 1: Survival
        if self.game_state.is_critical_health():
            commands = ["STRATEGY:defensive", "GOTO:CANCEL", "MODE:roam"]
            reasoning = f"Critical health ({self.game_state.health_percent:.0f}%) — retreating"
            return {"commands": commands, "reasoning": reasoning, "source": "heuristic"}

        # Priority 2: Activate teleporter once charged
        if self.game_state.teleporter_charged:
            commands = ["FIND_AND_INTERACT:teleporter"]
            reasoning = "Teleporter charged — activating for next stage"
            return {"commands": commands, "reasoning": reasoning, "source": "heuristic"}

        # Priority 3: Defend while teleporter is charging
        if self.game_state.teleporter_charge > 0:
            commands = ["MODE:combat", "STRATEGY:balanced"]
            reasoning = f"Teleporter charging ({self.game_state.teleporter_charge:.0f}%) — defending"
            return {"commands": commands, "reasoning": reasoning, "source": "heuristic"}

        # Priority 4: Loot when safe and affordable
        if not self.game_state.in_combat and self.game_state.money > 0:
            chest = self.game_state.get_nearest_chest()
            if chest:
                commands = ["FIND_AND_INTERACT:chest", "MODE:combat"]
                reasoning = f"Opening chest (cost: {chest.cost})"
                return {"commands": commands, "reasoning": reasoning, "source": "heuristic"}

        # Priority 5: Boss engagement
        if self.game_state.boss_active and self.game_state.health_percent > 60:
            commands = ["STRATEGY:aggressive", "MODE:combat"]
            reasoning = "Boss active — engaging aggressively"
            return {"commands": commands, "reasoning": reasoning, "source": "heuristic"}

        # Default: Exploration
        if self.game_state.should_be_defensive():
            strategy, reasoning = "STRATEGY:defensive", "Defensive exploration"
        elif self.game_state.should_be_aggressive():
            strategy, reasoning = "STRATEGY:aggressive", "Aggressive exploration"
        else:
            strategy, reasoning = "STRATEGY:balanced", "Balanced exploration"

        commands = [strategy, "MODE:roam"]
        reasoning += " — roaming and engaging enemies"
        return {"commands": commands, "reasoning": reasoning, "source": "heuristic"}

    # ------------------------------------------------------------------
    # Decision execution
    # ------------------------------------------------------------------

    async def _execute_decision(self, decision: dict):
        commands = decision.get("commands", [])
        reasoning = decision.get("reasoning", "")
        source = decision.get("source", "unknown")

        if reasoning and (self.iteration_count % 5 == 0 or "teleporter" in reasoning.lower()):
            logger.info(f"[{ts()}] [Brain] ({source}) {reasoning}")

        # Add synchronous commands to ledger immediately; FIND_AND_INTERACT
        # gets added when C# fires action_started (confirms target found)
        for cmd_str in commands:
            if not cmd_str.startswith("FIND_AND_INTERACT"):
                self._add_to_ledger(cmd_str, "pending")

        for cmd_str in commands:
            parts = cmd_str.split(':', 1)
            if len(parts) != 2:
                continue
            cmd, args = parts[0].upper(), parts[1]
            args_lower = args.lower()

            if cmd == "STRATEGY":
                if args_lower != self.game_state.current_strategy:
                    logger.info(f"[{ts()}] [Brain] STRATEGY {self.game_state.current_strategy} → {args_lower}")
                    self.bridge.set_strategy(args_lower)
                    self.game_state.current_strategy = args_lower

            elif cmd == "MODE":
                if args_lower != self.game_state.current_mode:
                    logger.info(f"[{ts()}] [Brain] MODE {self.game_state.current_mode} → {args_lower}")
                    self.bridge.set_mode(args_lower)
                    self.game_state.current_mode = args_lower

            elif cmd == "FIND_AND_INTERACT":
                self.bridge.send_command("FIND_AND_INTERACT", args_lower)

            elif cmd == "GOTO" and args_lower == "cancel":
                self.bridge.goto_cancel()

            elif cmd == "BUY_SHOP_ITEM":
                self.bridge.send_command("BUY_SHOP_ITEM", args)

    # ------------------------------------------------------------------
    # Action ledger
    # ------------------------------------------------------------------

    def _add_to_ledger(self, command: str, status: str = "pending", reason: str = ""):
        self.action_ledger.append({"command": command, "status": status, "reason": reason})
        if len(self.action_ledger) > self.max_ledger_entries:
            self.action_ledger.pop(0)

    def _process_event(self, event: dict):
        """Update action ledger from async C# events."""
        event_name = event.get("event_type", "")
        if not event_name:
            return

        event_data = {k: v for k, v in event.items() if k not in ("type", "event_type")}

        if event_name == "action_started":
            command = event_data.get("command", "")
            if command:
                reason = f"found {event_data.get('target', '?')} at {event_data.get('distance', '?')}m"
                # Deduplicate: if this command is already pending, just refresh the reason
                # (avoids filling the ledger with 5x "FIND_AND_INTERACT:teleporter → pending")
                existing = next(
                    (a for a in self.action_ledger if a["command"] == command and a["status"] == "pending"),
                    None
                )
                if existing:
                    existing["reason"] = reason
                else:
                    self._add_to_ledger(command, "pending", reason=reason)
                logger.info(f"[{ts()}] [Brain] action_started: {command}")

        elif event_name == "action_complete":
            command = event_data.get("command", "")
            status = event_data.get("status", "success")
            if command:
                for action in self.action_ledger:
                    if action["command"] == command and action["status"] in ("pending", "interrupted"):
                        action["status"] = status
                        action["reason"] = "completed"
                        logger.info(f"[{ts()}] [Brain] action_complete: {command} → {status}")
                        break

        elif event_name == "action_failed":
            command = event_data.get("command", "")
            reason = event_data.get("reason", "unknown")
            if command:
                updated = False
                for action in self.action_ledger:
                    if action["command"] == command and action["status"] == "pending":
                        action["status"] = "failed"
                        action["reason"] = reason
                        updated = True
                        break
                if not updated:
                    self._add_to_ledger(command, "failed", reason=reason)
                logger.info(f"[{ts()}] [Brain] action_failed: {command} — {reason}")

        elif event_name == "stuck":
            for action in self.action_ledger:
                if action["status"] == "pending" and (
                    action["command"].startswith("FIND_AND_INTERACT") or
                    action["command"].startswith("GOTO:")
                ):
                    action["status"] = "failed"
                    action["reason"] = "stuck"

        elif event_name == "combat_entered":
            for action in self.action_ledger:
                if action["status"] == "pending" and action["command"].startswith("FIND_AND_INTERACT"):
                    # Teleporter navigation is a stage objective - it persists through combat.
                    # Only interrupt loot-type actions (chests, shrines, shops).
                    if "teleporter" in action["command"].lower():
                        continue
                    action["status"] = "interrupted"
                    action["reason"] = "combat"
                    logger.info(f"[{ts()}] [Brain] interrupted: {action['command']}")

        elif event_name == "low_health":
            logger.warning(f"[{ts()}] [Brain] Low health event received")

    def _check_action_completion(self, current_interactables):
        """Mark pending FIND_AND_INTERACT actions successful if the target disappeared."""
        current_ids = set((i.type, i.name) for i in current_interactables) if current_interactables else set()
        disappeared = self.last_seen_interactables - current_ids

        for inter_type, inter_name in disappeared:
            for action in self.action_ledger:
                if action["status"] in ("pending", "interrupted") and \
                        action["command"].startswith(f"FIND_AND_INTERACT:{inter_type}"):
                    action["status"] = "success"
                    action["reason"] = f"{inter_name} opened"
                    logger.info(f"[{ts()}] [Brain] success: {action['command']} ({inter_name} opened)")

        self.last_seen_interactables = current_ids

        # Teleporter activation: boss_active flipping True means teleporter was activated
        boss_now = self.game_state.boss_active
        if boss_now and not self._prev_boss_active:
            for action in self.action_ledger:
                if action["status"] in ("pending", "interrupted") and "teleporter" in action["command"].lower():
                    action["status"] = "success"
                    action["reason"] = "teleporter activated (boss spawned)"
                    logger.info(f"[{ts()}] [Brain] teleporter activation confirmed")
        self._prev_boss_active = boss_now

    def _get_ledger_summary(self) -> str:
        if not self.action_ledger:
            return ""
        lines = ["Recent Action History:"]
        for action in self.action_ledger[-5:]:
            status_str = action["status"]
            if action["reason"]:
                status_str += f" ({action['reason']})"
            lines.append(f"• {action['command']} → {status_str}")
        return "\n".join(lines)

    # ------------------------------------------------------------------
    # User directive
    # ------------------------------------------------------------------

    def set_user_directive(self, directive: str):
        """Inject a directive from the REPL or external tool."""
        self.user_directive = directive
        self.directive_timestamp = time.time()
        self.directive_type = self._classify_directive(directive)
        self.directive_ttl = 45.0 if self.directive_type == "tactical" else 600.0
        logger.info(f"[{ts()}] [Brain] Directive set ({self.directive_type}): {directive}")

    def _classify_directive(self, directive: str) -> str:
        tactical_keywords = ["go to", "find", "use", "open", "interact", "buy",
                             "teleporter", "chest", "shrine", "navigate", "get"]
        directive_lower = directive.lower()
        return "tactical" if any(kw in directive_lower for kw in tactical_keywords) else "strategic"

    def _is_directive_valid(self) -> bool:
        if self.user_directive is None or self.directive_timestamp is None:
            return False
        return (time.time() - self.directive_timestamp) < self.directive_ttl

    def _action_matches_directive(self, command: str) -> bool:
        if not self.user_directive:
            return False
        directive_lower = self.user_directive.lower()
        command_lower = command.lower()
        for kw in ["teleporter", "chest", "shrine", "shop"]:
            if kw in directive_lower and kw in command_lower:
                return True
        return False

    def _try_auto_clear_directive(self):
        """Auto-clear tactical directives when the action ledger shows success."""
        if self.directive_type != "tactical" or not self.user_directive:
            return
        for action in self.action_ledger[-5:]:
            if action["status"] == "success" and self._action_matches_directive(action["command"]):
                logger.info(f"[{ts()}] [Brain] Directive completed: '{self.user_directive}'")
                self.user_directive = None
                self.directive_timestamp = None
                return

    def set_decision_callback(self, callback: callable):
        """Register a callback to receive Brain decisions (e.g. for a personality layer)."""
        self.on_decision_callback = callback

    def get_game_state_summary(self) -> str:
        return self.game_state.get_summary()
