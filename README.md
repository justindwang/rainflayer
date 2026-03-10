<h1 align="center">Rainflayer</h1>

<p align="center">LLM-controlled Risk of Rain 2 via BepInEx mod + Python brain</p>

<p align="center"><strong>Early development — expect bugs, rough edges, and missing features.</strong></p>

<p align="center">
  <img src="assets/demo.gif" alt="Rainflayer demo" width="640">
</p>

<p align="center">
  <a href="docs/SETUP.md">Setup</a> |
  <a href="docs/PROTOCOL.md">Protocol Reference</a> |
  <a href="docs/ARCHITECTURE.md">Architecture</a> |
  <a href="https://youtu.be/KtQ8kid-6og">Youtube Showcase</a>
</p>

Rainflayer connects a large language model to Risk of Rain 2. A BepInEx mod exposes game state (enemies, inventory, objectives, interactables) over a local TCP socket. A Python brain queries that state every few seconds, feeds it to an LLM, and sends back strategic commands that the mod executes at 50 Hz — movement, aiming, skills, camera, all without simulating mouse or keyboard input.

Inspired by [Mindcraft](https://github.com/kolbytn/mindcraft) and [Mineflayer](https://github.com/PrismarineJS/mineflayer).

---

## Why Rainflayer?

### vs. existing bot mods (PlayerBots, ImprovedSurvivorAI)

Mods like PlayerBots and ImprovedSurvivorAI use RoR2's native `AISkillDriver` system — static, hand-tuned rule sets baked into the mod at compile time. They work well as NPC teammates, but their behavior cannot be changed at runtime. You cannot tell them to play more aggressively, follow you to the teleporter, wait around and farm chests, or adapt to what's happening in the run.

Rainflayer exposes a reasoning layer. The LLM reads live game state every few seconds and issues commands that dynamically reshape behavior mid-run. Directives like `play defensively` or `go open that chest` are interpreted in context and translated into actual inputs. PlayerBots gives you a reliable NPC teammate, while Rainflayer is an experiment in giving an AI agency in a game.

Rainflayer runs entirely on consumer hardware using API calls — no local GPU or training involved. The goal is to explore what's achievable with foundation models and game-level APIs today.

### Who this is for

- Developers building AI agents who want a real game environment as a testbed
- Content creators interested in AI + gaming crossover
- Hobbyists curious about what LLM-driven play actually looks like in practice
- Modded Risk of Rain 2 players looking for something new

This is not a drop-in replacement for a human co-op partner. It's experimental and proof-of-concept — if you want a reliable RoR2 teammate, the RoR2 community has no shortage of players. This exists because nothing quite like it exists yet, and because the broader vision of an AI that can play any game (not just RoR2) is worth exploring.

### System requirements and RAM usage

A common concern with AI mods is RAM overhead. Here's what Rainflayer's expectage usage looks like:

| Component | RAM usage |
|-----------|-----------|
| BepInEx mod (C# plugin) | < 5 MB |
| Python brain | ~50–100 MB |
| Local GPU | Not required |

All LLM inference runs remotely via the Novita.ai API, the Python brain is a lightweight script that makes HTTP calls. Only internet connection is required.

---
## Disclaimers
- **Active development, not a finished product.** Rainflayer is in early proof-of-concept/beta. Core functionality works but expect rough edges, untested edge cases, and breaking changes between versions.
- **Not all survivors are fully tested.** Behavior quality varies by character. Some may work well, others may have unresolved issues.
- **This project includes AI-assisted and AI-generated code.** Some portions were created with the assistance of large language models. If you have concerns about AI-generated code, please be aware before using or contributing.
- **Use at your own risk.**

> [!NOTE]
> Rainflayer takes full control of your character. Set `EnableAIControl = false` in the mod config when you want to play normally without removing the mod.

---

## How It Works

```
┌─ Python Brain (Strategic, ~0.25 Hz) ────────────────┐
│  Llama 4 Maverick 17B via Novita.ai                 │
│  Reads game state → LLM → commands                  │
└────────────────────── ↕ TCP :7777 ──────────────────┘
┌─ BepInEx Mod (Tactical, 50 Hz) ─────────────────────┐
│  Intercepts InputBank + CameraRigController         │
│  Entity detection, pathfinding, skill execution     │
└──────────────────── Risk of Rain 2 ─────────────────┘
```

While the brain plays, you can type natural-language goals to influence its decisions in real time:

```
directive> go activate the teleporter
directive> play more aggressively
directive> clear
directive> status
```

This is just the minimum usage case - connecting to your own custom LLMs / AI projects is intended as the broader use case.

---

## Requirements

- **Risk of Rain 2** (Steam)
- **BepInEx 5.x** — [download](https://github.com/BepInEx/BepInEx/releases) or install via [r2modman](https://thunderstore.io/package/ebkr/r2modman/)
- **Python 3.10+**
- **Novita.ai API key** — [get one](https://novita.ai) (~$0.10–0.50/hr for Llama 4 Maverick 17B)

---

## Installation

### 1. Install the mod

**Via mod manager (recommended):** Install with [r2modman](https://thunderstore.io/package/ebkr/r2modman/) or [Thunderstore Mod Manager](https://www.overwolf.com/app/Thunderstore-Thunderstore_Mod_Manager) from the Thunderstore page.

**Manually:** Download the latest release, and drop `Rainflayer.dll` into your `BepInEx/plugins/` folder. Repeat for dependencies.

Launch RoR2 once to generate the config file, then close it.

### 2. Set up the Python brain

```bash
python -m venv venv
source venv/bin/activate   # Windows: venv\Scripts\activate
pip install -r requirements.txt
```

Create a `.env` file in the repo root:

```
NOVITA_API_KEY=your_novita_api_key_here
```

### 3. Run

Load into a solo run in RoR2, then in a separate terminal:

```bash
source venv/bin/activate
python -m brain.main
```

The brain waits up to 2 minutes for the mod to connect. Once connected, it starts making decisions every 4 seconds.

```
============================================================
  rainflayer — RoR2 LLM Brain Server
  Llama 4 Maverick 17B via Novita.ai
============================================================
  Game connected at 12:34:56

  Brain is running. Type a directive to influence its decisions.

directive>
```

---

## Configuration

### Mod config

Generated at `BepInEx/config/justindwang.rainflayer.cfg` after first run:

```ini
[Rainflayer]
EnableAIControl = true
DebugMode = false
```

### Brain options

```bash
python -m brain.main --interval 4.0   # decision interval in seconds (default: 4.0)
python -m brain.main --timeout 120    # seconds to wait for game connection
```

---

## How the AI Makes Decisions

Every few seconds the brain queries the mod for the current game state — health, gold, nearby enemies, interactables, teleporter charge — and sends it to an LLM (Llama 4 Maverick 17B). The LLM returns a short list of commands that the mod executes immediately.

The commands the AI can send:

### Navigation & Interaction

| Command | Args | Effect |
|---------|------|--------|
| `FIND_AND_INTERACT` | `chest` | Navigate to nearest chest and open it |
| `FIND_AND_INTERACT` | `shrine` | Navigate to nearest shrine and use it |
| `FIND_AND_INTERACT` | `teleporter` | Navigate to teleporter and activate it |
| `FIND_AND_INTERACT` | `pillar` | Navigate to nearest uncharged moon battery pillar and interact (moon2 only) |
| `FIND_AND_INTERACT` | `jump_pad` | Navigate to the jump pad leading to the Mythrix arena (moon2 only) |
| `FIND_AND_INTERACT` | `ship` | Navigate to the rescue ship to escape the moon after defeating Mythrix (moon2 only) |
| `GOTO` | `CANCEL` | Cancel current navigation and clear all island/return-path tracking |
| `BUY_SHOP_ITEM` | `Item Name` | Navigate to nearest shop and buy named item |

`FIND_AND_INTERACT` handles the full navigation → combat → interaction loop. If combat interrupts it (enemy within 10m), the mod pauses and resumes after the threat clears. `pillar`, `jump_pad`, and `ship` are long-running journeys (60–120s) — issue once and wait.

### Moon2 / Commencement

Commencement (moon2) has a distinct set of objectives: charge four battery pillars → use the jump pad → defeat Mythrix → escape to the rescue ship. The general flow is:

1. `FIND_AND_INTERACT:pillar` — navigates across the main platform and through the appropriate passage to the nearest uncharged pillar, activates it, and stands inside the zone until it's fully charged. After charging, use `MODE:defend_zone` to hold position while the charge ticks up.
2. Repeat for all four pillars. After each pillar charges, the AI returns to the main platform and proceeds to the next.
3. `FIND_AND_INTERACT:jump_pad` — navigates to the mass island, crosses the chain bridge, and reaches the jump pad teleporting into the Mythrix arena.
4. Fight Mythrix with `MODE:combat` and `STRATEGY:aggressive`.
5. `FIND_AND_INTERACT:ship` — after defeating Mythrix, navigates through the arena escape orb, follows the blood passage back to the main platform, and reaches the rescue ship.

There are also `GOTO:<island>` commands (`blood`, `soul`, `mass`, `design`) for recovering from bugged island state — these are not general navigation commands and should only be used when explicitly needed to reset position.

### Defend Zone

`MODE:defend_zone` locks the AI inside the nearest active holdout zone and keeps it there:

| Scenario | Zone type |
|----------|-----------|
| Teleporter charging | Teleporter's holdout zone |
| Moon2 pillar charging | Battery pillar's holdout zone |
| Post-Mythrix escape sequence | Extraction zone near the rescue ship |

When `MODE:defend_zone` is set, the AI navigates into the zone if outside it, then switches to free combat/roam behavior clamped inside the zone radius. All roam waypoints and strafe movement are constrained so the AI stays within the zone boundary. The mode self-cancels if the zone becomes inactive (e.g. teleporter fully charged, pillar done).

If no active zone is found when the command is issued, the mod sends `action_failed` with reason `no_active_zone` and the brain falls back to `MODE:combat`.

### Combat and Behaviors

| Command | Args | Effect |
|---------|------|--------|
| `STRATEGY` | `aggressive` | Close range (15m), strafing, high DPS |
| `STRATEGY` | `defensive` | Long range (25m), heavy kiting |
| `STRATEGY` | `balanced` | Medium range (20m), moderate skills |
| `STRATEGY` | `support` | Follow allies (does not engage enemies) |
| `MODE` | `roam` | Wander and auto-engage enemies |
| `MODE` | `combat` | Stay focused on nearby enemies |
| `MODE` | `follow` | Follow nearest ally |
| `MODE` | `wait` | Stop all movement |
| `MODE` | `defend_zone` | Stay inside the nearest active holdout zone (teleporter, pillar, or escape zone) |

You don't need to use these commands directly — just type natural-language goals into the directive prompt and the AI figures out what to do.

---

## Known Issues

This list is not exhaustive — there are likely bugs that haven't been discovered or documented yet. If you run into something, feel free to open an issue.

- **Engineer:** Thermal Harpoons (primary alt) do not fire correctly — the AI uses the default primary instead
- **Support strategy:** Does not actively engage enemies; best used in co-op to follow and buffer allies
- **Navigation stuck states:** The AI can get stuck on complex geometry. Stuck detection exists but doesn't catch every case — if it fails to trigger, the AI may idle until a new directive is issued
- **Interaction targeting:** The AI can lock onto an interaction target (chest, shrine, teleporter) and fail to complete it. A directive like `clear` or `roam` will usually unstick it
- **Midair jumps during navigation (especially on Commencement):** The AI uses midair jumps to help navigate Commencement, which has extremely complex geometry. This is intentional but unreliable — the Moon stage is the hardest environment for the pathfinder by a significant margin
- **Camera at extreme aim angles:** When targeting flying enemies (steep upward aim) or enemies near the player's feet (steep downward aim), the camera can lose the player character from frame. This is a fundamental tension between "show what the AI is targeting" and "keep the player visible" — an unsolved problem even in the RoR2 Autoplay mod. A pitch clamp for now reduces the worst cases
- **Survivor coverage:** Not all survivors have been thoroughly tested. Behavior quality varies by character
- **Too much speed:** Navigation gets bugged if character has more than 2 goat hoofs of speed
- **defend_zone wacky movement:** The defend_zone navigation override may make the character movement and navigation choppy/scuffed - working on smoothing out the leashing behavior

---

## Roadmap

### Near-term
- Fixing all bugs listed above

### LLM-controlled enemies (planned)

The same socket interface used to control the player character can be extended to control enemies. A `Soul` model issuing directives to `teamIndex: Monster` instead of the player would give the LLM agency over an enemy character — navigating, targeting the player, and using skills via the same command layer.

A more interesting variant: put the AI on an **opposing survivor** — same character type as the player but on the enemy team. RoR2 supports custom `TeamIndex` assignments (see [PVP Mod](https://thunderstore.io/package/brynzananas/PVP_Mod/) and [Refightilization](https://thunderstore.io/package/Wonda/Refightilization/) for prior art on team reassignment). This would create an AI-controlled adversary that fights and moves like a player, opening the door to AI vs. human PvP scenarios within a co-op game.
- Idea 1 - Both are survivors on different teams, still PVE'ing and collecting items (chest items automatically get added to inventory to prevent stealing) - and killing each other allows the killer to steal a chosen/random item from the deceased, and the deceased respawns after a cooldown, while dying PVE still generates an automatic loss
- Idea 2 - with 80% # of your items, where it chooses its build to counter yours (and with balanced HP) - all the way up to replacing Mythrix (and on mythrix it can potentially choose to disable/steal what it thinks are your best items)

### Longer-term
- The original zero-shot computer use approach (no mods, no game-specific code, just screenshots + mouse/keyboard) remains the broader goal, applicable to any game

---

## Credits

- [RoR2PlayerBots](https://thunderstore.io/package/Rampage45/PlayerBots/) and its community forks — foundational work on AI-controlled survivor characters in RoR2
- [ImprovedSurvivorAI](https://thunderstore.io/package/Samuel17/ImprovedSurvivorAI/) — comprehensive reference for per-survivor `AISkillDriver` tuning
- [Autoplay](https://thunderstore.io/package/Volvary/AutoPlay/) — prior art on AI game control and camera challenges in RoR2
- [PVP Mod](https://thunderstore.io/package/brynzananas/PVP_Mod/) and [Refightilization](https://thunderstore.io/package/Wonda/Refightilization/) — inspiration for team index manipulation and enemy/player crossover mechanics
- [Mindcraft](https://github.com/kolbytn/mindcraft) and [Mineflayer](https://github.com/PrismarineJS/mineflayer) — the original inspiration for LLM + game API integration
- The kind members of the RoR2 modding community whose feedback and questions shaped this project

---

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for a technical deep dive:
- How InputBank and CameraRigController are hooked
- Multi-height raycasting for line-of-sight checks
- Thread-safe Unity ↔ socket communication
- Why direct API control was chosen over input simulation

See [docs/PROTOCOL.md](docs/PROTOCOL.md) for the full socket protocol reference.

---

## How to Reach Me

- **GitHub Issues:** [github.com/justindwang/rainflayer/issues](https://github.com/justindwang/rainflayer/issues) — bug reports and feature requests
- **Email:** emailkiri3851@gmail.com
- **Reddit:** https://www.reddit.com/user/Riolutail/
- **Discord:** riolutail

---

## License

MIT
