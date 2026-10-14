using System;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Info
{
    /// <summary>
    /// Thread-Safe Data Hub for M3 Axis (Axis 4 / FORK2 — filter wheel / port selector)
    /// Populated by CentralizedPollingManager every ~5 main cycles (~13 Hz).
    /// UI and control code read from here instead of polling the drive directly.
    /// </summary>
    public class M3Info
    {
        private readonly object _lock = new object();

        // ── Raw drive values ─────────────────────────────────────────────────────────
        private double   _positionCounts;
        private double   _velocityCountsSec;
        private bool     _isMotorEnabled;
        private DateTime _timestamp = DateTime.MinValue;

        // ── Encoder resolution (set by CentralizedPollingManager from AppSettings) ──
        private long _encoderResolution = 262144L; // 18-bit default (2^18)

        // ────────────────────────────────────────────────────────────────────────────
        //  Public Properties (thread-safe reads)
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>Raw position from drive (counts)</summary>
        public double Position_Counts    { get { lock (_lock) return _positionCounts;    } }

        /// <summary>Position converted to degrees</summary>
        public double Position_Deg       { get { lock (_lock) return _encoderResolution > 0 ? _positionCounts / _encoderResolution * 360.0 : 0; } }

        /// <summary>Raw velocity from drive (counts/s)</summary>
        public double Velocity_Counts_Sec{ get { lock (_lock) return _velocityCountsSec; } }

        /// <summary>Velocity converted to deg/s</summary>
        public double Velocity_Deg_Sec   { get { lock (_lock) return _encoderResolution > 0 ? _velocityCountsSec / _encoderResolution * 360.0 : 0; } }

        /// <summary>True when drive motor is enabled (MO=1)</summary>
        public bool   Is_Motor_Enabled   { get { lock (_lock) return _isMotorEnabled;    } }

        /// <summary>Timestamp of the last successful poll</summary>
        public DateTime Timestamp        { get { lock (_lock) return _timestamp;         } }

        /// <summary>Encoder resolution in use (counts/rev)</summary>
        public long EncoderResolution    { get { lock (_lock) return _encoderResolution; } }

        // ────────────────────────────────────────────────────────────────────────────
        //  Update Methods (called by CentralizedPollingManager)
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Set encoder resolution once during initialization.
        /// </summary>
        public void SetEncoderResolution(long resolution)
        {
            lock (_lock) { _encoderResolution = resolution > 0 ? resolution : 262144L; }
        }

        /// <summary>
        /// Update all drive values in one atomic write (called from polling loop).
        /// </summary>
        public void Update(double positionCounts, double velocityCountsSec, bool motorEnabled)
        {
            lock (_lock)
            {
                _positionCounts    = positionCounts;
                _velocityCountsSec = velocityCountsSec;
                _isMotorEnabled    = motorEnabled;
                _timestamp         = NtpTimeService.Instance.GetCurrentTime();
            }
        }
    }
}
