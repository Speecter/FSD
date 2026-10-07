using VRage.Game.Components;
using Sandbox.Common.ObjectBuilders.Definitions;
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
using Sandbox.Game.EntityComponents;
using VRage.ModAPI;
using VRage.Game.Entity;
using ProtoBuf;

namespace WarpDriveMod
{
    public static class WarpSound
    {
        public static MySoundPair cruiseSpoolSound = new MySoundPair("quantum_charging", true);
        public static MySoundPair cruiseStartSound = new MySoundPair("quantum_jumpin", true);
        public static MySoundPair cruiseEndSound = new MySoundPair("quantum_jumpout", true);
        public static MySoundPair cruiseOperatingSound = new MySoundPair("FSD_CruiseLoop", true); //placeholder audioID
        public static MySoundPair cruiseSpoolSoundProto = new MySoundPair("FSD_ProtoCharging", true);
        public static MySoundPair cruiseStartSoundProto = new MySoundPair("FSD_Proto_JumpIn", true);
        public static MySoundPair cruiseEndSoundProto = new MySoundPair("FSD_Proto_JumpOut", true);
        public static MySoundPair gravityGlideOn = new MySoundPair("glide_on", true);
        public static MySoundPair gravityGlideOff = new MySoundPair("glide_off", true);
    }

    [ProtoContract]
    public class ItemsMessage
    {
        [ProtoMember(1)]
        public long entityId { get; set; }
        [ProtoMember(2)]
        public long sendingPlayerID { get; set; }
    }

    [ProtoContract]
    public class SpeedMessage
    {
        [ProtoMember(1)]
        public long entityId { get; set; }
        [ProtoMember(2)]
        public double warpSpeed { get; set; }
    }

