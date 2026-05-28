using VRage.Game.Components;
using Sandbox.ModAPI;
using System.Text;
using Sandbox.ModAPI.Interfaces.Terminal;
using System.Collections.Generic;
using System;
using VRage.Game.ModAPI;
using VRage.Game;
using Sandbox.Game.Entities;
using VRage.Utils;
using Sandbox.Game;
using Sandbox.Game.Screens.Terminal.Controls;
using Sandbox.Game.EntityComponents;
using VRage.ModAPI;
using VRage.Game.Entity;
using ProtoBuf;

namespace WarpDriveMod
{
    public static class WarpConstants
    {
        public static MySoundPair EmergencyDropSound = new MySoundPair("SuperCruiseGravity", true);
        public static MySoundPair chargingSound = new MySoundPair("quantum_charging", true);
        public static MySoundPair jumpInSound = new MySoundPair("quantum_jumpin", true);
        public static MySoundPair jumpOutSound = new MySoundPair("quantum_jumpout", true);
        public static MySoundPair PrototechChargingSound = new MySoundPair("FSD_ProtoCharging", true);
        public static MySoundPair PrototechJumpInSound = new MySoundPair("FSD_Proto_JumpIn", true);
        public static MySoundPair PrototechJumpOutSound = new MySoundPair("FSD_Proto_JumpOut", true);


        public const int groupSystemDelay = 1;

        public static MyDefinitionId ElectricityId = MyResourceDistributorComponent.ElectricityId;
    }

    [ProtoContract]
    public class ItemsMessage
    {
        [ProtoMember(1)]
        public long EntityId { get; set; }
        [ProtoMember(2)]
        public long SendingPlayerID { get; set; }
    }

