using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using Sandbox.ModAPI;

namespace WarpDriveMod
{
    [ProtoContract]
    public class Settings
    {
        public static Settings instance;
        
        public const string filename = "FSDriveConfig.cfg";

        [ProtoMember(1)]
        public double maxSpeed;

        [ProtoMember(2)]
        public double startSpeed;

        [ProtoMember(3)]
        public float maxHeat;

        [ProtoMember(4)]
        public float heatGain;

        [ProtoMember(5)]
        public float heatDissipationDrive;

        [ProtoMember(6)]
        public float baseRequiredPower;

        [ProtoMember(7)]
        public float baseRequiredPowerSmall;

        [ProtoMember(8)]
        public int powerRequirementMultiplier;

        [ProtoMember(9)]
        public float powerRequirementBySpeedDeviderLarge;

        [ProtoMember(10)]
        public float powerRequirementBySpeedDeviderSmall;

        [ProtoMember(11)]
        public bool AllowInGravity;

        [ProtoMember(13)]
        public bool AllowToDetectEnemyGrids;

        [ProtoMember(14)]
        public double DetectEnemyGridInRange;

        [ProtoMember(15)]
        public double DelayJumpIfEnemyIsNear;

        [ProtoMember(16)]
        public double DelayJump;

        [ProtoMember(17)]
        public float AllowInGravityMax;

        [ProtoMember(18)]
        public double AllowInGravityMaxSpeed;

        [ProtoMember(19)]
        public double AllowInGravityMinAltitude;

        [ProtoMember(21)]
        public double PrototechJump;

        [ProtoMember(22)]
        public float HeatSinkDissipation; 

        [ProtoMember(23)]
        public float HeatSinkCapacityBonus;

        [ProtoMember(24)]
        public bool AllowInSafeZone;

        public static Settings GetDefaults()
        {
            return new Settings
            {
                maxSpeed = 50000 / 60d, //Maximum supercruise speed, in km/s. Default is 50km/s.
                startSpeed = 1000 / 60d, //Initial cruise speed in km/s. Allows up to maxSpeed. Default is 1km/s.
                maxHeat = 1f, //Multiplier for FSD heat capacities. Does not affect Heat Sinks. Default is 1.
                heatGain = 1f, //Multiplier for global heat generation. Set to 0 to disable heat entirely. Default is 1.
                heatDissipationDrive = 1 / 60f, //Heat/sec FSD dissipation rate. Does not affect Heat Sink dissipation. SG is 1/5 of this. This value is 10x ingame. Default is 1.
                baseRequiredPower = 20f, //The absolute minimum power a LG drive can use to spool. Measured in MW. Default is 20.
                baseRequiredPowerSmall = 20f, //The absolute minimum power a SG drive can use to spool. Measured in MW. Default is 20.
                powerRequirementMultiplier = 2, //Power multiplier for higher speeds while actively cruising. Default is 2.
                powerRequirementBySpeedDeviderLarge = 3f, //General power divider for LG FSDs while actively cruising. Default is 3.
                powerRequirementBySpeedDeviderSmall = 3f, //General power divider for SG FSDs while actively cruising. Default is 3.
                AllowInGravity = true, //Allows cruise in gravity. Ship will drop to "AllowInGravityMaxSpeed" and stops if altitude is below "AllowInGravityMinAltitude" or gravity reaches "AllowInGravityMax". Default is true.
                AllowToDetectEnemyGrids = false, //If true, spooling time is delayed to "DelayJumpIfEnemyIsNear" when an enemy is within "DetectEnemyGridInRange". Default is false.
                DetectEnemyGridInRange = 2000, //Detection range in meters for enemy grids if "AllowToDetectEnemyGrids=true". Default is 2000m.
                DelayJumpIfEnemyIsNear = 30, //Spooling time for all drives when an enemy is within "DetectEnemyGridInRange". Max 90s, min is "DelayJump". Default is 30s.
                DelayJump = 10, //Spooling time for standard drives. Max 90s, min 3s. Default is 10s.
                PrototechJump = 5, //Spooling time for Prototech drives. Max 90s, min 3s. Default is 5s.
                AllowInGravityMax = 0.2f, //Maximum allowed gravity if "AllowInGravity=true". Default is 0.2g.
                AllowInGravityMaxSpeed = 3000 / 60d, //Maximum allowed speed in km/s while in gravity if "AllowInGravity=true". Don't set too high or performance may degrade. Default is 3km/s.
                AllowInGravityMinAltitude = 3000d, //Minimum allowed altitude on planets in meters if "AllowInGravityMax" doesn't stop you first. Bare minimum allowed is 300m. Default is 3000m.
                HeatSinkDissipation = 1 / 60f, //Heat/sec Heat Sink dissipation rate. Does not affect FSD dissipation. SG is 1/5 of this. This value is 10x ingame. Default is 1.
                HeatSinkCapacityBonus = 50f, //Flat heat capacity added per active vent. SG is 1/5 of this. This value is 10x ingame. Default is 50.
                AllowInSafeZone = false //Allows drives to spool and operate inside safe zones. Warning: enabling this can allow for economy station theft. Default is false.
            };
        }

