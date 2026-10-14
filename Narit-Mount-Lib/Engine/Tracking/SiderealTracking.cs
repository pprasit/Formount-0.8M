using System;
using System.Timers;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Control;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.PointingModel;
using PWLib.Astro;

namespace ForkMount_WPF.Engine.Tracking
{
    /// <summary>
    /// Sidereal Tracking Engine: Tracks fixed celestial objects (stars, galaxies, DSO)
    /// Uses simple PDF Controller (P + D + VFF)
    /// Self-contained: Internal 15ms timer, only needs RaDec + Observer location
    /// 
    /// [CRITICAL FIX] Uses SKY coordinates for both target and feedback!
    ///   Target:  PWLib.ToAltAz(RA/Dec) → apparent sky position
    ///   Current: MountInfo.Act_Azimuth_Pos_Correct → corrected sky position (from EncoderToAltAz)
    /// Both are in the same coordinate space → error calculation is always correct,
    /// regardless of pointing model quality or phiEncOffset accuracy.
    /// Raw encoder values remain available in MountInfo.Act_Azimuth_Pos_Raw for monitoring.
    /// </summary>
    public class SiderealTracking : IDisposable
    {
        private readonly CentralizedPollingManager _pollingManager;
        
        // Target celestial coordinates - using PWLib.Astro.RaDec (same as RaDecMountTarget)
        private RaDec _targetRaDec = null;  // null = not tracking
        
        // Observer location using PWLib.Astro.LatLon
        private LatLon _observerSite = null;
        private double _observerHeightMeters = 0;
        
        // Keep ObserverLocation for compatibility
        private ObserverLocation _observerLocation;
        
        // Internal Timer (15ms = 66.67 Hz)
        private readonly System.Timers.Timer _trackingTimer;
        private const double TIMER_INTERVAL_MS = 15.0;
        
        // Tracking state
        private bool _isTracking = false;
        private DateTime _trackingStartTime;
        private DateTime _lastUpdateTime = DateTime.MinValue;
        
        // Target cache (for UI/debug)
        private double _lastTargetAzimuth = 0.0;
        private double _lastTargetAltitude = 0.0;
        
        // PIDF Controller gains (P + I + D + VFF)
        private PDControllerGains _azimuthGains;
        private PDControllerGains _altitudeGains;
        private double _azimuthVffGain = 0.2;
        private double _altitudeVffGain = 0.2;
        
        // Integral accumulators (for I-term)
        private double _azimuthIntegral = 0.0;
        private double _altitudeIntegral = 0.0;
        private const double INTEGRAL_WINDUP_LIMIT = 2.0; // Max integral accumulation (deg·s)
        
        // Previous position for velocity measurement
        private double _previousAzimuthPosition = 0.0;
        private double _previousAltitudePosition = 0.0;
        private bool _hasPreviousAzimuthPosition = false;
        private bool _hasPreviousAltitudePosition = false;
        
        // Kalman Filters (for encoder noise reduction)
        private KalmanFilter1D _azimuthKalman;
        private KalmanFilter1D _altitudeKalman;
        private bool _kalmanInitialized = false;
        private bool _kalmanEnabled = true;
        
        // On-target detection
        private bool _isOnTarget = false;
        
        // [CALIBRATION] Use Raw encoder position instead of Corrected
        // During calibration, pointing model corrections must be bypassed
        // because the purpose of calibration is to BUILD the model.
        // An existing (wrong) model would cause Raw=-148° → Corrected=207° → mount goes to wrong position!
        private bool _useRawPosition = false;
        
        // [PRECESSION] Flag to indicate if target RA/Dec is already Apparent (current epoch)
        // true  = target is Apparent (from AltAz→RaDec conversion) → skip J2000ToNow
        // false = target is J2000 (from catalog) → apply J2000ToNow precession
        private bool _isTargetApparent = false;
        
        // Cache axis configs
        private AzimuthAxisConfig _cachedAzConfig;
        private AltitudeAxisConfig _cachedAltConfig;
        private DateTime _lastConfigRefreshTime = DateTime.MinValue;
        private const double CONFIG_REFRESH_INTERVAL_SEC = 30.0;
        
        // Auto-refresh gains from settings
        private DateTime _lastGainsRefreshTime = DateTime.MinValue;
        private const double GAINS_REFRESH_INTERVAL_SEC = 5.0;
        
        // Prediction lead time (ms) — controller aims at future position
        private double _predictionTimeMs = 0.0;
        
        // Loop timing diagnostics
        private DateTime _lastDiagLogTime = DateTime.MinValue;
        private const double DIAG_LOG_INTERVAL_SEC = 5.0;
        private double _diagDtMin = double.MaxValue;
        private double _diagDtMax = 0.0;
        private double _diagDtSum = 0.0;
        private int _diagCycleCount = 0;
        private double _diagLastAzP, _diagLastAzI, _diagLastAzD, _diagLastAzF, _diagLastAzCmd;
        private double _diagLastAzError, _diagLastAltError;
        
