using System;
using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Quiet in-flight visuals around the crosshair.
    /// Supercruise: a thin broken reticle with a speed arc on the left and a gravity-well arc on the right (also during the spool, filling with it).
    /// Hyperspace transit: a thin progress ring with a slow glint. All numbers are in the status strip, not here.
    /// </summary>
    public class FlightIndicator : HudElementBase
    {
        private const float CruiseRadius = 62f;
        private const float TransitRadius = 74f;
        private const int ArcTicks = 18;
        private const int RingTicks = 72;
        private const double ArcSpan = Math.PI * 2.0 / 3.0;   // 120 degrees per side arc

        private readonly CountdownReadout countdown;
        private readonly ReticleIntro intro = new ReticleIntro();

        private FsdHudModel model;
        private float clock;
        private float spin;   // radians the reticle details have turned: faster with speed or charge

        public FlightIndicator(HudParentBase parent) : base(parent)
        {
            Size = new Vector2(TransitRadius * 2f + 20f);
            countdown = new CountdownReadout(this, new Vector2(0f, CruiseRadius + 44f), HudPalette.Primary);   // above the reticle
            Visible = false;
        }

        /// <summary>Called while the indicator is hidden: the next showing plays the intro again.</summary>
        public void ResetState()
        {
            intro.Reset();
            countdown.Reset();
        }

        public static bool WantsToShow(FsdHudModel m)
        {
            return m.Phase == HudPhase.Cruise || m.Phase == HudPhase.Spool || m.Phase == HudPhase.SpoolDelayed
                || m.Phase == HudPhase.Transit || m.Phase == HudPhase.Hyperdicted;
        }

        /// <summary>Line art only: speed, ETA and the rest live in the status strip.</summary>
        public void SetModel(FsdHudModel m, float dt)
        {
            model = m;
            clock += dt;
            intro.Advance(dt);

            // The spool counts down above the reticle, the same rolling readout as the hyperspace countdown
            countdown.Update((m.Phase == HudPhase.Spool || m.Phase == HudPhase.SpoolDelayed) && m.HasSeconds, m.SecondsLeft, dt);

            float rate = 0f;
            if (m.Phase == HudPhase.Cruise)
                rate = 0.12f + 1.0f * m.SpeedFraction;
            else if (m.Phase == HudPhase.Spool)
                rate = 0.25f + 1.4f * m.Progress;
            else if (m.Phase == HudPhase.SpoolDelayed)
                rate = 0.08f;
            spin += rate * dt;
        }

        protected override void Draw()
        {
            if (model == null)
                return;

            MatrixD[] space = HudSpace.PlaneToWorldRef;
            Vector2 c = Position;

            if (model.Phase == HudPhase.Transit || model.Phase == HudPhase.Hyperdicted)
                DrawTransit(space, c);
            else
                DrawCruise(space, c);
        }

        /// <summary>Thin ring that fills with the distance travelled; a soft glint circles it, like light along the tunnel.</summary>
        private void DrawTransit(MatrixD[] space, Vector2 c)
        {
            bool interdicted = model.Phase == HudPhase.Hyperdicted;
            int lit = (int)Math.Round(model.Progress * RingTicks);
            float glint = (clock * 0.35f) % 1f;

            float radiusScale = intro.RadiusScale;
            for (int i = 0; i < RingTicks; i++)
            {
                float f = i / (float)RingTicks;
                if (!intro.Shown(f))
                    continue;

                double a = f * Math.PI * 2.0;
                Vector2 u = new Vector2((float)Math.Sin(a), (float)Math.Cos(a));

                float d = Math.Abs(f - glint);
                d = Math.Min(d, 1f - d);
                float shine = Math.Max(0f, 1f - d * 14f);

                Color color;
                if (interdicted)
                    color = HudPalette.Fade(HudPalette.Danger, (i % 2 == 0 ? 0.35f : 0.15f) + 0.4f * (float)Math.Abs(Math.Sin(clock * 9.0 + i)));
                else if (i < lit)
                    color = HudPalette.Fade(HudPalette.Primary, 0.55f + 0.4f * shine);
                else
                    color = HudPalette.Fade(HudPalette.Dim, 0.3f + 0.4f * shine);

                float edge = intro.Edge(f);
                if (edge > 0f)
                    color = Color.Lerp(color, Color.White, edge);

                Vector2 at = c + u * TransitRadius * radiusScale;
                DrawSplit(space, at, u, 3.5f, 0.8f, color.A / 255f);
                HudDraw.Segment(space, at, u, 3.5f, 0.8f, color);
            }

            // Outer ring of uneven dashes turning slowly against the glint, so the tunnel never sits still
            const int dashes = 32;
            for (int i = 0; i < dashes; i++)
            {
                if (!intro.Shown(i / (float)dashes))
                    continue;

                double a = i * Math.PI * 2.0 / dashes - clock * 0.4;
                Vector2 radial = new Vector2((float)Math.Sin(a), (float)Math.Cos(a));
                Vector2 tangent = new Vector2(radial.Y, -radial.X);
                float half = i % 4 == 0 ? 7f : 2.5f;
                Color dash = interdicted ? HudPalette.Fade(HudPalette.Danger, 0.3f) : HudPalette.Fade(HudPalette.Primary, i % 4 == 0 ? 0.4f : 0.2f);
                Vector2 dashAt = c + radial * (TransitRadius + 14f) * radiusScale;
                DrawSplit(space, dashAt, tangent, half, 0.7f, dash.A / 255f);
                HudDraw.Segment(space, dashAt, tangent, half, 0.7f, dash);
            }

            // Tiny centre pip so the eye has an anchor in the tunnel
            HudDraw.Diamond(space, c, 2f * intro.Draw, HudPalette.Fade(interdicted ? HudPalette.Danger : HudPalette.Primary, 0.6f));
        }

        private static readonly Color SplitRed = new Color(255, 50, 60);
        private static readonly Color SplitCyan = new Color(60, 230, 255);

        /// <summary>Hyperdiction RGB split: a red and a cyan ghost of the segment, pushed apart sideways (each tick slightly differently).</summary>
        private static void DrawSplit(MatrixD[] space, Vector2 at, Vector2 u, float halfLength, float halfWidth, float alpha)
        {
            float split = HudSettings.Current.Effects ? HudGlitch.Split : 0f;
            if (split < 0.5f)
                return;

            float jitter = 0.7f + 0.6f * HudGlitch.Unit(31, (int)(at.X * 7f + at.Y * 13f));
            Vector2 shift = new Vector2(split * jitter, 0f);
            float ghost = alpha * (0.45f + 0.35f * HudGlitch.Burst);
            HudDraw.Segment(space, at - shift, u, halfLength, halfWidth, HudPalette.Fade(SplitRed, ghost));
            HudDraw.Segment(space, at + shift, u, halfLength, halfWidth, HudPalette.Fade(SplitCyan, ghost));
        }

        /// <summary>Broken reticle: four short arcs, a speed arc on the left and a gravity arc on the right, with a flight-path pip.</summary>
        private void DrawCruise(MatrixD[] space, Vector2 c)
        {
            Color frame = HudPalette.Fade(HudPalette.Primary, 0.45f);
            float radiusScale = intro.RadiusScale;

            // Four short quarter arcs, gapped at the cardinal points; during the intro they are laid down one after the other
            for (int q = 0; q < 4; q++)
            {
                float laid = MathHelper.Clamp(intro.Ring * 4f - q, 0f, 1f);
                if (laid <= 0f)
                    continue;

                double start = q * Math.PI / 2.0 + 0.35 + spin;
                double span = (Math.PI / 2.0 - 0.7) * laid;
                Arc(space, c, (CruiseRadius - 14f) * radiusScale, start, start + span, Math.Max(1, (int)Math.Ceiling(8 * laid)), 1.2f, frame);
            }

            // A second, uneven ring of dashes turning the other way
            for (int i = 0; i < 24; i++)
            {
                if (!intro.Shown(i / 24f))
                    continue;

                double a = i * Math.PI * 2.0 / 24.0 - spin * 0.6;
                Vector2 radial = new Vector2((float)Math.Sin(a), (float)Math.Cos(a));
                Vector2 tangent = new Vector2(radial.Y, -radial.X);
                HudDraw.Segment(space, c + radial * (CruiseRadius + 12f) * radiusScale, tangent, i % 3 == 0 ? 5f : 1.5f, 0.6f, HudPalette.Fade(HudPalette.Primary, i % 3 == 0 ? 0.32f : 0.15f));
            }

            // Flight-path marker: small circle with wings, drawn on outward from the centre during the intro
            float draw = intro.Draw;
            Color marker = HudPalette.Fade(HudPalette.Primary, 0.75f);
            if (draw > 0f)
            {
                HudDraw.CircleArc(space, c, 5f * (0.4f + 0.6f * draw), 1.2f, 12, draw, marker);
                float wing = MathHelper.Clamp((draw - 0.4f) / 0.6f, 0f, 1f);
                if (wing > 0f)
                {
                    HudDraw.Line(space, c + new Vector2(-7f - 7f * wing, 0f), c + new Vector2(-7f, 0f), 1.4f, marker);
                    HudDraw.Line(space, c + new Vector2(7f, 0f), c + new Vector2(7f + 7f * wing, 0f), 1.4f, marker);
                    HudDraw.Line(space, c + new Vector2(0f, 5f), c + new Vector2(0f, 5f + 5f * wing), 1.4f, marker);
                }
            }

            // Left arc: speed while cruising, spool progress while charging (fills bottom to top)
            float fill = model.Phase == HudPhase.Cruise ? model.SpeedFraction : model.Progress;
            Color fillColor = model.Phase == HudPhase.SpoolDelayed ? HudPalette.Warning : HudPalette.Primary;
            SideArc(space, c, -1, fill, fillColor);

            // Right arc: gravity well strength
            if (model.Phase == HudPhase.Cruise && model.GravityFactor > 0.02f)
                SideArc(space, c, 1, model.GravityFactor, model.GravityFactor > 0.6f ? HudPalette.Danger : HudPalette.Warning);
        }

        /// <param name="side">-1 left, 1 right.</param>
        private void SideArc(MatrixD[] space, Vector2 c, int side, float fill, Color color)
        {
            int lit = (int)Math.Round(MathHelper.Clamp(fill, 0f, 1f) * ArcTicks);
            for (int i = 0; i < ArcTicks; i++)
            {
                // From the bottom (210 deg / 150 deg) up to the top (330 deg / 30 deg), measured clockwise from 12 o'clock
                double t = i / (double)(ArcTicks - 1);
                double a = side < 0 ? Math.PI * 7.0 / 6.0 + ArcSpan * t : Math.PI * 5.0 / 6.0 - ArcSpan * t;
                if (!intro.Shown((float)(a / (Math.PI * 2.0))))
                    continue;

                Vector2 u = new Vector2((float)Math.Sin(a), (float)Math.Cos(a));
                Color tick = i < lit ? HudPalette.Fade(color, 0.85f) : HudPalette.Fade(HudPalette.Dim, 0.4f);
                float edge = intro.Edge((float)(a / (Math.PI * 2.0)));
                if (edge > 0f)
                    tick = Color.Lerp(tick, Color.White, edge);
                HudDraw.Segment(space, c + u * CruiseRadius * intro.RadiusScale, u, 4f, 1.1f, tick);
            }
        }

        private static void Arc(MatrixD[] space, Vector2 c, float radius, double from, double to, int segments, float width, Color color)
        {
            Vector2 prev = c + new Vector2((float)Math.Sin(from), (float)Math.Cos(from)) * radius;
            for (int i = 1; i <= segments; i++)
            {
                double a = from + (to - from) * i / segments;
                Vector2 next = c + new Vector2((float)Math.Sin(a), (float)Math.Cos(a)) * radius;
                HudDraw.Line(space, prev, next, width, color);
                prev = next;
            }
        }
    }
}
