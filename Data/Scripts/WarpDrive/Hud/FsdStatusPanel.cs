using System;
using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// The bottom strip. Row 1: drive, phase and target. Row 2: the segmented progress bar with the time. Row 3: up to five stats
    /// (capacitor, heat, range or speed, power, gravity, hyperdictor). Texts are rebuilt about ten times a second, bars every frame.
    /// </summary>
    public class FsdStatusPanel : HudElementBase
    {
        public const float PanelWidth = 660f;
        public const float PanelHeight = 92f;
        private const float Pad = 14f;
        private const int SlotCount = 5;
        private const float SlotWidth = 120f;
        private const float SlotGap = 8f;
        private const float TimeWidth = 156f;   // room for "ETA 1234.56" at its pulsing size

        private readonly TexturedBox back;

        // Hyperdiction RGB split: a red and a cyan ghost behind each top-row label, pushed apart sideways
        private static readonly Color GhostRed = new Color(255, 50, 60);
        private static readonly Color GhostCyan = new Color(60, 230, 255);
        private readonly Label[] phaseGhost = new Label[2];
        private readonly Label[] targetGhost = new Label[2];
        private readonly Label[] timeGhost = new Label[2];
        private int lastGlitchFrame = -1;
        private bool wasGlitching;

        private readonly Label phaseLabel;
        private readonly Label targetLabel;
        private readonly HudBar mainBar;
        private readonly Label timeLabel;

        private readonly Label[] slotLabels = new Label[SlotCount];
        private readonly HudBar[] slotBars = new HudBar[SlotCount];
        private readonly string[] slotName = new string[SlotCount];
        private readonly string[] slotValue = new string[SlotCount];
        private readonly float[] slotFraction = new float[SlotCount];
        private readonly Color[] slotColor = new Color[SlotCount];
        private int slotCount;

        // Resting positions of the top-row labels (the glitch shoves them sideways for a moment)
        private readonly Vector2 phaseBase;
        private readonly Vector2 targetBase;
        private readonly Vector2 timeBase;

        // Border (top, bottom, left, right) and the light sweep that runs along the top edge
        private readonly TexturedBox[] border = new TexturedBox[4];
        private readonly TexturedBox sweep;
        private float sweepT;

        // Appearing and disappearing: the panel slides up into place with a scan line, and slides back down when the HUD goes quiet
        private readonly TexturedBox scan;
        private float presence;
        private bool wasPresent;
        private float scanT = -1f;
        private Color edgeTop;
        private Color edgeSide;

        private HudPhase lastPhase = (HudPhase)(-1);
        private HudDrive lastDrive = HudDrive.None;
        private float clock;
        private float frameDt;

        /// <summary>0 hidden .. 1 fully shown; the panel is still drawn while this is above 0.</summary>
        public float Presence { get { return presence; } }

        /// <summary>Vertical offset of the slide: 0 when shown, below its place while it comes or goes.</summary>
        public float SlideY { get { return -(1f - presence * presence * (3f - 2f * presence)) * 46f; } }

        /// <summary>Shared HUD effects (surge, flash); set by FsdHud.</summary>
        public HudFx Fx;

        public FsdStatusPanel(HudParentBase parent) : base(parent)
        {
            Size = new Vector2(PanelWidth, PanelHeight);
            back = new TexturedBox(this) { Size = Size, Color = HudPalette.Panel };
            for (int i = 0; i < border.Length; i++)
                border[i] = new TexturedBox(this);
            sweep = new TexturedBox(this) { Visible = false };
            scan = new TexturedBox(this) { Visible = false };

            phaseBase = new Vector2(-PanelWidth * 0.5f + Pad + 150f, 30f);
            targetBase = new Vector2(PanelWidth * 0.5f - Pad - 165f, 30f);
            timeBase = new Vector2(PanelWidth * 0.5f - Pad - TimeWidth * 0.5f, 6f);

            // Created before the real labels so they are drawn underneath them
            for (int g = 0; g < 2; g++)
            {
                phaseGhost[g] = MakeLabel(new Vector2(300f, 20f), phaseBase);
                targetGhost[g] = MakeLabel(new Vector2(330f, 20f), targetBase);
                timeGhost[g] = MakeLabel(new Vector2(TimeWidth, 20f), timeBase);
                phaseGhost[g].Visible = targetGhost[g].Visible = timeGhost[g].Visible = false;
            }
            phaseLabel = MakeLabel(new Vector2(300f, 20f), phaseBase);
            targetLabel = MakeLabel(new Vector2(330f, 20f), targetBase);

            float barWidth = PanelWidth - 2f * Pad - TimeWidth - 8f;
            mainBar = new HudBar(this, barWidth, 14f, 20) { Offset = new Vector2(-PanelWidth * 0.5f + Pad + barWidth * 0.5f, 6f) };
            timeLabel = MakeLabel(new Vector2(TimeWidth, 20f), timeBase);

            for (int i = 0; i < SlotCount; i++)
            {
                slotLabels[i] = MakeLabel(new Vector2(SlotWidth, 16f), new Vector2(0f, -22f));
                slotBars[i] = new HudBar(this, SlotWidth - 4f, 5f) { Offset = new Vector2(0f, -35f) };
            }
            LayoutSlots();
        }

        private Label MakeLabel(Vector2 size, Vector2 offset)
        {
            return new Label(this)
            {
                AutoResize = false,
                Size = size,
                Offset = offset,
                BuilderMode = TextBuilderModes.Unlined,
                VertCenterText = true
            };
        }

        public static Color PhaseColor(FsdHudModel m)
        {
            switch (m.Phase)
            {
                case HudPhase.Hyperdicted: return HudPalette.Danger;
                case HudPhase.Hold:
                case HudPhase.SpoolDelayed: return HudPalette.Warning;
                case HudPhase.FleetHold: return m.IsAligned ? HudPalette.Ready : HudPalette.Warning;
                case HudPhase.Countdown: return HudPalette.Ready;
                case HudPhase.Idle: return string.IsNullOrEmpty(m.Note) ? HudPalette.Ready : HudPalette.Warning;
                default: return HudPalette.Primary;
            }
        }

        /// <summary>Called every frame, also while the HUD is quiet, so the panel can slide out. Returns true while the panel should still be drawn.</summary>
        public bool UpdatePresence(bool present, float dt)
        {
            bool effects = HudSettings.Current.Effects;
            if (!effects)
                presence = present ? 1f : 0f;
            else
                presence = MathHelper.Clamp(presence + (present ? dt / 0.3f : -dt / 0.22f), 0f, 1f);

            if (present && !wasPresent && effects)
                scanT = 0f;
            wasPresent = present;

            if (scanT >= 0f)
            {
                scanT += dt / 0.4f;
                if (scanT >= 1f || !effects)
                    scanT = -1f;
            }

            if (scanT >= 0f)
            {
                // A bright vertical line wipes across the panel from left to right, fading as it goes
                scan.Visible = true;
                scan.Size = new Vector2(3f, PanelHeight);
                scan.Offset = new Vector2(-PanelWidth * 0.5f + PanelWidth * scanT, 0f);
                scan.Color = HudPalette.Fade(Color.Lerp(HudPalette.Primary, Color.White, 0.7f), (1f - scanT) * 0.85f);
            }
            else
            {
                scan.Visible = false;
            }

            ApplyBackAndBorder();
            return presence > 0f;
        }

        /// <summary>The HUD was switched off: the next showing slides in again.</summary>
        public void HidePresence()
        {
            presence = 0f;
            wasPresent = false;
            scanT = -1f;
            scan.Visible = false;
        }

        private static Color ScaleAlpha(Color color, float scale)
        {
            return new Color(color.R, color.G, color.B, (byte)(color.A * MathHelper.Clamp(scale, 0f, 1f)));
        }

        private void ApplyBackAndBorder()
        {
            bool glitching = Fx != null && HudSettings.Current.Effects && Fx.Glitch > 0f;
            back.Color = ScaleAlpha(glitching ? new Color(70, 14, 14, 175) : HudPalette.Panel, presence);
            border[0].Color = border[1].Color = ScaleAlpha(edgeTop, presence);
            border[2].Color = border[3].Color = ScaleAlpha(edgeSide, presence);
        }

        public void Update(FsdHudModel m, float dt, bool slow)
        {
            clock += dt;
            frameDt = dt;

            bool changed = m.Phase != lastPhase || m.Drive != lastDrive;
            if (changed)
            {
                // Entering a new phase: a quick scan line (the first showing already has one from UpdatePresence)
                if (scanT < 0f && lastPhase != (HudPhase)(-1) && HudSettings.Current.Effects)
                    scanT = 0f;

                lastPhase = m.Phase;
                lastDrive = m.Drive;
            }

            // A Hyperdiction corrupts the texts: rebuild them with each glitch frame, and once more when it ends to clean them up
            bool glitching = HudSettings.Current.Effects && HudGlitch.Active;
            bool glitchFrame = glitching && HudGlitch.Frame != lastGlitchFrame || wasGlitching && !glitching;
            lastGlitchFrame = HudGlitch.Frame;
            wasGlitching = glitching;

            if (slow || changed || glitchFrame)
            {
                BuildSlots(m);
                RebuildTexts(m);
            }
            else if (m.Phase == HudPhase.Transit || m.Phase == HudPhase.Hyperdicted || m.Phase == HudPhase.Cruise && m.HasCruiseEta)
            {
                // The ETA and the distance to go roll every frame instead of ticking ten times a second
                UpdateTimeTexts(m);
            }

            UpdateBars(m);
            ApplyGlitch();
        }

        /// <summary>A Hyperdiction glitch burst shoves the top-row labels sideways by different amounts and tints the panel red for a moment.</summary>
        private void ApplyGlitch()
        {
            float glitch = Fx != null && HudSettings.Current.Effects ? Fx.Glitch : 0f;
            if (glitch > 0f)
            {
                phaseLabel.Offset = phaseBase + new Vector2(Fx.GlitchOffset(1), 0f);
                targetLabel.Offset = targetBase + new Vector2(Fx.GlitchOffset(2), 0f);
                timeLabel.Offset = timeBase + new Vector2(Fx.GlitchOffset(3), 0f);
            }
            else
            {
                phaseLabel.Offset = phaseBase;
                targetLabel.Offset = targetBase;
                timeLabel.Offset = timeBase;
            }

            float split = HudSettings.Current.Effects ? HudGlitch.Split : 0f;
            PlaceGhosts(phaseGhost, phaseLabel.Offset, split, 41);
            PlaceGhosts(targetGhost, targetLabel.Offset, split, 42);
            PlaceGhosts(timeGhost, timeLabel.Offset, split, 43);
            ApplyBackAndBorder();
        }

        private static void PlaceGhosts(Label[] ghosts, Vector2 at, float split, int channel)
        {
            bool on = split >= 0.5f;
            for (int g = 0; g < 2; g++)
            {
                ghosts[g].Visible = on;
                if (on)
                {
                    float jitter = 0.7f + 0.6f * HudGlitch.Unit(channel, g);
                    ghosts[g].Offset = at + new Vector2((g == 0 ? -split : split) * jitter, 0f);
                }
            }
        }

        /// <summary>The same text as the real label in the two ghost colours (only while the HUD is glitching).</summary>
        private static void SetGhosts(Label[] ghosts, string text, float size, TextAlignment align)
        {
            if (!HudGlitch.Active)
                return;

            float alpha = 0.35f + 0.3f * HudGlitch.Level + 0.3f * HudGlitch.Burst;
            ghosts[0].TextBoard.SetText(text, HudPalette.Number(HudPalette.Fade(GhostRed, alpha), size, align));
            ghosts[1].TextBoard.SetText(text, HudPalette.Number(HudPalette.Fade(GhostCyan, alpha), size, align));
        }

        // ------------------------------------------------------------------ content

        private void AddSlot(string name, string value, float fraction, Color color)
        {
            if (slotCount >= SlotCount)
                return;

            slotName[slotCount] = name;
            slotValue[slotCount] = value;
            slotFraction[slotCount] = fraction;
            slotColor[slotCount] = color;
            slotCount++;
        }

        private static Color LevelColor(float fraction, bool highIsBad)
        {
            float bad = highIsBad ? fraction : 1f - fraction;
            if (bad >= 0.85f)
                return HudPalette.Danger;
            if (bad >= 0.65f)
                return HudPalette.Warning;
            return HudPalette.Primary;
        }

        private static string ApparentSpeed(FsdHudModel m)
        {
            return ((long)m.ApparentSpeedKmps).ToString(System.Globalization.CultureInfo.InvariantCulture) + " km/s";
        }

        private void BuildSlots(FsdHudModel m)
        {
            slotCount = 0;

            if (m.HasCapacitor)
                AddSlot("CAP", HudFormat.Percent(m.CapFraction), m.CapFraction, LevelColor(m.CapFraction, false));

            // In supercruise the speed is the top-right reading; only with a locked target (which takes that line) it moves to a slot
            if (m.Drive == HudDrive.Supercruise && m.Phase == HudPhase.Cruise && m.HasTarget)
                AddSlot("SPD", HudFormat.Speed(m.SpeedMps), m.SpeedFraction, HudPalette.Primary);

            if (m.HasHeat)
                AddSlot("HEAT", HudFormat.Percent(m.HeatFraction), m.HeatFraction, LevelColor(m.HeatFraction, true));

            if (m.Drive == HudDrive.Hyperspace && m.HasCapacitor)
            {
                if (m.InTransit)
                    AddSlot("SPD", ApparentSpeed(m), -1f, HudPalette.Primary);
                else
                    AddSlot("RANGE", HudFormat.Distance(m.RangeMeters), -1f, HudPalette.Primary);
            }
            else if (m.Drive == HudDrive.Capital && m.InTransit)
            {
                AddSlot("SPD", ApparentSpeed(m), -1f, HudPalette.Primary);
            }

            if (m.HasPower)
                AddSlot("PWR", HudFormat.Number(m.PowerMW, "0.0") + " MW", -1f, HudPalette.Primary);

            if (m.Drive == HudDrive.Supercruise && m.GravityFactor > 0.02f)
                AddSlot("GRAV", HudFormat.Percent(m.GravityFactor), m.GravityFactor, m.GravityFactor > 0.6f ? HudPalette.Danger : HudPalette.Warning);

            if (m.HasHyperdictor)
                AddHyperdictorSlot(m);
        }

        private void AddHyperdictorSlot(FsdHudModel m)
        {
            switch (m.HyperdictorState)
            {
                case HyperdictorState.Engaged:
                    AddSlot("HDICT", "ENGAGED", 1f, HudPalette.Ready);
                    break;
                case HyperdictorState.CoolingDown:
                    float cool = 1f - m.HyperdictorCooldownTicks / (float)HyperdictorBlock.COOLDOWN_TOTAL_TICKS;
                    int secs = m.HyperdictorCooldownTicks / 60;
                    AddSlot("HDICT", (secs / 60).ToString("00") + ":" + (secs % 60).ToString("00"), cool, HudPalette.Warning);
                    break;
                case HyperdictorState.Charged:
                    AddSlot("HDICT", "READY", 1f, HudPalette.Primary);
                    break;
                case HyperdictorState.Disrupted:
                    AddSlot("HDICT", "DISRUPTED", m.HyperdictorCharge, HudPalette.Danger);
                    break;
                default:
                    AddSlot("HDICT", HudFormat.Percent(m.HyperdictorCharge), m.HyperdictorCharge, HudPalette.Primary);
                    break;
            }
        }

        private void RebuildTexts(FsdHudModel m)
        {
            Color phaseColor = PhaseColor(m);

            string driveName = HudGlitch.Corrupt(m.DriveName, 1);
            string phaseText = HudGlitch.Corrupt(m.PhaseText(), 2);
            var phase = new RichText();
            phase.Add(driveName + "  ", HudPalette.Text(HudPalette.Dim, 0.85f));
            phase.Add(phaseText, HudPalette.Text(phaseColor, 0.95f));
            phaseLabel.TextBoard.SetText(phase);
            SetGhosts(phaseGhost, driveName + "  " + phaseText, 0.9f, TextAlignment.Left);

            UpdateTimeTexts(m);

            for (int i = 0; i < SlotCount; i++)
            {
                if (i >= slotCount)
                {
                    slotLabels[i].Visible = false;
                    continue;
                }

                var rt = new RichText();
                rt.Add(slotName[i] + " ", HudPalette.Text(HudPalette.Dim, 0.68f));
                rt.Add(HudGlitch.Corrupt(slotValue[i], 10 + i), HudPalette.Number(slotColor[i], 0.76f));
                slotLabels[i].TextBoard.SetText(rt);
                slotLabels[i].Visible = true;
            }

            LayoutSlots();
        }

        /// <summary>
        /// The target line and the time. In transit both show their full resolution (ETA to a hundredth, distance to the kilometre) so the
        /// last digits roll; in the last seconds the ETA turns amber and pulses faster the closer the arrival.
        /// </summary>
        private void UpdateTimeTexts(FsdHudModel m)
        {
            Color phaseColor = PhaseColor(m);
            string target = HudGlitch.Corrupt(TargetText(m), 3);
            targetLabel.TextBoard.SetText(target, HudPalette.Number(HudPalette.Primary, 0.8f, TextAlignment.Right));
            SetGhosts(targetGhost, target, 0.8f, TextAlignment.Right);

            string time = "";
            Color timeColor = phaseColor;
            float size = 0.9f;
            if ((m.Phase == HudPhase.Transit || m.Phase == HudPhase.Hyperdicted) && m.HasSeconds)
            {
                time = "ETA " + HudFormat.SecondsPrecise(m.SecondsLeft);

                float ramp = HudSettings.Current.Effects ? m.ArrivalRamp : 0f;
                if (ramp > 0f)
                {
                    float pulse = 0.5f + 0.5f * (float)Math.Sin(clock * (6.0 + 16.0 * ramp));
                    timeColor = Color.Lerp(Color.Lerp(phaseColor, HudPalette.Warning, Math.Min(1f, ramp * 2f)), Color.White, pulse * ramp * 0.55f);
                    size = 0.9f + 0.08f * ramp * pulse;
                }
            }
            else if (m.Phase == HudPhase.Cruise && m.HasCruiseEta)
            {
                // Supercruise assist: the time to the locked point, turning into a braking call
                float pulse = 0.5f + 0.5f * (float)Math.Sin(clock * 12.0);
                if (m.BrakeLevel == 2)
                {
                    time = "BRAKE NOW";
                    timeColor = Color.Lerp(HudPalette.Danger, Color.White, pulse * 0.5f);
                    size = 0.9f + 0.08f * pulse;
                }
                else if (m.BrakeLevel == 1)
                {
                    time = "SLOW DOWN";
                    timeColor = HudPalette.Warning;
                }
                else
                {
                    time = "ETA " + HudFormat.Seconds(m.CruiseEtaSeconds);
                }
            }
            else if (m.HasSeconds && m.Phase != HudPhase.Hyperdicted)
            {
                time = HudFormat.Seconds(m.SecondsLeft);
            }
            time = HudGlitch.Corrupt(time, 4);
            timeLabel.TextBoard.SetText(time, HudPalette.Number(timeColor, size, TextAlignment.Right));
            SetGhosts(timeGhost, time, size, TextAlignment.Right);
        }

        // Slots are left-packed; one with a long reading (the transit speed in km/s) takes more room instead of being cut off
        private void LayoutSlots()
        {
            const int longChars = 11;
            const float longWeight = 1.75f;

            float weightSum = 0f;
            for (int i = 0; i < slotCount; i++)
                weightSum += SlotWeight(i, longChars, longWeight);

            float available = PanelWidth - 2f * Pad - Math.Max(0, slotCount - 1) * SlotGap;
            float unit = weightSum > 0f ? Math.Min(SlotWidth, available / weightSum) : SlotWidth;

            float x = -PanelWidth * 0.5f + Pad;
            for (int i = 0; i < slotCount; i++)
            {
                float width = SlotWeight(i, longChars, longWeight) * unit;
                float center = x + width * 0.5f;
                slotLabels[i].Size = new Vector2(width, 16f);
                slotLabels[i].Offset = new Vector2(center, -22f);
                slotBars[i].SetWidth(width - 4f);
                slotBars[i].Offset = new Vector2(center, -35f);
                x += width + SlotGap;
            }
        }

        private float SlotWeight(int i, int longChars, float longWeight)
        {
            return slotName[i].Length + 1 + slotValue[i].Length > longChars ? longWeight : 1f;
        }

        private static string TargetText(FsdHudModel m)
        {
            if (m.Phase == HudPhase.Cruise)
            {
                // A locked GPS point takes the line (the speed moves to a slot); without one it shows the speed
                if (m.HasTarget)
                    return m.TargetName + "  " + HudFormat.Distance(m.TargetDistance);

                return "SPEED " + HudFormat.Speed(m.SpeedMps);
            }

            if (!m.HasTarget)
                return "";

            if (m.Phase == HudPhase.Transit || m.Phase == HudPhase.Hyperdicted)
                return m.TargetName + "  " + HudFormat.DistanceExact(m.TargetDistance) + " TO GO";

            if (m.TargetDistance > 1.0)
                return m.TargetName + "  " + HudFormat.Distance(m.TargetDistance);

            return m.TargetName;
        }

        // ------------------------------------------------------------------ border surge (every frame)

        private float SurgeLevel()
        {
            return Fx != null && HudSettings.Current.Effects ? Fx.Surge : 0f;
        }

        /// <summary>The panel's frame brightens, thickens and flashes with the phase's intensity; a light sweep runs along the top edge faster the stronger it is.</summary>
        private void UpdateBorder(FsdHudModel m, Color phaseColor, float surge)
        {
            float flash = Fx != null && HudSettings.Current.Effects ? Fx.Flash : 0f;
            Color edge = Color.Lerp(Color.Lerp(phaseColor, Fx != null ? Fx.Tint : phaseColor, 0.5f), Color.White, MathHelper.Clamp(flash, 0f, 1f) * 0.85f);

            // Heat tints the frame from cyan through amber to red, and the frame shimmers close to overheating
            float heat = m.HasHeat ? m.HeatFraction : 0f;
            float thermal = MathHelper.Clamp((heat - 0.5f) / 0.35f, 0f, 1f);
            float shimmer = 1f;
            if (thermal > 0f)
            {
                Color hot = Color.Lerp(HudPalette.Warning, HudPalette.Danger, MathHelper.Clamp((heat - 0.65f) / 0.25f, 0f, 1f));
                edge = Color.Lerp(edge, hot, thermal);
                surge = Math.Max(surge, thermal * 0.6f);
                if (heat > 0.85f)
                    shimmer = 0.8f + 0.2f * (float)Math.Sin(clock * 20.0);
            }

            float alpha = MathHelper.Clamp((0.3f + 0.6f * surge + 0.5f * flash) * shimmer, 0f, 1f);
            float t = 1.5f + 2.5f * surge + 1.5f * flash;

            border[0].Size = new Vector2(PanelWidth, t);
            border[1].Size = new Vector2(PanelWidth, t);
            border[2].Size = new Vector2(t, PanelHeight);
            border[3].Size = new Vector2(t, PanelHeight);
            border[0].Offset = new Vector2(0f, PanelHeight * 0.5f - t * 0.5f);
            border[1].Offset = new Vector2(0f, -PanelHeight * 0.5f + t * 0.5f);
            border[2].Offset = new Vector2(-PanelWidth * 0.5f + t * 0.5f, 0f);
            border[3].Offset = new Vector2(PanelWidth * 0.5f - t * 0.5f, 0f);

            // Sides fainter than the top and bottom so the frame reads as accent lines, not a box
            edgeTop = HudPalette.Fade(edge, alpha);
            edgeSide = HudPalette.Fade(edge, alpha * 0.55f);
            ApplyBackAndBorder();

            if (surge < 0.12f && flash < 0.05f)
            {
                sweep.Visible = false;
                return;
            }

            sweepT = (sweepT + frameDt * (0.25f + 1.6f * surge)) % 1f;
            float length = 70f + 110f * surge;
            sweep.Visible = true;
            sweep.Size = new Vector2(length, t + 1.5f);
            sweep.Offset = new Vector2(-PanelWidth * 0.5f + (PanelWidth - length) * sweepT + length * 0.5f, PanelHeight * 0.5f - (t + 1.5f) * 0.5f);
            sweep.Color = HudPalette.Fade(Color.Lerp(edge, Color.White, 0.6f), presence * (0.35f + 0.55f * surge) * (float)Math.Sin(sweepT * Math.PI));
        }

        // ------------------------------------------------------------------ bars (every frame)

        private void UpdateBars(FsdHudModel m)
        {
            Color phaseColor = PhaseColor(m);
            float pulse = 0.6f + 0.4f * (float)Math.Sin(clock * 5.0);

            switch (m.Phase)
            {
                case HudPhase.Hold:
                case HudPhase.FleetHold:
                    mainBar.Set(1f, phaseColor, pulse * 0.6f);
                    break;
                case HudPhase.Cooldown:
                    mainBar.Set(m.Progress, HudPalette.Dim);
                    break;
                case HudPhase.Idle:
                    // Jump drives: the capacitor, green once full. Supercruise-only drives: the heat still to cool off (empty when cold)
                    if (m.HasCapacitor)
                        mainBar.Set(m.CapFraction, m.CapFraction >= 0.999f ? HudPalette.Ready : HudPalette.Warning, 0.5f);
                    else
                        mainBar.Set(m.HasHeat ? m.HeatFraction : 0f, HudPalette.Dim, 0.5f);
                    break;
                case HudPhase.Cruise:
                    // With a locked target the bar fills from where it was locked to the target, otherwise it is the speed
                    mainBar.Set(m.HasCruiseTrip ? m.CruiseTripProgress : m.SpeedFraction, phaseColor);
                    break;
                default:
                    mainBar.Set(m.HasProgress ? m.Progress : 0f, phaseColor);
                    break;
            }

            bool flowing = m.Phase == HudPhase.Spool || m.Phase == HudPhase.Charging || m.Phase == HudPhase.Countdown
                || m.Phase == HudPhase.Engaging || m.Phase == HudPhase.Transit || m.Phase == HudPhase.Exiting;
            float surge = SurgeLevel();
            mainBar.Animate(frameDt, flowing, phaseColor, 1f + 2.5f * surge, 0.6f + 0.9f * surge);
            UpdateBorder(m, phaseColor, surge);

            for (int i = 0; i < SlotCount; i++)
            {
                slotBars[i].Visible = i < slotCount && slotFraction[i] >= 0f;
                if (slotBars[i].Visible)
                    slotBars[i].Set(slotFraction[i], slotColor[i]);
            }
        }
    }
}