    [ProtoContract]
    public class WarpStateMessage
    {
        [ProtoMember(1)]
        public long entityId { get; set; }
        [ProtoMember(2)]
        public int state { get; set; }
        [ProtoMember(3)]
        public double speed { get; set; }
        [ProtoMember(4)]
        public int spoolTicksRemaining { get; set; }
        [ProtoMember(5)]
        public bool enemyDelay { get; set; }
        [ProtoMember(6)]
        public bool clearSpeed { get; set; }
        [ProtoMember(7)]
        public long cockpitId { get; set; }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.Simulation)]
    public class WarpDriveSession : MySessionComponentBase
    {
        public static WarpDriveSession instance;
        public Random rand { get; private set; } = new Random();
        public long runtime { get; private set; } = 0;

        private readonly List<WarpSystem> warpSystems = new List<WarpSystem>();
        private readonly List<WarpSystem> newSystems = new List<WarpSystem>();
        private readonly Queue<HeatSink> exposureQueue = new Queue<HeatSink>();
        private readonly List<IMyPlayer> playerList = new List<IMyPlayer>();
        private bool _controlInit = false;
        public const ushort warpTogglePacket = 4374;
        public const ushort warpSpeedPacket = 4378;
        public const ushort warpConfigPacket = 4389;
        public const ushort warpStatePacket = 4375;

        public WarpDriveSession()
        {
            instance = this;
        }
        
        public override void BeforeStart()
        {
            base.BeforeStart();

            try
            {
                Settings.GetShared();

                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(warpConfigPacket, ReceiveWarpConfig);

                MyVisualScriptLogicProvider.PlayerLeftCockpit += PlayerLeftCockpit;

                if (MyAPIGateway.Multiplayer.IsServer)
                    MyVisualScriptLogicProvider.PlayerDisconnected += PlayerDisconnected;

                if (!MyAPIGateway.Utilities.IsDedicated && !MyAPIGateway.Multiplayer.IsServer)
                    RequestConfig();
            }
            catch (Exception)
            {
            }
        }

        public void InitJumpControl()
        {
            if (instance == null || _controlInit)
                return;

            _controlInit = true;

                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(warpTogglePacket, ReceiveToggleWarp);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(warpSpeedPacket, ReceiveWarpSpeed);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(warpStatePacket, ReceiveWarpState);

            if (MyAPIGateway.Multiplayer.IsServer)
                MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, (object target, ref MyDamageInformation info) => WarpSystem.PlayerO2DamagePause(target, ref info, warpSystems));

            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            Action<IMyTerminalBlock> toggle = TransmitToggleWarp;
            if (MyAPIGateway.Multiplayer.IsServer)
                toggle = ToggleWarp;

                IMyTerminalAction startWarp = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("ToggleWarp");
                startWarp.Enabled = IsWarpDrive;
                startWarp.Name = new StringBuilder("Toggle Supercruise");
                startWarp.Action = toggle;
                startWarp.Icon = "Textures\\GUI\\Icons\\Actions\\Toggle.dds";
                MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(startWarp);

                IMyTerminalControlButton startWarpBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("StartWarpBtn");
                startWarpBtn.Tooltip = MyStringId.GetOrCompute("Activates/deactivates the FSD's Supercruise mode");
                startWarpBtn.Title = MyStringId.GetOrCompute("Toggle Supercruise");
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
        }

        private bool GetWarpStatus(IMyTerminalBlock block)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();

            return HasValidSystem(drive) && drive.warpSystem.warpState != WarpSystem.State.idle;
        }

        private void SetWarpStatus(IMyTerminalBlock block, bool state)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
            if (!HasValidSystem(drive) || !MyAPIGateway.Multiplayer.IsServer)
                return;

            if (state != (drive.warpSystem.warpState != WarpSystem.State.idle))
                drive.warpSystem.RequestToggle(drive, null);
        }

        private IMyPlayer GetPlayer(ulong steamId)
        {
            playerList.Clear();
            MyAPIGateway.Players.GetPlayers(playerList, p => p.SteamUserId == steamId);
            return playerList.Count > 0 ? playerList[0] : null;
        }

        private static WarpDrive GetDrive(long entityId)
        {
            IMyEntity entity;
            return MyAPIGateway.Entities.TryGetEntityById(entityId, out entity) ? (entity as IMyFunctionalBlock)?.GameLogic?.GetAs<WarpDrive>() : null;
        }

        private void ReceiveToggleWarp(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
                return;

            var message = MyAPIGateway.Utilities.SerializeFromBinary<ItemsMessage>(data);
            if (message == null)
                return;

            WarpDrive drive = GetDrive(message.entityId);
            WarpSystem system = RefreshSystem(drive);
            if (system == null)
                    return;

            IMyPlayer player = GetPlayer(sender);
            if (player == null)
                return;

            system.RequestToggle(drive, player);
        }

        public void TransmitToggleWarp(IMyTerminalBlock block)
        {
            var player = MyAPIGateway.Session?.Player;

            if (!IsWarpDrive(block) || player == null)
                return;

            MyAPIGateway.Multiplayer.SendMessageToServer(warpTogglePacket,
                message: MyAPIGateway.Utilities.SerializeToBinary(new ItemsMessage
                {
                    entityId = block.EntityId,
                    sendingPlayerID = player.IdentityId
                }));
        }

        private void ReceiveWarpState(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            if (!fromServer || MyAPIGateway.Multiplayer.IsServer)
                return;

            var message = MyAPIGateway.Utilities.SerializeFromBinary<WarpStateMessage>(data);
            if (message == null)
                return;

            WarpDrive drive = GetDrive(message.entityId);
            WarpSystem system = RefreshSystem(drive);
            if (system == null)
                return;

            system.ApplyState(drive, message);
        }

        private void ReceiveWarpSpeed(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var message = MyAPIGateway.Utilities.SerializeFromBinary<SpeedMessage>(data);
            if (message == null)
                return;

            WarpDrive drive = GetDrive(message.entityId);

            if (!HasValidSystem(drive))
                return;

            if (MyAPIGateway.Multiplayer.IsServer)
            {
                if (!fromServer)
                    drive.warpSystem.ReadClientSpeedInput(drive, GetPlayer(sender), message.warpSpeed);
            }
            else if (fromServer)
            {
                drive.warpSystem.ApplyServerSpeed(message.warpSpeed);
            }
        }

        public void TransmitWarpSpeed(IMyFunctionalBlock warpBlock, int direction)
        {
            if (!IsWarpDrive(warpBlock))
                return;

            MyAPIGateway.Multiplayer.SendMessageToServer(warpSpeedPacket,
                message: MyAPIGateway.Utilities.SerializeToBinary(new SpeedMessage
                {
                    entityId = warpBlock.EntityId,
                    warpSpeed = direction
                }));
        }

        private void ReceiveWarpConfig(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var message = MyAPIGateway.Utilities.SerializeFromBinary<Settings>(data);
            if (message == null)
                return;

                if (MyAPIGateway.Utilities.IsDedicated || MyAPIGateway.Multiplayer.IsServer)
                {
                if (!fromServer)
                    MyAPIGateway.Multiplayer.SendMessageTo(warpConfigPacket,
                        message: MyAPIGateway.Utilities.SerializeToBinary(Settings.GetShared()), recipient: sender);
            }
            else if (fromServer)
            {
                Settings.GetShared().CopyFrom(message);
            }
        }

        public void RequestConfig()
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(warpConfigPacket,
                message: MyAPIGateway.Utilities.SerializeToBinary(new Settings()));
        }

        private void PlayerDisconnected(long playerId)
        {
            foreach (WarpSystem s in warpSystems)
                s?.PilotDisconnected(playerId);
        }

        private void PlayerLeftCockpit(string entityName, long playerId, string gridName)
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
                return;

            foreach (WarpSystem s in warpSystems)
            {
                if (s != null && s.OccupantLeft(playerId))
                    return;
            }

            long cockpitId;
            if (!long.TryParse(entityName, out cockpitId))
                return;

            var cockpit = MyAPIGateway.Entities.GetEntityById(cockpitId) as IMyShipController;
            var cockpitGrid = cockpit?.CubeGrid as MyCubeGrid;
            if (cockpitGrid == null)
                return;

            WarpSystem system = GetGridWarpSystem(cockpitGrid);
            if (system == null || !system.valid || system.warpState == WarpSystem.State.idle)
                return;

            system.PilotLeft(cockpit);
        }

        private bool IsWarpDrive(IMyTerminalBlock block)
        {
            return block?.GameLogic?.GetAs<WarpDrive>() != null;
        }

        public void QueueExposureCheck(HeatSink heatSink)
        {
            if (heatSink == null || heatSink.exposureQueued)
                return;

            heatSink.exposureQueued = true;
            exposureQueue.Enqueue(heatSink);
        }

        public override void Simulate()
        {
            runtime++;

                int budget = Math.Max(1, (exposureQueue.Count + 119) / 120); //rate limiting for grids with tons of sinks
                while (budget-- > 0 && exposureQueue.Count > 0)
                {
                    HeatSink heatSink = exposureQueue.Dequeue();
                    heatSink.exposureQueued = false;

                if (heatSink.block != null && !heatSink.block.MarkedForClose)
                    heatSink.ExposureCheck();
            }

            for (int i = warpSystems.Count - 1; i >= 0; i--)
            {
                WarpSystem s = warpSystems[i];

                if (s.isSleeping && (runtime + s.id) % 300 != 0) //sleepy bois resync every 5s
                    continue;

                if (s.valid)
                {
                    if (!s.isSleeping)
                    s.UpdateBeforeSimulation();
                }
                else
                    warpSystems.RemoveAtFast(i);
            }

                foreach (WarpSystem s in newSystems)
                {
                    if (!warpSystems.Contains(s))
                        warpSystems.Add(s);
                }

                newSystems.Clear();
        }

        public WarpSystem GetGridWarpSystem(MyCubeGrid grid)
        {
            if (grid == null)
                return null;

            return FindGridSystem(warpSystems, grid) ?? FindGridSystem(newSystems, grid);
        }

        private static WarpSystem FindGridSystem(List<WarpSystem> systems, MyCubeGrid grid)
        {
            foreach (WarpSystem s in systems)
            {
                if (s?.grid != null && s.grid.Contains(grid))
                    return s;
            }

            return null;
        }

        public WarpSystem GetWarpSystem(WarpDrive drive)
        {
            if (HasValidSystem(drive))
                return drive.warpSystem;

            foreach (WarpSystem s in warpSystems)
            {
                if (s != null && s.valid && s.Contains(drive))
                    return s;
            }

            foreach (WarpSystem s in newSystems)
            {
                if (s != null && s.Contains(drive))
                    return s;
            }

            WarpSystem newSystem = new WarpSystem(drive);

            if (newSystem.grid == null)
                return null;

            if (!newSystems.Contains(newSystem))
                newSystems.Add(newSystem);

            return newSystem;
        }

        private WarpSystem RefreshSystem(WarpDrive drive)
        {
            if (drive?.block == null || drive.block.MarkedForClose)
                return null;

            if (drive.warpSystem?.grid != null && drive.warpSystem.grid.IsValid(true))
                return drive.warpSystem;

            WarpSystem system = GetWarpSystem(drive);
            if (system == null || !system.valid)
                return null;

            drive.SetWarpSystem(system);
            return system;
        }

        private void ToggleWarp(IMyTerminalBlock block)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
            WarpSystem system = RefreshSystem(drive);
            if (system == null)
                return;

            system.RequestToggle(drive, MyAPIGateway.Session?.Player);
        }

        private bool HasValidSystem(WarpDrive drive)
        {
            return drive?.warpSystem != null && drive.warpSystem.valid;
        }

        protected override void UnloadData()
        {
            try
            {
                if (instance == null)
                    return;

                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(warpTogglePacket, ReceiveToggleWarp);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(warpSpeedPacket, ReceiveWarpSpeed);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(warpStatePacket, ReceiveWarpState);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(warpConfigPacket, ReceiveWarpConfig);

                MyVisualScriptLogicProvider.PlayerLeftCockpit -= PlayerLeftCockpit;
                MyVisualScriptLogicProvider.PlayerDisconnected -= PlayerDisconnected;

                exposureQueue.Clear();

                Settings.instance = null;
                HeatSink.heatEnabled = true;

                instance = null;

                base.UnloadData();
            }
            catch { }
        }
    }
}
