"""Data types for RoR2 game state communication."""
from dataclasses import dataclass
from typing import List


@dataclass
class EnemyInfo:
    """Information about an enemy entity."""
    name: str
    is_boss: bool
    is_elite: bool
    health: float
    max_health: float
    health_percent: float
    distance: float
    position: tuple

    @classmethod
    def from_dict(cls, data: dict) -> 'EnemyInfo':
        pos_data = data.get('position', [0, 0, 0])
        if isinstance(pos_data, list):
            pos = tuple(pos_data)
        else:
            pos_str = str(pos_data).strip('[]()')
            pos = tuple(float(x) for x in pos_str.split(','))

        return cls(
            name=data.get('name', 'Unknown'),
            is_boss=data.get('isBoss', False),
            is_elite=data.get('isElite', False),
            health=data.get('health', 0.0),
            max_health=data.get('maxHealth', 0.0),
            health_percent=data.get('healthPercent', 0.0),
            distance=data.get('distance', 0.0),
            position=pos
        )


@dataclass
class InteractableInfo:
    """Information about an interactable object."""
    type: str  # chest, shrine, teleporter, shop, drone, printer, portal, misc, command
    name: str
    cost: int
    distance: float
    position: tuple

    uses_remaining: int = -1   # For shrines
    charged: bool = False       # For teleporter
    charge_percent: float = 0.0 # For teleporter
    boss_active: bool = False   # For teleporter

    @classmethod
    def from_dict(cls, data: dict) -> 'InteractableInfo':
        pos_data = data.get('position', [0, 0, 0])
        if isinstance(pos_data, list):
            pos = tuple(pos_data)
        else:
            pos_str = str(pos_data).strip('[]()')
            pos = tuple(float(x) for x in pos_str.split(','))

        return cls(
            type=data.get('type', 'unknown'),
            name=data.get('name', 'Unknown'),
            cost=data.get('cost', 0),
            distance=data.get('distance', 0.0),
            position=pos,
            uses_remaining=data.get('usesRemaining', -1),
            charged=data.get('charged', False),
            charge_percent=data.get('chargePercent', 0.0),
            boss_active=data.get('bossActive', False)
        )


@dataclass
class AllyInfo:
    """Information about an ally player."""
    name: str
    health: float
    max_health: float
    health_percent: float
    distance: float
    position: tuple
    is_downed: bool

    @classmethod
    def from_dict(cls, data: dict) -> 'AllyInfo':
        pos_data = data.get('position', [0, 0, 0])
        if isinstance(pos_data, list):
            pos = tuple(pos_data)
        else:
            pos_str = str(pos_data).strip('[]()')
            pos = tuple(float(x) for x in pos_str.split(','))

        return cls(
            name=data.get('name', 'Ally'),
            health=data.get('health', 0.0),
            max_health=data.get('maxHealth', 0.0),
            health_percent=data.get('healthPercent', 0.0),
            distance=data.get('distance', 0.0),
            position=pos,
            is_downed=data.get('isDowned', False)
        )


@dataclass
class ObjectiveData:
    """Information about current objective."""
    objective: str  # exploring, charging_teleporter, teleporter_charged
    teleporter_charged: bool
    teleporter_charge: float
    boss_active: bool

    @classmethod
    def from_dict(cls, data: dict) -> 'ObjectiveData':
        return cls(
            objective=data.get('objective', 'exploring'),
            teleporter_charged=data.get('teleporter_charged', False),
            teleporter_charge=data.get('teleporter_charge', 0.0),
            boss_active=data.get('boss_active', False)
        )


@dataclass
class CombatStatusData:
    """Information about combat status."""
    in_game: bool  # False when not in an active run (lobby, loading, dead)
    in_combat: bool
    enemy_count: int
    nearest_enemy_distance: float
    strategy: str
    mode: str

    @classmethod
    def from_dict(cls, data: dict) -> 'CombatStatusData':
        return cls(
            in_game=data.get('note') != 'Not in game',
            in_combat=data.get('in_combat', False),
            enemy_count=data.get('enemy_count', 0),
            nearest_enemy_distance=data.get('nearest_enemy_distance', 999.0),
            strategy=data.get('strategy', 'balanced'),
            mode=data.get('mode', 'roam')
        )


@dataclass
class InventoryData:
    """Information about player inventory."""
    items: List[dict]  # List of {name, count}
    equipment: str
    money: int

    @classmethod
    def from_dict(cls, data: dict) -> 'InventoryData':
        return cls(
            items=data.get('items', []),
            equipment=data.get('equipment', 'None'),
            money=data.get('money', 0)
        )
