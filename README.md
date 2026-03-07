<h1 align="center">Rainflayer</h1>

<p align="center">LLM-controlled Risk of Rain 2 via BepInEx mod + Python brain</p>

<p align="center"><strong>Early development — expect bugs, rough edges, and missing features.</strong></p>

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

Mods like PlayerBots and ImprovedSurvivorAI use RoR2's native `AISkillDriver` system — static, hand-tuned rule sets baked into the mod at compile time. They work well as NPC teammates, but their behavior cannot be changed at runtime. You cannot tell them to play more aggressively, follow you to the teleporter, or adapt to what's happening in the run.

Rainflayer exposes a reasoning layer. The LLM reads live game state every few seconds and issues commands that dynamically reshape behavior mid-run. Directives like `play defensively` or `go open that chest` are interpreted in context and translated into actual inputs. These mods are not competitors — PlayerBots gives you a reliable NPC teammate, Rainflayer is an experiment in giving an AI agent genuine agency in a game.

### vs. research-grade AI gaming projects

Projects like DeepMind's SIMA or frameworks like Cradle require expensive training pipelines, proprietary hardware, or research-level infrastructure. Rainflayer runs entirely on consumer hardware using API calls — no local GPU required, no training involved. The goal is to explore what's achievable with foundation models and game-level APIs today.

### Who this is for

- Developers building AI agents who want a real game environment as a testbed
- Content creators interested in AI + gaming crossover
- Hobbyists curious about what LLM-driven play actually looks like in practice

This is not a drop-in replacement for a human co-op partner. It's experimental and proof-of-concept — if you want a reliable RoR2 teammate, the RoR2 community has no shortage of players. This exists because nothing quite like it exists yet, and because the broader vision (AI that can play *any* game, not just RoR2) is worth exploring.

### System requirements and RAM usage

A common concern with AI mods is RAM overhead. Here's what Rainflayer actually uses:

| Component | RAM usage |
|-----------|-----------|
| BepInEx mod (C# plugin) | < 5 MB |
| Python brain | ~50–100 MB |
| Local GPU | Not required |

All LLM inference runs remotely via the Novita.ai API — there is no local model loaded. The Python brain is a lightweight script that makes HTTP calls. Internet connection is required.

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
│  Llama 4 Maverick 17B via Novita.ai                  │
│  Reads game state → LLM → commands                   │
└────────────────────── ↕ TCP :7777 ───────────────────┘
┌─ BepInEx Mod (Tactical, 50 Hz) ─────────────────────┐
│  Intercepts InputBank + CameraRigController           │
│  Entity detection, pathfinding, skill execution       │
└──────────────────── Risk of Rain 2 ──────────────────┘
```

While the brain plays, you can type natural-language goals to influence its decisions in real time:

```
directive> go activate the teleporter
directive> play more aggressively
directive> clear
directive> status
```

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
| `GOTO` | `CANCEL` | Cancel current navigation |
| `BUY_SHOP_ITEM` | `Item Name` | Navigate to nearest shop and buy named item |

### Combat

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

You don't need to use these commands directly — just type natural-language goals into the directive prompt and the AI figures out what to do.

---

## Known Issues

This list is not exhaustive — there are likely bugs that haven't been discovered or documented yet. If you run into something, feel free to open an issue.

- **Engineer:** Thermal Harpoons (primary alt) do not fire correctly — the AI uses the default primary instead
- **Support strategy:** Does not actively engage enemies; best used in co-op to follow and buffer allies
- **Navigation stuck states:** The AI can get stuck on complex geometry. Stuck detection exists but doesn't catch every case — if it fails to trigger, the AI may idle until a new directive is issued
- **Interaction targeting:** The AI can lock onto an interaction target (chest, shrine, teleporter) and fail to complete it. A directive like `clear` or `roam` will usually unstick it
- **Midair jumps on Commencement (Moon 2):** The AI uses midair jumps to help navigate Commencement, which has extremely complex geometry. This is intentional but unreliable — the Moon stage is the hardest environment for the pathfinder by a significant margin
- **Camera at extreme aim angles:** When targeting flying enemies (steep upward aim) or enemies near the player's feet (steep downward aim), the camera can lose the player character from frame. This is a fundamental tension between "show what the AI is targeting" and "keep the player visible" — an unsolved problem even in the RoR2 Autoplay mod. A pitch clamp reduces the worst cases
- **Survivor coverage:** Not all survivors have been thoroughly tested. Behavior quality varies by character

---

## Roadmap

### Near-term
- Escape sequence fix — resolve the post-Mithrix teleport zones so the AI can complete a full run solo
- Camera improvements — the aerial combat framing problem is an open research question; incremental improvements ongoing

### LLM-controlled enemies (planned)

The same socket interface used to control the player character can be extended to control enemies. A `Soul` model issuing directives to `teamIndex: Monster` instead of the player would give the LLM agency over an enemy character — navigating, targeting the player, and using skills via the same command layer.

A more interesting variant: put the AI on an **opposing survivor** — same character type as the player but on the enemy team. RoR2 supports custom `TeamIndex` assignments (see [PVP Mod](https://thunderstore.io/package/brynzananas/PVP_Mod/) and [Refightilization](https://thunderstore.io/package/Wonda/Refightilization/) for prior art on team reassignment). This would create a genuine AI adversary that fights and moves like a player, opening the door to AI vs. human PvP scenarios within a co-op game.

### Longer-term
- Paradigm 1 — the original zero-shot computer use approach (no mods, no game-specific code, just screenshots + mouse/keyboard) remains the broader goal, applicable to any game

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
