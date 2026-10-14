using System;
using System.Timers;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Control;
using ForkMount_WPF.Engine.Manager;
using System.Diagnostics;
using PWLib.Astro;

namespace ForkMount_WPF.Engine.Tracking
{
    /// <summary>
    /// Satellite Tracking Engine: Controls Azimuth and Altitude axes to track satellite
    /// Implements PD Controller + Velocity Feedforward Control (PDF Controller)
    /// Separate gains for each axis with Kalman filtering and adaptive prediction
    /// Self-contained: Computes satellite position internally via SGP4 with built-in 15ms timer
    /// </summary>
    public class SatelliteTracking : IDisposable
    {
        private readonly AzimuthInfo _azimuthInfo;
        private readonly AltitudeInfo _altitudeInfo;
        private readonly CentralizedPollingManager _pollingManager;
        
        private bool _isTracking = false;
        
        // Satellite and Observer data
        private SatelliteTracker _satelliteTracker;
        private ObserverLocation _observerLocation;
        
        // Internal Timer (15ms = 66.67 Hz)
        private readonly System.Timers.Timer _trackingTimer;
        private const double TIMER_INTERVAL_MS = 15.0;
        
        // Target cache (for UI/debug only)
        private double _lastTargetAzimuth = 0.0;
        private double _lastTargetAltitude = 0.0;
        
        // Previous position errors for derivative calculation
        private double _previousAzimuthError = 0.0;
        private double _previousAltitudeError = 0.0;
        private bool _hasPreviousError = false;
        private DateTime _lastUpdateTime = DateTime.MinValue;
        
        // Integral accumulators for PID control
        private double _azimuthIntegral = 0.0;
        private double _altitudeIntegral = 0.0;
        
        // Kalman Filters for encoder noise reduction
        private KalmanFilter1D _azimuthKalman;
        private KalmanFilter1D _altitudeKalman;
        private bool _kalmanInitialized = false;
        
        // Velocity output filter
        private double _filteredAzVel = 0.0;
        private double _filteredAltVel = 0.0;
        
        // [PERFORMANCE] Cache AppSettings to avoid reading from file every 15ms
        private SatelliteTrackingPDGains _cachedGains;
        private AzimuthAxisConfig _cachedAzConfig;
        private AltitudeAxisConfig _cachedAltConfig;
        private DateTime _lastSettingsRefreshTime = DateTime.MinValue;
        private const double SETTINGS_REFRESH_INTERVAL_SEC = 5.0; // Refresh every 5 seconds
        
        // Debug logging throttle
        private DateTime _lastDebugLogTime = DateTime.MinValue;
        private const double DEBUG_LOG_INTERVAL_SEC = 2.0; // Log every 2 seconds
        
        // Limit approach detection - single warning per approach
        private bool _cwLimitWarningLogged = false;
        private bool _ccwLimitWarningLogged = false;
        private bool _altUpLimitWarningLogged = false;
        private bool _altDownLimitWarningLogged = false;
        
        // [NEW] Satellite switching criteria
        public event EventHandler<string> OnSatelliteUntrackable; // Event to notify UI to switch satellite
        
        public bool IsTracking => _isTracking;
        public double TargetAzimuth => _lastTargetAzimuth;
        public double TargetAltitude => _lastTargetAltitude;
        public SatelliteTracker CurrentSatellite => _satelliteTracker;

        
        
        public SatelliteTracking(AzimuthInfo azimuthInfo, AltitudeInfo altitudeInfo, CentralizedPollingManager pollingManager)
        {
            _azimuthInfo = azimuthInfo ?? throw new ArgumentNullException(nameof(azimuthInfo));
            _altitudeInfo = altitudeInfo ?? throw new ArgumentNullException(nameof(altitudeInfo));
            _pollingManager = pollingManager ?? throw new ArgumentNullException(nameof(pollingManager));
            
            // Initialize and START internal timer immediately (runs continuously)
            _trackingTimer = new System.Timers.Timer(TIMER_INTERVAL_MS);
            _trackingTimer.Elapsed += TrackingTimer_Elapsed;
            _trackingTimer.AutoReset = true;
            _trackingTimer.Start(); // Start immediately - runs forever
            
            SimpleLogger.Info($"[ENGINE] SatelliteTracking Engine initialized with {TIMER_INTERVAL_MS}ms timer (ALWAYS RUNNING)", "SatTrack");
        }

