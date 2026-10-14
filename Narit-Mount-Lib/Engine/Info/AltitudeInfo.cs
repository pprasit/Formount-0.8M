using System;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Info
{
    /// <summary>
    /// Thread-Safe Data Hub for Altitude Drive
    /// Central Hub for 4-Loop Architecture:
    ///   - Polling Loop (200Hz) writes Raw data + adds to Buffer
    ///   - PID Loop (50Hz) writes Command data
    ///   - Write Loop (100Hz) reads Command data
    ///   - UI Loop (100Hz) reads all data from Buffer
    /// For Elmo Platinum Drive Series
    /// Auto-updates MountInfo when Raw_Position changes
    /// </summary>
    public class AltitudeInfo
    {
        private readonly object _lock = new object();

        // References for auto-update to MountInfo
        private MountInfo _mountInfo;
        private double    _encoderResolution;
        private double    _positionOffsetDeg;

        #region Data Buffer

        /// <summary>
        /// High-speed data buffer for chart plotting.
        /// Stores all data points from Polling Loop (200Hz).
        /// UI consumes at 100Hz without data loss.
        /// </summary>
        public DataBuffer DataBuffer { get; set; }

        #endregion

        #region Position

        private double   _rawPosition;
        private DateTime _rawPositionTimestamp;

        /// <summary>
        /// Raw Position from Drive (counts).
        /// Auto-converts to degrees and updates MountInfo when set.
        /// </summary>
        public double Raw_Position
        {
            get { lock (_lock) return _rawPosition; }
            set
            {
                lock (_lock)
                {
                    _rawPosition          = value;
                    _rawPositionTimestamp = NtpTimeService.Instance.GetCurrentTime();

                    if (_mountInfo != null && _encoderResolution > 0)
                    {
                        double positionDeg = (_rawPosition / _encoderResolution * 360.0) + _positionOffsetDeg;
                        _mountInfo.Act_Altitude_Pos_Raw = positionDeg;
                    }
                }
            }
        }

        /// <summary>Timestamp of Raw Position</summary>
        public DateTime Raw_Position_Timestamp
        {
            get { lock (_lock) return _rawPositionTimestamp; }
        }

        /// <summary>
        /// Set MountInfo reference for auto-update. Call once during initialization.
        /// </summary>
        public void SetMountInfo(MountInfo mountInfo, double encoderResolution, double positionOffsetDeg = 0)
        {
            lock (_lock)
            {
                _mountInfo         = mountInfo;
                _encoderResolution = encoderResolution;
                _positionOffsetDeg = positionOffsetDeg;
            }
        }

        #endregion

        #region Filtered Position (Kalman Filter Output)

        private double   _filteredPosition;
        private DateTime _filteredPositionTimestamp;

        /// <summary>
        /// Filtered Position from Kalman Filter (counts).
        /// Smoothed position estimate with reduced encoder noise.
        /// </summary>
        public double Filtered_Position
        {
            get { lock (_lock) return _filteredPosition; }
            set
            {
                lock (_lock)
                {
                    _filteredPosition          = value;
                    _filteredPositionTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of Filtered Position</summary>
        public DateTime Filtered_Position_Timestamp
        {
            get { lock (_lock) return _filteredPositionTimestamp; }
        }

        #endregion

        #region Velocity

        private double   _rawVelocity;
        private DateTime _rawVelocityTimestamp;

        /// <summary>Raw Velocity from Drive (counts/s)</summary>
        public double Raw_Velocity
        {
            get { lock (_lock) return _rawVelocity; }
            set
            {
                lock (_lock)
                {
                    _rawVelocity          = value;
                    _rawVelocityTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of Raw Velocity</summary>
        public DateTime Raw_Velocity_Timestamp
        {
            get { lock (_lock) return _rawVelocityTimestamp; }
        }

        #endregion

        #region Filtered Velocity (Kalman Filter Output)

        private double   _filteredVelocity;
        private DateTime _filteredVelocityTimestamp;

        /// <summary>
        /// Filtered Velocity from Kalman Filter (counts/s).
        /// Smoothed velocity estimate derived from position tracking.
        /// </summary>
        public double Filtered_Velocity
        {
            get { lock (_lock) return _filteredVelocity; }
            set
            {
                lock (_lock)
                {
                    _filteredVelocity          = value;
                    _filteredVelocityTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of Filtered Velocity</summary>
        public DateTime Filtered_Velocity_Timestamp
        {
            get { lock (_lock) return _filteredVelocityTimestamp; }
        }

        #endregion

        #region Command Velocity (Manual Control & Tracking)

        private double   _commandVelocityDegSec;
        private DateTime _commandVelocityTimestamp;

        /// <summary>
        /// Command Velocity for Altitude Axis (deg/s).
        /// Written by: Manual Control / Satellite Tracking.
        /// Read by: Write Loop.
        /// </summary>
        public double Command_Velocity_Deg_Sec
        {
            get { lock (_lock) return _commandVelocityDegSec; }
            set
            {
                lock (_lock)
                {
                    _commandVelocityDegSec   = value;
                    _commandVelocityTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of Command Velocity</summary>
        public DateTime Command_Velocity_Timestamp
        {
            get { lock (_lock) return _commandVelocityTimestamp; }
        }

        #endregion

        #region Current

        private double   _currentIQ;
        private DateTime _currentIQTimestamp;

        /// <summary>Current IQ from Drive (A)</summary>
        public double Current_IQ
        {
            get { lock (_lock) return _currentIQ; }
            set
            {
                lock (_lock)
                {
                    _currentIQ          = value;
                    _currentIQTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of Current IQ</summary>
        public DateTime Current_IQ_Timestamp
        {
            get { lock (_lock) return _currentIQTimestamp; }
        }

        #endregion

        #region Drive State

        private bool     _isDriveEnabled;
        private DateTime _isDriveEnabledTimestamp;

        /// <summary>Drive Enable/Disable status (MO=1 → true)</summary>
        public bool Is_Drive_Enabled
        {
            get { lock (_lock) return _isDriveEnabled; }
            set
            {
                lock (_lock)
                {
                    _isDriveEnabled          = value;
                    _isDriveEnabledTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of Drive Enable State</summary>
        public DateTime Is_Drive_Enabled_Timestamp
        {
            get { lock (_lock) return _isDriveEnabledTimestamp; }
        }

        #endregion

        #region Calculated Values

        private double _positionCounts;
        private double _positionDeg;
        private double _velocityDegSec;

        /// <summary>Calculated Position in Counts (modulo)</summary>
        public double Position_Counts
        {
            get { lock (_lock) return _positionCounts; }
            set { lock (_lock) _positionCounts = value; }
        }

        /// <summary>Calculated Position in Degrees</summary>
        public double Position_Deg
        {
            get { lock (_lock) return _positionDeg; }
            set { lock (_lock) _positionDeg = value; }
        }

        /// <summary>Calculated Velocity in Degrees/Second</summary>
        public double Velocity_Deg_Sec
        {
            get { lock (_lock) return _velocityDegSec; }
            set { lock (_lock) _velocityDegSec = value; }
        }

        #endregion

        #region Filter Parameters (Velocity Loop)

        // Filter #1 (Lead-Lag or Low-Pass)
        public int    Filter1_Type      { get; set; } = 0;
        public double Filter1_Frequency { get; set; } = 0;
        public double Filter1_Damping   { get; set; } = 0;

        // Filter #2 (Notch)
        public int    Filter2_Type      { get; set; } = 0;
        public double Filter2_Frequency { get; set; } = 0;
        public double Filter2_QFactor   { get; set; } = 0;
        public double Filter2_Magnitude { get; set; } = 0;

        // Filter #3 (Notch)
        public int    Filter3_Type      { get; set; } = 0;
        public double Filter3_Frequency { get; set; } = 0;
        public double Filter3_QFactor   { get; set; } = 0;
        public double Filter3_Magnitude { get; set; } = 0;

        // Filter #4 (Notch)
        public int    Filter4_Type      { get; set; } = 0;
        public double Filter4_Frequency { get; set; } = 0;
        public double Filter4_QFactor   { get; set; } = 0;
        public double Filter4_Magnitude { get; set; } = 0;

        // Feedback Filter #1 (Notch)
        public int    FeedbackFilter1_Type      { get; set; } = 0;
        public double FeedbackFilter1_Frequency { get; set; } = 0;
        public double FeedbackFilter1_QFactor   { get; set; } = 0;
        public double FeedbackFilter1_Magnitude { get; set; } = 0;

        // Feedback Filter #2 (Notch)
        public int    FeedbackFilter2_Type      { get; set; } = 0;
        public double FeedbackFilter2_Frequency { get; set; } = 0;
        public double FeedbackFilter2_QFactor   { get; set; } = 0;
        public double FeedbackFilter2_Magnitude { get; set; } = 0;

        private DateTime _filterParametersTimestamp;

        /// <summary>Timestamp of last filter parameter update</summary>
        public DateTime FilterParametersTimestamp
        {
            get { lock (_lock) return _filterParametersTimestamp; }
        }

        public void UpdateFilterParametersTimestamp()
        {
            lock (_lock) { _filterParametersTimestamp = NtpTimeService.Instance.GetCurrentTime(); }
        }

        #endregion

        #region Tracking Status

        private bool         _isTracking;
        private DateTime     _isTrackingTimestamp;
        private TrackingMode _trackingMode;
        private double       _trackingRmsError_Arcsec;
        private DateTime     _trackingRmsTimestamp;
        private int          _trackingSampleCount;

        /// <summary>
        /// True when tracking error is within acceptable threshold.
        /// true = ready for imaging; false = slewing or tracking error too high.
        /// </summary>
        public bool IsTracking
        {
            get { lock (_lock) return _isTracking; }
            set
            {
                lock (_lock)
                {
                    _isTracking          = value;
                    _isTrackingTimestamp = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of last IsTracking update</summary>
        public DateTime IsTrackingTimestamp
        {
            get { lock (_lock) return _isTrackingTimestamp; }
        }

        /// <summary>Current tracking mode (Sidereal, Satellite, etc.)</summary>
        public TrackingMode TrackingMode
        {
            get { lock (_lock) return _trackingMode; }
            set { lock (_lock) _trackingMode = value; }
        }

        /// <summary>
        /// Current RMS tracking error in arcseconds.
        /// Lower is better (less than 3" is excellent for sidereal).
        /// </summary>
        public double TrackingRmsError_Arcsec
        {
            get { lock (_lock) return _trackingRmsError_Arcsec; }
            set
            {
                lock (_lock)
                {
                    _trackingRmsError_Arcsec = value;
                    _trackingRmsTimestamp    = NtpTimeService.Instance.GetCurrentTime();
                }
            }
        }

        /// <summary>Timestamp of last RMS update</summary>
        public DateTime TrackingRmsTimestamp
        {
            get { lock (_lock) return _trackingRmsTimestamp; }
        }

        /// <summary>Number of samples used for RMS calculation</summary>
        public int TrackingSampleCount
        {
            get { lock (_lock) return _trackingSampleCount; }
            set { lock (_lock) _trackingSampleCount = value; }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Returns true if position data has not been updated within maxAgeSeconds.
        /// </summary>
        public bool IsPositionDataStale(double maxAgeSeconds = 1.0)
        {
            DateTime ts;
            lock (_lock) { ts = _rawPositionTimestamp; }
            return (NtpTimeService.Instance.GetCurrentTime() - ts).TotalSeconds > maxAgeSeconds;
        }

        /// <summary>
        /// Returns true if velocity data has not been updated within maxAgeSeconds.
        /// </summary>
        public bool IsVelocityDataStale(double maxAgeSeconds = 1.0)
        {
            DateTime ts;
            lock (_lock) { ts = _rawVelocityTimestamp; }
            return (NtpTimeService.Instance.GetCurrentTime() - ts).TotalSeconds > maxAgeSeconds;
        }

        /// <summary>Age of position data in seconds.</summary>
        public double GetPositionDataAge()
        {
            DateTime ts;
            lock (_lock) { ts = _rawPositionTimestamp; }
            return (NtpTimeService.Instance.GetCurrentTime() - ts).TotalSeconds;
        }

        /// <summary>Age of velocity data in seconds.</summary>
        public double GetVelocityDataAge()
        {
            DateTime ts;
            lock (_lock) { ts = _rawVelocityTimestamp; }
            return (NtpTimeService.Instance.GetCurrentTime() - ts).TotalSeconds;
        }

        /// <summary>
        /// Update IsTracking based on position error vs. sensitivity threshold.
        /// </summary>
        public void UpdateTrackingStatus(double targetPositionDeg, double currentPositionDeg, double trackingSensitivityDeg)
        {
            double positionError = Math.Abs(targetPositionDeg - currentPositionDeg);
            bool   newTracking   = positionError <= trackingSensitivityDeg;

            bool changed;
            lock (_lock)
            {
                changed     = newTracking != _isTracking;
                _isTracking = newTracking;
                if (_isTracking)
                    _isTrackingTimestamp = NtpTimeService.Instance.GetCurrentTime();
            }

            if (changed)
                SimpleLogger.Info(
                    $"[ALTITUDE] Tracking {(newTracking ? "ACQUIRED" : "LOST")} " +
                    $"(error={positionError:F4}°, threshold={trackingSensitivityDeg:F2}°)", "AltInfo");
        }

        /// <summary>Reset all fields to default values.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _rawPosition    = 0;
                _rawVelocity    = 0;
                _currentIQ      = 0;
                _isDriveEnabled = false;
                _positionCounts = 0;
                _positionDeg    = 0;
                _velocityDegSec = 0;

                DateTime now = NtpTimeService.Instance.GetCurrentTime();
                _rawPositionTimestamp    = now;
                _rawVelocityTimestamp    = now;
                _currentIQTimestamp      = now;
                _isDriveEnabledTimestamp = now;
            }
        }

        /// <summary>Human-readable summary of current drive state.</summary>
        public override string ToString()
        {
            return $"AltitudeInfo:\n" +
                   $"  Position : {Raw_Position:F2} counts  (age {GetPositionDataAge():F3}s)\n" +
                   $"  Velocity : {Raw_Velocity:F2} counts/s  (age {GetVelocityDataAge():F3}s)\n" +
                   $"  Current  : {Current_IQ:F2} A\n" +
                   $"  Enabled  : {Is_Drive_Enabled}";
        }

        #endregion

        #region Constructor

        public AltitudeInfo()
        {
            // Keep 2 seconds of data at 200 Hz (~400 points)
            DataBuffer = new DataBuffer(maxAgeSeconds: 2.0, maxCapacity: 500);
            Reset();
        }

        #endregion
    }
}
