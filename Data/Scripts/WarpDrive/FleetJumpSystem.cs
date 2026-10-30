using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using ProtoBuf;

namespace WarpDriveMod
{
    [ProtoContract]
    public enum FleetActionType
    {
        [ProtoEnum] CreateLobby,
        [ProtoEnum] Invite,
        [ProtoEnum] Join,
        [ProtoEnum] Leave,
        [ProtoEnum] ExecuteJump,
        [ProtoEnum] CancelLobby,
        [ProtoEnum] SyncLobbyState,
        [ProtoEnum] UpdateTarget        // leader changed the destination or the blind-jump range
    }

    [ProtoContract]
    public class FleetJumpMessage
    {
        [ProtoMember(1)]
        public FleetActionType Action { get; set; }

        [ProtoMember(2)]
        public long LeaderGridId { get; set; }

        [ProtoMember(3)]
        public long MemberGridId { get; set; }

        [ProtoMember(4)]
        public long SendingPlayerId { get; set; }

        [ProtoMember(5)]
        public int Mode { get; set; }

        [ProtoMember(6)]
        public float JumpDistanceRatio { get; set; }

        [ProtoMember(7)]
        public bool HasGpsCoords { get; set; }

        [ProtoMember(8)]
        public Vector3D GpsCoords { get; set; }

        [ProtoMember(9)]
        public string GpsName { get; set; }

        [ProtoMember(10)]
        public int StaggerTicks { get; set; }

        [ProtoMember(11)]
        public double LeaderJumpDistance { get; set; }
    }

    public class FleetMemberInfo
    {
        public long GridEntityId;
        public long PlayerId;
        public string GridName;
        public Vector3D RelativeOffset; // Spatial offset relative to leader's departure orientation
        public int StaggerDelayTicks;
    }

    public class FleetJumpLobby
    {
        public long LeaderGridId;
        public long LeaderPlayerId;
        public long LeaderFactionId;
        public string LeaderGridName;
        public Vector3D DepartureOrigin;
        public MatrixD DepartureOrientation;
        public HyperspaceSystem.JumpMode Mode;
        public float JumpDistanceRatio;
        public Vector3D? TargetGpsCoords;
        public string TargetGpsName;
        public double LeaderJumpDistance;       // the fleet's distance: the leader's, after it cleared the way of obstacles. Wingmen fly exactly this
        public double FormationRadius;          // how far the wingmen are from the leader when the fleet engages; the leader clears this much extra lane width
        public List<FleetMemberInfo> Members = new List<FleetMemberInfo>();
        public HashSet<long> InTransitGridIds = new HashSet<long>();
        public bool IsExecuting = false;
    }

    public static class FleetJumpSystem
    {
        public const double FLEET_SCAN_RADIUS = 5000.0; // 5 km
        public const int FLEET_STAGGER_MIN_TICKS = 15; // 0.25s minimum delay step between adjacent ships
        public const int FLEET_STAGGER_MAX_TICKS = 600; // 10.0s maximum total cascade window

        // Generates dynamic, cinematic stagger delays:
        // - 2 ships: ~0.75s (45 ticks) distinct gap between Leader and Wingman
        // - 3-5 ships: ~0.6s - 0.7s per ship cascade
        // - 6-10 ships: ~0.4s - 0.5s per ship cascade
        // - 11-20+ ships: ~0.25s - 0.35s per ship cascade (never drops below 15 ticks / 0.25s)
        public static int GetDynamicFleetStaggerTicks(int memberIndex, int totalMembers, long gridEntityId = 0L)
        {
            if (memberIndex <= 0) return 0; // Leader always jumps at t = 0

            int idx = memberIndex; // 1-based index for wings (Wing 1 = 1, Wing 2 = 2, ...)

            // Calculate base step interval per ship in ticks
            // For 2 ships: 45 ticks (~0.75s)
            // Scaling smoothly down to 15 ticks (0.25s) as fleet size grows to 20+
            int stepTicks = MathHelper.Clamp((int)Math.Round(45.0 - ((double)(totalMembers - 2) * 1.65)), FLEET_STAGGER_MIN_TICKS, 45);

            // Deterministic organic micro-jitter (up to +/- 3 ticks) to make jumps look natural without reordering
            int jitterRange = Math.Min(3, Math.Max(1, stepTicks / 8));
            int seed = (int)((gridEntityId ^ (long)(idx * 7919)) & 0x7FFFFFFF);
            int jitter = (seed % (2 * jitterRange + 1)) - jitterRange;

            int delayTicks = (idx * stepTicks) + jitter;
            return MathHelper.Clamp(delayTicks, FLEET_STAGGER_MIN_TICKS, FLEET_STAGGER_MAX_TICKS);
        }

        private static readonly Dictionary<long, FleetJumpLobby> _activeLobbies = new Dictionary<long, FleetJumpLobby>();
        private static readonly Dictionary<long, long> _gridToLeaderMap = new Dictionary<long, long>();

        public static bool IsGridInFleet(long gridId)
        {
            return _gridToLeaderMap.ContainsKey(gridId);
        }

        public static long GetLeaderIdForGrid(long gridId)
        {
            long leaderId;
            if (_gridToLeaderMap.TryGetValue(gridId, out leaderId))
                return leaderId;
            return 0L;
        }

        public static bool IsSameFleet(long gridA, long gridB, long knownLeaderId = 0L)
        {
            if (gridA == gridB) return true;

            long leaderA = knownLeaderId;
            if (leaderA == 0L)
                _gridToLeaderMap.TryGetValue(gridA, out leaderA);

            long leaderB = 0L;
            _gridToLeaderMap.TryGetValue(gridB, out leaderB);

            if ((leaderA != 0L && gridB == leaderA) || (leaderB != 0L && gridA == leaderB)) return true;
            if (leaderA != 0L && leaderB != 0L && leaderA == leaderB) return true;

            return false;
        }

        public static FleetJumpLobby GetLobbyForGrid(long gridId)
        {
            long leaderId;
            if (_gridToLeaderMap.TryGetValue(gridId, out leaderId))
            {
                FleetJumpLobby lobby;
                if (_activeLobbies.TryGetValue(leaderId, out lobby))
                    return lobby;
            }
            return null;
        }

        public static FleetJumpLobby GetLobbyByLeader(long leaderGridId)
        {
            FleetJumpLobby lobby;
            return _activeLobbies.TryGetValue(leaderGridId, out lobby) ? lobby : null;
        }

        public static bool IsJumpLeader(long gridId)
        {
            return _activeLobbies.ContainsKey(gridId);
        }

