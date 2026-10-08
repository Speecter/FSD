using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    public static class HyperspaceControls
    {
        private static bool controlsInitialized = false;

        public static void RegisterControls()
        {
            if (controlsInitialized || MyAPIGateway.Utilities.IsDedicated)
                return;

            controlsInitialized = true;

            RegisterWarpDriveControls();
            RegisterRemoteControlControls();
        }

        private static void RegisterWarpDriveControls()
        {
            // Separator: Visual divider between Supercruise and Hyperspace
            var separator = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyUpgradeModule>("HyperspaceSeparator");
            separator.Enabled = IsHyperspaceDrive;
            separator.Visible = IsHyperspaceDrive;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(separator);

            // Jump Button
            var jumpBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("HyperspaceJumpBtn");
            jumpBtn.Title = MyStringId.GetOrCompute("Hyperspace Jump / Abort");
            jumpBtn.Tooltip = MyStringId.GetOrCompute("Initiates Hyperspace jump sequence or aborts if charging.");
            jumpBtn.Enabled = IsHyperspaceDrive;
            jumpBtn.Visible = IsHyperspaceDrive;
            jumpBtn.SupportsMultipleBlocks = false;
            jumpBtn.Action = OnJumpButtonPressed;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(jumpBtn);

            // Hotbar Action for Jump / Abort
            var jumpAction = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("HyperspaceJumpAction");
            jumpAction.Name = new StringBuilder("Hyperspace Jump / Abort");
            jumpAction.Icon = "Textures\\GUI\\Icons\\Actions\\Toggle.dds";
            jumpAction.Enabled = IsHyperspaceDrive;
            jumpAction.Action = OnJumpButtonPressed;
            jumpAction.Writer = ActionJumpWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(jumpAction);

            // Fleet Jump Button
            var fleetJumpBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("HyperspaceFleetJumpBtn");
            fleetJumpBtn.Title = MyStringId.GetOrCompute("Fleet Jump (Create / Join / Quit)");
            fleetJumpBtn.Tooltip = MyStringId.GetOrCompute("Creates a fleet jump lobby, joins a nearby friendly fleet jump, or manages/leaves formation.");
            fleetJumpBtn.Enabled = IsStandardHyperspaceDrive;
            fleetJumpBtn.Visible = IsStandardHyperspaceDrive;
            fleetJumpBtn.SupportsMultipleBlocks = false;
            fleetJumpBtn.Action = OnFleetJumpButtonPressed;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(fleetJumpBtn);

            // Hotbar Action for Fleet Jump
            var fleetJumpAction = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("HyperspaceFleetJumpAction");
            fleetJumpAction.Name = new StringBuilder("Fleet Jump (Create / Join / Quit)");
            fleetJumpAction.Icon = "Textures\\GUI\\Icons\\Actions\\Toggle.dds";
            fleetJumpAction.Enabled = IsStandardHyperspaceDrive;
            fleetJumpAction.Action = OnFleetJumpButtonPressed;
            fleetJumpAction.Writer = ActionFleetWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(fleetJumpAction);

            // Manual Distance Slider
            var distanceSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>("HyperspaceDistanceSlider");
            distanceSlider.Title = MyStringId.GetOrCompute("Range");
            distanceSlider.Tooltip = MyStringId.GetOrCompute("Adjusts manual forward jump distance based on available power.");
            distanceSlider.Enabled = IsHyperspaceDrive;
            distanceSlider.Visible = IsHyperspaceDrive;
            distanceSlider.SetLimits(0.0f, 1.0f);
            distanceSlider.Getter = (b) => { var hs = GetHyperspace(b); return hs.IsValid ? hs.JumpDistanceRatio : 1.0f; };
            distanceSlider.Setter = (b, v) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid)
                {
                    hs.JumpDistanceRatio = v;
                    hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                    hs.SelectedGpsCoords = null;
                    hs.SelectedGpsName = string.Empty;
                }
            };
            distanceSlider.Writer = (b, sb) =>
            {
                var hs = GetHyperspace(b);
                if (!hs.IsValid) return;
                float powerMW = hs.GetDrivePowerMW();
                double currentKm = hs.GetCurrentManualDistance(powerMW) / 1000.0;
                double maxKm = hs.GetMaxJumpDistance(powerMW) / 1000.0;
                sb.Append($"{currentKm:N0} km / {maxKm:N0} km ({(hs.JumpDistanceRatio * 100f):F0}%)");
            };
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(distanceSlider);

            // Hotbar Action: Increase Distance
            var incDistAction = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("HyperspaceIncDist");
            incDistAction.Name = new StringBuilder("Increase Jump Distance");
            incDistAction.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
            incDistAction.Enabled = IsHyperspaceDrive;
            incDistAction.Action = (b) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid)
                {
                    hs.JumpDistanceRatio = MathHelper.Clamp(hs.JumpDistanceRatio + 0.05f, 0f, 1f);
                    hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                    hs.SelectedGpsCoords = null;
                    hs.SelectedGpsName = string.Empty;
                }
            };
            incDistAction.Writer = ActionDistWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(incDistAction);

            // Hotbar Action: Decrease Distance
            var decDistAction = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("HyperspaceDecDist");
            decDistAction.Name = new StringBuilder("Decrease Jump Distance");
            decDistAction.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
            decDistAction.Enabled = IsHyperspaceDrive;
            decDistAction.Action = (b) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid)
                {
                    hs.JumpDistanceRatio = MathHelper.Clamp(hs.JumpDistanceRatio - 0.05f, 0f, 1f);
                    hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                    hs.SelectedGpsCoords = null;
                    hs.SelectedGpsName = string.Empty;
                }
            };
            decDistAction.Writer = ActionDistWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(decDistAction);

            // Clear GPS / Switch to Manual Button
            var clearGpsBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyUpgradeModule>("HyperspaceClearGps");
            clearGpsBtn.Title = MyStringId.GetOrCompute("Reset to Manual Distance");
            clearGpsBtn.Tooltip = MyStringId.GetOrCompute("Deselects GPS waypoint and returns to manual distance mode.");
            clearGpsBtn.Enabled = IsHyperspaceDrive;
            clearGpsBtn.Visible = IsHyperspaceDrive;
            clearGpsBtn.SupportsMultipleBlocks = false;
            clearGpsBtn.Action = (b) =>
            {
                var hs = GetHyperspace(b);
                if (!hs.IsValid) return;
                hs.SelectedGpsCoords = null;
                hs.SelectedGpsName = string.Empty;
                hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                b.UpdateVisual();
            };
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(clearGpsBtn);

            // GPS Waypoint Listbox
            var gpsListbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyUpgradeModule>("HyperspaceGpsList");
            gpsListbox.Title = MyStringId.GetOrCompute("Select GPS Destination");
            gpsListbox.Tooltip = MyStringId.GetOrCompute("Select a GPS coordinate from your personal waypoints.");
            gpsListbox.VisibleRowsCount = 6;
            gpsListbox.SupportsMultipleBlocks = false;
            gpsListbox.Enabled = IsHyperspaceDrive;
            gpsListbox.Visible = IsHyperspaceDrive;
            gpsListbox.ListContent = PopulateGpsList;
            gpsListbox.ItemSelected = OnGpsSelected;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(gpsListbox);

            // Programmable Block Properties
            var gpsProp = MyAPIGateway.TerminalControls.CreateProperty<string, IMyUpgradeModule>("HyperspaceTargetGPS");
            gpsProp.Enabled = IsHyperspaceDrive;
            gpsProp.Visible = IsHyperspaceDrive;
            gpsProp.Getter = GetGpsProperty;
            gpsProp.Setter = SetGpsProperty;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(gpsProp);

            var statusProp = MyAPIGateway.TerminalControls.CreateProperty<string, IMyUpgradeModule>("HyperspaceStatus");
            statusProp.Enabled = IsHyperspaceDrive;
            statusProp.Visible = IsHyperspaceDrive;
            statusProp.Getter = (b) => { var hs = GetHyperspace(b); return hs.IsValid ? hs.StateString : "Idle"; };
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(statusProp);

            var distProp = MyAPIGateway.TerminalControls.CreateProperty<float, IMyUpgradeModule>("HyperspaceDistanceRatio");
            distProp.Enabled = IsHyperspaceDrive;
            distProp.Visible = IsHyperspaceDrive;
            distProp.Getter = (b) => { var hs = GetHyperspace(b); return hs.IsValid ? hs.JumpDistanceRatio : 1.0f; };
            distProp.Setter = (b, v) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid) hs.JumpDistanceRatio = MathHelper.Clamp(v, 0f, 1f);
            };
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(distProp);
        }

        private static void RegisterRemoteControlControls()
        {
            // Separator: Visual divider on Remote Control
            var separator = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("HyperspaceSeparator_RC");
            separator.Enabled = IsHyperspaceGrid;
            separator.Visible = IsHyperspaceGrid;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(separator);

            // Jump Button
            var jumpBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>("HyperspaceJumpBtn_RC");
            jumpBtn.Title = MyStringId.GetOrCompute("Hyperspace Jump / Abort");
            jumpBtn.Tooltip = MyStringId.GetOrCompute("Initiates Hyperspace jump sequence or aborts if charging.");
            jumpBtn.Enabled = IsHyperspaceGrid;
            jumpBtn.Visible = IsHyperspaceGrid;
            jumpBtn.SupportsMultipleBlocks = false;
            jumpBtn.Action = OnJumpButtonPressed;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(jumpBtn);

            // Hotbar Action for Jump / Abort
            var jumpAction = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>("HyperspaceJumpAction_RC");
            jumpAction.Name = new StringBuilder("Hyperspace Jump / Abort");
            jumpAction.Icon = "Textures\\GUI\\Icons\\Actions\\Toggle.dds";
            jumpAction.Enabled = IsHyperspaceGrid;
            jumpAction.Action = OnJumpButtonPressed;
            jumpAction.Writer = ActionJumpWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(jumpAction);

            // Fleet Jump Button for Remote Control
            var fleetJumpBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>("HyperspaceFleetJumpBtn_RC");
            fleetJumpBtn.Title = MyStringId.GetOrCompute("Fleet Jump (Create / Join / Quit)");
            fleetJumpBtn.Tooltip = MyStringId.GetOrCompute("Creates a fleet jump lobby, joins a nearby friendly fleet jump, or manages/leaves formation.");
            fleetJumpBtn.Enabled = IsStandardHyperspaceGrid;
            fleetJumpBtn.Visible = IsStandardHyperspaceGrid;
            fleetJumpBtn.SupportsMultipleBlocks = false;
            fleetJumpBtn.Action = OnFleetJumpButtonPressed;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(fleetJumpBtn);

            // Hotbar Action for Fleet Jump on Remote Control
            var fleetJumpAction = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>("HyperspaceFleetJumpAction_RC");
            fleetJumpAction.Name = new StringBuilder("Fleet Jump (Create / Join / Quit)");
            fleetJumpAction.Icon = "Textures\\GUI\\Icons\\Actions\\Toggle.dds";
            fleetJumpAction.Enabled = IsStandardHyperspaceGrid;
            fleetJumpAction.Action = OnFleetJumpButtonPressed;
            fleetJumpAction.Writer = ActionFleetWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(fleetJumpAction);

            // Manual Distance Slider
            var distanceSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>("HyperspaceDistanceSlider_RC");
            distanceSlider.Title = MyStringId.GetOrCompute("Range");
            distanceSlider.Tooltip = MyStringId.GetOrCompute("Adjusts manual forward jump distance based on available power.");
            distanceSlider.Enabled = IsHyperspaceGrid;
            distanceSlider.Visible = IsHyperspaceGrid;
            distanceSlider.SetLimits(0.0f, 1.0f);
            distanceSlider.Getter = (b) => { var hs = GetHyperspace(b); return hs.IsValid ? hs.JumpDistanceRatio : 1.0f; };
            distanceSlider.Setter = (b, v) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid)
                {
                    hs.JumpDistanceRatio = v;
                    hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                    hs.SelectedGpsCoords = null;
                    hs.SelectedGpsName = string.Empty;
                }
            };
            distanceSlider.Writer = (b, sb) =>
            {
                var hs = GetHyperspace(b);
                if (!hs.IsValid)
                {
                    sb.Append("No FSD");
                    return;
                }
                float powerMW = hs.GetDrivePowerMW();
                double currentKm = hs.GetCurrentManualDistance(powerMW) / 1000.0;
                double maxKm = hs.GetMaxJumpDistance(powerMW) / 1000.0;
                sb.Append($"{currentKm:N0} km / {maxKm:N0} km ({(hs.JumpDistanceRatio * 100f):F0}%)");
            };
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(distanceSlider);

            // Hotbar Action: Increase Distance
            var incDistAction = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>("HyperspaceIncDist_RC");
            incDistAction.Name = new StringBuilder("Increase Jump Distance");
            incDistAction.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
            incDistAction.Enabled = IsHyperspaceGrid;
            incDistAction.Action = (b) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid)
                {
                    hs.JumpDistanceRatio = MathHelper.Clamp(hs.JumpDistanceRatio + 0.05f, 0f, 1f);
                    hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                    hs.SelectedGpsCoords = null;
                    hs.SelectedGpsName = string.Empty;
                }
            };
            incDistAction.Writer = ActionDistWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(incDistAction);

            // Hotbar Action: Decrease Distance
            var decDistAction = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>("HyperspaceDecDist_RC");
            decDistAction.Name = new StringBuilder("Decrease Jump Distance");
            decDistAction.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
            decDistAction.Enabled = IsHyperspaceGrid;
            decDistAction.Action = (b) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid)
                {
                    hs.JumpDistanceRatio = MathHelper.Clamp(hs.JumpDistanceRatio - 0.05f, 0f, 1f);
                    hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                    hs.SelectedGpsCoords = null;
                    hs.SelectedGpsName = string.Empty;
                }
            };
            decDistAction.Writer = ActionDistWriter;
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(decDistAction);

            // Clear GPS / Switch to Manual Button
            var clearGpsBtn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>("HyperspaceClearGps_RC");
            clearGpsBtn.Title = MyStringId.GetOrCompute("Reset to Manual Distance");
            clearGpsBtn.Tooltip = MyStringId.GetOrCompute("Deselects GPS waypoint and returns to manual distance mode.");
            clearGpsBtn.Enabled = IsHyperspaceGrid;
            clearGpsBtn.Visible = IsHyperspaceGrid;
            clearGpsBtn.SupportsMultipleBlocks = false;
            clearGpsBtn.Action = (b) =>
            {
                var hs = GetHyperspace(b);
                if (!hs.IsValid) return;
                hs.SelectedGpsCoords = null;
                hs.SelectedGpsName = string.Empty;
                hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                b.UpdateVisual();
            };
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(clearGpsBtn);

            // GPS Waypoint Listbox
            var gpsListbox = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyRemoteControl>("HyperspaceGpsList_RC");
            gpsListbox.Title = MyStringId.GetOrCompute("Select GPS Destination");
            gpsListbox.Tooltip = MyStringId.GetOrCompute("Select a GPS coordinate from your personal waypoints.");
            gpsListbox.VisibleRowsCount = 6;
            gpsListbox.SupportsMultipleBlocks = false;
            gpsListbox.Enabled = IsHyperspaceGrid;
            gpsListbox.Visible = IsHyperspaceGrid;
            gpsListbox.ListContent = PopulateGpsList;
            gpsListbox.ItemSelected = OnGpsSelected;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(gpsListbox);

            // Programmable Block Properties
            var gpsProp = MyAPIGateway.TerminalControls.CreateProperty<string, IMyRemoteControl>("HyperspaceTargetGPS");
            gpsProp.Enabled = IsHyperspaceGrid;
            gpsProp.Visible = IsHyperspaceGrid;
            gpsProp.Getter = GetGpsProperty;
            gpsProp.Setter = SetGpsProperty;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(gpsProp);

            var statusProp = MyAPIGateway.TerminalControls.CreateProperty<string, IMyRemoteControl>("HyperspaceStatus");
            statusProp.Enabled = IsHyperspaceGrid;
            statusProp.Visible = IsHyperspaceGrid;
            statusProp.Getter = (b) => { var hs = GetHyperspace(b); return hs.IsValid ? hs.StateString : "Idle"; };
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(statusProp);

            var distProp = MyAPIGateway.TerminalControls.CreateProperty<float, IMyRemoteControl>("HyperspaceDistanceRatio");
            distProp.Enabled = IsHyperspaceGrid;
            distProp.Visible = IsHyperspaceGrid;
            distProp.Getter = (b) => { var hs = GetHyperspace(b); return hs.IsValid ? hs.JumpDistanceRatio : 1.0f; };
            distProp.Setter = (b, v) =>
            {
                var hs = GetHyperspace(b);
                if (hs.IsValid) hs.JumpDistanceRatio = MathHelper.Clamp(v, 0f, 1f);
            };
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(distProp);
        }

        private static void ActionJumpWriter(IMyTerminalBlock block, StringBuilder sb)
        {
            var hs = GetHyperspace(block);
            if (!hs.IsValid)
            {
                sb.Append("No FSD");
                return;
            }

            var grid = block.CubeGrid;
            if (grid != null && FleetJumpSystem.IsGridInFleet(grid.EntityId))
            {
                if (FleetJumpSystem.IsJumpLeader(grid.EntityId))
                    sb.Append("Leader");
                else
                    sb.Append("In Fleet");
                return;
            }

            if (hs.IsIdle) { sb.Append("Jump"); }
            else if (hs.IsCharging) { sb.Append("Abort"); }
            else if (hs.IsActive) { sb.Append("Active"); }
            else if (hs.IsCooling) { sb.Append("Cooling"); }
        }

        private static void ActionFleetWriter(IMyTerminalBlock block, StringBuilder sb)
        {
            var hs = GetHyperspace(block);
            if (!hs.IsValid)
            {
                sb.Append("No FSD");
                return;
            }

            var grid = block.CubeGrid;
            if (grid == null)
            {
                sb.Append("Fleet");
                return;
            }

            if (FleetJumpSystem.IsGridInFleet(grid.EntityId))
            {
                if (FleetJumpSystem.IsJumpLeader(grid.EntityId))
                    sb.Append("Leader");
                else
                    sb.Append("Quit");
                return;
            }

            var player = MyAPIGateway.Session?.Player;
            long playerId = player?.IdentityId ?? 0L;
            if (FleetJumpSystem.GetNearestFriendlyLobby(grid, playerId) != null)
            {
                sb.Append("Join");
            }
            else
            {
                sb.Append("Fleet");
            }
        }

        private static void ActionDistWriter(IMyTerminalBlock block, StringBuilder sb)
        {
            var hs = GetHyperspace(block);
            if (hs.IsValid)
                sb.Append($"{(hs.JumpDistanceRatio * 100f):F0}%");
        }

        public static WarpDrive GetWarpDrive(IMyTerminalBlock block)
        {
            if (block == null) return null;

            // Direct block check
            var directDrive = block.GameLogic?.GetAs<WarpDrive>();
            if (directDrive != null)
                return directDrive;

            var grid = block.CubeGrid as MyCubeGrid;
            if (grid == null) return null;

            // Check WarpDriveSession active systems
            if (WarpDriveSession.Instance != null)
            {
                var system = WarpDriveSession.Instance.GetWarpSystem(grid);
                if (system?.Hyperspace?.HostDrive != null)
                    return system.Hyperspace.HostDrive;
            }

            // Search local grid
            foreach (var fat in grid.GetFatBlocks())
            {
                var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                if (drive != null && drive.Block != null && drive.Block.IsFunctional)
                    return drive;
            }

            // Search connected physical grid group (rotors, pistons, hinges, connectors)
            if (block.CubeGrid != null)
            {
                var group = new List<IMyCubeGrid>();
                MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Physical, group);
                foreach (var g in group)
                {
                    var mg = g as MyCubeGrid;
                    if (mg == null || mg == grid) continue;
                    foreach (var fat in mg.GetFatBlocks())
                    {
                        var drive = fat?.GameLogic?.GetAs<WarpDrive>();
                        if (drive != null && drive.Block != null && drive.Block.IsFunctional)
                            return drive;
                    }
                }
            }

            return null;
        }

        public struct HyperspaceAdapter
        {
            public readonly HyperspaceSystem hs;
            public readonly CapitalFSD cap;
            public readonly WarpDrive drive;

            public HyperspaceAdapter(WarpDrive d)
            {
                drive = d;
                hs = d?.Hyperspace;
                cap = d?.CapitalFSD;
            }

            public bool IsValid => (hs != null) || (cap != null);

            public float JumpDistanceRatio
            {
                get { return (hs != null) ? hs.JumpDistanceRatio : (cap != null ? cap.JumpDistanceRatio : 1f); }
                set { if (hs != null) hs.JumpDistanceRatio = value; else if (cap != null) cap.JumpDistanceRatio = value; }
            }

            public HyperspaceSystem.JumpMode Mode
            {
                get { return (hs != null) ? hs.Mode : (cap != null ? cap.Mode : HyperspaceSystem.JumpMode.ManualDistance); }
                set { if (hs != null) hs.Mode = value; else if (cap != null) cap.Mode = value; }
            }

            public string SelectedGpsName
            {
                get { return (hs != null) ? hs.SelectedGpsName : (cap != null ? cap.SelectedGpsName : string.Empty); }
                set { if (hs != null) hs.SelectedGpsName = value; else if (cap != null) cap.SelectedGpsName = value; }
            }

            public Vector3D? SelectedGpsCoords
            {
                get { return (hs != null) ? hs.SelectedGpsCoords : (cap != null ? cap.SelectedGpsCoords : null); }
                set { if (hs != null) hs.SelectedGpsCoords = value; else if (cap != null) cap.SelectedGpsCoords = value; }
            }

            public string StateString => (hs != null) ? hs.State.ToString() : (cap != null ? cap.State.ToString() : "Idle");

            public bool IsIdle => (hs != null) ? hs.State == HyperspaceSystem.HyperState.Idle : (cap != null ? cap.State == CapitalFSD.CapState.Idle : true);
            public bool IsActive => (hs != null) ? hs.State == HyperspaceSystem.HyperState.Active : (cap != null ? cap.State == CapitalFSD.CapState.Transit : false);
            public bool IsCooling => (hs != null) ? hs.State == HyperspaceSystem.HyperState.Cooldown : (cap != null ? cap.State == CapitalFSD.CapState.Cooldown : false);
            public bool IsCharging => (hs != null) ? (hs.State == HyperspaceSystem.HyperState.Charging || hs.State == HyperspaceSystem.HyperState.HoldingCharge || hs.State == HyperspaceSystem.HyperState.Countdown) : (cap != null ? (cap.State == CapitalFSD.CapState.Charging || cap.State == CapitalFSD.CapState.HoldingCharge || cap.State == CapitalFSD.CapState.EngageRiser) : false);

            public float GetDrivePowerMW() => (hs != null) ? hs.GetDrivePowerMW() : (cap != null ? (cap.HostDrive?.Settings?.baseRequiredPower ?? 100f) : 100f);

            public double GetCurrentManualDistance(float power) => (hs != null) ? hs.GetCurrentManualDistance(power) : (cap != null ? cap.GetCurrentManualDistance(power) : 0);

            public double GetMaxJumpDistance(float power) => (hs != null) ? hs.GetMaxJumpDistance(power) : (cap != null ? cap.GetMaxJumpDistance(power) : 0);
        }

        private static bool IsWarpDrive(IMyTerminalBlock block)
        {
            return block?.GameLogic?.GetAs<WarpDrive>() != null;
        }

        private static bool IsHyperspaceDrive(IMyTerminalBlock block)
        {
            var drive = block?.GameLogic?.GetAs<WarpDrive>();
            return drive != null && (drive.SupportsHyperspace || drive.IsCapitalFSD);
        }

        private static bool IsHyperspaceGrid(IMyTerminalBlock block)
        {
            var drive = GetWarpDrive(block);
            return drive != null && (drive.SupportsHyperspace || drive.IsCapitalFSD);
        }

        private static bool IsStandardHyperspaceDrive(IMyTerminalBlock block)
        {
            var drive = block?.GameLogic?.GetAs<WarpDrive>();
            return drive != null && drive.SupportsHyperspace;
        }

        private static bool IsStandardHyperspaceGrid(IMyTerminalBlock block)
        {
            var drive = GetWarpDrive(block);
            return drive != null && drive.SupportsHyperspace;
        }

        private static HyperspaceAdapter GetHyperspace(IMyTerminalBlock block)
        {
            return new HyperspaceAdapter(GetWarpDrive(block));
        }

        private static string GetGpsProperty(IMyTerminalBlock block)
        {
            var hs = GetHyperspace(block);
            if (!hs.IsValid || !hs.SelectedGpsCoords.HasValue) return string.Empty;
            var c = hs.SelectedGpsCoords.Value;
            return $"GPS:{hs.SelectedGpsName}:{c.X:F2}:{c.Y:F2}:{c.Z:F2}:";
        }

        private static void SetGpsProperty(IMyTerminalBlock block, string gpsString)
        {
            var hs = GetHyperspace(block);
            if (!hs.IsValid) return;

            if (string.IsNullOrWhiteSpace(gpsString))
            {
                hs.SelectedGpsCoords = null;
                hs.SelectedGpsName = string.Empty;
                hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                block.UpdateVisual();
                return;
            }

            string name;
            Vector3D coords;
            if (TryParseGps(gpsString, out name, out coords))
            {
                hs.SelectedGpsCoords = coords;
                hs.SelectedGpsName = name;
                hs.Mode = HyperspaceSystem.JumpMode.GpsWaypoint;
                block.UpdateVisual();
            }
        }

        public static bool TryParseGps(string input, out string name, out Vector3D coords)
        {
            name = "GPS";
            coords = Vector3D.Zero;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.Trim();

            // Format: GPS:Name:X:Y:Z:...
            if (input.StartsWith("GPS:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = input.Split(':');
                if (parts.Length >= 5)
                {
                    name = parts[1];
                    double x, y, z;
                    if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                        double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    {
                        coords = new Vector3D(x, y, z);
                        return true;
                    }
                }
            }

            // Format: X:Y:Z or X,Y,Z or X Y Z
            char[] seps = new char[] { ':', ',', ';', ' ' };
            var segments = input.Split(seps, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 3)
            {
                double x, y, z;
                if (double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                    double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                    double.TryParse(segments[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                {
                    name = "Target";
                    coords = new Vector3D(x, y, z);
                    return true;
                }
            }

            // Search player's GPS list for matching name
            var player = MyAPIGateway.Session?.Player;
            if (player != null)
            {
                var gpsList = MyAPIGateway.Session.GPS.GetGpsList(player.IdentityId);
                if (gpsList != null)
                {
                    foreach (var g in gpsList)
                    {
                        if (string.Equals(g.Name, input, StringComparison.OrdinalIgnoreCase))
                        {
                            name = g.Name;
                            coords = g.Coords;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Populates the listbox with player's GPS coordinates
        private static void PopulateGpsList(IMyTerminalBlock block, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var hs = GetHyperspace(block);
            if (!hs.IsValid) return;

            var player = MyAPIGateway.Session?.Player;
            if (player == null) return;

            var gpsList = MyAPIGateway.Session.GPS.GetGpsList(player.IdentityId);
            if (gpsList == null) return;

            foreach (var gps in gpsList)
            {
                var item = new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute(gps.Name),
                    MyStringId.GetOrCompute($"{gps.Coords.X:F0}, {gps.Coords.Y:F0}, {gps.Coords.Z:F0}"),
                    gps
                );
                items.Add(item);

                if (hs.Mode == HyperspaceSystem.JumpMode.GpsWaypoint && hs.SelectedGpsName == gps.Name)
                {
                    selected.Add(item);
                }
            }
        }

        // Handles GPS waypoint selection
        private static void OnGpsSelected(IMyTerminalBlock block, List<MyTerminalControlListBoxItem> selected)
        {
            var hs = GetHyperspace(block);
            if (!hs.IsValid) return;

            if (selected != null && selected.Count > 0)
            {
                var gps = selected[0].UserData as IMyGps;
                if (gps != null)
                {
                    hs.SelectedGpsCoords = gps.Coords;
                    hs.SelectedGpsName = gps.Name;
                    hs.Mode = HyperspaceSystem.JumpMode.GpsWaypoint;
                    block.UpdateVisual();
                }
            }
        }

        // Handles jump button press on both Host and Client
        private static void OnJumpButtonPressed(IMyTerminalBlock block)
        {
            var drive = GetWarpDrive(block);
            if (drive == null)
            {
                var p = MyAPIGateway.Session?.Player;
                long pid = p?.IdentityId ?? 0L;
                MyAPIGateway.Utilities.ShowNotification("No Frame Shift Drive found on grid!", 2000, "Red");
                return;
            }

            if (!drive.SupportsHyperspace && !drive.IsCapitalFSD)
            {
                var p = MyAPIGateway.Session?.Player;
                long pid = p?.IdentityId ?? 0L;
                MyAPIGateway.Utilities.ShowNotification("This FSD does not support Hyperspace jumps! (Supercruise Only)", 3000, "Red");
                return;
            }

            var player = MyAPIGateway.Session?.Player;
            long playerId = player?.IdentityId ?? 0L;
            var hs = new HyperspaceAdapter(drive);

            if (drive.System != null && drive.System.WarpState != WarpSystem.State.Idle && (!hs.IsValid || hs.IsIdle))
            {
                drive.System.SendMessage(drive.System.warnInUse, 3f, "Red", playerId);
                return;
            }

            var msg = new HyperspaceMessage
            {
                EntityId = drive.Block.EntityId,
                SendingPlayerID = playerId,
                Mode = hs.IsValid ? (int)hs.Mode : 0,
                JumpDistanceRatio = hs.IsValid ? hs.JumpDistanceRatio : 1.0f,
                HasGpsCoords = hs.IsValid && hs.SelectedGpsCoords.HasValue,
                GpsCoords = (hs.IsValid && hs.SelectedGpsCoords.HasValue) ? hs.SelectedGpsCoords.Value : Vector3D.Zero,
                GpsName = hs.IsValid ? hs.SelectedGpsName : string.Empty
            };

            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(msg);

            if (MyAPIGateway.Multiplayer.IsServer)
            {
                if (!MyAPIGateway.Utilities.IsDedicated)
                    MyAPIGateway.Multiplayer.SendMessageToOthers(WarpDriveSession.toggleHyperspacePacketId, data);

                drive.Hyperspace?.TriggerJump(playerId);
                drive.CapitalFSD?.TriggerJump(playerId);
            }
            else
            {
                // Send packet to host to initiate/abort jump
                MyAPIGateway.Multiplayer.SendMessageToServer(WarpDriveSession.toggleHyperspacePacketId, data);
            }
        }

        private static void OnFleetJumpButtonPressed(IMyTerminalBlock block)
        {
            var drive = GetWarpDrive(block);
            if (drive != null && !drive.SupportsHyperspace)
            {
                MyAPIGateway.Utilities.ShowNotification("This FSD does not support Hyperspace jumps! (Supercruise Only)", 3000, "Red");
                return;
            }

            var player = MyAPIGateway.Session?.Player;
            long playerId = player?.IdentityId ?? 0L;
            FleetJumpSystem.InitiateFleetJump(block, playerId);
        }
    }
}
