using System;

namespace ForkMount_WPF.Engine.Tracking
{
    /// <summary>
    /// Tracking modes for mount control
    /// </summary>
    public enum TrackingMode
    {
        /// <summary>
        /// No tracking - mount is idle or slewing
        /// </summary>
        None = 0,

        /// <summary>
        /// Sidereal tracking for star/deep-sky astrophotography
        /// Compensates for Earth's rotation (15.041 deg/hour)
        /// Threshold: RMS error ? 3 arcseconds
        /// </summary>
        Sidereal = 1,

        /// <summary>
        /// Satellite tracking for fast-moving objects
        /// Follows TLE predictions
        /// Threshold: RMS error ? 0.2 degrees (720 arcseconds)
        /// </summary>
        Satellite = 2,

        /// <summary>
        /// Lunar tracking - slower than sidereal
        /// Compensates for Moon's motion
        /// </summary>
        Lunar = 3,

        /// <summary>
        /// Solar tracking
        /// </summary>
        Solar = 4
    }

    /// <summary>
    /// Tracking thresholds for different modes
    /// </summary>
    public static class TrackingThresholds
    {
        /// <summary>
        /// Sidereal tracking RMS threshold (arcseconds)
        /// If RMS error ? this value, IsTracking = true
        /// </summary>
        public const double SIDEREAL_RMS_ARCSEC = 3.0;

        /// <summary>
        /// Satellite tracking RMS threshold (degrees)
        /// If RMS error ? this value, IsTracking = true
        /// </summary>
        public const double SATELLITE_RMS_DEG = 0.2;

        /// <summary>
        /// Satellite tracking RMS threshold (arcseconds)
        /// </summary>
        public const double SATELLITE_RMS_ARCSEC = SATELLITE_RMS_DEG * 3600.0; // 720"

        /// <summary>
        /// Minimum number of samples required for RMS calculation
        /// </summary>
        public const int MIN_SAMPLES_FOR_RMS = 10;

        /// <summary>
        /// Sample interval for RMS calculation (milliseconds)
        /// </summary>
        public const double RMS_SAMPLE_INTERVAL_MS = 100.0;

        /// <summary>
        /// Duration to maintain tracking before confirming (seconds)
        /// </summary>
        public const double TRACKING_CONFIRMATION_DURATION_SEC = 1.0;

        /// <summary>
        /// Get RMS threshold in arcseconds for a given tracking mode
        /// </summary>
        public static double GetRmsThreshold_Arcsec(TrackingMode mode)
        {
            return mode switch
            {
                TrackingMode.Sidereal => SIDEREAL_RMS_ARCSEC,
                TrackingMode.Satellite => SATELLITE_RMS_ARCSEC,
                TrackingMode.Lunar => SIDEREAL_RMS_ARCSEC * 1.2, // Slightly more relaxed
                TrackingMode.Solar => SIDEREAL_RMS_ARCSEC * 1.5, // Even more relaxed
                _ => double.MaxValue // No tracking, never satisfied
            };
        }
    }
}
