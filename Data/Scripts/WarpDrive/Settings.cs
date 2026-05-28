using System;
using System.IO;
using System.Xml.Serialization;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Utils;

namespace WarpDriveMod
{
    [ProtoContract]
    public class Settings
    {
        public static Settings Instance;

        public const string Filename = "FSDriveConfig.cfg";

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

        [ProtoMember(12)]
        public bool AllowUnlimittedSpeed;

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

        [ProtoMember(20)]
        public long BlockID;

        [ProtoMember(21)]
        public double PrototechJump;

        public static Settings GetDefaults()
        {
            return new Settings
            {
                maxSpeed = 50000 / 60d, // in settings numbers from higher than startSpeed + 1, max 100 or if AllowUnlimittedSpeed=true up to 2000 (game possile limit)
                startSpeed = 1000 / 60d, // in settings numbers from 1 to less than maxSpeed and max 99, or if AllowUnlimittedSpeed=true up to 1999
                maxHeat = 180f, // Shutdown when this amount of heat has been reached. this is in seconds if heatGain = 1 / 60f so it's 3 minutes;
                heatGain = 0 / 60f, // Amount of heat gained per tick = 1% per second if set 1, max possible 10
                heatDissipationDrive = 2 / 60f, // Amount of heat dissipated by warp drives every tick
                baseRequiredPower = 126f, //MW
                baseRequiredPowerSmall = 5f, // MW
                powerRequirementMultiplier = 2, // each speed step will take baseRequiredPower/Small and * powerRequirementMultiplier
                powerRequirementBySpeedDeviderLarge = 6f, // Now power requirement is based on mass + speed!, to lower power requirement set this to higher number.
                powerRequirementBySpeedDeviderSmall = 12f, // Now power requirement is based on mass + speed!, to lower power requirement set this to higher number.
                AllowInGravity = false, // allow to activate warp in gravity, ship will drop to 1km/s when in gravity and stop id altitude is below 300m
                AllowUnlimittedSpeed = false, // if set to true, will allow setting max speed to any number, if false them max is 100km/s = 100000.
                AllowToDetectEnemyGrids = false, // if set to true, then warp charge code will check if there is enemy grid in range, and delay jump by set amount.
                DetectEnemyGridInRange = 2000, // sphere range from ship center to detect enemy grid, max range is 8000 meters.
                DelayJumpIfEnemyIsNear = 30, // delay jump by this much seconds if enemy grid is in range, max is 90 seconds.
                DelayJump = 10, // delay jump start by this much seconds. max 90 sec, min 3 sec
                PrototechJump = 5, // seconds to wait for jump while using Prototech FSDs
                AllowInGravityMax = 1.8f, // Allow to enter gravity of planet till gravity level reach setting, Max possilbe 1.8
                AllowInGravityMaxSpeed = 3000 / 60d, // max speed in gravity, allowed up to speed 3 to prevent high load.
                AllowInGravityMinAltitude = 300d // Minimum altitude on planet.
            };
        }

