using System;
using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Drawn around the crosshair while the drive is spinning up: a tick ring that fills with the charge or countdown,
    /// and either an alignment guide (marker for where the target is, arrow for the way to turn) or, for a blind jump,
    /// an aiming reticle with the jump distance. Its texts sit above the ring so nothing below it collides with the strip.
    /// </summary>
    public class AlignmentIndicator : HudElementBase
    {
        public const float Radius = 96f;
        private const int Ticks = 48;
        private const float TickHalfLength = 5f;
        private const float TickHalfWidth = 1.6f;
        private const float MaxMarkerAngle = 60f;   // offsets at or beyond this sit on the ring itself

        private readonly Label offsetLabel;
        private readonly Label hintLabel;
        private readonly CountdownReadout countdown;
        private readonly ReticleIntro intro = new ReticleIntro();

        /// <summary>Shared HUD effects (kick, flash); set by FsdHud.</summary>
        public HudFx Fx;

        private FsdHudModel model;
        private float clock;
        private float flash;
        private int lastDigit;
        private string shownOffset;
        private string shownHint;

        // Lock-on snap: a green ring that closes in on the reticle ring when the nose comes onto the target
        private const float SnapSeconds = 0.5f;
        private float snap;
        private bool wasAligned;

        public AlignmentIndicator(HudParentBase parent) : base(parent)
        {
            Size = new Vector2(Radius * 2f + 40f);

            offsetLabel = MakeLabel(new Vector2(380f, 22f), new Vector2(0f, Radius + 44f));
            hintLabel = MakeLabel(new Vector2(380f, 20f), new Vector2(0f, Radius + 22f));
            countdown = new CountdownReadout(this, new Vector2(0f, Radius + 34f), HudPalette.Ready);   // above the ring
        }

        /// <summary>Called while the indicator is hidden: the next showing starts fresh (a lock that is already there still snaps).</summary>
        public void ResetState()
        {
            wasAligned = false;
            lastDigit = 0;
            snap = 0f;
            intro.Reset();
            countdown.Reset();
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

        public void SetModel(FsdHudModel m, float dt)
        {
            model = m;
            clock += dt;
            intro.Advance(dt);
            if (flash > 0f)
                flash = Math.Max(0f, flash - dt);
            if (snap > 0f)
                snap = Math.Max(0f, snap - dt);

            bool aligned = m.IsAligned && m.Phase != HudPhase.Countdown;
            if (aligned && !wasAligned)
            {
                snap = SnapSeconds;
                flash = Math.Max(flash, 0.22f);
                if (Fx != null)
                    Fx.Trigger(0.5f, HudPalette.Ready, true);
                HudSounds.Play(HudSounds.Lock);
            }
            wasAligned = m.IsAligned;

            countdown.Update(m.Phase == HudPhase.Countdown, m.SecondsLeft, dt);

            if (m.Phase == HudPhase.Countdown && m.CountdownDigit != lastDigit)
            {
                lastDigit = m.CountdownDigit;
                flash = 0.2f;
            }
            else if (m.Phase != HudPhase.Countdown)
            {
                lastDigit = 0;
            }

            UpdateLabels(m);
        }

        private static Color StateColor(FsdHudModel m)
        {
            if (m.BlindJump)
                return HudPalette.Primary;
            if (m.IsAligned)
                return HudPalette.Ready;
            if (m.Phase == HudPhase.Hold || m.Phase == HudPhase.Charging || m.Phase == HudPhase.FleetHold)
                return m.HasAlign && m.AlignAngle <= m.AlignBreak * 2f ? HudPalette.Primary : HudPalette.Warning;
            return HudPalette.Primary;
        }

        private void UpdateLabels(FsdHudModel m)
        {
            Color color = StateColor(m);

            string offset;
            string hint;
            if (m.BlindJump)
            {
                offset = "BLIND JUMP  " + HudFormat.Distance(m.TargetDistance);
                hint = "";
            }
            else if (m.HasAlign)
            {
                offset = "OFFSET " + HudFormat.Degrees(m.AlignAngle) + "  /  MAX " + HudFormat.Degrees(m.AlignTolerance);
                if (m.Phase == HudPhase.Countdown)
                    hint = "";
                else if (m.IsAligned)
                    hint = m.Phase == HudPhase.Charging ? "ALIGNED - CHARGING" : "ALIGNED - HOLD STEADY";
                else
                    hint = m.Phase == HudPhase.Charging ? "ALIGN BEFORE THE CHARGE COMPLETES" : "TURN TOWARD THE MARKER";
            }
            else
            {
                offset = "";
                hint = "";
            }

            // While the countdown runs above the ring, the offset text moves up out of its way
            offsetLabel.Offset = new Vector2(0f, Radius + (m.Phase == HudPhase.Countdown ? 80f : 44f));

            // Strings are compared, not rebuilt into the text board, unless they changed (offset changes in 0.1 deg steps)
            if (offset != shownOffset)
            {
                shownOffset = offset;
                offsetLabel.TextBoard.SetText(offset, HudPalette.Number(color, 0.8f, TextAlignment.Center));
            }
            if (hint != shownHint)
            {
                shownHint = hint;
                hintLabel.TextBoard.SetText(hint, HudPalette.Text(m.BlindJump ? HudPalette.Dim : color, 0.72f, TextAlignment.Center));
            }
        }

        protected override void Draw()
        {
            if (model == null)
                return;

            FsdHudModel m = model;
            Vector2 c = Position;
            Color state = StateColor(m);
            float pulse = 0.55f + 0.45f * (float)Math.Sin(clock * 4.0);

            // ---- ring: fills clockwise from the top with the charge or countdown
            int lit = m.HasProgress && (m.Phase == HudPhase.Charging || m.Phase == HudPhase.Countdown) ? (int)Math.Round(m.Progress * Ticks) : 0;
            bool waiting = m.Phase == HudPhase.Hold || m.Phase == HudPhase.FleetHold;

            // Impact kick swells the ring a little; the flash whitens the lit ticks
            bool effects = Fx != null && HudSettings.Current.Effects;
            float radius = Radius * (1f + (effects ? Fx.Kick * 0.06f : 0f)) * intro.RadiusScale;
            float whiten = effects ? MathHelper.Clamp(Fx.Flash, 0f, 1f) * 0.8f : 0f;

            float glitch = effects ? Fx.Glitch : 0f;

            for (int i = 0; i < Ticks; i++)
            {
                // Intro: the ring assembles clockwise from the top
                float around = i / (float)Ticks;
                if (!intro.Shown(around))
                    continue;

                double a = i * 2.0 * Math.PI / Ticks;
                Vector2 u = new Vector2((float)Math.Sin(a), (float)Math.Cos(a));

                Color color;
                if (flash > 0f)
                    color = HudPalette.Fade(HudPalette.Ready, 1f);
                else if (i < lit)
                    color = HudPalette.Fade(Color.Lerp(m.Phase == HudPhase.Countdown ? HudPalette.Ready : state, Color.White, whiten), 0.95f);
                else if (waiting)
                    color = HudPalette.Fade(state, 0.3f + 0.3f * pulse);
                else
                    color = HudPalette.Fade(Color.Lerp(HudPalette.Dim, Color.White, whiten * 0.6f), 0.85f);

                float edge = intro.Edge(around);
                if (edge > 0f)
                    color = Color.Lerp(color, Color.White, edge);

                // Glitch burst: the ring tears into slices that slide sideways, every other one red
                Vector2 slide = Vector2.Zero;
                if (glitch > 0f)
                {
                    slide = new Vector2(Fx.GlitchOffset(100 + i / 8), 0f);
                    if ((i / 8) % 2 == 0)
                        color = Color.Lerp(color, HudPalette.Danger, 0.55f * glitch);
                }

                DrawSegment(c + slide + u * radius, u, TickHalfLength, TickHalfWidth, color);
            }

            // Lock-on snap: a green ring closing in onto the tick ring
            if (snap > 0f)
            {
                float t = snap / SnapSeconds;
                DrawCircle(c, radius * (1f + 0.45f * t), 1.4f + 2.2f * t, 48, HudPalette.Fade(HudPalette.Ready, t * 0.9f));
            }

            // The countdown sits above the ring; the reticle stays in the centre
            float draw = intro.Draw;
            if (draw <= 0f)
                return;

            if (m.BlindJump)
            {
                DrawAimReticle(c, HudPalette.Fade(HudPalette.Primary, 0.95f), draw);
                return;
            }

            if (!m.HasAlign)
                return;

            // ---- nose reticle: a small ring that turns green on lock, drawn on around itself during the intro
            HudDraw.CircleArc(HudSpace.PlaneToWorldRef, c, 14f * (0.4f + 0.6f * draw), 1.4f, 20, draw, HudPalette.Fade(m.IsAligned ? HudPalette.Ready : HudPalette.Dim, 0.95f));

            // ---- target marker, placed by how far off the nose the target is, in the direction it lies
            Vector2 dir = new Vector2((float)m.AlignLocal.X, (float)m.AlignLocal.Y);
            if (dir.LengthSquared() < 1e-8f)
                dir = new Vector2(0f, 1f);
            dir.Normalize();

            float reach = MathHelper.Clamp(m.AlignAngle / MaxMarkerAngle, 0f, 1f);
            Vector2 marker = c + dir * MathHelper.Lerp(0f, Radius - 20f, reach);
            Color markerColor = HudPalette.Fade(state, m.IsAligned ? 1f : 0.8f + 0.2f * pulse);
            DrawDiamond(marker, (m.IsAligned ? 7f : 9f) * draw, markerColor);

            // ---- arrow just outside the ring, pointing the way to turn
            if (!m.IsAligned && draw >= 1f)
            {
                Vector2 perp = new Vector2(dir.Y, -dir.X);
                Vector2 tip = c + dir * (Radius + 22f);
                Vector2 back = tip - dir * 11f;
                DrawLine(tip, back + perp * 10f, 2.6f, markerColor);
                DrawLine(tip, back - perp * 10f, 2.6f, markerColor);
            }
        }

        /// <summary>Blind jump: corner brackets around the nose with a centre pip and side wings, like a gun sight.</summary>
        /// <param name="draw">0..1 intro: the pip appears first, the brackets open out from it, then the wings extend.</param>
        private void DrawAimReticle(Vector2 c, Color color, float draw)
        {
            MatrixD[] space = HudSpace.PlaneToWorldRef;
            float box = 24f * (0.25f + 0.75f * draw);
            HudDraw.Brackets(space, c, box, 9f * draw, 2f, color);
            float wing = MathHelper.Clamp((draw - 0.5f) * 2f, 0f, 1f);
            if (wing > 0f)
            {
                HudDraw.Line(space, c + new Vector2(-box - 6f - 12f * wing, 0f), c + new Vector2(-box - 6f, 0f), 2f, color);
                HudDraw.Line(space, c + new Vector2(box + 6f, 0f), c + new Vector2(box + 6f + 12f * wing, 0f), 2f, color);
            }
            HudDraw.Diamond(space, c, 3f * Math.Min(1f, draw * 3f), color);
        }

        private void DrawSegment(Vector2 center, Vector2 u, float halfLength, float halfWidth, Color color)
        {
            HudDraw.Segment(HudSpace.PlaneToWorldRef, center, u, halfLength, halfWidth, color);
        }

        private void DrawLine(Vector2 from, Vector2 to, float width, Color color)
        {
            HudDraw.Line(HudSpace.PlaneToWorldRef, from, to, width, color);
        }

        private void DrawDiamond(Vector2 center, float size, Color color)
        {
            HudDraw.Diamond(HudSpace.PlaneToWorldRef, center, size, color);
        }

        private void DrawCircle(Vector2 center, float radius, float width, int segments, Color color)
        {
            HudDraw.Circle(HudSpace.PlaneToWorldRef, center, radius, width, segments, color);
        }
    }
}
