using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Text;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "FSDriveLarge", "FSDriveSmall", "FSDriveLargeReskin", "PrototechFSDriveLarge", "PrototechFSDriveSmall")]
    public class WarpDrive : MyGameLogicComponent
    {
        public IMyFunctionalBlock block { get; private set; }
        public WarpSystem warpSystem { get; private set; }
        public bool hasPower => sink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) >= Math.Min(prevRequiredPower, _requiredPower);
        public float heat;
        public bool isRestabilizing => countdownTick > 0;
        
        // Ugly workaround
        public float requiredPower
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
        private bool sinkUpdated = false;
        private int countdownTick;
        private float powerMultiplier = 1;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);

            block = (IMyFunctionalBlock)Entity;
            Settings.GetShared();

            InitPowerSystem();

            if (WarpDriveSession.instance != null)
                initStart = WarpDriveSession.instance.runtime;

            if (!MyAPIGateway.Utilities.IsDedicated)
                block.AppendingCustomInfo += Block_AppendingCustomInfo;

            NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;
        }

        private void Block_AppendingCustomInfo(IMyTerminalBlock arg1, StringBuilder info)
        {
            if (arg1 == null || Settings.instance == null || warpSystem == null)
                return;

            info?.AppendLine("Max Required Power: " + (WarpSystem.GetCruisePower(this, warpSystem.GetShipMass(), Settings.instance.maxSpeed) * powerMultiplier).ToString("N") + " MW");

            info?.AppendLine("Current Required Power: " + requiredPower.ToString("N") + " MW");

                float capacity, breakEvenPower;
                warpSystem.HeatInfo(this, out capacity, out breakEvenPower);

            if (HeatSink.heatEnabled)
            {
                info?.AppendLine("System Heat: " + Math.Ceiling(heat * 10f).ToString("0") + "/" + (capacity * 10f).ToString("0"));

                info?.AppendLine("Thermal Buildup Point: " + breakEvenPower.ToString("N") + " MW");
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            if (WarpDriveSession.instance == null || block == null)
                return;

            WarpDriveSession.instance.InitJumpControl();

            if (!MyAPIGateway.Utilities.IsDedicated)
                block.RefreshCustomInfo();
        }

        public override void UpdateBeforeSimulation()
        {
            if (WarpDriveSession.instance == null)
                return;

            if (countdownTick > 0 && --countdownTick == 0)
                block.Enabled = true;

            if (!started)
            {
                if (warpSystem != null && warpSystem.valid)
                    started = true;
                else if (initStart <= WarpDriveSession.instance.runtime - 1)
                {
                    WarpSystem system = WarpDriveSession.instance.GetWarpSystem(this);
                    if (system == null)
                        return;

                    SetWarpSystem(system);
                    started = true;
                }
            }
            else
            {
                bool inUse = warpSystem != null && warpSystem.warpState != WarpSystem.State.idle;
                if (inUse || !sinkUpdated)
                {
                sink.Update();
                    sinkUpdated = !inUse;
                }
            }
        }

        public void IsRestabilizing()
        {
            if (block == null || !MyAPIGateway.Multiplayer.IsServer)
                return;

            block.Enabled = false;
            countdownTick = 60;
        }

        public override void Close()
        {
            if (!MyAPIGateway.Utilities.IsDedicated && block != null)
                block.AppendingCustomInfo -= Block_AppendingCustomInfo;

            if (warpSystem != null)
                warpSystem.OnSystemInvalidatedAction -= OnSystemInvalidated;
        }

        private void InitPowerSystem()
        {
            Settings settings = Settings.GetShared();

            powerMultiplier = WarpSystem.IsPrototech(this) ? 0.5f : 1f;

            sink = new MyResourceSinkComponent();
            sink.Init(MyStringHash.GetOrCompute("Utility"),
                (float)((WarpSystem.IsSmallDrive(this) ? settings.baseRequiredPowerSmall : settings.baseRequiredPower) * settings.powerRequirementMultiplier * powerMultiplier),
                ComputeRequiredPower, (MyCubeBlock)Entity);

            Entity.Components.Add(sink);
            sink.Update();
        }

        private float ComputeRequiredPower()
        {
            if (warpSystem == null || warpSystem.warpState == WarpSystem.State.idle)
                requiredPower = 0;

            return requiredPower;
        }

        public static bool ProximityDanger(MatrixD gridMatrix, IMyCubeGrid warpGrid, double gridSpeed, bool spooling = false)
        {
            if (warpGrid == null || (spooling && warpGrid.Physics == null))
                return false;

            if (spooling && !Settings.instance.AllowInSafeZone && !MySessionComponentSafeZones.IsActionAllowed(warpGrid.WorldAABB.Center, MySafeZoneAction.Damage))
                return true;

            Vector3D forward = gridMatrix.Forward;
            double pathLength = warpGrid.WorldAABB.HalfExtents.AbsMax() + 500.0;

            LineD path = new LineD(warpGrid.WorldAABB.Center, warpGrid.WorldAABB.Center + forward * pathLength);

            BoundingBox shipBox = warpGrid.LocalAABB;
            double alongX = Vector3D.Dot(warpGrid.WorldMatrix.Right, forward);
            double alongY = Vector3D.Dot(warpGrid.WorldMatrix.Up, forward);
            double alongZ = Vector3D.Dot(warpGrid.WorldMatrix.Forward, forward);
            double pathRadius = Math.Sqrt(shipBox.Width * shipBox.Width * (1 - alongX * alongX) + shipBox.Height * shipBox.Height * (1 - alongY * alongY) + shipBox.Depth * shipBox.Depth * (1 - alongZ * alongZ)) / 2;

            var sphere = new BoundingSphereD(warpGrid.WorldAABB.Center + forward * (pathLength / 2), pathLength / 2 + pathRadius);
            List<IMyEntity> entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);

            if (entList == null || entList.Count == 0)
                return false;

            var attachedList = new List<IMyCubeGrid>();

            //gets subgrids and locked landing gear to ignore
            MyAPIGateway.GridGroups.GetGroup(warpGrid, GridLinkTypeEnum.Physical, attachedList);

            foreach (var ent in entList)
            {
                if (!Settings.instance.AllowInSafeZone && ent is MySafeZone)
                    return true;

                if (!(ent is MyCubeGrid || ent is MyVoxelMap))
                    continue;

                //asteroids ignored over 20km/s
                if (!spooling && ent is MyVoxelMap && gridSpeed >= 333.333)
                    continue;

                if (attachedList.Contains(ent as IMyCubeGrid))
                    continue;

                BoundingBoxD obstacleBox = new BoundingBoxD(ent.WorldAABB.Min - new Vector3D(pathRadius), ent.WorldAABB.Max + new Vector3D(pathRadius));
                double hitDistance;

                if (obstacleBox.Intersects(ref path, out hitDistance))
                    return true;
            }

            return false;
        }

        public static bool EnemyProximityCharge(IMyCubeGrid warpGrid)
        {
            if (warpGrid == null || warpGrid.Physics == null || Settings.instance == null)
                return false;

            long warpGridOwner = (warpGrid.BigOwners != null && warpGrid.BigOwners.Count > 0) ? warpGrid.BigOwners[0] : 0L;
            if (warpGridOwner == 0L)
                return false;

            var warpGridFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(warpGridOwner);
            if (warpGridFaction == null)
                return false;

            var sphere = new BoundingSphereD(warpGrid.PositionComp.GetPosition(), Settings.instance.DetectEnemyGridInRange);
            var entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);

            if (entList == null || entList.Count == 0)
                return false;

            var attachedList = new List<IMyCubeGrid>();

            MyAPIGateway.GridGroups.GetGroup(warpGrid, GridLinkTypeEnum.Physical, attachedList);

            foreach (var ent in entList)
            {
                var foundGrid = ent as MyCubeGrid;
                if (foundGrid == null || attachedList.Contains(foundGrid))
                    continue;

                long foundGridOwner = (foundGrid.BigOwners != null && foundGrid.BigOwners.Count > 0) ? foundGrid.BigOwners[0] : 0L;
                if (foundGridOwner == 0L || foundGridOwner == warpGridOwner)
                    continue;

                var foundGridFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(foundGridOwner);
                if (foundGridFaction == null || foundGridFaction.FactionId == warpGridFaction.FactionId)
                    continue;

                if (MyAPIGateway.Session.Factions.GetRelationBetweenFactions(foundGridFaction.FactionId, warpGridFaction.FactionId) == MyRelationsBetweenFactions.Enemies && GridPowered(foundGrid))
                    return true;
            }

            return false;
        }

        private static bool GridPowered(MyCubeGrid grid)
        {
            if (grid == null)
                return false;

            foreach (MyCubeBlock enemyBlock in grid.GetFatBlocks())
            {
                var producer = enemyBlock as Sandbox.ModAPI.Ingame.IMyPowerProducer;
                if (producer != null && producer.IsWorking)
                    return true;
            }

            return false;
        }

        private void OnSystemInvalidated(WarpSystem system)
        {
            if (block.MarkedForClose || block.CubeGrid.MarkedForClose)
                return;

            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            if (WarpDriveSession.instance != null)
                SetWarpSystem(WarpDriveSession.instance.GetWarpSystem(this));
        }

        public void SetWarpSystem(WarpSystem system)
        {
            if (system == null || ReferenceEquals(warpSystem, system))
                return;

            if (warpSystem != null)
                warpSystem.OnSystemInvalidatedAction -= OnSystemInvalidated;

            warpSystem = system;
            warpSystem.OnSystemInvalidatedAction += OnSystemInvalidated;
        }

        public override bool Equals(object obj)
        {
            var drive = obj as WarpDrive;

            return drive != null && EqualityComparer<IMyFunctionalBlock>.Default.Equals(block, drive.block);
        }

        public override int GetHashCode()
        {
            return 957606482 + EqualityComparer<IMyFunctionalBlock>.Default.GetHashCode(block);
        }
    }
}
