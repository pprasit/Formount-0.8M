namespace NARIT.Mount.API.Models
{
    /// <summary>
    /// Snapshot of the mount's current position in multiple coordinate systems.
    /// </summary>
    public class MountPosition
    {
        /// <summary>Azimuth position in degrees (raw encoder value converted to degrees)</summary>
        public double Azimuth_Raw_Deg { get; set; }

        /// <summary>Altitude position in degrees (raw encoder value converted to degrees)</summary>
        public double Altitude_Raw_Deg { get; set; }

        /// <summary>Azimuth position in degrees (corrected by pointing model)</summary>
        public double Azimuth_Corrected_Deg { get; set; }

        /// <summary>Altitude position in degrees (corrected by pointing model)</summary>
        public double Altitude_Corrected_Deg { get; set; }

        /// <summary>Timestamp when this position was sampled (NTP-synced)</summary>
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Snapshot of a single axis state.
    /// </summary>
    public class AxisState
    {
        /// <summary>Raw encoder position in counts</summary>
        public double RawPosition_Counts { get; set; }

        /// <summary>Raw position converted to degrees</summary>
        public double RawPosition_Deg { get; set; }

        /// <summary>Filtered position from Kalman filter in counts</summary>
        public double FilteredPosition_Counts { get; set; }

        /// <summary>Raw velocity in counts/sec</summary>
        public double RawVelocity_CountsPerSec { get; set; }

        /// <summary>Filtered velocity from Kalman filter in counts/sec</summary>
        public double FilteredVelocity_CountsPerSec { get; set; }

        /// <summary>Command velocity being sent to drive in deg/sec</summary>
        public double CommandVelocity_DegPerSec { get; set; }

        /// <summary>Motor current (IQ) in amps</summary>
        public double Current_IQ { get; set; }

        /// <summary>Whether the drive is currently enabled (motor ON)</summary>
        public bool IsDriveEnabled { get; set; }

        /// <summary>CCW limit sensor is active</summary>
        public bool CCW_Sensor_Active { get; set; }

        /// <summary>CW limit sensor is active</summary>
        public bool CW_Sensor_Active { get; set; }

        /// <summary>Whether this axis is currently tracking within RMS threshold</summary>
        public bool IsTracking { get; set; }

        /// <summary>Calculated axis status: Disabled, Idle, Slewing, or Tracking</summary>
        public string Axis_Status { get; set; } = "Disabled";

        /// <summary>Configured tracking sensitivity threshold in degrees</summary>
        public double TrackingSensitivity_Deg { get; set; }

        /// <summary>Current tracking RMS error in arcseconds</summary>
        public double TrackingRmsError_Arcsec { get; set; }

        /// <summary>Number of samples used for RMS calculation</summary>
        public int TrackingSampleCount { get; set; }

        /// <summary>Timestamp of last position update</summary>
        public DateTime Timestamp { get; set; }
    }
}
