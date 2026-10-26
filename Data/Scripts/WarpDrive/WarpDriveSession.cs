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
using VRageMath;

namespace WarpDriveMod
{
    public static class WarpSound
    {
        public static MySoundPair cruiseSpoolSound = new MySoundPair("quantum_charging", true);
        public static MySoundPair cruiseStartSound = new MySoundPair("quantum_jumpin", true);
        public static MySoundPair cruiseEndSound = new MySoundPair("quantum_jumpout", true);
        public static MySoundPair cruiseOperatingSound = new MySoundPair("FTL_LowSpeed", true);
        public static MySoundPair cruiseSpoolSoundProto = new MySoundPair("FSD_ProtoCharging", true);
        public static MySoundPair cruiseStartSoundProto = new MySoundPair("FSD_Proto_JumpIn", true);
        public static MySoundPair cruiseEndSoundProto = new MySoundPair("FSD_Proto_JumpOut", true);
        public static MySoundPair gravityGlideOn = new MySoundPair("glide_on", true);
        public static MySoundPair gravityGlideOff = new MySoundPair("glide_off", true);
        public static MySoundPair spoolStop = new MySoundPair("ArcHudAntennaOff", true);
        public static MySoundPair hyperspaceJump = new MySoundPair("Hyperspace_jump", true);
        public static MySoundPair fsdHyperCharge = new MySoundPair("fsd_hyper_charge", true);
        public static MySoundPair engagingHyperspace = new MySoundPair("engaging_hyperspace", true);
        public static MySoundPair hyperdiction = new MySoundPair("hyperdiction", false);
        public static MySoundPair inHyperspace = new MySoundPair("inHyperSpace", true);
        public static MySoundPair holdCharge = new MySoundPair("hold_charge", true);
        public static MySoundPair spoolDown = new MySoundPair("spool_down", true);
        public static MySoundPair emergencyDropSound = new MySoundPair("SuperCruiseGravity", true);
        public static MySoundPair jumpInSound = new MySoundPair("quantum_jumpin", true);
        public static MySoundPair jumpOutSound = new MySoundPair("quantum_jumpout", true);
        public static MySoundPair hyperdictorCharge = new MySoundPair("Hyperdictor_charge", true);
        public static MySoundPair hyperdictorEngaged = new MySoundPair("Hyperdictor_engaged", true);
        public static MySoundPair hyperdictorReactor = new MySoundPair("ArcBlockReactorSm", true);
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
        [ProtoMember(8)]
        public bool spoolStop { get; set; }
    }

    [ProtoContract]
    public class HyperspaceMessage
    {
        [ProtoMember(1)]
        public long entityId { get; set; }
        [ProtoMember(2)]
        public long sendingPlayerID { get; set; }
        [ProtoMember(3)]
        public int mode { get; set; }
        [ProtoMember(4)]
        public float jumpDistanceRatio { get; set; }
        [ProtoMember(5)]
        public bool hasGpsCoords { get; set; }
        [ProtoMember(6)]
        public VRageMath.Vector3D gpsCoords { get; set; }
        [ProtoMember(7)]
        public string gpsName { get; set; }
    }

    [ProtoContract]
    public class HyperspaceFXMessage
    {
        [ProtoMember(1)]
        public VRageMath.Vector3D position { get; set; }
        [ProtoMember(2)]
        public VRageMath.Vector3D direction { get; set; }
        [ProtoMember(3)]
        public float scale { get; set; }
        [ProtoMember(4)]
        public bool isArrival { get; set; }
        [ProtoMember(5)]
        public bool isPrototech { get; set; }
        [ProtoMember(6)]
        public float speed { get; set; }
        [ProtoMember(7)]
        public bool isArrivalLead { get; set; }
    }

    [ProtoContract]
    public class HyperdictorToggleMessage
    {
        [ProtoMember(1)]
        public long entityId { get; set; }
        [ProtoMember(2)]
        public long sendingPlayerID { get; set; }
    }

    [ProtoContract]
    public class HyperdictorSyncMessage
    {
        [ProtoMember(1)]
        public long entityId { get; set; }
        [ProtoMember(2)]
        public int state { get; set; }
        [ProtoMember(3)]
        public float storedPowerRatio { get; set; }
        [ProtoMember(4)]
        public int cooldownTicksRemaining { get; set; }
    }