        public static FleetJumpLobby GetNearestFriendlyLobby(IMyCubeGrid grid, long playerId)
        {
            if (grid == null) return null;
            Vector3D pos = grid.PositionComp.GetPosition();
            long memberOwnerId = (grid.BigOwners != null && grid.BigOwners.Count > 0) ? grid.BigOwners[0] : playerId;
            long factionId = GetGridFactionId(grid);

            FleetJumpLobby nearestLobby = null;
            double nearestDist = FLEET_SCAN_RADIUS;

            foreach (var kvp in _activeLobbies)
            {
                var lobby = kvp.Value;
                if (lobby == null || lobby.IsExecuting) continue;

                if (IsFriendlyFaction(lobby.LeaderFactionId, factionId, lobby.LeaderPlayerId, memberOwnerId))
                {
                    double dist = Vector3D.Distance(pos, lobby.DepartureOrigin);
                    if (dist <= nearestDist)
                    {
                        nearestDist = dist;
                        nearestLobby = lobby;
                    }
                }
            }
            return nearestLobby;
        }

        // Initiates or Manages Fleet Jump (Create, Join, Leave, or Manage Leader Dialog)
        public static void InitiateFleetJump(IMyTerminalBlock block, long playerId)
        {
            var drive = HyperspaceControls.GetWarpDrive(block);
            if (drive == null || drive.hyperspace == null)
            {
                HudEvents.PostLocal("No Frame Shift Drive found on ship!", 3000, "Red");
                return;
            }

            if (!drive.supportsHyperspace)
            {
                HudEvents.PostLocal("This FSD does not support Hyperspace jumps! (Supercruise Only)", 3000, "Red");
                return;
            }

            var hs = drive.hyperspace;
            var mainGrid = hs?.gridSystem?.mainGrid;
            if (mainGrid == null) return;

            // 1. If already in fleet:
            if (IsGridInFleet(mainGrid.EntityId))
            {
                var existingLobby = GetLobbyForGrid(mainGrid.EntityId);
                if (existingLobby != null && existingLobby.IsExecuting)
                {
                    // The previous fleet jump was executed and this ship has already arrived.
                    // Cleanly unregister this ship so it can immediately create or join a new fleet jump.
                    UnregisterFleetGrid(mainGrid.EntityId);
                }
                else if (existingLobby != null && !MyAPIGateway.Utilities.IsDedicated && FsdHud.IsReady)
                {
                    // In a fleet: the button opens / closes the lobby menu (destination, members, engage, disband or leave)
                    FsdHud.ToggleFleetMenu(existingLobby, mainGrid.EntityId);
                    return;
                }
                else if (IsJumpLeader(mainGrid.EntityId))
                {
                    if (existingLobby != null && !existingLobby.IsExecuting && !MyAPIGateway.Utilities.IsDedicated)
                        TryEngageFleetJump(existingLobby);
                    return;
                }
                else
                {
                    // Member -> Leave / Quit fleet jump
                    CancelFleetJump(mainGrid.EntityId, playerId);
                    HudEvents.PostLocal("Left Fleet Jump formation.", 3000, "White");
                    return;
                }
            }

            // 3. Otherwise -> Create new lobby (become Leader)
            // 0. Supercruise conflict check
            if (drive.warpSystem != null && drive.warpSystem.warpState != WarpSystem.State.idle)
            {
                HudEvents.PostLocal("Another seat is piloting this grid", 3000, "Red");
                return;
            }

            // Safety & Power pre-check
            // The fleet aligns while holding, so creating the lobby does not require the leader to be aligned yet
            if (!hs.CanInitiateJump(playerId, false)) return;

            long leaderOwnerId = (mainGrid.BigOwners != null && mainGrid.BigOwners.Count > 0) ? mainGrid.BigOwners[0] : playerId;
            long factionId = GetGridFactionId(mainGrid);

            // Enforce sector rule: If a friendly fleet jump lobby is active within 5 km, join it instead of erroring!
            Vector3D originPos = mainGrid.PositionComp.GetPosition();
            foreach (var kvp in _activeLobbies)
            {
                var existing = kvp.Value;
                if (existing == null || existing.IsExecuting) continue;

                if (IsFriendlyFaction(existing.LeaderFactionId, factionId, existing.LeaderPlayerId, leaderOwnerId))
                {
                    double dist = Vector3D.Distance(existing.DepartureOrigin, originPos);
                    if (dist <= FLEET_SCAN_RADIUS)
                    {
                        JoinFleetJump(mainGrid, playerId);
                        return;
                    }
                }
            }

            float leaderPower = hs.GetDrivePowerMW();
            double leaderMaxRange = hs.GetMaxJumpDistance(leaderPower);
            double leaderPlannedDistance = (hs.Mode == HyperspaceSystem.JumpMode.GpsWaypoint && hs.SelectedGpsCoords.HasValue)
                ? Vector3D.Distance(originPos, hs.SelectedGpsCoords.Value)
                : hs.GetCurrentManualDistance(leaderPower);

            // Create Lobby
            var lobby = new FleetJumpLobby
            {
                LeaderGridId = mainGrid.EntityId,
                LeaderPlayerId = playerId,
                LeaderFactionId = factionId,
                LeaderGridName = mainGrid.DisplayName ?? "Leader Ship",
                DepartureOrigin = originPos,
                DepartureOrientation = mainGrid.WorldMatrix.GetOrientation(),
                Mode = hs.Mode,
                JumpDistanceRatio = hs.JumpDistanceRatio,
                TargetGpsCoords = hs.SelectedGpsCoords,
                TargetGpsName = hs.SelectedGpsName,
                LeaderJumpDistance = leaderPlannedDistance,
                IsExecuting = false
            };

            // Add leader as member #0 (0 tick stagger)
            lobby.Members.Add(new FleetMemberInfo
            {
                GridEntityId = mainGrid.EntityId,
                PlayerId = playerId,
                GridName = lobby.LeaderGridName,
                RelativeOffset = Vector3D.Zero,
                StaggerDelayTicks = 0
            });

            _activeLobbies[mainGrid.EntityId] = lobby;
            _gridToLeaderMap[mainGrid.EntityId] = mainGrid.EntityId;
            hs.FleetLeaderGridId = mainGrid.EntityId;

            // Put leader drive into Fleet Charging / Holding Loop
            hs.EnterFleetChargingLoop(playerId);

            // Broadcast message / network sync
            var msg = new FleetJumpMessage
            {
                Action = FleetActionType.CreateLobby,
                LeaderGridId = mainGrid.EntityId,
                MemberGridId = mainGrid.EntityId,
                SendingPlayerId = playerId,
                Mode = (int)hs.Mode,
                JumpDistanceRatio = hs.JumpDistanceRatio,
                HasGpsCoords = hs.SelectedGpsCoords.HasValue,
                GpsCoords = hs.SelectedGpsCoords ?? Vector3D.Zero,
                GpsName = hs.SelectedGpsName ?? string.Empty,
                LeaderJumpDistance = leaderPlannedDistance
            };
            SendFleetMessage(msg);

            // Perform single scan for friendly ships within 5km and send invitations
            ScanAndInviteFriendlyShips(lobby);

            // The lobby panel appears on the leader's HUD; engaging is a second press of the Fleet Jump button
            if (!MyAPIGateway.Utilities.IsDedicated && playerId == (MyAPIGateway.Session?.Player?.IdentityId ?? -1))
            {
                if (FsdHud.IsReady)
                    FsdHud.ToggleFleetMenu(lobby, mainGrid.EntityId);
                else
                    HudEvents.PostLocal("FLEET LOBBY OPEN - PRESS FLEET JUMP AGAIN TO ENGAGE", 6000, "White");
            }
        }