        /// <summary>
        /// Internal timer callback (15ms) - Runs continuously, checks if target is set
        /// </summary>
        private void TrackingTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                // Check if target satellite is set (null = not tracking)
                if (_satelliteTracker == null || _observerLocation == null)
                {
                    // No target set - skip this cycle (target is null = stopped)
                    return;
                }
                
                // [DEBUG] Log every 1 second to verify timer is running
                if (_lastUpdateTime == DateTime.MinValue || (NtpTimeService.Instance.GetCurrentTime() - _lastUpdateTime).TotalSeconds > 1.0)
                {
                    System.Diagnostics.Debug.WriteLine($"[ENGINE TIMER] Running - Target: {_satelliteTracker.Name}, IsTracking: {_isTracking}");
                }
                
                // Target is set - execute tracking computation
                ComputeAndSendCommands();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[ENGINE TIMER] Exception in tracking loop: {ex.Message}", "SatTrack");
            }
        }

        /// <summary>
        /// Set tracking target satellite and observer location
        /// NOTE: Creates a CLONE of the satellite tracker to avoid race conditions with UI
        /// </summary>
        public void SetTarget(SatelliteTracker satelliteTracker, ObserverLocation observerLocation)
        {
            if (satelliteTracker == null) throw new ArgumentNullException(nameof(satelliteTracker));
            if (observerLocation == null) throw new ArgumentNullException(nameof(observerLocation));
            
            // [CRITICAL] Clone the satellite tracker to avoid race conditions!
            // PWLib.Astro.Satellite has internal state that gets modified by RefreshPosition()
            // If UI and Engine share the same instance, they will corrupt each other's calculations
            _satelliteTracker = satelliteTracker.Clone();
            _observerLocation = observerLocation;
            
            SimpleLogger.Info($"[ENGINE] Target set: {_satelliteTracker.Name} (CLONED) at Lat={observerLocation.Latitude:F4}°, Lon={observerLocation.Longitude:F4}°, Alt={observerLocation.Altitude:F0}m", "SatTrack");
        }

        /// <summary>
        /// Start tracking satellite (sets target, timer already running)
        /// </summary>
        public void StartTracking()
        {
            if (_satelliteTracker == null || _observerLocation == null)
            {
                SimpleLogger.Error("[ENGINE] Cannot start tracking - Satellite or Observer not set! Call SetTarget() first.", "SatTrack");
                return;
            }
            
            _isTracking = true;
            
            // Reset derivative terms
            _previousAzimuthError = 0.0;
            _previousAltitudeError = 0.0;
            _hasPreviousError = false;
            _lastUpdateTime = DateTime.MinValue;
            _filteredAzVel = 0.0;
            _filteredAltVel = 0.0;
            _azimuthIntegral = 0.0;
            _altitudeIntegral = 0.0;
            
            // Reset limit warning flags
            _cwLimitWarningLogged = false;
            _ccwLimitWarningLogged = false;
            _altUpLimitWarningLogged = false;
            _altDownLimitWarningLogged = false;
            
            // Initialize Kalman Filters
            InitializeKalmanFilters();
            
            // [PERFORMANCE] Load settings into cache
            RefreshSettingsCache();
            
            // [DEBUG] Log initial satellite position
            var testPos = _satelliteTracker.GetTopocentric(_observerLocation, NtpTimeService.Instance.GetCurrentUtcTime());
            SimpleLogger.Info($"[TRACK START] Satellite: {_satelliteTracker.Name}", "SatTrack");
            SimpleLogger.Info($"[TRACK START] Observer: Lat={_observerLocation.Latitude:F4}°, Lon={_observerLocation.Longitude:F4}°, Alt={_observerLocation.Altitude:F0}m", "SatTrack");
            SimpleLogger.Info($"[TRACK START] Initial Position: Az={testPos.Azimuth:F2}°, El={testPos.Elevation:F2}°", "SatTrack");
            
            // Timer is already running - just set target to enable tracking
            SimpleLogger.Info($"[TRACK START] Tracking started (Timer already running)", "SatTrack");
        }

        /// <summary>
        /// Stop tracking satellite (sets target to null, timer keeps running)
        /// Sends STOP command to both axes immediately
        /// </summary>
        public void StopTracking()
        {
            SimpleLogger.Info("[TRACK STOP] Stopping tracking - Sending STOP commands to both axes", "SatTrack");
            
            // [CRITICAL] Send zero velocity commands FIRST (before setting target to null)
            // This ensures motors stop immediately via PollingManager
            _pollingManager.SetAzimuthCommandVelocity(0.0);
            _pollingManager.SetAltitudeCommandVelocity(0.0);
            SimpleLogger.Info("[TRACK STOP] ✓ Azimuth velocity = 0.0 deg/s", "SatTrack");
            SimpleLogger.Info("[TRACK STOP] ✓ Altitude velocity = 0.0 deg/s", "SatTrack");
            
            // Set tracking flag to false
            _isTracking = false;
            
            // Set target to null - this prevents tracking loop from executing
            _satelliteTracker = null;
            _observerLocation = null;
            
            // Reset all controller states
            _previousAzimuthError = 0.0;
            _previousAltitudeError = 0.0;
            _lastUpdateTime = DateTime.MinValue;
            _filteredAzVel = 0.0;
            _filteredAltVel = 0.0;
            _azimuthIntegral = 0.0;
            _altitudeIntegral = 0.0;
            _kalmanInitialized = false;
            
            // Reset limit warning flags
            _cwLimitWarningLogged = false;
            _ccwLimitWarningLogged = false;
            _altUpLimitWarningLogged = false;
            _altDownLimitWarningLogged = false;
            
            SimpleLogger.Info("[TRACK STOP] ✓ Tracking stopped (target set to null, timer keeps running)", "SatTrack");
        }

        /// <summary>
        /// Initialize Kalman Filters for both axes
        /// </summary>
        private void InitializeKalmanFilters()
        {
            var azConfig = AppSettings.GetAzimuthAxisConfig();
            var altConfig = AppSettings.GetAltitudeAxisConfig();
            
            // [CRITICAL FIX] Use PollingManager Info references (LIVE data!)
            double currentAzRaw = (_pollingManager.AzimuthInfo.Raw_Position / azConfig.Encoder_Resolution) * 360.0;
            double currentAltRaw = (_pollingManager.AltitudeInfo.Raw_Position / altConfig.Encoder_Resolution) * 360.0;
            
            _azimuthKalman = new KalmanFilter1D(currentAzRaw, 0);
            _altitudeKalman = new KalmanFilter1D(currentAltRaw, 0);
            
            // Tune Kalman for satellite tracking
            _azimuthKalman.SetProcessNoise(0.005, 0.05);
            _azimuthKalman.SetMeasurementNoise(0.10);
            
            _altitudeKalman.SetProcessNoise(0.005, 0.05);
            _altitudeKalman.SetMeasurementNoise(0.10);
            
            _kalmanInitialized = true;
            SimpleLogger.Info("[KALMAN] Filters initialized for satellite tracking", "SatTrack");
        }

        /// <summary>
        /// Refresh settings cache (only when needed to avoid file I/O every 15ms)
        /// </summary>
        private void RefreshSettingsCache()
        {
            _cachedGains = AppSettings.GetSatelliteTrackingPDGains();
            _cachedAzConfig = AppSettings.GetAzimuthAxisConfig();
            _cachedAltConfig = AppSettings.GetAltitudeAxisConfig();
            _lastSettingsRefreshTime = NtpTimeService.Instance.GetCurrentTime();
            
            // [DEBUG] Log gains and encoder resolution
            SimpleLogger.Info($"[GAINS] Az: Kp={_cachedGains.Azimuth_Kp} Kd={_cachedGains.Azimuth_Kd} VFF={_cachedGains.Azimuth_VffGain}", "SatTrack");
            SimpleLogger.Info($"[GAINS] Alt: Kp={_cachedGains.Altitude_Kp} Kd={_cachedGains.Altitude_Kd} VFF={_cachedGains.Altitude_VffGain}", "SatTrack");
            SimpleLogger.Info($"[ENCODER] Az Resolution={_cachedAzConfig.Encoder_Resolution:F0} counts/rev", "SatTrack");
            SimpleLogger.Info($"[ENCODER] Alt Resolution={_cachedAltConfig.Encoder_Resolution:F0} counts/rev", "SatTrack");
            SimpleLogger.Info($"[LIMITS] Az: {_cachedAzConfig.CCW_Limit_Deg}° to {_cachedAzConfig.CW_Limit_Deg}° | Max Vel: {_cachedAzConfig.Max_Control_Velocity_Deg_Sec}°/s", "SatTrack");
            SimpleLogger.Info($"[LIMITS] Alt: {_cachedAltConfig.Down_Limit_Deg}° to {_cachedAltConfig.Up_Limit_Deg}° | Max Vel: {_cachedAltConfig.Max_Velocity_Deg_Sec}°/s", "SatTrack");
        }

        /// <summary>
        /// Compute and send command velocities to axes (called every 15ms)
        /// Self-contained: Calculates satellite position via SGP4 with adaptive prediction
        /// Algorithm: Command_Vel = (Position_Error * Kp) + (Error_Rate * Kd) + (Target_Velocity * VFF)
        /// </summary>
        public void ComputeAndSendCommands()
        {
            // [THREAD-SAFETY] Capture local references to avoid NullReferenceException
            // StopTracking() on another thread can set these to null between the check and usage
            var tracker = _satelliteTracker;
            var observer = _observerLocation;
            
            if (!_isTracking || tracker == null || observer == null) 
                return;

            // Calculate dt
            DateTime now = NtpTimeService.Instance.GetCurrentTime(); // Use NTP-synced time
            double dt = 0.015; // Default 15ms
            if (_lastUpdateTime != DateTime.MinValue)
            {
                dt = (now - _lastUpdateTime).TotalSeconds;
                dt = Math.Max(0.001, Math.Min(dt, 0.1)); // Clamp dt to [1ms, 100ms]
            }
            _lastUpdateTime = now;

            // [PERFORMANCE] Refresh settings cache every 5 seconds (not every 15ms!)
            if ((now - _lastSettingsRefreshTime).TotalSeconds > SETTINGS_REFRESH_INTERVAL_SEC)
            {
                RefreshSettingsCache();
            }

            // Get PD gains from cache (not from file!)
            var gains = _cachedGains;
            var azConfig = _cachedAzConfig;
            var altConfig = _cachedAltConfig;

            // ===== STEP 1: Calculate Satellite Position (Real-time SGP4 every 15ms) =====
            // CRITICAL: Use NTP-synced UTC time for accurate satellite position calculation
            DateTime utcNow = NtpTimeService.Instance.GetCurrentUtcTime();
            
            // Get current satellite position
            var topoCurrent = tracker.GetTopocentric(observer, utcNow);
            double currentAltitude = topoCurrent.Elevation;
            
            // [CRITICAL] Check if satellite position calculation failed (Elevation = -999 means error)
            if (currentAltitude < -900)
            {
                // Position calculation failed - skip this cycle but don't stop tracking
                Debug.WriteLine($"[TRACK] Satellite position calculation failed - skipping this cycle");
                return;
            }
            
            // [ADAPTIVE PREDICTION] Calculate prediction time based on satellite altitude
            // Higher altitude = higher angular velocity = need longer prediction
            var altAngle = Angle.FromDegs(currentAltitude);
            double altitudeFactor = Angle.Sin(altAngle) * Angle.Sin(altAngle); // sin²(alt)
            double adaptivePredictionTimeMs = gains.Azimuth_BasePredictionTimeMs + 
                (gains.Azimuth_MaxPredictionTimeMs - gains.Azimuth_BasePredictionTimeMs) * altitudeFactor;
            
            double predictionTimeSec = adaptivePredictionTimeMs / 1000.0;
            
            // Get future satellite position with adaptive prediction
            var topoFuture = tracker.GetTopocentric(observer, utcNow.AddSeconds(predictionTimeSec));
            
            // Check if future position calculation also failed
            if (topoFuture.Elevation < -900)
            {
                Debug.WriteLine($"[TRACK] Future satellite position calculation failed - skipping this cycle");
                return;
            }
            
            // Calculate satellite velocity
            // [CRITICAL FIX] Wrap the angle difference BEFORE dividing by time!
            // The 180° threshold is for angle wrapping, not velocity wrapping.
            // Wrong order: (0.5° - 359.5°) / 0.1s = -3590°/s → wrap → -3230°/s (WRONG)
            // Right order: (0.5° - 359.5°) = -359° → wrap → +1° → /0.1s = +10°/s (CORRECT)
            double azDiff = topoFuture.Azimuth - topoCurrent.Azimuth;
            if (azDiff > 180.0) azDiff -= 360.0;
            else if (azDiff < -180.0) azDiff += 360.0;
            double satAzVel = azDiff / predictionTimeSec;
            
            double satAltVel = (topoFuture.Elevation - topoCurrent.Elevation) / predictionTimeSec;
            
            // Use real-time satellite position
            double targetAzimuth = topoCurrent.Azimuth;
            double targetAltitude = topoCurrent.Elevation;

            // Apply telescope offset so manual Direction Offset can work while satellite tracking is active.
            var mountInfo = _pollingManager?.MountInfo;
            if (mountInfo != null)
            {
                double offsetAzArcsec = mountInfo.TelescopeOffset_Az_Arcsec;
                double offsetAltArcsec = mountInfo.TelescopeOffset_Alt_Arcsec;
                if (offsetAzArcsec != 0.0 || offsetAltArcsec != 0.0)
                {
                    targetAzimuth += offsetAzArcsec / 3600.0;
                    targetAltitude += offsetAltArcsec / 3600.0;
                }
            }
            
            // Cache for UI
            _lastTargetAzimuth = targetAzimuth;
            _lastTargetAltitude = targetAltitude;

            // ===== AZIMUTH AXIS =====
            
            // [NEW] Get corrected position from MountInfo (with pointing model applied)
            double currentAzRaw = 0;
            if (_pollingManager?.MountInfo != null)
            {
                currentAzRaw = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
            }
            else
            {
                // Fallback: calculate manually
                currentAzRaw = (_pollingManager.AzimuthInfo.Raw_Position / azConfig.Encoder_Resolution) * 360.0;
            }
            double currentAzimuth = currentAzRaw;
            
            if (_kalmanInitialized && _azimuthKalman != null)
            {
                currentAzimuth = _azimuthKalman.Update(currentAzRaw, dt);
            }

            // Smart Normalization with proactive limit avoidance
            double targetAzNormalized = NormalizeAzimuthTarget(targetAzimuth, currentAzimuth,
                azConfig.CCW_Limit_Deg, azConfig.CW_Limit_Deg, satAzVel, azConfig.Max_Control_Velocity_Deg_Sec);

            // Compute position error
            double azimuthError = targetAzNormalized - currentAzimuth;

            // Compute error derivative (rate of change)
            double azimuthErrorRate = 0.0;
            if (_hasPreviousError)
            {
                azimuthErrorRate = (azimuthError - _previousAzimuthError) / dt;
            }
            _previousAzimuthError = azimuthError;

            // PID + VFF control law (separate gains)
            double azProportional = azimuthError * gains.Azimuth_Kp;
            
            // Integral: accumulate error over time with anti-windup clamp
            _azimuthIntegral += azimuthError * dt;
            _azimuthIntegral = Math.Clamp(_azimuthIntegral, -gains.Max_Integral_Deg, gains.Max_Integral_Deg);
            double azIntegral = _azimuthIntegral * gains.Azimuth_Ki;
            
            double azDerivative = azimuthErrorRate * gains.Azimuth_Kd;
            double azFeedforward = satAzVel * gains.Azimuth_VffGain;

            double azimuthCommandRaw = azProportional + azIntegral + azDerivative + azFeedforward;

            // Apply velocity filter (alpha from settings)
            double filterAlpha = gains.Velocity_Filter_Alpha;
            _filteredAzVel = (filterAlpha * azimuthCommandRaw) + 
                           ((1.0 - filterAlpha) * _filteredAzVel);

            // Velocity limiting (use max control velocity from AppSettings)
            _filteredAzVel = Math.Clamp(_filteredAzVel, -azConfig.Max_Control_Velocity_Deg_Sec, 
                                                        azConfig.Max_Control_Velocity_Deg_Sec);

            // Send to Azimuth axis via PollingManager
            _pollingManager.SetAzimuthCommandVelocity(_filteredAzVel);

            // ===== ALTITUDE AXIS =====

            // [NEW] Get corrected position from MountInfo (with pointing model applied)
            double currentAltRaw2 = 0;
            if (_pollingManager?.MountInfo != null)
            {
                currentAltRaw2 = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;
            }
            else
            {
                // Fallback: calculate manually
                currentAltRaw2 = (_pollingManager.AltitudeInfo.Raw_Position / altConfig.Encoder_Resolution) * 360.0;
            }
            double currentAltitude2 = currentAltRaw2;

            if (_kalmanInitialized && _altitudeKalman != null)
            {
                currentAltitude2 = _altitudeKalman.Update(currentAltRaw2, dt);
            }

            // Compute position error
            double altitudeError = targetAltitude - currentAltitude2;

            // Compute error derivative (rate of change)
            double altitudeErrorRate = 0.0;
            if (_hasPreviousError)
            {
                altitudeErrorRate = (altitudeError - _previousAltitudeError) / dt;
            }
            _previousAltitudeError = altitudeError;
            _hasPreviousError = true;

            // PID + VFF control law (separate gains)
            double altProportional = altitudeError * gains.Altitude_Kp;
            
            // Integral: accumulate error over time with anti-windup clamp
            _altitudeIntegral += altitudeError * dt;
            _altitudeIntegral = Math.Clamp(_altitudeIntegral, -gains.Max_Integral_Deg, gains.Max_Integral_Deg);
            double altIntegral = _altitudeIntegral * gains.Altitude_Ki;
            
            double altDerivative = altitudeErrorRate * gains.Altitude_Kd;
            double altFeedforward = satAltVel * gains.Altitude_VffGain;

            double altitudeCommandRaw = altProportional + altIntegral + altDerivative + altFeedforward;

            // Apply velocity filter (alpha from settings)
            _filteredAltVel = (filterAlpha * altitudeCommandRaw) + 
                            ((1.0 - filterAlpha) * _filteredAltVel);

            // Velocity limiting (use max velocity from AppSettings)
            _filteredAltVel = Math.Clamp(_filteredAltVel, -altConfig.Max_Velocity_Deg_Sec, 
                                                          altConfig.Max_Velocity_Deg_Sec);

            // Send to Altitude axis via PollingManager
            _pollingManager.SetAltitudeCommandVelocity(_filteredAltVel);
            
            // [NEW] Check if satellite is approaching limits and handle appropriately
            CheckAndHandleLimits(targetAzimuth, targetAltitude, currentAzimuth, currentAltitude2, azConfig, altConfig);
        }
        
        /// <summary>
        /// Check if satellite is approaching or exceeding limits
        /// Handles: Unwrap Azimuth, Switch to new satellite if untrackable
        /// Warnings fire only ONCE per limit approach to avoid log spam
        /// </summary>
        private void CheckAndHandleLimits(double targetAz_0to360, double targetAlt, double currentAz, double currentAlt, 
            AzimuthAxisConfig azConfig, AltitudeAxisConfig altConfig)
        {
            try
            {
                // ===== ALTITUDE LIMIT CHECK =====
                if (targetAlt < altConfig.Down_Limit_Deg)
                {
                    SimpleLogger.Warning($"[LIMIT] Satellite below horizon! Alt={targetAlt:F2}° < {altConfig.Down_Limit_Deg}° - UNTRACKABLE", "SatTrack");
                    OnSatelliteUntrackable?.Invoke(this, $"Satellite dropped below horizon (Alt={targetAlt:F2}°)");
                    StopTracking();
                    return;
                }
                
                if (targetAlt > altConfig.Up_Limit_Deg)
                {
                    SimpleLogger.Warning($"[LIMIT] Satellite above limit! Alt={targetAlt:F2}° > {altConfig.Up_Limit_Deg}° - UNTRACKABLE", "SatTrack");
                    OnSatelliteUntrackable?.Invoke(this, $"Satellite exceeded altitude limit (Alt={targetAlt:F2}°)");
                    StopTracking();
                    return;
                }
                
                // Altitude approach warnings (single-fire)
                if (targetAlt < (altConfig.Down_Limit_Deg + altConfig.Limit_Warning_Distance_Deg))
                {
                    if (!_altDownLimitWarningLogged)
                    {
                        SimpleLogger.Warning($"[LIMIT] Approaching DOWN limit! Alt={targetAlt:F2}° (limit={altConfig.Down_Limit_Deg}°)", "SatTrack");
                        _altDownLimitWarningLogged = true;
                    }
                }
                else
                {
                    _altDownLimitWarningLogged = false; // Reset when away from limit
                }
                
                if (targetAlt > (altConfig.Up_Limit_Deg - altConfig.Limit_Warning_Distance_Deg))
                {
                    if (!_altUpLimitWarningLogged)
                    {
                        SimpleLogger.Warning($"[LIMIT] Approaching UP limit! Alt={targetAlt:F2}° (limit={altConfig.Up_Limit_Deg}°)", "SatTrack");
                        _altUpLimitWarningLogged = true;
                    }
                }
                else
                {
                    _altUpLimitWarningLogged = false;
                }
                
                // ===== AZIMUTH LIMIT CHECK =====
                double azCCWLimit = azConfig.CCW_Limit_Deg;
                double azCWLimit = azConfig.CW_Limit_Deg;
                double azLimitWarningDist = azConfig.Limit_Warning_Distance_Deg;
                
                // Azimuth approach warnings (single-fire)
                if (currentAz <= (azCCWLimit + azLimitWarningDist))
                {
                    if (!_ccwLimitWarningLogged)
                    {
                        SimpleLogger.Warning($"[LIMIT] Approaching CCW limit! Current Az={currentAz:F2}° (limit={azCCWLimit}°)", "SatTrack");
                        _ccwLimitWarningLogged = true;
                    }
                }
                else
                {
                    _ccwLimitWarningLogged = false;
                }
                
                if (currentAz >= (azCWLimit - azLimitWarningDist))
                {
                    if (!_cwLimitWarningLogged)
                    {
                        SimpleLogger.Warning($"[LIMIT] Approaching CW limit! Current Az={currentAz:F2}° (limit={azCWLimit}°)", "SatTrack");
                        _cwLimitWarningLogged = true;
                    }
                }
                else
                {
                    _cwLimitWarningLogged = false;
                }
                
                // Check if satellite itself is outside azimuth tracking range
                double[] azOptions = { targetAz_0to360, targetAz_0to360 - 360.0, targetAz_0to360 + 360.0 };
                
                bool anyWithinLimits = false;
                foreach (var option in azOptions)
                {
                    if (option >= azCCWLimit && option <= azCWLimit)
                    {
                        anyWithinLimits = true;
                        break;
                    }
                }
                
                if (!anyWithinLimits)
                {
                    SimpleLogger.Warning($"[LIMIT] Satellite azimuth untrackable! No valid representation within limits ({azCCWLimit}° to {azCWLimit}°)", "SatTrack");
                    OnSatelliteUntrackable?.Invoke(this, $"Satellite azimuth outside trackable range");
                    StopTracking();
                    return;
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[LIMIT CHECK ERROR] {ex.Message}", "SatTrack");
            }
        }
        
        /// <summary>
        /// Smart Normalization with Proactive Limit Avoidance:
        /// When satellite is heading toward a limit, proactively unwrap to the opposite side
        /// BEFORE hitting the limit, so the mount can continuously track without getting stuck.
        /// </summary>
        private double NormalizeAzimuthTarget(double targetAz, double currentAz, 
            double minLimit, double maxLimit, double satVelocity = 0.0, double maxMountVelocity = 100.0)
        {
            // Try all 3 possible representations of target
            double[] options = { targetAz, targetAz - 360.0, targetAz + 360.0 };
            
            // Collect valid options (within limits)
            var validOptions = new List<(double angle, double distance)>();
            foreach (var option in options)
            {
                if (option >= minLimit && option <= maxLimit)
                {
                    validOptions.Add((option, Math.Abs(option - currentAz)));
                }
            }
            
            // No valid options → fall back to closest regardless of limits
            if (validOptions.Count == 0)
            {
                double bestOpt = options[0];
                double minDist = double.MaxValue;
                foreach (var opt in options)
                {
                    double d = Math.Abs(opt - currentAz);
                    if (d < minDist) { minDist = d; bestOpt = opt; }
                }
                return bestOpt;
            }
            
            // Single valid option → use it
            if (validOptions.Count == 1)
            {
                return validOptions[0].angle;
            }
            
            // Multiple valid options → use satellite velocity for proactive limit avoidance
            const double MIN_SAT_VELOCITY = 0.05;
            if (Math.Abs(satVelocity) >= MIN_SAT_VELOCITY)
            {
                // Dynamic proactive zone: scales with satellite velocity and mount traversal time
                // Mount needs ~360/maxVel seconds to traverse, so start early for fast satellites
                double traversalTimeSec = 360.0 / Math.Max(1.0, maxMountVelocity);
                double proactiveZoneDeg = Math.Clamp(Math.Abs(satVelocity) * traversalTimeSec * 3.0, 5.0, 60.0);
                
                if (satVelocity > 0) // Satellite moving CW → avoid CW limit
                {
                    var safeOptions = validOptions
                        .Where(v => (maxLimit - v.angle) > proactiveZoneDeg)
                        .OrderBy(v => v.distance)
                        .ToList();
                    
                    if (safeOptions.Count > 0)
                        return safeOptions[0].angle;
                }
                else // Satellite moving CCW → avoid CCW limit
                {
                    var safeOptions = validOptions
                        .Where(v => (v.angle - minLimit) > proactiveZoneDeg)
                        .OrderBy(v => v.distance)
                        .ToList();
                    
                    if (safeOptions.Count > 0)
                        return safeOptions[0].angle;
                }
            }
            
            // Default: closest valid option
            return validOptions.OrderBy(v => v.distance).First().angle;
        }

        /// <summary>
        /// Get tracking status string for UI
        /// </summary>
        public string GetStatusString()
        {
            if (!_isTracking)
                return "Tracking: OFF";

            var azimuthConfig = AppSettings.GetAzimuthAxisConfig();
            var altitudeConfig = AppSettings.GetAltitudeAxisConfig();
            double azCountsPerDeg = azimuthConfig.Encoder_Resolution / 360.0;
            double altCountsPerDeg = altitudeConfig.Encoder_Resolution / 360.0;
            
            // [CRITICAL FIX] Use PollingManager Info references (LIVE data!)
            double azError = _lastTargetAzimuth - (_pollingManager.AzimuthInfo.Raw_Position / azCountsPerDeg);
            double altError = _lastTargetAltitude - (_pollingManager.AltitudeInfo.Raw_Position / altCountsPerDeg);
            
            // Handle azimuth wrap-around
            if (azError > 180.0) azError -= 360.0;
            if (azError < -180.0) azError += 360.0;

            // Get RMS values from Info objects
            double azRms = _pollingManager.AzimuthInfo?.TrackingRmsError_Arcsec ?? 0.0;
            double altRms = _pollingManager.AltitudeInfo?.TrackingRmsError_Arcsec ?? 0.0;

            return $"Tracking: ON | Az Error: {azError:F2}° (RMS: {azRms:F2}\") | Alt Error: {altError:F2}° (RMS: {altRms:F2}\") | Target: Az {_lastTargetAzimuth:F1}° Alt {_lastTargetAltitude:F1}°";
        }

        /// <summary>
        /// Dispose pattern implementation - cleanup timer
        /// </summary>
        public void Dispose()
        {
            _trackingTimer?.Stop();
            _trackingTimer?.Dispose();
            SimpleLogger.Info("[ENGINE] SatelliteTracking Engine disposed", "SatTrack");
        }
    }
}
