"""Counterboss Model - LLM-generated counter-build for the adversary survivor.

On each item pickup, generates a counter-loadout designed to beat the player's
current build. The result is cached and sent to C# when the teleporter fires.

Item catalog is received live from C# on run_started (Run.instance.availableTier1/2/3DropList
via ItemCatalog + Language), so the LLM only picks items that actually exist in this run.
Falls back to a static seed list if no catalog has arrived yet.

Key design decisions:
- Random subselection of items (with desc) keeps context small and forces variety
- Random subselection of survivors (minus recent ban-list) forces survivor variety
- Ban-list culling is done in Python before the LLM sees anything — deterministic
- Boss-survivor item principles in system prompt handle the "on-kill is useless" problem
- Few-shot examples teach build philosophy without hardcoding item ratings
"""
from __future__ import annotations

import json
import logging
import random
import re
from collections import deque
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Tuple

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Static seed catalog — only used if run_started hasn't arrived yet.
# ---------------------------------------------------------------------------
_SEED_TIER1 = [
    "Syringe", "Bear", "Crowbar", "ChainLightning", "CritGlasses",
    "HealWhileSafe", "Hoof", "Infusion", "Mushroom", "NearbyDamageBonus",
    "PersonalShield", "Seed", "Stealthkit", "Tooth", "FlatHealth",
    "Feather", "BleedOnHit", "Firework",
]
_SEED_TIER2 = [
    "Bandolier", "Clover", "DeathMark", "FireRing", "FrostRelic",
    "HealOnCrit", "IceRing", "LaserTurbine", "Medkit", "Missile",
    "Phasing", "Plant", "Razorwire", "ShieldOnly", "Squid",
    "StickyBomb", "TPHealingNova", "UtilitySkillMagazine", "HeadHunter",
    "AttackSpeedOnCrit",
]
_SEED_TIER3 = [
    "AlienHead", "ArmorReductionOnHit", "BarrierOnOverHeal", "BarrierOnKill",
    "ExtraLife", "FireballsOnHit", "GainArmor", "CeremonialDagger",
    "IncreaseMaxHealth", "Knurl", "LightningStrikeOnHit", "Meteor",
    "NovaOnHeal", "ShockNearby",
]

# How many items to show the LLM per tier (random subselection each call)
_SUBSELECT_PER_TIER = 10
# How many survivors to show the LLM (random subselection, after culling ban-list)
_SUBSELECT_SURVIVORS = 5


@dataclass
class CounterbuildResult:
    """Result of a counterbuild LLM call."""
    items: List[Tuple[str, int]]   # (internal_item_name, count)
    reasoning: str
    source: str = "llm"            # "llm" or "random"
    survivor: Optional[str] = None  # cachedName e.g. "Huntress"; None = use config default


# ---------------------------------------------------------------------------
# Context builders
# ---------------------------------------------------------------------------

def _build_item_context(
    catalog: Optional[List[Dict]],
    tier_map: Dict[str, str],
) -> str:
    """
    Build a random subselection of items per tier with their descriptions.
    Uses live catalog if available, otherwise falls back to seed names.
    """
    if catalog:
        t1 = [i for i in catalog if i.get("tier") == "tier1"]
        t2 = [i for i in catalog if i.get("tier") == "tier2"]
        t3 = [i for i in catalog if i.get("tier") == "tier3"]

        def fmt(items: List[Dict]) -> str:
            sub = random.sample(items, min(_SUBSELECT_PER_TIER, len(items)))
            parts = []
            for i in sub:
                desc = i.get("desc", "").strip()
                entry = f"{i['name']} (\"{i['displayName']}\")"
                if desc:
                    entry += f" — {desc}"
                parts.append(entry)
            return "\n  ".join(parts)

        return (
            "AVAILABLE ITEMS — format: InternalName (\"Display Name\") — pickup description\n"
            f"(Showing a random subset per tier. Other items exist but are not listed.)\n\n"
            f"WHITE (Tier 1):\n  {fmt(t1)}\n\n"
            f"GREEN (Tier 2):\n  {fmt(t2)}\n\n"
            f"RED   (Tier 3):\n  {fmt(t3)}"
        )
    else:
        # Seed fallback — no descriptions
        t1 = random.sample(_SEED_TIER1, min(_SUBSELECT_PER_TIER, len(_SEED_TIER1)))
        t2 = random.sample(_SEED_TIER2, min(_SUBSELECT_PER_TIER, len(_SEED_TIER2)))
        t3 = random.sample(_SEED_TIER3, min(_SUBSELECT_PER_TIER, len(_SEED_TIER3)))
        return (
            "AVAILABLE ITEMS (internal names, random subset shown):\n\n"
            "WHITE (Tier 1): " + ", ".join(t1) + "\n\n"
            "GREEN (Tier 2): " + ", ".join(t2) + "\n\n"
            "RED   (Tier 3): " + ", ".join(t3)
        )


