"""Bridge - Python interface to the RoR2 C# mod via TCP socket.

All communication goes through the SocketBridge on port 7777.
Call start_socket_bridge() before constructing Bridge.
"""
import logging
from typing import List, Optional

from .socket_bridge import get_socket_bridge, SocketBridge
from .types import (
    InteractableInfo, AllyInfo,
    ObjectiveData, CombatStatusData, InventoryData
)

logger = logging.getLogger(__name__)


class Bridge:
    """
    Interface to the RoR2 mod over TCP socket.

    Supported commands (mirrors prompts.py):
        STRATEGY:aggressive|defensive|balanced|support
        MODE:roam|combat|follow|wait
        FIND_AND_INTERACT:chest|shrine|teleporter
        GOTO:CANCEL
        BUY_SHOP_ITEM:<item_name>
    """

    def __init__(self):
        self.socket: Optional[SocketBridge] = get_socket_bridge()
        if not self.socket:
            raise RuntimeError(
                "Socket bridge not started — call start_socket_bridge() before creating Bridge"
            )

    # ------------------------------------------------------------------
    # Connection
    # ------------------------------------------------------------------

    def is_connected(self) -> bool:
        return self.socket is not None and self.socket.is_connected()

    # ------------------------------------------------------------------
    # Queries
    # ------------------------------------------------------------------

    def query_inventory(self) -> Optional[InventoryData]:
        response = self.socket.send_query("QUERY_INVENTORY")
        if response and "error" not in response:
            return InventoryData.from_dict(response)
        return None

    def query_interactables(self) -> List[InteractableInfo]:
        """Returns the closest interactable of each type."""
        response = self.socket.send_query("QUERY_INTERACTABLES")
        if not response or "error" in response:
            return []
        closest_by_type: dict = {}
        for d in response.get("interactables", []):
            inter = InteractableInfo.from_dict(d)
            if inter.type not in closest_by_type or inter.distance < closest_by_type[inter.type].distance:
                closest_by_type[inter.type] = inter
        return list(closest_by_type.values())

    def query_allies(self) -> List[AllyInfo]:
        response = self.socket.send_query("QUERY_ALLIES")
        if not response or "error" in response:
            return []
        return [AllyInfo.from_dict(d) for d in response.get("allies", [])]

    def query_objective(self) -> Optional[ObjectiveData]:
        response = self.socket.send_query("QUERY_OBJECTIVE")
        if response and "error" not in response:
            return ObjectiveData.from_dict(response)
        return None

    def query_combat_status(self) -> Optional[CombatStatusData]:
        response = self.socket.send_query("QUERY_COMBAT_STATUS")
        if response and "error" not in response:
            return CombatStatusData.from_dict(response)
        return None

    def poll_events(self) -> list:
        """Non-blocking drain of async C# events."""
        return self.socket.poll_events()

    # ------------------------------------------------------------------
    # Commands
    # ------------------------------------------------------------------

    def send_command(self, command: str, args: str = "") -> bool:
        """Send any raw mod command."""
        if not self.is_connected():
            logger.warning(f"[Bridge] Not connected — cannot send: {command}")
            return False
        response = self.socket.send_query("COMMAND", command=command, args=args)
        return bool(response and "error" not in response)

    def set_strategy(self, strategy: str) -> bool:
        """aggressive | defensive | balanced | support"""
        return self.send_command("STRATEGY", strategy)

    def set_mode(self, mode: str) -> bool:
        """roam | combat | follow | wait"""
        return self.send_command("MODE", mode)

    def goto_cancel(self) -> bool:
        return self.send_command("GOTO", "CANCEL")