    [ProtoContract]
    public class SpeedMessage
    {
        [ProtoMember(1)]
        public long EntityId { get; set; }
        [ProtoMember(2)]
        public double WarpSpeed { get; set; }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.Simulation)]
    public class WarpDriveSession : MySessionComponentBase
    {
        public static WarpDriveSession Instance;
        public Random Rand { get; private set; } = new Random();
        public long Runtime { get; private set; } = 0;
        public Dictionary<IMyFunctionalBlock, double> warpDrivesSpeeds = new Dictionary<IMyFunctionalBlock, double>();

        private readonly List<WarpSystem> warpSystems = new List<WarpSystem>();
        private readonly List<WarpSystem> newSystems = new List<WarpSystem>();
        private readonly List<WarpDrive> requireSystem = new List<WarpDrive>();
        private bool isHost;
        private bool isPlayer;
        private bool _controlInit = false;
        public const ushort toggleWarpPacketId = 4374;
        public const ushort toggleWarpPacketIdSpeed = 4378;
        public const ushort WarpConfigPacketId = 4389;
        private Action<IMyTerminalBlock> toggle;

        public WarpDriveSession()
        {
            Instance = this;
        }

        public void InitJumpControl()
        {
            if (Instance == null || WarpDrive.Instance == null || WarpSystem.Instance == null)
                return;

            if (!_controlInit)
            {
                isPlayer = !MyAPIGateway.Utilities.IsDedicated;
                isHost = MyAPIGateway.Multiplayer.IsServer;

                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(toggleWarpPacketId, ReceiveToggleWarp);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(toggleWarpPacketIdSpeed, ReceiveWarpSpeed);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(WarpConfigPacketId, ReceiveWarpConfig);

                if (isHost)
                {
                    if (isPlayer)
                    {
                        // Session is host, toggle the warp drive directly.
                        MyLog.Default.WriteLineAndConsole("Initialized Warp Drive mod on a hosted multiplayer world.");
                        toggle = ToggleWarp;
                    }
                    else
                    {
                        // Do not create terminal controls on dedicated server.
                        MyLog.Default.WriteLineAndConsole("Initialized Warp Drive mod on dedicated server.");
                    }
                }
                else
                {
                    if (isPlayer)
                    {
                        // Session is client, tell the host to toggle the warp drive.
                        toggle = TransmitToggleWarp;
                        MyLog.Default.WriteLineAndConsole("Initialized Frame Shift Drive mod on a multiplayer client.");
                    }
                    else
                        throw new Exception("Session is not host or client. What?!");
                }

                // no need to init controls on server.
                if (MyAPIGateway.Utilities.IsDedicated)
                {
                    _controlInit = true;
                    return;
                }

                if (toggle == null)
                    return;

                IMyTerminalAction startWarp = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("ToggleWarp");
                startWarp.Enabled = IsWarpDrive;
                startWarp.Name = new StringBuilder("Toggle Supercruise");
                startWarp.Action = toggle;
                startWarp.Icon = "Textures\\GUI\\Icons\\Actions\\Toggle.dds";
                MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(startWarp);

                IMyTerminalControlButton startWarpBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("StartWarpBtn");
                startWarpBtn.Tooltip = MyStringId.GetOrCompute("Toggles the status of the warp drives on the ship");
                startWarpBtn.Title = MyStringId.GetOrCompute("Toggle Warp");
                startWarpBtn.Enabled = IsWarpDrive;
                startWarpBtn.Visible = IsWarpDrive;
                startWarpBtn.SupportsMultipleBlocks = false;
                startWarpBtn.Action = toggle;
                MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(startWarpBtn);

                IMyTerminalControlProperty<bool> inWarp = MyAPIGateway.TerminalControls.CreateProperty<bool, IMyUpgradeModule>("WarpStatus");
                inWarp.Enabled = IsWarpDrive;
                inWarp.Visible = IsWarpDrive;
                inWarp.SupportsMultipleBlocks = false;
                inWarp.Setter = SetWarpStatus;
                inWarp.Getter = GetWarpStatus;
                MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(inWarp);

                _controlInit = true;
            }
        }

        private bool GetWarpStatus(IMyTerminalBlock block)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
            if (!HasValidSystem(drive))
                return false;

            return drive.System.WarpState != WarpSystem.State.Idle;
        }

        private void SetWarpStatus(IMyTerminalBlock block, bool state)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
            if (!HasValidSystem(drive))
                return;

            long PlayerID = 0;

            if (state)
            {
                if (drive.System.WarpState == WarpSystem.State.Idle)
                    drive.System.ToggleWarp(block, block.CubeGrid, PlayerID);
            }
            else
            {
                if (drive.System.WarpState != WarpSystem.State.Idle)
                    drive.System.ToggleWarp(block, block.CubeGrid, PlayerID);
            }
        }

        private void ReceiveToggleWarp(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var message = MyAPIGateway.Utilities.SerializeFromBinary<ItemsMessage>(data);
            if (message == null)
                return;

            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(message.EntityId, out entity))
                return;

            var block = entity as IMyFunctionalBlock;
            if (block != null)
            {
                WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
                if (!HasValidSystem(drive))
                    return;

                if (drive.System.WarpState == WarpSystem.State.Idle)
                {
                    RefreshGridCockpits(block);

                    var Gridmatrix = drive.System.grid.FindWorldMatrix();

                    if (WarpDrive.Instance.ProxymityDangerCharge(Gridmatrix, block.CubeGrid))
                    {
                        drive.System.SendMessage(drive.System.ProximytyAlert, 2f, "Red", message.SendingPlayerID);
                        return;
                    }
                }

                drive.System.ToggleWarp(block, block.CubeGrid, message.SendingPlayerID);
            }
        }

        public void TransmitToggleWarp(IMyTerminalBlock block)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
            var player = MyAPIGateway.Session?.Player;

            if (drive == null || player == null)
                return;

            MyAPIGateway.Multiplayer.SendMessageToServer(toggleWarpPacketId,
                message: MyAPIGateway.Utilities.SerializeToBinary(new ItemsMessage
                {
                    EntityId = block.EntityId,
                    SendingPlayerID = player.IdentityId
                }));
        }

        private void ReceiveWarpSpeed(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var message = MyAPIGateway.Utilities.SerializeFromBinary<SpeedMessage>(data);
            if (message == null)
                return;

            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(message.EntityId, out entity))
                return;

            var block = entity as IMyFunctionalBlock;
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();

            if (!HasValidSystem(drive))
                return;

            if (!warpDrivesSpeeds.ContainsKey(block))
                warpDrivesSpeeds.Add(block, message.WarpSpeed);
            else
                warpDrivesSpeeds[block] = message.WarpSpeed;

            // Message is from client and should be relayed
            //if (MyAPIGateway.Utilities.IsDedicated)
            //    MyAPIGateway.Multiplayer.SendMessageToOthers(toggleWarpPacketIdSpeed, data);
        }

        public void TransmitWarpSpeed(IMyFunctionalBlock WarpBlock, double currentSpeedPt)
        {
            var DriveBlock = WarpBlock as IMyTerminalBlock;
            WarpDrive drive = DriveBlock?.GameLogic?.GetAs<WarpDrive>();
            if (drive == null)
                return;

            MyAPIGateway.Multiplayer.SendMessageToServer(toggleWarpPacketIdSpeed,
                message: MyAPIGateway.Utilities.SerializeToBinary(new SpeedMessage
                {
                    EntityId = DriveBlock.EntityId,
                    WarpSpeed = currentSpeedPt
                }));
        }

        private void ReceiveWarpConfig(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var message = MyAPIGateway.Utilities.SerializeFromBinary<Settings>(data);
            if (message == null)
                return;

            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(message.BlockID, out entity))
                return;

            var block = entity as IMyFunctionalBlock;
            if (block != null)
            {
                WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
                if (!HasValidSystem(drive))
                    return;

                if (MyAPIGateway.Utilities.IsDedicated || MyAPIGateway.Multiplayer.IsServer)
                {
                    MyAPIGateway.Multiplayer.SendMessageTo(WarpConfigPacketId,
                        message: MyAPIGateway.Utilities.SerializeToBinary(new Settings
                        {
                            maxSpeed = drive.Settings.maxSpeed,
                            startSpeed = drive.Settings.startSpeed,
                            maxHeat = drive.Settings.maxHeat,
                            heatGain = drive.Settings.heatGain,
                            heatDissipationDrive = drive.Settings.heatDissipationDrive,
                            baseRequiredPower = drive.Settings.baseRequiredPower,
                            baseRequiredPowerSmall = drive.Settings.baseRequiredPowerSmall,
                            powerRequirementMultiplier = drive.Settings.powerRequirementMultiplier,
                            powerRequirementBySpeedDeviderLarge = drive.Settings.powerRequirementBySpeedDeviderLarge,
                            powerRequirementBySpeedDeviderSmall = drive.Settings.powerRequirementBySpeedDeviderSmall,
                            AllowInGravity = drive.Settings.AllowInGravity,
                            AllowUnlimittedSpeed = drive.Settings.AllowUnlimittedSpeed,
                            AllowToDetectEnemyGrids = drive.Settings.AllowToDetectEnemyGrids,
                            DetectEnemyGridInRange = drive.Settings.DetectEnemyGridInRange,
                            DelayJumpIfEnemyIsNear = drive.Settings.DelayJumpIfEnemyIsNear,
                            DelayJump = drive.Settings.DelayJump,
                            PrototechJump = drive.Settings.PrototechJump,
                            AllowInGravityMax = drive.Settings.AllowInGravityMax,
                            AllowInGravityMaxSpeed = drive.Settings.AllowInGravityMaxSpeed,
                            AllowInGravityMinAltitude = drive.Settings.AllowInGravityMinAltitude,
                            BlockID = block.EntityId,
                        }), recipient: sender);
                }

                if (!MyAPIGateway.Utilities.IsDedicated && !MyAPIGateway.Multiplayer.IsServer)
                {
                    drive.Settings.maxSpeed = message.maxSpeed;
                    drive.Settings.startSpeed = message.startSpeed;
                    drive.Settings.maxHeat = message.maxHeat;
                    drive.Settings.heatGain = message.heatGain;
                    drive.Settings.heatDissipationDrive = message.heatDissipationDrive;
                    drive.Settings.baseRequiredPower = message.baseRequiredPower;
                    drive.Settings.baseRequiredPowerSmall = message.baseRequiredPowerSmall;
                    drive.Settings.powerRequirementMultiplier = message.powerRequirementMultiplier;
                    drive.Settings.powerRequirementBySpeedDeviderLarge = message.powerRequirementBySpeedDeviderLarge;
                    drive.Settings.powerRequirementBySpeedDeviderSmall = message.powerRequirementBySpeedDeviderSmall;
                    drive.Settings.AllowInGravity = message.AllowInGravity;
                    drive.Settings.AllowUnlimittedSpeed = message.AllowUnlimittedSpeed;
                    drive.Settings.AllowToDetectEnemyGrids = message.AllowToDetectEnemyGrids;
                    drive.Settings.DetectEnemyGridInRange = message.DetectEnemyGridInRange;
                    drive.Settings.DelayJumpIfEnemyIsNear = message.DelayJumpIfEnemyIsNear;
                    drive.Settings.DelayJump = message.DelayJump;
                    drive.Settings.PrototechJump = message.PrototechJump;
                    drive.Settings.AllowInGravityMax = message.AllowInGravityMax;
                    drive.Settings.AllowInGravityMaxSpeed = message.AllowInGravityMaxSpeed;
                    drive.Settings.AllowInGravityMinAltitude = message.AllowInGravityMinAltitude;
                    drive.Settings.BlockID = message.BlockID;

                    Settings.SaveClient(message);
                }
            }
        }

        public void TransmitWarpConfig(Settings SettingsData, long BlockID)
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(WarpConfigPacketId,
                message: MyAPIGateway.Utilities.SerializeToBinary(new Settings
                {
                    BlockID = BlockID,
                }));
        }

        private bool IsWarpDrive(IMyTerminalBlock block)
        {
            return block?.GameLogic?.GetAs<WarpDrive>() != null;
        }

        public override void Simulate()
        {
            Runtime++;

            for (int i = requireSystem.Count - 1; i >= 0; i--)
            {
                WarpDrive drive = requireSystem[i];
                if (drive.System == null || drive.System.InvalidOn <= Runtime - WarpConstants.groupSystemDelay)
                {
                    requireSystem.RemoveAtFast(i);

                    var DriveSystemNew = GetWarpSystem(drive);
                    if (DriveSystemNew != null)
                        drive.SetWarpSystem(DriveSystemNew);
                }
                else if (HasValidSystem(drive))
                    requireSystem.RemoveAtFast(i);
            }

            for (int i = warpSystems.Count - 1; i >= 0; i--)
            {
                WarpSystem s = warpSystems[i];
                if (s.Valid)
                    s.UpdateBeforeSimulation();
                else
                    warpSystems.RemoveAtFast(i);
            }

            if (newSystems != null && newSystems.Count > 0)
            {
                foreach (WarpSystem s in newSystems)
                {
                    if (!warpSystems.Contains(s))
                        warpSystems.Add(s);
                }
                newSystems.Clear();
            }
        }

        public WarpSystem GetWarpSystem(WarpDrive drive)
        {
            if (HasValidSystem(drive))
                return drive.System; // Why are you here?!?!

            foreach (WarpSystem s in warpSystems)
            {
                if (s == null)
                    continue;

                if (s.Valid && s.Contains(drive))
                    return s;
            }

            foreach (WarpSystem s in newSystems)
            {
                if (s == null)
                    continue;

                if (s.Contains(drive))
                    return s;
            }

            WarpSystem newSystem = new WarpSystem(drive, drive.System);

            if (newSystem == null)
                return null;

            if (!newSystems.Contains(newSystem))
                newSystems.Add(newSystem);

            return newSystem;
        }

        public void DelayedGetWarpSystem(WarpDrive drive)
        {
            requireSystem.Add(drive);
        }

        private void ToggleWarp(IMyTerminalBlock block)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
            if (!HasValidSystem(drive))
                return;

            drive.System.ToggleWarp(block, block.CubeGrid, 0);
        }

        public void RefreshGridCockpits(IMyTerminalBlock block)
        {
            if (block == null)
                return;

            MyCubeGrid grid = (MyCubeGrid)block.CubeGrid;
            if (grid == null)
                return;

            var slimList = new List<IMySlimBlock>();
            var ShipControllerList = new HashSet<IMyShipController>();

            block.CubeGrid.GetBlocks(slimList);

            // check all valid cockpits
            foreach (var slim in slimList)
            {
                if (slim.FatBlock != null && slim.FatBlock is IMyShipController)
                {
                    if (WarpSystem.Instance.grid.IsShipController(slim.FatBlock))
                        ShipControllerList.Add(slim.FatBlock as IMyShipController);
                }
            }

            // remove found cockpits from active list
            HashSet<IMyShipController> gridCockpits = new HashSet<IMyShipController>();
            if (WarpSystem.Instance.grid.cockpits.TryGetValue(grid, out gridCockpits))
                WarpSystem.Instance.grid.cockpits[grid] = null;

            // add updated cocpits to active list
            WarpSystem.Instance.grid.cockpits[grid] = ShipControllerList;
        }

        private bool HasValidSystem(WarpDrive drive)
        {
            return drive?.System != null && drive.System.Valid;
        }

        protected override void UnloadData()
        {
            try
            {
                if (Instance == null)
                    return;

                if (WarpDrive.Instance != null)
                    MyVisualScriptLogicProvider.PlayerLeftCockpit -= WarpDrive.Instance.PlayerLeftCockpit;

                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(toggleWarpPacketId, ReceiveToggleWarp);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(toggleWarpPacketIdSpeed, ReceiveWarpSpeed);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(WarpConfigPacketId, ReceiveWarpConfig);

                if (WarpDrive.Instance != null)
                    WarpDrive.Instance = null;

                if (WarpSystem.Instance != null)
                    WarpSystem.Instance = null;

                Instance = null;

                base.UnloadData();
            }
            catch { }
        }
    }
}
