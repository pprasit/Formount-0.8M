using System;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Model for Application Settings
    /// For Elmo Platinum Drive Series
    /// </summary>
    public class AppSettingsModel
    {
        /// <summary>
        /// Serial Number for Platinum Drive (identifies which mount to connect to)
        /// One Drive controls all axes (AX1=Azimuth, AX2=Altitude, etc.)
        /// Different mounts (ForkMount, L-Mount) have different Drive S/N
        /// </summary>
        public string Drive_SN { get; set; } = string.Empty;
        
        /// <summary>
        /// Legacy: Maps to Drive_SN for backward compatibility with old JSON ("Azimuth_SN")
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("Azimuth_SN")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public string Azimuth_SN_Legacy
        {
            get => null; // Never write this property
            set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(Drive_SN)) Drive_SN = value; }
        }

        /// <summary>
        /// Baud Rate for Azimuth Drive
        /// Default: 921600 (fastest for USB connection)
        /// </summary>
        public int Azimuth_BaudRate { get; set; } = 921600;

        /// <summary>
        /// Polling Interval (ms) for Azimuth Drive
        /// </summary>
        public int Azimuth_PollingInterval { get; set; } = 100;

        /// <summary>
        /// Azimuth Axis Configuration
        /// </summary>
        public AzimuthAxisConfig Azimuth_AxisConfig { get; set; } = new AzimuthAxisConfig();

        /// <summary>
        /// Azimuth PD Controller Parameters
        /// </summary>
        public PDControllerParameters Azimuth_PD_Controller { get; set; } = new PDControllerParameters();

        /// <summary>
        /// Azimuth Tuning Parameters
        /// </summary>
        public TuningParameters Azimuth_Tuning_Parameters { get; set; } = new TuningParameters();

        /// <summary>
        /// Tuning Steps
        /// </summary>
        public TuningSteps Tuning_Steps { get; set; } = new TuningSteps();

        /// <summary>
        /// PD Controller Parameters (Legacy - kept for backward compatibility)
        /// </summary>
        public PDControllerParameters PD_Controller { get; set; } = new PDControllerParameters();

        /// <summary>
        /// Tuning Parameters (Legacy - kept for backward compatibility)
        /// </summary>
        public TuningParameters Tuning_Parameters { get; set; } = new TuningParameters();

        /// <summary>
        /// Tuning Offsets for Fine Tuning
        /// </summary>
        public TuningOffsets Tuning_Offsets { get; set; } = new TuningOffsets();

        /// <summary>
        /// [Deprecated] Altitude_SN is no longer used � Altitude shares the same Drive as Azimuth.
        /// Kept for backward compatibility when reading old JSON files.
        /// </summary>
        [Obsolete("Use Drive_SN instead. Altitude shares the same Drive.")]
        public string Altitude_SN { get; set; } = string.Empty;

        /// <summary>
        /// Baud Rate for Altitude Drive
        /// Default: 921600 (fastest for USB connection)
        /// </summary>
        public int Altitude_BaudRate { get; set; } = 921600;

        /// <summary>
        /// Polling Interval (ms) for Altitude Drive
        /// </summary>
        public int Altitude_PollingInterval { get; set; } = 100;

        /// <summary>
        /// Altitude Axis Configuration
        /// </summary>
        public AltitudeAxisConfig Altitude_AxisConfig { get; set; } = new AltitudeAxisConfig();

        /// <summary>
        /// Altitude PD Controller Parameters
        /// </summary>
        public PDControllerParameters Altitude_PD_Controller { get; set; } = new PDControllerParameters();

        /// <summary>
        /// Altitude Tuning Parameters
        /// </summary>
        public TuningParameters Altitude_Tuning_Parameters { get; set; } = new TuningParameters();

        /// <summary>
        /// Observer Location Settings
        /// </summary>
        public ObserverLocationSettings Observer_Location { get; set; } = new ObserverLocationSettings();

        /// <summary>
        /// System Settings (NTP, etc.)
        /// </summary>
        public SystemSettings System_Settings { get; set; } = new SystemSettings();

        /// <summary>
        /// Satellite Tracking PDF Controller Gains
        /// </summary>
        public SatelliteTrackingPDGains Satellite_Tracking_PD_Gains { get; set; } = new SatelliteTrackingPDGains();

        /// <summary>
        /// Sidereal Tracking PD Controller Gains
        /// </summary>
        public SiderealTrackingPDGains Sidereal_Tracking_PD_Gains { get; set; } = new SiderealTrackingPDGains();

        /// <summary>
        /// Mount Calibration Settings
        /// </summary>
        public MountCalibrationSettings Mount_Calibration_Settings { get; set; } = new MountCalibrationSettings();

        /// <summary>
        /// ZWO Camera Settings
        /// </summary>
        public ZWOCameraSettings ZWO_Camera_Settings { get; set; } = new ZWOCameraSettings();

        /// <summary>
        /// CameraOffset Window Histogram Stretch Profile (separate from CameraSettings)
        /// </summary>
        public HistogramStretchProfile CameraOffset_HistogramStretch { get; set; } = new HistogramStretchProfile();

        /// <summary>
        /// FITS Auto Save - directory path
        /// </summary>
        public string FitsAutoSaveDirectory { get; set; } = "";

        /// <summary>
        /// FITS Auto Save - filename prefix
        /// </summary>
        public string FitsAutoSavePrefix { get; set; } = "Light";

        /// <summary>
        /// Instrument Axis (Axis 3 / FORK1) — jog speed (deg/s)
        /// </summary>
        public double Instrument_JogSpeed_Deg { get; set; } = 1.0;

        /// <summary>
        /// Instrument Axis (Axis 3 / FORK1) — last goto target position (deg)
        /// </summary>
        public double Instrument_TargetPosition_Deg { get; set; } = 0.0;

        /// <summary>
        /// Instrument Axis (Axis 3 / FORK1) — goto velocity (deg/s)
        /// </summary>
        public double Instrument_GotoVelocity_Deg { get; set; } = 1.0;

        /// <summary>
        /// Instrument Axis (Axis 3 / FORK1) — invert automatic field-derotation direction.
        /// Useful when the hardware sign convention is opposite to the sky-rotation convention.
        /// </summary>
        public bool Instrument_DerotationDirectionInverted { get; set; } = false;

        /// <summary>
        /// M3 Axis — last used target position (deg)
        /// </summary>
        public double M3_TargetPosition_Deg { get; set; } = 0.0;

        /// <summary>
        /// M3 Axis — last used velocity (deg/s)
        /// </summary>
        public double M3_Velocity_Deg { get; set; } = 1.0;

        /// <summary>
        /// M3 Axis — Port 1 preset position (deg)
        /// </summary>
        public double M3_Port1_Deg { get; set; } = 0.0;

        /// <summary>
        /// M3 Axis — Port 2 preset position (deg)
        /// </summary>
        public double M3_Port2_Deg { get; set; } = 90.0;

        /// <summary>
        /// M3 Axis — Step size for +/- jog buttons (deg)
        /// </summary>
        public double M3_Step_Deg { get; set; } = 0.01;

        /// <summary>
        /// M3 Axis — Acceleration (deg/s²)
        /// </summary>
        public double M3_Acceleration_Deg_S2 { get; set; } = 10.0;

        /// <summary>
        /// M3 Axis — Deceleration (deg/s²)
        /// </summary>
        public double M3_Deceleration_Deg_S2 { get; set; } = 10.0;

        /// <summary>
        /// M3 Axis (Axis 4 / FORK2) configuration — encoder resolution, axis number, velocity limits
        /// </summary>
        public M3AxisConfig M3_AxisConfig { get; set; } = new M3AxisConfig();

        /// <summary>
        /// Field-Derotator Axis (Axis 3 / FORK1) configuration — encoder resolution, axis number, velocity limits
        /// </summary>
        public FieldDerotatorAxisConfig FieldDerotator_AxisConfig { get; set; } = new FieldDerotatorAxisConfig();

        /// <summary>
        /// Last Modified Date
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Settings Version (Platinum Edition)
        /// </summary>
        public string Version { get; set; } = "1.0-Platinum";

        // ── NARIT TLE API ─────────────────────────────────────────────────────
        public string NaritTLE_ApiToken { get; set; } = "";
    }

    /// <summary>
    /// Azimuth Axis Configuration for Platinum Drive
    /// </summary>
    public class AzimuthAxisConfig
    {
        public double Position_Offset_Deg { get; set; } = 0.0;
        public double CCW_Limit_Deg { get; set; } = -300.0;
        public double CW_Limit_Deg { get; set; } = 300.0;
        public long Encoder_Resolution { get; set; } = 4294967296; // 32-bit encoder (2^32 counts per revolution)
        public double Max_Control_Velocity_Deg_Sec { get; set; } = 100.0;
        public double Max_Velocity_Deg_Sec { get; set; } = 500.0;
        public double Max_Acceleration_Deg_Sec2 { get; set; } = 200.0;
        public double Acceleration_Deg_Sec2 { get; set; } = 100.0; // ACC - Normal acceleration
        public double Deceleration_Deg_Sec2 { get; set; } = 100.0; // DCC - Normal deceleration
        public double Stop_Deceleration_Deg_Sec2 { get; set; } = 200.0; // Stop DCC - Emergency stop deceleration
        public double Smooth_Factor_Ms { get; set; } = 10.0; // Smooth - Smoothing time in milliseconds
        public double Limit_Warning_Distance_Deg { get; set; } = 0.5; // Distance from limit to trigger warning
        public double Tracking_Sensitivity_Deg { get; set; } = 0.1; // Max position error for isTracking = true (degrees)
        public double Current_Limit_Amp { get; set; } = 10.0;
        public double Time_Duration_Ms { get; set; } = 1000.0;
    }

    /// <summary>
    /// Altitude Axis Configuration for Platinum Drive
    /// Similar to Azimuth but without sensor limits
    /// </summary>
    public class AltitudeAxisConfig
    {
        public double Position_Offset_Deg { get; set; } = 0.0;
        public double Down_Limit_Deg { get; set; } = -90.0;
        public double Up_Limit_Deg { get; set; } = 90.0;
        public long Encoder_Resolution { get; set; } = 4294967296; // Changed from int to long for 32-bit encoder
        public double Max_Velocity_Deg_Sec { get; set; } = 500.0;
        public double Acceleration_Deg_Sec2 { get; set; } = 100.0; // ACC - Normal acceleration
        public double Deceleration_Deg_Sec2 { get; set; } = 100.0; // DCC - Normal deceleration
        public double Stop_Deceleration_Deg_Sec2 { get; set; } = 200.0; // Stop DCC - Emergency stop deceleration
        public double Smooth_Factor_Ms { get; set; } = 10.0; // Smooth - Smoothing time in milliseconds
        public double Limit_Warning_Distance_Deg { get; set; } = 0.5; // Distance from limit to trigger warning
        public double Tracking_Sensitivity_Deg { get; set; } = 0.1; // Max position error for isTracking = true (degrees)
    }

    /// <summary>
    /// M3 Axis Configuration (Axis 4 / FORK2 — filter wheel / port selector)
    /// </summary>
    public class M3AxisConfig
    {
        /// <summary>PlatinumDrive axis number</summary>
        public int  Axis_Number              { get; set; } = 4;       // AX4 = FORK2
        /// <summary>Encoder resolution in counts/rev (18-bit = 2^18)</summary>
        public long Encoder_Resolution       { get; set; } = 262144;
        public double Position_Offset_Deg    { get; set; } = 0.0;
        public double Max_Velocity_Deg_Sec   { get; set; } = 10.0;
        public double Default_Velocity_Deg_Sec { get; set; } = 1.0;
    }

    /// <summary>
    /// Field-Derotator Axis Configuration (Axis 3 / FORK1)
    /// </summary>
    public class FieldDerotatorAxisConfig
    {
        /// <summary>PlatinumDrive axis number</summary>
        public int  Axis_Number              { get; set; } = 3;           // AX3 = FORK1
        /// <summary>Encoder resolution in counts/rev (32-bit = 2^32)</summary>
        public long Encoder_Resolution       { get; set; } = 4294967296L;
        public double Position_Offset_Deg    { get; set; } = 0.0;
        public double Max_Velocity_Deg_Sec   { get; set; } = 5.0;
        public double Default_Velocity_Deg_Sec { get; set; } = 1.0;
    }

    /// <summary>
    /// Tuning Parameters for Platinum Drive
    /// </summary>
    public class TuningParameters
    {
        public double Velocity_Gain { get; set; } = 3.73356e-05;
        public double Velocity_Integral { get; set; } = 0.7619;
        public double Position_Gain { get; set; } = 486.2025;
        public double Acc_Feed_Forward { get; set; } = 0.0;
        public double Vel_Feed_Forward { get; set; } = 1.0;
    }

    /// <summary>
    /// Tuning Steps
    /// </summary>
    public class TuningSteps
    {
        public double Velocity_Gain_Step { get; set; } = 1.1e-06;
        public double Velocity_Integral_Step { get; set; } = 0.001;
        public double Position_Gain_Step { get; set; } = 1.0;
        public double Acc_Feed_Forward_Step { get; set; } = 1e-06;
        public double Vel_Feed_Forward_Step { get; set; } = 0.1;
    }

    /// <summary>
    /// PID Controller Parameters for Position Control
    /// [UPDATED] Deadband is the single source of truth for setpoint tolerance
    /// Position_Window_Deg has been removed - all setpoint detection uses Deadband
    /// </summary>
    public class PDControllerParameters
    {
        public double Kp { get; set; } = 1.0;
        public double Ki { get; set; } = 0.0;
        public double Kd { get; set; } = 0.1;
        public double Deadband { get; set; } = 0.001;  // Setpoint tolerance - HIGH PRECISION (0.001 degrees)
    }

    /// <summary>
    /// Tuning Offsets for Fine Tuning
    /// </summary>
    public class TuningOffsets
    {
        // PD Controller Offsets
        public double Kp_Offset { get; set; } = 0.1;
        public double Ki_Offset { get; set; } = 0.01;
        public double Kd_Offset { get; set; } = 0.01;
        public double Max_Velocity_Offset { get; set; } = 1.0;
        public double Deadband_Offset { get; set; } = 0.01;

        // Elmo Internal Loop Offsets
        public double Velocity_Gain_Offset { get; set; } = 1E-06;
        public double Velocity_Integral_Offset { get; set; } = 0.01;
        public double Position_Gain_Offset { get; set; } = 1.0;
        public double Acc_Feed_Forward_Offset { get; set; } = 0.01;
        public double Vel_Feed_Forward_Offset { get; set; } = 0.01;

        // Motion Parameter Offsets
        public double Acceleration_Offset { get; set; } = 10.0;
        public double Deceleration_Offset { get; set; } = 10.0;
        public double Stop_Deceleration_Offset { get; set; } = 10.0;
        public double Smooth_Offset { get; set; } = 1.0;
    }

    /// <summary>
    /// Observer Location Settings for Satellite Tracking
    /// Chiang Mai, Thailand (default)
    /// This is a wrapper class for ObserverLocation (from SatelliteData.cs)
    /// to allow JSON serialization in AppSettings
    /// </summary>
    public class ObserverLocationSettings
    {
        public string LocationName { get; set; } = "Chiang Mai, Thailand";
        public double Latitude_Deg { get; set; } = 18.852638;   // Chiang Mai latitude
        public double Longitude_Deg { get; set; } = 98.958352;  // Chiang Mai longitude
        public double Altitude_Meters { get; set; } = 350.0;    // Altitude (meters above sea level)
        public double UtcOffset_Hours { get; set; } = 7.0;      // UTC+7 for Thailand (Indochina Time)
    }
    
    /// <summary>
    /// System Settings for NTP Time Synchronization and other global settings
    /// </summary>
    public class SystemSettings
    {
        public string NtpServerAddress { get; set; } = "pool.ntp.org";
        public int NtpSyncIntervalMinutes { get; set; } = 60;  // Sync every 60 minutes
        public bool AutoSyncEnabled { get; set; } = false;     // Auto-sync on startup
        public bool EnableNtpSync { get; set; } = true;        // Enable NTP synchronization
    }

    /// <summary>
    /// Mount Calibration Settings
    /// </summary>
    public class MountCalibrationSettings
    {
        /// <summary>
        /// Pointing model backend used during live calibration updates.
        /// LegacyPxpReload keeps the existing append-.pxp and reload flow.
        /// ExperimentalCoreDirect uses PointXPCore.AddCalPoint + Calibrate in memory.
        /// </summary>
        public string PointingModelBackendMode { get; set; } = "LegacyPxpReload";

        /// <summary>
        /// Tracking duration in seconds before capturing image
        /// Time to wait for mount to stabilize at each calibration point
        /// Default: 8 seconds
        /// </summary>
        public int TrackingDuration_Seconds { get; set; } = 8;
        
        /// <summary>
        /// Arrival threshold in degrees
        /// Maximum distance from target to consider mount "arrived"
        /// Default: 0.5 degrees
        /// </summary>
        public double ArrivalThreshold_Deg { get; set; } = 0.5;
        
        /// <summary>
        /// RMS stability threshold in arcseconds
        /// Maximum RMS error to consider tracking "stable"
        /// Default: 3.0 arcseconds
        /// </summary>
        public double RmsStabilityThreshold_Arcsec { get; set; } = 3.0;
        
        /// <summary>
        /// RMS stability duration in milliseconds
        /// How long RMS must stay below threshold to be considered stable
        /// Default: 1000 ms (1 second)
        /// </summary>
        public int RmsStabilityDuration_Ms { get; set; } = 1000;
        
        /// <summary>
        /// Plate solver search radius in degrees
        /// How far from estimated position to search for solution
        /// Default: 10.0 degrees
        /// </summary>
        public double PlateSolverSearchRadius_Deg { get; set; } = 10.0;

        /// <summary>
        /// Minimum number of tracking samples required before stability evaluation begins.
        /// Lower values reduce waiting time per point but may increase noise sensitivity.
        /// </summary>
        public int MinimumTrackingSamples { get; set; } = 6;

        /// <summary>
        /// Extra settling time after RMS stability has already been achieved.
        /// Helps absorb residual oscillation without spending as much time as the previous fixed delay.
        /// </summary>
        public int ExtraSettleTime_Ms { get; set; } = 750;

        /// <summary>
        /// Additional delay after image readout completes before the mount is allowed to proceed.
        /// </summary>
        public int PostExposureBuffer_Ms { get; set; } = 500;

        /// <summary>
        /// Maximum time to wait for RMS to drop below threshold before capturing anyway.
        /// </summary>
        public double RmsTimeout_Seconds { get; set; } = 20.0;

        /// <summary>
        /// Full path to the currently loaded Pointing Model file (.pxp or .pmd)
        /// Saved when user manually selects a file via "Load Pointing Model File" button
        /// </summary>
        public string PointingModelFilePath { get; set; } = string.Empty;
    }
    
    /// <summary>
    /// Sidereal Tracking PD Controller Gains
    /// For tracking fixed celestial objects (stars, galaxies) with Earth rotation compensation
    /// PDF = PD (Proportional-Derivative) + VFF (Velocity Feed Forward)
    /// Separate gains for Azimuth and Altitude axes
    /// </summary>
    public class SiderealTrackingPDGains
    {
        // ===== AZIMUTH AXIS GAINS =====
        
        /// <summary>
        /// Azimuth Proportional Gain (Kp)
        /// Converts azimuth position error to velocity correction
        /// Typical range: 0.5 - 2.0 (deg/s per deg error)
        /// </summary>
        public double Azimuth_Kp { get; set; } = 1.5;
        
        /// <summary>
        /// Azimuth Derivative Gain (Kd)
        /// Provides damping for azimuth axis to reduce oscillation
        /// Typical range: 0.1 - 0.5
        /// </summary>
        public double Azimuth_Kd { get; set; } = 0.3;
        
        /// <summary>
        /// Azimuth Integral Gain (Ki)
        /// Eliminates steady-state error by accumulating position error over time
        /// Typical range: 0.01 - 0.5 (keep low to avoid windup)
        /// </summary>
        public double Azimuth_Ki { get; set; } = 0.0;
        
        /// <summary>
        /// Azimuth Velocity Feed Forward Gain (VFF)
        /// Scales Earth rotation compensation velocity for azimuth
        /// 1.0 = Full feedforward (100% of Earth rotation rate)
        /// Typical range: 0.0 - 1.0
        /// </summary>
        public double Azimuth_VffGain { get; set; } = 0.2;
        
        // ===== ALTITUDE AXIS GAINS =====
        
        /// <summary>
        /// Altitude Proportional Gain (Kp)
        /// Converts altitude position error to velocity correction
        /// Typical range: 0.5 - 2.0 (deg/s per deg error)
        /// </summary>
        public double Altitude_Kp { get; set; } = 1.5;
        
        /// <summary>
        /// Altitude Derivative Gain (Kd)
        /// Provides damping for altitude axis to reduce oscillation
        /// Typical range: 0.1 - 0.5
        /// </summary>
        public double Altitude_Kd { get; set; } = 0.3;
        
        /// <summary>
        /// Altitude Integral Gain (Ki)
        /// Eliminates steady-state error by accumulating position error over time
        /// Typical range: 0.01 - 0.5 (keep low to avoid windup)
        /// </summary>
        public double Altitude_Ki { get; set; } = 0.0;
        
        /// <summary>
        /// Altitude Velocity Feed Forward Gain (VFF)
        /// Scales Earth rotation compensation velocity for altitude
        /// 1.0 = Full feedforward (100% of Earth rotation rate)
        /// Typical range: 0.0 - 1.0
        /// </summary>
        public double Altitude_VffGain { get; set; } = 0.2;
        
        // ===== GLOBAL SETTINGS =====
        
        /// <summary>
        /// Enable Earth rotation feedforward compensation
        /// When disabled, only PD control is used (no feedforward)
        /// </summary>
        public bool EnableEarthRotationCompensation { get; set; } = true;
        
        /// <summary>
        /// Prediction lead time in milliseconds for target position.
        /// Controller aims at the target's FUTURE position (now + PredictionTimeMs)
        /// to compensate for system latency (control loop + motor response + mechanical lag).
        /// RMS error measurement still uses the CURRENT position for accurate reporting.
        /// 0 = disabled (use current position). Typical range: 15 - 100 ms.
        /// </summary>
        public double PredictionTime_Ms { get; set; } = 0.0;
        
        // ===== KALMAN FILTER SETTINGS =====
        
        /// <summary>
        /// Enable Kalman filter for encoder noise reduction
        /// When disabled, raw encoder values are fed directly to the controller
        /// </summary>
        public bool Kalman_Enabled { get; set; } = true;
        
        /// <summary>
        /// Process noise for position state (how much the model trusts constant-velocity prediction)
        /// Lower = smoother but slower response. Higher = faster response but noisier.
        /// Typical range: 0.0000001 - 0.001
        /// </summary>
        public double Kalman_ProcessNoise_Position { get; set; } = 0.0000002;
        
        /// <summary>
        /// Process noise for velocity state
        /// Typical range: 0.000001 - 0.01
        /// </summary>
        public double Kalman_ProcessNoise_Velocity { get; set; } = 0.000002;
        
        /// <summary>
        /// Measurement noise (how much the model trusts the encoder reading)
        /// Lower = trust encoder more (less filtering). Higher = trust encoder less (more filtering).
        /// Typical range: 0.01 - 10.0
        /// </summary>
        public double Kalman_MeasurementNoise { get; set; } = 1.0;
    }
    
    /// <summary>
    /// Satellite Tracking PDF Controller Gains
    /// For closed-loop position tracking with adaptive feedforward compensation
    /// PDF = PD (Proportional-Derivative) + VFF (Velocity Feed Forward)
    /// Separate gains for Azimuth and Altitude axes
    /// </summary>
    public class SatelliteTrackingPDGains
    {
        // ===== AZIMUTH AXIS GAINS =====
        
        /// <summary>
        /// Azimuth Proportional Gain (Kp) - Converts position error to velocity correction
        /// Higher value = faster response, but may cause overshoot/oscillation
        /// Typical range: 0.1 - 1.0 (deg/s per deg error)
        /// </summary>
        public double Azimuth_Kp { get; set; } = 0.3;
        
        /// <summary>
        /// Azimuth Integral Gain (Ki) - Eliminates steady-state error at high velocities
        /// Accumulates position error over time to correct persistent tracking offset
        /// Too high = overshoot/oscillation, too low = slow correction
        /// Typical range: 0.0 - 0.5
        /// </summary>
        public double Azimuth_Ki { get; set; } = 0.0;
        
        /// <summary>
        /// Azimuth Derivative Gain (Kd) - Provides damping to reduce oscillation
        /// Higher value = more damping, smoother motion
        /// Typical range: 0.01 - 0.2
        /// </summary>
        public double Azimuth_Kd { get; set; } = 0.08;
        
        /// <summary>
        /// Azimuth Velocity Feed Forward Gain (VFF) - Uses satellite velocity for prediction
        /// Reduces tracking lag by predicting required velocity before error occurs
        /// 1.0 = perfect feedforward (100% of satellite velocity)
        /// 0.8-0.95 = conservative (safer, still effective)
        /// Typical range: 0.8 - 1.0
        /// </summary>
        public double Azimuth_VffGain { get; set; } = 0.90;
        
        /// <summary>
        /// Azimuth Base Prediction Time (ms) - Used at low altitudes (0-30�)
        /// Lower angular velocity requires less prediction
        /// Typical range: 20 - 100 ms
        /// </summary>
        public double Azimuth_BasePredictionTimeMs { get; set; } = 50.0;
        
        /// <summary>
        /// Azimuth Max Prediction Time (ms) - Used at high altitudes (60-90�)
        /// Higher angular velocity (near zenith) requires more prediction
        /// Typical range: 200 - 1000 ms
        /// Adaptive formula: Pred = Base + (Max - Base) � sin�(Altitude)
        /// </summary>
        public double Azimuth_MaxPredictionTimeMs { get; set; } = 500.0;
        
        // ===== ALTITUDE AXIS GAINS =====
        
        /// <summary>
        /// Altitude Proportional Gain (Kp) - Converts position error to velocity correction
        /// Higher value = faster response, but may cause overshoot/oscillation
        /// Typical range: 0.1 - 1.0 (deg/s per deg error)
        /// </summary>
        public double Altitude_Kp { get; set; } = 0.3;
        
        /// <summary>
        /// Altitude Integral Gain (Ki) - Eliminates steady-state error at high velocities
        /// Accumulates position error over time to correct persistent tracking offset
        /// Too high = overshoot/oscillation, too low = slow correction
        /// Typical range: 0.0 - 0.5
        /// </summary>
        public double Altitude_Ki { get; set; } = 0.0;
        
        /// <summary>
        /// Altitude Derivative Gain (Kd) - Provides damping to reduce oscillation
        /// Higher value = more damping, smoother motion
        /// Typical range: 0.01 - 0.2
        /// </summary>
        public double Altitude_Kd { get; set; } = 0.08;
        
        /// <summary>
        /// Altitude Velocity Feed Forward Gain (VFF) - Uses satellite velocity for prediction
        /// Reduces tracking lag by predicting required velocity before error occurs
        /// 1.0 = perfect feedforward (100% of satellite velocity)
        /// 0.8-0.95 = conservative (safer, still effective)
        /// Typical range: 0.8 - 1.0
        /// </summary>
        public double Altitude_VffGain { get; set; } = 0.90;
        
        /// <summary>
        /// Altitude Base Prediction Time (ms) - Used at low altitudes (0-30�)
        /// Lower angular velocity requires less prediction
        /// Typical range: 20 - 100 ms
        /// </summary>
        public double Altitude_BasePredictionTimeMs { get; set; } = 50.0;
        
        /// <summary>
        /// Altitude Max Prediction Time (ms) - Used at high altitudes (60-90�)
        /// Higher angular velocity (near zenith) requires more prediction
        /// Typical range: 200 - 1000 ms
        /// Adaptive formula: Pred = Base + (Max - Base) � sin�(Altitude)
        /// </summary>
        public double Altitude_MaxPredictionTimeMs { get; set; } = 500.0;
        
        // ===== INTEGRAL ANTI-WINDUP =====
        
        /// <summary>
        /// Maximum integral accumulator value (degrees) - Anti-windup clamp
        /// Prevents integral term from growing too large during slewing or large errors
        /// Typical range: 1.0 - 10.0
        /// </summary>
        public double Max_Integral_Deg { get; set; } = 5.0;
        
        // ===== VELOCITY OUTPUT FILTER =====
        
        /// <summary>
        /// Velocity Output Filter Alpha (0.0 - 1.0)
        /// Controls smoothing of velocity commands sent to motors
        /// Higher = faster response but more noise, Lower = smoother but more lag
        /// Affects tracking RMS at high velocities: lag ? (TimerInterval / Alpha)
        /// Typical range: 0.20 - 0.80
        /// </summary>
        public double Velocity_Filter_Alpha { get; set; } = 0.30;
        
        // ===== LEGACY PROPERTIES (for backward compatibility) =====
        
        /// <summary>
        /// Legacy Kp property - maps to Azimuth_Kp
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double Kp 
        { 
            get => Azimuth_Kp;
            set => Azimuth_Kp = value;
        }
        
        /// <summary>
        /// Legacy Kd property - maps to Azimuth_Kd
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double Kd 
        { 
            get => Azimuth_Kd;
            set => Azimuth_Kd = value;
        }
        
        /// <summary>
        /// Legacy VffGain property - maps to Azimuth_VffGain
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double VffGain 
        { 
            get => Azimuth_VffGain;
            set => Azimuth_VffGain = value;
        }
        
        /// <summary>
        /// Legacy BasePredictionTimeMs property - maps to Azimuth_BasePredictionTimeMs
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double BasePredictionTimeMs 
        { 
            get => Azimuth_BasePredictionTimeMs;
            set => Azimuth_BasePredictionTimeMs = value;
        }
        
        /// <summary>
        /// Legacy MaxPredictionTimeMs property - maps to Azimuth_MaxPredictionTimeMs
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double MaxPredictionTimeMs 
        { 
            get => Azimuth_MaxPredictionTimeMs;
            set => Azimuth_MaxPredictionTimeMs = value;
        }
        
        /// <summary>
        /// Legacy property for backward compatibility
        /// Now uses Azimuth_BasePredictionTimeMs as default
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double PredictionTimeMs 
        { 
            get => Azimuth_BasePredictionTimeMs;
            set => Azimuth_BasePredictionTimeMs = value;
        }
    }

    /// <summary>
    /// ZWO Camera Settings
    /// Default values configured for ZWO ASI 294MC Pro
    /// Sensor: IMX492, Resolution: 8288�5644, Pixel Size: 2.3?m
    /// </summary>
    public class ZWOCameraSettings
    {
        /// <summary>
        /// Exposure Time in milliseconds
        /// For plate solving, 2-5 seconds is typically enough
        /// </summary>
        public int ExposureTime_ms { get; set; } = 3000;

        /// <summary>
        /// Gain setting (0-570 for ASI294MC)
        /// Higher gain = more sensitivity but more noise
        /// Recommended for plate solving: 100-200
        /// </summary>
        public int Gain { get; set; } = 150;

        /// <summary>
        /// Offset setting (0-100)
        /// Adjusts black level to prevent clipping
        /// </summary>
        public int Offset { get; set; } = 10;

        /// <summary>
        /// Image width (0 = full frame = 8288 for ASI294MC)
        /// </summary>
        public int Width { get; set; } = 0;

        /// <summary>
        /// Image height (0 = full frame = 5644 for ASI294MC)
        /// </summary>
        public int Height { get; set; } = 0;


        /// <summary>
        /// Binning mode (1, 2, 3, or 4)
        /// Bin 2 recommended for faster readout and larger FOV per pixel
        /// ASI294MC: Bin1=8288�5644, Bin2=4144�2822, Bin3=2762�1880, Bin4=2072�1410
        /// </summary>
        public int Binning { get; set; } = 2;

        /// <summary>
        /// Image Type (0=RAW8, 1=RAW16, 2=RGB24)
        /// RAW16 recommended for maximum dynamic range
        /// </summary>
        public int ImageType { get; set; } = 1; // RAW16 default

        /// <summary>
        /// Focal Length in millimeters (for PlateScale calculation)
        /// CRITICAL: This must be set correctly for accurate star field overlay
        /// Example: 200mm lens, 500mm telescope, 1000mm telescope, etc.
        /// </summary>
        public double FocalLength_mm { get; set; } = 1000.0; // Default 1000mm

        /// <summary>
        /// Plate Scale in arcseconds per pixel (calculated from PixelSize, FocalLength, Binning)
        /// This is auto-calculated when Apply is clicked in Camera Settings Window
        /// Formula: PlateScale = (PixelSize_um * Binning / FocalLength_mm) * 206.265
        /// 
        /// For ASI294MC (2.3�m) at 1000mm focal length:
        /// - Bin1: (2.3 * 1 / 1000) * 206.265 = 0.47 arcsec/pixel
        /// - Bin2: (2.3 * 2 / 1000) * 206.265 = 0.95 arcsec/pixel
        /// </summary>
        public double PlateScale_ArcsecPerPixel { get; set; } = 0.0;
        
        /// <summary>
        /// Camera Pixel Size in micrometers (�m)
        /// ASI294MC Pro: 2.3�m
        /// This is used for PlateScale calculation if not auto-detected from camera
        /// </summary>
        public double PixelSize_um { get; set; } = 2.3;  // ASI294MC Pro
        
        /// <summary>
        /// Histogram Stretch Profile Settings
        /// For consistent image display across CameraSettings and LiveCalibrationImage
        /// </summary>
        public HistogramStretchProfile HistogramStretch { get; set; } = new HistogramStretchProfile();

        /// <summary>
        /// Whether camera cooling should be enabled automatically after connect.
        /// Applies to any camera provider that supports cooling.
        /// </summary>
        public bool CoolerEnabled { get; set; } = false;

        /// <summary>
        /// Target cooler temperature in degrees Celsius.
        /// </summary>
        public double CoolerTargetTemp_C { get; set; } = -10.0;

        /// <summary>
        /// Last camera type selected/connected by the user.
        /// Used so Mount Calibration can auto-restore the preferred camera provider.
        /// </summary>
        public string PreferredCameraType { get; set; } = "ZWO_ASI";
    }
    
    /// <summary>
    /// Histogram Stretch Profile Presets (like MaximDL)
    /// </summary>
    public enum StretchProfileType
    {
        /// <summary>Auto-detect from image histogram</summary>
        Auto = 0,
        /// <summary>Low stretch - good for bright objects</summary>
        Low = 1,
        /// <summary>Medium stretch - balanced for most objects</summary>
        Medium = 2,
        /// <summary>High stretch - good for faint objects</summary>
        High = 3,
        /// <summary>Manual - user defined min/max values</summary>
        Manual = 4,
        /// <summary>Bright Star - optimized for bright star fields</summary>
        BrightStar = 5,
        /// <summary>Moon - optimized for lunar imaging</summary>
        Moon = 6,
        /// <summary>Planet - optimized for planetary imaging</summary>
        Planet = 7,
        /// <summary>Deep Sky - optimized for faint nebulae/galaxies</summary>
        DeepSky = 8
    }
    
    /// <summary>
    /// Histogram Stretch Profile for consistent image display
    /// Used in CameraSettingsWindow and LiveCalibrationImageControl
    /// Supports preset profiles like MaximDL
    /// </summary>
    public class HistogramStretchProfile
    {
        /// <summary>
        /// Currently selected profile type
        /// </summary>
        public StretchProfileType ProfileType { get; set; } = StretchProfileType.Auto;
        
        /// <summary>
        /// Minimum brightness level (black point)
        /// Range: 0 - 65535 for 16-bit images
        /// Used when ProfileType = Manual
        /// </summary>
        public int MinValue { get; set; } = 0;
        
        /// <summary>
        /// Maximum brightness level (white point)
        /// Range: 0 - 65535 for 16-bit images
        /// Used when ProfileType = Manual
        /// </summary>
        public int MaxValue { get; set; } = 65535;
        
        /// <summary>
        /// Enable auto-stretch on capture
        /// If true, auto-calculates min/max from image histogram
        /// </summary>
        public bool AutoStretchOnCapture { get; set; } = false;
        
        /// <summary>
        /// Percentile for black point (0-100)
        /// Lower = more clipping of dark pixels
        /// </summary>
        public double BlackPercentile { get; set; } = 1.0;
        
        /// <summary>
        /// Percentile for white point (0-100)
        /// Higher = less clipping of bright pixels
        /// </summary>
        public double WhitePercentile { get; set; } = 99.5;
        
        /// <summary>
        /// Get preset percentile values for each profile type
        /// </summary>
        public static (double Black, double White) GetPresetPercentiles(StretchProfileType profileType)
        {
            return profileType switch
            {
                StretchProfileType.Auto => (0.5, 99.95),       // Similar to Maxim automatic default
                StretchProfileType.Low => (1.0, 99.98),        // Low contrast, dark background
                StretchProfileType.Medium => (3.0, 99.95),     // Balanced everyday stretch
                StretchProfileType.High => (8.0, 99.90),       // Stronger dark clipping for faint detail
                StretchProfileType.BrightStar => (1.5, 99.97), // Preserve star cores with darker sky
                StretchProfileType.Moon => (0.2, 99.70),       // High contrast with some highlight saturation
                StretchProfileType.Planet => (0.05, 99.99),    // Highest contrast while avoiding saturation
                StretchProfileType.DeepSky => (12.0, 99.85),   // Aggressive background suppression
                StretchProfileType.Manual => (3.0, 99.95),     // Neutral default for manual mode
                _ => (3.0, 99.95)
            };
        }

        /// <summary>
        /// Tone-curve strength used after percentile clipping.
        /// Values &gt; 1 brighten shadows more strongly when using asinh stretch.
        /// Values near 1 behave close to linear.
        /// </summary>
        public static double GetPresetToneStrength(StretchProfileType profileType)
        {
            return profileType switch
            {
                StretchProfileType.Auto => 8.0,
                StretchProfileType.Low => 4.0,
                StretchProfileType.Medium => 7.0,
                StretchProfileType.High => 12.0,
                StretchProfileType.BrightStar => 5.0,
                StretchProfileType.Moon => 1.4,
                StretchProfileType.Planet => 1.2,
                StretchProfileType.DeepSky => 18.0,
                StretchProfileType.Manual => 1.0,
                _ => 7.0
            };
        }
        
        /// <summary>
        /// Get display name for profile type
        /// </summary>
        public static string GetProfileDisplayName(StretchProfileType profileType)
        {
            return profileType switch
            {
                StretchProfileType.Auto => "? Auto",
                StretchProfileType.Low => "?? Low (Bright)",
                StretchProfileType.Medium => "?? Medium",
                StretchProfileType.High => "?? High (Faint)",
                StretchProfileType.Manual => "?? Manual",
                StretchProfileType.BrightStar => "? Bright Stars",
                StretchProfileType.Moon => "?? Moon",
                StretchProfileType.Planet => "?? Planet",
                StretchProfileType.DeepSky => "?? Deep Sky",
                _ => profileType.ToString()
            };
        }
    }
    
    /// <summary>
    /// Known Drive profiles mapping Serial Number to Encoder Resolution
    /// Used to auto-configure Encoder_Resolution when connecting to a Drive
    /// </summary>
    public class DriveProfileEntry
    {
        public string SerialNumber { get; set; } = string.Empty;
        public long Encoder_Resolution { get; set; } = 4294967296;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lookup service for Drive profiles
    /// Maps known Drive Serial Numbers to their Encoder Resolution
    /// </summary>
    public static class DriveProfileLookup
    {
        /// <summary>
        /// Known Drive profiles (SN -> Encoder Resolution)
        /// Add new drives here when hardware changes
        /// </summary>
        private static readonly DriveProfileEntry[] KnownDrives =
        [
            new() { SerialNumber = "25262718", Encoder_Resolution = 4294967296, Description = "32-bit encoder (2^32)" },
            new() { SerialNumber = "25382282", Encoder_Resolution = 134217728,  Description = "27-bit encoder (2^27)" },
        ];

        /// <summary>
        /// Get Encoder Resolution for a given Serial Number
        /// Returns null if SN is not in the known profiles
        /// </summary>
        public static long? GetEncoderResolution(string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber)) return null;

            foreach (var drive in KnownDrives)
            {
                if (drive.SerialNumber == serialNumber)
                    return drive.Encoder_Resolution;
            }
            return null;
        }

        /// <summary>
        /// Get description for a given Serial Number
        /// </summary>
        public static string GetDescription(string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber)) return "Unknown";

            foreach (var drive in KnownDrives)
            {
                if (drive.SerialNumber == serialNumber)
                    return drive.Description;
            }
            return "Unknown drive";
        }
    }
}











