using System;
using System.Collections.Generic;
using RichHudFramework.Client;
using RichHudFramework.UI;
using RichHudFramework.UI.Client;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Client-side entry point of the FSD HUD (built on Rich HUD Framework). Owns the elements, finds the ship the local player sits in,
    /// refreshes the model and feeds the views. Nothing here runs on a dedicated server.
    /// </summary>
    public static class FsdHud
    {
        private const float BottomMargin = 210f;          // keeps the strip clear of the vanilla toolbar and block name
        private const float SidePanelGap = 160f;          // side panels start this far outside the alignment ring
        private const double RegistrationGiveUpSeconds = 10.0;
        private const int SlowEveryFrames = 6;            // ~10 Hz for text and planet/mass lookups
        private const int GridCheckEveryFrames = 10;

        private struct PendingEvent
        {
            public string text;
            public HudSeverity severity;
            public float seconds;
        }

        private static bool initStarted;
        private static bool faulted;
        private static bool ready;
        private static bool warnedMissing;
        private static DateTime initTime;
        private static DateTime lastFrame;
        private static readonly List<PendingEvent> pending = new List<PendingEvent>();

        private static CamSpaceNode scaledRoot;
        private static FsdStatusPanel status;
        private static AlignmentIndicator align;
        private static FleetLobbyMenu lobby;
        private static FlightIndicator flight;
        private static HudFx fx;
        private static FlowFx flowFx;
        private static GlitchOverlay glitch;
        private static bool cursorOn;
        private static GpsLockMarker gpsLock;
        private static TargetMenu menu;
        private static EventFeed feed;
        private static DangerOverlay danger;

        private static readonly FsdHudModel model = new FsdHudModel();
        private static MyCubeGrid currentGrid;
        private static bool gridHasFsd;
        private static int frame;

        // Alignment ping
        private static MyEntity3DSoundEmitter pingEmitter;
        private static float pingTimer;
        private static float secondPingTimer;
        private static bool wasAligned;

        private static Vector2 headingOffset;   // the reticle's place on screen (third person: where the ship is heading)

        public static bool IsReady { get { return ready; } }
        public static bool IsTargetMenuOpen { get { return menu != null && menu.IsOpen; } }

        /// <summary>Fleet Jump pressed while in a fleet: opens the lobby with the mouse cursor, or closes it again.</summary>
        public static void ToggleFleetMenu(FleetJumpLobby fleet, long localGridId)
        {
            if (!ready || faulted || lobby == null || !HudSettings.Current.Enabled)
                return;

            try
            {
                if (lobby.Interactive)
                    lobby.Close();
                else
                    lobby.Open(fleet, localGridId);
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        public static void Init()
        {
            if (MyAPIGateway.Utilities.IsDedicated || initStarted)
                return;

            initStarted = true;
            initTime = DateTime.UtcNow;
            lastFrame = initTime;
            HudSettings.Load();
            RichHudClient.Init("Elite Dangerous FSD Supercruise", OnRhfReady, OnRhfReset);
        }

        public static void Close()
        {
            if (!initStarted)
                return;

            initStarted = false;
            ready = false;
            pending.Clear();
            DropElements();

            try
            {
                if (pingEmitter != null)
                    pingEmitter.StopSound(true);
            }
            catch (Exception)
            {
            }
            pingEmitter = null;
            HudSounds.Close();

            try
            {
                RichHudClient.Reset();
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ framework callbacks

        private static void OnRhfReady()
        {
            scaledRoot = new CamSpaceNode(HudMain.Root) { RotationAxis = new Vector3(0f, 0f, 1f) };
            flowFx = new FlowFx(scaledRoot);       // first, so it draws behind everything else
            fx = new HudFx(scaledRoot);            // shockwave rings sit behind the panel and the ring
            status = new FsdStatusPanel(scaledRoot) { Visible = false, Fx = fx };
            align = new AlignmentIndicator(scaledRoot) { Visible = false, Fx = fx };
            gpsLock = new GpsLockMarker(scaledRoot);
            flight = new FlightIndicator(scaledRoot);
            lobby = new FleetLobbyMenu(scaledRoot);
            menu = new TargetMenu(scaledRoot);
            feed = new EventFeed(scaledRoot);
            danger = new DangerOverlay(scaledRoot);
            glitch = new GlitchOverlay(scaledRoot) { Panel = status, Reticle = flight };   // last, so it tears across everything

            ready = true;
            warnedMissing = false;
            lastFrame = DateTime.UtcNow;

            for (int i = 0; i < pending.Count; i++)
                PushEvent(pending[i].text, pending[i].severity, pending[i].seconds);
            pending.Clear();
        }

        private static void OnRhfReset()
        {
            ready = false;
            DropElements();
        }

        private static void DropElements()
        {
            scaledRoot = null;
            status = null;
            align = null;
            lobby = null;
            flight = null;
            fx = null;
            flowFx = null;
            glitch = null;
            cursorOn = false;
            gpsLock = null;
            menu = null;
            feed = null;
            danger = null;
        }

        // ------------------------------------------------------------------ events

        /// <summary>Called from the drive logic (which runs in lockstep): a HUD error must never escape into it.</summary>
        public static void PushEvent(string text, HudSeverity severity, float seconds)
        {
            if (string.IsNullOrEmpty(text) || !initStarted || faulted)
                return;

            try
            {
                PushEventUnsafe(text, severity, seconds);
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        private static void PushEventUnsafe(string text, HudSeverity severity, float seconds)
        {
            if (!ready)
            {
                if (pending.Count < 16 && !warnedMissing)
                    pending.Add(new PendingEvent { text = text, severity = severity, seconds = seconds });
                return;
            }

            if (!HudSettings.Current.Enabled)
                return;

            feed.Push(text, severity, Math.Max(1.5f, seconds));
            if (severity == HudSeverity.Critical)
            {
                danger.Trigger(text);
                fx.OnCriticalEvent(text);
            }
        }

        // ------------------------------------------------------------------ target menu

        /// <summary>"Jump Target" toolbar action: first press opens the menu, the next one locks the highlighted target.</summary>
        public static void ToggleTargetMenu(WarpDrive drive)
        {
            if (MyAPIGateway.Utilities.IsDedicated || !ready || faulted || menu == null)
                return;

            try
            {
                ToggleTargetMenuUnsafe(drive);
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        private static void ToggleTargetMenuUnsafe(WarpDrive drive)
        {
            if (!HudSettings.Current.Enabled)
            {
                MyAPIGateway.Utilities.ShowNotification("The FSD HUD is off - type /fsd hud on to use the target menu", 3000, "White");
                return;
            }

            if (menu.IsOpen)
            {
                HudSeverity severity;
                string result = menu.Lock(out severity);
                PushEvent(result, severity, 4f);
                if (result.Length > 0 && severity == HudSeverity.Success)
                    HudSounds.Play(HudSounds.TargetSet);
                return;
            }

            var hs = new HyperspaceControls.HyperspaceAdapter(drive);
            if (drive == null)
            {
                PushEvent("NO FSD ON THIS SHIP", HudSeverity.Warning, 3f);
                return;
            }

            // A drive that can't jump gets the same menu for the supercruise assist lock (always allowed, also while cruising)
            if (hs.IsValid && !hs.IsIdle)
            {
                PushEvent("TARGET CAN'T BE CHANGED WHILE THE DRIVE IS ACTIVE", HudSeverity.Warning, 3f);
                return;
            }

            var grid = drive.block?.CubeGrid as MyCubeGrid;
            Vector3D shipPos = grid != null ? grid.PositionComp.GetPosition() : MyAPIGateway.Session.Camera.Position;
            menu.Open(drive, shipPos);
        }

        private static void UpdateTargetMenu(float dt, Vector2 view)
        {
            if (!menu.IsOpen)
                return;

            // Leaving the ship closes it
            WarpDrive drive = menu.Drive;
            if (currentGrid == null || drive?.warpSystem?.grid == null || !drive.warpSystem.grid.Contains(currentGrid))
            {
                menu.Close();
                return;
            }

            // The wheel drives the menu: keep mouse clicks away from the game while it is open
            BindManager.RequestTempBlacklist(SeBlacklistModes.Mouse);
            int steps = WheelSteps();

            menu.Offset = new Vector2(-(AlignmentIndicator.Radius + SidePanelGap) - TargetMenu.PanelWidth * 0.5f, 0f);
            menu.Update(dt, steps);
        }

        /// <summary>Mouse wheel notches this frame: positive is wheel up.</summary>
        private static int WheelSteps()
        {
            int raw = MyAPIGateway.Input.DeltaMouseScrollWheelValue();
            return raw == 0 ? 0 : (Math.Abs(raw) >= 120 ? raw / 120 : Math.Sign(raw));
        }

        // ------------------------------------------------------------------ per frame

        public static void Update()
        {
            if (!initStarted || faulted)
                return;

            try
            {
                UpdateFrame();
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        // Switch the HUD off instead of throwing every frame; giving the mouse back comes first, and nothing here may throw again
        private static void Fault(Exception ex)
        {
            faulted = true;
            VRage.Utils.MyLog.Default.WriteLineAndConsole("[FsdHud] disabled after an error: " + ex);

            try
            {
                UpdateCursor(false);
            }
            catch (Exception)
            {
            }

            try
            {
                HideAll();
            }
            catch (Exception)
            {
            }
        }

        private static void UpdateFrame()
        {
            frame++;
            DateTime now = DateTime.UtcNow;
            float dt = (float)Math.Min(0.1, (now - lastFrame).TotalSeconds);
            lastFrame = now;

            if (frame % GridCheckEveryFrames == 0 || currentGrid != null && currentGrid.MarkedForClose)
            {
                currentGrid = FindLocalGrid();
                gridHasFsd = currentGrid != null && WarpDriveSession.instance != null && WarpDriveSession.instance.GetGridWarpSystem(currentGrid) != null;
            }

            if (!ready)
            {
                CheckMissingFramework(now);
                return;
            }

            if (!HudSettings.Current.Enabled || IsVanillaHudHidden())
            {
                HideAll();
                return;
            }

            float scale = HudSettings.Current.Scale;
            scaledRoot.PlaneScale = scale;
            Vector2 view = HudMain.ScreenDimHighDPI / scale;
            float statusY = -view.Y * 0.5f + BottomMargin + FsdStatusPanel.PanelHeight * 0.5f;
            float statusTop = statusY + FsdStatusPanel.PanelHeight * 0.5f;
            bool slow = frame % SlowEveryFrames == 0;

            bool active = gridHasFsd && currentGrid != null && model.Fill(currentGrid, slow);
            fx.Update(model, active, dt);
            bool statusShown = status.UpdatePresence(active, dt);
            status.Visible = statusShown;
            status.Offset = new Vector2(0f, statusY + status.SlideY) + fx.PanelOffset + fx.ShakeOffset;
            if (active)
            {
                status.Update(model, dt, slow);
                Vector2 reticleAt = UpdateHeadingOffset(view * 0.5f, dt) + fx.ShakeOffset;

                bool showAlign = model.HasAlign || model.BlindJump || model.Phase == HudPhase.Countdown;
                align.Visible = showAlign;
                if (showAlign)
                {
                    align.Offset = reticleAt;
                    align.SetModel(model, dt);
                }
                else
                {
                    align.ResetState();
                }

                bool showFlight = !showAlign && FlightIndicator.WantsToShow(model);
                flight.Visible = showFlight;
                if (showFlight)
                {
                    flight.Offset = reticleAt;
                    flight.SetModel(model, dt);
                }
                else
                {
                    flight.ResetState();
                }

                double gpsDistance = model.HasGpsLock ? Vector3D.Distance(model.GpsCoords, currentGrid.PositionComp.GetPosition()) : 0.0;
                gpsLock.Update(model, view * 0.5f, gpsDistance);

                // Supercruise assist: within 500 m of the locked point the lock resets
                if (model.HasGpsLock && model.Drive == HudDrive.Supercruise && gpsDistance < CruiseLock.ReachedMeters)
                    ReleaseReachedTarget(model.GpsName);

                // The wheel pages through a big fleet while the lobby has the cursor (the target menu keeps it when open)
                if (lobby.Interactive && !menu.IsOpen)
                    lobby.Scroll(WheelSteps());
                lobby.Update(model, slow, AlignmentIndicator.Radius + SidePanelGap, 0f);
                UpdatePing(dt);
            }
            else
            {
                align.Visible = false;
                align.ResetState();
                gpsLock.Visible = false;
                flight.Visible = false;
                flight.ResetState();
                lobby.Visible = false;
                lobby.Close();
                ResetPing();
            }

            UpdateCursor(lobby.Visible && lobby.Interactive);
            flowFx.Update(model, active, dt, view);
            glitch.Update();

            UpdateTargetMenu(dt, view);
            feed.Update(dt, statusTop + 10f + EventFeed.LineHeight * 0.5f + fx.PanelOffset.Y + fx.ShakeOffset.Y);
            danger.Update(dt, view.Y * 0.5f - 120f);
        }

        /// <summary>
        /// Where the reticle sits: the screen centre in first person; in third person the point the ship is heading for (straight
        /// ahead of the seat, at infinity), so it lines up with the target marker exactly as it does from the cockpit.
        /// </summary>
        private static Vector2 UpdateHeadingOffset(Vector2 halfView, float dt)
        {
            Vector2 target = Vector2.Zero;
            var session = MyAPIGateway.Session;
            var seat = session?.ControlledObject as IMyShipController;
            var camera = session?.Camera;

            var view = session?.CameraController;
            if (seat != null && camera != null && view != null && ReferenceEquals(view, seat) && !view.IsInFirstPersonView)
            {
                Vector3D v = Vector3D.TransformNormal(seat.WorldMatrix.Forward, camera.ViewMatrix);
                if (v.Z < -0.05)
                {
                    MatrixD proj = camera.ProjectionMatrix;
                    float x = (float)(proj.M11 * v.X / -v.Z) * halfView.X;
                    float y = (float)(proj.M22 * v.Y / -v.Z) * halfView.Y;
                    float marginX = Math.Max(0f, halfView.X - AlignmentIndicator.Radius - 40f);
                    float marginY = Math.Max(0f, halfView.Y - AlignmentIndicator.Radius - 40f);
                    target = new Vector2(MathHelper.Clamp(x, -marginX, marginX), MathHelper.Clamp(y, -marginY, marginY));
                }
            }

            // Eased, so switching views glides instead of jumping and the third-person camera's wobble is not copied
            headingOffset = Vector2.Lerp(headingOffset, target, 1f - (float)Math.Exp(-15.0 * dt));
            return headingOffset;
        }

        private static void ReleaseReachedTarget(string name)
        {
            WarpSystem ws = WarpDriveSession.instance != null ? WarpDriveSession.instance.GetGridWarpSystem(currentGrid) : null;
            CruiseLock.Clear(ws);

            PushEventUnsafe("TARGET REACHED: " + name, HudSeverity.Success, 4f);
            fx.Trigger(0.6f, HudPalette.Ready, true);
            HudSounds.Play(HudSounds.TargetReached);
        }

        private static void HideAll()
        {
            if (status != null)
            {
                status.Visible = false;
                status.HidePresence();
            }
            if (align != null) align.Visible = false;
            if (lobby != null)
            {
                lobby.Visible = false;
                lobby.Close();
            }
            if (flight != null)
            {
                flight.Visible = false;
                flight.ResetState();
            }
            if (fx != null) fx.Hide();
            if (flowFx != null) flowFx.Hide();
            if (glitch != null) glitch.Visible = false;
            UpdateCursor(false);
            if (gpsLock != null) gpsLock.Visible = false;
            if (menu != null) menu.Close();
            if (feed != null) feed.Clear();
            if (danger != null) danger.Hide();
            ResetPing();
        }

        /// <summary>Mouse cursor for the interactive fleet lobby; mouse look and clicks are kept from the game while it is up.</summary>
        private static void UpdateCursor(bool wanted)
        {
            if (wanted)
                BindManager.RequestTempBlacklist(SeBlacklistModes.MouseAndCam);

            if (wanted == cursorOn)
                return;

            cursorOn = wanted;
            HudMain.EnableCursor = wanted;
        }

        private static bool IsVanillaHudHidden()
        {
            var config = MyAPIGateway.Session?.Config;
            return config != null && config.HudState == 0;
        }

        private static MyCubeGrid FindLocalGrid()
        {
            IMyPlayer player = MyAPIGateway.Session?.Player;
            if (player == null)
                return null;

            var block = player.Controller?.ControlledEntity?.Entity as IMyCubeBlock ?? player.Character?.Parent as IMyCubeBlock;
            return block?.CubeGrid as MyCubeGrid;
        }

        private static void CheckMissingFramework(DateTime now)
        {
            if (warnedMissing || frame % 60 != 0 || (now - initTime).TotalSeconds < RegistrationGiveUpSeconds)
                return;

            // Only bother a player who actually sits in a ship with a drive
            if (!gridHasFsd)
                return;

            warnedMissing = true;
            pending.Clear();
            MyAPIGateway.Utilities.ShowNotification("FSD HUD requires the Rich HUD Master mod - install it from the Steam Workshop", 8000, "Red");
        }

        // ------------------------------------------------------------------ alignment ping

        private static void ResetPing()
        {
            pingTimer = 0f;
            secondPingTimer = 0f;
            wasAligned = false;
        }

        private static void UpdatePing(float dt)
        {
            FsdHudModel m = model;
            bool wantsAlign = m.HasAlign && (m.Phase == HudPhase.Hold || m.Phase == HudPhase.FleetHold || (m.Phase == HudPhase.Charging && !m.IsAligned));

            if (!HudSettings.Current.Ping || !wantsAlign)
            {
                // Lock reached by the countdown starting: no further pings
                if (m.Phase != HudPhase.Countdown)
                    ResetPing();
                return;
            }

            if (secondPingTimer > 0f)
            {
                secondPingTimer -= dt;
                if (secondPingTimer <= 0f)
                    Ping();
            }

            if (m.IsAligned)
            {
                if (!wasAligned)
                {
                    // Lock: a quick double ping
                    Ping();
                    secondPingTimer = 0.14f;
                }
                wasAligned = true;
                return;
            }

            wasAligned = false;
            pingTimer -= dt;
            if (pingTimer <= 0f)
            {
                Ping();
                // 1.2 s between pings when far off, 0.15 s when nearly on target
                float far = MathHelper.Clamp((m.AlignAngle - m.AlignTolerance) / (90f - m.AlignTolerance), 0f, 1f);
                pingTimer = MathHelper.Lerp(0.15f, 1.2f, far);
            }
        }

        private static void Ping()
        {
            try
            {
                if (pingEmitter == null)
                    pingEmitter = new MyEntity3DSoundEmitter(null) { CanPlayLoopSounds = false };

                var camera = MyAPIGateway.Session?.Camera;
                if (camera != null)
                    pingEmitter.SetPosition(camera.Position);

                pingEmitter.PlaySound(WarpSound.hudBleep, true, force2D: true, alwaysHearOnRealistic: true);
            }
            catch (Exception)
            {
            }
        }
    }
}
