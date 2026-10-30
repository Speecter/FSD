using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>One choice for a jump destination: the blind jump along the nose, or one of the player's GPS points.</summary>
    public struct JumpTarget
    {
        public string Name;
        public bool Blind;
        public Vector3D Coords;
        public double Distance;
        public string Status;
        public Color StatusColor;

        public bool InRange { get { return Blind || StatusColor == HudPalette.Ready; } }
    }

    /// <summary>Builds the destination list shared by the target menu and the fleet lobby: blind jump first, then GPS points nearest first.</summary>
    public static class JumpTargets
    {
        /// <param name="cruise">Supercruise-only drive: no blind jump, the first entry clears the lock and every point is lockable.</param>
        public static void Build(List<JumpTarget> list, Vector3D shipPos, double minRange, double maxRange, double blindDistance, bool cruise = false)
        {
            list.Clear();
            list.Add(new JumpTarget
            {
                Name = cruise ? "NO TARGET (CLEAR THE LOCK)" : "BLIND JUMP (ALONG THE NOSE)",
                Blind = true,
                Distance = blindDistance,
                Status = cruise ? "NONE" : "BLIND",
                StatusColor = cruise ? HudPalette.Dim : HudPalette.Primary
            });

            IMyPlayer player = MyAPIGateway.Session?.Player;
            List<IMyGps> gpsList = player != null ? MyAPIGateway.Session.GPS.GetGpsList(player.IdentityId) : null;
            if (gpsList == null)
                return;

            var gpsTargets = new List<JumpTarget>(gpsList.Count);
            foreach (var gps in gpsList)
            {
                double d = Vector3D.Distance(gps.Coords, shipPos);
                var t = new JumpTarget { Name = string.IsNullOrEmpty(gps.Name) ? "UNNAMED" : gps.Name, Coords = gps.Coords, Distance = d };
                if (cruise)
                {
                    bool near = d < CruiseLock.ReachedMeters;
                    t.Status = near ? "TOO CLOSE" : "LOCKABLE";
                    t.StatusColor = near ? HudPalette.Warning : HudPalette.Ready;
                }
                else if (d < minRange)
                {
                    t.Status = "TOO CLOSE";
                    t.StatusColor = HudPalette.Warning;
                }
                else if (d > maxRange)
                {
                    t.Status = "OUT OF RANGE";
                    t.StatusColor = HudPalette.Danger;
                }
                else
                {
                    t.Status = "IN RANGE";
                    t.StatusColor = HudPalette.Ready;
                }
                gpsTargets.Add(t);
            }

            gpsTargets.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            list.AddRange(gpsTargets);
        }

        /// <summary>Index of the entry matching the given GPS coordinates, or 0 (blind) if none.</summary>
        public static int IndexOf(List<JumpTarget> list, Vector3D? coords)
        {
            if (!coords.HasValue)
                return 0;

            for (int i = 1; i < list.Count; i++)
            {
                if (Vector3D.DistanceSquared(list[i].Coords, coords.Value) < 1.0)
                    return i;
            }
            return 0;
        }
    }
}
