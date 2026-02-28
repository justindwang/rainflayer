#!/usr/bin/env python3
"""
rainflayer - RoR2 LLM Brain Server

Starts the Python brain server and connects to the Ror2GamerMode BepInEx mod.
The LLM (Llama 4 Maverick 17B) makes strategic decisions every few seconds.
Use this REPL to inject natural-language directives that influence the brain.

Requirements:
    - RoR2 running with Ror2GamerMode mod loaded
    - NOVITA_API_KEY set in .env or environment

Usage:
    python -m brain.main
    python -m brain.main --interval 5.0
"""
import asyncio
import logging
import os
import sys
import time
import threading
import readline  # enables arrow keys / history in input()
from datetime import datetime
from typing import Optional

from dotenv import load_dotenv

from .socket_bridge import start_socket_bridge, stop_socket_bridge, get_socket_bridge
from .model import RoR2BrainModel
from .orchestrator import RoR2Orchestrator


logging.basicConfig(level=logging.WARNING)
logger = logging.getLogger(__name__)


# ── Colour helpers ─────────────────────────────────────────────────────────────
def ts() -> str:
    return datetime.now().strftime("%H:%M:%S")

def green(s):  return f"\033[92m{s}\033[0m"
def yellow(s): return f"\033[93m{s}\033[0m"
def cyan(s):   return f"\033[96m{s}\033[0m"
def red(s):    return f"\033[91m{s}\033[0m"
def dim(s):    return f"\033[2m{s}\033[0m"


HELP = """
Directive REPL — inject high-level goals into the running LLM brain.

  <anything>   Set a directive  (e.g. "go activate the teleporter", "play more aggressively")
  status        Show active directive and iteration count
  clear         Clear the active directive
  help          Show this help
  quit / exit   Stop the brain and exit

Directives expire after ~10 min (strategic) or ~45s (tactical, e.g. "go to chest").
The brain picks up the directive on its next decision cycle.
"""


def setup_readline():
    readline.set_history_length(200)


def wait_for_connection(timeout: float = 120.0) -> bool:
    print(yellow(f"  Waiting for game to connect on port 7777..."))
    print(dim("  (Start RoR2 with Ror2GamerMode mod loaded)"))
    deadline = time.time() + timeout
    dots = 0
    while time.time() < deadline:
        bridge = get_socket_bridge()
        if bridge and bridge.is_connected():
            return True
        time.sleep(0.5)
        dots += 1
        if dots % 6 == 0:
            remaining = int(deadline - time.time())
            print(f"  ... still waiting ({remaining}s remaining)", end="\r", flush=True)
    return False


def run_directive_repl(brain_controller):
    """Interactive REPL for injecting directives into the running brain."""
    print()
    print(green("  Brain is running. Type a directive to influence its decisions."))
    print(dim(f"  (Directives expire after ~45s tactical / ~10min strategic. Type 'help' for commands.)"))
    print()

    while True:
        try:
            raw = input(cyan("directive> ")).strip()
        except (EOFError, KeyboardInterrupt):
            print()
            break

        if not raw:
            continue

        lower = raw.lower()

        if lower in ("quit", "exit", "q"):
            break

        if lower == "help":
            print(HELP)
            continue

        if lower == "clear":
            brain_controller.user_directive = None
            brain_controller.directive_timestamp = None
            print(green("  Directive cleared"))
            continue

        if lower == "status":
            if brain_controller.user_directive and brain_controller._is_directive_valid():
                age = time.time() - brain_controller.directive_timestamp
                remaining = brain_controller.directive_ttl - age
                print(f"  Directive : {cyan(brain_controller.user_directive)}")
                print(f"  Type      : {brain_controller.directive_type}")
                print(f"  Expires in: {remaining:.0f}s")
            else:
                print("  No active directive")
            print(f"  Brain iterations: {brain_controller.iteration_count}")
            continue

        brain_controller.set_user_directive(raw)
        print(green(f"  Directive set: {raw}"))
        print(dim(f"  (Brain picks it up on next cycle, ~{brain_controller.update_interval:.0f}s)"))
        print()


def main():
    import argparse
    parser = argparse.ArgumentParser(description="rainflayer — RoR2 LLM Brain Server")
    parser.add_argument("--interval", type=float, default=4.0,
                        help="Brain decision interval in seconds (default: 4.0)")
    parser.add_argument("--timeout", type=float, default=120.0,
                        help="Seconds to wait for game connection (default: 120)")
    args = parser.parse_args()

    load_dotenv()
    api_key = os.environ.get("NOVITA_API_KEY")
    if not api_key:
        print(red("  ERROR: NOVITA_API_KEY not set."))
        print(red("  Add it to .env or set it as an environment variable."))
        sys.exit(1)

    print()
    print("=" * 60)
    print("  rainflayer — RoR2 LLM Brain Server")
    print("  Llama 4 Maverick 17B via Novita.ai")
    print("=" * 60)

    setup_readline()
    logging.getLogger().setLevel(logging.INFO)

    print(cyan("  Initialising brain model..."))
    brain_model = RoR2BrainModel(api_key=api_key)

    print(cyan("  Creating orchestrator (socket bridge on port 7777)..."))
    try:
        orchestrator = RoR2Orchestrator(
            brain_model=brain_model,
            brain_update_interval=args.interval,
        )
    except RuntimeError as e:
        print(red(f"  ERROR: {e}"))
        sys.exit(1)

    brain_controller = orchestrator.brain_controller

    if not wait_for_connection(timeout=args.timeout):
        print(red("\n  Timed out waiting for game connection. Is the mod loaded?"))
        stop_socket_bridge()
        sys.exit(1)

    print(green(f"\n  Game connected at {ts()}"))
    print()

    # Run the brain's async loop in a background thread
    def run_brain():
        async def _loop():
            await orchestrator.start()
            while True:
                await asyncio.sleep(1)
        asyncio.run(_loop())

    brain_thread = threading.Thread(target=run_brain, daemon=True)
    brain_thread.start()

    try:
        run_directive_repl(brain_controller)
    finally:
        stop_socket_bridge()
        print(dim("  Socket closed."))


if __name__ == "__main__":
    main()
