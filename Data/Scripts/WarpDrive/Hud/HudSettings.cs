using System;
using ProtoBuf;
using Sandbox.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Per-player HUD preferences, stored in local storage. Deliberately not part of the server's Settings class:
    /// the server overwrites that on every client.
    /// </summary>
    [ProtoContract]
    public class HudSettings
    {
        private const string FileName = "FsdHud.xml";

        public static HudSettings Current = new HudSettings();

        [ProtoMember(1)]
        public bool Enabled { get; set; }

        [ProtoMember(2)]
        public float Scale { get; set; }

        [ProtoMember(3)]
        public float Opacity { get; set; }

        [ProtoMember(4)]
        public bool Ping { get; set; }

        [ProtoMember(5)]
        public bool Effects { get; set; }

        public HudSettings()
        {
            Reset();
        }

        public void Reset()
        {
            Enabled = true;
            Scale = 1f;
            Opacity = 1f;
            Ping = true;
            Effects = true;
        }

        private void Clamp()
        {
            Scale = MathHelper.Clamp(Scale, 0.6f, 1.5f);
            Opacity = MathHelper.Clamp(Opacity, 0.2f, 1f);
        }

        public static void Load()
        {
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInLocalStorage(FileName, typeof(HudSettings)))
                    return;

                using (var reader = MyAPIGateway.Utilities.ReadFileInLocalStorage(FileName, typeof(HudSettings)))
                {
                    var loaded = MyAPIGateway.Utilities.SerializeFromXML<HudSettings>(reader.ReadToEnd());
                    if (loaded != null)
                    {
                        loaded.Clamp();
                        Current = loaded;
                    }
                }
            }
            catch (Exception)
            {
                Current = new HudSettings();
            }
        }

        public static void Save()
        {
            try
            {
                Current.Clamp();
                using (var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(FileName, typeof(HudSettings)))
                    writer.Write(MyAPIGateway.Utilities.SerializeToXML(Current));
            }
            catch (Exception)
            {
            }
        }
    }
}
