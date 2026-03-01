# Architecture

Technical overview of how Rainflayer works.

---

## Overview

```
┌─ Python Brain (Strategic, ~0.25 Hz) ──────────────────────┐
│                                                           │
│  BrainController                                          │
│    → query game state (5 queries per cycle)               │
│    → drain C# event queue (action outcomes)               │
│    → build context for LLM                                │
│    → RoR2BrainModel (Llama 4 Maverick 17B, Novita.ai)     │
│    → parse JSON commands                                  │
│    → send commands to mod                                 │
│                                                           │
└──────────────── ↕ TCP 127.0.0.1:7777 ─────────────────────┘
┌─ BepInEx Mod (Tactical, 50 Hz) ───────────────────────────┐
│                                                           │
│  RainflayerPlugin  (lifecycle, hooks)                     │
│    ├── SocketBridge  (TCP server comms, main-thread queue)│
│    ├── AIController  (command routing)                    │
│    │     ├── CombatController   (aiming, skills, camera)  │
│    │     ├── NavigationController  (pathfinding, interact)│
│    │     └── EntityDetector  (enemies, interactables)     │
│    └── PlayerAI  (BaseAI wrapper)                         │
│                                                           │
└──────────────────── Risk of Rain 2 ───────────────────────┘
```

---

## Why Direct API Control Instead of Input Simulation

Early versions tried simulating keyboard/mouse input. This doesn't work reliably:

- On macOS (CrossOver), DirectInput bypass is needed but unreliable
- Input simulation adds latency and timing complexity
- Mouse simulation doesn't work at all in some configurations

**Rainflayer writes directly to RoR2's internal state:**

- **Movement/aim:** Writes directly to `CharacterBody.inputBank.moveVector` and `inputBank.aimDirection`
- **Skills:** Calls `InputBank.skill1/2/3/4` button states directly
- **Camera:** Hooks `CameraRigController.GetCameraState()` and returns computed rotation/position
- **Sprint:** Writes `inputBank.sprint`

The hook in `RainflayerPlugin` saves AI-set input values before calling `orig()`, then restores them afterward — overwriting any player input that the original update wrote:

```csharp
// Save AI values
Vector3 aiMoveVector = body.inputBank.moveVector;
Vector3 aiAimDirection = body.inputBank.aimDirection;

orig(self);  // Player input + engine updates run here

// Restore AI values (overwrite player input)
body.inputBank.moveVector = aiMoveVector;
body.inputBank.aimDirection = aiAimDirection;
```

---

## Thread Safety

Unity's API is not thread-safe. Calling `FindObjectsOfType`, `transform`, `GetComponent`, etc. from a background thread causes native crashes (corrupted vtable → page fault).

**SocketBridge uses a main-thread dispatch queue:**

```
[Background read thread]
    reads line from TCP socket
    parses query type (string only, no Unity API)
    enqueues handler closure → mainThreadQueue

[Unity Update() — main thread]
    dequeues and calls all pending handlers
    handlers can safely call any Unity API
```

Two separate locks:
- `lockObj` — protects socket read/write operations
- `queueLock` — protects the mainThreadQueue

---

## Multi-Height Raycasting

A single raycast at eye level is too strict — small obstacles (rocks, railings) block it even when the path is walkable. Rainflayer uses 4 raycast heights:

```
0.5m  (chest height)
1.0m  (waist)
1.5m  (eye level)
2.0m  (above head)
```

If **any** raycast succeeds → target is considered reachable.
If **all** raycasts fail → target is blocked (wall, cliff, etc.).

This eliminates most false negatives while preserving real terrain blocking.

---

## Navigation & Interaction

`FIND_AND_INTERACT` handles the full loop:

1. EntityDetector finds nearest matching interactable with line-of-sight
2. NavigationController navigates using RoR2's NodeGraph pathfinding
3. If enemies come within 10m: pause navigation, wait for combat to clear
4. On arrival (within ~2.5m): hold interact button for 1.5s (some objects require hold)
5. Settle timer (0.5s within range) prevents interaction firing during high-speed overshoot
6. On completion: push `EVENT action_complete` to Python with `success`/`interrupted`/`failed`