        public static void InitiateFleetJoin(IMyTerminalBlock block, long playerId)
        {
            var drive = HyperspaceControls.GetWarpDrive(block);
            if (drive == null || drive.hyperspace == null)
            {
                HudEvents.PostLocal("No Frame Shift Drive found on ship!", 3000, "Red");
                return;
            }

            if (!drive.supportsHyperspace)
            {
                HudEvents.PostLocal("This FSD does not support Hyperspace jumps!", 3000, "Red");
                return;
            }

            var hs = drive.hyperspace;
            var mainGrid = hs?.gridSystem?.mainGrid;
            if (mainGrid == null) return;

            if (IsGridInFleet(mainGrid.EntityId))
            {
                HudEvents.PostLocal("Already in a Fleet Jump formation.", 3000, "Red");
                return;
            }

            var nearbyLobby = GetNearestFriendlyLobby(mainGrid, playerId);
            if (nearbyLobby != null)
            {
                JoinFleetJump(mainGrid, playerId);
            }
            else
            {
                HudEvents.PostLocal("No friendly Fleet Jump lobby found nearby.", 3000, "Red");
            }
        }

        // Calculates real-time alignment offset angle in degrees for any fleet member
        public static double GetMemberAlignmentOffset(long gridId, FleetJumpLobby lobby)
        {
            if (lobby == null) return 0.0;
            var grid = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (grid == null) return 999.0;

            IMyShipController cockpit = null;
            foreach (var fat in grid.GetFatBlocks())
            {
                var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                if (drive?.hyperspace?.GridSystem != null)
                {
                    cockpit = drive.hyperspace.GridSystem.FindMainCockpit();
                    if (cockpit != null) break;
                }
            }

            if (cockpit == null)
            {
                IMyRemoteControl rc = null;
                foreach (var fat in grid.GetFatBlocks())
                {
                    var c = fat as IMyCockpit;
                    if (c != null && c.IsFunctional)
                    {
                        if (c.Pilot != null) { cockpit = c; break; }
                        if (cockpit == null) cockpit = c;
                    }
                    var r = fat as IMyRemoteControl;
                    if (r != null && r.IsFunctional && rc == null) rc = r;
                }
                if (cockpit == null && rc != null) cockpit = rc;
            }

            MatrixD memberMatrix = (cockpit != null) ? cockpit.WorldMatrix : grid.WorldMatrix;
            Vector3D targetDir;
            if (lobby.Mode == HyperspaceSystem.JumpMode.GpsWaypoint && lobby.TargetGpsCoords.HasValue)
            {
                targetDir = Vector3D.Normalize(lobby.TargetGpsCoords.Value - memberMatrix.Translation);
            }
            else
            {
                var leaderGrid = MyEntities.GetEntityById(lobby.LeaderGridId) as MyCubeGrid;
                Vector3D leaderDir = Vector3D.Zero;
                if (leaderGrid != null && !leaderGrid.MarkedForClose)
                {
                    lobby.DepartureOrientation = leaderGrid.WorldMatrix.GetOrientation();
                    lobby.DepartureOrigin = leaderGrid.PositionComp.GetPosition();
                    
                    foreach (var fat in leaderGrid.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive?.hyperspace != null)
                        {
                            if (drive.hyperspace.JumpDirection.LengthSquared() > 0.001)
                                leaderDir = drive.hyperspace.JumpDirection;
                            break;
                        }
                    }
                }
                targetDir = leaderDir.LengthSquared() > 0.001 ? leaderDir : lobby.DepartureOrientation.Forward;
            }

            double dot = MathHelper.Clamp(Vector3D.Dot(memberMatrix.Forward, targetDir), -1.0, 1.0);
            return MathHelper.ToDegrees(Math.Acos(dot));
        }

        // Single scan at lobby creation to invite friendly ships
        private static void ScanAndInviteFriendlyShips(FleetJumpLobby lobby)
        {
            BoundingSphereD sphere = new BoundingSphereD(lobby.DepartureOrigin, FLEET_SCAN_RADIUS);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            if (entities == null || entities.Count == 0) return;

            long leaderPlayerId = lobby.LeaderPlayerId;

            foreach (var ent in entities)
            {
                var grid = ent as MyCubeGrid;
                if (grid == null || grid.EntityId == lobby.LeaderGridId || grid.MarkedForClose) continue;

                // Check friendly relation
                if (!IsFriendlyGrid(grid, leaderPlayerId, lobby.LeaderFactionId)) continue;

                // Check if grid has an operational FSD that supports Hyperspace
                WarpDrive drive = null;
                foreach (var fat in grid.GetFatBlocks())
                {
                    drive = fat?.GameLogic?.GetAs<WarpDrive>();
                    if (drive != null && drive.block != null && drive.block.IsFunctional && drive.supportsHyperspace)
                        break;
                    drive = null;
                }

                if (drive != null && drive.hyperspace != null)
                {
                    // If ship is on FSD cooldown, don't invite or auto-join until cooled down
                    if (drive.hyperspace.State == HyperspaceSystem.HyperState.Cooldown)
                        continue;

                    // Check if grid has a seated pilot or an unmanned working remote control
                    bool hasSeatedPilot = false;
                    bool hasWorkingRemoteControl = false;
                    bool hasActiveRemotePilot = false;

                    foreach (var fat in grid.GetFatBlocks())
                    {
                        var cockpit = fat as IMyCockpit;
                        if (cockpit?.Pilot != null)
                            hasSeatedPilot = true;

                        var rc = fat as IMyRemoteControl;
                        if (rc != null && rc.IsFunctional)
                        {
                            hasWorkingRemoteControl = true;
                            if (rc.IsUnderControl)
                                hasActiveRemotePilot = true;
                        }
                    }

                    if (hasSeatedPilot || hasActiveRemotePilot)
                    {
                        // Nothing to do here: every machine receives the lobby message and shows the invitation to its own pilot (NotifyLocalPlayerOfInvite)
                    }
                    else if (hasWorkingRemoteControl)
                    {
                        // Unmanned drone with Remote Control: Auto-join friendly fleet!
                        JoinFleetJump(grid, leaderPlayerId);
                    }
                }
            }
        }

