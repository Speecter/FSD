using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.Models;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;

namespace WarpDriveMod
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "FSDriveLarge", "FSDriveLarge_A",
        "FSDriveSmall", "FSDriveSmall_A",
        "FSDriveLargeReskin", "FSDriveLargeReskin_A", "FSDriveLargerReskin", "FSDriveLargerReskin_A",
        "PrototechFSDriveLarge", "PrototechFSDriveLarge_S", "PrototechFSDriveLarge_9S",
        "PrototechFSDriveSmall", "PrototechFSDriveSmall_S")]
    public class WarpDrive : MyGameLogicComponent
    {
        public static readonly Guid CAP_STORAGE_GUID = new Guid("B6E1C3A2-5D47-4F0A-9C8E-2A7D1F3B6E50");
        private static readonly Dictionary<long, WarpDrive> drivesById = new Dictionary<long, WarpDrive>();

        public static WarpDrive Find(long entityId)
        {
            WarpDrive drive;
            return drivesById.TryGetValue(entityId, out drive) ? drive : null;
        }

        /// <summary>Energy stored for hyperspace jumps (hyperspace capable drives only). Saved with the block.</summary>
        public float CapStoredMWh { get; private set; }

        private bool capLoaded;
        private float capLastSaved = -1f;
        private float capLastSynced = -1f;
        private int capSyncTicks;
        private float capDrawMW;
        private float baseMaxRequired;
        private long capEntityId;

        public IMyFunctionalBlock block { get; private set; }
        public WarpSystem warpSystem { get; private set; }
        public HyperspaceSystem hyperspace => warpSystem?.hyperspace;
        public CapitalFSD capitalFSD => warpSystem?.capitalFSD;
        public bool supportsHyperspace
        {
            get
            {
                if (block?.BlockDefinition == null || isCapitalFSD) return false;
                var sub = block.BlockDefinition.SubtypeId;
                return sub.EndsWith("_A") || sub.EndsWith("_S")
                    || sub == "PrototechFSDriveLarge" || sub == "PrototechFSDriveSmall";
            }
        }
        public bool isCapitalFSD
        {
            get
            {
                if (block?.BlockDefinition == null) return false;
                return block.BlockDefinition.SubtypeId == "PrototechFSDriveLarge_9S";
            }
        }
        public bool supportsSupercruise => !isCapitalFSD;
        public bool isPrototech
        {
            get
            {
                if (block?.BlockDefinition == null) return false;
                return block.BlockDefinition.SubtypeId.StartsWith("Prototech");
            }
        }
        public bool isDuplicateFSD => warpSystem != null && warpSystem.functionalDrivesCount > 1;
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
        public static readonly List<LineD> debugDraw = new List<LineD>();
        private static readonly MyStorageData coarseRock = new MyStorageData(MyStorageDataTypeFlags.Content);
        private static readonly MyStorageData fineRock = new MyStorageData(MyStorageDataTypeFlags.Content);

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
            if (arg1 == null || Settings.instance == null || warpSystem == null || !supportsSupercruise)
                return;

            info?.AppendLine("Max Required Power: " + (WarpSystem.GetCruisePower(this, warpSystem.GetShipMass(), Settings.instance.maxSpeed) * powerMultiplier).ToString("N") + " MW");

            info?.AppendLine("Current Required Power: " + requiredPower.ToString("N") + " MW");

            if (supportsHyperspace && hyperspace != null)
            {
                info?.AppendLine("Hyperspace Capacitor: " + CapStoredMWh.ToString("N1") + " / " + hyperspace.GetCapacitorCapacityMWh().ToString("N1") + " MWh");
                info?.AppendLine("Capacitor Charge Power: " + capDrawMW.ToString("N1") + " MW");
            }

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

            UpdateCapacitor();

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
            SaveCapacitorStorage();
            if (capEntityId != 0)
                drivesById.Remove(capEntityId);

            if (!MyAPIGateway.Utilities.IsDedicated && block != null)
                block.AppendingCustomInfo -= Block_AppendingCustomInfo;

            if (warpSystem != null)
            {
                warpSystem.OnSystemInvalidatedAction -= OnSystemInvalidated;
                warpSystem.hyperspace?.Close();
                warpSystem.capitalFSD?.Close();
            }
        }

        private void InitPowerSystem()
        {
            Settings settings = Settings.GetShared();

            powerMultiplier = WarpSystem.IsPrototech(this) ? 0.5f : 1f;

            baseMaxRequired = (float)((WarpSystem.IsSmallDrive(this) ? settings.baseRequiredPowerSmall : settings.baseRequiredPower) * settings.powerRequirementMultiplier * powerMultiplier);

            sink = new MyResourceSinkComponent();
            sink.Init(MyStringHash.GetOrCompute("Utility"),
                baseMaxRequired,
                ComputeRequiredPower, (MyCubeBlock)Entity);

            Entity.Components.Add(sink);
            sink.Update();
        }

        private float ComputeRequiredPower()
        {
            if (warpSystem == null || warpSystem.warpState == WarpSystem.State.idle)
                requiredPower = 0;

            return requiredPower + capDrawMW;
        }

        private static bool IsAuthority => NetAuthority.IsAuthority;

        // ---- Hyperspace capacitor: filled from ship power while the drive is idle, spent by hyperspace jumps ----

        private void UpdateCapacitor()
        {
            if (!capLoaded)
                LoadCapacitorStorage();

            var hs = hyperspace;
            if (!supportsHyperspace || hs == null)
            {
                SetCapacitorDraw(0f);
                return;
            }

            float capacity = (float)hs.GetCapacitorCapacityMWh();

            // A power reading of 0 (a blip, a grid split, supercruise cutting power for a moment) gives a capacity of 0.
            // Only trust the capacity for clamping when it is valid and nothing is in progress, so stored energy is never wiped by it
            if (capacity > 0f && CapStoredMWh > capacity && hs.State == HyperspaceSystem.HyperState.Idle
                && warpSystem != null && warpSystem.warpState == WarpSystem.State.idle)
                CapStoredMWh = capacity;

            bool canCharge = block.IsFunctional && block.Enabled && hs.State == HyperspaceSystem.HyperState.Idle
                && warpSystem != null && warpSystem.warpState == WarpSystem.State.idle && CapStoredMWh < capacity - 0.0001f;

            SetCapacitorDraw(canCharge ? (float)hs.GetCapacitorChargePowerMW(capacity) : 0f);

            if (IsAuthority)
            {
                if (canCharge)
                {
                    float inputMW = sink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId);
                    float dtHours = 10f / 60f / 3600f;
                    CapStoredMWh = Math.Min(capacity, CapStoredMWh + Math.Min(inputMW, capDrawMW) * dtHours);
                }

                SaveCapacitorStorage();
            }

            capSyncTicks += 10;
            if (capSyncTicks >= 60)
            {
                capSyncTicks = 0;
                SyncCapacitor(false);
            }
        }

        private void SetCapacitorDraw(float mw)
        {
            if (Math.Abs(mw - capDrawMW) < 0.01f)
                return;

            capDrawMW = mw;
            sink.SetMaxRequiredInputByType(MyResourceDistributorComponent.ElectricityId, Math.Max(baseMaxRequired, capDrawMW + requiredPower));
            sink.Update();
        }

        /// <summary>Spends stored energy on a jump (server / single player only).</summary>
        public bool TryDrainCapacitor(float mwh)
        {
            if (!IsAuthority)
                return true;
            if (mwh <= 0f)
                return true;
            if (CapStoredMWh + 0.0001f < mwh)
                return false;

            CapStoredMWh = Math.Max(0f, CapStoredMWh - mwh);
            SaveCapacitorStorage();
            SyncCapacitor(true);
            return true;
        }

        private void SyncCapacitor(bool force)
        {
            if (!MyAPIGateway.Multiplayer.MultiplayerActive || !MyAPIGateway.Multiplayer.IsServer || !supportsHyperspace)
                return;
            if (!force && Math.Abs(CapStoredMWh - capLastSynced) < 0.0005f)
                return;

            capLastSynced = CapStoredMWh;
            var msg = new FsdCapSyncMessage { entityId = block.EntityId, storedMWh = CapStoredMWh };
            MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fsdCapSyncPacket, MyAPIGateway.Utilities.SerializeToBinary(msg));
        }

        public void ApplyCapacitorSync(FsdCapSyncMessage msg)
        {
            if (msg != null)
                CapStoredMWh = Math.Max(0f, msg.storedMWh);
        }

        // Loaded on the first update, once the block is in the scene, so existing storage is never replaced by an empty one
        private void LoadCapacitorStorage()
        {
            capLoaded = true;
            capEntityId = block.EntityId;
            drivesById[capEntityId] = this;

            try
            {
                string val;
                float stored;
                if (Entity.Storage != null && Entity.Storage.TryGetValue(CAP_STORAGE_GUID, out val) && !string.IsNullOrEmpty(val)
                    && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out stored))
                    CapStoredMWh = Math.Max(0f, stored);

                capLastSaved = CapStoredMWh;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[WarpDrive] Error loading capacitor storage: {ex.Message}");
            }
        }

        // Written whenever the value changes so a world save always sees the current charge
        private void SaveCapacitorStorage()
        {
            if (!capLoaded || !supportsHyperspace || Math.Abs(CapStoredMWh - capLastSaved) < 0.00001f)
                return;

            try
            {
                if (Entity.Storage == null)
                    Entity.Storage = new MyModStorageComponent();

                Entity.Storage.SetValue(CAP_STORAGE_GUID, CapStoredMWh.ToString("R", CultureInfo.InvariantCulture));
                capLastSaved = CapStoredMWh;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"[WarpDrive] Error saving capacitor storage: {ex.Message}");
            }
        }

        public static IMyEntity ProximityDanger(MatrixD gridMatrix, GridSystem gridSystem, double gridSpeed, bool spooling = false)
        {
            IMyCubeGrid mainGrid = gridSystem?.mainGrid;
            if (mainGrid == null || (spooling && mainGrid.Physics == null) || !WarpDriveSession.instance.debugProx)
                return null;

            MatrixD toSeat = MatrixD.Invert(gridMatrix);
            BoundingBoxD shipBox = BoundingBoxD.CreateInvalid();
            foreach (IMyCubeGrid connectedGrid in gridSystem.gridGroup)
            {
                BoundingBox gridBox = connectedGrid.LocalAABB;
                shipBox = BoundingBoxD.CreateMerged(shipBox, new BoundingBoxD(gridBox.Min, gridBox.Max).TransformFast(connectedGrid.WorldMatrix * toSeat));
            }

            Vector3D center = Vector3D.Transform(shipBox.Center, gridMatrix);
            Vector3D forward = gridMatrix.Forward;
            double pathWidth = shipBox.HalfExtents.X * 1.2;
            double pathHeight = shipBox.HalfExtents.Y * 1.2;
            LineD path = new LineD(center - forward * shipBox.HalfExtents.Z, center + forward * (shipBox.HalfExtents.Z + Math.Max(500.0, gridSpeed)));
            var pathBox = new MyOrientedBoundingBoxD((path.From + path.To) / 2, new Vector3D(pathWidth, pathHeight, path.Length / 2), Quaternion.CreateFromRotationMatrix(gridMatrix));

            if (WarpDriveSession.instance.debugDraw)
            {
                var boxCorners = new Vector3D[8];
                for (int i = 0; i < 8; i++)
                    boxCorners[i] = pathBox.Center + gridMatrix.Right * ((i & 1) == 0 ? -pathWidth : pathWidth) + gridMatrix.Up * ((i & 2) == 0 ? -pathHeight : pathHeight) + forward * ((i & 4) == 0 ? -path.Length / 2 : path.Length / 2);

                for (int i = 0; i < 8; i++)
                {
                    for (int bit = 1; bit < 8; bit <<= 1)
                    {
                        if ((i & bit) == 0)
                            debugDraw.Add(new LineD(boxCorners[i], boxCorners[i | bit]));
                    }
                }
            }

            var sphere = new BoundingSphereD(pathBox.Center, pathBox.HalfExtent.Length());
            List<IMyEntity> entList = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);

            if (entList == null || entList.Count == 0)
                return null;

            var attachedList = new List<IMyCubeGrid>();

            //gets subgrids and locked landing gear to ignore
            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Physical, attachedList);
            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Mechanical, attachedList);
            MyAPIGateway.GridGroups.GetGroup(mainGrid, GridLinkTypeEnum.Logical, attachedList);

            foreach (var ent in entList)
            {
                var character = ent as IMyCharacter;
                var zone = ent as MySafeZone;
                if (character != null)
                {
                    if (spooling || character.IsDead)
                        continue;
                }
                else if (zone == null || Settings.instance.AllowInSafeZone || !zone.Enabled)
                {
                    if (!(ent is MyCubeGrid || ent is MyVoxelBase))
                        continue;

                    //asteroids ignored over 20km/s
                    if (!spooling && ent is MyVoxelMap && gridSpeed >= 333.333)
                        continue;

                    var candidateGrid = ent as IMyCubeGrid;
                    if (candidateGrid != null)
                    {
                        if (attachedList.Contains(candidateGrid) || WarpDrive.IsGridIgnored(candidateGrid, mainGrid, 0L))
                            continue;

                        if (gridSystem != null && gridSystem.gridGroup != null)
                        {
                            bool isMember = false;
                            foreach (var g in gridSystem.gridGroup)
                            {
                                if (g != null && g.EntityId == candidateGrid.EntityId)
                                {
                                    isMember = true;
                                    break;
                                }
                            }
                            if (isMember)
                                continue;
                        }
                    }
                }
                else if (zone.Shape == MySafeZoneShape.Sphere)
                {
                    var zoneSphere = new BoundingSphereD(ent.GetPosition(), zone.Radius);
                    if (pathBox.Intersects(ref zoneSphere))
                        return ent;

                    continue;
                }

                var planet = ent as MyPlanet ?? (ent as MyVoxelBase)?.RootVoxel as MyPlanet;
                if (planet != null)
                {
                    BoundingBoxD planetBox = new BoundingBoxD(
                        Vector3D.Min(path.From, path.To) - new Vector3D(Math.Max(pathWidth, pathHeight)),
                        Vector3D.Max(path.From, path.To) + new Vector3D(Math.Max(pathWidth, pathHeight))
                    );

                    if (planet.PositionComp.WorldAABB.Intersects(planetBox) && planet.GetIntersectionWithAABB(ref planetBox))
                        return ent;

                    continue;
                }

                var obstacleBox = new MyOrientedBoundingBoxD(new BoundingBoxD(ent.LocalAABB.Min, ent.LocalAABB.Max), ent.WorldMatrix);

                if (!pathBox.Intersects(ref obstacleBox))
                    continue;

                if (!(ent is MyVoxelMap))
                    return ent;

                if (VoxelPathBlocked((IMyVoxelBase)ent, ref pathBox, path, gridMatrix, pathWidth, pathHeight))
                    return ent;
            }

            return null;
        }

        /// <summary>
        /// True when rock of the asteroid lies inside the oriented path box. Reads the voxel storage at a coarse level of detail first
        /// and only refines the cells that are partly filled, so it costs a few storage reads instead of a mesh raycast per ray.
        /// </summary>
        public static bool VoxelPathBlocked(IMyVoxelBase asteroid, ref MyOrientedBoundingBoxD pathBox, LineD path, MatrixD gridMatrix, double pathWidth, double pathHeight)
        {
            Vector3D forward = gridMatrix.Forward;
            double widestSide = Math.Max(pathWidth, pathHeight);
            int coarseLod = Math.Max(3, Math.Min(5, (int)Math.Ceiling(Math.Log(widestSide / 4, 2))));
            int fineLod = coarseLod - 3;
            double coarseSize = 1 << coarseLod;
            double fineSize = 1 << fineLod;
            Vector3D storageOrigin = asteroid.PositionLeftBottomCorner;
            Vector3I coarseLimit = (asteroid.Storage.Size >> coarseLod) - 1;
            int segments = (int)Math.Ceiling(path.Length / (2 * widestSide));
            double segmentLength = path.Length / segments;
            Vector3D segmentExtent = Vector3D.Abs(gridMatrix.Right * pathWidth) + Vector3D.Abs(gridMatrix.Up * pathHeight) + Vector3D.Abs(forward * (segmentLength / 2));

            for (int segment = 0; segment < segments; segment++)
            {
                Vector3D segmentCenter = path.From + forward * (segmentLength * (segment + 0.5));
                Vector3I coarseMin = Vector3I.Max(Vector3I.Floor((segmentCenter - segmentExtent - storageOrigin) / coarseSize), Vector3I.Zero);
                Vector3I coarseMax = Vector3I.Min(Vector3I.Floor((segmentCenter + segmentExtent - storageOrigin) / coarseSize), coarseLimit);
                if (coarseMin.X > coarseMax.X || coarseMin.Y > coarseMax.Y || coarseMin.Z > coarseMax.Z)
                    continue;

                coarseRock.Resize(coarseMin, coarseMax);
                asteroid.Storage.ReadRange(coarseRock, MyStorageDataTypeFlags.Content, coarseLod, coarseMin, coarseMax);

                for (int z = 0; z <= coarseMax.Z - coarseMin.Z; z++)
                {
                    for (int y = 0; y <= coarseMax.Y - coarseMin.Y; y++)
                    {
                        for (int x = 0; x <= coarseMax.X - coarseMin.X; x++)
                        {
                            byte content = coarseRock.Content(x, y, z);
                            if (content == 0)
                                continue;

                            Vector3I coarseCell = coarseMin + new Vector3I(x, y, z);
                            Vector3D cellMin = storageOrigin + coarseCell * coarseSize;
                            var cellBox = new BoundingBoxD(cellMin, cellMin + coarseSize);
                            if (!pathBox.Intersects(ref cellBox))
                                continue;

                            if (content == byte.MaxValue)
                                return true;

                            Vector3I fineMin = coarseCell * 8;
                            Vector3I fineMax = fineMin + 7;
                            fineRock.Resize(fineMin, fineMax);
                            asteroid.Storage.ReadRange(fineRock, MyStorageDataTypeFlags.Content, fineLod, fineMin, fineMax);

                            for (int fz = 0; fz < 8; fz++)
                            {
                                for (int fy = 0; fy < 8; fy++)
                                {
                                    for (int fx = 0; fx < 8; fx++)
                                    {
                                        if (fineRock.Content(fx, fy, fz) <= 127)
                                            continue;

                                        Vector3D rockPoint = storageOrigin + (fineMin + new Vector3I(fx, fy, fz)) * fineSize + fineSize / 2;
                                        if (pathBox.Contains(ref rockPoint))
                                            return true;
                                    }
                                }
                            }
                        }
                    }
                }
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

        public static bool IsGridIgnored(IMyCubeGrid candidateGrid, IMyCubeGrid myGrid, long myFleetLeader = 0L)
        {
            if (candidateGrid == null || candidateGrid.MarkedForClose)
                return true;

            // Fleet whitelist: Ignore Leader, Wingmen, and all their subgrids.
            // Dictionary lookups only, so it goes first: every ship of a fleet scans its wingmen and the checks below are far costlier
            if (myFleetLeader != 0L)
            {
                if (candidateGrid.EntityId == myFleetLeader || FleetJumpSystem.GetLeaderIdForGrid(candidateGrid.EntityId) == myFleetLeader || FleetJumpSystem.IsSameFleet(myGrid?.EntityId ?? 0L, candidateGrid.EntityId, myFleetLeader))
                    return true;

                var topFleetCandidate = candidateGrid.GetTopMostParent();
                if (topFleetCandidate != null && (topFleetCandidate.EntityId == myFleetLeader || FleetJumpSystem.GetLeaderIdForGrid(topFleetCandidate.EntityId) == myFleetLeader || FleetJumpSystem.IsSameFleet(myGrid?.EntityId ?? 0L, topFleetCandidate.EntityId, myFleetLeader)))
                    return true;
            }

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
