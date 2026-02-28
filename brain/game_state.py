"""RoR2 game state representation for Brain decisions."""
from dataclasses import dataclass, field
from typing import Optional, List

from .types import (
    EnemyInfo, InteractableInfo, AllyInfo,
    ObjectiveData, CombatStatusData, InventoryData
)


@dataclass
class RoR2GameState:
    """RoR2-specific game state for Brain strategic decisions."""

    # Player status
    health_percent: float = 100.0
    max_health: float = 100.0
    current_health: float = 100.0
    money: int = 0

    # Enemy situation
    enemies: List[EnemyInfo] = field(default_factory=list)
    closest_enemy_distance: float = 999.0
    boss_present: bool = False
    elite_present: bool = False
    num_enemies: int = 0

    # Combat context
    current_strategy: str = "balanced"
    current_mode: str = "roam"
    in_combat: bool = False

    # Stage context
    teleporter_charged: bool = False
    teleporter_charge: float = 0.0
    boss_active: bool = False

    # Entity data
    interactables: List[InteractableInfo] = field(default_factory=list)
    allies: List[AllyInfo] = field(default_factory=list)
    inventory: Optional[InventoryData] = None

    def update_from_interactables(self, interactables: List[InteractableInfo]):
        self.interactables = interactables
        for i in interactables:
            if i.type == "teleporter":
                self.teleporter_charged = i.charged
                self.teleporter_charge = i.charge_percent
                self.boss_active = i.boss_active
                break

    def update_from_allies(self, allies: List[AllyInfo]):
        self.allies = allies

    def update_from_inventory(self, inventory: InventoryData):
        self.inventory = inventory
        self.money = inventory.money

    def update_from_combat_status(self, combat_status: CombatStatusData):
        self.in_combat = combat_status.in_combat if combat_status.in_game else False
        self.num_enemies = combat_status.enemy_count
        self.closest_enemy_distance = combat_status.nearest_enemy_distance
        self.current_strategy = combat_status.strategy
        self.current_mode = combat_status.mode

    def update_from_objective(self, objective: ObjectiveData):
        self.teleporter_charged = objective.teleporter_charged
        self.teleporter_charge = objective.teleporter_charge
        self.boss_active = objective.boss_active

    def is_critical_health(self) -> bool:
        return self.health_percent < 30.0

    def is_low_health(self) -> bool:
        return self.health_percent < 50.0

    def is_surrounded(self) -> bool:
        return self.num_enemies >= 5

    def should_be_defensive(self) -> bool:
        return self.is_critical_health() or (self.is_low_health() and self.is_surrounded())

    def should_be_aggressive(self) -> bool:
        return self.health_percent > 70.0 and self.boss_active and not self.is_surrounded()

    def get_affordable_chests(self) -> List[InteractableInfo]:
        return [i for i in self.interactables if i.type == "chest" and i.cost <= self.money]

    def get_nearest_chest(self) -> Optional[InteractableInfo]:
        affordable = self.get_affordable_chests()
        return min(affordable, key=lambda c: c.distance) if affordable else None

    def get_summary(self) -> str:
        """Text summary of game state for LLM input."""
        summary = f"""RoR2 Game State:
- Health: {self.health_percent:.0f}% ({self.current_health:.0f}/{self.max_health:.0f})
- Money: {self.money}
- Enemies: {self.num_enemies}
- Closest enemy: {self.closest_enemy_distance:.0f}m
- Strategy: {self.current_strategy}
- Mode: {self.current_mode}
- In combat: {self.in_combat}
- Teleporter: {self.teleporter_charge:.0f}% charged (boss: {self.boss_active})
"""
        if self.interactables:
            inter_strs = [f"{i.name}({i.type}, {i.distance:.0f}m, ${i.cost})" for i in self.interactables]
            summary += f"- Nearby interactables: {', '.join(inter_strs)}\n"
        else:
            summary += "- Nearby interactables: none\n"

        if self.allies:
            summary += f"- Allies: {len(self.allies)}\n"

        return summary