        /// <summary>
        /// Leader changes the fleet's destination (GPS or blind with a range ratio). Applied here and sent to every machine;
        /// ships keep their charge and simply re-aim.
        /// </summary>
        public static void SetFleetTarget(FleetJumpLobby lobby, HyperspaceSystem.JumpMode mode, Vector3D? gpsCoords, string gpsName, float rangeRatio, long playerId)
        {
            if (lobby == null || lobby.IsExecuting)
                return;

            rangeRatio = MathHelper.Clamp(rangeRatio, 0f, 1f);
            var leaderHs = FindHyperspace(lobby.LeaderGridId);
            var leaderGrid = MyEntities.GetEntityById(lobby.LeaderGridId) as MyCubeGrid;
            double distance = lobby.LeaderJumpDistance;
            if (leaderHs != null && leaderGrid != null)
            {
                if (mode == HyperspaceSystem.JumpMode.GpsWaypoint && gpsCoords.HasValue)
                {
                    distance = Vector3D.Distance(leaderGrid.PositionComp.GetPosition(), gpsCoords.Value);
                }
                else
                {
                    leaderHs.JumpDistanceRatio = rangeRatio;
                    distance = leaderHs.GetCurrentManualDistance(leaderHs.GetDrivePowerMW());
                }
            }

            ApplyFleetTarget(lobby, mode, gpsCoords, gpsName, rangeRatio, distance);

            SendFleetMessage(new FleetJumpMessage
            {
                Action = FleetActionType.UpdateTarget,
                LeaderGridId = lobby.LeaderGridId,
                SendingPlayerId = playerId,
                Mode = (int)mode,
                JumpDistanceRatio = rangeRatio,
                HasGpsCoords = gpsCoords.HasValue,
                GpsCoords = gpsCoords ?? Vector3D.Zero,
                GpsName = gpsName ?? string.Empty,
                LeaderJumpDistance = distance
            });
        }

        private static void ApplyFleetTarget(FleetJumpLobby lobby, HyperspaceSystem.JumpMode mode, Vector3D? gpsCoords, string gpsName, float rangeRatio, double leaderDistance)
        {
            bool gps = mode == HyperspaceSystem.JumpMode.GpsWaypoint && gpsCoords.HasValue;
            lobby.Mode = gps ? HyperspaceSystem.JumpMode.GpsWaypoint : HyperspaceSystem.JumpMode.ManualDistance;
            lobby.TargetGpsCoords = gps ? gpsCoords : null;
            lobby.TargetGpsName = gps ? gpsName : string.Empty;
            lobby.JumpDistanceRatio = rangeRatio;
            lobby.LeaderJumpDistance = leaderDistance;

            foreach (var member in lobby.Members)
            {
                var hs = FindHyperspace(member.GridEntityId);
                if (hs == null)
                    continue;

                hs.Mode = lobby.Mode;
                hs.JumpDistanceRatio = rangeRatio;
                hs.SelectedGpsCoords = lobby.TargetGpsCoords;
                hs.SelectedGpsName = lobby.TargetGpsName;
                hs.RetargetFleetHold();
            }
        }

        /// <summary>The hyperspace system of a fleet ship (null if it has none or is not loaded).</summary>
        public static HyperspaceSystem FindHyperspace(long gridId)
        {
            var grid = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (grid == null)
                return null;

            foreach (var fat in grid.GetFatBlocks())
            {
                var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                if (drive?.hyperspace != null)
                    return drive.hyperspace;
            }
            return null;
        }

        /// <summary>Leader pressed Fleet Jump while the lobby is open: engage if every ship is cooled down and aligned.</summary>
        public static bool TryEngageFleetJump(FleetJumpLobby lobby)
        {
            if (lobby == null || lobby.IsExecuting)
                return false;

            for (int i = 0; i < lobby.Members.Count; i++)
            {
                var m = lobby.Members[i];
                var memberHs = FindHyperspace(m.GridEntityId);
                if (memberHs != null && memberHs.State == HyperspaceSystem.HyperState.Cooldown)
                {
                    HudEvents.PostLocal($"Cannot jump: {m.GridName}'s FSD is still cooling down!", 4000, "Red");
                    return false;
                }

                double offset = GetMemberAlignmentOffset(m.GridEntityId, lobby);
                if (offset > HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG)
                {
                    // An unmanned drone that isn't aligned is left behind when the fleet engages; only the leader and crewed ships hold it up
                    bool leftBehind = i > 0 && memberHs != null && !memberHs.HasHumanPilot();
                    if (!leftBehind)
                    {
                        HudEvents.PostLocal($"Cannot jump: {m.GridName} is not aligned (Offset: {offset:F1}°, Max {HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG:F1}°)", 4000, "Red");
                        return false;
                    }
                }
            }

            ConfirmAndExecuteFleetJump(lobby.LeaderGridId, lobby.LeaderPlayerId);
            return true;
        }

