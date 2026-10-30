using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// The supercruise GPS lock: an assist for flying to a GPS point, never a control (it does not steer or limit the ship).
    /// Drives that can jump keep one target on their jump system, so for them the lock is the jump target; supercruise-only drives
    /// keep it on the grid's warp system. The lock resets once the ship is within <see cref="ReachedMeters"/> of the point.
    /// </summary>
    public static class CruiseLock
    {
        public const double ReachedMeters = 500.0;

        private static WarpDrive JumpDrive(WarpSystem ws)
        {
            if (ws == null)
                return null;

            HyperspaceSystem hs = ws.hyperspace;
            if (hs != null && hs.HostDrive != null && hs.HostDrive.supportsHyperspace)
                return hs.HostDrive;

            return ws.capitalFSD != null ? ws.capitalFSD.HostDrive : null;
        }

        /// <summary>The locked point, if any.</summary>
        public static bool TryGet(WarpSystem ws, out Vector3D coords, out string name)
        {
            coords = Vector3D.Zero;
            name = "";
            if (ws == null)
                return false;

            WarpDrive jumpDrive = JumpDrive(ws);
            if (jumpDrive != null)
            {
                var jump = new HyperspaceControls.HyperspaceAdapter(jumpDrive);
                if (!jump.IsValid || jump.Mode != HyperspaceSystem.JumpMode.GpsWaypoint || !jump.SelectedGpsCoords.HasValue)
                    return false;

                coords = jump.SelectedGpsCoords.Value;
                name = jump.SelectedGpsName;
                return true;
            }

            if (!ws.CruiseTargetCoords.HasValue)
                return false;

            coords = ws.CruiseTargetCoords.Value;
            name = ws.CruiseTargetName;
            return true;
        }

        /// <summary>Locks a point on a supercruise-only drive (jump-capable drives lock through their jump target).</summary>
        public static void Set(WarpSystem ws, Vector3D coords, string name)
        {
            if (ws == null)
                return;

            ws.CruiseTargetCoords = coords;
            ws.CruiseTargetName = name ?? string.Empty;
        }

        /// <summary>Drops the lock. For a jump-capable drive that is idle this is the same as picking the blind jump in the target menu.</summary>
        public static void Clear(WarpSystem ws)
        {
            if (ws == null)
                return;

            ws.CruiseTargetCoords = null;
            ws.CruiseTargetName = string.Empty;

            WarpDrive jumpDrive = JumpDrive(ws);
            if (jumpDrive == null)
                return;

            var jump = new HyperspaceControls.HyperspaceAdapter(jumpDrive);
            if (jump.IsValid && jump.IsIdle)
            {
                jump.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                jump.SelectedGpsCoords = null;
                jump.SelectedGpsName = string.Empty;
            }
        }
    }
}
