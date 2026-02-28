using RoR2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Rainflayer
{
    /// <summary>
    /// Socket-based bridge for reliable Python↔C# communication.
    ///
    /// CRITICAL: All Unity API calls are dispatched to the main thread via a queue.
    /// The socket read thread only does JSON parsing and enqueues work.
    /// Unity's API (FindObjectsOfType, transform, GetComponent, etc.) is NOT thread-safe
    /// and will cause native crashes (corrupted vtable → page fault) if called from background threads.
    ///
    /// Protocol:
    /// - JSON messages delimited by newlines
    /// - Python sends: {"type": "QUERY_INVENTORY"}
    /// - C# responds: {"money": 40, "items": [], "equipment": "None"}
    /// </summary>
    public class SocketBridge : MonoBehaviour
    {
        private TcpClient client;
        private NetworkStream stream;
        private StreamReader reader;
        private StreamWriter writer;
        private Thread readThread;
        private bool connected = false;
        private readonly object lockObj = new object();

        // Main-thread dispatch queue: socket thread enqueues, Update() dequeues
        private readonly Queue<Action> mainThreadQueue = new Queue<Action>();
        private readonly object queueLock = new object();

        // Connection settings
        private const string HOST = "127.0.0.1";
        private const int PORT = 7777;
        private const int RECONNECT_DELAY_MS = 2000;  // Reconnect every 2 seconds

        // Query handlers (called on main thread via dispatch queue)
        private readonly Dictionary<string, Action<string>> queryHandlers = new Dictionary<string, Action<string>>();

        void Start()
        {
            // Register query handlers
            RegisterQueryHandlers();

            // Start connection thread
            Thread connectThread = new Thread(ConnectionLoop)
            {
                IsBackground = true,
                Name = "SocketBridge-Connect"
            };
            connectThread.Start();
        }

        void OnDestroy()
        {
            Disconnect();
        }

        /// <summary>
        /// Process queued actions on the main thread.
        /// This runs every frame and ensures all Unity API calls happen safely.
        /// </summary>
        void Update()
        {
            Action[] actionsToRun = null;

            lock (queueLock)
            {
                if (mainThreadQueue.Count > 0)
                {
                    actionsToRun = mainThreadQueue.ToArray();
                    mainThreadQueue.Clear();
                }
            }

            if (actionsToRun != null)
            {
                foreach (Action action in actionsToRun)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        RainflayerPlugin.Instance?.LogError(
                            "[SocketBridge] Error in main-thread dispatch: " + e.Message + "\n" + e.StackTrace);
                    }
                }
            }
        }

        /// <summary>
        /// Enqueue an action to run on Unity's main thread.
        /// Safe to call from any thread.
        /// </summary>
        void RunOnMainThread(Action action)
        {
            lock (queueLock)
            {
                mainThreadQueue.Enqueue(action);
            }
        }

        void RegisterQueryHandlers()
        {
            // Query handlers (return data via socket response)
            queryHandlers["QUERY_INVENTORY"] = HandleQueryInventory;
            queryHandlers["QUERY_OBJECTIVE"] = HandleQueryObjective;
            queryHandlers["QUERY_COMBAT_STATUS"] = HandleQueryCombatStatus;
            queryHandlers["QUERY_INTERACTABLES"] = HandleQueryInteractables;
            queryHandlers["QUERY_ALLIES"] = HandleQueryAllies;

            // Command handlers (execute via AIController, return status via socket)
            queryHandlers["COMMAND"] = HandleCommand;
        }

        void ConnectionLoop()
        {
            while (true)
            {
                if (!connected)
                {
                    TryConnect();
                }

                if (!connected)
                {
                    // Wait before retrying
                    Thread.Sleep(RECONNECT_DELAY_MS);
                }
                else
                {
                    // Connected, wait a bit before checking again
                    Thread.Sleep(1000);
                }
            }
        }

        void TryConnect()
        {
            try
            {
                // Always log connection attempts (not just in debug mode)
                RainflayerPlugin.Instance?.Log("[SocketBridge] Attempting to connect to Python at " + HOST + ":" + PORT);

                client = new TcpClient(HOST, PORT);
                stream = client.GetStream();
                // Use UTF-8 without BOM to avoid Python parsing errors
                var utf8NoBom = new System.Text.UTF8Encoding(false);
                reader = new StreamReader(stream, utf8NoBom);
                writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true };

                connected = true;
                RainflayerPlugin.Instance?.Log("[SocketBridge] Connected to Python!");

                // Start read thread
                readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "SocketBridge-Read"
                };
                readThread.Start();
            }
            catch (Exception e)
            {
                // Always log connection errors (critical for troubleshooting)
                RainflayerPlugin.Instance?.LogError("[SocketBridge] Connection failed: " + e.Message);
                RainflayerPlugin.Instance?.LogDebug("[SocketBridge] Error details: " + e.ToString());
                connected = false;
            }
        }

        void Disconnect()
        {
            lock (lockObj)
            {
                connected = false;

                try
                {
                    reader?.Close();
                    writer?.Close();
                    stream?.Close();
                    client?.Close();
                }
                catch (Exception e)
                {
                    DebugLog("[SocketBridge] Error disconnecting: " + e.Message);
                }

                reader = null;
                writer = null;
                stream = null;
                client = null;
            }
        }

        void ReadLoop()
        {
            RainflayerPlugin.Instance?.Log("[SocketBridge] Read thread started");

            while (connected)
            {
                try
                {
                    // Read line (blocking with timeout)
                    string line = reader.ReadLine();

                    if (string.IsNullOrEmpty(line))
                    {
                        RainflayerPlugin.Instance?.Log("[SocketBridge] Connection closed by Python");
                        Disconnect();
                        break;
                    }

                    // Parse query type manually (look for "type": "QUERY_...")
                    // This is pure string parsing - safe on any thread
                    string queryType = null;
                    int typeIndex = line.IndexOf("\"type\":");
                    if (typeIndex >= 0)
                    {
                        int startQuote = line.IndexOf("\"", typeIndex + 7) + 1;
                        int endQuote = line.IndexOf("\"", startQuote);
                        if (startQuote > 0 && endQuote > startQuote)
                        {
                            queryType = line.Substring(startQuote, endQuote - startQuote);
                        }
                    }

                    if (!string.IsNullOrEmpty(queryType))
                    {
                        DebugLog("[SocketBridge] Received query: " + queryType);

                        // Dispatch handler to main thread
                        if (queryHandlers.TryGetValue(queryType, out Action<string> handler))
                        {
                            // Capture variables for closure
                            string capturedLine = line;
                            Action<string> capturedHandler = handler;

                            RunOnMainThread(() => capturedHandler(capturedLine));
                        }
                        else
                        {
                            RainflayerPlugin.Instance?.LogError("[SocketBridge] Unknown query type: " + queryType);
                            SendError("Unknown query type: " + queryType);
                        }
                    }
                    else
                    {
                        RainflayerPlugin.Instance?.LogError("[SocketBridge] Failed to parse query type from: " + line);
                        SendError("Failed to parse query type");
                    }
                }
                catch (IOException e)
                {
                    RainflayerPlugin.Instance?.LogError("[SocketBridge] Connection error: " + e.Message);
                    Disconnect();
                    break;
                }
                catch (Exception e)
                {
                    RainflayerPlugin.Instance?.LogError("[SocketBridge] Error in read loop: " + e.Message);
                }
            }

            RainflayerPlugin.Instance?.Log("[SocketBridge] Read thread stopped");
        }

        void SendResponse(string json)
        {
            lock (lockObj)
            {
                if (!connected || writer == null)
                {
                    DebugLog("[SocketBridge] Cannot send response - not connected");
                    return;
                }

                try
                {
                    writer.WriteLine(json);
                }
                catch (Exception e)
                {
                    DebugLog("[SocketBridge] Error sending response: " + e.Message);
                    Disconnect();
                }
            }
        }

        void SendError(string error)
        {
            string errorJson = "{\"error\": \"" + error.Replace("\"", "\\\"") + "\"}";
            SendResponse(errorJson);
        }

        /// <summary>
        /// Push an unsolicited event to Python over the existing TCP connection.
        /// Safe to call from any thread (SendResponse is locked).
        /// Python's reader thread routes {"type":"EVENT",...} to the event queue.
        /// </summary>
        public void SendEvent(string eventType, params (string key, string value)[] data)
        {
            string json = "{\"type\": \"EVENT\", \"event_type\": \"" + EscapeJson(eventType) + "\"";
            if (data != null)
            {
                foreach (var (k, v) in data)
                    json += ", \"" + EscapeJson(k) + "\": \"" + EscapeJson(v) + "\"";
            }
            json += "}";
            SendResponse(json);
        }

        // ==================== QUERY HANDLERS ====================
        // These run on the MAIN THREAD (dispatched via Update → mainThreadQueue).
        // Safe to call any Unity API here.

        public void HandleQueryInventory(string queryJson = null)
        {
            try
            {
                // Quick check if we're in a valid game state
                if (!IsInGame())
                {
                    SendResponse("{\"type\": \"INVENTORY\", \"money\": 0, \"equipment\": \"None\", \"items\": [], \"note\": \"Not in game\"}");
                    return;
                }

                CharacterBody body = RainflayerPlugin.GetPlayerBody();
                if (body == null || body.inventory == null)
                {
                    SendResponse("{\"type\": \"INVENTORY\", \"money\": 0, \"equipment\": \"None\", \"items\": [], \"note\": \"No inventory\"}");
                    return;
                }

                // Get item counts
                var itemCounts = new List<object>();
                if (body.inventory.itemAcquisitionOrder != null)
                {
                    foreach (var itemIndex in body.inventory.itemAcquisitionOrder)
                    {
                        ItemDef itemDef = ItemCatalog.GetItemDef(itemIndex);
                        int count = body.inventory.GetItemCount(itemIndex);
                        itemCounts.Add(new { name = itemDef?.name ?? "Unknown", count });
                    }
                }

                // Get equipment
                string equipmentName = "None";
                if (body.inventory.currentEquipmentIndex != EquipmentIndex.None)
                {
                    EquipmentDef equipment = EquipmentCatalog.GetEquipmentDef(body.inventory.currentEquipmentIndex);
                    equipmentName = equipment?.name ?? "None";
                }

                // Build JSON response
                string json = "{";
                json += "\"type\": \"INVENTORY\",";
                json += "\"money\": " + (body.master?.money ?? 0) + ",";
                json += "\"equipment\": \"" + equipmentName + "\",";
                json += "\"items\": [";

                for (int i = 0; i < itemCounts.Count; i++)
                {
                    var item = itemCounts[i];
                    var nameProp = item.GetType().GetProperty("name");
                    var countProp = item.GetType().GetProperty("count");
                    string itemName = nameProp?.GetValue(item)?.ToString() ?? "Unknown";
                    string itemCount = countProp?.GetValue(item)?.ToString() ?? "0";

                    json += "{\"name\": \"" + itemName + "\", \"count\": " + itemCount + "}";

                    if (i < itemCounts.Count - 1)
                        json += ",";
                }

                json += "]}";

                SendResponse(json);
                DebugLog("[SocketBridge] Sent inventory: money=" + (body.master?.money ?? 0));
            }
            catch (Exception e)
            {
                SendResponse("{\"type\": \"INVENTORY\", \"money\": 0, \"equipment\": \"None\", \"items\": [], \"error\": \"" + EscapeJson(e.Message) + "\"}");
            }
        }

        public void HandleQueryObjective(string queryJson = null)
        {
            try
            {
                bool teleporterCharged = false;
                float teleporterCharge = 0.0f;
                bool bossActive = false;

                if (TeleporterInteraction.instance != null)
                {
                    teleporterCharged = TeleporterInteraction.instance.isCharged;
                    bossActive = TeleporterInteraction.instance.activationState == TeleporterInteraction.ActivationState.Charging ||
                                 TeleporterInteraction.instance.activationState == TeleporterInteraction.ActivationState.Charged;

                    // Get actual charge fraction via HoldoutZoneController (0-100%)
                    HoldoutZoneController holdoutZone = TeleporterInteraction.instance.GetComponent<HoldoutZoneController>();
                    teleporterCharge = holdoutZone != null ? holdoutZone.charge * 100f : (teleporterCharged ? 100f : 0f);
                }

                string objective = teleporterCharged ? "teleporter_charged" : (bossActive ? "charging_teleporter" : "exploring");

                string json = "{";
                json += "\"type\": \"OBJECTIVE\",";
                json += "\"objective\": \"" + objective + "\",";
                json += "\"teleporter_charged\": " + teleporterCharged.ToString().ToLower() + ",";
                json += "\"teleporter_charge\": " + teleporterCharge.ToString("F1") + ",";
                json += "\"boss_active\": " + bossActive.ToString().ToLower();
                json += "}";

                SendResponse(json);
                DebugLog("[SocketBridge] Sent objective: charge=" + teleporterCharge.ToString("F1") + "%, boss=" + bossActive);
            }
            catch (Exception e)
            {
                DebugLog("[SocketBridge] Error in HandleQueryObjective: " + e.Message);
                SendError("Error: " + e.Message);
            }
        }

        public void HandleQueryCombatStatus(string queryJson = null)
        {
            try
            {
                // Quick check if we're in a valid game state
                if (!IsInGame())
                {
                    SendResponse("{\"type\": \"COMBAT_STATUS\", \"in_combat\": false, \"enemy_count\": 0, \"nearest_enemy_distance\": 999.0, \"note\": \"Not in game\"}");
                    return;
                }

                CharacterBody body = RainflayerPlugin.GetPlayerBody();

                // Count enemies
                int enemyCount = 0;
                float closestDistance = 999f;

                if (CharacterBody.readOnlyInstancesList != null)
                {
                    foreach (var otherBody in CharacterBody.readOnlyInstancesList)
                    {
                        if (otherBody == null || otherBody.teamComponent == null)
                            continue;

                        // Skip same team
                        if (otherBody.teamComponent.teamIndex == body.teamComponent.teamIndex)
                            continue;

                        // Null check for transforms
                        if (body.transform == null || otherBody.transform == null)
                            continue;

                        float dist = Vector3.Distance(body.transform.position, otherBody.transform.position);
                        if (dist < 50f)
                        {
                            enemyCount++;
                            if (dist < closestDistance)
                                closestDistance = dist;
                        }
                    }
                }

                bool inCombat = enemyCount > 0;

                // Build JSON response
                string json = "{";
                json += "\"type\": \"COMBAT_STATUS\",";
                json += "\"in_combat\": " + inCombat.ToString().ToLower() + ",";
                json += "\"enemy_count\": " + enemyCount + ",";
                json += "\"nearest_enemy_distance\": " + closestDistance.ToString("F1");
                json += "}";

                SendResponse(json);
                DebugLog("[SocketBridge] Sent combat status: in_combat=" + inCombat + ", enemies=" + enemyCount);
            }
            catch (Exception e)
            {
                SendResponse("{\"type\": \"COMBAT_STATUS\", \"in_combat\": false, \"enemy_count\": 0, \"nearest_enemy_distance\": 999.0, \"error\": \"" + EscapeJson(e.Message) + "\"}");
            }
        }

        public void HandleQueryInteractables(string queryJson = null)
        {
            try
            {
                if (!IsInGame())
                {
                    SendResponse("{\"type\": \"INTERACTABLES\", \"interactables\": [], \"note\": \"Not in game\"}");
                    return;
                }

                var aiController = RainflayerPlugin.Instance?.aiController;
                if (aiController == null)
                {
                    SendResponse("{\"type\": \"INTERACTABLES\", \"interactables\": [], \"note\": \"No AI controller\"}");
                    return;
                }

                // Get entity detector from AI controller
                var entityDetectorField = typeof(AIController).GetField("entityDetector",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (entityDetectorField == null)
                {
                    SendResponse("{\"type\": \"INTERACTABLES\", \"interactables\": [], \"note\": \"No entity detector\"}");
                    return;
                }

                var entityDetector = entityDetectorField.GetValue(aiController) as EntityDetector;
                if (entityDetector == null)
                {
                    SendResponse("{\"type\": \"INTERACTABLES\", \"interactables\": [], \"note\": \"Entity detector not initialized\"}");
                    return;
                }

                CharacterBody body = RainflayerPlugin.GetPlayerBody();
                Vector3 aimDirection = body?.inputBank?.aimDirection ?? Vector3.forward;

                // Find interactables - safe now because we're on the main thread
                InteractableInfo[] interactables = entityDetector.FindInteractablesInRange(
                    100f,
                    fovDegrees: 180f,
                    aimDirection: aimDirection,
                    requireLineOfSight: true,
                    debug: false
                );

                // Build JSON response
                string json = "{\"type\": \"INTERACTABLES\", \"interactables\": [";

                for (int i = 0; i < interactables.Length; i++)
                {
                    InteractableInfo info = interactables[i];
                    json += "{";
                    json += "\"type\": \"" + EscapeJson(info.Type) + "\",";
                    json += "\"name\": \"" + EscapeJson(info.Name) + "\",";
                    json += "\"cost\": " + info.Cost + ",";
                    json += "\"distance\": " + info.Distance.ToString("F1");
                    json += ", \"locked\": " + info.IsLocked.ToString().ToLower();

                    if (info.Type == "shrine")
                        json += ", \"usesRemaining\": " + info.UsesRemaining;
                    if (info.Type == "teleporter")
                    {
                        json += ", \"charged\": " + info.Charged.ToString().ToLower();
                        json += ", \"chargePercent\": " + info.ChargePercent.ToString("F1");
                        json += ", \"bossActive\": " + info.BossActive.ToString().ToLower();
                    }

                    json += "}";
                    if (i < interactables.Length - 1)
                        json += ",";
                }

                json += "]}";
                SendResponse(json);
                DebugLog("[SocketBridge] Sent " + interactables.Length + " interactables");
            }
            catch (Exception e)
            {
                RainflayerPlugin.Instance?.LogError("[SocketBridge] Error in HandleQueryInteractables: " + e.Message);
                SendResponse("{\"type\": \"INTERACTABLES\", \"interactables\": [], \"error\": \"" + EscapeJson(e.Message) + "\"}");
            }
        }

        public void HandleQueryAllies(string queryJson = null)
        {
            try
            {
                if (!IsInGame())
                {
                    SendResponse("{\"type\": \"ALLIES\", \"allies\": [], \"note\": \"Not in game\"}");
                    return;
                }

                var aiController = RainflayerPlugin.Instance?.aiController;
                if (aiController == null)
                {
                    SendResponse("{\"type\": \"ALLIES\", \"allies\": [], \"note\": \"No AI controller\"}");
                    return;
                }

                // Get entity detector from AI controller
                var entityDetectorField = typeof(AIController).GetField("entityDetector",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (entityDetectorField == null)
                {
                    SendResponse("{\"type\": \"ALLIES\", \"allies\": [], \"note\": \"No entity detector\"}");
                    return;
                }

                var entityDetector = entityDetectorField.GetValue(aiController) as EntityDetector;
                if (entityDetector == null)
                {
                    SendResponse("{\"type\": \"ALLIES\", \"allies\": [], \"note\": \"Entity detector not initialized\"}");
                    return;
                }

                AllyInfo[] allies = entityDetector.GetAllies();

                // Build JSON response
                string json = "{\"type\": \"ALLIES\", \"allies\": [";

                for (int i = 0; i < allies.Length; i++)
                {
                    AllyInfo ally = allies[i];
                    json += "{";
                    json += "\"name\": \"" + EscapeJson(ally.Name) + "\",";
                    json += "\"health\": " + ally.Health.ToString("F1") + ",";
                    json += "\"maxHealth\": " + ally.MaxHealth.ToString("F1") + ",";
                    json += "\"distance\": " + ally.Distance.ToString("F1") + ",";
                    json += "\"isDowned\": " + ally.IsDowned.ToString().ToLower();
                    json += "}";
                    if (i < allies.Length - 1)
                        json += ",";
                }

                json += "]}";
                SendResponse(json);
                DebugLog("[SocketBridge] Sent " + allies.Length + " allies");
            }
            catch (Exception e)
            {
                RainflayerPlugin.Instance?.LogError("[SocketBridge] Error in HandleQueryAllies: " + e.Message);
                SendResponse("{\"type\": \"ALLIES\", \"allies\": [], \"error\": \"" + EscapeJson(e.Message) + "\"}");
            }
        }

        string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        public void HandleCommand(string queryJson)
        {
            try
            {
                DebugLog("[SocketBridge] HandleCommand received JSON: " + queryJson);

                // Parse command JSON: {"type": "COMMAND", "command": "GOTO", "args": "teleporter"}
                string command = null;
                string args = "";

                // Simple parsing: split by " to find values
                // Format: {"type": "COMMAND", "command": "VALUE", "args": "VALUE"}
                string[] parts = queryJson.Split(new string[] { "\"" }, StringSplitOptions.None);

                for (int i = 0; i < parts.Length - 2; i++)
                {
                    // Trim whitespace and check for key (without the colon)
                    string part = parts[i].Trim();

                    // When we find "command", the value is at i+2 (after the ": " part)
                    if (part == "command" && i + 2 < parts.Length)
                    {
                        command = parts[i + 2];
                        DebugLog("[SocketBridge] Found command: '" + command + "'");
                    }
                    // When we find "args", the value is at i+2 (after the ": " part)
                    else if (part == "args" && i + 2 < parts.Length)
                    {
                        args = parts[i + 2];
                        DebugLog("[SocketBridge] Found args: '" + args + "'");
                    }
                }

                DebugLog("[SocketBridge] Parsed command='" + command + "' args='" + args + "'");

                if (string.IsNullOrEmpty(command))
                {
                    SendError("Missing 'command' field");
                    return;
                }

                // Forward to AIController (already on main thread via dispatch queue)
                var aiController = RainflayerPlugin.Instance?.aiController;
                if (aiController == null)
                {
                    SendError("AI controller not found");
                    return;
                }

                // Execute command - safe because we're on the main thread
                // NOTE: QUERY_* commands send their own responses and return early,
                // so we only send COMMAND_RESPONSE for non-query commands.
                aiController.ExecuteCommand(command, args);

                // Only send response if this wasn't a query command (queries handle their own responses)
                if (!command.StartsWith("QUERY_"))
                {
                    string json = "{";
                    json += "\"type\": \"COMMAND_RESPONSE\",";
                    json += "\"command\": \"" + command + "\",";
                    json += "\"status\": \"ok\"";
                    json += "}";
                    SendResponse(json);
                    DebugLog("[SocketBridge] Executed command: " + command);
                }
            }
            catch (Exception e)
            {
                DebugLog("[SocketBridge] Error in HandleCommand: " + e.Message);
                SendError("Error: " + e.Message);
            }
        }

        void DebugLog(string message)
        {
            try
            {
                if (RainflayerPlugin.Instance != null && RainflayerPlugin.DebugMode.Value)
                {
                    RainflayerPlugin.Instance.LogDebug("[SocketBridge] " + message);
                }
            }
            catch
            {
                // Silently fail - don't crash in debug logging
            }
        }

        bool IsInGame()
        {
            try
            {
                CharacterBody body = RainflayerPlugin.GetPlayerBody();
                return body != null && body.teamComponent != null;
            }
            catch
            {
                return false;
            }
        }

        bool IsConnected()
        {
            lock (lockObj)
            {
                return connected;
            }
        }
    }
}