        public static Settings Load()
        {
            Settings defaults = GetDefaults();
            Settings settings = GetDefaults();

            try
            {
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(Filename, typeof(Settings)))
                {
                    TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(Filename, typeof(Settings));
                    string text = reader.ReadToEnd();
                    reader.Close();

                    settings = MyAPIGateway.Utilities.SerializeFromXML<Settings>(text);
                    double startSpeed = 0;
                    double maxSpeed = 0;
                    double gravityMaxSpeed = 0;
                    float heatDissipationDrive = 0;
					float heatGain = 0;

                    // convert and check startSpeed settings
                    if (settings.startSpeed < 1 || settings.startSpeed > 99)
					{
                        startSpeed = defaults.startSpeed;
						settings.startSpeed = defaults.startSpeed * 60d / 1000;
						Save(settings);
					}
                    else if (settings.startSpeed > settings.maxSpeed)
					{
                        startSpeed = defaults.startSpeed;
						settings.startSpeed = defaults.startSpeed * 60d / 1000;
						Save(settings);
					}
                    else
                        startSpeed = settings.startSpeed * 1000 / 60d;

                    // convert and check maxSpeed settings
                    if (settings.maxSpeed > 2000 && settings.AllowUnlimittedSpeed)
                    {
                        maxSpeed = 2000 * 1000 / 60d;
                        settings.maxSpeed = 2000;
                        Save(settings);
					}
                    else if (settings.maxSpeed > 100 && !settings.AllowUnlimittedSpeed)
					{
                        maxSpeed = 100 * 1000 / 60d;
						settings.maxSpeed = 100;
						Save(settings);
					}
					
					if (settings.maxSpeed < settings.startSpeed)
					{
                        maxSpeed = (settings.startSpeed + 5) * 1000 / 60d;
						settings.maxSpeed = settings.startSpeed + 5;
						Save(settings);
					}
                    else
                        maxSpeed = settings.maxSpeed * 1000 / 60d;

                    // convert and check maxHeat settings
                    if (settings.maxHeat <= 0)
					{
                        settings.maxHeat = 60f;
						Save(settings);
					}
                    else if (settings.maxHeat < 30f)
					{
                        settings.maxHeat = 30f;
						Save(settings);
					}

                    // convert and check heatGain settings
                    if (settings.heatGain < 0f)
					{
						heatGain = 0f;
                        settings.heatGain = 0f;
						Save(settings);
					}
                    else if (settings.heatGain > 10f)
                    {
                        heatGain = 10 / 60f;
                        settings.heatGain = 10f;
                        Save(settings);
                    }
                    else
                        heatGain = settings.heatGain / 60f;

                    // convert and check heatDissipationDrive settings
                    if (settings.heatDissipationDrive < 2f)
					{
						heatDissipationDrive = defaults.heatDissipationDrive;
                        settings.heatDissipationDrive = 2f;
						Save(settings);
					}
                    else
                        heatDissipationDrive = settings.heatDissipationDrive / 60f;

                    // check DelayJump settings
                    if (settings.DelayJump > 90 || settings.DelayJump < 3)
					{
                        settings.DelayJump = 10;
						Save(settings);
					}

                    if (settings.PrototechJump > 90 || settings.PrototechJump < 3)
                    {
                        settings.PrototechJump = 0;
                        Save(settings);
                    }

                    // check DelayJumpIfEnemyIsNear settings
                    if (settings.DelayJumpIfEnemyIsNear < settings.DelayJump)
					{
                        settings.DelayJumpIfEnemyIsNear = 30;
						Save(settings);
					}

                    if (settings.DelayJumpIfEnemyIsNear > 90)
					{
                        settings.DelayJumpIfEnemyIsNear = 30;
						Save(settings);
					}

                    // check DetectEnemyGridInRange settings
                    if (settings.DetectEnemyGridInRange > 8000 || settings.DetectEnemyGridInRange < 1000)
					{
                        settings.DetectEnemyGridInRange = 2000;
						Save(settings);
					}

                    if (settings.AllowInGravityMax > 1.8f || settings.AllowInGravityMax <= 0f)
                    {
                        settings.AllowInGravityMax = 1.8f;
                        Save(settings);
                    }

                    if (settings.AllowInGravityMaxSpeed < 1 || settings.AllowInGravityMaxSpeed > 3)
                    {
                        gravityMaxSpeed = defaults.AllowInGravityMaxSpeed;
                        settings.AllowInGravityMaxSpeed = defaults.AllowInGravityMaxSpeed * 60d / 1000;
                        Save(settings);
                    }
                    else if (settings.AllowInGravityMaxSpeed > settings.maxSpeed)
                    {
                        gravityMaxSpeed = defaults.AllowInGravityMaxSpeed;
                        settings.AllowInGravityMaxSpeed = defaults.AllowInGravityMaxSpeed * 60d / 1000;
                        Save(settings);
                    }
                    else
                        gravityMaxSpeed = settings.AllowInGravityMaxSpeed * 1000 / 60d;

                    if (settings.AllowInGravityMinAltitude < 300)
                        settings.AllowInGravityMinAltitude = 300;

                    // loading settings for actual code
                    settings.maxSpeed = maxSpeed;
                    settings.startSpeed = startSpeed;
                    settings.AllowInGravityMaxSpeed = gravityMaxSpeed;
                    settings.heatGain = heatGain;
					settings.heatDissipationDrive = heatDissipationDrive;
                }
                else
                {
                    MyLog.Default.Info("[Frame Shift Drive] Config file not found. Loading default settings");
					
					settings.maxSpeed = settings.maxSpeed * 60d / 1000;
					settings.startSpeed = settings.startSpeed * 60d / 1000;
                    settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 60d / 1000;
                    settings.heatGain *= 60f;
                    settings.heatDissipationDrive *= 60f;
					Save(settings);
					
                    settings.maxSpeed = settings.maxSpeed * 1000 / 60d;
                    settings.startSpeed = settings.startSpeed * 1000 / 60d;
                    settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 1000 / 60d;
                    settings.heatGain /= 60f;
                    settings.heatDissipationDrive /= 60f;
                }
            }
            catch (Exception e)
            {
                MyLog.Default.Info($"[Frame Shift Drive] Failed to load saved configuration. Loading defaults\n {e}");

                settings = defaults;

                settings.maxSpeed = defaults.maxSpeed * 60d / 1000;
                settings.startSpeed = defaults.startSpeed * 60d / 1000;
                settings.AllowInGravityMaxSpeed = defaults.AllowInGravityMaxSpeed * 60d / 1000;
                settings.heatGain = defaults.heatGain * 60f;
                settings.heatDissipationDrive = defaults.heatDissipationDrive * 60f;
                Save(settings);

                settings.maxSpeed = settings.maxSpeed * 1000 / 60d;
                settings.startSpeed = settings.startSpeed * 1000 / 60d;
                settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 1000 / 60d;
                settings.heatGain /= 60f;
                settings.heatDissipationDrive /= 60f;
            }

