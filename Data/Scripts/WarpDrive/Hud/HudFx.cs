using System;
using RichHudFramework.UI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// The HUD's sense of power. Instead of lighting the screen edges, the events of the drive are shown on the HUD itself:
    /// a spring "kick" that jolts the panel and ring, expanding shockwave rings around the reticle, a border "surge" that grows
    /// with the phase's intensity, and a flash for key moments. This class only holds the state and draws the rings; the panel and
    /// the alignment ring read <see cref="Kick"/>, <see cref="Surge"/>, <see cref="Flash"/> and <see cref="Tint"/>.
    /// </summary>
    public class HudFx : HudElementBase
    {
        private const int MaxRings = 3;
        private const float RingStart = AlignmentIndicator.Radius + 8f;
        private const float RingReach = 380f;
        private const int RingSegments = 64;

        // Impact spring: an impulse sets the velocity, the position then rings back to rest
        private const float SpringStiffness = 190f;
        private const float SpringDamping = 13f;

        private struct Ring
        {
            public float Age;
            public float Life;
            public float Strength;
            public Color Color;
            public bool Active;
        }

        private readonly Ring[] rings = new Ring[MaxRings];

        private float kick;
        private float kickVelocity;
        private float flash;
        private float flashDecay = 2f;
        private float surge;
        private Color tint = HudPalette.Primary;
        private Color flashColor = Color.White;
        private float clock;

        private HudPhase lastPhase = HudPhase.Idle;
        private int lastDigit;
        private int lastBrake;

        // Shake that follows the camera's, and the glitch bursts of a Hyperdiction
        private readonly Random random = new Random(7);
        private Vector2 shake;
        private float glitchWait = 0.4f;
        private float glitchLeft;
        private float glitchStrength;
        private int glitchSeed;

        private float glitchLevel;

        // After the drop the glitch carries on through the blackout, fading out smoothly
        private float aftermathLeft;
        private float aftermathPeak;

        /// <summary>The HUD's own tremble in HUD units: the camera's shake (same value and gusts) scaled down to a few pixels.</summary>
        public Vector2 ShakeOffset { get { return shake; } }

        /// <summary>0..1 strength of the glitch burst happening right now (0 between bursts).</summary>
        public float Glitch { get { return glitchLeft > 0f ? glitchStrength : 0f; } }

        /// <summary>A stable horizontal displacement in HUD units for one element or slice during the current glitch burst.</summary>
        public float GlitchOffset(int channel)
        {
            if (glitchLeft <= 0f)
                return 0f;

            uint h = unchecked((uint)glitchSeed * 2654435761u + (uint)channel * 40503u);
            h ^= h >> 15;
            h = unchecked(h * 0x2c1b3c6du);
            h ^= h >> 12;
            float unit = ((h & 0xFFFF) / 32767.5f) - 1f;
            return unit * (3f + 11f * glitchStrength);
        }

        /// <summary>Spring position: about +-1 on a strong hit, 0 at rest.</summary>
        public float Kick { get { return kick; } }

        /// <summary>0..1 how strongly the current phase is "charged" (drives border brightness, thickness and the sweep).</summary>
        public float Surge { get { return surge; } }

        /// <summary>0..1 white flash that decays on its own.</summary>
        public float Flash { get { return flash; } }

        public Color Tint { get { return tint; } }

        public Color FlashColor { get { return flashColor; } }

        /// <summary>Panel shove in HUD units: down on a hit, then back up past rest and settling.</summary>
        public Vector2 PanelOffset { get { return new Vector2(0f, -kick * 16f); } }

        public HudFx(HudParentBase parent) : base(parent)
        {
            Visible = false;
        }

        /// <summary>One event hit: kick, flash and (optionally) a shockwave ring.</summary>
        public void Trigger(float strength, Color color, bool ring)
        {
            if (!HudSettings.Current.Effects)
                return;

            strength = MathHelper.Clamp(strength, 0f, 1f);
            kickVelocity += 9f * strength;
            flash = Math.Max(flash, 0.75f * strength);
            flashColor = Color.Lerp(color, Color.White, 0.6f);
            flashDecay = 1.2f + 1.4f * (1f - strength);

            if (ring && strength > 0.2f)
                SpawnRing(strength, color);
        }

        /// <summary>Critical event text decides whether this was a drop (hard hit, red ring) or a plain warning.</summary>
        public void OnCriticalEvent(string text)
        {
            string upper = text.ToUpperInvariant();
            if (upper.Contains("DROP") || upper.Contains("CONDUIT") || upper.Contains("EMERGENCY") || upper.Contains("COLLAPSE"))
                Trigger(1f, HudPalette.Danger, true);
            else
                Trigger(0.45f, HudPalette.Warning, false);
        }

        private void SpawnRing(float strength, Color color)
        {
            int slot = 0;
            for (int i = 0; i < MaxRings; i++)
            {
                if (!rings[i].Active)
                {
                    slot = i;
                    break;
                }
                if (rings[i].Age > rings[slot].Age)
                    slot = i;
            }

            rings[slot].Active = true;
            rings[slot].Age = 0f;
            rings[slot].Life = 0.9f + 0.5f * strength;
            rings[slot].Strength = strength;
            rings[slot].Color = color;
        }

        public void Update(FsdHudModel m, bool active, float dt)
        {
            clock += dt;

            bool effects = HudSettings.Current.Effects;
            Color targetTint;
            float target = active && effects ? StateSurge(m, out targetTint) : Idle(out targetTint);

            if (active && effects)
                DetectEvents(m);
            else
            {
                lastPhase = HudPhase.Idle;
                lastDigit = 0;
                lastBrake = 0;
            }

            // Ease towards the target: about a third of a second either way
            float k = 1f - (float)Math.Exp(-dt / 0.35f);
            surge += (target - surge) * k;
            tint = Color.Lerp(tint, targetTint, k);

            // Spring (semi-implicit Euler, stable at the clamped frame time)
            kickVelocity += (-SpringStiffness * kick - SpringDamping * kickVelocity) * dt;
            kick += kickVelocity * dt;
            kick = MathHelper.Clamp(kick, -1.4f, 1.4f);

            if (flash > 0f)
                flash = Math.Max(0f, flash - flashDecay * dt);

            UpdateShake(m, active && effects, dt);
            UpdateGlitch(m, active && effects, dt);
            HudGlitch.Update(glitchLevel, Glitch, dt);

            bool anyRing = false;
            for (int i = 0; i < MaxRings; i++)
            {
                if (!rings[i].Active)
                    continue;

                rings[i].Age += dt;
                if (rings[i].Age >= rings[i].Life)
                    rings[i].Active = false;
                else
                    anyRing = true;
            }

            Visible = anyRing && effects;
        }

        private void UpdateShake(FsdHudModel m, bool on, float dt)
        {
            if (!on || m.ShakeMagnitude <= 0f)
            {
                // Settles instead of snapping when the transit ends
                shake *= (float)Math.Exp(-dt * 14.0);
                return;
            }

            // The camera's own value, gusts included; steady transit is about a pixel, the entry and exit peaks about two, a Hyperdictor several
            float magnitude = ShakeShaper.Natural(m.ShakeMagnitude, m.ShakeSeed);
            float amplitude = Math.Max(0f, magnitude - 0.4f) * 1.8f * (1f + 2f * m.HyperdictionIntensity);
            shake = new Vector2(
                amplitude * (float)(Math.Sin(clock * 37.1) * Math.Sin(clock * 13.7)),
                amplitude * 0.8f * (float)(Math.Sin(clock * 29.3 + 1.3) * Math.Sin(clock * 11.1)));
        }

        /// <summary>The hyperdiction's strength, or after the drop the fading tail of it.</summary>
        private float GlitchIntensity(FsdHudModel m, bool on, float dt)
        {
            if (!on)
            {
                aftermathLeft = 0f;
                return 0f;
            }

            if (m.Phase == HudPhase.Hyperdicted)
            {
                aftermathLeft = (float)HyperdictionRules.AFTERMATH_SECONDS;
                aftermathPeak = Math.Max(0.6f, m.HyperdictionIntensity);
                return m.HyperdictionIntensity;
            }

            if (aftermathLeft <= 0f)
                return m.HyperdictionIntensity;

            aftermathLeft = Math.Max(0f, aftermathLeft - dt);
            float t = aftermathLeft / (float)HyperdictionRules.AFTERMATH_SECONDS;
            return Math.Max(m.HyperdictionIntensity, aftermathPeak * t * t * (3f - 2f * t));
        }

        private void UpdateGlitch(FsdHudModel m, bool on, float dt)
        {
            float intensity = GlitchIntensity(m, on, dt);
            glitchLevel = intensity;

            if (glitchLeft > 0f)
                glitchLeft -= dt;

            if (intensity < 0.05f)
            {
                glitchLeft = 0f;
                glitchWait = 0.4f;
                return;
            }

            glitchWait -= dt;
            if (glitchWait > 0f)
                return;

            // A short burst, then a pause that shrinks as the hyperdiction gets stronger
            glitchLeft = 0.05f + 0.09f * (float)random.NextDouble();
            glitchStrength = MathHelper.Clamp(0.35f + 0.65f * intensity, 0f, 1f);
            glitchSeed = random.Next();
            glitchWait = MathHelper.Lerp(1.1f, 0.12f, intensity) * (0.5f + (float)random.NextDouble());
            kickVelocity += 2.5f * intensity;
        }

        private static float Idle(out Color tint)
        {
            tint = HudPalette.Primary;
            return 0f;
        }

        private void DetectEvents(FsdHudModel m)
        {
            if (m.Phase != lastPhase)
            {
                if (m.Phase == HudPhase.Engaging || m.Phase == HudPhase.Transit && lastPhase != HudPhase.Engaging)
                {
                    Trigger(1f, HudPalette.Primary, true);
                }
                else if ((m.Phase == HudPhase.Cooldown || m.Phase == HudPhase.Exiting || m.Phase == HudPhase.Idle) && (lastPhase == HudPhase.Transit || lastPhase == HudPhase.Exiting))
                {
                    Trigger(0.8f, HudPalette.Primary, true);   // the drive's own arrival sound is enough
                }
                else if (m.Phase == HudPhase.Hyperdicted)
                    Trigger(1f, HudPalette.Danger, true);
                else if (m.Phase == HudPhase.Cruise && (lastPhase == HudPhase.Spool || lastPhase == HudPhase.SpoolDelayed))
                    Trigger(0.5f, HudPalette.Primary, true);
                else if (m.Phase == HudPhase.Charging || m.Phase == HudPhase.Spool)
                    Trigger(0.25f, HudPalette.Primary, false);
                else if (m.Phase == HudPhase.Countdown)
                    Trigger(0.35f, HudPalette.Ready, false);
            }

            if (m.Phase == HudPhase.Countdown && m.CountdownDigit != lastDigit)
                Trigger(0.15f + 0.05f * (5 - Math.Min(5, m.CountdownDigit)), HudPalette.Ready, false);

            // The braking call getting more urgent
            if (m.BrakeLevel > lastBrake)
            {
                Trigger(m.BrakeLevel == 2 ? 0.45f : 0.2f, m.BrakeLevel == 2 ? HudPalette.Danger : HudPalette.Warning, false);
            }

            lastPhase = m.Phase;
            lastDigit = m.Phase == HudPhase.Countdown ? m.CountdownDigit : 0;
            lastBrake = m.BrakeLevel;
        }

        /// <summary>0..1 intensity and tint of the current phase.</summary>
        private float StateSurge(FsdHudModel m, out Color color)
        {
            color = HudPalette.Primary;
            float breathe = 0.5f + 0.5f * (float)Math.Sin(clock * 1.6);

            switch (m.Phase)
            {
                case HudPhase.Spool:
                    return 0.12f + 0.3f * m.Progress;

                case HudPhase.Charging:
                    return 0.2f + 0.4f * m.Progress;

                case HudPhase.SpoolDelayed:
                case HudPhase.Hold:
                    color = HudPalette.Warning;
                    return 0.2f + 0.12f * breathe;

                case HudPhase.FleetHold:
                    color = m.IsAligned ? HudPalette.Primary : HudPalette.Warning;
                    return 0.18f + 0.1f * breathe;

                case HudPhase.Countdown:
                    color = HudPalette.Ready;
                    return 0.55f + 0.35f * m.Progress;

                case HudPhase.Engaging:
                    return 0.7f + 0.3f * m.Progress;

                case HudPhase.Transit:
                    // Steady and strong; climbs over the last 5 s before arrival, turns amber with a hyperdiction
                    float arrival = m.ArrivalRamp;
                    color = Color.Lerp(HudPalette.Primary, HudPalette.Warning, Math.Max(m.HyperdictionIntensity, arrival * 0.5f));
                    return MathHelper.Clamp(0.6f + 0.06f * breathe + 0.34f * arrival + 0.2f * m.HyperdictionIntensity, 0f, 1f);

                case HudPhase.Exiting:
                    return 0.6f * (1f - m.Progress);

                case HudPhase.Hyperdicted:
                    color = Color.Lerp(HudPalette.Warning, HudPalette.Danger, m.HyperdictionIntensity);
                    float shudder = 0.5f + 0.5f * (float)Math.Sin(clock * 11.0) * (float)Math.Sin(clock * 4.3);
                    return 0.6f + 0.4f * m.HyperdictionIntensity * (0.6f + 0.4f * shudder);

                case HudPhase.Cruise:
                    color = Color.Lerp(HudPalette.Primary, HudPalette.Warning, MathHelper.Clamp(m.GravityFactor * 1.5f, 0f, 1f));
                    if (m.BrakeLevel == 2)
                        color = HudPalette.Danger;
                    else if (m.BrakeLevel == 1)
                        color = HudPalette.Warning;
                    return 0.1f + 0.4f * m.SpeedFraction + 0.3f * m.GravityFactor + 0.2f * m.BrakeLevel;

                default:
                    return 0f;
            }
        }

        public void Hide()
        {
            surge = 0f;
            flash = 0f;
            kick = 0f;
            kickVelocity = 0f;
            shake = Vector2.Zero;
            glitchLeft = 0f;
            glitchLevel = 0f;
            aftermathLeft = 0f;
            HudGlitch.Reset();
            for (int i = 0; i < MaxRings; i++)
                rings[i].Active = false;
            Visible = false;
        }

        protected override void Draw()
        {
            Vector2 c = Position;
            MatrixD[] space = HudSpace.PlaneToWorldRef;

            for (int i = 0; i < MaxRings; i++)
            {
                if (!rings[i].Active)
                    continue;

                float t = MathHelper.Clamp(rings[i].Age / rings[i].Life, 0f, 1f);
                float eased = 1f - (1f - t) * (1f - t);
                float radius = MathHelper.Lerp(RingStart, RingStart + RingReach * (0.6f + 0.4f * rings[i].Strength), eased);
                float alpha = (1f - t) * (1f - t) * rings[i].Strength * 0.75f;
                float width = 1.2f + 3.2f * (1f - t) * rings[i].Strength;

                HudDraw.Circle(space, c, radius, width, RingSegments, HudPalette.Fade(rings[i].Color, alpha));
            }
        }
    }
}