        // Public properties
        public bool IsTracking => _isTracking;
        public bool IsOnTarget => _isOnTarget;
        public double? TargetRA => _targetRaDec?.Ra.Hours;
        public double? TargetDec => _targetRaDec?.Dec.Degs;
        public RaDec TargetRaDec => _targetRaDec;
        public double TargetAzimuth => _lastTargetAzimuth;
        public double TargetAltitude => _lastTargetAltitude;
        public bool UseRawPosition 
        { 
            get => _useRawPosition; 
            set 
            {
                if (_useRawPosition != value)
                {
                    _useRawPosition = value;
                    
                    // [CRITICAL FIX] Reset previous positions to prevent D-term velocity spike!
                    // When switching Raw→Corrected (or vice versa), position jumps discontinuously
                    // (e.g., 0.45° → -4.25°). Without reset, the D-term computes:
                    //   velocity = (new - old) / dt = (-4.25 - 0.45) / 0.015 = -313 deg/s
                    // This causes a massive velocity command spike that jerks the mount.
                    _previousAzimuthPosition = 0.0;
                    _previousAltitudePosition = 0.0;
                    _hasPreviousAzimuthPosition = false;
                    _hasPreviousAltitudePosition = false;
                    
                    // [CRITICAL FIX] Reset integral accumulators!
                    // When switching coordinate frames, stale integral values (saturated at ±2.0)
                    // create a persistent bias that causes overshoot. The integral must start
                    // fresh in the new coordinate frame.
                    _azimuthIntegral = 0.0;
                    _altitudeIntegral = 0.0;
                    
                    // [CRITICAL FIX] Reinitialize Kalman filters!
                    // The Kalman state (position estimate + velocity estimate) from the old
                    // coordinate frame is invalid in the new frame. Without reinit, the filter
                    // smooths the first few corrected readings toward the old RAW state,
                    // causing a transient error spike (up to ~600" observed in logs).
                    InitializeKalmanFilters();
                    
                    Debug.WriteLine($"[SIDEREAL] UseRawPosition changed to {value} — previous positions, integral, and Kalman reset");
                    
                    // [CRITICAL] Sync RmsTrackingManager to use same coordinate system!
                    // Prevents RMS mismatch where target is in Raw but current is in Corrected (or vice versa)
                    _pollingManager?.RmsTrackingManager?.SetUseRawPosition(value);
                }
            }
        }
        
        public SiderealTracking(CentralizedPollingManager pollingManager)
        {
            _pollingManager = pollingManager ?? throw new ArgumentNullException(nameof(pollingManager));
            
            // Load gains from settings
            LoadGainsFromSettings();
            
            // Start internal timer (runs continuously)
            _trackingTimer = new System.Timers.Timer(TIMER_INTERVAL_MS);
            _trackingTimer.Elapsed += TrackingTimer_Elapsed;
            _trackingTimer.AutoReset = true;
            _trackingTimer.Start();
            
            Debug.WriteLine($"[SIDEREAL] Engine initialized (15ms timer, PDF Controller)");
        }
        
        /// <summary>
        /// Timer callback (15ms) - Execute tracking computation
        /// </summary>
        private void TrackingTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                // Check if target is set
                if (_targetRaDec == null || _observerSite == null)
                    return;
                
                // Execute tracking
                ComputeAndSendCommands();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SIDEREAL TIMER] Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set tracking target (RA/Dec + Observer location)
        /// </summary>
        /// <param name="raHours">RA in hours (0-24)</param>
        /// <param name="decDegrees">Dec in degrees (-90 to +90)</param>
        /// <param name="observerLocation">Observer location</param>
        /// <param name="isApparent">true if coordinates are already Apparent (current epoch), false if J2000</param>
        public void SetTarget(double raHours, double decDegrees, ObserverLocation observerLocation, bool isApparent = false)
        {
            // Validate inputs
            if (raHours < 0 || raHours >= 24)
                throw new ArgumentOutOfRangeException(nameof(raHours), "RA must be 0-24 hours");
            if (decDegrees < -90 || decDegrees > 90)
                throw new ArgumentOutOfRangeException(nameof(decDegrees), "Dec must be ±90°");
            if (observerLocation == null)
                throw new ArgumentNullException(nameof(observerLocation), "Observer location cannot be null");
            
            // Log only when target changes significantly (avoid spam from calibration loop every 50ms)
            bool isSignificantChange = _targetRaDec == null || 
                Math.Abs(_targetRaDec.Ra.Hours - raHours) > 0.01 || 
                Math.Abs(_targetRaDec.Dec.Degs - decDegrees) > 0.1;
            
            // Create PWLib.Astro.RaDec object (same pattern as RaDecMountTarget)
            _targetRaDec = RaDec.FromHoursDegs(raHours, decDegrees);
            _isTargetApparent = isApparent;

            _pollingManager?.MountInfo?.SetTrackingCommandRaDec(raHours, decDegrees, isApparent);
            
            // Create PWLib.Astro.LatLon for observer site
            _observerLocation = observerLocation;
            _observerSite = new LatLon(
                Angle.FromDegs(observerLocation.Latitude),
                Angle.FromDegs(observerLocation.Longitude));
            _observerHeightMeters = observerLocation.Altitude;
            
            if (isSignificantChange)
            {
                Debug.WriteLine($"[SIDEREAL] Target set: RA={raHours:F4}h Dec={decDegrees:F4}° ({(isApparent ? "Apparent" : "J2000")})");
                Debug.WriteLine($"[SIDEREAL] Observer: Lat={observerLocation.Latitude:F4}° Lon={observerLocation.Longitude:F4}°");
            }
        }
        
