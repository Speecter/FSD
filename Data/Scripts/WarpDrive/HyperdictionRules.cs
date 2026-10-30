using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>The one definition of "this machine decides" for the server-authoritative parts of the mod.</summary>
    public static class NetAuthority
    {
        public static bool IsAuthority { get { return MyAPIGateway.Multiplayer.IsServer || !MyAPIGateway.Multiplayer.MultiplayerActive; } }
    }

    /// <summary>
    /// What hyperdiction does to the ships it touches: a ship that was pulled out of hyperspace cannot jump again for a while
    /// (supercruise, hyperspace or capital FSD), and an engaged field stops hostile ships inside it from jumping into hyperspace.
    /// Every machine learns of a drop through the same Drop message, so each keeps its own copy of the lockout.
    /// </summary>
    public static class HyperdictionRules
    {
        public const double LOCKOUT_SECONDS = 30.0;
        public const double AFTERMATH_SECONDS = 10.0;   // power blackout and FSD cooldown after the drop (the HUD glitch fades out over it too)

        private static readonly Dictionary<long, long> lockoutUntilTick = new Dictionary<long, long>();

        public static void StartLockout(MyCubeGrid mainGrid)
        {
            var session = WarpDriveSession.instance;
            if (mainGrid == null || session == null)
                return;

            lockoutUntilTick[mainGrid.EntityId] = session.runtime + (long)(LOCKOUT_SECONDS * 60.0);
        }

        public static double LockoutSecondsLeft(MyCubeGrid mainGrid)
        {
            var session = WarpDriveSession.instance;
            long until;
            if (mainGrid == null || session == null || !lockoutUntilTick.TryGetValue(mainGrid.EntityId, out until))
                return 0.0;

            long left = until - session.runtime;
            if (left <= 0L)
            {
                lockoutUntilTick.Remove(mainGrid.EntityId);
                return 0.0;
            }
            return left / 60.0;
        }

        /// <summary>Why this ship may not start a jump right now because of hyperdiction, or null when nothing stops it.</summary>
        public static string JumpBlocker(MyCubeGrid mainGrid, bool hyperspace)
        {
            if (mainGrid == null)
                return null;

            double left = LockoutSecondsLeft(mainGrid);
            if (left > 0.0)
                return "FSD LOCKED OUT AFTER HYPERDICTION - " + (int)Math.Ceiling(left) + " S";

            if (hyperspace)
            {
                var session = HyperdictionSession.Instance;
                if (session != null && session.IsInsideEnemyField(mainGrid))
                    return "HYPERDICTION FIELD ACTIVE - HYPERSPACE JUMP BLOCKED";
            }
            return null;
        }

        public static void Close()
        {
            lockoutUntilTick.Clear();
        }
    }
}
