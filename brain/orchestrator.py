"""RoR2 Orchestrator - wires Brain, Bridge, and SocketBridge together.

Architecture:
    Brain (Python, ~0.25 Hz) ──commands──> RoR2 Mod ──execution──> Game
         ↑                                     │
         └──────────── game state ─────────────┘

The mod handles all fast tactical execution (movement, aim, skills at 50 Hz).
Python handles strategic decisions (what to do) and user directives.
"""
import asyncio
import logging
from typing import Optional, TYPE_CHECKING
from datetime import datetime

from .socket_bridge import start_socket_bridge, stop_socket_bridge
from .bridge import Bridge
from .brain_controller import RoR2BrainController
from .counterboss_model import CounterbossModel
from .counterboss_worker import CounterbossWorker
from .llm_backend import LLMBackend

if TYPE_CHECKING:
    from .model import RoR2BrainModel

logger = logging.getLogger(__name__)


def ts() -> str:
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


class RoR2Orchestrator:
    """
    Main entry point for the RoR2 brain server.

    Usage:
        orchestrator = RoR2Orchestrator(brain_model=model)
        await orchestrator.start()
        # inject directives via orchestrator.brain_controller.set_user_directive(...)
        await orchestrator.stop()
    """

    def __init__(
        self,
        brain_model: Optional["RoR2BrainModel"] = None,
        brain_update_interval: float = 3.0,
        api_key: Optional[str] = None,
    ):
        self.brain_model = brain_model
        self.brain_update_interval = brain_update_interval

        # Start socket server (non-blocking — waits for C# mod to connect)
        logger.info(f"[{ts()}] [Orchestrator] Starting socket bridge on 127.0.0.1:7777...")
        socket_bridge = start_socket_bridge(blocking=False)

        # Bridge wraps the socket with typed query methods
        self.bridge = Bridge()

        # Brain controller owns the decision loop
        self.brain_controller = RoR2BrainController(
            bridge=self.bridge,
            brain_model=brain_model,
            update_interval=brain_update_interval,
        )

        # Counterboss worker — independent of brain loop, runs as its own asyncio task.
        # Uses COUNTERBOSS_MODEL env var if set, otherwise shares the brain's backend.
        # No-ops if C# never sends item_picked_up.
        self.counterboss_worker: Optional[CounterbossWorker] = None
        if api_key:
            # Brain backend (used by RoR2BrainModel passed in from main.py)
            brain_backend = LLMBackend.from_env("BRAIN", novita_key=api_key)
            # Counterboss backend — falls back to brain_backend if COUNTERBOSS_* not set
            counterboss_backend = LLMBackend.from_env("COUNTERBOSS", novita_key=api_key, fallback=brain_backend)
            counterboss_model = CounterbossModel(backend=counterboss_backend)
            self.counterboss_worker = CounterbossWorker(
                counterboss_model=counterboss_model,
                socket_bridge=socket_bridge,
            )
            # Hook the socket bridge so events are pushed directly to the worker
            socket_bridge._counterboss_callback = self.counterboss_worker.push_event
            logger.info(
                f"[{ts()}] [Orchestrator] CounterbossWorker initialized "
                f"(model={counterboss_backend.model})"
            )

        self.running = False
        self.tasks = []

        logger.info(f"[{ts()}] [Orchestrator] Ready — "
                    f"brain={'LLM' if brain_model else 'heuristics'}, "
                    f"interval={brain_update_interval}s, "
                    f"counterboss={'yes' if self.counterboss_worker else 'no'}")

    async def start(self):
        if self.running:
            return
        self.running = True
        print(f"[{ts()}] [Orchestrator] Starting — load into a RoR2 run to begin")
        self.tasks = [asyncio.create_task(self.brain_controller.start())]
        if self.counterboss_worker:
            self.tasks.append(asyncio.create_task(self.counterboss_worker.run()))

    async def stop(self):
        if not self.running:
            return
        self.running = False

        await self.brain_controller.stop()
        for task in self.tasks:
            task.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)

        stop_socket_bridge()
        print(f"[{ts()}] [Orchestrator] Stopped")

    def get_game_state_summary(self) -> str:
        return self.brain_controller.get_game_state_summary()