        // Joins a friendly ship into an existing fleet jump
        public static void JoinFleetJump(IMyCubeGrid memberGrid, long playerId)
        {
            if (memberGrid == null) return;
            if (IsGridInFleet(memberGrid.EntityId)) return;

            // Find nearest active friendly lobby within the fleet sector (5 km)
            FleetJumpLobby targetLobby = null;
            double nearestDist = FLEET_SCAN_RADIUS;

            Vector3D memberPos = memberGrid.PositionComp.GetPosition();
            long memberOwnerId = (memberGrid.BigOwners != null && memberGrid.BigOwners.Count > 0) ? memberGrid.BigOwners[0] : playerId;
            long memberFactionId = GetGridFactionId(memberGrid);

            foreach (var kvp in _activeLobbies)
            {
                var lobby = kvp.Value;
                if (lobby.IsExecuting) continue;

                if (IsFriendlyFaction(lobby.LeaderFactionId, memberFactionId, lobby.LeaderPlayerId, memberOwnerId))
                {
                    double dist = Vector3D.Distance(memberPos, lobby.DepartureOrigin);
                    if (dist <= nearestDist)
                    {
                        nearestDist = dist;
                        targetLobby = lobby;
                    }
                }
            }

            if (targetLobby == null)
            {
                HudEvents.PostLocal("No active Fleet Jump invitation within 5 km.", 3000, "Red");
                return;
            }

            // Find member's warp drive
            var warpDrive = HyperspaceControls.GetWarpDrive(memberGrid as IMyTerminalBlock);
            if (warpDrive == null)
            {
                foreach (var fat in (memberGrid as MyCubeGrid).GetFatBlocks())
                {
                    warpDrive = fat?.GameLogic?.GetAs<WarpDrive>();
                    if (warpDrive != null) break;
                }
            }

            if (warpDrive == null || warpDrive.hyperspace == null)
            {
                HudEvents.PostLocal("No Frame Shift Drive on ship!", 3000, "Red");
                return;
            }

            if (!warpDrive.supportsHyperspace)
            {
                HudEvents.PostLocal("Your FSD does not support Hyperspace jumps! (Supercruise Only)", 3000, "Red");
                return;
            }

            var hs = warpDrive.hyperspace;

            if (hs.State == HyperspaceSystem.HyperState.Cooldown)
            {
                HudEvents.PostLocal("FSD is cooling down from previous jump!", 3000, "Red");
                return;
            }

            // Strict range validation: Ensure member ship's FSD has enough power to jump as far as the leader
            float powerMW = hs.GetDrivePowerMW();
            double memberMaxDist = hs.GetMaxJumpDistance(powerMW);
            double requiredDist = targetLobby.LeaderJumpDistance;

            if (memberMaxDist < requiredDist || !hs.HasEnergyForJump(requiredDist))
            {
                HudEvents.PostLocal($"INSUFFICIENT FSD RANGE! (Need: {requiredDist / 1000.0:N0} km, Max: {memberMaxDist / 1000.0:N0} km)", 4000, "Red");
                return;
            }

            // Calculate relative offset in Leader's departure orientation space
            MatrixD leaderInv = MatrixD.Invert(targetLobby.DepartureOrientation);
            Vector3D relWorld = memberPos - targetLobby.DepartureOrigin;
            Vector3D relLocal = Vector3D.TransformNormal(relWorld, leaderInv);

            // Assign dynamic stagger delay
            int staggerIndex = targetLobby.Members.Count;
            int staggerTicks = GetDynamicFleetStaggerTicks(staggerIndex, staggerIndex + 1, memberGrid.EntityId);

            var memberInfo = new FleetMemberInfo
            {
                GridEntityId = memberGrid.EntityId,
                PlayerId = playerId,
                GridName = memberGrid.DisplayName ?? $"Wing Ship {staggerIndex}",
                RelativeOffset = relLocal,
                StaggerDelayTicks = staggerTicks
            };

            targetLobby.Members.Add(memberInfo);
            _gridToLeaderMap[memberGrid.EntityId] = targetLobby.LeaderGridId;

            // Synchronize member's HyperspaceSystem settings
            hs.FleetLeaderGridId = targetLobby.LeaderGridId;
            hs.Mode = targetLobby.Mode;
            hs.JumpDistanceRatio = targetLobby.JumpDistanceRatio;
            hs.SelectedGpsCoords = targetLobby.TargetGpsCoords;
            hs.SelectedGpsName = targetLobby.TargetGpsName;

            // Put member's drive into holding loop
            hs.EnterFleetChargingLoop(playerId);

            // Send network message
            var msg = new FleetJumpMessage
            {
                Action = FleetActionType.Join,
                LeaderGridId = targetLobby.LeaderGridId,
                MemberGridId = memberGrid.EntityId,
                SendingPlayerId = playerId,
                StaggerTicks = staggerTicks
            };
            SendFleetMessage(msg);

            HudEvents.PostLocal($"Joined {targetLobby.LeaderGridName}'s Fleet Jump!", 3000, "White");
        }