def _build_survivor_context(
    catalog: Optional[List[Dict]],
    banned_survivors: Optional[List[str]] = None,
) -> Tuple[str, List[str]]:
    """
    Random subselection of survivors, culling the ban-list first.
    Returns (context_string, list_of_shown_survivor_names).
    """
    banned = set(banned_survivors or [])

    if catalog:
        available = [s for s in catalog if s["name"] not in banned]
        sub = random.sample(available, min(_SUBSELECT_SURVIVORS, len(available)))
        entries = [s["name"] for s in sub]
        shown = [s["name"] for s in sub]
    else:
        fallback_all = [
            "Commando", "Huntress", "Bandit2", "Toolbot", "Engi",
            "Mage", "Merc", "Treebot", "Loader", "Croco", "Captain",
        ]
        available = [s for s in fallback_all if s not in banned]
        sub = random.sample(available, min(_SUBSELECT_SURVIVORS, len(available)))
        entries = sub[:]
        shown = sub[:]

    context = (
        "AVAILABLE SURVIVORS — InternalName:\n"
        + ", ".join(entries)
    )
    return context, shown


def _build_system_prompt(item_context: str, survivor_context: str) -> str:
    return f"""You are designing a counter-loadout for an AI adversary survivor in Risk of Rain 2.

The adversary fights the player head-on at the teleporter. Your job: pick a survivor and items
that specifically counter the player's win condition.

{item_context}

{survivor_context}

---- BOSS SURVIVOR ITEM PRINCIPLES ----
The adversary IS the boss — these facts change which items are good:

EXCELLENT for boss:
- Healing items (HealWhileSafe, TPHealingNova, HealOnCrit, Medkit, Plant, Seed) — boss has huge
  HP scaling, making % heals and regen dramatically more effective than on a normal survivor
- Barrier / shield items (Bear, ShieldOnly, BarrierOnOverHeal) — stacking mitigation is strong
- Damage-on-hit procs (StickyBomb, FireRing, IceRing, Missile, LightningStrikeOnHit) — boss
  attacks frequently, so on-hit procs fire often
- Pure stat items (FlatHealth, Knurl, IncreaseMaxHealth, GainArmor, CritGlasses) — straightforward

USELESS or BAD for boss (never pick these):
- On-kill items — the adversary does not need to farm kills to win. Items whose description
  mentions "on kill" provide zero value. Examples: BonusGoldPackOnKill, IgniteOnKill,
  WarCryOnMultiKill, Bandolier, HeadHunter (boss won't stack kills in a 1v1)
- BossDamageBonus ("Old Guillotine") — deals extra damage TO bosses; you ARE the boss, useless
- Armor-piercing-on-boss items — same problem

---- ITEM DIVERSITY ----
- For builds under 15 items: aim for 4–6 distinct item types
- For 15+ items: aim for 5–8 distinct types
- Stacking the same item more than 3× is only justified for top-tier defensive items
  (Bear, FlatHealth, Knurl). Otherwise variety beats stacks — e.g. 4 items at 2–3 each
  is better than 1 item at 8×

---- FEW-SHOT EXAMPLES (use as inspiration for style, not as templates) ----

Example — player has crit/attack-speed build, 8 items, 5w/2g/1r:
{{"survivor":"Loader","items":[{{"name":"Bear","count":3}},{{"name":"ShieldOnly","count":2}},{{"name":"FlatHealth","count":2}},{{"name":"GainArmor","count":1}}],"reasoning":"Crit-storm incoming. Loader behind layered armor walls makes their offense irrelevant — ShieldOnly eats the burst while Bear blocks the crits."}}

Example — player has bleed/DoT build, 10 items, 5w/3g/2r:
{{"survivor":"Croco","items":[{{"name":"DeathMark","count":1}},{{"name":"BleedOnHit","count":3}},{{"name":"HealWhileSafe","count":3}},{{"name":"StickyBomb","count":2}},{{"name":"Bear","count":1}}],"reasoning":"Fight fire with fire. Acrid's poison + DeathMark turns their bleed into a liability, and regenerating while safe negates their sustained pressure."}}

Example — player has on-kill stacking build, 6 items, 4w/2g:
{{"survivor":"Huntress","items":[{{"name":"CritGlasses","count":2}},{{"name":"HealOnCrit","count":2}},{{"name":"Hoof","count":1}},{{"name":"Missile","count":1}}],"reasoning":"Their scaling requires kills to ramp — Huntress crit loop deals consistent pressure before they can stack up, and HealOnCrit keeps the adversary alive."}}

Example — player has raw healing/regen build, 12 items, 6w/4g/2r:
{{"survivor":"Bandit2","items":[{{"name":"DeathMark","count":1}},{{"name":"CritGlasses","count":3}},{{"name":"BleedOnHit","count":3}},{{"name":"StickyBomb","count":3}},{{"name":"Clover","count":2}}],"reasoning":"Their healing is the win condition — DeathMark + bleed bypasses all of it. Bandit resets on kill keep the DoT pressure relentless."}}

---- OUTPUT FORMAT ----
Output ONLY valid JSON — no markdown, no text outside the JSON:
{{
  "survivor": "InternalName",
  "items": [{{"name": "InternalName", "count": N}}, ...],
  "reasoning": "1-2 punchy sentences: name their build style, name the chosen survivor, explain the counter"
}}

RULES:
1. survivor MUST be an InternalName from the AVAILABLE SURVIVORS list above
2. Use ONLY item InternalNames from the AVAILABLE ITEMS lists above — no made-up names
3. Total item count MUST exactly match the player's total (given in the user message)
4. MATCH THE PLAYER'S RARITY SPLIT — if they have 6w/3g/1r, match that ratio
5. Follow the ITEM DIVERSITY guideline above — avoid stacking one item excessively
6. NO lunar, void, or equipment items
7. Never pick on-kill items or BossDamageBonus (see BOSS SURVIVOR ITEM PRINCIPLES)
8. reasoning is shown in-game chat — make it punchy and viewer-friendly"""


