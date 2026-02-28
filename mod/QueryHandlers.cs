using RoR2;
using UnityEngine;

namespace Rainflayer
{
    /// <summary>
    /// Query handlers for AIController.
    /// Delegates to SocketBridge for socket responses.
    /// </summary>
    public class QueryHandlers
    {
        private readonly AIController controller;

        public QueryHandlers(AIController controller)
        {
            this.controller = controller;
        }

        /// <summary>
        /// Handle QUERY_INVENTORY command.
        /// Delegates to SocketBridge for response.
        /// </summary>
        public void HandleQueryInventory()
        {
            var socketBridge = controller.GetComponent<SocketBridge>();
            if (socketBridge != null)
            {
                socketBridge.HandleQueryInventory();
            }
        }

        /// <summary>
        /// Handle QUERY_INTERACTABLES command.
        /// Delegates to SocketBridge for response.
        /// </summary>
        public void HandleQueryInteractables()
        {
            var socketBridge = controller.GetComponent<SocketBridge>();
            if (socketBridge != null)
            {
                socketBridge.HandleQueryInteractables();
            }
            else
            {
                RainflayerPlugin.Instance?.Log("[INTERACTABLES] ERROR: SocketBridge is NULL!");
            }
        }

        /// <summary>
        /// Handle QUERY_ALLIES command.
        /// Delegates to SocketBridge for response.
        /// </summary>
        public void HandleQueryAllies()
        {
            var socketBridge = controller.GetComponent<SocketBridge>();
            if (socketBridge != null)
            {
                socketBridge.HandleQueryAllies();
            }
            else
            {
                RainflayerPlugin.Instance?.Log("[ALLIES] ERROR: SocketBridge is NULL!");
            }
        }

        /// <summary>
        /// Handle QUERY_OBJECTIVE command.
        /// Delegates to SocketBridge for response.
        /// </summary>
        public void HandleQueryObjective()
        {
            var socketBridge = controller.GetComponent<SocketBridge>();
            if (socketBridge != null)
            {
                socketBridge.HandleQueryObjective();
            }
        }

        /// <summary>
        /// Handle QUERY_COMBAT_STATUS command.
        /// Delegates to SocketBridge for response.
        /// </summary>
        public void HandleQueryCombatStatus()
        {
            var socketBridge = controller.GetComponent<SocketBridge>();
            if (socketBridge != null)
            {
                socketBridge.HandleQueryCombatStatus();
            }
        }
    }
}