        /// <summary>
        /// Set target using PWLib.Astro.RaDec object directly (same pattern as RaDecMountTarget)
        /// </summary>
        public void SetTarget(RaDec raDec, ObserverLocation observerLocation)
        {
            if (raDec == null)
                throw new ArgumentNullException(nameof(raDec), "RaDec cannot be null");
            if (observerLocation == null)
                throw new ArgumentNullException(nameof(observerLocation), "Observer location cannot be null");
            
            _targetRaDec = raDec.Clone();  // Clone to avoid external modifications (same as RaDecMountTarget)
            _isTargetApparent = false;

            _pollingManager?.MountInfo?.SetTrackingCommandRaDec(_targetRaDec.Ra.Hours, _targetRaDec.Dec.Degs, _isTargetApparent);
            
            _observerLocation = observerLocation;
            _observerSite = new LatLon(
                Angle.FromDegs(observerLocation.Latitude),
                Angle.FromDegs(observerLocation.Longitude));
            _observerHeightMeters = observerLocation.Altitude;
            
            Debug.WriteLine($"[SIDEREAL] Target set (RaDec): RA={raDec.Ra.Hours:F4}h Dec={raDec.Dec.Degs:F4}°");
        }
        
        /// <summary>
        /// Start tracking
        /// </summary>
        public void StartTracking()
        {
            if (_targetRaDec == null || _observerSite == null)
            {
                Debug.WriteLine("[SIDEREAL] Cannot start - no target set!");
                return;
            }
            
            _isTracking = true;
            _isOnTarget = false;
            _trackingStartTime = NtpTimeService.Instance.GetCurrentUtcTime(); // Use NTP-synced time
            _lastUpdateTime = DateTime.MinValue;
            
            // Reset previous position
            _previousAzimuthPosition = 0.0;
            _previousAltitudePosition = 0.0;
            _hasPreviousAzimuthPosition = false;
            _hasPreviousAltitudePosition = false;
            
            // Reset integral accumulators
            _azimuthIntegral = 0.0;
            _altitudeIntegral = 0.0;
            
            // Load configs and gains
            RefreshConfigCache();
            LoadGainsFromSettings();
            
            // Initialize Kalman filters for encoder noise reduction
            InitializeKalmanFilters();
            
            Debug.WriteLine("[SIDEREAL] Tracking STARTED");
        }
        
        /// <summary>
        /// Stop tracking
        /// </summary>
        public void StopTracking()
        {
            // Stop motors
            _pollingManager.SetAzimuthCommandVelocity(0.0);
            _pollingManager.SetAltitudeCommandVelocity(0.0);
            
            _isTracking = false;
            _isOnTarget = false;
            _targetRaDec = null;
            _observerSite = null;
            _observerLocation = null;
            _isTargetApparent = false;

            Debug.WriteLine("[SIDEREAL] Tracking STOPPED");
        }
        
