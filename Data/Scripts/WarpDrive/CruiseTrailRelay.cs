using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// The supercruise trail beyond the sync distance. A client only has ships the server streams to it (a few km), so the server sends
    /// each player, three times a second, where every ship in supercruise within 1000 km of them is, which way it flies and how fast.
    /// The client draws the trail for those it does not have loaded itself, moving it along between updates.
    /// </summary>
    public static class CruiseTrailRelay
    {
        public const double RANGE_METERS = 1000000.0;
        private const int SEND_INTERVAL_TICKS = 20;
        private const int STALE_TICKS = 60;   // three missed updates: the ship left supercruise or the range

        private class Remote
        {
            public Vector3D Position;
            public Vector3D Forward;
            public Vector3D Down;
            public double SpeedPt;
            public bool Small;
            public long ReceivedTick;
        }

        private static readonly Dictionary<long, Remote> remotes = new Dictionary<long, Remote>();
        private static readonly List<long> stale = new List<long>();
        private static readonly List<CruiseTrailEntry> cruising = new List<CruiseTrailEntry>();
        private static readonly List<IMyPlayer> players = new List<IMyPlayer>();

        // ------------------------------------------------------------------ server

        public static void ServerUpdate(List<WarpSystem> systems, long runtime)
        {
            if (!MyAPIGateway.Multiplayer.MultiplayerActive || !MyAPIGateway.Multiplayer.IsServer || runtime % SEND_INTERVAL_TICKS != 0)
                return;

            cruising.Clear();
            for (int i = 0; i < systems.Count; i++)
            {
                Vector3D position, forward, down;
                double speedPt;
                bool small;
                long id;
                if (systems[i].TryGetTrailState(out id, out position, out forward, out down, out speedPt, out small))
                {
                    cruising.Add(new CruiseTrailEntry
                    {
                        entityId = id,
                        position = position,
                        forward = forward,
                        down = down,
                        speedPt = (float)speedPt,
                        small = small
                    });
                }
            }

            if (cruising.Count == 0)
                return;

            players.Clear();
            MyAPIGateway.Players.GetPlayers(players);
            ulong self = MyAPIGateway.Multiplayer.MyId;   // a hosting player has every ship already

            for (int p = 0; p < players.Count; p++)
            {
                IMyPlayer player = players[p];
                if (player == null || player.IsBot || player.SteamUserId == self)
                    continue;

                Vector3D at = player.GetPosition();
                List<CruiseTrailEntry> near = null;
                for (int i = 0; i < cruising.Count; i++)
                {
                    if (Vector3D.DistanceSquared(at, cruising[i].position) > RANGE_METERS * RANGE_METERS)
                        continue;
                    if (near == null)
                        near = new List<CruiseTrailEntry>();
                    near.Add(cruising[i]);
                }

                if (near == null)
                    continue;

                byte[] data = MyAPIGateway.Utilities.SerializeToBinary(new CruiseTrailMessage { ships = near });
                MyAPIGateway.Multiplayer.SendMessageTo(WarpDriveSession.cruiseTrailPacket, data, player.SteamUserId);
            }
        }

        // ------------------------------------------------------------------ client

        public static void Receive(byte[] data, long runtime)
        {
            CruiseTrailMessage msg;
            try
            {
                msg = MyAPIGateway.Utilities.SerializeFromBinary<CruiseTrailMessage>(data);
            }
            catch (Exception)
            {
                return;
            }

            if (msg?.ships == null)
                return;

            for (int i = 0; i < msg.ships.Count; i++)
            {
                CruiseTrailEntry e = msg.ships[i];
                Remote r;
                if (!remotes.TryGetValue(e.entityId, out r))
                {
                    r = new Remote();
                    remotes[e.entityId] = r;
                }

                r.Position = e.position;
                r.Forward = e.forward;
                r.Down = e.down;
                r.SpeedPt = e.speedPt;
                r.Small = e.small;
                r.ReceivedTick = runtime;
            }
        }

        public static void Draw(long runtime)
        {
            if (remotes.Count == 0)
                return;

            stale.Clear();
            foreach (var pair in remotes)
            {
                Remote r = pair.Value;
                long age = runtime - r.ReceivedTick;
                if (age > STALE_TICKS)
                {
                    stale.Add(pair.Key);
                    continue;
                }

                // A ship this client has loaded draws its own trail
                if (MyAPIGateway.Entities.GetEntityById(pair.Key) != null)
                    continue;

                // Carried along between updates at the reported speed (metres per tick)
                Vector3D position = r.Position + r.Forward * (r.SpeedPt * Math.Max(0L, age));
                WarpSystem.DrawTrail(position, r.Forward, r.Down, r.SpeedPt, r.Small);
            }

            for (int i = 0; i < stale.Count; i++)
                remotes.Remove(stale[i]);
        }

        public static void Clear()
        {
            remotes.Clear();
            cruising.Clear();
            players.Clear();
        }
    }
}
