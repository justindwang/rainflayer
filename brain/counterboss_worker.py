"""CounterbossWorker - standalone asyncio task for LLM counter-build generation.

Completely independent of the brain controller and its decision loop.
Listens for item_picked_up events, calls the LLM, and sends COUNTERBOSS_SPAWN
back to C# immediately when the result is ready.

The worker runs as its own asyncio.create_task() in the orchestrator.
It shares the TCP socket but has its own dedicated event queue, populated
by SocketBridge._reader_loop whenever an item_picked_up event arrives.
"""
from __future__ import annotations

import asyncio
import json
import logging
from collections import deque
from datetime import datetime
from typing import Any, Dict, List, Optional, TYPE_CHECKING

if TYPE_CHECKING:
    from .counterboss_model import CounterbossModel
    from .socket_bridge import SocketBridge

logger = logging.getLogger(__name__)


def ts() -> str:
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


class CounterbossWorker:
    """
    Standalone asyncio worker for LLM counter-build generation.

    Lifecycle:
      1. Receives item_picked_up / run_started events from SocketBridge via push_event()
      2. Fires an LLM call on each pickup (cancels any in-flight one for the same stage)
      3. Sends COUNTERBOSS_SPAWN to C# immediately when the LLM finishes
      4. C# caches the build; at teleporter time it uses whatever is cached
         with no TCP roundtrip required

    No dependency on brain_controller or its update interval.
    """

    def __init__(
        self,
        counterboss_model: "CounterbossModel",
        socket_bridge: "SocketBridge",
    ):
        self.model = counterboss_model
        self.socket = socket_bridge

        self._event_queue: asyncio.Queue = asyncio.Queue()
        self._cached_build: Optional[Dict[str, Any]] = None
        self._llm_task: Optional[asyncio.Task] = None
        self._running = False
        # Item catalog received from C# on run start: list of {name, displayName, tier, desc}
        self._item_catalog: Optional[List[Dict]] = None
        # Survivor catalog received from C# on run start: list of {name, displayName}
        self._survivor_catalog: Optional[List[Dict]] = None
        # Rolling ban-list: last 4 survivors used — culled from subselection before LLM sees it
        self._recent_survivors: deque = deque(maxlen=4)

        logger.info(f"[{ts()}] [CounterbossWorker] Initialized")

    def push_event(self, event: Dict[str, Any]):
        """
        Push a counterboss-relevant event into the worker's queue.
        Called from SocketBridge._reader_loop (background thread) — uses
        put_nowait which is thread-safe with asyncio queues when the event
        loop is running.
        """
        try:
            self._event_queue.put_nowait(event)
        except asyncio.QueueFull:
            pass

    async def run(self):
        """Main event loop — run as asyncio.create_task()."""
        self._running = True
        logger.info(f"[{ts()}] [CounterbossWorker] Started")

        while self._running:
            try:
                event = await asyncio.wait_for(self._event_queue.get(), timeout=1.0)
                await self._handle_event(event)
            except asyncio.TimeoutError:
                continue
            except asyncio.CancelledError:
                break
            except Exception as e:
                logger.error(f"[{ts()}] [CounterbossWorker] Event loop error: {e}", exc_info=True)

        logger.info(f"[{ts()}] [CounterbossWorker] Stopped")

    async def _handle_event(self, event: Dict[str, Any]):
        event_type = event.get("event_type", "")

        if event_type == "run_started":
            items = event.get("items", [])
            survivors = event.get("survivors", [])
            self._item_catalog = items
            self._survivor_catalog = survivors if survivors else None
            self._cached_build = None
            self._recent_survivors.clear()
            if self._llm_task and not self._llm_task.done():
                self._llm_task.cancel()
            t1 = sum(1 for i in items if i.get("tier") == "tier1")
            t2 = sum(1 for i in items if i.get("tier") == "tier2")
            t3 = sum(1 for i in items if i.get("tier") == "tier3")
            logger.info(f"[{ts()}] [CounterbossWorker] Catalog received: {t1} white, {t2} green, {t3} red, {len(survivors)} survivors")
            if items:
                sample = items[0]
                logger.info(f"[CounterbossWorker] Sample catalog entry: {sample}")

        elif event_type == "stage_started":
            # Pre-emptive LLM call at stage start so a build is ready before teleporter fires.
            # On stage 1 the player has 0 items → pick a random survivor (no items to counter yet).
            # On later stages the player already has items → generates a real counter.
            items = event.get("items", [])
            total = event.get("total_items", 0)
            player_survivor = event.get("player_survivor", "")
            player_survivor_display = event.get("player_survivor_display", "")
            self._cached_build = None  # invalidate previous stage's cache
            logger.info(
                f"[{ts()}] [CounterbossWorker] Stage started: {total} items, "
                f"survivor={player_survivor_display or player_survivor} — scheduling pre-emptive LLM call"
            )
            self._schedule_llm_stage_start(items, total, player_survivor, player_survivor_display)

        elif event_type == "item_picked_up":
            items = event.get("items", [])
            total = event.get("total_items", len(items))
            player_survivor = event.get("player_survivor", "")
            player_survivor_display = event.get("player_survivor_display", "")
            if not items:
                return
            logger.info(f"[{ts()}] [CounterbossWorker] Item pickup: {total} total items, survivor={player_survivor_display or player_survivor} — scheduling LLM call")
            self._schedule_llm(items, total, player_survivor, player_survivor_display)

    def _schedule_llm(self, items: List[Dict], total: int, player_survivor: str = "", player_survivor_display: str = ""):
        """Cancel any in-flight LLM task and start a fresh one."""
        if self._llm_task and not self._llm_task.done():
            self._llm_task.cancel()
        self._llm_task = asyncio.create_task(
            self._run_llm(items, total, self._survivor_catalog, player_survivor, player_survivor_display,
                          banned_survivors=list(self._recent_survivors))
        )

    def _schedule_llm_stage_start(self, items: List[Dict], total: int, player_survivor: str = "", player_survivor_display: str = ""):
        """Like _schedule_llm but always runs even with 0 items (picks random survivor for stage 1)."""
        if self._llm_task and not self._llm_task.done():
            self._llm_task.cancel()
        self._llm_task = asyncio.create_task(
            self._run_llm_stage_start(items, total, self._survivor_catalog, player_survivor, player_survivor_display,
                                      banned_survivors=list(self._recent_survivors))
        )

    async def _run_llm_stage_start(self, items: List[Dict], total: int, survivor_catalog: Optional[List[Dict]],
                                   player_survivor: str = "", player_survivor_display: str = "",
                                   banned_survivors: Optional[List[str]] = None):
        """Stage-start variant: when player has no items yet, pick a random survivor and send 0 items."""
        try:
            if not items:
                # Stage 1 (or skipped items): pick a random survivor from catalog, respecting ban-list
                survivor = None
                if survivor_catalog:
                    import random as _random
                    banned = set(banned_survivors or [])
                    available = [s for s in survivor_catalog if s["name"] not in banned]
                    if available:
                        pick = _random.choice(available)
                        survivor = pick["name"]
                build = {
                    "items": [],
                    "reasoning": f"Stage start — no player items yet. Adversary will be {survivor or 'random'}.",
                    "source": "random",
                    "survivor": survivor,
                }
                self._cached_build = build
                if survivor:
                    self._recent_survivors.append(survivor)
                logger.info(f"[{ts()}] [CounterbossWorker] Stage-start build (0 items): survivor={survivor}")
                await self._send_build(build)
            else:
                # Player already has items (stages 2+) — run a real LLM counter
                await self._run_llm(items, total, survivor_catalog, player_survivor, player_survivor_display,
                                    banned_survivors=banned_survivors)
        except asyncio.CancelledError:
            pass
        except Exception as e:
            logger.error(f"[{ts()}] [CounterbossWorker] Stage-start LLM call failed: {e}", exc_info=True)

    async def _run_llm(self, items: List[Dict], total: int, survivor_catalog: Optional[List[Dict]],
                       player_survivor: str = "", player_survivor_display: str = "",
                       banned_survivors: Optional[List[str]] = None):
        """Call the LLM, cache the result, and immediately send it to C#.

        Sending immediately after pickup means the build is already in C#'s
        cache by the time the teleporter fires — no TCP roundtrip at teleporter time.
        """
        try:
            result = await self.model.generate_counterbuild(
                items, total,
                item_catalog=self._item_catalog,
                survivor_catalog=survivor_catalog,
                player_survivor=player_survivor,
                player_survivor_display=player_survivor_display,
                banned_survivors=banned_survivors,
            )
            build = {
                "items": [{"name": n, "count": c} for n, c in result.items],
                "reasoning": result.reasoning,
                "source": result.source,
                "survivor": result.survivor,
            }
            self._cached_build = build
            # Record survivor for ban-list so the next stage picks a different one
            if result.survivor:
                self._recent_survivors.append(result.survivor)
            logger.info(
                f"[{ts()}] [CounterbossWorker] Build ready ({result.source}): "
                f"{len(result.items)} types — {result.reasoning[:60]}..."
            )
            # Push to C# cache now so it's ready before teleporter fires
            await self._send_build(build)
        except asyncio.CancelledError:
            pass
        except Exception as e:
            logger.error(f"[{ts()}] [CounterbossWorker] LLM call failed: {e}", exc_info=True)

    async def _send_build(self, build: Dict[str, Any]):
        """Send COUNTERBOSS_SPAWN to C# immediately after LLM completes.

        C# caches the build; at teleporter time it uses whatever is cached
        without any TCP roundtrip.
        """
        if not self.socket or not self.socket.is_connected():
            logger.error(f"[{ts()}] [CounterbossWorker] Socket not connected — cannot send build")
            return

        payload = json.dumps({
            "type": "COUNTERBOSS_SPAWN",
            "items": build["items"],
            "reasoning": build["reasoning"],
            "source": build.get("source", "llm"),
            "survivor": build.get("survivor"),
        }) + "\n"

        try:
            with self.socket._send_lock:
                self.socket.client_socket.sendall(payload.encode("utf-8"))
            logger.info(
                f"[{ts()}] [CounterbossWorker] Sent COUNTERBOSS_SPAWN to C# cache — "
                f"{len(build['items'])} item types, source={build.get('source')}"
            )
        except Exception as e:
            logger.error(f"[{ts()}] [CounterbossWorker] Failed to send COUNTERBOSS_SPAWN: {e}")

    def stop(self):
        self._running = False
        if self._llm_task and not self._llm_task.done():
            self._llm_task.cancel()
