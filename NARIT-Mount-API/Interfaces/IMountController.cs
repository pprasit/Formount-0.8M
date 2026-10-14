using NARIT.Mount.API.Events;
using NARIT.Mount.API.Models;

namespace NARIT.Mount.API.Interfaces
{
    /// <summary>
    /// Combined dual-axis mount controller interface.
    /// Provides coordinated control of both Azimuth and Altitude axes together.
    /// </summary>
    public interface IMountController : IDisposable
    {
        #region Connection

        /// <summary>Whether the mount is connected to the drive hardware</summary>
        bool IsConnected { get; }

        /// <summary>Connect to mount drive hardware</summary>
        Task<bool> ConnectAsync();

        /// <summary>Disconnect from mount drive hardware</summary>
        void Disconnect();

        #endregion

        #region Axis Controllers (Individual Axis Access)

        /// <summary>Azimuth axis controller (for per-axis control)</summary>
        IAxisController AzimuthAxis { get; }

        /// <summary>Altitude axis controller (for per-axis control)</summary>
        IAxisController AltitudeAxis { get; }

        #endregion

        #region Combined Mount Control

        /// <summary>Enable both axes simultaneously</summary>
        void Enable();

        /// <summary>Disable both axes simultaneously</summary>
        void Disable();

        /// <summary>Whether both drives are enabled</summary>
        bool IsMountEnabled { get; }

        /// <summary>
        /// Goto position on both axes simultaneously.
        /// </summary>
        /// <param name="azimuthDeg">Target azimuth in degrees</param>
        /// <param name="altitudeDeg">Target altitude in degrees</param>
        /// <param name="velocityDegPerSec">Slew velocity in degrees/second</param>
        void GotoPosition(double azimuthDeg, double altitudeDeg, double velocityDegPerSec);

        /// <summary>
        /// Move both axes at specified velocities.
        /// </summary>
        /// <param name="azVelocityDegPerSec">Azimuth velocity in deg/sec</param>
        /// <param name="altVelocityDegPerSec">Altitude velocity in deg/sec</param>
        void MoveVelocity(double azVelocityDegPerSec, double altVelocityDegPerSec);

        /// <summary>
        /// Jog both axes together in a single mount command.
        /// Any active tracking should be stopped before manual jog begins.
        /// </summary>
        /// <param name="azVelocityDegPerSec">Azimuth jog velocity in deg/sec</param>
        /// <param name="altVelocityDegPerSec">Altitude jog velocity in deg/sec</param>
        void Jog(double azVelocityDegPerSec, double altVelocityDegPerSec);

        /// <summary>Stop all motion on both axes</summary>
        void Stop();

        /// <summary>
        /// Apply a telescope offset using the specified coordinate mode, step size, and direction.
        /// This matches the Camera/Offset workflow in the main mount application.
        /// </summary>
        /// <param name="offsetType">Offset coordinate mode shown to the user</param>
        /// <param name="stepSizeArcsec">Offset step size in arcseconds</param>
        /// <param name="direction">Requested offset direction</param>
        void ApplyOffset(OffsetType offsetType, double stepSizeArcsec, OffsetDirection direction);

        /// <summary>
        /// Reset accumulated telescope offset back to zero on both axes.
        /// </summary>
        void ResetOffset();

        #endregion

        #region Tracking

        /// <summary>
        /// Start sidereal tracking (compensates for Earth rotation).
        /// Requires observer site to be configured.
        /// </summary>
        void TrackSidereal();

        /// <summary>
        /// Track a celestial object by RA/Dec coordinates (J2000).
        /// Automatically starts sidereal tracking to the specified target.
        /// </summary>
        /// <param name="raHours">Right Ascension in hours (0-24)</param>
        /// <param name="decDeg">Declination in degrees (-90 to +90)</param>
        /// <param name="objectName">Optional name for the object</param>
        void TrackObject(double raHours, double decDeg, string objectName = "");

        /// <summary>
        /// Track a celestial object by RA/Dec coordinates in J2000 string format.
        /// Automatically starts sidereal tracking to the specified target.
        /// </summary>
        /// <param name="raJ2000">Right Ascension in J2000 format HH:MM:SS.XXX</param>
        /// <param name="decJ2000">Declination in J2000 format [+-]DD:MM:SS.XXX</param>
        /// <param name="objectName">Optional name for the object</param>
        void TrackObject(string raJ2000, string decJ2000, string objectName = "");

        /// <summary>
        /// Track a satellite using TLE (Two-Line Element) data.
        /// </summary>
        /// <param name="tleLine1">TLE line 1</param>
        /// <param name="tleLine2">TLE line 2</param>
        /// <param name="satelliteName">Optional satellite name</param>
        void TrackSatellite(string tleLine1, string tleLine2, string satelliteName = "");

        /// <summary>
        /// Track an asteroid using user-supplied orbital elements.
        /// </summary>
        /// <param name="elements">Asteroid orbital elements</param>
        void TrackAsteroid(AsteroidOrbitalElements elements);

        /// <summary>
        /// Track a comet using user-supplied orbital elements.
        /// This API currently supports elliptical comet orbits only.
        /// </summary>
        /// <param name="elements">Comet orbital elements</param>
        void TrackComet(CometOrbitalElements elements);

        /// <summary>
        /// Track a Near-Earth Object using orbital elements.
        /// NOTE: This feature is planned but not yet implemented in the engine.
        /// Calling this method will throw NotImplementedException.
        /// </summary>
        /// <param name="elements">Keplerian orbital elements</param>
        void TrackNearEarthObject(OrbitalElements elements);

        /// <summary>
        /// Stop tracking and halt both axes.
        /// </summary>
        void StopTracking();

        /// <summary>Get current tracking information</summary>
        TrackingInfo GetTrackingInfo();

        /// <summary>Whether the mount is currently tracking a target</summary>
        bool IsTracking { get; }

        #endregion

        #region Status & Data

        /// <summary>Get comprehensive mount status snapshot</summary>
        MountStatus GetStatus();

        /// <summary>Get current mount position snapshot</summary>
        MountPosition GetPosition();

        #endregion

        #region Configuration

        /// <summary>
        /// Set observer site location and timezone.
        /// Required for RA/Dec ? Az/Alt coordinate conversions.
        /// </summary>
        /// <param name="site">Observer site configuration</param>
        void SetObserverSite(ObserverSite site);

        /// <summary>Get current observer site configuration</summary>
        ObserverSite GetObserverSite();

        /// <summary>
        /// Set pointing model file path.
        /// The pointing model corrects mechanical/alignment errors.
        /// </summary>
        /// <param name="filePath">Full path to the PointXP model file (.pxp)</param>
        void SetPointingModelPath(string filePath);

        /// <summary>Get the currently persisted pointing model file path.</summary>
        string GetPointingModelPath();

        /// <summary>
        /// Configure NTP time synchronization settings.
        /// </summary>
        /// <param name="config">NTP configuration</param>
        void SetNtpConfig(NtpConfig config);

        #endregion

        #region Events

        /// <summary>Raised when mount position is updated (~66Hz)</summary>
        event EventHandler<PositionChangedEventArgs> PositionChanged;

        /// <summary>Raised when tracking state changes</summary>
        event EventHandler<TrackingStateChangedEventArgs> TrackingStateChanged;

        /// <summary>Raised when a mount error occurs</summary>
        event EventHandler<MountErrorEventArgs> ErrorOccurred;

        #endregion
    }
}