Stuck detection uses a frustration counter: if the character moves < 1 m/s for 5 seconds, frustration increases. At frustration ≥ 10, navigation gives up and pushes `failed (stuck)`.

---

## Aim-Confirm Pulse (e.g., Engineer Turret)

Some skills are two-stage: activate → confirm placement. After the first press, the EntityState reads `InputBank.justPressed` events to detect the confirm.

CombatController implements a pulse system:

```csharp
aimConfirmActive = true;
aimConfirmTimer = 2.5f;

// Each FixedUpdate while active:
aimConfirmPulse = !aimConfirmPulse;   // alternates each frame
inputBank.skill4.down = aimConfirmPulse;
// justPressed fires on the rising edge → confirms placement
```

---

## Character-Specific Logic

| Character | Special handling |
|-----------|-----------------|
| Huntress | Can sprint while shooting |
| MUL-T | Special (skill4) switches weapon mode, not fires |
| Engineer | Two-stage special (turret placement) — aim-confirm pulse |
| Captain | Two-stage utility (orbital strike / supply drop) — aim-confirm pulse |

Sprint blocking: CombatController maintains lists of EntityStates (from `RTAutoSprintEx`) where sprinting must be delayed or disabled — e.g., during Flamethrower, Missile Painter, Scope states.

---

## LLM Brain Decision Cycle

Default interval: 4 seconds (0.25 Hz)

1. **Query** — 5 parallel queries: inventory, objective, combat status, interactables, allies
2. **Events** — drain async event queue from mod (action outcomes: success/interrupted/failed)
3. **State summary** — build text description of current game state + action history
4. **LLM call** — Llama 4 Maverick 17B via Novita.ai (60s timeout)
5. **Parse** — extract `{"commands": [...], "reasoning": "..."}` from response
6. **Execute** — send each command to the mod

On any LLM error, falls back to `STRATEGY:balanced, MODE:roam`.

**Directive prompt** — while the brain runs, you can type natural-language goals:
```
directive> go activate the teleporter
directive> play more aggressively
directive> clear
```
The directive is included in the LLM context for ~45s (tactical) or ~10min (strategic).

---

## Performance

| Metric | Value |
|--------|-------|
| Socket roundtrip latency | ~5ms |
| Tactical loop rate | 50 Hz (FixedUpdate) |
| Brain decision rate | 0.25 Hz (every 4s) |
| LLM latency (Llama 4 Maverick) | 1–5s typical |
| Enemy detection range | 50m |
| Interactable detection range | 100m, 180° FOV |

---

## Building from Source

If you want to modify the C# mod, you'll need to set up the dependency DLLs manually since they come from your RoR2 installation and can't be redistributed.

### 1. Install HookGenPatcher

Rainflayer uses MonoMod hooks (`MMHOOK_RoR2.dll`), which is generated by [HookGenPatcher](https://thunderstore.io/package/RiskofThunder/HookGenPatcher/). Install it via r2modman and launch RoR2 once — the DLL will appear at `BepInEx/plugins/MMHOOK/MMHOOK_RoR2.dll`.

### 2. Copy dependency DLLs into `mod/libs/`

**From `Risk of Rain 2_Data/Managed/`:**
- `Assembly-CSharp.dll`, `RoR2.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.PhysicsModule.dll`, `UnityEngine.UI.dll`, `KinematicCharacterController.dll`, `HGCSharpUtils.dll`, `LegacyResourcesAPI.dll`, `com.unity.multiplayer-hlapi.Runtime.dll`

**From `BepInEx/core/`:**
- `BepInEx.Core.dll`, `BepInEx.Unity.Mono.dll`, `0Harmony.dll`, `Mono.Cecil.dll`, `MonoMod.Utils.dll`

**From `BepInEx/plugins/MMHOOK/`:**
- `MMHOOK_RoR2.dll`

### 3. Build

```bash
cd mod
dotnet build -c Debug
```

Output: `mod/bin/Debug/netstandard2.1/Rainflayer.dll`

Copy this to `BepInEx/plugins/` in your RoR2 install.