        /// <summary>
        /// Compute and send velocity commands (PIDF Controller)
        /// </summary>
        private void ComputeAndSendCommands()
        {
            if (!_isTracking || _targetRaDec == null || _observerSite == null) 
                return;
            
            try
            {
                // ===== Calculate dt =====
                DateTime now = NtpTimeService.Instance.GetCurrentTime();
                double dt = 0.015;
                if (_lastUpdateTime != DateTime.MinValue)
                {
                    dt = (now - _lastUpdateTime).TotalSeconds;
                    dt = Math.Clamp(dt, 0.001, 0.1);
                }
                _lastUpdateTime = now;
                
                // ===== Auto-refresh configs and gains =====
                if ((now - _lastConfigRefreshTime).TotalSeconds > CONFIG_REFRESH_INTERVAL_SEC)
                    RefreshConfigCache();
                
                if ((now - _lastGainsRefreshTime).TotalSeconds > GAINS_REFRESH_INTERVAL_SEC)
                    LoadGainsFromSettings();
                
                var azConfig = _cachedAzConfig;
                var altConfig = _cachedAltConfig;
                
                // ===== Convert RA/Dec to SKY position using PWLib =====
                DateTime utcNow = NtpTimeService.Instance.GetCurrentUtcTime();
                double jd = AstroTime.JulianDay(utcNow);
                
                var raDecForAltAz = _isTargetApparent ? _targetRaDec : _targetRaDec.J2000ToNow();
                
                // Current position (for RMS measurement — actual sky position NOW)
                var altAzCurrent = raDecForAltAz.ToAltAz(_observerSite, jd);
                double currentTargetAltitude = altAzCurrent.Alt.Degs;
                double currentTargetAzimuth = altAzCurrent.Az.Degs;
                
                // Controller target: future position if prediction enabled, else current
                double controlTargetAltitude = currentTargetAltitude;
                double controlTargetAzimuth = currentTargetAzimuth;
                
                if (_predictionTimeMs > 0)
                {
                    double predSec = _predictionTimeMs / 1000.0;
                    double jdFuture = AstroTime.JulianDay(utcNow.AddSeconds(predSec));
                    var altAzFuture = raDecForAltAz.ToAltAz(_observerSite, jdFuture);
                    controlTargetAltitude = altAzFuture.Alt.Degs;
                    controlTargetAzimuth = altAzFuture.Az.Degs;
                }
                
                // ===== Apply Telescope Offset from CameraOffsetWindow =====
                var mountInfo = _pollingManager.MountInfo;
                if (mountInfo != null)
                {
                    double offsetAzArcsec = mountInfo.TelescopeOffset_Az_Arcsec;
                    double offsetAltArcsec = mountInfo.TelescopeOffset_Alt_Arcsec;
                    
                    if (offsetAzArcsec != 0.0 || offsetAltArcsec != 0.0)
                    {
                        controlTargetAzimuth  += offsetAzArcsec  / 3600.0;
                        controlTargetAltitude += offsetAltArcsec / 3600.0;
                        
                        // Also offset the "current" target used for RMS/UI
                        currentTargetAzimuth  += offsetAzArcsec  / 3600.0;
                        currentTargetAltitude += offsetAltArcsec / 3600.0;
                    }
                }
                
                // Cache current (actual) position for UI display
                _lastTargetAzimuth = currentTargetAzimuth;
                _lastTargetAltitude = currentTargetAltitude;
                
                // ===== Calculate Earth rotation velocity (for VFF) =====
                var (azEarthVelocity, altEarthVelocity) = CalculateEarthRotationVelocity(utcNow);
                
                // ===== AZIMUTH AXIS (target = future position for lead compensation) =====
                double azCommand = ComputeAzimuthCommand(
                    controlTargetAzimuth, azEarthVelocity, azConfig, dt);
                _pollingManager.SetAzimuthCommandVelocity(azCommand);
                
                // ===== ALTITUDE AXIS (target = future position for lead compensation) =====
                double altCommand = ComputeAltitudeCommand(
                    controlTargetAltitude, altEarthVelocity, altConfig, dt);
                _pollingManager.SetAltitudeCommandVelocity(altCommand);
                
                // ===== On-Target Detection =====
                double azPositionError = _diagLastAzError;
                double altPositionError = _diagLastAltError;
                bool azOnTarget = Math.Abs(azPositionError) < azConfig.Tracking_Sensitivity_Deg;
                bool altOnTarget = Math.Abs(altPositionError) < altConfig.Tracking_Sensitivity_Deg;
                _isOnTarget = azOnTarget && altOnTarget;
                
                // ===== Update RMS Manager (target = CURRENT position for accurate error) =====
                _pollingManager.RmsTrackingManager?.SetAzimuthTarget(currentTargetAzimuth);
                _pollingManager.RmsTrackingManager?.SetAltitudeTarget(currentTargetAltitude);
                
                // ===== Loop Timing Diagnostics (every 5s) =====
                _diagDtMin = Math.Min(_diagDtMin, dt);
                _diagDtMax = Math.Max(_diagDtMax, dt);
                _diagDtSum += dt;
                _diagCycleCount++;
                
                if ((now - _lastDiagLogTime).TotalSeconds >= DIAG_LOG_INTERVAL_SEC)
                {
                    double avgDt = _diagCycleCount > 0 ? _diagDtSum / _diagCycleCount : 0;
                    double avgHz = avgDt > 0 ? 1.0 / avgDt : 0;
                    Debug.WriteLine($"[SIDEREAL DIAG] Loop: {avgHz:F1}Hz (dt avg={avgDt*1000:F1}ms min={_diagDtMin*1000:F1}ms max={_diagDtMax*1000:F1}ms) cycles={_diagCycleCount}");
                    Debug.WriteLine($"[SIDEREAL DIAG] Az PIDF: P={_diagLastAzP:F6} I={_diagLastAzI:F6} D={_diagLastAzD:F6} F={_diagLastAzF:F6} → Cmd={_diagLastAzCmd:F6} deg/s");
                    Debug.WriteLine($"[SIDEREAL DIAG] Error: Az={_diagLastAzError*3600:F1}\" Alt={_diagLastAltError*3600:F1}\"  IntAcc: Az={_azimuthIntegral:F4} Alt={_altitudeIntegral:F4}");
                    
                    _diagDtMin = double.MaxValue;
                    _diagDtMax = 0.0;
                    _diagDtSum = 0.0;
                    _diagCycleCount = 0;
                    _lastDiagLogTime = now;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SIDEREAL] ComputeAndSendCommands error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Read current Azimuth position (degrees) based on tracking mode.
        /// Normal: Corrected sky position (from pointing model)
        /// Calibration: Raw encoder position (bypass model)
        /// </summary>
        private double ReadAzimuthPosition(AzimuthAxisConfig azConfig)
        {
            if (_useRawPosition)
            {
                return (_pollingManager.AzimuthInfo.Raw_Position / azConfig.Encoder_Resolution) * 360.0 + azConfig.Position_Offset_Deg;
            }
            else if (_pollingManager?.MountInfo != null)
            {
                return _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
            }
            else
            {
                return (_pollingManager.AzimuthInfo.Raw_Position / azConfig.Encoder_Resolution) * 360.0 + azConfig.Position_Offset_Deg;
            }
        }
        
        /// <summary>
        /// Read current Altitude position (degrees) based on tracking mode.
        /// Normal: Corrected sky position (from pointing model)
        /// Calibration: Raw encoder position (bypass model)
        /// </summary>
        private double ReadAltitudePosition(AltitudeAxisConfig altConfig)
        {
            if (_useRawPosition)
            {
                return (_pollingManager.AltitudeInfo.Raw_Position / altConfig.Encoder_Resolution) * 360.0 + altConfig.Position_Offset_Deg;
            }
            else if (_pollingManager?.MountInfo != null)
            {
                return _pollingManager.MountInfo.Act_Altitude_Pos_Correct;
            }
            else
            {
                return (_pollingManager.AltitudeInfo.Raw_Position / altConfig.Encoder_Resolution) * 360.0 + altConfig.Position_Offset_Deg;
            }
        }
        
        /// <summary>
        /// Azimuth PIDF Controller.
        /// Command = Kp×error + Ki×∫error·dt + Kd×velocityError + VFF×targetVelocity
        /// </summary>
        private double ComputeAzimuthCommand(
            double targetAzimuth_0to360, double earthVelocity, AzimuthAxisConfig config, double dt)
        {
            // 1. Read filtered position
            double rawPosition = ReadAzimuthPosition(config);
            double position = rawPosition;
            if (_kalmanEnabled && _kalmanInitialized && _azimuthKalman != null)
            {
                position = _azimuthKalman.Update(rawPosition, dt);
            }
            
            // 2. Unwrap target to mount range
            double target = UnwrapAzimuthToMountRange(
                targetAzimuth_0to360, position, config.CCW_Limit_Deg, config.CW_Limit_Deg);
            
            // 3. Position error
            double positionError = target - position;
            
            // 4. Measured velocity (from encoder)
            double measuredVelocity = 0.0;
            if (_hasPreviousAzimuthPosition)
            {
                measuredVelocity = (position - _previousAzimuthPosition) / dt;
            }
            _previousAzimuthPosition = position;
            _hasPreviousAzimuthPosition = true;
            
            // 5. Velocity error
            double velocityError = earthVelocity - measuredVelocity;
            
            // 6. PIDF terms
            double P = positionError * _azimuthGains.Kp;
            
            _azimuthIntegral += positionError * dt;
            _azimuthIntegral = Math.Clamp(_azimuthIntegral, -INTEGRAL_WINDUP_LIMIT, INTEGRAL_WINDUP_LIMIT);
            double I = _azimuthIntegral * _azimuthGains.Ki;
            
            double D = velocityError * _azimuthGains.Kd;
            double F = earthVelocity * _azimuthVffGain;
            
            double command = P + I + D + F;
            
            // 7. Velocity limiting
            command = Math.Clamp(command, -config.Max_Control_Velocity_Deg_Sec, 
                                          config.Max_Control_Velocity_Deg_Sec);
            
            // Cache for diagnostics
            _diagLastAzP = P; _diagLastAzI = I; _diagLastAzD = D; _diagLastAzF = F;
            _diagLastAzCmd = command; _diagLastAzError = positionError;
            
            return command;
        }
        
        /// <summary>
        /// Altitude PIDF Controller.
        /// Command = Kp×error + Ki×∫error·dt + Kd×velocityError + VFF×targetVelocity
        /// </summary>
        private double ComputeAltitudeCommand(
            double targetAltitude, double earthVelocity, AltitudeAxisConfig config, double dt)
        {
            // 1. Read filtered position
            double rawPosition = ReadAltitudePosition(config);
            double position = rawPosition;
            if (_kalmanEnabled && _kalmanInitialized && _altitudeKalman != null)
            {
                position = _altitudeKalman.Update(rawPosition, dt);
            }
            
            // 2. Position error
            double positionError = targetAltitude - position;
            
            // 3. Measured velocity (from encoder)
            double measuredVelocity = 0.0;
            if (_hasPreviousAltitudePosition)
            {
                measuredVelocity = (position - _previousAltitudePosition) / dt;
            }
            _previousAltitudePosition = position;
            _hasPreviousAltitudePosition = true;
            
            // 4. Velocity error
            double velocityError = earthVelocity - measuredVelocity;
            
            // 5. PIDF terms
            double P = positionError * _altitudeGains.Kp;
            
            _altitudeIntegral += positionError * dt;
            _altitudeIntegral = Math.Clamp(_altitudeIntegral, -INTEGRAL_WINDUP_LIMIT, INTEGRAL_WINDUP_LIMIT);
            double I = _altitudeIntegral * _altitudeGains.Ki;
            
            double D = velocityError * _altitudeGains.Kd;
            double F = earthVelocity * _altitudeVffGain;
            
            double command = P + I + D + F;
            
            // 6. Velocity limiting
            command = Math.Clamp(command, -config.Max_Velocity_Deg_Sec, 
                                          config.Max_Velocity_Deg_Sec);
            
            // Cache for diagnostics
            _diagLastAltError = positionError;
            
            return command;
        }
        
        /// <summary>
        /// Unwrap azimuth from 0-360° to mount's range
        /// </summary>
        private double UnwrapAzimuthToMountRange(double targetAz_0to360, double currentAz, double ccwLimit, double cwLimit)
        {
            double[] options = new double[] {
                targetAz_0to360,
                targetAz_0to360 - 360.0,
                targetAz_0to360 + 360.0
            };
            
            var validOptions = options
                .Where(opt => opt >= ccwLimit && opt <= cwLimit)
                .Select(opt => (angle: opt, distance: Math.Abs(opt - currentAz)))
                .OrderBy(x => x.distance)
                .ToList();
            
            return validOptions.Count > 0 ? validOptions[0].angle : targetAz_0to360 - 360.0;
        }
        
        /// <summary>
        /// Calculate Earth rotation velocity (for VFF feedforward)
        /// Uses PWLib.ToAltAz() at two time points to get SKY velocity (deg/sec)
        /// Since we track in SKY coordinates, VFF should also be in SKY coordinates.
        /// [CRITICAL FIX] Must apply same precession as target position calculation
        /// to avoid coordinate mismatch between P-term target and VFF velocity.
        /// </summary>
        private (double azVelocity, double altVelocity) CalculateEarthRotationVelocity(DateTime utcNow)
        {
            if (_targetRaDec == null || _observerSite == null)
                return (0, 0);
            
            const double dt = 0.1;  // 100ms delta for velocity calculation
            
            double jd1 = AstroTime.JulianDay(utcNow);
            double jd2 = AstroTime.JulianDay(utcNow.AddSeconds(dt));
            
            // [CRITICAL FIX] Apply same precession as ComputeAndSendCommands (line 327)
            // Without this, VFF velocity is computed in J2000 frame while target position
            // is in Apparent frame → P-term must constantly compensate the mismatch → oscillation
            var raDecForVff = _isTargetApparent ? _targetRaDec : _targetRaDec.J2000ToNow();
            
            var altAz1 = raDecForVff.ToAltAz(_observerSite, jd1);
            var altAz2 = raDecForVff.ToAltAz(_observerSite, jd2);
            double alt1 = altAz1.Alt.Degs;
            double az1 = altAz1.Az.Degs;
            double alt2 = altAz2.Alt.Degs;
            double az2 = altAz2.Az.Degs;
            
            // Handle azimuth wrap-around
            double azDiff = az2 - az1;
            if (azDiff > 180.0) azDiff -= 360.0;
            if (azDiff < -180.0) azDiff += 360.0;
            
            double azVelocity = azDiff / dt;
            double altVelocity = (alt2 - alt1) / dt;
            
            return (azVelocity, altVelocity);
        }
        
        /// <summary>
        /// Load PD and VFF gains from settings
        /// </summary>
        private void LoadGainsFromSettings()
        {
            var settings = AppSettings.GetSiderealTrackingPDGains();
            
            _azimuthGains = new PDControllerGains 
            { 
                Kp = settings.Azimuth_Kp, 
                Ki = settings.Azimuth_Ki,
                Kd = settings.Azimuth_Kd 
            };
            
            _altitudeGains = new PDControllerGains 
            { 
                Kp = settings.Altitude_Kp, 
                Ki = settings.Altitude_Ki,
                Kd = settings.Altitude_Kd 
            };
            
            _azimuthVffGain = settings.Azimuth_VffGain;
            _altitudeVffGain = settings.Altitude_VffGain;
            _predictionTimeMs = settings.PredictionTime_Ms;
            _lastGainsRefreshTime = NtpTimeService.Instance.GetCurrentTime();
        }
        
        /// <summary>
        /// Refresh axis configs cache
        /// </summary>
        private void RefreshConfigCache()
        {
            _cachedAzConfig = AppSettings.GetAzimuthAxisConfig();
            _cachedAltConfig = AppSettings.GetAltitudeAxisConfig();
            _lastConfigRefreshTime = NtpTimeService.Instance.GetCurrentTime();
        }
        
        /// <summary>
        /// Initialize Kalman Filters (reduces encoder noise)
        /// Loads enable/disable and tuning parameters from settings
        /// </summary>
        private void InitializeKalmanFilters()
        {
            var azConfig = _cachedAzConfig;
            var altConfig = _cachedAltConfig;
            
            // Guard: configs may not be cached yet if called from UseRawPosition setter
            // before the first RefreshConfigCache() call
            if (azConfig == null || altConfig == null)
            {
                RefreshConfigCache();
                azConfig = _cachedAzConfig;
                altConfig = _cachedAltConfig;
            }
            
            if (azConfig == null || altConfig == null)
            {
                _kalmanInitialized = false;
                return;
            }
            
            var gains = AppSettings.GetSiderealTrackingPDGains();
            
            _kalmanEnabled = gains.Kalman_Enabled;
            
            if (!_kalmanEnabled)
            {
                _kalmanInitialized = false;
                Debug.WriteLine("[SIDEREAL] Kalman filters DISABLED by settings");
                return;
            }
            
            // Get current raw positions
            double currentAzRaw = (_pollingManager.AzimuthInfo.Raw_Position / azConfig.Encoder_Resolution) * 360.0 + azConfig.Position_Offset_Deg;
            double currentAltRaw = (_pollingManager.AltitudeInfo.Raw_Position / altConfig.Encoder_Resolution) * 360.0 + altConfig.Position_Offset_Deg;
            
            // Create Kalman filters
            _azimuthKalman = new KalmanFilter1D(currentAzRaw, 0);
            _altitudeKalman = new KalmanFilter1D(currentAltRaw, 0);
            
            // Apply tuning from settings
            _azimuthKalman.SetProcessNoise(gains.Kalman_ProcessNoise_Position, gains.Kalman_ProcessNoise_Velocity);
            _azimuthKalman.SetMeasurementNoise(gains.Kalman_MeasurementNoise);
            
            _altitudeKalman.SetProcessNoise(gains.Kalman_ProcessNoise_Position, gains.Kalman_ProcessNoise_Velocity);
            _altitudeKalman.SetMeasurementNoise(gains.Kalman_MeasurementNoise);
            
            _kalmanInitialized = true;
            Debug.WriteLine($"[SIDEREAL] Kalman filters initialized (Q_pos={gains.Kalman_ProcessNoise_Position:E1} Q_vel={gains.Kalman_ProcessNoise_Velocity:E1} R={gains.Kalman_MeasurementNoise:F2})");
        }
        
        /// <summary>
        /// Update PD gains (for live tuning from UI)
        /// </summary>
        public void SetGains(PDControllerGains azGains, PDControllerGains altGains)
        {
            _azimuthGains = azGains;
            _altitudeGains = altGains;
            _lastGainsRefreshTime = NtpTimeService.Instance.GetCurrentTime();
            Debug.WriteLine($"[SIDEREAL] Gains updated (live) - Az: Kp={azGains.Kp:F2} Ki={azGains.Ki:F3} Kd={azGains.Kd:F3}");
        }
        
        /// <summary>
        /// Update Kalman filter settings (for live tuning from UI)
        /// Re-initializes filters with new parameters
        /// </summary>
        public void SetKalmanSettings(bool enabled, double processNoisePos, double processNoiseVel, double measurementNoise)
        {
            _kalmanEnabled = enabled;
            
            if (!enabled)
            {
                _kalmanInitialized = false;
                Debug.WriteLine("[SIDEREAL] Kalman filters DISABLED (live)");
                return;
            }
            
            // Re-initialize with new parameters
            if (_azimuthKalman != null)
            {
                _azimuthKalman.SetProcessNoise(processNoisePos, processNoiseVel);
                _azimuthKalman.SetMeasurementNoise(measurementNoise);
            }
            if (_altitudeKalman != null)
            {
                _altitudeKalman.SetProcessNoise(processNoisePos, processNoiseVel);
                _altitudeKalman.SetMeasurementNoise(measurementNoise);
            }
            
            Debug.WriteLine($"[SIDEREAL] Kalman updated (live) - Q_pos={processNoisePos:E1} Q_vel={processNoiseVel:E1} R={measurementNoise:F2}");
        }
        
        /// <summary>
        /// Update VFF gains (for live tuning from UI)
        /// </summary>
        public void SetVffGains(double azVff, double altVff)
        {
            if (azVff < 0 || azVff > 1.0)
                throw new ArgumentOutOfRangeException(nameof(azVff), "VFF must be 0.0 - 1.0");
            if (altVff < 0 || altVff > 1.0)
                throw new ArgumentOutOfRangeException(nameof(altVff), "VFF must be 0.0 - 1.0");
            
            _azimuthVffGain = azVff;
            _altitudeVffGain = altVff;
            _lastGainsRefreshTime = NtpTimeService.Instance.GetCurrentTime();
            Debug.WriteLine($"[SIDEREAL] VFF updated (live) - Az: {azVff:F2}, Alt: {altVff:F2}");
        }
        
        /// <summary>
        /// Update prediction lead time (for live tuning from UI)
        /// </summary>
        public void SetPredictionTime(double predictionMs)
        {
            _predictionTimeMs = Math.Max(0, predictionMs);
            _lastGainsRefreshTime = NtpTimeService.Instance.GetCurrentTime();
            Debug.WriteLine($"[SIDEREAL] Prediction time updated (live) - {_predictionTimeMs:F1}ms");
        }
        
        /// <summary>
        /// Get current tracking status
        /// </summary>
        public SiderealTrackingStatus GetStatus()
        {
            // CRITICAL: Use NTP-synced UTC time for accurate calculations
            DateTime utcNow = NtpTimeService.Instance.GetCurrentUtcTime();
            double jd = AstroTime.JulianDay(utcNow);
            
            double targetAlt = 0, targetAz = 0;
            if (_isTracking && _targetRaDec != null && _observerSite != null)
            {
                var raDecForAltAz = _isTargetApparent ? _targetRaDec : _targetRaDec.J2000ToNow();
                var altAz = raDecForAltAz.ToAltAz(_observerSite, jd);
                targetAlt = altAz.Alt.Degs;
                targetAz = altAz.Az.Degs;
            }
            
            // Calculate LST using PWLib.Astro
            double lst = 0;
            if (_observerSite != null)
            {
                lst = _observerSite.Lmst(jd).Hours;
            }
            
            return new SiderealTrackingStatus
            {
                IsTracking = _isTracking,
                IsOnTarget = _isOnTarget,
                TargetRaDec = _targetRaDec,  // Pass the RaDec object for proper formatting
                TargetRA = _targetRaDec?.Ra.Hours ?? 0,
                TargetDec = _targetRaDec?.Dec.Degs ?? 0,
                CurrentAltitude = targetAlt,
                CurrentAzimuth = targetAz,
                TrackingDuration = _isTracking ? utcNow - _trackingStartTime : TimeSpan.Zero,
                LocalSiderealTime = lst
            };
        }
        
        /// <summary>
        /// Get tracking status string for UI
        /// </summary>
        public string GetStatusString()
        {
            if (!_isTracking)
                return "Tracking: OFF";
            
            var azConfig = AppSettings.GetAzimuthAxisConfig();
            var altConfig = AppSettings.GetAltitudeAxisConfig();
            
            // Use corrected position for error display (consistent with tracking)
            // Raw position is still available in MountInfo.Act_Azimuth_Pos_Raw for monitoring
            double currentAz = _pollingManager.MountInfo?.Act_Azimuth_Pos_Correct
                ?? ((_pollingManager.AzimuthInfo.Raw_Position / azConfig.Encoder_Resolution) * 360.0 + azConfig.Position_Offset_Deg);
            double currentAlt = _pollingManager.MountInfo?.Act_Altitude_Pos_Correct
                ?? ((_pollingManager.AltitudeInfo.Raw_Position / altConfig.Encoder_Resolution) * 360.0 + altConfig.Position_Offset_Deg);
            
            double azError = _lastTargetAzimuth - currentAz;
            if (azError > 180.0) azError -= 360.0;
            if (azError < -180.0) azError += 360.0;
            double altError = _lastTargetAltitude - currentAlt;
            
            double azRms = _pollingManager.AzimuthInfo?.TrackingRmsError_Arcsec ?? 0.0;
            double altRms = _pollingManager.AltitudeInfo?.TrackingRmsError_Arcsec ?? 0.0;
            
            string status = _isOnTarget ? "LOCKED" : "TRACKING";
            
            return $"{status} | Az Error: {azError:F2}° (RMS: {azRms:F2}\") | Alt Error: {altError:F2}° (RMS: {altRms:F2}\") | Target: Az {_lastTargetAzimuth:F1}° Alt {_lastTargetAltitude:F1}°";
        }
        
        /// <summary>
        /// Dispose pattern
        /// </summary>
        public void Dispose()
        {
            _trackingTimer?.Stop();
            _trackingTimer?.Dispose();
            Debug.WriteLine("[SIDEREAL] Engine disposed");
        }
    }
    
    /// <summary>
    /// PID Controller gains
    /// </summary>
    public class PDControllerGains
    {
        public double Kp { get; set; }  // Proportional gain
        public double Ki { get; set; }  // Integral gain
        public double Kd { get; set; }  // Derivative gain
    }
    
    /// <summary>
    /// Sidereal tracking status
    /// </summary>
    public class SiderealTrackingStatus
    {
        public bool IsTracking { get; set; }
        public bool IsOnTarget { get; set; }
        public RaDec TargetRaDec { get; set; }         // PWLib.Astro.RaDec object
        public double TargetRA { get; set; }          // Hours (for backward compatibility)
        public double TargetDec { get; set; }         // Degrees (for backward compatibility)
        public double CurrentAltitude { get; set; }   // Degrees
        public double CurrentAzimuth { get; set; }    // Degrees
        public TimeSpan TrackingDuration { get; set; }
        public double LocalSiderealTime { get; set; } // Hours
        
        /// <summary>
        /// Format RA as HH:MM:SS.s string
        /// </summary>
        public string RAString
        {
            get
            {
                double raHours = TargetRaDec?.Ra.Hours ?? TargetRA;
                int h = (int)raHours;
                int m = (int)((raHours - h) * 60);
                double s = ((raHours - h) * 60 - m) * 60;
                return $"{h:00}h {m:00}m {s:00.0}s";
            }
        }
        
        /// <summary>
        /// Format Dec as ±DD°MM'SS.s" string
        /// </summary>
        public string DecString
        {
            get
            {
                double decDeg = TargetRaDec?.Dec.Degs ?? TargetDec;
                string sign = decDeg >= 0 ? "+" : "-";
                double absDec = Math.Abs(decDeg);
                int d = (int)absDec;
                int m = (int)((absDec - d) * 60);
                double s = ((absDec - d) * 60 - m) * 60;
                return $"{sign}{d:00}° {m:00}' {s:00.0}\"";
            }
        }
    }
}
