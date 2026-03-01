<h1 align="center">Rainflayer</h1>

<p align="center">LLM-controlled Risk of Rain 2 via BepInEx mod + Python brain</p>

<p align="center">
  <a href="docs/SETUP.md">Setup</a> |
  <a href="docs/PROTOCOL.md">Protocol Reference</a> |
  <a href="docs/ARCHITECTURE.md">Architecture</a>
</p>

Rainflayer connects a large language model to Risk of Rain 2. A BepInEx mod exposes game state (enemies, inventory, objectives, interactables) over a local TCP socket. A Python brain queries that state every few seconds, feeds it to an LLM, and sends back strategic commands that the mod executes at 50 Hz — movement, aiming, skills, camera, all without simulating mouse or keyboard input.

Inspired by [Mindcraft](https://github.com/kolbytn/mindcraft) and [Mineflayer](https://github.com/PrismarineJS/mineflayer).

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

**Manually:** Download the latest release, and drop `Rainflayer.dll` into your `BepInEx/plugins/` folder.

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

Generated at `BepInEx/config/com.rainflayer.cfg` after first run:

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

- **Engineer:** Thermal Harpoons (primary alt) do not fire correctly — the AI uses the default primary instead
- **Support strategy:** Does not actively engage enemies; best used in co-op to follow and buffer allies
- Navigation can occasionally get stuck on complex geometry — the AI will retry automatically, but may time out on unreachable targets
- Teleporter navigation and interaction is pretty bugged on Sky Meadow

---

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for a technical deep dive:
- How InputBank and CameraRigController are hooked
- Multi-height raycasting for line-of-sight checks
- Thread-safe Unity ↔ socket communication
- Why direct API control was chosen over input simulation

See [docs/PROTOCOL.md](docs/PROTOCOL.md) for the full socket protocol reference.

---

## License

MIT