    [ProtoContract]
    public class HyperdictionSequenceMessage
    {
        [ProtoMember(1)]
        public int kind { get; set; }
        [ProtoMember(2)]
        public long victimGridEntityId { get; set; }
        [ProtoMember(3)]
        public long hyperdictorBlockEntityId { get; set; }
        [ProtoMember(4)]
        public int randomSeed { get; set; }
        [ProtoMember(5)]
        public Vector3D dropPosition { get; set; }
        [ProtoMember(6)]
        public float journeySeconds { get; set; }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.Simulation)]
    public class WarpDriveSession : MySessionComponentBase
    {
        public static WarpDriveSession instance;
        public static WarpDriveSession Instance => instance;
        public HyperdictionSession hyperdictionSession { get; private set; }
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
        public const ushort debugProxPacket = 4376;
        public const ushort hyperspaceTogglePacket = 4390;
        public const ushort hyperspaceFXPacket = 4391;
        public const ushort fleetJumpPacket = 4392;
        public const ushort hyperdictorTogglePacket = 4393;
        public const ushort hyperdictorSyncPacket = 4394;
        public const ushort hyperdictionSequencePacket = 4395;
        public bool debugDraw;
        public bool debugProx = true;
        private readonly MyStringId debugRayMaterial = MyStringId.GetOrCompute("Square");

        private readonly HashSet<HyperspaceSystem> hyperspaceSystems = new HashSet<HyperspaceSystem>();
        private readonly HashSet<CapitalFSD> capitalFSDSystems = new HashSet<CapitalFSD>();
        private readonly List<HyperspaceSystem> tempHyperspaceList = new List<HyperspaceSystem>();
        private readonly List<CapitalFSD> tempCapitalFSDList = new List<CapitalFSD>();

        public void RegisterHyperspace(HyperspaceSystem hs)
        {
            if (hs != null && !hyperspaceSystems.Contains(hs))
                hyperspaceSystems.Add(hs);
        }

        public void UnregisterHyperspace(HyperspaceSystem hs)
        {
            if (hs != null)
                hyperspaceSystems.Remove(hs);
        }

        public void RegisterCapitalFSD(CapitalFSD hs)
        {
            if (hs != null && !capitalFSDSystems.Contains(hs))
                capitalFSDSystems.Add(hs);
        }

        public void UnregisterCapitalFSD(CapitalFSD hs)
        {
            if (hs != null)
                capitalFSDSystems.Remove(hs);
        }

        public bool IsHyperspaceActiveForGrid(IMyCubeGrid grid)
        {
            if (grid == null) return false;
            foreach (var hs in hyperspaceSystems)
            {
                if (hs.gridSystem?.mainGrid == grid && hs.state != HyperspaceSystem.HyperState.idle)
                    return true;
            }
            foreach (var hs in capitalFSDSystems)
            {
                if (hs.gridSystem?.mainGrid == grid && hs.state != CapitalFSD.CapState.idle)
                    return true;
            }
            return false;
        }

        public bool IsInHyperspaceOrTransit(IMyCubeGrid grid)
        {
            if (grid == null) return false;
            foreach (var hs in hyperspaceSystems)
            {
                if (hs.gridSystem?.mainGrid == grid && hs.state == HyperspaceSystem.HyperState.active)
                    return true;
            }
            foreach (var hs in capitalFSDSystems)
            {
                if (hs.gridSystem?.mainGrid == grid && (hs.state == CapitalFSD.CapState.transit || hs.state == CapitalFSD.CapState.jumpIn || hs.state == CapitalFSD.CapState.jumpOut))
                    return true;
            }
            return false;
        }

        public bool IsSupercruiseActiveForGrid(IMyCubeGrid grid)
        {
            if (grid == null) return false;
            foreach (var ws in warpSystems)
            {
                if (ws.grid?.mainGrid == grid && ws.warpState != WarpSystem.State.idle)
                    return true;
            }
            return false;
        }

        public HashSet<HyperspaceSystem> GetHyperspaceSystems() => hyperspaceSystems;
        public HashSet<CapitalFSD> GetCapitalFSDSystems() => capitalFSDSystems;

        public HyperspaceSystem GetGridHyperspace(IMyCubeGrid grid)
        {
            if (grid == null) return null;
            foreach (var hs in hyperspaceSystems)
            {
                if (hs?.gridSystem?.mainGrid == grid)
                    return hs;
            }
            return null;
        }

        public HyperspaceSystem GetGridHyperspace(long gridEntityId)
        {
            if (gridEntityId == 0) return null;
            foreach (var hs in hyperspaceSystems)
            {
                if (hs?.gridSystem?.mainGrid?.EntityId == gridEntityId)
                    return hs;
            }
            return null;
        }

        public CapitalFSD GetGridCapitalFSD(IMyCubeGrid grid)
        {
            if (grid == null) return null;
            foreach (var cap in capitalFSDSystems)
            {
                if (cap?.gridSystem?.mainGrid == grid)
                    return cap;
            }
            return null;
        }

        public CapitalFSD GetGridCapitalFSD(long gridEntityId)
        {
            if (gridEntityId == 0) return null;
            foreach (var cap in capitalFSDSystems)
            {
                if (cap?.gridSystem?.mainGrid?.EntityId == gridEntityId)
                    return cap;
            }
            return null;
        }

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
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(debugProxPacket, ReceiveDebugProx);

                // Registered here (not in InitJumpControl) so a Hyperdictor works even when the world has no FSD block loaded
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(hyperdictorTogglePacket, ReceiveHyperdictorToggle);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(hyperdictorSyncPacket, ReceiveHyperdictorSync);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(hyperdictionSequencePacket, ReceiveHyperdictionSequence);

                MyVisualScriptLogicProvider.PlayerLeftCockpit += PlayerLeftCockpit;

                if (MyAPIGateway.Multiplayer.IsServer)
                    MyVisualScriptLogicProvider.PlayerDisconnected += PlayerDisconnected;

                if (!MyAPIGateway.Utilities.IsDedicated && !MyAPIGateway.Multiplayer.IsServer)
                    RequestConfig();

                if (!MyAPIGateway.Utilities.IsDedicated)
                    MyAPIGateway.Utilities.MessageEntered += CommandRecieved;

                HyperspaceSunManager.Instance.Init(ModContext?.ModPath);
                hyperdictionSession = new HyperdictionSession();
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
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(hyperspaceTogglePacket, ReceiveToggleHyperspace);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(hyperspaceFXPacket, ReceiveHyperspaceFX);
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(fleetJumpPacket, ReceiveFleetJump);

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

                HyperspaceControls.RegisterControls();
        }

        private bool GetWarpStatus(IMyTerminalBlock block)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();

            return HasValidSystem(drive) && drive.supportsSupercruise && drive.warpSystem.warpState != WarpSystem.State.idle;
        }

        private void SetWarpStatus(IMyTerminalBlock block, bool state)
        {
            WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
            if (!HasValidSystem(drive) || !MyAPIGateway.Multiplayer.IsServer || !drive.supportsSupercruise)
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
            if (drive == null || !drive.supportsSupercruise)
                return;

            WarpSystem system = RefreshSystem(drive);
            if (system == null)
                    return;

            IMyPlayer player = GetPlayer(sender);
            if (player == null)
                return;

            system.RequestToggle(drive, player);
        }

        private void ReceiveToggleHyperspace(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var message = MyAPIGateway.Utilities.SerializeFromBinary<HyperspaceMessage>(data);
            if (message == null)
                return;

            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(message.entityId, out entity))
                return;

            var block = entity as IMyFunctionalBlock;
            if (block != null)
            {
                WarpDrive drive = block?.GameLogic?.GetAs<WarpDrive>();
                if (drive == null && block is IMyTerminalBlock)
                {
                    drive = HyperspaceControls.GetWarpDrive(block as IMyTerminalBlock);
                }

                if (drive?.hyperspace != null)
                {
                    drive.hyperspace.mode = (HyperspaceSystem.JumpMode)message.mode;
                    drive.hyperspace.jumpDistanceRatio = message.jumpDistanceRatio;
                    if (message.hasGpsCoords)
                    {
                        drive.hyperspace.selectedGpsCoords = message.gpsCoords;
                        drive.hyperspace.selectedGpsName = message.gpsName ?? string.Empty;
                    }
                    else
                    {
                        drive.hyperspace.selectedGpsCoords = null;
                        drive.hyperspace.selectedGpsName = string.Empty;
                    }

                    if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                    {
                        MyAPIGateway.Multiplayer.SendMessageToOthers(hyperspaceTogglePacket, data);
                    }

                    drive.hyperspace.TriggerJump(message.sendingPlayerID);
                }
                else if (drive?.capitalFSD != null)
                {
                    drive.capitalFSD.mode = (HyperspaceSystem.JumpMode)message.mode;
                    drive.capitalFSD.jumpDistanceRatio = message.jumpDistanceRatio;
                    if (message.hasGpsCoords)
                    {
                        drive.capitalFSD.selectedGpsCoords = message.gpsCoords;
                        drive.capitalFSD.selectedGpsName = message.gpsName ?? string.Empty;
                    }
                    else
                    {
                        drive.capitalFSD.selectedGpsCoords = null;
                        drive.capitalFSD.selectedGpsName = string.Empty;
                    }

                    if (MyAPIGateway.Multiplayer.IsServer || MyAPIGateway.Utilities.IsDedicated)
                    {
                        MyAPIGateway.Multiplayer.SendMessageToOthers(hyperspaceTogglePacket, data);
                    }

                    drive.capitalFSD.TriggerJump(message.sendingPlayerID);
                }
            }
        }

        private void ReceiveHyperspaceFX(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var msg = MyAPIGateway.Utilities.SerializeFromBinary<HyperspaceFXMessage>(data);
            if (msg == null) return;

            if (MyAPIGateway.Session?.Camera != null)
            {
                double distSq = VRageMath.Vector3D.DistanceSquared(msg.position, MyAPIGateway.Session.Camera.Position);
                if (distSq <= 50000.0 * 50000.0)
                {
                    if (msg.isArrival)
                    {
                        if (msg.isArrivalLead)
                            HyperspaceSystem.SpawnGlobalArrivalLeadFX(msg.position, msg.direction, msg.scale, msg.speed, msg.isPrototech);
                        else
                            HyperspaceSystem.SpawnGlobalArrivalFX(msg.position, msg.direction, msg.scale, msg.isPrototech);
                    }
                    else
                    {
                        HyperspaceSystem.SpawnGlobalOriginFX(msg.position, msg.direction, msg.scale, msg.speed, msg.isPrototech);
                    }
                }
            }
        }

        private void ReceiveFleetJump(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var msg = MyAPIGateway.Utilities.SerializeFromBinary<FleetJumpMessage>(data);
            if (msg == null) return;

            FleetJumpSystem.HandleNetworkMessage(msg);
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

        private void CommandRecieved(string messageText, ref bool sendToOthers)
        {
            string[] words = messageText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !words[0].Equals("/FSD", StringComparison.OrdinalIgnoreCase))
                return;

            sendToOthers = false;

            string setting = words.Length > 1 ? words[1].ToLowerInvariant() : "";
            if (setting != "debugdraw" && setting != "debugprox")
            {
                MyAPIGateway.Utilities.ShowMessage("FSD", "Invalid command");
                return;
            }

            bool state;
            if (words.Length != 3 || !bool.TryParse(words[2], out state))
            {
                MyAPIGateway.Utilities.ShowMessage("FSD", "Invalid bool");
                return;
            }

            if (setting == "debugdraw")
            {
                if (!MyAPIGateway.Multiplayer.IsServer)
                {
                    MyAPIGateway.Utilities.ShowMessage("FSD", "Unable to change debug settings. Player is not host!");
                    return;
                }

                debugDraw = state;
            }
            else
            {
                if (MyAPIGateway.Session.PromoteLevel < MyPromoteLevel.Admin)
                {
                    MyAPIGateway.Utilities.ShowMessage("FSD", "Unable to change DebugProx. Player is not admin!");
                    return;
                }

                if (MyAPIGateway.Multiplayer.IsServer)
                    debugProx = state;
                else
                    MyAPIGateway.Multiplayer.SendMessageToServer(debugProxPacket, new[] { state ? (byte)1 : (byte)0 });
            }

            MyAPIGateway.Utilities.ShowMessage("FSD", (setting == "debugdraw" ? "DebugDraw" : "DebugProx") + " set to " + (state ? "true" : "false"));
        }

        private void ReceiveDebugProx(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            if (MyAPIGateway.Multiplayer.IsServer && data.Length == 1 && MyAPIGateway.Session.GetUserPromoteLevel(sender) >= MyPromoteLevel.Admin)
                debugProx = data[0] == 1;
        }

        private bool IsWarpDrive(IMyTerminalBlock block)
        {
            var drive = block?.GameLogic?.GetAs<WarpDrive>();
            return drive != null && drive.supportsSupercruise;
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
            WarpDrive.debugDraw.Clear();

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

            if (hyperspaceSystems.Count > 0)
            {
                tempHyperspaceList.Clear();
                tempHyperspaceList.AddRange(hyperspaceSystems);
                for (int i = 0; i < tempHyperspaceList.Count; i++)
                {
                    var hs = tempHyperspaceList[i];
                    if (hs != null)
                        hs.UpdateSimulationTick();
                }
                tempHyperspaceList.Clear();
            }

            if (capitalFSDSystems.Count > 0)
            {
                tempCapitalFSDList.Clear();
                tempCapitalFSDList.AddRange(capitalFSDSystems);
                for (int i = 0; i < tempCapitalFSDList.Count; i++)
                {
                    var cap = tempCapitalFSDList[i];
                    if (cap != null)
                        cap.Update();
                }
                tempCapitalFSDList.Clear();
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                HyperspaceSunManager.Instance.UpdateSimulation();
            }

            hyperdictionSession?.UpdateSimulation();
        }

        public override void Draw()
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            foreach (WarpSystem s in warpSystems)
                s.Draw();

            if (debugDraw)
            {
                Vector4 color = Color.Yellow;
                foreach (LineD ray in WarpDrive.debugDraw)
                    MySimpleObjectDraw.DrawLine(ray.From, ray.To, debugRayMaterial, ref color, 0.5f);
            }

            HyperspaceSunManager.Instance.UpdateDraw();
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
            if (drive == null || !drive.supportsSupercruise)
                return;

            WarpSystem system = RefreshSystem(drive);
            if (system == null)
                return;

            system.RequestToggle(drive, MyAPIGateway.Session?.Player);
        }

        private bool HasValidSystem(WarpDrive drive)
        {
            return drive?.warpSystem != null && drive.warpSystem.valid;
        }

        private void ReceiveHyperdictorToggle(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var msg = MyAPIGateway.Utilities.SerializeFromBinary<HyperdictorToggleMessage>(data);
            if (msg != null)
                hyperdictionSession?.HandleToggleMessage(msg, sender);
        }

        private void ReceiveHyperdictorSync(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var msg = MyAPIGateway.Utilities.SerializeFromBinary<HyperdictorSyncMessage>(data);
            if (msg != null)
                hyperdictionSession?.HandleSyncMessage(msg);
        }

        private void ReceiveHyperdictionSequence(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            var msg = MyAPIGateway.Utilities.SerializeFromBinary<HyperdictionSequenceMessage>(data);
            if (msg != null)
                hyperdictionSession?.HandleClientSequenceMessage(msg);
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
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(debugProxPacket, ReceiveDebugProx);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(hyperspaceTogglePacket, ReceiveToggleHyperspace);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(hyperspaceFXPacket, ReceiveHyperspaceFX);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(fleetJumpPacket, ReceiveFleetJump);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(hyperdictorTogglePacket, ReceiveHyperdictorToggle);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(hyperdictorSyncPacket, ReceiveHyperdictorSync);
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(hyperdictionSequencePacket, ReceiveHyperdictionSequence);

                hyperspaceSystems.Clear();
                capitalFSDSystems.Clear();
                hyperdictionSession?.Close();

                MyVisualScriptLogicProvider.PlayerLeftCockpit -= PlayerLeftCockpit;
                MyVisualScriptLogicProvider.PlayerDisconnected -= PlayerDisconnected;
                MyAPIGateway.Utilities.MessageEntered -= CommandRecieved;

                exposureQueue.Clear();

                foreach (WarpSystem s in warpSystems)
                    MyAPIGateway.Session?.OxygenProviderSystem?.RemoveOxygenGenerator(s);

                Settings.instance = null;
                HeatSink.heatEnabled = true;

                HyperspaceSunManager.Instance.Unload();

                instance = null;

                base.UnloadData();
            }
            catch { }
        }
    }
}
