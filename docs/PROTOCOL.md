# Socket Protocol Reference

Bidirectional TCP communication on `127.0.0.1:7777` between Python (server) and the C# mod (client).

All messages are newline-delimited JSON.

---

## Connection

- Python starts a TCP server on port 7777
- The mod connects automatically on startup and retries every 2 seconds if unavailable
- The mod uses a background read thread; all Unity API calls are safely dispatched to the main thread

**C# log on connect:**
```
[Info :Rainflayer] [SocketBridge] Connected to Python!
```

---

## Message Format

```
{"type": "MESSAGE_TYPE", ...}\n
```

---

## Queries

Python sends a query; the mod responds synchronously on the same connection.

### QUERY_INVENTORY

```json
{"type": "QUERY_INVENTORY"}
```

Response:
```json
{
  "type": "INVENTORY",
  "money": 450,
  "equipment": "Backup Magazine",
  "items": [
    {"name": "Soldier's Syringe", "count": 3},
    {"name": "Armor-Piercing Rounds", "count": 1}
  ]
}
```

---

### QUERY_OBJECTIVE

```json
{"type": "QUERY_OBJECTIVE"}
```

Response:
```json
{
  "type": "OBJECTIVE",
  "objective": "charging_teleporter",
  "teleporter_charged": false,
  "teleporter_charge": 45.0,
  "boss_active": false
}
```

`objective` values:
- `exploring` — teleporter not yet activated
- `charging_teleporter` — teleporter is charging
- `teleporter_charged` — ready to advance stage

---

### QUERY_COMBAT_STATUS

```json
{"type": "QUERY_COMBAT_STATUS"}
```

Response:
```json
{
  "type": "COMBAT_STATUS",
  "in_combat": true,
  "enemy_count": 5,
  "nearest_enemy_distance": 12.5
}
```

`in_combat` is true when any enemy is within 50m.

---

### QUERY_INTERACTABLES

```json
{"type": "QUERY_INTERACTABLES"}
```

Response:
```json
{
  "type": "INTERACTABLES",
  "interactables": [
    {"type": "chest",      "name": "Chest",      "cost": 25,  "distance": 15.3, "locked": false},
    {"type": "shrine",     "name": "Shrine of Combat", "cost": 0, "distance": 28.1, "locked": false, "usesRemaining": 2},
    {"type": "teleporter", "name": "Teleporter",  "cost": 0,  "distance": 45.0, "locked": false, "charged": false, "chargePercent": 0.0, "bossActive": false}
  ]
}
```

Interactable types: `chest`, `shrine`, `teleporter`, `shop`, `drone`, `printer`, `portal`, `misc`

Extra fields by type:
- `shrine`: `usesRemaining`
- `teleporter`: `charged`, `chargePercent`, `bossActive`

Python's `bridge.query_interactables()` automatically deduplicates to the closest of each type.

---

### QUERY_ALLIES

```json
{"type": "QUERY_ALLIES"}
```

Response:
```json
{
  "type": "ALLIES",
  "allies": [
    {"name": "Player2", "health": 80.0, "maxHealth": 110.0, "distance": 30.5, "isDowned": false}
  ]
}
```

---

## Commands

```json
{"type": "COMMAND", "command": "COMMAND_NAME", "args": "arguments"}
```

Response:
```json
{"type": "COMMAND_RESPONSE", "command": "FIND_AND_INTERACT", "status": "ok"}
```

### Navigation & Interaction

| command | args | description |
|---------|------|-------------|
| `FIND_AND_INTERACT` | `chest` | Navigate to nearest chest and open it |
| `FIND_AND_INTERACT` | `shrine` | Navigate to nearest shrine and use it |
| `FIND_AND_INTERACT` | `teleporter` | Navigate to teleporter and activate it |
| `GOTO` | `CANCEL` | Cancel current navigation |
| `BUY_SHOP_ITEM` | `Item Name` | Navigate to nearest shop and buy named item |

`FIND_AND_INTERACT` handles the full navigation → combat → interaction loop internally. If combat interrupts it (enemy within 10m), the mod pauses and resumes after the threat is cleared.

### Combat strategy

| command | args | description |
|---------|------|-------------|
| `STRATEGY` | `aggressive` | Close range (15m), strafing, max DPS |
| `STRATEGY` | `defensive` | Long range (25m), heavy kiting |
| `STRATEGY` | `balanced` | Medium range (20m), moderate skill use |
| `STRATEGY` | `support` | Follow and protect allies |

### Behavior mode

| command | args | description |
|---------|------|-------------|
| `MODE` | `roam` | Wander, explore, auto-engage enemies |
| `MODE` | `combat` | Stay focused on nearby enemies |
| `MODE` | `follow` | Follow nearest ally |
| `MODE` | `wait` | Stop all movement |

---

## Events (C# → Python, unsolicited)

The mod can push events to Python over the same TCP connection without being queried. Python's socket bridge routes these to an event queue that the brain drains each cycle.

```json
{"type": "EVENT", "event_type": "action_complete", "command": "FIND_AND_INTERACT:chest", "result": "success"}
{"type": "EVENT", "event_type": "action_complete", "command": "FIND_AND_INTERACT:chest", "result": "interrupted"}
{"type": "EVENT", "event_type": "action_complete", "command": "FIND_AND_INTERACT:chest", "result": "failed (stuck)"}
{"type": "EVENT", "event_type": "action_complete", "command": "FIND_AND_INTERACT:chest", "result": "failed (no gold)"}
```

The brain tracks these outcomes as action history and feeds them back to the LLM so it can retry or adapt.

---

## Errors

```json
{"error": "Error message describing what went wrong"}
```

Common errors:
- `"Missing 'command' field"` — malformed command JSON
- `"Unknown query type: X"` — unrecognized message type
- `"AI controller not found"` — mod not fully initialized yet
- `"Not in game"` — query sent before player spawned into a run

---

## Adding New Queries (C#)

```csharp
// In SocketBridge.cs → RegisterQueryHandlers()
queryHandlers["QUERY_MY_TYPE"] = HandleMyQuery;

public void HandleMyQuery(string queryJson) {
    string json = "{\"type\": \"MY_RESPONSE\", \"data\": 42}";
    SendResponse(json);
}
```

On the Python side:
```python
response = bridge.socket.send_query("QUERY_MY_TYPE")
```
