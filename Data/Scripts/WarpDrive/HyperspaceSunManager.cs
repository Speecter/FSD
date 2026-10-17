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
    /// Visual sun flare is fought by dynamically positioning the sun directly behind the local player's active camera
    /// (cockpit, 3rd person, character, or camera block) using the Day/Night rotation cycle.
    /// Spectator camera (F8) is completely unaffected, retaining normal sun position and lighting.
    /// </summary>
    public class HyperspaceSunManager
    {
        private static HyperspaceSunManager _instance;
        public static HyperspaceSunManager Instance => _instance ?? (_instance = new HyperspaceSunManager());

        private readonly List<object> _activeSources = new List<object>();

        // Space Engineers standard celestial orientation basis
        private static readonly Vector3 DefaultSunRotationAxis = new Vector3(-0.3420637f, 0.7044079f, 0.6219341f);
        private static readonly Vector3 DefaultBaseSunDirection = new Vector3(0.3394673f, 0.7097954f, -0.6172134f);

        private Vector3 _sunRotationAxis;
        private Vector3 _lastObservedSunDir = Vector3.Zero;

        private bool _isSuppressed = false;
        private bool _sunMoved = false;
        private bool _originalSunRotationEnabled = false;
        private float _originalSunTime = 0f;
        private Vector3 _originalSunDir = Vector3.Zero;
        private float _dayLength = 120f;
        private bool _isSpectatorActive = false;
        private bool _needsInitialSet = false;
        private Vector3 _lastCommandedDir = Vector3.Zero;
        private bool _hasCommanded = false;
        private int _lastCommandTick = 0;

        public bool IsSuppressed => _isSuppressed;

        public void Init(string modPath)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;

            _sunRotationAxis = DefaultSunRotationAxis;
            _sunRotationAxis.Normalize();
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
                DeactivateSuppression();
            }
        }

        private void ActivateSuppression()
        {
            _isSuppressed = true;
            _needsInitialSet = true;
            _hasCommanded = false;
            _isSpectatorActive = false;

            try
            {
                // Capture exact initial sun state before any modification
                _originalSunTime = MyVisualScriptLogicProvider.SunRotationGetCurrentTime();
                _originalSunDir = MyVisualScriptLogicProvider.GetSunDirection();
                if (_originalSunDir.LengthSquared() > 1e-4f)
                {
                    _originalSunDir.Normalize();
                }
                else
                {
                    _originalSunDir = DefaultBaseSunDirection;
                }

                _originalSunRotationEnabled = MyAPIGateway.Session?.SessionSettings != null && MyAPIGateway.Session.SessionSettings.EnableSunRotation;

                if (!_originalSunRotationEnabled)
                {
                    MyVisualScriptLogicProvider.SunRotationEnabled(true);
                }

                _dayLength = MyVisualScriptLogicProvider.SunRotationGetDayLength();
                if (_dayLength <= 0.001f)
                    _dayLength = 120f;

                // Extinguish directional sunlight and shadows
                if (MyAPIGateway.Session?.WeatherEffects != null)
                {
                    MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = 0.0001f;
                }

                // Immediately place sun behind camera on this very frame before rendering
                UpdateSunPositionAndLighting();
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[HyperspaceSunManager] ActivateSuppression error: " + ex.Message);
            }
        }

        private void DeactivateSuppression()
        {
            _isSuppressed = false;

            try
            {
                // Restore original sun time
                if (_sunMoved)
                {
                    MyVisualScriptLogicProvider.SunRotationSetTime(_originalSunTime);
                    _sunMoved = false;
                }

                // Restore original sun rotation setting
                if (!_originalSunRotationEnabled)
                {
                    MyVisualScriptLogicProvider.SunRotationEnabled(false);
                }

                // Restore sunlight and shadows
                if (MyAPIGateway.Session?.WeatherEffects != null && MyAPIGateway.Session.WeatherEffects.SunIntensityOverride.HasValue)
                {
                    MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = null;
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
                TrackSunAxis();
                return;
            }

            UpdateSunPositionAndLighting();
        }

        public void UpdateDraw()
        {
            if (!_isSuppressed || MyAPIGateway.Utilities.IsDedicated)
                return;

            // Catch camera switches immediately on draw frames (e.g. 1st <-> 3rd person)
            UpdateSunPositionAndLighting();
        }

        /// <summary>
        /// Passively detects the world's exact sun rotation axis from sun movement during normal flight.
        /// </summary>
        private void TrackSunAxis()
        {
            try
            {
                Vector3 currentDir = MyVisualScriptLogicProvider.GetSunDirection();
                if (currentDir.LengthSquared() < 1e-4f)
                    return;

                currentDir.Normalize();

                if (_lastObservedSunDir != Vector3.Zero)
                {
                    float dot = MathHelper.Clamp(Vector3.Dot(_lastObservedSunDir, currentDir), -1f, 1f);
                    float angleDiff = (float)Math.Acos(dot);
                    // When sun has moved at least 0.5 degrees (~0.0087 rad)
                    if (angleDiff > 0.0087f)
                    {
                        Vector3 computedAxis = Vector3.Cross(_lastObservedSunDir, currentDir);
                        if (computedAxis.LengthSquared() > 1e-4f)
                        {
                            computedAxis.Normalize();
                            // Keep consistent orientation relative to standard SE axis
                            if (Vector3.Dot(computedAxis, DefaultSunRotationAxis) < 0f)
                                computedAxis = -computedAxis;

                            _sunRotationAxis = computedAxis;
                        }

                        _lastObservedSunDir = currentDir;
                    }
                }
                else
                {
                    _lastObservedSunDir = currentDir;
                }
            }
            catch
            {
            }
        }

        private void UpdateSunPositionAndLighting()
        {
            if (MyAPIGateway.Session == null)
                return;

            var controller = MyAPIGateway.Session.CameraController;
            bool isValidOnboardView = (controller is Sandbox.ModAPI.IMyCockpit)
                || (controller is VRage.Game.ModAPI.IMyCharacter)
                || (controller is Sandbox.ModAPI.Ingame.IMyCameraBlock);

            bool isSpectator = MyAPIGateway.Session.IsCameraUserControlledSpectator
                || !isValidOnboardView;

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
                    MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = 0.0001f;
                }
            }

            // Ensure sunlight override remains applied for onboard cameras
            if (MyAPIGateway.Session.WeatherEffects != null && !MyAPIGateway.Session.WeatherEffects.SunIntensityOverride.HasValue)
            {
                MyAPIGateway.Session.WeatherEffects.SunIntensityOverride = 0.0001f;
            }

            var camera = MyAPIGateway.Session.Camera;
            if (camera == null)
                return;

            Vector3 camForward = (Vector3)camera.WorldMatrix.Forward;
            Vector3 camUp = (Vector3)camera.WorldMatrix.Up;
            Vector3 camRight = (Vector3)camera.WorldMatrix.Right;

            Vector3 A = _sunRotationAxis;

            // Project camera forward onto the celestial plane perpendicular to the sun rotation axis
            Vector3 F_perp = camForward - Vector3.Dot(camForward, A) * A;
            Vector3 S_optimal;

            float perpLenSq = F_perp.LengthSquared();
            if (perpLenSq > 1e-4f)
            {
                // Optimal sun direction on the orbit circle is strictly opposite to camera forward projected onto the sun plane.
                // This maximizes the angular distance from the player's crosshairs at ALL angles.
                S_optimal = -Vector3.Normalize(F_perp);
            }
            else
            {
                // Degenerate pole case: camera line of sight is precisely along the celestial rotation axis (+A or -A).
                // In this singular case, every point on the orbit circle is 90 degrees from the camera forward vector.
                // Project the camera DOWN vector onto the sun plane, pushing the sun deep below the cockpit dashboard.
                Vector3 Down = -camUp;
                Vector3 Down_perp = Down - Vector3.Dot(Down, A) * A;
                if (Down_perp.LengthSquared() > 1e-4f)
                {
                    S_optimal = Vector3.Normalize(Down_perp);
                }
                else
                {
                    Vector3 fallback = Vector3.Cross(A, camRight);
                    if (fallback.LengthSquared() > 1e-4f)
                        S_optimal = Vector3.Normalize(fallback);
                    else
                        S_optimal = Vector3.Normalize(Vector3.Cross(A, Vector3.UnitY));
                }
            }

            // Pure Feed-Forward Re-Aim:
            // Compare the desired direction S_optimal against the LAST COMMANDED direction.
            // Never read back current sun direction/time for target calculation, which eliminates
            // stale readback feedback loops, draw-frame lag, and overshooting completely.
            float cosErr = _hasCommanded ? MathHelper.Clamp(Vector3.Dot(_lastCommandedDir, S_optimal), -1f, 1f) : -1f;
            int currentTick = (MyAPIGateway.Session != null) ? MyAPIGateway.Session.GameplayFrameCounter : 0;

            // Re-aim if uninitialized, if desired camera direction moved (> ~0.57 deg), or every 30 ticks to cancel clock drift
            if (_needsInitialSet || cosErr < 0.99995f || (currentTick - _lastCommandTick > 30))
            {
                // Absolute angle from the captured initial reference direction _originalSunDir to S_optimal around axis A
                float dot = MathHelper.Clamp(Vector3.Dot(_originalSunDir, S_optimal), -1f, 1f);
                Vector3 cross = Vector3.Cross(_originalSunDir, S_optimal);
                float sin = Vector3.Dot(cross, A);
                double theta = Math.Atan2(sin, dot);
                if (theta < 0.0)
                    theta += 2.0 * Math.PI;

                float targetTime = _originalSunTime + (float)(theta / (2.0 * Math.PI) * _dayLength);
                while (targetTime >= _dayLength) targetTime -= _dayLength;
                while (targetTime < 0f) targetTime += _dayLength;

                MyVisualScriptLogicProvider.SunRotationSetTime(targetTime);

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
                DeactivateSuppression();
            }

            _activeSources.Clear();
            _instance = null;
        }
    }
}