        public static Settings GetShared()
        {
            if (instance == null)
            {
                instance = (MyAPIGateway.Utilities.IsDedicated || MyAPIGateway.Multiplayer.IsServer) ? Load() : GetDefaults();
                HeatSink.heatEnabled = instance.heatGain > 0f;
            }
            return instance;
        }

        public void CopyFrom(Settings m)
        {
            maxSpeed = m.maxSpeed;
            startSpeed = m.startSpeed;
            maxHeat = m.maxHeat;
            heatGain = m.heatGain;
            HeatSink.heatEnabled = heatGain > 0f;
            heatDissipationDrive = m.heatDissipationDrive;
            baseRequiredPower = m.baseRequiredPower;
            baseRequiredPowerSmall = m.baseRequiredPowerSmall;
            powerRequirementMultiplier = m.powerRequirementMultiplier;
            powerRequirementBySpeedDeviderLarge = m.powerRequirementBySpeedDeviderLarge;
            powerRequirementBySpeedDeviderSmall = m.powerRequirementBySpeedDeviderSmall;
            AllowInGravity = m.AllowInGravity;
            AllowToDetectEnemyGrids = m.AllowToDetectEnemyGrids;
            DetectEnemyGridInRange = m.DetectEnemyGridInRange;
            DelayJumpIfEnemyIsNear = m.DelayJumpIfEnemyIsNear;
            DelayJump = m.DelayJump;
            PrototechJump = m.PrototechJump;
            AllowInGravityMax = m.AllowInGravityMax;
            AllowInGravityMaxSpeed = m.AllowInGravityMaxSpeed;
            AllowInGravityMinAltitude = m.AllowInGravityMinAltitude;
            HeatSinkDissipation = m.HeatSinkDissipation;
            HeatSinkCapacityBonus = m.HeatSinkCapacityBonus;
            AllowInSafeZone = m.AllowInSafeZone;
        }

        public static Settings Load()
        {
            Settings configDefault = GetConfigDefault();
            Settings settings;
            bool save;

            try
            {
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(filename, typeof(Settings)))
                {
                    TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(filename, typeof(Settings));
                    string text = reader.ReadToEnd();
                    reader.Close();

                    settings = MyAPIGateway.Utilities.SerializeFromXML<Settings>(text);
                    save = HealConfigFile(text, settings, configDefault);
					}
                    else
					{
                    settings = GetConfigDefault();
                    save = true;
                }

                if (Validate(settings, configDefault))
                    save = true;
            }
            catch (Exception)
            {
                settings = GetConfigDefault();
                save = true;
            }

            if (save)
                Save(settings);

            settings.maxSpeed = settings.maxSpeed * 1000 / 60d;
            settings.startSpeed = settings.startSpeed * 1000 / 60d;
            settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 1000 / 60d;
            settings.heatDissipationDrive /= 60f;
            settings.HeatSinkDissipation /= 60f;

            return settings;
        }

