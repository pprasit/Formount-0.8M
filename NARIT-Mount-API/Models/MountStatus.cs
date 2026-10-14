namespace NARIT.Mount.API.Models
{
    /// <summary>
    /// Overall mount status information.
    /// </summary>
    public class MountStatus
    {
        /// <summary>Current mount state</summary>
        public MountState State { get; set; }

        /// <summary>Both axes are enabled (motor ON)</summary>
        public bool IsMountEnabled { get; set; }

        /// <summary>Mount is actively tracking a target within RMS threshold</summary>
        public bool IsTracking { get; set; }

        /// <summary>Whether NTP time is synchronized</summary>
        public bool IsTimeSynchronized { get; set; }

        /// <summary>NTP offset from local clock in milliseconds</summary>
        public double NtpOffsetMs { get; set; }

        /// <summary>Current UTC time (NTP-synced)</summary>
        public DateTime SystemUtcTime { get; set; }

        /// <summary>Julian Day computed inside Lib from the synchronized UTC time</summary>
        public double JulianDay { get; set; }

        /// <summary>Name of the object currently being tracked (empty if none)</summary>
        public string CurrentTrackingObjectName { get; set; } = "";

        /// <summary>Whether Lib currently has a command RA/Dec cached</summary>
        public bool HasCommandRaDec { get; set; }

        /// <summary>Command Right Ascension received by Lib (hours)</summary>
        public double CommandRa_Hours { get; set; }

        /// <summary>Command Declination received by Lib (degrees)</summary>
        public double CommandDec_Deg { get; set; }

        /// <summary>True when the command RA/Dec received by Lib is apparent/current-epoch</summary>
        public bool CommandRaDecIsApparent { get; set; }

        /// <summary>Whether Lib currently has Actual RA/Dec J2000 derived from the corrected position</summary>
        public bool HasActualRaDecJ2000 { get; set; }

        /// <summary>Actual Right Ascension J2000 derived inside Lib from corrected Alt/Az (hours)</summary>
        public double ActualRaJ2000_Hours { get; set; }

        /// <summary>Actual Declination J2000 derived inside Lib from corrected Alt/Az (degrees)</summary>
        public double ActualDecJ2000_Deg { get; set; }

        /// <summary>Current tracking mode</summary>
        public TrackingModeType CurrentTrackingMode { get; set; }

        /// <summary>Azimuth axis state snapshot</summary>
        public AxisState AzimuthAxis { get; set; }

        /// <summary>Altitude axis state snapshot</summary>
        public AxisState AltitudeAxis { get; set; }

        /// <summary>Current mount position snapshot</summary>
        public MountPosition Position { get; set; }

        /// <summary>Telescope offset applied from camera alignment (arcseconds)</summary>
        public double TelescopeOffset_Az_Arcsec { get; set; }

        /// <summary>Telescope offset applied from camera alignment (arcseconds)</summary>
        public double TelescopeOffset_Alt_Arcsec { get; set; }
    }

    /// <summary>
    /// Mount state machine states.
    /// </summary>
    public enum MountState
    {
        /// <summary>Not connected to drive hardware</summary>
        Disconnected,

        /// <summary>Connected but drives are disabled</summary>
        Disabled,

        /// <summary>Drives enabled, mount is idle (not moving)</summary>
        Idle,

        /// <summary>Slewing to a target position</summary>
        Slewing,

        /// <summary>Actively tracking a celestial object</summary>
        Tracking,

        /// <summary>Emergency stop active</summary>
        Stopped,

        /// <summary>Error condition</summary>
        Error
    }

    /// <summary>
    /// Tracking mode types exposed by the API.
    /// </summary>
    public enum TrackingModeType
    {
        /// <summary>No tracking active</summary>
        None = 0,

        /// <summary>Sidereal tracking (stars, galaxies, DSO) � 15.041�/hour</summary>
        Sidereal = 1,

        /// <summary>Satellite tracking via TLE/SGP4</summary>
        Satellite = 2,

        /// <summary>Lunar tracking</summary>
        Lunar = 3,

        /// <summary>Solar tracking</summary>
        Solar = 4,

        /// <summary>Asteroid tracking via orbital elements</summary>
        Asteroid = 5,

        /// <summary>Comet tracking via orbital elements</summary>
        Comet = 6,

        /// <summary>Near-Earth Object tracking via orbital elements</summary>
        NearEarthObject = 7
    }
}
