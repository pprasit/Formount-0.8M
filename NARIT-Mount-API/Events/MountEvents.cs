using NARIT.Mount.API.Models;

namespace NARIT.Mount.API.Events
{
    /// <summary>
    /// Event raised when mount position is updated (at polling rate ~66Hz).
    /// </summary>
    public class PositionChangedEventArgs : EventArgs
    {
        /// <summary>Current mount position snapshot</summary>
        public MountPosition Position { get; set; }
    }

    /// <summary>
    /// Event raised when tracking state changes (start/stop/mode change).
    /// </summary>
    public class TrackingStateChangedEventArgs : EventArgs
    {
        /// <summary>Whether tracking is now active</summary>
        public bool IsTracking { get; set; }

        /// <summary>Current tracking mode</summary>
        public TrackingModeType Mode { get; set; }

        /// <summary>Previous tracking mode</summary>
        public TrackingModeType PreviousMode { get; set; }

        /// <summary>Object name being tracked</summary>
        public string ObjectName { get; set; } = "";
    }

    /// <summary>
    /// Event raised when a mount error occurs.
    /// </summary>
    public class MountErrorEventArgs : EventArgs
    {
        /// <summary>Error message</summary>
        public string Message { get; set; } = "";

        /// <summary>Exception that caused the error (may be null)</summary>
        public Exception Exception { get; set; }

        /// <summary>Whether the error is recoverable</summary>
        public bool IsRecoverable { get; set; } = true;
    }

    /// <summary>
    /// Event raised when drive enable/disable state changes.
    /// </summary>
    public class DriveStateChangedEventArgs : EventArgs
    {
        /// <summary>Which axis changed</summary>
        public AxisType Axis { get; set; }

        /// <summary>New enabled state</summary>
        public bool IsEnabled { get; set; }
    }

    /// <summary>
    /// Axis identifier.
    /// </summary>
    public enum AxisType
    {
        /// <summary>Azimuth axis</summary>
        Azimuth,

        /// <summary>Altitude axis</summary>
        Altitude
    }
}
