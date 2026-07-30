using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Noise.Combiners;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "FSDriveLarge", "FSDriveSmall", "FSDriveLargeReskin", "PrototechFSDriveLarge", "PrototechFSDriveSmall")]
    public class WarpDrive : MyGameLogicComponent
    {
        public IMyFunctionalBlock Block { get; private set; }
        public WarpSystem System { get; private set; }
        public Settings Settings { get; private set; }
        public static WarpDrive Instance;
        public bool HasPower => sink.CurrentInputByType(WarpConstants.ElectricityId) >= prevRequiredPower;
        public bool BlockWasON = false;

        private T CastProhibit<T>(T ptr, object val) => (T)val;

        // Ugly workaround
        public float RequiredPower
        {
            get
            {
                return _requiredPower;
            }
            set
            {
                prevRequiredPower = _requiredPower;
                _requiredPower = (float)(value * powerMultiplier);
            }
        }
        private float prevRequiredPower;
        private float _requiredPower;
        private MyResourceSinkComponent sink;
        private long initStart;
        private bool started = false;
        private int BlockOnTick = 0;
        private float powerMultiplier = 1;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);

            Instance = this;
            Block = (IMyFunctionalBlock)Entity;
            Settings = Settings.Load();

            InitPowerSystem();

            if (WarpDriveSession.Instance != null)
                initStart = WarpDriveSession.Instance.Runtime;

            MyVisualScriptLogicProvider.PlayerLeftCockpit += PlayerLeftCockpit;

            if (!MyAPIGateway.Utilities.IsDedicated)
                Block.AppendingCustomInfo += Block_AppendingCustomInfo;

            NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;
        }

        private void Block_AppendingCustomInfo(IMyTerminalBlock arg1, StringBuilder Info)
        {
            if (arg1 == null || Settings == null || System == null)
                return;

            float _mass = 1;
            if (System.GridsMass != null && System.GridsMass.Count > 0 && arg1.CubeGrid != null && System.GridsMass.ContainsKey(arg1.CubeGrid.EntityId))
                System.GridsMass.TryGetValue(arg1.CubeGrid.EntityId, out _mass);
            else
            {
                if (arg1.CubeGrid != null)
                {
                    _mass = System.CulcucateGridGlobalMass(arg1.CubeGrid);
                    System.GridsMass[arg1.CubeGrid.EntityId] = _mass;
                }
            }

            float SpeedNormalize = (float)(Settings.maxSpeed * 0.06); // 60 / 1000
            float SpeedCalc = 1f + (SpeedNormalize * SpeedNormalize);

            float MassCalc;
            if (arg1.CubeGrid.GridSizeEnum == MyCubeSize.Small)
                MassCalc = _mass * (SpeedCalc / 0.528f) / 800000f;
            else
                MassCalc = _mass * (SpeedCalc / 0.528f) / 800000f;

            float MaxNeededPower;

            if (arg1.CubeGrid.GridSizeEnum == MyCubeSize.Large)
            {
                if (System.currentSpeedPt != Settings.maxSpeed)
                    MaxNeededPower = (MassCalc + Settings.baseRequiredPower * 3) / Settings.powerRequirementBySpeedDeviderLarge * 0.9725f;
                else
                    MaxNeededPower = RequiredPower;
            }
            else
            {
                if (System.currentSpeedPt != Settings.maxSpeed)
                    MaxNeededPower = (MassCalc + Settings.baseRequiredPowerSmall * 3) / Settings.powerRequirementBySpeedDeviderSmall * 0.9725f;
                else
                    MaxNeededPower = RequiredPower;
            }

            Info?.AppendLine("Max Required Power: " + MaxNeededPower.ToString("N") + " MW");

            Info?.AppendLine("Required Power: " + RequiredPower.ToString("N") + " MW");

            if (sink != null)
                Info?.AppendLine("Current Power: " + sink.CurrentInputByType(WarpConstants.ElectricityId).ToString("N") + " MW");

            Info?.Append("FSD Heat: ").Append(System.DriveHeat).Append("%\n");
        }

        public override void UpdateBeforeSimulation10()
        {
            if (WarpDriveSession.Instance == null || Block == null)
                return;

            // init once
            if (Block != null)
                WarpDriveSession.Instance.InitJumpControl();

            if (BlockWasON)
            {
                if (BlockOnTick++ > 20)
                {
                    Block.Enabled = true;
                    BlockWasON = false;
                    BlockOnTick = 0;
                }
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
                Block.RefreshCustomInfo();
        }

        public override void UpdateBeforeSimulation()
        {
            if (WarpDriveSession.Instance == null)
                return;

            if (!started)
            {
                if (System != null && System.Valid)
                    started = true;
                else if (initStart <= WarpDriveSession.Instance.Runtime - WarpConstants.groupSystemDelay)
                {
                    System = WarpDriveSession.Instance.GetWarpSystem(this);
                    if (System == null)
                        return;

                    System.OnSystemInvalidatedAction += OnSystemInvalidated;
                    started = true;
                }
            }
            else
            {
                sink.Update();
            }
        }

        public override void Close()
        {
            if (System == null)
                return;

            System.OnSystemInvalidatedAction -= OnSystemInvalidated;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                if (Block != null)
                    Block.AppendingCustomInfo -= Block_AppendingCustomInfo;

                System.StopBlinkParticleEffect();
            }

            if (Block != null && Block.CubeGrid != null && System.GridsMass.ContainsKey(Block.CubeGrid.EntityId))
                System.GridsMass.Remove(Block.CubeGrid.EntityId);
        }

        private void InitPowerSystem()
        {
            MyResourceSinkComponent powerSystem = new MyResourceSinkComponent();

            switch (Block.BlockDefinition.SubtypeId)
            {
                // Updates like intels tik-tok process
                // Vanilla >> regular power and size
                case "FSDriveSmall":
                    powerMultiplier = 1f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPowerSmall * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);               
                    break;

                case "FSDriveLarge":
                    powerMultiplier = 1f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPower * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);               
                    break;

                case "FSDriveLargeReskin":
                    powerMultiplier = 1f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPower * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);               
                    break;

                // Prototech >> ultra op, yet costs a lot
                case "PrototechFSDriveSmall":
                    powerMultiplier = 0.5f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPowerSmall * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);
                    break;

                case "PrototechFSDriveLarge":
                    powerMultiplier = 0.5f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPower * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);
                    break;
               

                default:
                    // No drive found - deactivated
                    break;
            }

            Entity.Components.Add(powerSystem);
            sink = powerSystem;
            sink.Update();
        }

        private float ComputeRequiredPower()
        {
            if (System == null || System.WarpState == WarpSystem.State.Idle)
                RequiredPower = 0;

            return RequiredPower;
        }

        public void PlayerLeftCockpit(string entityName, long playerId, string gridName)
        {
            if (Block == null || System == null)
                return;

            WarpDrive drive = Block?.GameLogic?.GetAs<WarpDrive>();
            if (drive == null)
                return;
            else if (drive.System.WarpState == WarpSystem.State.Idle)
                return;

            if (entityName != "")
            {
                long temp_id;
                if (long.TryParse(entityName, out temp_id))
                {
                    var dump_cockpit = MyAPIGateway.Entities.GetEntityById(temp_id) as IMyShipController;
                    var CockpitGrid = dump_cockpit?.CubeGrid as MyCubeGrid;
                    HashSet<IMyShipController> FoundCockpits = new HashSet<IMyShipController>();

                    if (CockpitGrid == null)
                        return;

                    if ((bool)(drive.System.grid?.cockpits?.TryGetValue(CockpitGrid, out FoundCockpits)))
                    {
                        if (FoundCockpits.Count > 0 && FoundCockpits.Contains(dump_cockpit))
                        {
                            if (dump_cockpit.CubeGrid.EntityId != drive.Block.CubeGrid.EntityId)
                                return;

                            drive.System.SafeTriggerON = true;

                            if (MyAPIGateway.Utilities.IsDedicated || MyAPIGateway.Multiplayer.IsServer)
                            {
                                drive.System.currentSpeedPt = -1f;
                                dump_cockpit.CubeGrid?.Physics?.ClearSpeed();

                                drive.System.Dewarp(true);
                                Block.Enabled = false;
                                BlockWasON = true;
                            }

                            drive.System.SafeTriggerON = false;
                        }
                    }
                }
            }
        }

        public bool ProxymityDangerInWarp(MatrixD gridMatrix, MyCubeGrid MainGrid, double GridSpeed)
        {
            if (MainGrid == null)
                return false;

            List<IMyEntity> entList;
            IMyCubeGrid WarpGrid = MainGrid;
            Vector3D forward = gridMatrix.Forward;
            MatrixD FrontStart = MatrixD.CreateFromDir(-forward);
            Vector3D PointFromFront;

            if (WarpGrid.GridSizeEnum == MyCubeSize.Small)
            {
                Vector3D effectOffsetSmall = forward * WarpGrid.WorldAABB.HalfExtents.AbsMax();
                FrontStart.Translation = WarpGrid.WorldAABB.Center + effectOffsetSmall;
                FrontStart.Translation += forward * 400.0;
                PointFromFront = FrontStart.Translation;
                var sphere = new BoundingSphereD(PointFromFront, 300.0);
                entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            }
            else
            {
                Vector3D effectOffsetLarge = forward * WarpGrid.WorldAABB.HalfExtents.AbsMax();
                FrontStart.Translation = WarpGrid.WorldAABB.Center + effectOffsetLarge;
                FrontStart.Translation += forward * 500.0;
                PointFromFront = FrontStart.Translation;
                var sphere = new BoundingSphereD(PointFromFront, 400.0);
                entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            }

            if (entList == null || entList.Count == 0)
                return false;

            var AttachedList = new List<IMyCubeGrid>();

            // get all subgrids grids and locked on landing gear.
            MyAPIGateway.GridGroups.GetGroup(WarpGrid, GridLinkTypeEnum.Physical, AttachedList);

            foreach (var ent in entList)
            {
                if (ent is MySafeZone)
                    return true;

                if (!(ent is MyCubeGrid || ent is MyVoxelMap))
                    continue;

                // dont stop if grid speed is 20 or above.
                if (ent is MyVoxelMap && GridSpeed >= 333.333)
                    continue;

                if (ent is MyCubeGrid)
                {
                    var FoundGrid = ent as IMyCubeGrid;

                    if (FoundGrid != null && AttachedList != null && AttachedList.Count > 0 && AttachedList.Contains(FoundGrid))
                        continue;
                }

                var EntityPosition = ent.GetPosition() + Vector3D.Zero;

                if (WarpGrid.GridSizeEnum == MyCubeSize.Small)
                {
                    if ((EntityPosition - PointFromFront).Length() <= 250.0)
                        return true;
                }
                else
                {
                    if (ent is MyVoxelMap && (EntityPosition - PointFromFront).Length() <= 280.0)
                        return true;
                    else if ((EntityPosition - PointFromFront).Length() <= 220.0)
                        return true;
                }
            }
            return false;
        }

        public bool ProxymityDangerCharge(MatrixD gridMatrix, IMyCubeGrid WarpGrid)
        {
            if (WarpGrid == null || WarpGrid.Physics == null)
                return false;

            List<IMyEntity> entList;
            Vector3D forward = gridMatrix.Forward;
            MatrixD FrontStart = MatrixD.CreateFromDir(-forward);
            Vector3D PointFromFront;

            if (MyAPIGateway.Session?.Player != null)
            {
                bool allowed = MySessionComponentSafeZones.IsActionAllowed(MyAPIGateway.Session.Player.Character.WorldMatrix.Translation, CastProhibit(MySessionComponentSafeZones.AllowedActions, 1));
                if (!allowed)
                    return true;
            }

            if (WarpGrid.GridSizeEnum == MyCubeSize.Small)
            {
                Vector3D effectOffsetSmall = forward * WarpGrid.WorldAABB.HalfExtents.AbsMax();
                FrontStart.Translation = WarpGrid.WorldAABB.Center + effectOffsetSmall;
                FrontStart.Translation += forward * 400.0;
                PointFromFront = FrontStart.Translation;
                var sphere = new BoundingSphereD(PointFromFront, 300.0);
                entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            }
            else
            {
                Vector3D effectOffsetLarge = forward * WarpGrid.WorldAABB.HalfExtents.AbsMax();
                FrontStart.Translation = WarpGrid.WorldAABB.Center + effectOffsetLarge;
                FrontStart.Translation += forward * 500.0;
                PointFromFront = FrontStart.Translation;
                var sphere = new BoundingSphereD(PointFromFront, 400.0);
                entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            }

            if (entList == null || entList.Count == 0)
                return false;

            var AttachedList = new List<IMyCubeGrid>();

            // get all subgrids grids and locked on landing gear.
            MyAPIGateway.GridGroups.GetGroup(WarpGrid, GridLinkTypeEnum.Physical, AttachedList);

            foreach (var ent in entList)
            {
                if (ent is MySafeZone)
                    return true;

                if (!(ent is MyCubeGrid || ent is MyVoxelMap))
                    continue;

                if (ent is MyCubeGrid)
                {
                    var FoundGrid = ent as IMyCubeGrid;

                    if (FoundGrid != null && AttachedList != null && AttachedList.Count > 0 && AttachedList.Contains(FoundGrid))
                        continue;
                }

                var EntityPosition = ent.PositionComp.GetPosition() + Vector3D.Zero;

                if ((EntityPosition - PointFromFront).Length() <= 250.0)
                    return true;
            }

            return false;
        }

        public bool EnemyProxymityDangerCharge(IMyCubeGrid WarpGrid)
        {
            if (WarpGrid == null || WarpGrid.Physics == null)
                return false;

            var Gridlocation = WarpGrid.PositionComp.GetPosition();
            var sphere = new BoundingSphereD(Gridlocation, Settings.DetectEnemyGridInRange);
            var entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);

            if (entList == null || entList.Count == 0)
                return false;

            var AttachedList = new List<IMyCubeGrid>();

            // get all subgrids grids and locked on landing gear.
            MyAPIGateway.GridGroups.GetGroup(WarpGrid, GridLinkTypeEnum.Physical, AttachedList);

            var WarpGridOwner = WarpGrid.BigOwners.FirstOrDefault();
            var WarpGridFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(WarpGridOwner);

            foreach (var ent in entList)
            {
                if (!(ent is MyCubeGrid))
                    continue;

                if (ent is MyCubeGrid)
                {
                    var FoundGrid = ent as IMyCubeGrid;

                    if (FoundGrid != null && AttachedList != null && AttachedList.Count > 0 && AttachedList.Contains(FoundGrid))
                        continue;

                    if (FoundGrid.BigOwners != null && FoundGrid.BigOwners.FirstOrDefault() != 0L)
                    {
                        var FoundGridOwner = FoundGrid.BigOwners.FirstOrDefault();

                        if (FoundGridOwner == WarpGridOwner)
                            continue;

                        var FoundGridFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(FoundGridOwner);

                        if (WarpGridFaction != null && FoundGridFaction != null)
                        {
                            if (FoundGridFaction.FactionId == WarpGridFaction.FactionId)
                                continue;

                            var FactionsRelationship = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(FoundGridFaction.FactionId, WarpGridFaction.FactionId);
                            if (FactionsRelationship != MyRelationsBetweenFactions.Enemies)
                                continue;

                            // found enenmy grid in sphere!
                            return true;
                        }
                        else
                            return true;
                    }
                }
            }

            return false;
        }

        private void OnSystemInvalidated(WarpSystem system)
        {
            if (Block.MarkedForClose || Block.CubeGrid.MarkedForClose)
                return;

            WarpDriveSession.Instance.DelayedGetWarpSystem(this);
        }

        public void SetWarpSystem(WarpSystem system)
        {
            System = system;
            System.OnSystemInvalidatedAction += OnSystemInvalidated;
        }

        public override bool Equals(object obj)
        {
            var drive = obj as WarpDrive;

            return drive != null && EqualityComparer<IMyFunctionalBlock>.Default.Equals(Block, drive.Block);
        }

        public override int GetHashCode()
        {
            return 957606482 + EqualityComparer<IMyFunctionalBlock>.Default.GetHashCode(Block);
        }
    }
}