        private static Settings GetConfigDefault()
        {
            Settings settings = GetDefaults();
            settings.maxSpeed = settings.maxSpeed * 60d / 1000;
            settings.startSpeed = settings.startSpeed * 60d / 1000;
            settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 60d / 1000;
            settings.heatDissipationDrive *= 60f;
            settings.HeatSinkDissipation *= 60f;
            return settings;
        }

        private static bool Validate(Settings saved, Settings defaults)
        {
            bool changed = false;

            if (!(saved.maxSpeed > 0))
            {
                saved.maxSpeed = defaults.maxSpeed;
                changed = true;
            }

            if (!(saved.startSpeed > 0) || saved.startSpeed > saved.maxSpeed)
            {
                saved.startSpeed = Math.Min(defaults.startSpeed, saved.maxSpeed);
                changed = true;
            }

            if (!(saved.maxHeat > 0f && saved.maxHeat <= 200f))
            {
                saved.maxHeat = defaults.maxHeat;
                changed = true;
            }

            if (saved.heatDissipationDrive < 0f)
            {
                saved.heatDissipationDrive = 0f;
                changed = true;
        }

            if (!(saved.baseRequiredPower >= 0f))
            {
                saved.baseRequiredPower = defaults.baseRequiredPower;
                changed = true;
            }

            if (!(saved.baseRequiredPowerSmall >= 0f))
            {
                saved.baseRequiredPowerSmall = defaults.baseRequiredPowerSmall;
                changed = true;
            }

            if (saved.powerRequirementMultiplier < 0)
            {
                saved.powerRequirementMultiplier = defaults.powerRequirementMultiplier;
                changed = true;
            }

            if (!(saved.powerRequirementBySpeedDeviderLarge > 0f))
            {
                saved.powerRequirementBySpeedDeviderLarge = defaults.powerRequirementBySpeedDeviderLarge;
                changed = true;
            }

            if (!(saved.powerRequirementBySpeedDeviderSmall > 0f))
            {
                saved.powerRequirementBySpeedDeviderSmall = defaults.powerRequirementBySpeedDeviderSmall;
                changed = true;
            }

            if (!(saved.HeatSinkDissipation > 0f))
            {
                saved.HeatSinkDissipation = defaults.HeatSinkDissipation;
                changed = true;
            }

            if (!(saved.HeatSinkCapacityBonus > 0f))
            {
                saved.HeatSinkCapacityBonus = defaults.HeatSinkCapacityBonus;
                changed = true;
            }

            if (!(saved.DelayJump >= 3 && saved.DelayJump <= 90))
            {
                saved.DelayJump = defaults.DelayJump;
                changed = true;
            }

            if (!(saved.PrototechJump >= 3 && saved.PrototechJump <= 90))
            {
                saved.PrototechJump = defaults.PrototechJump;
                changed = true;
            }

            if (!(saved.DelayJumpIfEnemyIsNear <= 90))
            {
                saved.DelayJumpIfEnemyIsNear = defaults.DelayJumpIfEnemyIsNear;
                changed = true;
            }

            double minEnemyDelay = Math.Max(saved.DelayJump, saved.PrototechJump);
            if (saved.DelayJumpIfEnemyIsNear < minEnemyDelay)
            {
                saved.DelayJumpIfEnemyIsNear = minEnemyDelay;
                changed = true;
            }

            if (!(saved.DetectEnemyGridInRange > 0))
            {
                saved.DetectEnemyGridInRange = defaults.DetectEnemyGridInRange;
                changed = true;
            }

            if (!(saved.AllowInGravityMax >= 0f))
            {
                saved.AllowInGravityMax = defaults.AllowInGravityMax;
                changed = true;
            }

            double minGravitySpeed = Math.Min(1, saved.maxSpeed);
            if (double.IsNaN(saved.AllowInGravityMaxSpeed))
            {
                saved.AllowInGravityMaxSpeed = Math.Min(defaults.AllowInGravityMaxSpeed, saved.maxSpeed);
                changed = true;
            }
            else if (saved.AllowInGravityMaxSpeed < minGravitySpeed)
            {
                saved.AllowInGravityMaxSpeed = minGravitySpeed;
                changed = true;
            }
            else if (saved.AllowInGravityMaxSpeed > saved.maxSpeed)
            {
                saved.AllowInGravityMaxSpeed = saved.maxSpeed;
                changed = true;
            }

            if (!(saved.AllowInGravityMinAltitude >= 300))
            {
                saved.AllowInGravityMinAltitude = 300;
                changed = true;
            }

            return changed;
        }

