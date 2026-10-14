namespace NARIT.Mount.API.Models
{
    /// <summary>
    /// NTP server configuration for time synchronization.
    /// </summary>
    public class NtpConfig
    {
        /// <summary>NTP server hostname or IP address</summary>
        public string ServerAddress { get; set; } = "pool.ntp.org";

        /// <summary>Sync interval in seconds (how often to re-sync with NTP server)</summary>
        public int SyncIntervalSeconds { get; set; } = 3600;

        /// <summary>
        /// Create NTP configuration.
        /// </summary>
        /// <param name="server">NTP server address (default: pool.ntp.org)</param>
        /// <param name="syncIntervalSeconds">Re-sync interval in seconds (default: 3600 = 1 hour)</param>
        public NtpConfig(string server = "pool.ntp.org", int syncIntervalSeconds = 3600)
        {
            ServerAddress = server;
            SyncIntervalSeconds = syncIntervalSeconds;
        }

        /// <summary>Thai NTP server (ntp.ku.ac.th)</summary>
        public static NtpConfig ThaiNtp => new NtpConfig("ntp.ku.ac.th", 3600);
    }

    /// <summary>
    /// Tracking information for the current tracking session.
    /// </summary>
    public class TrackingInfo
    {
        /// <summary>Current tracking mode</summary>
        public TrackingModeType Mode { get; set; }

        /// <summary>Target Right Ascension in hours (0-24)</summary>
        public double TargetRA_Hours { get; set; }

        /// <summary>Target Declination in degrees (-90 to +90)</summary>
        public double TargetDec_Deg { get; set; }

        /// <summary>True when TargetRA/TargetDec represent apparent/current-epoch coordinates</summary>
        public bool TargetRaDecIsApparent { get; set; }

        /// <summary>Julian Day calculated inside Lib</summary>
        public double JulianDay { get; set; }

        /// <summary>Whether Lib currently has Actual RA/Dec J2000 derived from corrected Alt/Az</summary>
        public bool HasActualRaDecJ2000 { get; set; }

        /// <summary>Actual Right Ascension J2000 derived inside Lib from corrected Alt/Az (hours)</summary>
        public double ActualRaJ2000_Hours { get; set; }

        /// <summary>Actual Declination J2000 derived inside Lib from corrected Alt/Az (degrees)</summary>
        public double ActualDecJ2000_Deg { get; set; }

        /// <summary>Target Azimuth in degrees (computed from RA/Dec)</summary>
        public double TargetAzimuth_Deg { get; set; }

        /// <summary>Target Altitude in degrees (computed from RA/Dec)</summary>
        public double TargetAltitude_Deg { get; set; }

        /// <summary>Azimuth tracking RMS error in arcseconds</summary>
        public double AzimuthRms_Arcsec { get; set; }

        /// <summary>Altitude tracking RMS error in arcseconds</summary>
        public double AltitudeRms_Arcsec { get; set; }

        /// <summary>Mount is on target (within tracking sensitivity)</summary>
        public bool IsOnTarget { get; set; }

        /// <summary>Tracking duration since start</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>Local Sidereal Time in hours</summary>
        public double LocalSiderealTime_Hours { get; set; }

        /// <summary>Object name being tracked</summary>
        public string ObjectName { get; set; } = "";
    }

    /// <summary>
    /// Orbital elements for Near-Earth Object (NEO) tracking.
    /// Standard 6-element Keplerian orbit definition.
    /// </summary>
    public class OrbitalElements
    {
        /// <summary>Epoch of the elements (Julian Date)</summary>
        public double Epoch_JD { get; set; }

        /// <summary>Semi-major axis in AU</summary>
        public double SemiMajorAxis_AU { get; set; }

        /// <summary>Eccentricity (0 = circle, 0-1 = ellipse)</summary>
        public double Eccentricity { get; set; }

        /// <summary>Inclination in degrees</summary>
        public double Inclination_Deg { get; set; }

        /// <summary>Longitude of ascending node in degrees (?)</summary>
        public double LongAscendingNode_Deg { get; set; }

        /// <summary>Argument of perihelion in degrees (?)</summary>
        public double ArgPerihelion_Deg { get; set; }

        /// <summary>Mean anomaly at epoch in degrees</summary>
        public double MeanAnomaly_Deg { get; set; }

        /// <summary>Object name/designation</summary>
        public string Name { get; set; } = "";
    }
}
