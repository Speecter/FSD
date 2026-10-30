using Sandbox.ModAPI;

namespace WarpDriveMod
{
    /// <summary>
    /// Effects meant only for the people riding a ship (the cruise streaks, the hidden sun)
    /// show only while the camera is on board: the seat, a turret, a camera block or the player's own character. A spectator camera
    /// sees the world as everyone outside the ship does, even when the player's character is still seated.
    /// </summary>
    public static class LocalView
    {
        public static bool IsOnboardCamera
        {
            get
            {
                var session = MyAPIGateway.Session;
                if (session == null || session.IsCameraUserControlledSpectator)
                    return false;

                var controller = session.CameraController;
                return controller is Sandbox.ModAPI.Ingame.IMyShipController       // cockpits, remote controls, 3rd person on a ship
                    || controller is Sandbox.ModAPI.Ingame.IMyLargeTurretBase
                    || controller is VRage.Game.ModAPI.IMyCharacter
                    || controller is Sandbox.ModAPI.Ingame.IMyCameraBlock;
            }
        }
    }
}
