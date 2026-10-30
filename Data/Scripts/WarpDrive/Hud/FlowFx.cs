using System;
using RichHudFramework.UI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Thin light streaks that drift through the periphery of the screen, leaving the centre clear.
    /// Outward while cruising (faster with speed), inward while the drive charges (gathering towards the reticle as the charge builds).
    /// They are always in motion but faint, so the HUD feels alive without flashing. Not in hyperspace: there the streaks are the
    /// tunnel itself, a particle effect in the world (HyperspaceTunnel.cs), and flat lines painted over it would only muddy it.
    /// </summary>
    public class FlowFx : HudElementBase
    {
        private const int Streaks = 40;
        private const float ClearRadius = 150f;   // nothing is drawn this close to the crosshair
        private const float MaxAlpha = 0.30f;

        private struct Streak
        {
            public float Angle;
            public float Progress;   // 0 at the clear radius, 1 at the screen corner
            public float Speed;
            public float Length;
        }

        private readonly Streak[] streaks = new Streak[Streaks];
        private readonly Random random = new Random(11);

        private float level;
        private float flow;
        private float density = 1f;       // share of the streaks in use: in supercruise it grows with the speed
        private float targetDensity = 1f;
        private bool inward;
        private float clock;
        private Color tint = HudPalette.Primary;
        private Vector2 view;

        public FlowFx(HudParentBase parent) : base(parent)
        {
            Visible = false;
            for (int i = 0; i < Streaks; i++)
            {
                Respawn(i);
                streaks[i].Progress = (float)random.NextDouble();
            }
        }

        private void Respawn(int i)
        {
            streaks[i].Angle = (float)(random.NextDouble() * Math.PI * 2.0);
            streaks[i].Speed = 0.6f + 0.8f * (float)random.NextDouble();
            streaks[i].Length = 0.7f + 0.6f * (float)random.NextDouble();
            streaks[i].Progress = inward ? 1f : 0f;
        }

        public void Update(FsdHudModel m, bool active, float dt, Vector2 screen)
        {
            view = screen;
            clock += dt;

            float targetLevel, targetFlow;
            Target(active ? m : null, out targetLevel, out targetFlow);

            float k = 1f - (float)Math.Exp(-dt / 0.6f);
            level += (targetLevel - level) * k;
            flow += (targetFlow - flow) * k;
            density += (targetDensity - density) * k;

            Visible = level > 0.01f;
            if (!Visible)
                return;

            for (int i = 0; i < Streaks; i++)
            {
                float speed = flow * streaks[i].Speed;
                if (inward)
                    streaks[i].Progress -= dt * speed * (0.12f + 0.55f * streaks[i].Progress);
                else
                    streaks[i].Progress += dt * speed * (0.08f + 0.9f * streaks[i].Progress);

                if (streaks[i].Progress > 1f || streaks[i].Progress < 0f)
                    Respawn(i);
            }
        }

        private void Target(FsdHudModel m, out float targetLevel, out float targetFlow)
        {
            targetLevel = 0f;
            targetFlow = 0f;
            targetDensity = 1f;
            if (m == null || !HudSettings.Current.Effects)
                return;

            Color tinted = HudPalette.Primary;
            switch (m.Phase)
            {
                case HudPhase.Spool:
                    // The supercruise spool is the WarpStart particle effect (WarpStart.sbc), not the HUD streaks
                    break;

                case HudPhase.Charging:
                    inward = true;
                    targetLevel = 0.3f + 0.5f * m.Progress;
                    targetFlow = 0.3f + 0.9f * m.Progress;
                    break;

                case HudPhase.SpoolDelayed:
                case HudPhase.Hold:
                case HudPhase.FleetHold:
                    inward = true;
                    targetLevel = 0.25f;
                    targetFlow = 0.18f;
                    tinted = Color.Lerp(HudPalette.Primary, HudPalette.Warning, 0.7f);
                    break;

                case HudPhase.Countdown:
                    inward = true;
                    targetLevel = 0.8f;
                    targetFlow = 1.2f + 0.8f * m.Progress;
                    break;

                case HudPhase.Engaging:
                case HudPhase.Transit:
                case HudPhase.Exiting:
                case HudPhase.Hyperdicted:
                    // The tunnel is on screen as a real particle effect (HyperspaceTunnel.cs): no HUD streaks over it
                    break;

                case HudPhase.Cruise:
                    inward = false;
                    targetLevel = m.SpeedFraction < 0.02f ? 0f : 0.25f + 0.5f * m.SpeedFraction;
                    targetFlow = 0.15f + 1.2f * m.SpeedFraction;
                    targetDensity = 0.2f + 0.8f * m.SpeedFraction;   // a crawl shows a few lazy streaks, full speed fills the screen
                    tinted = Color.Lerp(HudPalette.Primary, HudPalette.Warning, MathHelper.Clamp(m.GravityFactor * 1.5f, 0f, 1f));
                    break;
            }

            tint = Color.Lerp(tint, tinted, 0.1f);
        }

        public void Hide()
        {
            level = 0f;
            flow = 0f;
            Visible = false;
        }

        protected override void Draw()
        {
            if (level < 0.01f)
                return;

            MatrixD[] space = HudSpace.PlaneToWorldRef;
            Vector2 c = Position;
            float maxRadius = 0.5f * (float)Math.Sqrt(view.X * view.X + view.Y * view.Y);
            float span = Math.Max(1f, maxRadius - ClearRadius);
            float lengthScale = 8f + 60f * Math.Min(flow, 2.2f) / 2.2f;

            int inUse = Math.Max(1, (int)Math.Round(Streaks * density));
            for (int i = 0; i < inUse; i++)
            {
                float p = streaks[i].Progress;

                // Fade in just outside the clear zone and out towards the corners
                float env = Math.Min(Math.Min(1f, p / 0.12f), Math.Min(1f, (1f - p) / 0.15f));
                float alpha = level * MaxAlpha * env * (0.45f + 0.55f * p);
                if (alpha < 0.01f)
                    continue;

                float angle = streaks[i].Angle;
                Vector2 dir = new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle));
                float radius = ClearRadius + span * p;
                float length = lengthScale * streaks[i].Length * (0.4f + 0.6f * p);

                Vector2 head = c + dir * radius;
                Vector2 tail = head - dir * length * (inward ? -0.6f : 1f);
                float width = 0.9f + 0.9f * p;

                // Three pieces with rising alpha towards the head read as a fading trail
                for (int s = 0; s < 3; s++)
                {
                    Vector2 a = Vector2.Lerp(tail, head, s / 3f);
                    Vector2 b = Vector2.Lerp(tail, head, (s + 1) / 3f);
                    HudDraw.Line(space, a, b, width, HudPalette.Fade(tint, alpha * (s + 1) / 3f));
                }
            }
        }
    }
}