            return settings;
        }

        public static void Save(Settings settings)
        {
            try
            {
                MyLog.Default.Info($"[Frame Shift Drive] Saving Settings");
                TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(Filename, typeof(Settings));
                writer.Write(MyAPIGateway.Utilities.SerializeToXML(settings));
                writer.Close();
            }
            catch (Exception e)
            {
                MyLog.Default.Info($"[Frame Shift Drive] Failed to save settings\n {e}");
            }
        }

        public static void SaveClient(Settings settings)
        {
            try
            {
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(Filename, typeof(Settings)))
                {
                    TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(Filename, typeof(Settings));
                    string text = reader.ReadToEnd();
                    reader.Close();

                    var settingsClient = MyAPIGateway.Utilities.SerializeFromXML<Settings>(text);

                    settings.maxSpeed = settings.maxSpeed * 60d / 1000;
                    settings.startSpeed = settings.startSpeed * 60d / 1000;
                    settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 60d / 1000;
                    settings.heatGain *= 60f;
                    settings.heatDissipationDrive *= 60f;

                    if (settingsClient != settings)
                        Save(settings);
                }
                else
                {
                    settings.maxSpeed = settings.maxSpeed * 60d / 1000;
                    settings.startSpeed = settings.startSpeed * 60d / 1000;
                    settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 60d / 1000;
                    settings.heatGain *= 60f;
                    settings.heatDissipationDrive *= 60f;
                    Save(settings);
                }

                settings.maxSpeed = settings.maxSpeed * 1000 / 60d;
                settings.startSpeed = settings.startSpeed * 1000 / 60d;
                settings.AllowInGravityMaxSpeed = settings.AllowInGravityMaxSpeed * 1000 / 60d;
                settings.heatGain /= 60f;
                settings.heatDissipationDrive /= 60f;
            }
            catch (Exception e)
            {
                MyLog.Default.Info($"[Frame Shift Drive] Failed to save client settings\n {e}");
            }
        }
    }
}
