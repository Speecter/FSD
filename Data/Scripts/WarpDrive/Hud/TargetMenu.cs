using System;
using System.Collections.Generic;
using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Jump target picker opened from the "Jump Target" toolbar action: a blind-jump entry plus every GPS point, nearest first,
    /// each marked in range / too close / out of range. The mouse wheel moves the highlight, pressing the action again locks it.
    /// </summary>
    public class TargetMenu : HudElementBase
    {
        public const float PanelWidth = 400f;
        private const int VisibleRows = 8;
        private const float RowHeight = 22f;
        private const float HeaderHeight = 30f;
        private const float FooterHeight = 34f;
        private const float Pad = 8f;
        private const float IdleCloseSeconds = 20f;

        private readonly TexturedBox back;
        private readonly TexturedBox accent;
        private readonly Label header;
        private readonly Label footer;
        private readonly TexturedBox highlight;
        private readonly Label[] names = new Label[VisibleRows];
        private readonly Label[] distances = new Label[VisibleRows];
        private readonly Label[] states = new Label[VisibleRows];
        private readonly Label moreAbove;
        private readonly Label moreBelow;

        private readonly List<JumpTarget> entries = new List<JumpTarget>();
        private WarpDrive drive;
        private int selected;
        private int firstShown = -1;
        private int shownSelected = -1;
        private float idle;

        public bool IsOpen { get; private set; }
        public WarpDrive Drive { get { return drive; } }

        /// <summary>The drive can't jump: the menu sets the supercruise assist lock instead of a jump target.</summary>
        private bool cruise;

        public TargetMenu(HudParentBase parent) : base(parent)
        {
            float height = HeaderHeight + VisibleRows * RowHeight + FooterHeight + 2f * Pad;
            Size = new Vector2(PanelWidth, height);

            back = new TexturedBox(this) { Size = Size, Color = HudPalette.Panel };
            accent = new TexturedBox(this) { Size = new Vector2(PanelWidth, 2f), Offset = new Vector2(0f, height * 0.5f - 1f), Color = HudPalette.Primary };

            float top = height * 0.5f - Pad;
            header = MakeLabel(PanelWidth - 2f * Pad, new Vector2(0f, top - HeaderHeight * 0.5f));
            header.TextBoard.SetText("SELECT JUMP TARGET", HudPalette.Text(HudPalette.Primary, 0.95f));

            highlight = new TexturedBox(this) { Size = new Vector2(PanelWidth - 2f * Pad, RowHeight), Color = new Color(90, 190, 255, 55) };

            for (int i = 0; i < VisibleRows; i++)
            {
                float y = RowY(i);
                names[i] = MakeLabel(210f, new Vector2(-PanelWidth * 0.5f + Pad + 6f + 105f, y));
                distances[i] = MakeLabel(80f, new Vector2(PanelWidth * 0.5f - Pad - 96f - 40f, y));
                states[i] = MakeLabel(96f, new Vector2(PanelWidth * 0.5f - Pad - 48f, y));
            }

            moreAbove = MakeLabel(60f, new Vector2(PanelWidth * 0.5f - Pad - 30f, top - HeaderHeight * 0.5f));
            moreBelow = MakeLabel(60f, new Vector2(PanelWidth * 0.5f - Pad - 30f, RowY(VisibleRows - 1) - RowHeight));

            footer = MakeLabel(PanelWidth - 2f * Pad, new Vector2(0f, -height * 0.5f + Pad + FooterHeight * 0.5f));
            footer.TextBoard.SetText("SCROLL TO CHOOSE  -  PRESS JUMP TARGET AGAIN TO LOCK", HudPalette.Text(HudPalette.Dim, 0.72f, TextAlignment.Center));

            Visible = false;
        }

        private float RowY(int row)
        {
            return Height * 0.5f - Pad - HeaderHeight - RowHeight * (row + 0.5f);
        }

        private Label MakeLabel(float width, Vector2 offset)
        {
            return new Label(this)
            {
                AutoResize = false,
                Size = new Vector2(width, RowHeight),
                Offset = offset,
                BuilderMode = TextBuilderModes.Unlined,
                VertCenterText = true
            };
        }

        // ------------------------------------------------------------------ open / lock

        public void Open(WarpDrive targetDrive, Vector3D shipPos)
        {
            drive = targetDrive;
            var hs = new HyperspaceControls.HyperspaceAdapter(drive);
            cruise = !hs.IsValid;
            header.TextBoard.SetText(cruise ? "SELECT CRUISE TARGET" : "SELECT JUMP TARGET", HudPalette.Text(HudPalette.Primary, 0.95f));

            if (cruise)
            {
                JumpTargets.Build(entries, shipPos, 0.0, double.MaxValue, 0.0, true);

                Vector3D lockCoords;
                string lockName;
                selected = CruiseLock.TryGet(drive != null ? drive.warpSystem : null, out lockCoords, out lockName) ? JumpTargets.IndexOf(entries, lockCoords) : 0;
            }
            else
            {
                BuildEntries(hs, shipPos);

                // Start on the current target
                selected = JumpTargets.IndexOf(entries, hs.Mode == HyperspaceSystem.JumpMode.GpsWaypoint ? hs.SelectedGpsCoords : null);
            }

            idle = 0f;
            firstShown = shownSelected = -1;
            IsOpen = true;
            Visible = true;
            Refresh();
        }

        public void Close()
        {
            IsOpen = false;
            Visible = false;
            drive = null;
        }

        /// <summary>Applies the highlighted entry to the drive. Returns the event text to show.</summary>
        public string Lock(out HudSeverity severity)
        {
            severity = HudSeverity.Success;
            if (!IsOpen || drive == null || entries.Count == 0)
            {
                Close();
                severity = HudSeverity.Warning;
                return "";
            }

            var hs = new HyperspaceControls.HyperspaceAdapter(drive);
            JumpTarget e = entries[MathHelper.Clamp(selected, 0, entries.Count - 1)];
            WarpSystem cruiseSystem = drive.warpSystem;
            Close();

            if (!hs.IsValid)
                return LockCruise(cruiseSystem, e, out severity);

            if (!hs.IsIdle)
            {
                severity = HudSeverity.Warning;
                return "TARGET NOT CHANGED - DRIVE IS ACTIVE";
            }

            if (e.Blind)
            {
                hs.Mode = HyperspaceSystem.JumpMode.ManualDistance;
                hs.SelectedGpsCoords = null;
                hs.SelectedGpsName = string.Empty;
                return "BLIND JUMP SELECTED - " + HudFormat.Distance(e.Distance) + " ALONG THE NOSE";
            }

            hs.Mode = HyperspaceSystem.JumpMode.GpsWaypoint;
            hs.SelectedGpsCoords = e.Coords;
            hs.SelectedGpsName = e.Name;
            if (e.InRange)
                return "TARGET LOCKED: " + e.Name + "  " + HudFormat.Distance(e.Distance);

            severity = HudSeverity.Warning;
            return "TARGET LOCKED: " + e.Name + " (" + e.Status + ")";
        }

        /// <summary>Supercruise-only drive: sets or clears the assist lock. It may be changed at any time, also while cruising.</summary>
        private static string LockCruise(WarpSystem ws, JumpTarget e, out HudSeverity severity)
        {
            severity = HudSeverity.Success;
            if (ws == null)
            {
                severity = HudSeverity.Warning;
                return "TARGET NOT CHANGED";
            }

            if (e.Blind)
            {
                CruiseLock.Clear(ws);
                return "CRUISE TARGET CLEARED";
            }

            if (e.Distance < CruiseLock.ReachedMeters)
            {
                severity = HudSeverity.Warning;
                return "ALREADY WITHIN 500 M OF " + e.Name;
            }

            CruiseLock.Set(ws, e.Coords, e.Name);
            return "CRUISE TARGET LOCKED: " + e.Name + "  " + HudFormat.Distance(e.Distance);
        }

        private void BuildEntries(HyperspaceControls.HyperspaceAdapter hs, Vector3D shipPos)
        {
            float power = hs.GetDrivePowerMW();
            double minRange = hs.cap != null ? CapitalFSD.MIN_CAPITAL_JUMP_DISTANCE : HyperspaceSystem.MIN_JUMP_DISTANCE;
            JumpTargets.Build(entries, shipPos, minRange, hs.GetMaxJumpDistance(power), hs.GetCurrentManualDistance(power));
        }

        // ------------------------------------------------------------------ per frame

        /// <param name="scrollSteps">Wheel notches this frame: positive moves up the list.</param>
        /// <returns>False when the menu closed itself (timed out).</returns>
        public bool Update(float dt, int scrollSteps)
        {
            if (!IsOpen)
                return false;

            if (scrollSteps != 0 && entries.Count > 0)
            {
                selected = MathHelper.Clamp(selected - scrollSteps, 0, entries.Count - 1);
                idle = 0f;
            }
            else
            {
                idle += dt;
                if (idle >= IdleCloseSeconds)
                {
                    Close();
                    return false;
                }
            }

            Refresh();
            return true;
        }

        private void Refresh()
        {
            int count = entries.Count;
            int first = MathHelper.Clamp(selected - VisibleRows / 2, 0, Math.Max(0, count - VisibleRows));
            if (first == firstShown && selected == shownSelected)
                return;

            firstShown = first;
            shownSelected = selected;

            for (int row = 0; row < VisibleRows; row++)
            {
                int i = first + row;
                bool used = i < count;
                names[row].Visible = distances[row].Visible = states[row].Visible = used;
                if (!used)
                    continue;

                JumpTarget e = entries[i];
                bool sel = i == selected;
                string name = e.Name.Length > 24 ? e.Name.Substring(0, 23) + "." : e.Name;

                names[row].TextBoard.SetText((sel ? "> " : "  ") + name, HudPalette.Text(sel ? Color.White : HudPalette.Primary, 0.8f));
                distances[row].TextBoard.SetText(HudFormat.Distance(e.Distance), HudPalette.Number(sel ? Color.White : HudPalette.Dim, 0.76f, TextAlignment.Right));
                states[row].TextBoard.SetText(e.Status, HudPalette.Text(e.StatusColor, 0.68f, TextAlignment.Right));

                if (sel)
                    highlight.Offset = new Vector2(0f, RowY(row));
            }

            moreAbove.Visible = first > 0;
            if (first > 0)
                moreAbove.TextBoard.SetText("+" + first + " UP", HudPalette.Text(HudPalette.Dim, 0.65f, TextAlignment.Right));

            int below = count - (first + VisibleRows);
            moreBelow.Visible = below > 0;
            if (below > 0)
                moreBelow.TextBoard.SetText("+" + below + " DOWN", HudPalette.Text(HudPalette.Dim, 0.65f, TextAlignment.Right));
        }
    }
}
