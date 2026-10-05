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
        [ProtoEnum] SyncLobbyState
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
        public double LeaderJumpDistance;
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
            if (drive == null || drive.Hyperspace == null)
            {
                MyAPIGateway.Utilities.ShowNotification("No Frame Shift Drive found on ship!", 3000, "Red");
                return;
            }

            if (!drive.SupportsHyperspace)
            {
                MyAPIGateway.Utilities.ShowNotification("This FSD does not support Hyperspace jumps! (Supercruise Only)", 3000, "Red");
                return;
            }

            var hs = drive.Hyperspace;
            var mainGrid = hs.GridSystem?.MainGrid;
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
                else if (IsJumpLeader(mainGrid.EntityId))
                {
                    if (existingLobby != null && !existingLobby.IsExecuting && !MyAPIGateway.Utilities.IsDedicated)
                    {
                        OpenJumpLeaderDialog(existingLobby);
                    }
                    return;
                }
                else
                {
                    // Member -> Leave / Quit fleet jump
                    CancelFleetJump(mainGrid.EntityId, playerId);
                    MyAPIGateway.Utilities.ShowNotification("Left Fleet Jump formation.", 3000, "White");
                    return;
                }
            }

            // 2. Check if a friendly lobby is nearby (< 5km) -> Join it!
            var nearbyLobby = GetNearestFriendlyLobby(mainGrid, playerId);
            if (nearbyLobby != null)
            {
                JoinFleetJump(mainGrid, playerId);
                return;
            }

            // 3. Otherwise -> Create new lobby (become Leader)
            // 0. Supercruise conflict check
            if (drive.System != null && drive.System.WarpState != WarpSystem.State.Idle)
            {
                drive.System.SendMessage(drive.System.warnInUse, 3f, "Red", playerId);
                return;
            }

            // Safety & Power pre-check
            if (!hs.CanInitiateJump(playerId)) return;

            long leaderOwnerId = (mainGrid.BigOwners != null && mainGrid.BigOwners.Count > 0) ? mainGrid.BigOwners[0] : playerId;
            long factionId = GetGridFactionId(mainGrid);

            // Enforce sector rule: Only 1 friendly fleet jump lobby active per 5 km sector
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
                        MyAPIGateway.Utilities.ShowNotification("Another friendly Fleet Jump is already active within 5 km! Move away or join it.", 4000, "Red");
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

            // Open Jump Leader confirmation popup window on leader's client
            if (!MyAPIGateway.Utilities.IsDedicated && playerId == (MyAPIGateway.Session?.Player?.IdentityId ?? -1))
            {
                OpenJumpLeaderDialog(lobby);
            }
        }

        // Calculates real-time alignment offset angle in degrees for any fleet member
        public static double GetMemberAlignmentOffset(long gridId, FleetJumpLobby lobby)
        {
            if (lobby == null) return 0.0;
            var grid = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (grid == null) return 999.0;

            IMyCockpit cockpit = null;
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

            MatrixD memberMatrix = (cockpit != null) ? cockpit.WorldMatrix : (rc != null ? rc.WorldMatrix : grid.WorldMatrix);
            Vector3D targetDir;
            if (lobby.Mode == HyperspaceSystem.JumpMode.GpsWaypoint && lobby.TargetGpsCoords.HasValue)
            {
                targetDir = Vector3D.Normalize(lobby.TargetGpsCoords.Value - memberMatrix.Translation);
            }
            else
            {
                var leaderGrid = MyEntities.GetEntityById(lobby.LeaderGridId) as MyCubeGrid;
                if (leaderGrid != null && !leaderGrid.MarkedForClose)
                {
                    lobby.DepartureOrientation = leaderGrid.WorldMatrix.GetOrientation();
                    lobby.DepartureOrigin = leaderGrid.PositionComp.GetPosition();
                }
                targetDir = lobby.DepartureOrientation.Forward;
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
                    if (drive != null && drive.Block != null && drive.Block.IsFunctional && drive.SupportsHyperspace)
                        break;
                    drive = null;
                }

                if (drive != null && drive.Hyperspace != null)
                {
                    // If ship is on FSD cooldown, don't invite or auto-join until cooled down
                    if (drive.Hyperspace.State == HyperspaceSystem.HyperState.Cooldown)
                        continue;

                    // Check if grid has a seated pilot or an unmanned working remote control
                    bool hasSeatedPilot = false;
                    bool hasWorkingRemoteControl = false;

                    foreach (var fat in grid.GetFatBlocks())
                    {
                        var cockpit = fat as IMyCockpit;
                        if (cockpit?.Pilot != null)
                            hasSeatedPilot = true;

                        var rc = fat as IMyRemoteControl;
                        if (rc != null && rc.IsFunctional)
                            hasWorkingRemoteControl = true;
                    }

                    if (hasSeatedPilot)
                    {
                        // Send Invitation notification to players on this grid
                        NotifyGridOfFleetInvite(grid, lobby);
                    }
                    else if (hasWorkingRemoteControl)
                    {
                        // Unmanned drone with Remote Control: Only auto-join if owned by same player or same faction!
                        long gridOwnerId = (grid.BigOwners != null && grid.BigOwners.Count > 0) ? grid.BigOwners[0] : 0L;
                        long gridFactionId = GetGridFactionId(grid);

                        bool isSameOwnerOrFaction = (leaderPlayerId != 0 && leaderPlayerId == gridOwnerId) ||
                                                    (lobby.LeaderFactionId != 0 && lobby.LeaderFactionId == gridFactionId);

                        if (isSameOwnerOrFaction)
                        {
                            JoinFleetJump(grid, leaderPlayerId);
                        }
                    }
                }
            }
        }

        // Shows the Jump Leader confirmation dialog (matching vanilla jump drive UX)
        public static void OpenJumpLeaderDialog(FleetJumpLobby lobby)
        {
            if (lobby == null || lobby.IsExecuting) return;

            string targetInfo = (lobby.Mode == HyperspaceSystem.JumpMode.GpsWaypoint && !string.IsNullOrEmpty(lobby.TargetGpsName))
                ? $"Target Waypoint: {lobby.TargetGpsName}"
                : $"Manual Distance: {(lobby.JumpDistanceRatio * 100f):F0}% range ({(lobby.LeaderJumpDistance / 1000.0):N0} km)";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("FLEET HYPERSPACE COORDINATION");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine(targetInfo);
            sb.AppendLine($"Fleet Sector Range: {FLEET_SCAN_RADIUS / 1000.0:F0} km");
            sb.AppendLine();
            sb.AppendLine($"Joined Fleet Ships ({lobby.Members.Count}):");
            for (int i = 0; i < lobby.Members.Count; i++)
            {
                var m = lobby.Members[i];
                string role = (i == 0) ? "[LEADER]" : $"[WING {i}]";
                double offset = GetMemberAlignmentOffset(m.GridEntityId, lobby);
                
                var memberGrid = MyEntities.GetEntityById(m.GridEntityId) as MyCubeGrid;
                HyperspaceSystem memberHs = null;
                if (memberGrid != null)
                {
                    foreach (var fat in memberGrid.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive?.Hyperspace != null)
                        {
                            memberHs = drive.Hyperspace;
                            break;
                        }
                    }
                }

                string alignStatus;
                if (memberHs != null && memberHs.State == HyperspaceSystem.HyperState.Cooldown)
                {
                    alignStatus = "[COOLING DOWN]";
                }
                else if (offset <= HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG)
                {
                    alignStatus = "[READY]";
                }
                else
                {
                    alignStatus = $"[ALIGNING: {offset:F1}°]";
                }

                sb.AppendLine($" {role} {m.GridName} - {alignStatus}");
            }
            sb.AppendLine();
            sb.AppendLine("Instructions:");
            sb.AppendLine(" • Click 'ENGAGE FLEET JUMP' when all ships are aligned [READY].");
            sb.AppendLine(" • Click 'X' / Esc to minimize window (reopen via 'Fleet Jump' button).");
            sb.AppendLine(" • Trigger standard 'Hyperspace Jump' (solo jump) to abort/cancel fleet jump.");

            MyAPIGateway.Utilities.ShowMissionScreen(
                "FLEET HYPERSPACE JUMP - JUMP LEADER",
                "CONFIRM FLEET JUMP",
                $"{lobby.Members.Count} SHIP(S) IN FORMATION",
                sb.ToString(),
                (result) =>
                {
                    if (result == ResultEnum.OK)
                    {
                        // Verify all members are cooled down and within 5.0 deg alignment tolerance before executing
                        for (int i = 0; i < lobby.Members.Count; i++)
                        {
                            var m = lobby.Members[i];
                            var mGrid = MyEntities.GetEntityById(m.GridEntityId) as MyCubeGrid;
                            if (mGrid != null)
                            {
                                foreach (var fat in mGrid.GetFatBlocks())
                                {
                                    var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                                    if (drive?.Hyperspace != null && drive.Hyperspace.State == HyperspaceSystem.HyperState.Cooldown)
                                    {
                                        MyAPIGateway.Utilities.ShowNotification($"Cannot jump: {m.GridName}'s FSD is still cooling down!", 4000, "Red");
                                        return;
                                    }
                                }
                            }

                            double offset = GetMemberAlignmentOffset(m.GridEntityId, lobby);
                            if (offset > HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG)
                            {
                                MyAPIGateway.Utilities.ShowNotification($"Cannot jump: {m.GridName} is not aligned (Offset: {offset:F1}°, Max {HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG:F1}°)", 4000, "Red");
                                return;
                            }
                        }

                        // Ok clicked and all ships aligned -> execute jump
                        ConfirmAndExecuteFleetJump(lobby.LeaderGridId, lobby.LeaderPlayerId);
                    }
                    else
                    {
                        // Closed / cancelled / escape -> keep fleet lobby active in holding charge!
                        MyAPIGateway.Utilities.ShowNotification("Fleet Jump window minimized. Press 'Fleet Jump' to reopen.", 3000, "White");
                    }
                },
                "ENGAGE FLEET JUMP"
            );
        }

        // Joins a friendly ship into an existing fleet jump
        public static void JoinFleetJump(IMyCubeGrid memberGrid, long playerId)
        {
            if (memberGrid == null) return;
            if (IsGridInFleet(memberGrid.EntityId)) return;

            // Find nearest active friendly lobby within 10km
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
                MyAPIGateway.Utilities.ShowNotification("No active Fleet Jump invitation within 5 km.", 3000, "Red");
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

            if (warpDrive == null || warpDrive.Hyperspace == null)
            {
                MyAPIGateway.Utilities.ShowNotification("No Frame Shift Drive on ship!", 3000, "Red");
                return;
            }

            if (!warpDrive.SupportsHyperspace)
            {
                MyAPIGateway.Utilities.ShowNotification("Your FSD does not support Hyperspace jumps! (Supercruise Only)", 3000, "Red");
                return;
            }

            var hs = warpDrive.Hyperspace;

            if (hs.State == HyperspaceSystem.HyperState.Cooldown)
            {
                MyAPIGateway.Utilities.ShowNotification("FSD is cooling down from previous jump!", 3000, "Red");
                return;
            }

            // Strict range validation: Ensure member ship's FSD has enough power to jump as far as the leader
            float powerMW = hs.GetDrivePowerMW();
            double memberMaxDist = hs.GetMaxJumpDistance(powerMW);
            double requiredDist = targetLobby.LeaderJumpDistance;

            if (memberMaxDist < requiredDist)
            {
                MyAPIGateway.Utilities.ShowNotification($"INSUFFICIENT FSD RANGE! (Need: {requiredDist / 1000.0:N0} km, Max: {memberMaxDist / 1000.0:N0} km)", 4000, "Red");
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

            MyAPIGateway.Utilities.ShowNotification($"Joined {targetLobby.LeaderGridName}'s Fleet Jump!", 3000, "White");
        }

        // Leaves or cancels a fleet jump
        public static void CancelFleetJump(long gridId, long playerId)
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
                            if (drive?.Hyperspace != null && drive.Hyperspace.IsFleetHolding)
                            {
                                drive.Hyperspace.AbortJump("FLEET JUMP DISBANDED BY LEADER");
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
                // Member left
                _gridToLeaderMap.Remove(gridId);
                lobby.Members.RemoveAll(m => m.GridEntityId == gridId);

                var grid = MyEntities.GetEntityById(gridId) as MyCubeGrid;
                if (grid != null)
                {
                    foreach (var fat in grid.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive?.Hyperspace != null && drive.Hyperspace.IsFleetHolding)
                        {
                            drive.Hyperspace.AbortJump("LEFT FLEET JUMP");
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
                if (drive?.Hyperspace != null)
                {
                    leaderHs = drive.Hyperspace;
                    break;
                }
            }

            // 1. Leader proximity obstacle check
            if (leaderHs != null && leaderHs.IsProximityDanger(leaderGrid.WorldMatrix, leaderGrid))
            {
                MyAPIGateway.Utilities.ShowNotification("Cannot jump: Leader path is obstructed by a proximity hazard!", 4000, "Red");
                return;
            }

            // 2. Member status check: ensure all wingmen are cooled down and aligned (<= 5.0°)
            for (int i = 0; i < lobby.Members.Count; i++)
            {
                var m = lobby.Members[i];
                var mGrid = MyEntities.GetEntityById(m.GridEntityId) as MyCubeGrid;
                if (mGrid != null)
                {
                    foreach (var fat in mGrid.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive?.Hyperspace != null && drive.Hyperspace.State == HyperspaceSystem.HyperState.Cooldown)
                        {
                            MyAPIGateway.Utilities.ShowNotification($"Cannot jump: {m.GridName}'s FSD is still cooling down!", 4000, "Red");
                            return;
                        }
                    }
                }

                double offset = GetMemberAlignmentOffset(m.GridEntityId, lobby);
                if (offset > HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG)
                {
                    MyAPIGateway.Utilities.ShowNotification($"Cannot jump: {m.GridName} is not aligned (Offset: {offset:F1}°, Max {HyperspaceSystem.ALIGNMENT_TOLERANCE_DEG:F1}°)", 4000, "Red");
                    return;
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
                    if (drive?.Hyperspace != null)
                    {
                        leaderHs = drive.Hyperspace;
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

            // Execute Leader jump (0 delay)
            leaderHs.ExecuteFleetMemberJump(0, Vector3D.Zero, leaderDir, leaderJourneyDuration, lobby.LeaderGridId, lobby.DepartureOrientation, lobby.LeaderJumpDistance);

            // Execute Member jumps with formation offsets, staggered delays, and synchronized travel duration
            Vector3D leaderPos = leaderGrid.PositionComp.GetPosition();
            MatrixD leaderInv = MatrixD.Invert(lobby.DepartureOrientation);

            // Populate InTransitGridIds to dynamically track every ship in flight
            lobby.InTransitGridIds.Clear();
            lobby.InTransitGridIds.Add(lobby.LeaderGridId);
            _gridToLeaderMap[lobby.LeaderGridId] = lobby.LeaderGridId;

            for (int i = 1; i < lobby.Members.Count; i++)
            {
                var member = lobby.Members[i];
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
                        if (drive?.Hyperspace != null)
                        {
                            drive.Hyperspace.AbortJump("FLEET JUMP ABORTED - EXCEEDED 5km SECTOR LIMIT");
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
                    if (drive?.Hyperspace != null)
                    {
                        var memberHs = drive.Hyperspace;
                        float mPower = memberHs.GetDrivePowerMW();
                        double mMaxDist = memberHs.GetMaxJumpDistance(mPower);

                        // Final check 1: confirm member still has power/range
                        if (mMaxDist < lobby.LeaderJumpDistance)
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

                        int staggerTicks = GetDynamicFleetStaggerTicks(i, lobby.Members.Count, member.GridEntityId);
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
                if (lobby.InTransitGridIds.Count == 0)
                {
                    foreach (var member in lobby.Members)
                    {
                        _gridToLeaderMap.Remove(member.GridEntityId);
                    }
                    _gridToLeaderMap.Remove(leaderId);
                    _activeLobbies.Remove(leaderId);
                }
            }
            else
            {
                _gridToLeaderMap.Remove(gridId);
            }
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
                                if (drive?.Hyperspace != null)
                                {
                                    var hs = drive.Hyperspace;
                                    hs.FleetLeaderGridId = msg.LeaderGridId;
                                    hs.Mode = lobby.Mode;
                                    hs.JumpDistanceRatio = lobby.JumpDistanceRatio;
                                    hs.SelectedGpsCoords = lobby.TargetGpsCoords;
                                    hs.SelectedGpsName = lobby.TargetGpsName;
                                    hs.EnterFleetChargingLoop(msg.SendingPlayerId);
                                    break;
                                }
                            }
                        }
                    }
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacketId, data);
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
                                    if (drive?.Hyperspace != null)
                                    {
                                        var hs = drive.Hyperspace;
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
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacketId, data);
                    }
                    break;

                case FleetActionType.Leave:
                    CancelFleetJump(msg.MemberGridId, msg.SendingPlayerId);
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacketId, data);
                    }
                    break;

                case FleetActionType.CancelLobby:
                    CancelFleetJump(msg.LeaderGridId, msg.SendingPlayerId);
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacketId, data);
                    }
                    break;

                case FleetActionType.ExecuteJump:
                    if (MyAPIGateway.Multiplayer.IsServer)
                    {
                        byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);
                        MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacketId, data);
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
                MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.fleetJumpPacketId, data);
            }
            else
            {
                MyAPIGateway.Multiplayer.SendMessageToServer(WarpDriveSession.fleetJumpPacketId, data);
            }
        }

        private static void NotifyGridOfFleetInvite(IMyCubeGrid grid, FleetJumpLobby lobby)
        {
            var myGrid = grid as MyCubeGrid;
            if (myGrid == null) return;

            // Discreet notification to avoid clutter
            string targetText = lobby.Mode == HyperspaceSystem.JumpMode.GpsWaypoint ? lobby.TargetGpsName : "Manual Vector";
            string text = $"[FLEET JUMP] {lobby.LeaderGridName} inviting to {targetText}. Use 'Fleet Jump' to Join.";

            foreach (var fat in myGrid.GetFatBlocks())
            {
                var cockpit = fat as IMyCockpit;
                if (cockpit?.Pilot != null)
                {
                    var player = MyAPIGateway.Players.GetPlayerControllingEntity(cockpit.Pilot);
                    if (player != null)
                    {
                        MyAPIGateway.Utilities.ShowNotification(text, 5000, "White");
                    }
                }
            }
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
            long gridOwnerId = (grid.BigOwners != null && grid.BigOwners.Count > 0) ? grid.BigOwners[0] : 0L;
            if (gridOwnerId == 0L) return false; // Unowned grids cannot join fleet

            long gridFactionId = GetGridFactionId(grid);
            return IsFriendlyFaction(leaderFactionId, gridFactionId, leaderPlayerId, gridOwnerId);
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
