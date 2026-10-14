using System;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Info
{
    /// <summary>
    /// Thread-Safe Data Hub for Azimuth Drive
    /// Central Hub for 4-Loop Architecture:
    ///   - Polling Loop (200Hz) writes Raw data + adds to Buffer
    ///   - PID Loop (50Hz) writes Command data
    ///   - Write Loop (100Hz) reads Command data
    ///   - UI Loop (100Hz) reads all data from Buffer
    /// For Elmo Platinum Drive Series
    /// [NEW] Auto-updates MountInfo when Raw_Position changes
    /// </summary>
    public class AzimuthInfo
    {
        private readonly object _lock = new object();
        
        // [NEW] References for auto-update
        private MountInfo _mountInfo;
        private double _encoderResolution;
        private double _positionOffsetDeg;

        #region Data Buffer (New!)

        /// <summary>
        /// High-speed data buffer for chart plotting
        /// Stores all data points from Polling Loop (200Hz)
        /// UI consumes at 100Hz without data loss
        /// </summary>
        public DataBuffer DataBuffer { get; set; }

        #endregion

        #region Position

        private double _rawPosition;
        private DateTime _rawPositionTimestamp;

        /// <summary>
        /// Raw Position from Drive (counts)
        /// [NEW] Auto-converts to degrees and updates MountInfo when set
        /// </summary>
        public double Raw_Position
        {
            get { lock (_lock) return _rawPosition; }
            set
            {
                lock (_lock)
                {
                    _rawPosition = value;
                    _rawPositionTimestamp = NtpTimeService.Instance.GetCurrentTime();
                    
                    // [NEW] Auto-convert to degrees and update MountInfo
                    if (_mountInfo != null && _encoderResolution > 0)
                    {
                        double positionDeg = (_rawPosition / _encoderResolution * 360.0) + _positionOffsetDeg;
                        _mountInfo.Act_Azimuth_Pos_Raw = positionDeg;
                    }
                }
            }
        }

        public DateTime Raw_Position_Timestamp
        {
            get { lock (_lock) return _rawPositionTimestamp; }
        }
        
        /// <summary>
        /// Set MountInfo reference for auto-update
        /// Call this during initialization
        /// </summary>
        public void SetMountInfo(MountInfo mountInfo, double encoderResolution, double positionOffsetDeg = 0)
        {
            lock (_lock)
            {
                _mountInfo = mountInfo;
                _encoderResolution = encoderResolution;
                _positionOffsetDeg = positionOffsetDeg;
            }
        }

        #endregion

        #region Filtered Position (Kalman Filter Output)

        private double _filteredPosition;
        private DateTime _filteredPositionTimestamp;

        /// <summary>
        /// Filtered Position from Kalman Filter (counts)
        /// Smoothed position estimate with reduced encoder noise
        /// </summary>
        public double Filtered_Position
        {
            get { lock (_lock) return _filteredPosition; }
            set
            {
                lock (_lock)
                {
                    _filteredPosition = value;
                    _filteredPositionTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime Filtered_Position_Timestamp
        {
            get { lock (_lock) return _filteredPositionTimestamp; }
        }

        #endregion

        #region Velocity

        private double _rawVelocity;
        private DateTime _rawVelocityTimestamp;

        public double Raw_Velocity
        {
            get { lock (_lock) return _rawVelocity; }
            set
            {
                lock (_lock)
                {
                    _rawVelocity = value;
                    _rawVelocityTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime Raw_Velocity_Timestamp
        {
            get { lock (_lock) return _rawVelocityTimestamp; }
        }

        #endregion

        #region Filtered Velocity (Kalman Filter Output)

        private double _filteredVelocity;
        private DateTime _filteredVelocityTimestamp;

        /// <summary>
        /// Filtered Velocity from Kalman Filter (counts/sec)
        /// Smoothed velocity estimate derived from position tracking
        /// </summary>
        public double Filtered_Velocity
        {
            get { lock (_lock) return _filteredVelocity; }
            set
            {
                lock (_lock)
                {
                    _filteredVelocity = value;
                    _filteredVelocityTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime Filtered_Velocity_Timestamp
        {
            get { lock (_lock) return _filteredVelocityTimestamp; }
        }

        #endregion

        #region Command Velocity (PID Controller Output)

        private double _commandVelocityDegSec;
        private DateTime _commandVelocityTimestamp;

        /// <summary>
        /// Command Velocity from PID Controller (deg/sec)
        /// Written by: PID Loop
        /// Read by: Write Loop
        /// </summary>
        public double Command_Velocity_Deg_Sec
        {
            get { lock (_lock) return _commandVelocityDegSec; }
            set
            {
                lock (_lock)
                {
                    _commandVelocityDegSec = value;
                    _commandVelocityTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime Command_Velocity_Timestamp
        {
            get { lock (_lock) return _commandVelocityTimestamp; }
        }

        #endregion

        #region Command Enable/Disable (High Priority Command)

        private bool? _commandEnable;

        /// <summary>
        /// Command to Enable/Disable Drive
        /// Written by: UI (Enable/Disable button)
        /// Read by: Write Loop (high priority)
        /// Cleared by: Write Loop after executing MO command
        /// null = no command pending
        /// true = enable command pending
        /// false = disable command pending
        /// </summary>
        public bool? Command_Enable
        {
            get { lock (_lock) return _commandEnable; }
            set { lock (_lock) _commandEnable = value; }
        }

        #endregion

        #region Command Stop (High Priority Flag)

        private bool _commandStop;

        /// <summary>
        /// Emergency Stop Flag
        /// Written by: UI (Stop button)
        /// Read by: Write Loop (highest priority)
        /// Cleared by: Write Loop after executing ST command
        /// </summary>
        public bool Command_Stop
        {
            get { lock (_lock) return _commandStop; }
            set { lock (_lock) _commandStop = value; }
        }

        #endregion

        #region Current

        private double _currentIQ;
        private DateTime _currentIQTimestamp;

        public double Current_IQ
        {
            get { lock (_lock) return _currentIQ; }
            set
            {
                lock (_lock)
                {
                    _currentIQ = value;
                    _currentIQTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime Current_IQ_Timestamp
        {
            get { lock (_lock) return _currentIQTimestamp; }
        }

        #endregion

        #region Drive State

        private bool _isDriveEnabled;
        private DateTime _isDriveEnabledTimestamp;

        public bool Is_Drive_Enabled
        {
            get { lock (_lock) return _isDriveEnabled; }
            set
            {
                lock (_lock)
                {
                    _isDriveEnabled = value;
                    _isDriveEnabledTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime Is_Drive_Enabled_Timestamp
        {
            get { lock (_lock) return _isDriveEnabledTimestamp; }
        }

        #endregion

        #region Sensors

        private bool _ccwSensorActive;
        private DateTime _ccwSensorActiveTimestamp;

        public bool CCW_Sensor_Active
        {
            get { lock (_lock) return _ccwSensorActive; }
            set
            {
                lock (_lock)
                {
                    _ccwSensorActive = value;
                    _ccwSensorActiveTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime CCW_Sensor_Active_Timestamp
        {
            get { lock (_lock) return _ccwSensorActiveTimestamp; }
        }

        private bool _cwSensorActive;
        private DateTime _cwSensorActiveTimestamp;

        public bool CW_Sensor_Active
        {
            get { lock (_lock) return _cwSensorActive; }
            set
            {
                lock (_lock)
                {
                    _cwSensorActive = value;
                    _cwSensorActiveTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime CW_Sensor_Active_Timestamp
        {
            get { lock (_lock) return _cwSensorActiveTimestamp; }
        }

        #endregion

        #region Unwrap State (Satellite Tracking)

        private bool _isUnwrapping;

        /// <summary>
        /// Flag to indicate that Azimuth is currently unwrapping (crossing 360° boundary)
        /// Set by: Satellite Tracking PD Control
        /// Read by: CommandSenderLoop to bypass limit checks during unwrap
        /// </summary>
        public bool IsUnwrapping
        {
            get { lock (_lock) return _isUnwrapping; }
            set { lock (_lock) _isUnwrapping = value; }
        }

        #endregion

        #region Filter Parameters (Advanced Filters - Velocity Loop)

        // Filter #1 (Lead-Lag or Low-Pass)
        public int Filter1_Type { get; set; } = 0;
        public double Filter1_Frequency { get; set; } = 0;
        public double Filter1_Damping { get; set; } = 0;

        // Filter #2 (Notch)
        public int Filter2_Type { get; set; } = 0;
        public double Filter2_Frequency { get; set; } = 0;
        public double Filter2_QFactor { get; set; } = 0;
        public double Filter2_Magnitude { get; set; } = 0;

        // Filter #3 (Notch)
        public int Filter3_Type { get; set; } = 0;
        public double Filter3_Frequency { get; set; } = 0;
        public double Filter3_QFactor { get; set; } = 0;
        public double Filter3_Magnitude { get; set; } = 0;

        // Filter #4 (Notch)
        public int Filter4_Type { get; set; } = 0;
        public double Filter4_Frequency { get; set; } = 0;
        public double Filter4_QFactor { get; set; } = 0;
        public double Filter4_Magnitude { get; set; } = 0;

        // Feedback Filter #1 (Notch)
        public int FeedbackFilter1_Type { get; set; } = 0;
        public double FeedbackFilter1_Frequency { get; set; } = 0;
        public double FeedbackFilter1_QFactor { get; set; } = 0;
        public double FeedbackFilter1_Magnitude { get; set; } = 0;

        // Feedback Filter #2 (Notch)
        public int FeedbackFilter2_Type { get; set; } = 0;
        public double FeedbackFilter2_Frequency { get; set; } = 0;
        public double FeedbackFilter2_QFactor { get; set; } = 0;
        public double FeedbackFilter2_Magnitude { get; set; } = 0;

        private DateTime _filterParametersTimestamp;

        public DateTime FilterParametersTimestamp
        {
            get { lock (_lock) return _filterParametersTimestamp; }
        }

        public void UpdateFilterParametersTimestamp()
        {
            lock (_lock)
            {
                _filterParametersTimestamp = NtpTimeService.Instance.GetCurrentTime();
            }
        }


        #endregion

        #region Tracking Status (for Astrophotography & Satellite Tracking)

        private bool _isTracking;
        private DateTime _isTrackingTimestamp;
        private TrackingMode _trackingMode;
        private double _trackingRmsError_Arcsec;
        private DateTime _trackingRmsTimestamp;
        private int _trackingSampleCount;

        /// <summary>
        /// Indicates if tracking is within acceptable RMS error threshold
        /// true = Ready for imaging
        /// false = Slewing or tracking error too high
        /// </summary>
        public bool IsTracking
        {
            get { lock (_lock) return _isTracking; }
            set
            {
                lock (_lock)
                {
                    _isTracking = value;
                    _isTrackingTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime IsTrackingTimestamp
        {
            get { lock (_lock) return _isTrackingTimestamp; }
        }

        /// <summary>
        /// Current tracking mode (Sidereal, Satellite, etc.)
        /// </summary>
        public TrackingMode TrackingMode
        {
            get { lock (_lock) return _trackingMode; }
            set { lock (_lock) _trackingMode = value; }
        }

        /// <summary>
        /// Current RMS tracking error in arcseconds
        /// Lower is better (< 3" excellent for sidereal)
        /// </summary>
        public double TrackingRmsError_Arcsec
        {
            get { lock (_lock) return _trackingRmsError_Arcsec; }
            set
            {
                lock (_lock)
                {
                    _trackingRmsError_Arcsec = value;
                    _trackingRmsTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public DateTime TrackingRmsTimestamp
        {
            get { lock (_lock) return _trackingRmsTimestamp; }
        }

        /// <summary>
        /// Number of samples used for RMS calculation
        /// More samples = more reliable RMS
        /// </summary>
        public int TrackingSampleCount
        {
            get { lock (_lock) return _trackingSampleCount; }
            set { lock (_lock) _trackingSampleCount = value; }
        }

        #endregion

        #region Constructor


        public AzimuthInfo()
        {
            // Initialize data buffer with 2 seconds time window
            // At 200Hz, this will keep ~400 points
            DataBuffer = new DataBuffer(maxAgeSeconds: 2.0, maxCapacity: 500);
            Reset();
        }

        #endregion

        #region Helper Methods

        public bool IsPositionDataStale(double maxAgeSeconds = 1.0)
        {
            lock (_lock)
            {
                return (NtpTimeService.Instance.GetCurrentTime() - _rawPositionTimestamp).TotalSeconds > maxAgeSeconds;
            }
        }

        public bool IsVelocityDataStale(double maxAgeSeconds = 1.0)
        {
            lock (_lock)
            {
                return (NtpTimeService.Instance.GetCurrentTime() - _rawVelocityTimestamp).TotalSeconds > maxAgeSeconds;
            }
        }

        public double GetPositionDataAge()
        {
            lock (_lock)
            {
                return (NtpTimeService.Instance.GetCurrentTime() - _rawPositionTimestamp).TotalSeconds;
            }
        }

        public double GetVelocityDataAge()
        {
            lock (_lock)
            {
                return (NtpTimeService.Instance.GetCurrentTime() - _rawVelocityTimestamp).TotalSeconds;
            }
        }

        /// <summary>
        /// Update Tracking Status based on position error
        /// Sets IsTracking = true if error is within Tracking_Sensitivity_Deg threshold
        /// </summary>
        /// <param name="targetPositionDeg">Target position in degrees</param>
        /// <param name="currentPositionDeg">Current position in degrees</param>
        /// <param name="trackingSensitivityDeg">Sensitivity threshold in degrees (from config)</param>
        public void UpdateTrackingStatus(double targetPositionDeg, double currentPositionDeg, double trackingSensitivityDeg)
        {
            double positionError = Math.Abs(targetPositionDeg - currentPositionDeg);
            
            lock (_lock)
            {
                bool wasTracking = _isTracking;
                _isTracking = positionError <= trackingSensitivityDeg;
                
                // Log only when status changes
                if (wasTracking != _isTracking)
                {
                    System.Diagnostics.Debug.WriteLine($"[TRACKING STATUS] Azimuth tracking: {(_isTracking ? "ACQUIRED" : "LOST")} (Error: {positionError:F4}°, Threshold: {trackingSensitivityDeg:F2}°)");
                }
                
                if (_isTracking)
                {
                    _isTrackingTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _rawPosition = 0;
                _rawVelocity = 0;
                _commandVelocityDegSec = 0;
                _commandEnable = null;
                _commandStop = false;
                _currentIQ = 0;
                _isDriveEnabled = false;
                _ccwSensorActive = false;
                _cwSensorActive = false;

                Filter1_Type = 0;
                Filter1_Frequency = 0;
                Filter1_Damping = 0;
                Filter2_Type = 0;
                Filter2_Frequency = 0;
                Filter2_QFactor = 0;
                Filter2_Magnitude = 0;
                Filter3_Type = 0;
                Filter3_Frequency = 0;
                Filter3_QFactor = 0;
                Filter3_Magnitude = 0;
                Filter4_Type = 0;
                Filter4_Frequency = 0;
                Filter4_QFactor = 0;
                Filter4_Magnitude = 0;
                FeedbackFilter1_Type = 0;
                FeedbackFilter1_Frequency = 0;
                FeedbackFilter1_QFactor = 0;
                FeedbackFilter1_Magnitude = 0;
                FeedbackFilter2_Type = 0;
                FeedbackFilter2_Frequency = 0;
                FeedbackFilter2_QFactor = 0;
                FeedbackFilter2_Magnitude = 0;

                DateTime now = NtpTimeService.Instance.GetCurrentTime();
                _rawPositionTimestamp = now;
                _rawVelocityTimestamp = now;
                _commandVelocityTimestamp = now;
                _currentIQTimestamp = now;
                _isDriveEnabledTimestamp = now;
                _ccwSensorActiveTimestamp = now;
                _cwSensorActiveTimestamp = now;
                _filterParametersTimestamp = now;
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return $"AzimuthInfo (Platinum Drive):\n" +
                       $"  Position: {_rawPosition:F2} (Age: {GetPositionDataAge():F3}s)\n" +
                       $"  Velocity: {_rawVelocity:F2} (Age: {GetVelocityDataAge():F3}s)\n" +
                       $"  Command Velocity: {_commandVelocityDegSec:F2} deg/s\n" +
                       $"  Command Stop: {_commandStop}\n" +
                       $"  Current IQ: {_currentIQ:F2} A\n" +
                       $"  Drive Enabled: {_isDriveEnabled}\n" +
                       $"  CCW Sensor: {_ccwSensorActive}\n" +
                       $"  CW Sensor: {_cwSensorActive}";
            }
        }

        #endregion
    }
}
