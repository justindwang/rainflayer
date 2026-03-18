# Setup Guide

Full setup instructions for Rainflayer.

---

## Prerequisites

- Risk of Rain 2 (Steam)
- BepInEx 5.x (via [r2modman](https://thunderstore.io/package/ebkr/r2modman/) or [manual install](https://github.com/BepInEx/BepInEx/releases))
- Python 3.10+
- Novita.ai API key ([get one](https://novita.ai))

---

## Step 1: Install the mod

**Via mod manager (recommended):**
1. Install [r2modman](https://thunderstore.io/package/ebkr/r2modman/)
2. Select Risk of Rain 2 and create a profile
3. Search for Rainflayer and install it (BepInEx installs automatically as a dependency) (Note: I still need to do this)
4. Launch RoR2 through r2modman once to generate the config, then close it

**Manually:**
1. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) into your RoR2 folder
2. Launch RoR2 once so BepInEx initializes, then close it
3. Drop `Rainflayer.dll` from the latest release into `BepInEx/plugins/`
4. Launch RoR2 once more to generate the config, then close it

---

## Step 2: Set up Python

```bash
python -m venv venv
source venv/bin/activate     # Windows: venv\Scripts\activate
pip install -r requirements.txt
```

Create `.env` in the repo root:
```
NOVITA_API_KEY=your_key_here
```

This is enough to run both the LLM player and the Counterboss feature using the default model (Llama 4 Maverick 17B via Novita.ai).

### Using a different model

You can swap the LLM for either feature independently using env vars:

```
# Override the player brain model
BRAIN_MODEL=openai/gpt-4o-mini
BRAIN_API_KEY=sk-...

# Override the counterboss model (optional — falls back to BRAIN_* if unset)
COUNTERBOSS_MODEL=anthropic/claude-haiku-4-5-20251001
COUNTERBOSS_API_KEY=<anthropic key>
```

Models are specified in [LiteLLM format](https://docs.litellm.ai/docs/providers): `provider/model-name`. Any provider LiteLLM supports works here. If `COUNTERBOSS_MODEL` is not set, counterboss uses the same model as the brain.

---

## Step 3: Run

1. Launch RoR2 and start a solo run (any character)
2. Wait for the drop pod to land
3. In a terminal:

```bash
source venv/bin/activate
python -m brain.main
```

The brain waits up to 2 minutes for the mod to connect. You'll see:

```
  Game connected at 12:34:56
  Brain is running. Type a directive to influence its decisions.
directive>
```

---

## Mod Configuration

Config file is generated at `BepInEx/config/com.rainflayer.cfg` on first run:

```ini
[Rainflayer]
EnableAIControl = true
DebugMode = false
```

- `EnableAIControl` — set to `false` to play manually without removing the mod
- `DebugMode` — enables verbose logging in `BepInEx/LogOutput.log`

```ini
[Counterboss]
EnableCounterboss = true
CounterbossHPMultiplier = 1.0
CounterbossDamageMultiplier = 0.4
CounterbossHealMultiplier = 0.25
```

- `EnableCounterboss` — set to `false` to disable the adversary entirely
- `CounterbossHPMultiplier` — scales the adversary's HP relative to the teleporter boss it replaced
- `CounterbossDamageMultiplier` — scales the adversary's damage output (default 0.4 keeps it fair without the brain playing it optimally)
- `CounterbossHealMultiplier` — scales incoming healing on the adversary (default 0.25 reduces the strength of self-healing item builds)

See [COUNTERBOSS.md](COUNTERBOSS.md) for more detail on how the feature works.

---

## Troubleshooting

### Mod doesn't load

- Verify `Rainflayer.dll` is in `BepInEx/plugins/` (not a subdirectory)
- Check `BepInEx/LogOutput.log` for errors
- Try running RoR2 as administrator (Windows)

### Socket won't connect

```bash
# Check nothing else is using port 7777
lsof -ti:7777          # macOS/Linux
netstat -ano | findstr :7777   # Windows
```

- Make sure the Python brain is started before the connection timeout expires
- Check that your firewall isn't blocking localhost connections

### Character doesn't move

- Verify `EnableAIControl = true` in mod config
- Enable `DebugMode = true` and check logs for `[AIController] [Movement]` lines

### LLM brain errors

- Confirm `NOVITA_API_KEY` is set: `echo $NOVITA_API_KEY`
- Check your Novita.ai account has credits
- The brain falls back to safe defaults (`STRATEGY:balanced, MODE:roam`) on any API error

### Log locations

| Component | Location |
|-----------|----------|
| Mod (C#) | `BepInEx/LogOutput.log` (filter for `[Rainflayer]`) |
| Brain (Python) | Console output |

---

## Building from Source

If you want to modify the mod itself, see the dependency DLL setup and build instructions in [ARCHITECTURE.md](ARCHITECTURE.md#building-from-source).