        // Leaves or cancels a fleet jump
        /// <param name="reason">Shown to the removed ship instead of the default "left / removed / disbanded" text.</param>
        public static void CancelFleetJump(long gridId, long playerId, string reason = null)
        {
            long leaderId;
            if (!_gridToLeaderMap.TryGetValue(gridId, out leaderId)) return;

            FleetJumpLobby lobby;
            if (!_activeLobbies.TryGetValue(leaderId, out lobby)) return;

            if (gridId == leaderId)
            {
                // Leader cancelled -> Disband entire fleet lobby
                _activeLobbies.Remove(leaderId);

                // Notify & spool down all members
                foreach (var member in lobby.Members)
                {
                    _gridToLeaderMap.Remove(member.GridEntityId);
                    var grid = MyEntities.GetEntityById(member.GridEntityId) as MyCubeGrid;
                    if (grid != null)
                    {
                        foreach (var fat in grid.GetFatBlocks())
                        {
                            var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                            if (drive?.hyperspace != null && drive.hyperspace.IsFleetHolding)
                            {
                                drive.hyperspace.AbortJump(reason ?? "FLEET JUMP DISBANDED BY LEADER");
                            }
                        }
                    }
                }

                var msg = new FleetJumpMessage
                {
                    Action = FleetActionType.CancelLobby,
                    LeaderGridId = leaderId,
                    SendingPlayerId = playerId
                };
                SendFleetMessage(msg);
            }
            else
            {
                // Member left (or the leader removed it)
                var leaving = lobby.Members.Find(m => m.GridEntityId == gridId);
                bool removedByLeader = leaving != null && playerId != 0L && playerId != leaving.PlayerId && playerId == lobby.LeaderPlayerId;
                _gridToLeaderMap.Remove(gridId);
                lobby.Members.RemoveAll(m => m.GridEntityId == gridId);

                // Leaving an engaged fleet (an abort after the leader engaged): the ship is no longer in flight with it either,
                // so the fleet still closes once the last ship that is flying lands
                if (lobby.IsExecuting)
                {
                    lobby.InTransitGridIds.Remove(gridId);
                    CloseLobbyIfLanded(leaderId, lobby);
                }

                var grid = MyEntities.GetEntityById(gridId) as MyCubeGrid;
                if (grid != null)
                {
                    foreach (var fat in grid.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive?.hyperspace != null && drive.hyperspace.IsFleetHolding)
                        {
                            drive.hyperspace.AbortJump(reason ?? (removedByLeader ? "REMOVED FROM FLEET JUMP BY THE LEADER" : "LEFT FLEET JUMP"));
                        }
                    }
                }

                var msg = new FleetJumpMessage
                {
                    Action = FleetActionType.Leave,
                    LeaderGridId = leaderId,
                    MemberGridId = gridId,
                    SendingPlayerId = playerId
                };
                SendFleetMessage(msg);
            }
        }

        // Jump Leader confirms execution -> Triggers synchronized, staggered fleet jump
        public static void ConfirmAndExecuteFleetJump(long leaderGridId, long playerId)
        {
            FleetJumpLobby lobby;
            if (!_activeLobbies.TryGetValue(leaderGridId, out lobby) || lobby.IsExecuting) return;

            var leaderGrid = MyEntities.GetEntityById(leaderGridId) as MyCubeGrid;
            if (leaderGrid == null || leaderGrid.MarkedForClose) return;

            HyperspaceSystem leaderHs = null;
            foreach (var fat in leaderGrid.GetFatBlocks())
            {
                var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                if (drive?.hyperspace != null)
                {
                    leaderHs = drive.hyperspace;
                    break;
                }
            }

            // 1. Leader proximity obstacle check
            if (leaderHs != null && leaderHs.IsProximityDanger(leaderGrid.WorldMatrix, leaderGrid))
            {
                HudEvents.PostLocal("Cannot jump: Leader path is obstructed by a proximity hazard!", 4000, "Red");
                return;
            }

            // Leader alignment check
            double leaderOffset = GetMemberAlignmentOffset(leaderGridId, lobby);
            if (leaderOffset > HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG)
            {
                HudEvents.PostLocal($"Cannot jump: Align leader ship with target! (Offset: {leaderOffset:F1}°, Max {HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG:F1}°)", 4000, "Red");
                return;
            }

            // 2. Member status check: ensure all wingmen are cooled down and aligned (<= 5.0°)
            for (int i = lobby.Members.Count - 1; i >= 1; i--)
            {
                var m = lobby.Members[i];
                var mGrid = MyEntities.GetEntityById(m.GridEntityId) as MyCubeGrid;
                HyperspaceSystem memberHs = null;
                if (mGrid != null)
                {
                    foreach (var fat in mGrid.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive?.hyperspace != null)
                        {
                            memberHs = drive.hyperspace;
                            break;
                        }
                    }
                }

                if (memberHs != null && memberHs.State == HyperspaceSystem.HyperState.Cooldown)
                {
                    HudEvents.PostLocal($"Cannot jump: {m.GridName}'s FSD is still cooling down!", 4000, "Red");
                    return;
                }

                double offset = GetMemberAlignmentOffset(m.GridEntityId, lobby);
                if (offset > HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG)
                {
                    bool isManned = memberHs != null && memberHs.HasHumanPilot();
                    if (isManned)
                    {
                        HudEvents.PostLocal($"Cannot jump: {m.GridName} is not aligned (Offset: {offset:F1}°, Max {HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG:F1}°)", 4000, "Red");
                        return;
                    }
                    else
                    {
                        // Unmanned drone failed to align in time: leave it behind. CancelFleetJump drops it from the lobby here and,
                        // through the Leave message, on every other machine, and spools its drive down (which also releases its gyros)
                        HudEvents.PostLocal($"{m.GridName} was not aligned ({offset:F1}°) and was left behind.", 4000, "White");
                        CancelFleetJump(m.GridEntityId, playerId, "FLEET JUMP LEFT BEHIND - FAILED ALIGNMENT");
                    }
                }
            }

            lobby.IsExecuting = true;

            var msg = new FleetJumpMessage
            {
                Action = FleetActionType.ExecuteJump,
                LeaderGridId = leaderGridId,
                SendingPlayerId = playerId,
                Mode = (int)lobby.Mode,
                JumpDistanceRatio = lobby.JumpDistanceRatio,
                HasGpsCoords = lobby.TargetGpsCoords.HasValue,
                GpsCoords = lobby.TargetGpsCoords ?? Vector3D.Zero,
                GpsName = lobby.TargetGpsName ?? string.Empty,
                LeaderJumpDistance = lobby.LeaderJumpDistance
            };
            SendFleetMessage(msg);

            ExecuteFleetJumpLocally(lobby);
        }

        // Executes the synchronized jump across all members with relative formation & staggered delays
        public static void ExecuteFleetJumpLocally(FleetJumpLobby lobby)
        {
            if (lobby == null) return;

            var leaderGrid = MyEntities.GetEntityById(lobby.LeaderGridId) as MyCubeGrid;
            HyperspaceSystem leaderHs = null;
            if (leaderGrid != null)
            {
                foreach (var fat in leaderGrid.GetFatBlocks())
                {
                    var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                    if (drive?.hyperspace != null)
                    {
                        leaderHs = drive.hyperspace;
                        break;
                    }
                }
            }

            if (leaderHs == null) return;

            // Ensure Leader's departure orientation and distance are current
            lobby.DepartureOrientation = leaderGrid.WorldMatrix.GetOrientation();
            if (lobby.LeaderJumpDistance <= 0.0)
                lobby.LeaderJumpDistance = leaderHs.JumpDistance;

            // Calculate Leader's journey duration to synchronize all fleet members
            float leaderPower = leaderHs.GetDrivePowerMW();
            double leaderMaxDist = leaderHs.GetMaxJumpDistance(leaderPower);
            float leaderJourneyDuration = leaderHs.CalculateJourneyTime(lobby.LeaderJumpDistance, leaderMaxDist);

            Vector3D leaderDir = leaderHs.JumpDirection.LengthSquared() > 0.001 ? leaderHs.JumpDirection : leaderGrid.WorldMatrix.Forward;

            // Work on a copy: a wingman that aborts below leaves the lobby, and removing it from the list being walked would skip the next ship
            var members = new List<FleetMemberInfo>(lobby.Members);
            Vector3D leaderPos = leaderGrid.PositionComp.GetPosition();

            double formationRadius = 0.0;
            for (int i = 1; i < members.Count; i++)
            {
                var formationGrid = MyEntities.GetEntityById(members[i].GridEntityId) as MyCubeGrid;
                if (formationGrid != null && !formationGrid.MarkedForClose)
                    formationRadius = Math.Max(formationRadius, Vector3D.Distance(formationGrid.PositionComp.GetPosition(), leaderPos));
            }
            lobby.FormationRadius = Math.Min(formationRadius, FLEET_SCAN_RADIUS);

            // Execute Leader jump (0 delay). The leader clears the fleet's lane of obstacles, which can shorten the jump
            leaderHs.ExecuteFleetMemberJump(0, Vector3D.Zero, leaderDir, leaderJourneyDuration, lobby.LeaderGridId, lobby.DepartureOrientation, lobby.LeaderJumpDistance);
            if (leaderHs.JumpDistance > 1.0)
                lobby.LeaderJumpDistance = leaderHs.JumpDistance;

            // Execute Member jumps with formation offsets, staggered delays, and the leader's travel duration and distance
            MatrixD leaderInv = MatrixD.Invert(lobby.DepartureOrientation);

            // Populate InTransitGridIds to dynamically track every ship in flight
            lobby.InTransitGridIds.Clear();
            lobby.InTransitGridIds.Add(lobby.LeaderGridId);
            _gridToLeaderMap[lobby.LeaderGridId] = lobby.LeaderGridId;

            for (int i = 1; i < members.Count; i++)
            {
                var member = members[i];
                var memberGrid = MyEntities.GetEntityById(member.GridEntityId) as MyCubeGrid;
                if (memberGrid == null || memberGrid.MarkedForClose) continue;

                Vector3D memberPos = memberGrid.PositionComp.GetPosition();
                double distToLeader = Vector3D.Distance(memberPos, leaderPos);

                // 1. Tether check: Must remain within 5 km sector limit of leader
                if (distToLeader > FLEET_SCAN_RADIUS)
                {
                    foreach (var fat in memberGrid.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive?.hyperspace != null)
                        {
                            drive.hyperspace.AbortJump("FLEET JUMP ABORTED - EXCEEDED 5km SECTOR LIMIT");
                            break;
                        }
                    }
                    continue;
                }

                // 2. Dynamically snapshot actual formation offset at jump execution time
                Vector3D relWorld = memberPos - leaderPos;
                Vector3D relLocal = Vector3D.TransformNormal(relWorld, leaderInv);

                foreach (var fat in memberGrid.GetFatBlocks())
                {
                    var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                    if (drive?.hyperspace != null)
                    {
                        var memberHs = drive.hyperspace;
                        float mPower = memberHs.GetDrivePowerMW();
                        double mMaxDist = memberHs.GetMaxJumpDistance(mPower);

                        // Final check 1: confirm member still has power/range
                        if (mMaxDist < lobby.LeaderJumpDistance || !memberHs.HasEnergyForJump(lobby.LeaderJumpDistance))
                        {
                            memberHs.AbortJump("FLEET JUMP ABORTED - INSUFFICIENT FSD RANGE");
                            break;
                        }

                        // Final check 2: confirm member's departure path is not blocked by non-fleet obstacle
                        if (memberHs.IsProximityDanger(memberGrid.WorldMatrix, memberGrid))
                        {
                            memberHs.AbortJump("FLEET JUMP ABORTED - PROXIMITY HAZARD DETECTED");
                            break;
                        }

                        int staggerTicks = GetDynamicFleetStaggerTicks(i, members.Count, member.GridEntityId);
                        member.StaggerDelayTicks = staggerTicks;

                        lobby.InTransitGridIds.Add(member.GridEntityId);
                        _gridToLeaderMap[member.GridEntityId] = lobby.LeaderGridId;

                        memberHs.ExecuteFleetMemberJump(staggerTicks, relLocal, leaderDir, leaderJourneyDuration, lobby.LeaderGridId, lobby.DepartureOrientation, lobby.LeaderJumpDistance);
                        break;
                    }
                }
            }

            // Note: Mappings are preserved during transit so ships ignore fellow fleet members in proximity/collision checks.
            // Grids will individually unregister via UnregisterFleetGrid on arrival or abort.
        }

        // Unregisters an individual grid upon arrival or abort
        public static void UnregisterFleetGrid(long gridId)
        {
            long leaderId;
            if (!_gridToLeaderMap.TryGetValue(gridId, out leaderId))
                return;

            FleetJumpLobby lobby;
            if (_activeLobbies.TryGetValue(leaderId, out lobby))
            {
                lobby.InTransitGridIds.Remove(gridId);

                // As long as other ships in this fleet are still in transit, keep _gridToLeaderMap intact
                // so later arriving ships still recognize the ships already at the destination as fellow fleet members.
                // Once the LAST ship arrives (or aborts/drops out mid-jump), clean up the lobby and all grid mappings immediately.
                CloseLobbyIfLanded(leaderId, lobby);
            }
            else
            {
                _gridToLeaderMap.Remove(gridId);
            }
        }

        // No ship of the fleet is in flight any more: close the lobby and release every ship from the fleet rules
        private static void CloseLobbyIfLanded(long leaderId, FleetJumpLobby lobby)
        {
            if (lobby.InTransitGridIds.Count > 0)
                return;

            foreach (var member in lobby.Members)
            {
                _gridToLeaderMap.Remove(member.GridEntityId);
            }
            _gridToLeaderMap.Remove(leaderId);
            _activeLobbies.Remove(leaderId);
        }

        // Network message handler
        public static void HandleNetworkMessage(FleetJumpMessage msg)
        {
            if (msg == null) return;

            switch (msg.Action)
            {
                case FleetActionType.CreateLobby:
                    if (!_activeLobbies.ContainsKey(msg.LeaderGridId))
                    {
                        var leaderGrid = MyEntities.GetEntityById(msg.LeaderGridId) as MyCubeGrid;
                        if (leaderGrid != null)
                        {
                            long factionId = GetGridFactionId(leaderGrid);
                            var lobby = new FleetJumpLobby
                            {
                                LeaderGridId = msg.LeaderGridId,
                                LeaderPlayerId = msg.SendingPlayerId,
                                LeaderFactionId = factionId,
                                LeaderGridName = leaderGrid.DisplayName ?? "Leader Ship",
                                DepartureOrigin = leaderGrid.PositionComp.GetPosition(),
                                DepartureOrientation = leaderGrid.WorldMatrix.GetOrientation(),
                                Mode = (HyperspaceSystem.JumpMode)msg.Mode,
                                JumpDistanceRatio = msg.JumpDistanceRatio,
                                TargetGpsCoords = msg.HasGpsCoords ? (Vector3D?)msg.GpsCoords : null,
                                TargetGpsName = msg.GpsName,
                                LeaderJumpDistance = msg.LeaderJumpDistance,
                                IsExecuting = false
                            };

                            lobby.Members.Add(new FleetMemberInfo
                            {
                                GridEntityId = msg.LeaderGridId,
                                PlayerId = msg.SendingPlayerId,
                                GridName = lobby.LeaderGridName,
                                RelativeOffset = Vector3D.Zero,
                                StaggerDelayTicks = 0
                            });

                            _activeLobbies[msg.LeaderGridId] = lobby;
                            _gridToLeaderMap[msg.LeaderGridId] = msg.LeaderGridId;

                            foreach (var fat in leaderGrid.GetFatBlocks())
                            {
                                var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                                if (drive?.hyperspace != null)
                                {
                                    var hs = drive.hyperspace;
                                    hs.FleetLeaderGridId = msg.LeaderGridId;
                                    hs.Mode = lobby.Mode;
                                    hs.JumpDistanceRatio = lobby.JumpDistanceRatio;
                                    hs.SelectedGpsCoords = lobby.TargetGpsCoords;
                                    hs.SelectedGpsName = lobby.TargetGpsName;
                                    hs.EnterFleetChargingLoop(msg.SendingPlayerId);
                                    break;
                                }
                            }

                            NotifyLocalPlayerOfInvite(lobby);
                        }
                    }
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacket, data);
                    }
                    break;

                case FleetActionType.Join:
                    FleetJumpLobby joinLobby;
                    if (_activeLobbies.TryGetValue(msg.LeaderGridId, out joinLobby))
                    {
                        if (!_gridToLeaderMap.ContainsKey(msg.MemberGridId))
                        {
                            var memberGrid = MyEntities.GetEntityById(msg.MemberGridId) as MyCubeGrid;
                            if (memberGrid != null)
                            {
                                Vector3D memberPos = memberGrid.PositionComp.GetPosition();
                                MatrixD leaderInv = MatrixD.Invert(joinLobby.DepartureOrientation);
                                Vector3D relWorld = memberPos - joinLobby.DepartureOrigin;
                                Vector3D relLocal = Vector3D.TransformNormal(relWorld, leaderInv);

                                int staggerTicks = (msg.StaggerTicks >= FLEET_STAGGER_MIN_TICKS && msg.StaggerTicks <= FLEET_STAGGER_MAX_TICKS)
                                    ? msg.StaggerTicks
                                    : GetDynamicFleetStaggerTicks(joinLobby.Members.Count, joinLobby.Members.Count + 1, memberGrid.EntityId);

                                joinLobby.Members.Add(new FleetMemberInfo
                                {
                                    GridEntityId = msg.MemberGridId,
                                    PlayerId = msg.SendingPlayerId,
                                    GridName = memberGrid.DisplayName ?? $"Wing Ship {joinLobby.Members.Count}",
                                    RelativeOffset = relLocal,
                                    StaggerDelayTicks = staggerTicks
                                });
                                _gridToLeaderMap[msg.MemberGridId] = msg.LeaderGridId;

                                foreach (var fat in memberGrid.GetFatBlocks())
                                {
                                    var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                                    if (drive?.hyperspace != null)
                                    {
                                        var hs = drive.hyperspace;
                                        hs.FleetLeaderGridId = msg.LeaderGridId;
                                        hs.Mode = joinLobby.Mode;
                                        hs.JumpDistanceRatio = joinLobby.JumpDistanceRatio;
                                        hs.SelectedGpsCoords = joinLobby.TargetGpsCoords;
                                        hs.SelectedGpsName = joinLobby.TargetGpsName;
                                        hs.EnterFleetChargingLoop(msg.SendingPlayerId);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacket, data);
                    }
                    break;

                case FleetActionType.Leave:
                    CancelFleetJump(msg.MemberGridId, msg.SendingPlayerId);
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacket, data);
                    }
                    break;

                case FleetActionType.CancelLobby:
                    CancelFleetJump(msg.LeaderGridId, msg.SendingPlayerId);
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacket, data);
                    }
                    break;

                case FleetActionType.UpdateTarget:
                    FleetJumpLobby targetLobby;
                    if (_activeLobbies.TryGetValue(msg.LeaderGridId, out targetLobby) && !targetLobby.IsExecuting)
                    {
                        ApplyFleetTarget(targetLobby, (HyperspaceSystem.JumpMode)msg.Mode, msg.HasGpsCoords ? (Vector3D?)msg.GpsCoords : null,
                            msg.GpsName, msg.JumpDistanceRatio, msg.LeaderJumpDistance);
                    }
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacket, data);
                    }
                    break;

                case FleetActionType.ExecuteJump:
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacket, data);
                    }
                    FleetJumpLobby execLobby;
                    if (_activeLobbies.TryGetValue(msg.LeaderGridId, out execLobby))
                    {
                        execLobby.IsExecuting = true;
                        ExecuteFleetJumpLocally(execLobby);
                    }
                    break;
            }
        }

        private static void SendFleetMessage(FleetJumpMessage msg)
        {
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacket, data);
            }
            else
            {
                MyAPIGateway.Multiplayer.SendMessageToServer(WarpDriveSession.fleetJumpPacket, data);
            }
        }

        // The scan that creates a lobby only runs on the leader's machine, but every machine receives the lobby message:
        // the invited pilot's own machine shows the invitation (a friendly lobby within range of the ship they sit in).
        private static void NotifyLocalPlayerOfInvite(FleetJumpLobby lobby)
        {
            IMyPlayer local = MyAPIGateway.Session?.Player;
            if (local == null || MyAPIGateway.Utilities.IsDedicated || local.IdentityId == lobby.LeaderPlayerId)
                return;

            var seat = local.Character?.Parent as IMyCubeBlock;
            IMyCubeGrid grid = seat?.CubeGrid;
            if (grid == null || grid.EntityId == lobby.LeaderGridId || IsGridInFleet(grid.EntityId))
                return;

            if (GetNearestFriendlyLobby(grid, local.IdentityId) != lobby)
                return;

            string targetText = lobby.Mode == HyperspaceSystem.JumpMode.GpsWaypoint ? lobby.TargetGpsName : "Manual Vector";
            HudEvents.Post(0L, 0L, $"[FLEET JUMP] {lobby.LeaderGridName} inviting to {targetText}. Use 'Fleet Jump' to Join.", HudSeverity.Info, HudRoute.Local, 5f);
        }

        // Relation helper checks
        private static long GetGridFactionId(IMyCubeGrid grid)
        {
            if (grid?.BigOwners != null && grid.BigOwners.Count > 0)
            {
                var faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(grid.BigOwners[0]);
                return faction?.FactionId ?? 0L;
            }
            return 0L;
        }

        private static bool IsFriendlyGrid(IMyCubeGrid grid, long leaderPlayerId, long leaderFactionId)
        {
            if (grid == null) return false;

            // In singleplayer / local play, unowned grids are friendly player grids
            if (!MyAPIGateway.Multiplayer.MultiplayerActive && (grid.BigOwners == null || grid.BigOwners.Count == 0))
                return true;

            long gridFactionId = GetGridFactionId(grid);
            if (grid.BigOwners != null && grid.BigOwners.Count > 0)
            {
                foreach (var owner in grid.BigOwners)
                {
                    if (IsFriendlyFaction(leaderFactionId, gridFactionId, leaderPlayerId, owner))
                        return true;
                }
            }
            else if (leaderFactionId == 0 && gridFactionId == 0)
            {
                return true;
            }

            return false;
        }

        private static bool IsFriendlyFaction(long fac1, long fac2, long p1, long p2)
        {
            // Same player
            if (p1 != 0 && p1 == p2) return true;

            // Same faction (and both are valid factions)
            if (fac1 != 0 && fac1 == fac2) return true;

            // Declared Allied factions (Friends)
            if (fac1 != 0 && fac2 != 0)
            {
                var rel = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(fac1, fac2);
                return rel == MyRelationsBetweenFactions.Friends;
            }

            // Un-factioned different players, neutrals, or enemies are NOT friendly
            return false;
        }
    }
}
