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
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "FSDriveLarge", "FSDriveLarge_A",
        "FSDriveSmall", "FSDriveSmall_A",
        "FSDriveLargeReskin", "FSDriveLargeReskin_A", "FSDriveLargerReskin", "FSDriveLargerReskin_A",
        "PrototechFSDriveLarge", "PrototechFSDriveLarge_S",
        "PrototechFSDriveSmall", "PrototechFSDriveSmall_S")]
    public class WarpDrive : MyGameLogicComponent
    {
        public IMyFunctionalBlock Block { get; private set; }
        public WarpSystem System { get; private set; }
        public HyperspaceSystem Hyperspace => System?.Hyperspace;
        public Settings Settings { get; private set; }
        public static WarpDrive Instance;
        public bool HasPower => sink.CurrentInputByType(WarpConstants.ElectricityId) >= prevRequiredPower;
        public bool BlockWasON = false;

        public bool SupportsHyperspace
        {
            get
            {
                if (Block?.BlockDefinition == null) return false;
                var sub = Block.BlockDefinition.SubtypeId;
                return sub.EndsWith("_A") || sub.EndsWith("_S")
                    || sub == "PrototechFSDriveLarge" || sub == "PrototechFSDriveSmall";
            }
        }

        public bool IsPrototech
        {
            get
            {
                if (Block?.BlockDefinition == null) return false;
                var sub = Block.BlockDefinition.SubtypeId;
                return sub.StartsWith("PrototechFSDrive");
            }
        }

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
            try
            {
                if (Hyperspace != null)
                {
                    Hyperspace.Close();
                }
            }
            catch { }

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
                // Vanilla >> regular power and size
                case "FSDriveSmall":
                case "FSDriveSmall_A":
                    powerMultiplier = 1f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPowerSmall * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);               
                    break;

                case "FSDriveLarge":
                case "FSDriveLarge_A":
                case "FSDriveLargeReskin":
                case "FSDriveLargeReskin_A":
                case "FSDriveLargerReskin":
                case "FSDriveLargerReskin_A":
                    powerMultiplier = 1f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPower * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);               
                    break;

                // Prototech >> ultra op, yet costs a lot
                case "PrototechFSDriveSmall":
                case "PrototechFSDriveSmall_S":
                    powerMultiplier = 0.5f;
                    powerSystem.Init(MyStringHash.GetOrCompute("Utility"),
                        (float)(Settings.baseRequiredPowerSmall * Settings.powerRequirementMultiplier * powerMultiplier),
                        ComputeRequiredPower, (MyCubeBlock)Entity);
                    break;

                case "PrototechFSDriveLarge":
                case "PrototechFSDriveLarge_S":
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

            var hs = drive.Hyperspace;
            bool isHyperspaceActive = hs != null && hs.State != HyperspaceSystem.HyperState.Idle;
            bool isWarpActive = drive.System.WarpState != WarpSystem.State.Idle;

            if (!isWarpActive && !isHyperspaceActive)
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
                            // If Hyperspace is active, abort or emergency drop
                            if (isHyperspaceActive)
                            {
                                if (hs.State == HyperspaceSystem.HyperState.Active)
                                {
                                    hs.ExitHyperspace();
                                    drive.System.SendMessage("EMERGENCY DROP - SEAT VACATED", 5f, "Red");
                                }
                                else if (hs.State == HyperspaceSystem.HyperState.Charging || hs.State == HyperspaceSystem.HyperState.HoldingCharge || hs.State == HyperspaceSystem.HyperState.Countdown)
                                {
                                    hs.AbortJump("JUMP ABORTED - CREW LEFT SEAT");
                                }
                            }

                            if (isWarpActive)
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
        }

        public static bool IsGridIgnored(IMyCubeGrid candidateGrid, IMyCubeGrid myGrid, long myFleetLeader)
        {
            if (candidateGrid == null || candidateGrid.MarkedForClose)
                return true;

            // Ignore unphysical, preview, or empty dummy grids (e.g. projections, mod placeholders)
            if (candidateGrid.Physics == null || !candidateGrid.Physics.Enabled)
                return true;

            var cg = candidateGrid as MyCubeGrid;
            if (cg != null && (cg.IsPreview || cg.BlocksCount == 0))
                return true;

            // Ignore shield, weaponcore, nanite, and other utility dummy entities disguised as grids
            string candidateName = candidateGrid.DisplayName ?? candidateGrid.CustomName ?? candidateGrid.Name ?? "";
            if (candidateName.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidateName.IndexOf("dshield", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidateName.IndexOf("nanite", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (myGrid != null)
            {
                if (candidateGrid.EntityId == myGrid.EntityId)
                    return true;

                var topCandidate = candidateGrid.GetTopMostParent();
                var topMyGrid = myGrid.GetTopMostParent();
                if (topCandidate != null && topMyGrid != null && topCandidate.EntityId == topMyGrid.EntityId)
                    return true;

                if (MyAPIGateway.GridGroups.HasConnection(myGrid, candidateGrid, GridLinkTypeEnum.Logical) ||
                    MyAPIGateway.GridGroups.HasConnection(myGrid, candidateGrid, GridLinkTypeEnum.Physical) ||
                    MyAPIGateway.GridGroups.HasConnection(myGrid, candidateGrid, GridLinkTypeEnum.Mechanical))
                {
                    return true;
                }
            }

            // Fleet whitelist: Ignore Leader, Wingmen, and all their subgrids
            if (myFleetLeader != 0L)
            {
                if (candidateGrid.EntityId == myFleetLeader || FleetJumpSystem.GetLeaderIdForGrid(candidateGrid.EntityId) == myFleetLeader || FleetJumpSystem.IsSameFleet(myGrid?.EntityId ?? 0L, candidateGrid.EntityId, myFleetLeader))
                    return true;

                var topCandidate = candidateGrid.GetTopMostParent();
                if (topCandidate != null && (topCandidate.EntityId == myFleetLeader || FleetJumpSystem.GetLeaderIdForGrid(topCandidate.EntityId) == myFleetLeader || FleetJumpSystem.IsSameFleet(myGrid?.EntityId ?? 0L, topCandidate.EntityId, myFleetLeader)))
                    return true;
            }

            return false;
        }

        public bool ProxymityDangerInWarp(MatrixD gridMatrix, MyCubeGrid MainGrid, double GridSpeed)
        {
            if (MainGrid == null || MainGrid.Physics == null)
                return false;

            Vector3D forward = gridMatrix.Forward;
            Vector3D gridCenter = MainGrid.PositionComp.WorldAABB.Center;

            // Compute the ship's forward extent (front nose offset) and cross-sectional radius
            BoundingBoxD localAABB = MainGrid.PositionComp.LocalAABB;
            MyOrientedBoundingBoxD shipOBB = new MyOrientedBoundingBoxD(localAABB, MainGrid.WorldMatrix);
            Vector3D[] corners = new Vector3D[8];
            shipOBB.GetCorners(corners, 0);

            double maxForwardDist = 0.0;
            double maxPerpDist = (MainGrid.GridSizeEnum == MyCubeSize.Small) ? 2.5 : 5.0;

            for (int i = 0; i < 8; i++)
            {
                Vector3D offset = corners[i] - gridCenter;
                double fwdProj = Vector3D.Dot(offset, forward);
                if (fwdProj > maxForwardDist)
                    maxForwardDist = fwdProj;

                Vector3D perpOffset = offset - (forward * fwdProj);
                double perpDist = perpOffset.Length();
                if (perpDist > maxPerpDist)
                    maxPerpDist = perpDist;
            }

            double corridorRadius = maxPerpDist + 2.0;
            double corridorLength = (MainGrid.GridSizeEnum == MyCubeSize.Small) ? 400.0 : 500.0;

            // Start strictly outside the ship's nose to prevent self-collision
            Vector3D startPos = gridCenter + (forward * (maxForwardDist + 5.0));
            Vector3D endPos = startPos + (forward * corridorLength);
            RayD flightRay = new RayD(startPos, forward);

            double scanRadius = Math.Max(corridorLength * 0.55, 1500.0);
            BoundingSphereD querySphere = new BoundingSphereD(startPos + (forward * (corridorLength * 0.5)), scanRadius);
            var entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref querySphere);
            if (entList == null || entList.Count == 0)
                return false;

            long myGridId = MainGrid.EntityId;
            long myFleetLeader = FleetJumpSystem.GetLeaderIdForGrid(myGridId);

            foreach (var ent in entList)
            {
                if (ent == null || ent.MarkedForClose) continue;

                if (ent is MySafeZone)
                {
                    var sz = ent as MySafeZone;
                    if (sz != null)
                    {
                        var szSphere = new BoundingSphereD(sz.PositionComp.GetPosition(), sz.Radius + corridorRadius);
                        double? hit = szSphere.Intersects(flightRay);
                        if (hit.HasValue && hit.Value >= 0 && hit.Value <= corridorLength)
                            return true;
                    }
                    continue;
                }

                var foundGrid = ent as IMyCubeGrid;
                if (foundGrid != null)
                {
                    if (IsGridIgnored(foundGrid, MainGrid, myFleetLeader))
                        continue;

                    // 3D Capsule-to-Box Corridor Check
                    BoundingSphereD obstacleSphere = foundGrid.PositionComp.WorldVolume;
                    Vector3D seg = endPos - startPos;
                    double segLenSq = seg.LengthSquared();
                    double t = (segLenSq > 1e-6) ? MathHelper.Clamp(Vector3D.Dot(obstacleSphere.Center - startPos, seg) / segLenSq, 0.0, 1.0) : 0.0;
                    Vector3D closestPt = startPos + (seg * t);
                    double maxDist = obstacleSphere.Radius + corridorRadius;
                    if (Vector3D.DistanceSquared(obstacleSphere.Center, closestPt) <= maxDist * maxDist)
                    {
                        Vector3D localPt = Vector3D.Transform(closestPt, foundGrid.PositionComp.WorldMatrixInvScaled);
                        double localDistSq = ((BoundingBoxD)foundGrid.PositionComp.LocalAABB).DistanceSquared(localPt);
                        if (localDistSq <= corridorRadius * corridorRadius)
                            return true;
                    }
                }
                else if (ent is MyVoxelBase)
                {
                    // Don't drop if grid speed is 20 (333.33 km/s) or above
                    if (GridSpeed >= 333.333)
                        continue;

                    var voxel = ent as MyVoxelBase;
                    if (voxel == null) continue;

                    // Ignore planets and planet terrain physics chunks
                    if (voxel is MyPlanet || (voxel as MyVoxelBase)?.RootVoxel is MyPlanet)
                        continue;

                    double asteroidScanDist = Math.Max(corridorLength, 2500.0);
                    BoundingBoxD asteroidCorridorBox = new BoundingBoxD(
                        Vector3D.Min(startPos, startPos + forward * asteroidScanDist) - new Vector3D(corridorRadius),
                        Vector3D.Max(startPos, startPos + forward * asteroidScanDist) + new Vector3D(corridorRadius)
                    );

                    if (voxel.PositionComp.WorldAABB.Intersects(asteroidCorridorBox))
                    {
                        if (voxel.GetIntersectionWithAABB(ref asteroidCorridorBox))
                            return true;
                    }
                }
            }
            return false;
        }

        public bool ProxymityDangerCharge(MatrixD gridMatrix, IMyCubeGrid WarpGrid)
        {
            if (WarpGrid == null || WarpGrid.Physics == null)
                return false;

            Vector3D forward = gridMatrix.Forward;
            Vector3D gridCenter = WarpGrid.PositionComp.WorldAABB.Center;

            // Compute the ship's forward extent (front nose offset) and cross-sectional radius
            BoundingBoxD localAABB = WarpGrid.PositionComp.LocalAABB;
            MyOrientedBoundingBoxD shipOBB = new MyOrientedBoundingBoxD(localAABB, WarpGrid.WorldMatrix);
            Vector3D[] corners = new Vector3D[8];
            shipOBB.GetCorners(corners, 0);

            double maxForwardDist = 0.0;
            double maxPerpDist = (WarpGrid.GridSizeEnum == MyCubeSize.Small) ? 2.5 : 5.0;

            for (int i = 0; i < 8; i++)
            {
                Vector3D offset = corners[i] - gridCenter;
                double fwdProj = Vector3D.Dot(offset, forward);
                if (fwdProj > maxForwardDist)
                    maxForwardDist = fwdProj;

                Vector3D perpOffset = offset - (forward * fwdProj);
                double perpDist = perpOffset.Length();
                if (perpDist > maxPerpDist)
                    maxPerpDist = perpDist;
            }

            double corridorRadius = maxPerpDist + 2.0;
            double corridorLength = (WarpGrid.GridSizeEnum == MyCubeSize.Small) ? 400.0 : 500.0;

            // Start strictly outside the ship's nose to prevent self-collision
            Vector3D startPos = gridCenter + (forward * (maxForwardDist + 5.0));
            Vector3D endPos = startPos + (forward * corridorLength);
            RayD flightRay = new RayD(startPos, forward);

            double scanRadius = Math.Max(corridorLength * 0.55, 1500.0);
            BoundingSphereD querySphere = new BoundingSphereD(startPos + (forward * (corridorLength * 0.5)), scanRadius);
            var entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref querySphere);
            if (entList == null || entList.Count == 0)
                return false;

            long myGridId = WarpGrid.EntityId;
            long myFleetLeader = FleetJumpSystem.GetLeaderIdForGrid(myGridId);

            foreach (var ent in entList)
            {
                if (ent == null || ent.MarkedForClose) continue;

                if (ent is MySafeZone)
                {
                    var sz = ent as MySafeZone;
                    if (sz != null)
                    {
                        var szSphere = new BoundingSphereD(sz.PositionComp.GetPosition(), sz.Radius + corridorRadius);
                        double? hit = szSphere.Intersects(flightRay);
                        if (hit.HasValue && hit.Value >= 0 && hit.Value <= corridorLength)
                        {
                            MyAPIGateway.Utilities.ShowNotification("Can't Start FSD - SafeZone in flight path!", 4000, "Red");
                            return true;
                        }
                    }
                    continue;
                }

                var foundGrid = ent as IMyCubeGrid;
                if (foundGrid != null)
                {
                    if (IsGridIgnored(foundGrid, WarpGrid, myFleetLeader))
                        continue;

                    // 3D Capsule-to-Box Corridor Check
                    BoundingSphereD obstacleSphere = foundGrid.PositionComp.WorldVolume;
                    Vector3D seg = endPos - startPos;
                    double segLenSq = seg.LengthSquared();
                    double t = (segLenSq > 1e-6) ? MathHelper.Clamp(Vector3D.Dot(obstacleSphere.Center - startPos, seg) / segLenSq, 0.0, 1.0) : 0.0;
                    Vector3D closestPt = startPos + (seg * t);
                    double maxDist = obstacleSphere.Radius + corridorRadius;
                    if (Vector3D.DistanceSquared(obstacleSphere.Center, closestPt) <= maxDist * maxDist)
                    {
                        Vector3D localPt = Vector3D.Transform(closestPt, foundGrid.PositionComp.WorldMatrixInvScaled);
                        double localDistSq = ((BoundingBoxD)foundGrid.PositionComp.LocalAABB).DistanceSquared(localPt);
                        if (localDistSq <= corridorRadius * corridorRadius)
                        {
                            string gridName = !string.IsNullOrWhiteSpace(foundGrid.DisplayName) ? foundGrid.DisplayName :
                                              (!string.IsNullOrWhiteSpace(foundGrid.CustomName) ? foundGrid.CustomName : "Ship/Station");
                            MyAPIGateway.Utilities.ShowNotification($"Can't Start FSD - Obstacle ahead: {gridName}", 4000, "Red");
                            return true;
                        }
                    }
                }
                else if (ent is MyVoxelBase)
                {
                    var voxel = ent as MyVoxelBase;
                    if (voxel == null) continue;

                    // Ignore planets and planet terrain physics chunks
                    if (voxel is MyPlanet || (voxel as MyVoxelBase)?.RootVoxel is MyPlanet)
                        continue;

                    // Asteroids: check actual voxel geometry inside the flight corridor (up to 2.5km)
                    double asteroidScanDist = Math.Max(corridorLength, 2500.0);
                    BoundingBoxD asteroidCorridorBox = new BoundingBoxD(
                        Vector3D.Min(startPos, startPos + forward * asteroidScanDist) - new Vector3D(corridorRadius),
                        Vector3D.Max(startPos, startPos + forward * asteroidScanDist) + new Vector3D(corridorRadius)
                    );

                    if (voxel.PositionComp.WorldAABB.Intersects(asteroidCorridorBox))
                    {
                        if (voxel.GetIntersectionWithAABB(ref asteroidCorridorBox))
                        {
                            MyAPIGateway.Utilities.ShowNotification("Can't Start FSD - Asteroid in flight path!", 4000, "Red");
                            return true;
                        }
                    }
                }
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

            var WarpGridOwner = WarpGrid.BigOwners.FirstOrDefault();
            var WarpGridFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(WarpGridOwner);
            long myFleetLeader = FleetJumpSystem.GetLeaderIdForGrid(WarpGrid.EntityId);

            foreach (var ent in entList)
            {
                var FoundGrid = ent as IMyCubeGrid;
                if (FoundGrid == null || IsGridIgnored(FoundGrid, WarpGrid, myFleetLeader))
                    continue;

                if (FoundGrid.BigOwners != null && FoundGrid.BigOwners.Count > 0 && FoundGrid.BigOwners.FirstOrDefault() != 0L)
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
                        if (FactionsRelationship == MyRelationsBetweenFactions.Enemies)
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
