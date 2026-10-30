using System;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Utils;

namespace WarpDriveMod
{
    /// <summary>
    /// Short UI cues for the HUD's events, all taken from the game's own interface sounds (no audio files of the mod's own).
    /// They share the HUD's "ping" setting, play in 2D and never throw into the HUD.
    /// </summary>
    public static class HudSounds
    {
        public static readonly MySoundPair Lock = new MySoundPair("HudLockingSuccess", true);
        public static readonly MySoundPair TargetSet = new MySoundPair("ArcHudItem", true);
        public static readonly MySoundPair TargetReached = new MySoundPair("ArcHudObjectiveComplete", true);

        private static MyEntity3DSoundEmitter emitter;

        public static void Play(MySoundPair sound)
        {
            if (!HudSettings.Current.Ping || MyAPIGateway.Utilities.IsDedicated)
                return;

            try
            {
                if (emitter == null)
                    emitter = new MyEntity3DSoundEmitter(null) { CanPlayLoopSounds = false };

                var camera = MyAPIGateway.Session?.Camera;
                if (camera != null)
                    emitter.SetPosition(camera.Position);

                emitter.PlaySound(sound, true, force2D: true, alwaysHearOnRealistic: true);
            }
            catch (Exception)
            {
            }
        }

        public static void Close()
        {
            try
            {
                if (emitter != null)
                    emitter.StopSound(true);
            }
            catch (Exception)
            {
            }
            emitter = null;
        }
    }
}
