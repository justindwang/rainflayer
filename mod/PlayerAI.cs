using EntityStates;
using EntityStates.AI.Walker;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;

namespace Rainflayer
{
    /// <summary>
    /// Custom BaseAI component for the AI-controlled player.
    /// Based on PlayerBots' PlayerBotBaseAI approach.
    /// </summary>
    public class PlayerAI : BaseAI
    {
        public PlayerAI()
        {
            this.scanState = new SerializableEntityStateType(typeof(Wander));
            this.fullVision = true;
            this.aimVectorDampTime = 0.01f;
            this.aimVectorMaxSpeed = 180f;
            this.enemyAttentionDuration = 5f;
            this.neverRetaliateFriendlies = true;
            this.selectedSkilldriverName = "";
        }

        public override void OnBodyDeath(CharacterBody characterBody)
        {
            if (this.body)
            {
                RainflayerPlugin.Instance?.LogDebug($"[PlayerAI] Body died: {characterBody.GetDisplayName()}");
            }
        }
    }
}