class CounterbossModel:
    """
    Generates counter-builds for the adversary survivor.

    Accepts an LLMBackend instance — use LLMBackend.novita() for the
    current default, or swap to any other provider via LLMBackend.from_env().
    Falls back to a static seed list if no catalog has arrived yet.
    """

    def __init__(self, backend: "Any"):  # LLMBackend, avoid circular import
        self.backend = backend
        logger.info("[CounterbossModel] Initialized")

    async def generate_counterbuild(
        self,
        player_items: List[Dict[str, Any]],
        total_items: int,
        item_catalog: Optional[List[Dict]] = None,
        survivor_catalog: Optional[List[Dict]] = None,
        player_survivor: str = "",
        player_survivor_display: str = "",
        banned_survivors: Optional[List[str]] = None,
    ) -> CounterbuildResult:
        if not player_items:
            return self._random_fallback(total_items, "No player items to counter", item_catalog)

        try:
            # Build tier_map for rarity counting in user message
            if item_catalog:
                tier_map = {i["name"]: i.get("tier", "") for i in item_catalog}
                tier_map.update({i["displayName"]: i.get("tier", "") for i in item_catalog})
            else:
                tier_map = (
                    {n: "tier1" for n in _SEED_TIER1}
                    | {n: "tier2" for n in _SEED_TIER2}
                    | {n: "tier3" for n in _SEED_TIER3}
                )

            item_context = _build_item_context(item_catalog, tier_map)
            survivor_context, shown_survivors = _build_survivor_context(survivor_catalog, banned_survivors)
            system_prompt = _build_system_prompt(item_context, survivor_context)
            user_content = self._build_user_message(
                player_items, total_items, tier_map,
                player_survivor=player_survivor,
                player_survivor_display=player_survivor_display,
            )

            logger.info(
                f"[CounterbossModel] Sending prompt to {self.backend.model}\n"
                f"=== SYSTEM ===\n{system_prompt}\n"
                f"=== USER ===\n{user_content}\n"
                f"=== END PROMPT ==="
            )
            raw_text = await self.backend.complete(
                system=system_prompt,
                user=user_content,
                max_tokens=1024,
                temperature=0.7,
                json_mode=True,
                reasoning_effort="minimal",
            )
            logger.info(f"[CounterbossModel] Response:\n{raw_text}")
            raw = self._parse_json(raw_text)
            if not raw:
                logger.warning(f"[CounterbossModel] JSON parse failed on above response")
            result = self._parse_response(raw, total_items, item_catalog, survivor_catalog, shown_survivors)
            logger.info(
                f"[CounterbossModel] Generated {len(result.items)} types — "
                f"{result.reasoning[:80]}..."
            )
            return result
        except Exception as e:
            logger.error(f"[CounterbossModel] Generation failed: {e}", exc_info=True)
            return self._random_fallback(total_items, f"LLM error: {e}", item_catalog)

    def _build_user_message(
        self,
        player_items: List[Dict],
        total_items: int,
        tier_map: Dict[str, str],
        player_survivor: str = "",
        player_survivor_display: str = "",
    ) -> str:
        t1 = t2 = t3 = 0
        for it in player_items:
            tier = tier_map.get(it["name"], "")
            cnt = it.get("count", 1)
            if tier == "tier1":   t1 += cnt
            elif tier == "tier2": t2 += cnt
            elif tier == "tier3": t3 += cnt

        survivor_line = ""
        if player_survivor_display or player_survivor:
            label = player_survivor_display or player_survivor
            survivor_line = f"Player survivor: {label}\n"

        item_lines = [f"  - {it['name']} x{it['count']}" for it in player_items[:25]]
        return (
            f"{survivor_line}"
            f"Player inventory ({total_items} total items — "
            f"~{t1} white / {t2} green / {t3} red):\n"
            + "\n".join(item_lines)
            + f"\n\nBuild a counter-loadout with exactly {total_items} total items, "
            + f"roughly matching {t1} white / {t2} green / {t3} red. "
            + f"Pick a survivor that counters {player_survivor_display or player_survivor or 'the player'}'s playstyle."
        )

    def _parse_json(self, text: str) -> Dict[str, Any]:
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            match = re.search(r"\{.*\}", text, re.DOTALL)
            if match:
                try:
                    return json.loads(match.group(0))
                except json.JSONDecodeError:
                    pass
        return {}

    def _parse_response(
        self,
        raw: Dict[str, Any],
        total_items: int,
        catalog: Optional[List[Dict]],
        survivor_catalog: Optional[List[Dict]],
        shown_survivors: List[str],
    ) -> CounterbuildResult:
        items_raw = raw.get("items", [])
        reasoning = raw.get("reasoning", "Counter-build generated")

        # Validate survivor — only accept one of the survivors shown to the LLM
        survivor = raw.get("survivor", "").strip() or None
        if survivor:
            valid_survivors = set(shown_survivors)
            # Also accept any survivor from the full catalog as a fallback
            if survivor_catalog:
                valid_survivors |= {s["name"] for s in survivor_catalog}
            if survivor not in valid_survivors:
                logger.warning(f"[CounterbossModel] LLM picked unknown survivor '{survivor}' — ignoring")
                survivor = None

        # Build valid item name set from catalog or seeds
        if catalog:
            valid_names = {i["name"] for i in catalog}
        else:
            valid_names = set(_SEED_TIER1 + _SEED_TIER2 + _SEED_TIER3)

        items: List[Tuple[str, int]] = []
        seen: set = set()
        for entry in items_raw:
            name = entry.get("name", "").strip()
            count = max(1, int(entry.get("count", 1)))
            if not name or name in seen:
                continue
            if name not in valid_names:
                logger.warning(f"[CounterbossModel] LLM picked unknown item '{name}' — skipping")
                continue
            items.append((name, count))
            seen.add(name)

        if not items:
            logger.info(f"[CounterbossModel] All LLM items were invalid — raw names: {[e.get('name') for e in items_raw]}")
            logger.info(f"[CounterbossModel] Catalog valid_names sample: {sorted(valid_names)[:30]}")
            return self._random_fallback(total_items, "LLM returned no valid items", catalog)

        # Scale to match player total exactly
        if total_items > 0:
            current = sum(c for _, c in items)
            if current != total_items:
                scale = total_items / max(current, 1)
                items = [(n, max(1, round(c * scale))) for n, c in items]
                actual = sum(c for _, c in items)
                diff = total_items - actual
                if diff != 0:
                    n, c = items[-1]
                    items[-1] = (n, max(1, c + diff))

        return CounterbuildResult(items=items, reasoning=reasoning, source="llm", survivor=survivor)

    def _random_fallback(
        self,
        total_items: int,
        reason: str,
        catalog: Optional[List[Dict]] = None,
    ) -> CounterbuildResult:
        """Random items weighted toward greens, drawn from live catalog if available."""
        if catalog:
            t1 = [i["name"] for i in catalog if i.get("tier") == "tier1"]
            t2 = [i["name"] for i in catalog if i.get("tier") == "tier2"]
            t3 = [i["name"] for i in catalog if i.get("tier") == "tier3"]
        else:
            t1, t2, t3 = _SEED_TIER1, _SEED_TIER2, _SEED_TIER3

        pool = t1 * 2 + t2 * 3 + t3
        if not pool:
            pool = _SEED_TIER1 + _SEED_TIER2 + _SEED_TIER3

        chosen: Dict[str, int] = {}
        for _ in range(max(total_items, 0)):
            item = random.choice(pool)
            chosen[item] = chosen.get(item, 0) + 1

        logger.warning(f"[CounterbossModel] Random fallback: {reason}")
        return CounterbuildResult(
            items=list(chosen.items()),
            reasoning=f"Random counter-build ({reason})",
            source="random",
        )
