using System;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Info
{
    /// <summary>
    /// Thread-Safe Data Hub for the Field-Derotator Axis (Axis 3 / FORK1, 32-bit encoder).
    /// Populated by CentralizedPollingManager (drive state) and by InstrumentControl (derotation state).
    /// Allows any component to read current position, velocity, motor state, and derotation rate.
    /// </summary>
    public class FieldDerotatorInfo
    {
        private readonly object _lock = new object();

        // ── Raw drive values ─────────────────────────────────────────────────────────
        private double   _positionCounts;
        private double   _velocityCountsSec;
        private bool     _isMotorEnabled;
        private DateTime _driveTimestamp = DateTime.MinValue;

        // ── Encoder resolution ───────────────────────────────────────────────────────
        private long _encoderResolution = 4_294_967_296L; // 32-bit default (2^32)

        // ── Field-Derotation computed state ──────────────────────────────────────────
        private bool     _isDerotationActive;
        private double   _computedRateDegSec;
        private double   _inputAzDeg;
        private double   _inputAltDeg;
        private DateTime _derotationTimestamp = DateTime.MinValue;

        // ────────────────────────────────────────────────────────────────────────────
        //  Public Properties — Drive State
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>Raw position from drive (counts)</summary>
        public double Position_Counts     { get { lock (_lock) return _positionCounts;    } }

        /// <summary>Position converted to degrees</summary>
        public double Position_Deg        { get { lock (_lock) return _encoderResolution > 0 ? _positionCounts / _encoderResolution * 360.0 : 0; } }

        /// <summary>Raw velocity from drive (counts/s)</summary>
        public double Velocity_Counts_Sec { get { lock (_lock) return _velocityCountsSec; } }

        /// <summary>Velocity converted to deg/s</summary>
        public double Velocity_Deg_Sec    { get { lock (_lock) return _encoderResolution > 0 ? _velocityCountsSec / _encoderResolution * 360.0 : 0; } }

        /// <summary>True when drive motor is enabled (MO=1)</summary>
        public bool   Is_Motor_Enabled    { get { lock (_lock) return _isMotorEnabled;    } }

        /// <summary>Timestamp of the last successful drive poll</summary>
        public DateTime Drive_Timestamp   { get { lock (_lock) return _driveTimestamp;    } }

        /// <summary>Encoder resolution in use (counts/rev)</summary>
        public long EncoderResolution     { get { lock (_lock) return _encoderResolution; } }

        // ────────────────────────────────────────────────────────────────────────────
        //  Public Properties — Field-Derotation State
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>True when automatic field-derotation is running</summary>
        public bool   Is_Derotation_Active  { get { lock (_lock) return _isDerotationActive;  } }

        /// <summary>
        /// Computed parallactic-angle rate (deg/s) sent to the derotator.
        /// The mount uses north-based azimuth (N=0°, E=90°), so
        /// dPA/dt = ω_E · cos(φ) · cos(Az) / cos(Alt).
        /// </summary>
        public double Computed_Rate_Deg_Sec { get { lock (_lock) return _computedRateDegSec;  } }

        /// <summary>Azimuth (deg) used in the last derotation rate calculation</summary>
        public double Input_Az_Deg          { get { lock (_lock) return _inputAzDeg;          } }

        /// <summary>Altitude (deg) used in the last derotation rate calculation</summary>
        public double Input_Alt_Deg         { get { lock (_lock) return _inputAltDeg;         } }

        /// <summary>Timestamp of the last derotation rate computation</summary>
        public DateTime Derotation_Timestamp{ get { lock (_lock) return _derotationTimestamp; } }

        // ────────────────────────────────────────────────────────────────────────────
        //  Update Methods
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Set encoder resolution once during initialization.
        /// </summary>
        public void SetEncoderResolution(long resolution)
        {
            lock (_lock) { _encoderResolution = resolution > 0 ? resolution : 4_294_967_296L; }
        }

        /// <summary>
        /// Update drive state (position, velocity, motor enable) from polling loop.
        /// </summary>
        public void UpdateFromDrive(double positionCounts, double velocityCountsSec, bool motorEnabled)
        {
            lock (_lock)
            {
                _positionCounts    = positionCounts;
                _velocityCountsSec = velocityCountsSec;
                _isMotorEnabled    = motorEnabled;
                _driveTimestamp    = NtpTimeService.Instance.GetCurrentTime();
            }
        }

        /// <summary>
        /// Update field-derotation state (called by InstrumentControl after each derotation tick).
        /// </summary>
        public void UpdateDerotation(bool isActive, double rateDegSec, double azDeg, double altDeg)
        {
            lock (_lock)
            {
                _isDerotationActive  = isActive;
                _computedRateDegSec  = rateDegSec;
                _inputAzDeg          = azDeg;
                _inputAltDeg         = altDeg;
                _derotationTimestamp = NtpTimeService.Instance.GetCurrentTime();
            }
        }
    }
}
