using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Purely client-side manager that eliminates sunlight, shadows, and visual sun glare during Hyperspace transit.
    /// Sunlight & shadows are extinguished via WeatherEffects.SunIntensityOverride.
    /// The sun disc is moved out of sight by turning the Day/Night clock: the game turns the sun about the axis
    /// normalize(Up - (Up.base) base) of its base direction (the vanilla default world gives exactly the old hard-coded axis), so the
    /// orbit's axis is known from the base direction and no read-back is needed after the jump starts (the game's reported sun direction
    /// lags behind the clock). Turns are measured from where the sun is when the jump starts, at that moment's clock time.
    /// The sun is placed on the point of that circle farthest from where the camera looks.
    /// Spectator camera (F8) is completely unaffected, retaining normal sun position and lighting.
    /// </summary>
    public class HyperspaceSunManager
    {
        private static HyperspaceSunManager _instance;
        public static HyperspaceSunManager Instance => _instance ?? (_instance = new HyperspaceSunManager());

        private readonly List<object> _activeSources = new List<object>();

        private const float SUPPRESSED_INTENSITY = 0.0001f;
        private const float DEFAULT_SUN_INTENSITY = 50f;      // the vanilla environment's sun; the fade-in ends by handing back to the world's own value
        private const float FADE_IN_SECONDS = 1f;

        private bool _isSuppressed = false;
        private bool _sunMoved = false;
        private bool _originalSunRotationEnabled = false;
        private float _originalSunTime = 0f;
        private TimeSpan _suppressedAt;
        private float _dayLength = 120f;
        private bool _isSpectatorActive = false;
        private bool _needsInitialSet = false;
        private Vector3 _lastCommandedDir = Vector3.Zero;
        private bool _hasCommanded = false;
        private int _lastCommandTick = 0;

        // The orbit: the sun direction at clock time t is the base direction turned by 2 pi t / day length about the axis
        private bool _orbitKnown;
        private Vector3 _baseDir;
        private Vector3 _axis;

        // Where the sun is when the jump starts and the clock time then: every re-aim turns the clock relative to this
        private Vector3 _refDir;
        private float _refTime;

        private bool _fading;
        private float _fadeTime;

        public bool IsSuppressed => _isSuppressed;

        public void Init(string modPath)
        {
        }

        public void RequestSuppression(object source, bool isLocalPlayerOnboard)
        {
            if (MyAPIGateway.Utilities.IsDedicated || source == null || !isLocalPlayerOnboard)
                return;

            if (!_activeSources.Contains(source))
                _activeSources.Add(source);

            if (!_isSuppressed)
            {
                ActivateSuppression();
            }
        }

        public void ReleaseSuppression(object source, bool instant)
        {
            if (MyAPIGateway.Utilities.IsDedicated || source == null)
                return;

            _activeSources.Remove(source);

            if (_activeSources.Count == 0 && _isSuppressed)
            {
                DeactivateSuppression(instant);
            }
        }

        private void ActivateSuppression()
        {
            _isSuppressed = true;
            _needsInitialSet = true;
            _hasCommanded = false;
            _isSpectatorActive = false;
            _fading = false;

            try
            {
                // Capture exact initial sun state before any modification
                _originalSunTime = MyVisualScriptLogicProvider.SunRotationGetCurrentTime();
                Vector3 currentDir = MyVisualScriptLogicProvider.GetSunDirection();
                _suppressedAt = MyAPIGateway.Session != null ? MyAPIGateway.Session.ElapsedPlayTime : TimeSpan.Zero;

                _originalSunRotationEnabled = MyAPIGateway.Session?.SessionSettings != null && MyAPIGateway.Session.SessionSettings.EnableSunRotation;

                if (!_originalSunRotationEnabled)
                {
                    MyVisualScriptLogicProvider.SunRotationEnabled(true);
                }

                _dayLength = MyVisualScriptLogicProvider.SunRotationGetDayLength();
                if (_dayLength <= 0.001f)
                    _dayLength = 120f;

                // Once per world: the sun has not been moved by us yet, so the reported direction is trustworthy
                if (currentDir.LengthSquared() > 1e-4f)
                {
                    if (!_orbitKnown)
                        LearnOrbit(Vector3.Normalize(currentDir), _originalSunRotationEnabled ? _originalSunTime : 0f);

                    // The reference for this jump: the sun as it is now, at the current clock time (projected onto the orbit's plane)
                    Vector3 seen = Vector3.Normalize(currentDir);
                    Vector3 onPlane = seen - Vector3.Dot(seen, _axis) * _axis;
                    _refDir = onPlane.LengthSquared() > 1e-4f ? Vector3.Normalize(onPlane) : _baseDir;
                    _refTime = _originalSunTime;
                }
                else
                {
                    _refDir = _baseDir;
                    _refTime = _originalSunTime;
                }

                // Extinguish directional sunlight and shadows
                if (MyAPIGateway.Session?.WeatherEffects != null)
                {
                    MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = SUPPRESSED_INTENSITY;
                }

                // Immediately place sun behind camera on this very frame before rendering
                UpdateSunPositionAndLighting(0f);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[HyperspaceSunManager] ActivateSuppression error: " + ex.Message);
            }
        }

        private void DeactivateSuppression(bool instant)
        {
            _isSuppressed = false;

            try
            {
                // Restore the clock where it would be now: a moving sun carries on from where it was plus the time spent in the tunnel
                if (_sunMoved)
                {
                    float restoreTime = _originalSunTime;
                    if (_originalSunRotationEnabled && MyAPIGateway.Session != null)
                        restoreTime += (float)(MyAPIGateway.Session.ElapsedPlayTime - _suppressedAt).TotalSeconds;
                    MyVisualScriptLogicProvider.SunRotationSetTime(WrapTime(restoreTime));
                    _sunMoved = false;
                }

                // Restore original sun rotation setting
                if (!_originalSunRotationEnabled)
                {
                    MyVisualScriptLogicProvider.SunRotationEnabled(false);
                }

                // Restore sunlight and shadows: at once after an emergency drop, faded in over a second after a normal arrival
                if (MyAPIGateway.Session?.WeatherEffects != null && MyAPIGateway.Session.WeatherEffects.SunIntensityOverride.HasValue)
                {
                    if (instant)
                    {
                        MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = null;
                    }
                    else
                    {
                        _fading = true;
                        _fadeTime = 0f;
                    }
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[HyperspaceSunManager] DeactivateSuppression error: " + ex.Message);
            }
        }

        public void UpdateSimulation()
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            if (!_isSuppressed)
            {
                UpdateFade();
                return;
            }

            UpdateSunPositionAndLighting(1f / 60f);
        }

        public void UpdateDraw()
        {
            if (!_isSuppressed || MyAPIGateway.Utilities.IsDedicated)
                return;

            // Catch camera switches immediately on draw frames (e.g. 1st <-> 3rd person)
            UpdateSunPositionAndLighting(0f);
        }

        private void UpdateFade()
        {
            if (!_fading)
                return;

            var weather = MyAPIGateway.Session?.WeatherEffects;
            _fadeTime += 1f / 60f;
            if (weather == null || _fadeTime >= FADE_IN_SECONDS)
            {
                _fading = false;
                if (weather != null)
                    weather.SunIntensityOverride = null;
                return;
            }

            float t = _fadeTime / FADE_IN_SECONDS;
            weather.SunIntensityOverride = SUPPRESSED_INTENSITY + DEFAULT_SUN_INTENSITY * t * t;
        }

        private float WrapTime(float time)
        {
            while (time >= _dayLength) time -= _dayLength;
            while (time < 0f) time += _dayLength;
            return time;
        }

        // ------------------------------------------------------------------ the orbit

        /// <summary>The game's rotation axis for a base sun direction: Up made perpendicular to it (Left when the sun is nearly overhead).</summary>
        private static Vector3 AxisFor(Vector3 baseDir)
        {
            Vector3 reference = Math.Abs(Vector3.Dot(baseDir, Vector3.Up)) > 0.95f ? Vector3.Left : Vector3.Up;
            Vector3 axis = reference - Vector3.Dot(reference, baseDir) * baseDir;
            return Vector3.Normalize(axis);
        }

        private static Vector3 Rotate(Vector3 v, Vector3 axis, double angle)
        {
            return Vector3.Transform(v, Matrix.CreateFromAxisAngle(axis, (float)angle));
        }

        /// <summary>
        /// Finds the base direction from the sun seen at clock time <paramref name="time"/>. Without sun rotation that is simply the
        /// sun itself; with rotation, the base is where the sun was at time 0, so the axis that is consistent with both is searched for.
        /// </summary>
        private void LearnOrbit(Vector3 seen, float time)
        {
            double angle = 2.0 * Math.PI * time / _dayLength;
            if (Math.Abs(angle) < 1e-4)
            {
                _baseDir = seen;
                _axis = AxisFor(seen);
                _orbitKnown = true;
                return;
            }

            // The axis is perpendicular to the seen direction: try every direction around it and keep the one the game's rule reproduces
            Vector3 p1 = Vector3.Normalize(Vector3.CalculatePerpendicularVector(seen));
            Vector3 p2 = Vector3.Cross(seen, p1);
            double bestError = double.MaxValue, bestPhi = 0.0;
            for (int pass = 0; pass < 3; pass++)
            {
                double span = pass == 0 ? Math.PI : Math.PI / Math.Pow(60.0, pass);
                double center = pass == 0 ? 0.0 : bestPhi;
                for (int i = 0; i <= 120; i++)
                {
                    double phi = center - span + 2.0 * span * i / 120.0;
                    Vector3 axis = p1 * (float)Math.Cos(phi) + p2 * (float)Math.Sin(phi);
                    Vector3 baseDir = Rotate(seen, axis, -angle);
                    double error = (AxisFor(baseDir) - axis).LengthSquared();
                    if (error < bestError)
                    {
                        bestError = error;
                        bestPhi = phi;
                    }
                }
            }

            Vector3 found = p1 * (float)Math.Cos(bestPhi) + p2 * (float)Math.Sin(bestPhi);
            if (bestError < 1e-4)
            {
                _axis = found;
                _baseDir = Rotate(seen, found, -angle);
            }
            else
            {
                // No consistent orbit (unexpected clock offset): treat the sun as seen now as the base, at the current time
                MyLog.Default.WriteLine("[HyperspaceSunManager] sun orbit not consistent with the clock (error " + bestError.ToString("0.000000") + "); assuming the current sun is the base");
                _axis = AxisFor(seen);
                _baseDir = Rotate(seen, _axis, -angle);
            }
            _orbitKnown = true;
        }

        // ------------------------------------------------------------------ aiming

        private void UpdateSunPositionAndLighting(float dt)
        {
            if (MyAPIGateway.Session == null)
                return;

            bool isSpectator = !LocalView.IsOnboardCamera;

            if (isSpectator)
            {
                if (!_isSpectatorActive)
                {
                    _isSpectatorActive = true;

                    // Restore sun for spectator camera so normal space and lighting are visible
                    if (_sunMoved)
                    {
                        MyVisualScriptLogicProvider.SunRotationSetTime(_originalSunTime);
                        _sunMoved = false;
                    }

                    if (MyAPIGateway.Session.WeatherEffects != null && MyAPIGateway.Session.WeatherEffects.SunIntensityOverride.HasValue)
                    {
                        MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = null;
                    }
                }
                return;
            }

            // If returning from spectator camera, re-engage suppression
            if (_isSpectatorActive)
            {
                _isSpectatorActive = false;
                _needsInitialSet = true;
                _hasCommanded = false;

                if (MyAPIGateway.Session.WeatherEffects != null)
                {
                    MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = SUPPRESSED_INTENSITY;
                }
            }

            // Ensure sunlight override remains applied for onboard cameras
            if (MyAPIGateway.Session.WeatherEffects != null && !MyAPIGateway.Session.WeatherEffects.SunIntensityOverride.HasValue)
            {
                MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = SUPPRESSED_INTENSITY;
            }

            var camera = MyAPIGateway.Session.Camera;
            if (camera == null || !_orbitKnown)
                return;

            Vector3 camForward = (Vector3)camera.WorldMatrix.Forward;
            Vector3 camUp = (Vector3)camera.WorldMatrix.Up;
            Vector3 A = _axis;

            // The orbit is a great circle perpendicular to A: its point farthest from the line of sight is opposite to the forward
            // direction projected onto that circle's plane
            Vector3 F_perp = camForward - Vector3.Dot(camForward, A) * A;
            Vector3 S_optimal;
            if (F_perp.LengthSquared() > 1e-4f)
            {
                S_optimal = -Vector3.Normalize(F_perp);
            }
            else
            {
                // Looking straight along the axis: every point is 90 degrees off, push the sun below the dashboard
                Vector3 down = -camUp;
                Vector3 down_perp = down - Vector3.Dot(down, A) * A;
                S_optimal = down_perp.LengthSquared() > 1e-4f ? Vector3.Normalize(down_perp) : Vector3.Normalize(Vector3.CalculatePerpendicularVector(A));
            }

            // Pure Feed-Forward Re-Aim:
            // Compare the desired direction S_optimal against the LAST COMMANDED direction.
            // Never read back current sun direction/time, which lags behind the clock.
            float cosErr = _hasCommanded ? MathHelper.Clamp(Vector3.Dot(_lastCommandedDir, S_optimal), -1f, 1f) : -1f;
            int currentTick = MyAPIGateway.Session.GameplayFrameCounter;

            // Re-aim if uninitialized, if desired camera direction moved (> ~0.57 deg), or every 30 ticks to cancel clock drift
            if (_needsInitialSet || cosErr < 0.99995f || (currentTick - _lastCommandTick > 30))
            {
                // Clock time that turns the jump-start sun onto S_optimal
                float dot = MathHelper.Clamp(Vector3.Dot(_refDir, S_optimal), -1f, 1f);
                float sin = Vector3.Dot(Vector3.Cross(_refDir, S_optimal), A);
                double theta = Math.Atan2(sin, dot);
                if (theta < 0.0)
                    theta += 2.0 * Math.PI;

                MyVisualScriptLogicProvider.SunRotationSetTime(WrapTime(_refTime + (float)(theta / (2.0 * Math.PI) * _dayLength)));

                _lastCommandedDir = S_optimal;
                _hasCommanded = true;
                _lastCommandTick = currentTick;
                _sunMoved = true;
                _needsInitialSet = false;
            }
        }

        public void Unload()
        {
            if (_isSuppressed)
            {
                DeactivateSuppression(true);
            }

            if (_fading && MyAPIGateway.Session?.WeatherEffects != null)
                MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = null;

            _activeSources.Clear();
            _instance = null;
        }
    }
}
