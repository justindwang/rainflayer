"""Brain Model - Strategic planning via LLM (default: Llama 4 Maverick 17B on Novita.ai).

Takes a text game state summary from the mod and outputs JSON commands.
No screenshots needed — the mod provides all entity data.

Provider is configured via LLMBackend — see llm_backend.py for switching instructions.
"""
from __future__ import annotations

import json
import logging
import re
import time
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

from .llm_backend import LLMBackend
from .prompts import generate_ror2_brain_system_prompt

logger = logging.getLogger(__name__)


@dataclass
class RoR2BrainInput:
    """Input for the Brain model."""
    game_state_summary: str
    user_directive: Optional[str] = None
    last_commands: List[str] = None

    def __post_init__(self):
        if self.last_commands is None:
            self.last_commands = []


@dataclass
class RoR2BrainOutput:
    """Strategic commands from the Brain model."""
    timestamp_ms: int
    commands: List[str]
    reasoning: str
    generation_latency_ms: float = 0.0


class RoR2BrainModel:
    """
    Strategic planning for RoR2 via configurable LLM backend.

    Default: Llama 4 Maverick 17B on Novita.ai.
    Switch provider via BRAIN_MODEL / BRAIN_API_KEY env vars (see llm_backend.py).
    """

    def __init__(self, backend: LLMBackend):
        self.backend = backend
        self.system_prompt = generate_ror2_brain_system_prompt()
        logger.info(f"[BrainModel] Initialized with backend model={backend.model}")

    async def generate_commands(self, brain_input: RoR2BrainInput) -> RoR2BrainOutput:
        try:
            start_time = time.time()
            user_content = self._build_user_message(brain_input)
            response_text = await self.backend.complete(
                system=self.system_prompt,
                user=user_content,
                max_tokens=1024,
                temperature=0.3,
            )
            latency_ms = (time.time() - start_time) * 1000
            response_dict = self._parse_json_output(response_text)

            brain_output = RoR2BrainOutput(
                timestamp_ms=int(time.time() * 1000),
                commands=response_dict.get("commands", []),
                reasoning=response_dict.get("reasoning", ""),
                generation_latency_ms=latency_ms,
            )

            logger.info(f"[BrainModel] {len(brain_output.commands)} commands in {latency_ms:.0f}ms")
            logger.debug(f"[BrainModel] Commands: {brain_output.commands}")
            logger.debug(f"[BrainModel] Reasoning: {brain_output.reasoning}")
            return brain_output

        except Exception as e:
            logger.error(f"[BrainModel] Generation failed: {e}", exc_info=True)
            return RoR2BrainOutput(
                timestamp_ms=int(time.time() * 1000),
                commands=["STRATEGY:balanced", "MODE:roam"],
                reasoning="Error in brain generation — using safe fallback",
                generation_latency_ms=0,
            )

    def _build_user_message(self, brain_input: RoR2BrainInput) -> str:
        parts = [brain_input.game_state_summary]
        if brain_input.last_commands:
            parts.append("\nRecent commands sent to mod:\n" +
                         "\n".join(f"  - {cmd}" for cmd in brain_input.last_commands[-3:]))
        if brain_input.user_directive:
            parts.append(f"\nUser directive: {brain_input.user_directive}")
        return "\n".join(parts)

    def _parse_json_output(self, response_text: str) -> Dict[str, Any]:
        try:
            parsed = json.loads(response_text)
        except json.JSONDecodeError:
            match = re.search(r"\{.*\}", response_text, re.DOTALL)
            if match:
                try:
                    parsed = json.loads(match.group(0))
                except json.JSONDecodeError:
                    parsed = {}
            else:
                parsed = {}

        parsed.setdefault("commands", ["STRATEGY:balanced", "MODE:roam"])
        parsed.setdefault("reasoning", "No reasoning provided")
        return parsed
