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
            queryHandlers["QUERY_PILLARS"] = HandleQueryPillars;
            queryHandlers["QUERY_CONFIG"] = HandleQueryConfig;

            // Command handlers (execute via AIController, return status via socket)
            queryHandlers["COMMAND"] = HandleCommand;

            // Counterboss: Python sends a rich JSON payload (not a simple COMMAND)
            queryHandlers["COUNTERBOSS_SPAWN"] = HandleCounterbossSpawn;
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

        public void HandleQueryConfig(string queryJson = null)
        {
            string enableAi = RainflayerPlugin.EnableAIControl.Value ? "true" : "false";
            string enableCb = RainflayerPlugin.EnableCounterboss.Value ? "true" : "false";
            SendResponse("{\"type\":\"CONFIG\",\"enable_ai_control\":" + enableAi + ",\"enable_counterboss\":" + enableCb + "}");
        }

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
                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                string json = "{";
                json += "\"type\": \"OBJECTIVE\",";
                json += "\"objective\": \"" + objective + "\",";
                json += "\"scene_name\": \"" + EscapeJson(sceneName) + "\",";
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

        public void HandleQueryPillars(string queryJson = null)
        {
            try
            {
                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                bool isMoon2 = sceneName == "moon2";

                var pillarEntries = new System.Collections.Generic.List<string>();
                int total = 0, charged = 0;

                HoldoutZoneController[] holdouts = GameObject.FindObjectsOfType<HoldoutZoneController>();
                foreach (var hz in holdouts)
                {
                    if (hz == null) continue;
                    try
                    {
                        GameObject obj = hz.gameObject;
                        if (obj == null) continue;
                        string hzName = obj.name;
                        if (!hzName.ToLower().Contains("battery")) continue;
                        if (obj.GetComponent<TeleporterInteraction>() != null) continue;
                        if (!obj.activeInHierarchy) continue;  // skip bugged disabled extras

                        float charge = hz.charge;
                        bool isCharged = charge >= 1.0f;
                        total++;
                        if (isCharged) charged++;

                        pillarEntries.Add("{\"name\": \"" + EscapeJson(hzName) + "\", \"charge\": " + (charge * 100f).ToString("F1") + ", \"charged\": " + isCharged.ToString().ToLower() + "}");
                    }
                    catch { }
                }

                bool allCharged = (total > 0) && (charged == total);

                string json = "{";
                json += "\"type\": \"PILLARS\",";
                json += "\"scene\": \"" + EscapeJson(sceneName) + "\",";
                json += "\"is_moon2\": " + isMoon2.ToString().ToLower() + ",";
                json += "\"total\": " + total + ",";
                json += "\"charged\": " + charged + ",";
                json += "\"all_charged\": " + allCharged.ToString().ToLower() + ",";
                json += "\"pillars\": [" + string.Join(",", pillarEntries) + "]";
                json += "}";

                SendResponse(json);
                DebugLog("[SocketBridge] Sent pillars: " + charged + "/" + total + " charged");
            }
            catch (Exception e)
            {
                SendResponse("{\"type\": \"PILLARS\", \"error\": \"" + EscapeJson(e.Message) + "\"}");
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

        // ==================== COUNTERBOSS HANDLERS ====================

        /// <summary>
        /// Handles COUNTERBOSS_SPAWN message from Python.
        /// Parses the item list and reasoning, then delegates to CounterbossController.
        /// Called on the main thread (safe for all Unity API calls).
        ///
        /// Expected JSON format:
        /// {
        ///   "type": "COUNTERBOSS_SPAWN",
        ///   "items": [{"name": "Syringe", "count": 3}, ...],
        ///   "reasoning": "Detected attack speed build...",
        ///   "source": "llm"
        /// }
        /// </summary>
        void HandleCounterbossSpawn(string json)
        {
            try
            {
                DebugLog("[Counterboss] HandleCounterbossSpawn received");

                if (!RainflayerPlugin.EnableCounterboss.Value)
                {
                    DebugLog("[Counterboss] EnableCounterboss=false — ignoring spawn");
                    SendResponse("{\"type\": \"COUNTERBOSS_RESPONSE\", \"status\": \"disabled\"}");
                    return;
                }

                // Parse reasoning
                string reasoning = "Counter-build activated";
                int reasoningStart = json.IndexOf("\"reasoning\":");
                if (reasoningStart >= 0)
                {
                    int qStart = json.IndexOf("\"", reasoningStart + 12) + 1;
                    int qEnd = json.IndexOf("\"", qStart);
                    if (qStart > 0 && qEnd > qStart)
                        reasoning = json.Substring(qStart, qEnd - qStart);
                }

                // Parse survivor (optional — null means use config default)
                string survivor = null;
                int survivorStart = json.IndexOf("\"survivor\":");
                if (survivorStart >= 0)
                {
                    int qStart = json.IndexOf("\"", survivorStart + 11) + 1;
                    int qEnd = json.IndexOf("\"", qStart);
                    if (qStart > 0 && qEnd > qStart)
                        survivor = json.Substring(qStart, qEnd - qStart);
                }

                // Parse items array — simple extraction: find each {"name":"X","count":N} block
                var items = new List<(string name, int count)>();
                int searchFrom = 0;
                while (true)
                {
                    int nameIdx = json.IndexOf("\"name\":", searchFrom);
                    if (nameIdx < 0) break;

                    int nqStart = json.IndexOf("\"", nameIdx + 7) + 1;
                    int nqEnd = json.IndexOf("\"", nqStart);
                    if (nqStart <= 0 || nqEnd <= nqStart) break;
                    string itemName = json.Substring(nqStart, nqEnd - nqStart);

                    int countIdx = json.IndexOf("\"count\":", nqEnd);
                    int count = 1;
                    if (countIdx >= 0)
                    {
                        int numStart = countIdx + 8;
                        while (numStart < json.Length && (json[numStart] == ' ' || json[numStart] == ':'))
                            numStart++;
                        int numEnd = numStart;
                        while (numEnd < json.Length && char.IsDigit(json[numEnd]))
                            numEnd++;
                        if (numEnd > numStart)
                            int.TryParse(json.Substring(numStart, numEnd - numStart), out count);
                    }

                    if (!string.IsNullOrEmpty(itemName))
                        items.Add((itemName, count));

                    searchFrom = nqEnd + 1;
                }

                DebugLog($"[Counterboss] Parsed {items.Count} item types, reasoning length={reasoning.Length}");

                // Route to the correct controller based on current scene.
                // On moon2 (Commencement) the herald handles Phase 1; everywhere else the
                // normal teleporter counterboss handles it.
                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                bool isMoon2 = sceneName == "moon2";

                if (isMoon2 && RainflayerPlugin.EnableMithrixHerald.Value)
                {
                    var heraldController = RainflayerPlugin.Instance?.mithrixHeraldController;
                    if (heraldController == null)
                    {
                        RainflayerPlugin.Instance?.LogError("[Counterboss] MithrixHeraldController not initialized");
                        SendResponse("{\"type\": \"COUNTERBOSS_RESPONSE\", \"status\": \"error\", \"reason\": \"herald_controller_null\"}");
                        return;
                    }
                    heraldController.CacheCounterbuild(items, reasoning, survivor);
                }
                else
                {
                    var counterbossController = RainflayerPlugin.Instance?.counterbossController;
                    if (counterbossController == null)
                    {
                        RainflayerPlugin.Instance?.LogError("[Counterboss] CounterbossController not initialized");
                        SendResponse("{\"type\": \"COUNTERBOSS_RESPONSE\", \"status\": \"error\", \"reason\": \"controller_null\"}");
                        return;
                    }
                    counterbossController.CacheCounterbuild(items, reasoning, survivor);
                }
                SendResponse("{\"type\": \"COUNTERBOSS_RESPONSE\", \"status\": \"ok\"}");
            }
            catch (Exception e)
            {
                RainflayerPlugin.Instance?.LogError($"[Counterboss] HandleCounterbossSpawn error: {e.Message}");
                SendResponse("{\"type\": \"COUNTERBOSS_RESPONSE\", \"status\": \"error\", \"reason\": \"" + EscapeJson(e.Message) + "\"}");
            }
        }

        /// <summary>
        /// Emit an item_picked_up event to Python with the player's full current inventory.
        /// Call this whenever the player's inventory changes (Inventory.onInventoryChanged hook).
        /// Safe to call from any thread — SendResponse is locked.
        /// </summary>
        public void SendItemPickedUpEvent(CharacterBody body)
        {
            if (body?.inventory == null) return;

            try
            {
                // Build items JSON array
                var itemParts = new List<string>();
                int totalItems = 0;
                if (body.inventory.itemAcquisitionOrder != null)
                {
                    foreach (var idx in body.inventory.itemAcquisitionOrder)
                    {
                        ItemDef def = ItemCatalog.GetItemDef(idx);
                        int count = body.inventory.GetItemCountPermanent(idx);
                        if (count <= 0) continue;
                        totalItems += count;
                        string name = def?.name ?? "Unknown";
                        itemParts.Add("{\"name\": \"" + EscapeJson(name) + "\", \"count\": " + count + "}");
                    }
                }

                // Include the player's survivor so the LLM can tailor the counter
                string survivorName = "";
                string survivorDisplay = "";
                CharacterBody b = RainflayerPlugin.GetPlayerBody();
                if (b != null)
                {
                    var survivorDef = SurvivorCatalog.FindSurvivorDefFromBody(b.gameObject);
                    if (survivorDef != null)
                    {
                        survivorName = EscapeJson(survivorDef.cachedName);
                        survivorDisplay = EscapeJson(Language.GetString(survivorDef.displayNameToken));
                    }
                    else
                    {
                        // Body might not be in SurvivorCatalog (e.g. modded) — fall back to display name
                        survivorDisplay = EscapeJson(b.GetDisplayName());
                    }
                }

                string json = "{\"type\": \"EVENT\", \"event_type\": \"item_picked_up\", ";
                json += "\"total_items\": " + totalItems + ", ";
                json += "\"player_survivor\": \"" + survivorName + "\", ";
                json += "\"player_survivor_display\": \"" + survivorDisplay + "\", ";
                json += "\"items\": [" + string.Join(",", itemParts) + "]}";
                SendResponse(json);
                DebugLog($"[Counterboss] Sent item_picked_up: {totalItems} total items");
            }
            catch (Exception e)
            {
                DebugLog($"[Counterboss] SendItemPickedUpEvent error: {e.Message}");
            }
        }

        /// <summary>
        /// Emit a counterboss_died event to Python after the adversary is defeated.
        /// </summary>
        public void SendCounterbossDiedEvent()
        {
            SendEvent("counterboss_died");
            DebugLog("[Counterboss] Sent counterboss_died event");
        }

        /// <summary>
        /// Emit a stage_started event to Python with the player's current inventory
        /// (may be empty on stage 1). Python fires a pre-emptive LLM call so a
        /// counterbuild is cached before the teleporter starts charging.
        /// </summary>
        public void SendStageStartedEvent(CharacterBody body)
        {
            if (body == null) return;
            try
            {
                // Build player inventory snapshot (same format as item_picked_up)
                var itemParts = new List<string>();
                int totalItems = 0;
                if (body.inventory != null)
                {
                    foreach (var idx in body.inventory.itemAcquisitionOrder)
                    {
                        int count = body.inventory.GetItemCountPermanent(idx);
                        if (count <= 0) continue;
                        ItemDef def = ItemCatalog.GetItemDef(idx);
                        if (def == null) continue;
                        string name = EscapeJson(def.name);
                        itemParts.Add($"{{\"name\":\"{name}\",\"count\":{count}}}");
                        totalItems += count;
                    }
                }

                // Player survivor
                string survivorName = "";
                string survivorDisplay = "";
                if (body.master != null)
                {
                    var survivors = SurvivorCatalog.orderedSurvivorDefs;
                    foreach (var s in survivors)
                    {
                        if (s == null) continue;
                        if (s.bodyPrefab == body.gameObject ||
                            (body.gameObject.name.StartsWith(s.cachedName, StringComparison.OrdinalIgnoreCase)))
                        {
                            survivorName = EscapeJson(s.cachedName);
                            survivorDisplay = EscapeJson(Language.GetString(s.displayNameToken));
                            break;
                        }
                    }
                    // Fallback: derive from body name
                    if (string.IsNullOrEmpty(survivorName))
                    {
                        survivorName = EscapeJson(body.name.Replace("Body(Clone)", "").Replace("Body", "").Trim());
                        survivorDisplay = survivorName;
                    }
                }

                string json = "{\"type\":\"EVENT\",\"event_type\":\"stage_started\","
                            + "\"items\":[" + string.Join(",", itemParts) + "],"
                            + "\"total_items\":" + totalItems + ","
                            + "\"player_survivor\":\"" + survivorName + "\","
                            + "\"player_survivor_display\":\"" + survivorDisplay + "\"}";
                SendResponse(json);
                RainflayerPlugin.Instance?.Log($"[Counterboss] Sent stage_started: {totalItems} items, survivor={survivorDisplay}");
            }
            catch (Exception e)
            {
                RainflayerPlugin.Instance?.LogError($"[Counterboss] SendStageStartedEvent error: {e.Message}");
            }
        }

        /// <summary>
        /// Emit a run_started event to Python with the full available item catalog
        /// for this run, grouped by tier. Python uses this as the LLM's item pool
        /// so it never picks items that don't exist in the current run.
        ///
        /// Format: {"type":"EVENT","event_type":"run_started",
        ///          "items":[{"name":"Syringe","displayName":"Soldier's Syringe","tier":"tier1","desc":"Gain +15% attack speed."}, ...]}
        /// </summary>
        public void SendRunStartedEvent()
        {
            if (Run.instance == null) return;
            try
            {
                var parts = new List<string>();

                void AddList(List<PickupIndex> list)
                {
                    if (list == null) return;
                    foreach (var pickup in list)
                    {
                        PickupDef pd = PickupCatalog.GetPickupDef(pickup);
                        if (pd == null || pd.itemIndex == ItemIndex.None) continue;
                        ItemDef def = ItemCatalog.GetItemDef(pd.itemIndex);
                        if (def == null) continue;
                        // Use the item's authoritative tier, not the drop-list label
                        string tierLabel = def.tier switch
                        {
                            ItemTier.Tier1    => "tier1",
                            ItemTier.Tier2    => "tier2",
                            ItemTier.Tier3    => "tier3",
                            ItemTier.Lunar    => "lunar",
                            ItemTier.Boss     => "boss",
                            ItemTier.VoidTier1 => "void1",
                            ItemTier.VoidTier2 => "void2",
                            ItemTier.VoidTier3 => "void3",
                            _                 => "other",
                        };
                        string internalName = EscapeJson(def.name);
                        string displayName = EscapeJson(
                            !string.IsNullOrEmpty(def.nameToken)
                                ? Language.GetString(def.nameToken)
                                : def.name
                        );
                        string desc = EscapeJson(
                            !string.IsNullOrEmpty(def.pickupToken)
                                ? Language.GetString(def.pickupToken)
                                : ""
                        );
                        parts.Add($"{{\"name\":\"{internalName}\",\"displayName\":\"{displayName}\",\"tier\":\"{tierLabel}\",\"desc\":\"{desc}\"}}");
                    }
                }

                AddList(Run.instance.availableTier1DropList);
                AddList(Run.instance.availableTier2DropList);
                AddList(Run.instance.availableTier3DropList);
                AddList(Run.instance.availableBossDropList);
                AddList(Run.instance.availableLunarItemDropList);
                AddList(Run.instance.availableVoidTier1DropList);
                AddList(Run.instance.availableVoidTier2DropList);
                AddList(Run.instance.availableVoidTier3DropList);

                // Build survivor list — non-hidden survivors that have a spawnable master prefab
                var survivorParts = new List<string>();
                foreach (var survivorDef in SurvivorCatalog.orderedSurvivorDefs)
                {
                    if (survivorDef == null || survivorDef.hidden) continue;
                    string cachedName = survivorDef.cachedName;
                    if (string.IsNullOrEmpty(cachedName)) continue;
                    // Verify a monster master exists for this survivor
                    string masterName = cachedName + "MonsterMaster";
                    if (MasterCatalog.FindMasterPrefab(masterName) == null)
                        masterName = cachedName + "Master";
                    if (MasterCatalog.FindMasterPrefab(masterName) == null) continue;
                    string displayName = EscapeJson(Language.GetString(survivorDef.displayNameToken));
                    survivorParts.Add($"{{\"name\":\"{EscapeJson(cachedName)}\",\"displayName\":\"{displayName}\"}}");
                }

                string enableAi = RainflayerPlugin.EnableAIControl.Value ? "true" : "false";
                string json = "{\"type\":\"EVENT\",\"event_type\":\"run_started\",\"items\":["
                              + string.Join(",", parts)
                              + "],\"survivors\":["
                              + string.Join(",", survivorParts)
                              + "],\"enable_ai_control\":" + enableAi + "}";
                SendResponse(json);
                RainflayerPlugin.Instance?.Log($"[Counterboss] Sent run_started: {parts.Count} items, {survivorParts.Count} survivors, enable_ai_control={enableAi}");
            }
            catch (Exception e)
            {
                RainflayerPlugin.Instance?.LogError($"[Counterboss] SendRunStartedEvent error: {e.Message}");
            }
        }
    }
}