        private static bool HealConfigFile(string xml, Settings saved, Settings defaults)
        {
            bool healed = false;
            Func<string, bool> missing = name => xml == null || !xml.Contains("<" + name + ">");

            if (missing("maxSpeed")) { saved.maxSpeed = defaults.maxSpeed; healed = true; }
            if (missing("startSpeed")) { saved.startSpeed = defaults.startSpeed; healed = true; }
            if (missing("maxHeat")) { saved.maxHeat = defaults.maxHeat; healed = true; }
            if (missing("heatGain")) { saved.heatGain = defaults.heatGain; healed = true; }
            if (missing("heatDissipationDrive")) { saved.heatDissipationDrive = defaults.heatDissipationDrive; healed = true; }
            if (missing("baseRequiredPower")) { saved.baseRequiredPower = defaults.baseRequiredPower; healed = true; }
            if (missing("baseRequiredPowerSmall")) { saved.baseRequiredPowerSmall = defaults.baseRequiredPowerSmall; healed = true; }
            if (missing("powerRequirementMultiplier")) { saved.powerRequirementMultiplier = defaults.powerRequirementMultiplier; healed = true; }
            if (missing("powerRequirementBySpeedDeviderLarge")) { saved.powerRequirementBySpeedDeviderLarge = defaults.powerRequirementBySpeedDeviderLarge; healed = true; }
            if (missing("powerRequirementBySpeedDeviderSmall")) { saved.powerRequirementBySpeedDeviderSmall = defaults.powerRequirementBySpeedDeviderSmall; healed = true; }
            if (missing("AllowInGravity")) { saved.AllowInGravity = defaults.AllowInGravity; healed = true; }
            if (missing("AllowToDetectEnemyGrids")) { saved.AllowToDetectEnemyGrids = defaults.AllowToDetectEnemyGrids; healed = true; }
            if (missing("DetectEnemyGridInRange")) { saved.DetectEnemyGridInRange = defaults.DetectEnemyGridInRange; healed = true; }
            if (missing("DelayJumpIfEnemyIsNear")) { saved.DelayJumpIfEnemyIsNear = defaults.DelayJumpIfEnemyIsNear; healed = true; }
            if (missing("DelayJump")) { saved.DelayJump = defaults.DelayJump; healed = true; }
            if (missing("AllowInGravityMax")) { saved.AllowInGravityMax = defaults.AllowInGravityMax; healed = true; }
            if (missing("AllowInGravityMaxSpeed")) { saved.AllowInGravityMaxSpeed = defaults.AllowInGravityMaxSpeed; healed = true; }
            if (missing("AllowInGravityMinAltitude")) { saved.AllowInGravityMinAltitude = defaults.AllowInGravityMinAltitude; healed = true; }
            if (missing("PrototechJump")) { saved.PrototechJump = defaults.PrototechJump; healed = true; }
            if (missing("HeatSinkDissipation")) { saved.HeatSinkDissipation = defaults.HeatSinkDissipation; healed = true; }
            if (missing("HeatSinkCapacityBonus")) { saved.HeatSinkCapacityBonus = defaults.HeatSinkCapacityBonus; healed = true; }
            if (missing("AllowInSafeZone")) { saved.AllowInSafeZone = defaults.AllowInSafeZone; healed = true; }

            return healed;
        }

