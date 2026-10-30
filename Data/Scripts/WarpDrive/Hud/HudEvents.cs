using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace WarpDriveMod
{
    public enum HudSeverity
    {
        Info = 0,
        Success = 1,
        Warning = 2,
        Critical = 3
    }

    public enum HudRoute
    {
        /// <summary>For code that runs on every machine in lockstep: shown only where the local player is the target. Nothing is sent.</summary>
        Local = 0,
        /// <summary>For server-authoritative code: the server works out who is on board and sends each remote player the event.</summary>
        Server = 1
    }

    [ProtoContract]
    public class HudEventMessage
    {
        [ProtoMember(1)]
        public string text { get; set; }

        [ProtoMember(2)]
        public int severity { get; set; }

        [ProtoMember(3)]
        public float seconds { get; set; }
    }

    /// <summary>Replacement for the vanilla notifications: routes a message to the right players exactly once and hands it to the HUD.</summary>
    public static class HudEvents
    {
        private static readonly List<IMyPlayer> players = new List<IMyPlayer>();
        private static readonly List<IMyCubeGrid> group = new List<IMyCubeGrid>();

        private static readonly string[] CriticalKeywords =
        {
            "HYPERDICTION DETECTED", "EMERGENCY", "OVERHEAT", "POWER FAIL", "POWER LOST", "CONDUIT",
            "CONTROL LOST", "COLLAPSED", "OCCUPANT NOT SEATED", "FS DRIVE OFFLINE", "DRIVE DAMAGED", "FSD DAMAGED", "FSD FAILURE"
        };

        /// <summary>Severity from the old notification font colour, raised to Critical for the messages that mean the jump is lost or in danger.</summary>
        public static HudSeverity Classify(string text, string font)
        {
            string upper = text.ToUpperInvariant();
            for (int i = 0; i < CriticalKeywords.Length; i++)
            {
                if (upper.Contains(CriticalKeywords[i]))
                    return HudSeverity.Critical;
            }

            if (font == "Red")
                return HudSeverity.Warning;
            if (font == "Green")
                return HudSeverity.Success;
            return HudSeverity.Info;
        }

        /// <summary>Feedback for something the local player just did (button clicks and the like): always shown on this machine.</summary>
        public static void PostLocal(string text, int milliseconds, string font)
        {
            Post(0L, 0L, text, Classify(text, font), HudRoute.Local, milliseconds / 1000f);
        }

        /// <param name="gridId">The ship the message is about: it goes to whoever is aboard (seated or controlling it remotely).
        /// 0 with playerId 0 means "this machine, always" (button feedback).</param>
        /// <param name="playerId">The player who caused it. Also reaches them when they are not aboard any ship (e.g. at a terminal),
        /// but never while they fly a different ship - a fleet leader must not get every wing ship's messages.</param>
        public static void Post(long gridId, long playerId, string text, HudSeverity severity, HudRoute route, float seconds = 4f)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (route == HudRoute.Local)
            {
                if (IsLocalTarget(gridId, playerId))
                    Deliver(text, severity, seconds);
                return;
            }

            if (!MyAPIGateway.Multiplayer.IsServer)
                return;

            players.Clear();
            MyAPIGateway.Players.GetPlayers(players);
            CollectGroup(gridId);

            byte[] data = null;
            for (int i = 0; i < players.Count; i++)
            {
                IMyPlayer p = players[i];
                if (!IsRecipient(p, gridId, playerId))
                    continue;

                IMyPlayer local = MyAPIGateway.Session?.Player;
                if (local != null && p.IdentityId == local.IdentityId && !MyAPIGateway.Utilities.IsDedicated)
                {
                    Deliver(text, severity, seconds);
                    continue;
                }

                if (data == null)
                    data = MyAPIGateway.Utilities.SerializeToBinary(new HudEventMessage { text = text, severity = (int)severity, seconds = seconds });
                MyAPIGateway.Multiplayer.SendMessageTo(WarpDriveSession.hudEventPacket, data, p.SteamUserId);
            }
        }

        public static void Receive(byte[] data)
        {
            try
            {
                var msg = MyAPIGateway.Utilities.SerializeFromBinary<HudEventMessage>(data);
                if (msg != null)
                    Deliver(msg.text, (HudSeverity)msg.severity, msg.seconds);
            }
            catch (Exception)
            {
            }
        }

        private static void Deliver(string text, HudSeverity severity, float seconds)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            FsdHud.PushEvent(text, severity, seconds);
        }

        private static bool IsLocalTarget(long gridId, long playerId)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return false;

            IMyPlayer local = MyAPIGateway.Session?.Player;
            if (local == null)
                return false;

            if (gridId == 0L && playerId == 0L)
                return true;

            CollectGroup(gridId);
            return IsRecipient(local, gridId, playerId);
        }

        /// <summary>The routing rule for both routes. Expects CollectGroup(gridId) to have run.</summary>
        private static bool IsRecipient(IMyPlayer player, long gridId, long playerId)
        {
            if (player == null)
                return false;

            if (gridId != 0L && group.Count > 0)
            {
                IMyCubeGrid aboard = AboardGrid(player);
                if (aboard != null)
                    return group.Contains(aboard);

                // Not aboard anything: only the player who caused it
                return playerId > 0 && player.IdentityId == playerId;
            }

            return playerId > 0 ? player.IdentityId == playerId : gridId == 0L;
        }

        private static void CollectGroup(long gridId)
        {
            group.Clear();
            var grid = MyAPIGateway.Entities.GetEntityById(gridId) as IMyCubeGrid;
            if (grid != null)
                MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Physical, group);
        }

        /// <summary>The ship a player is flying or sitting in (remote control counts), null when on foot.</summary>
        private static IMyCubeGrid AboardGrid(IMyPlayer player)
        {
            var block = player.Controller?.ControlledEntity?.Entity as IMyCubeBlock ?? player.Character?.Parent as IMyCubeBlock;
            return block?.CubeGrid;
        }
    }
}