        private static readonly Dictionary<string, string> configComments = new Dictionary<string, string>
        {
            { "maxSpeed", "Maximum supercruise speed, in km/s. Default: 50km/s." },
            { "startSpeed", "Initial cruise speed in km/s. Allows up to maxSpeed. Default: 1km/s." },
            { "maxHeat", "Multiplier for FSD heat capacities. Does not affect Heat Sinks. Max: 200. Default: 1." },
            { "heatGain", "Multiplier for global heat generation. Set to 0 to disable heat entirely. Default: 1." },
            { "heatDissipationDrive", "Heat/sec FSD dissipation rate. Does not affect Heat Sink dissipation. SG is 1/5 of this. This value is 10x ingame. Default: 1." },
            { "baseRequiredPower", "The absolute minimum power a LG drive can use to spool. Measured in MW. Default: 20." },
            { "baseRequiredPowerSmall", "The absolute minimum power a SG drive can use to spool. Measured in MW. Default: 20." },
            { "powerRequirementMultiplier", "Power multiplier for higher speeds while actively cruising. Default: 2." },
            { "powerRequirementBySpeedDeviderLarge", "General power divider for LG FSDs while actively cruising. Default: 3." },
            { "powerRequirementBySpeedDeviderSmall", "General power divider for SG FSDs while actively cruising. Default: 3." },
            { "AllowInGravity", "Allows cruise in gravity. Ship will drop to \"AllowInGravityMaxSpeed\" and stops if altitude is below \"AllowInGravityMinAltitude\" or gravity reaches \"AllowInGravityMax\". Default: true." },
            { "AllowToDetectEnemyGrids", "If true, spooling time is delayed to \"DelayJumpIfEnemyIsNear\" when an enemy is within \"DetectEnemyGridInRange\". Default: false." },
            { "DetectEnemyGridInRange", "Detection range in meters for enemy grids if \"AllowToDetectEnemyGrids=true\". Default: 2000m." },
            { "DelayJumpIfEnemyIsNear", "Spooling time for all drives when an enemy is within \"DetectEnemyGridInRange\". Max 90s, min is \"DelayJump\". Default: 30s." },
            { "DelayJump", "Spooling time for standard drives. Max 90s, min 3s. Default: 10s." },
            { "PrototechJump", "Spooling time for Prototech drives. Max 90s, min 3s. Default: 5s." },
            { "AllowInGravityMax", "Maximum allowed gravity if \"AllowInGravity=true\". Default: 0.2g." },
            { "AllowInGravityMaxSpeed", "Maximum allowed speed in km/s while in gravity if \"AllowInGravity=true\". Don't set too high or performance may degrade. Default: 3km/s." },
            { "AllowInGravityMinAltitude", "Minimum allowed altitude on planets in meters if \"AllowInGravityMax\" doesn't stop you first. Bare minimum allowed is 300m. Default: 3000m." },
            { "HeatSinkDissipation", "Heat/sec Heat Sink dissipation rate. Does not affect FSD dissipation. SG is 1/5 of this. This value is 10x ingame. Default: 1." },
            { "HeatSinkCapacityBonus", "Flat heat capacity added per active vent. SG is 1/5 of this. This value is 10x ingame. Default: 50." },
            { "AllowInSafeZone", "Allows drives to spool and operate inside safe zones. Warning: enabling this can allow for economy station theft. Default: false." },
        };

        private static string WriteConfigComments(string xml)
        {
            foreach (KeyValuePair<string, string> comment in configComments)
            {
                int tag = xml.IndexOf("<" + comment.Key + ">", StringComparison.Ordinal);
                if (tag < 0)
                    continue;

                int lineStart = xml.LastIndexOf('\n', tag) + 1;
                string indent = xml.Substring(lineStart, tag - lineStart);

                if (indent.Trim().Length == 0)
                    xml = xml.Insert(lineStart, indent + "<!-- " + comment.Value + " -->" + Environment.NewLine);
                else
                    xml = xml.Insert(tag, "<!-- " + comment.Value + " -->");
            }

            return xml;
        }

        public static void Save(Settings settings)
        {
            try
            {
                TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(filename, typeof(Settings));
                writer.Write(WriteConfigComments(MyAPIGateway.Utilities.SerializeToXML(settings)));
                writer.Close();
            }
            catch (Exception)
            {
            }
        }
    }
}
