using System.Diagnostics;
using System.Globalization;
using NARIT.Mount.API.Events;
using NARIT.Mount.API.Interfaces;
using NARIT.Mount.API.Models;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.PointingModel;
using PWLib.Astro;

// Resolve ambiguity between API events and internal engine events
using ApiTrackingStateChangedEventArgs = NARIT.Mount.API.Events.TrackingStateChangedEventArgs;

namespace NARIT.Mount.API
{
    /// <summary>
    /// NARIT Mount Controller - Main API entry point for controlling the NARIT Fork Mount.
    /// 
    /// Usage:
    /// <code>
    /// var mount = new NaritMountController();
    /// mount.SetObserverSite(new ObserverSite(18.5736, 98.4820, 2457));
    /// await mount.ConnectAsync();
    /// mount.Enable();
    /// mount.TrackObject(raHours: 5.9195, decDeg: 7.4072, "Betelgeuse");
    /// // ... 
    /// mount.StopTracking();
    /// mount.Disable();
    /// mount.Disconnect();
    /// mount.Dispose();
    /// </code>
    /// </summary>
    public partial class NaritMountController : IMountController
    {
        #region Fields

        private PlatinumDrive _platinumDrive;
        private CentralizedPollingManager _pollingManager;
        private SiderealTracking _siderealTracking;
        private SatelliteTracking _satelliteTracking;
        private PointingModelManager _pointingModelManager;

        private AxisController _azimuthAxisController;
        private AxisController _altitudeAxisController;

        private ObserverSite _observerSite;
        private NtpConfig _ntpConfig;
        private string _preferredDriveSerialNumber;
        private string _pointingModelPath;
        private CancellationTokenSource _trackingAcquisitionCts;

        private bool _isConnected;
        private bool _isDisposed;
        private TrackingModeType _currentTrackingMode = TrackingModeType.None;
        private TrackingObjectType _currentTrackingObjectType = TrackingObjectType.None;

        private const double DefaultAzimuthSlewVelocityDegPerSec = 5.0;
        private const double DefaultAltitudeSlewVelocityDegPerSec = 3.0;
        private const double TrackingAcquisitionToleranceDeg = 0.15;
        private const double TrackingAcquisitionRelaxedToleranceDeg = 1.5;
        private const int TrackingAcquisitionPollIntervalMs = 250;
        private const int TrackingAcquisitionSettleDelayMs = 500;

        #endregion

        #region Events

        /// <inheritdoc />
        public event EventHandler<PositionChangedEventArgs> PositionChanged;

        /// <inheritdoc />
        public event EventHandler<ApiTrackingStateChangedEventArgs> TrackingStateChanged;

        /// <inheritdoc />
        public event EventHandler<MountErrorEventArgs> ErrorOccurred;

        #endregion

        #region Properties

        /// <inheritdoc />
        public bool IsConnected => _isConnected;

        /// <inheritdoc />
        public IAxisController AzimuthAxis => _azimuthAxisController;

        /// <inheritdoc />
        public IAxisController AltitudeAxis => _altitudeAxisController;

        /// <inheritdoc />
        public bool IsMountEnabled =>
            (_pollingManager?.AzimuthInfo?.Is_Drive_Enabled ?? false)
            && (_pollingManager?.AltitudeInfo?.Is_Drive_Enabled ?? false);

        /// <inheritdoc />
        public bool IsTracking => _siderealTracking?.IsTracking == true
                               || _satelliteTracking?.IsTracking == true;

        #endregion

        #region Constructor

        /// <summary>
        /// Create a new NARIT Mount Controller instance.
        /// Call <see cref="SetObserverSite"/> and <see cref="ConnectAsync"/> before use.
        /// </summary>
        public NaritMountController()
        {
            try
            {
                _preferredDriveSerialNumber = AppSettings.GetDriveSerialNumber();
                if (!string.IsNullOrWhiteSpace(_preferredDriveSerialNumber))
                {
                    AppSettings.UseSettingsProfileForDriveSerialNumber(_preferredDriveSerialNumber);
                    Debug.WriteLine($"[NARIT-API] Restored preferred drive serial from settings: {_preferredDriveSerialNumber}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Failed to restore preferred drive serial from settings: {ex.Message}");
            }

            try
            {
                _pointingModelPath = AppSettings.GetPointingModelFilePath();
                if (!string.IsNullOrWhiteSpace(_pointingModelPath))
                    Debug.WriteLine($"[NARIT-API] Restored pointing model path from settings: {_pointingModelPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Failed to restore pointing model path from settings: {ex.Message}");
            }

            try
            {
                var observer = AppSettings.GetObserverLocation();
                _observerSite = new ObserverSite(
                    observer.Latitude_Deg,
                    observer.Longitude_Deg,
                    observer.Altitude_Meters,
                    observer.UtcOffset_Hours);
                Debug.WriteLine($"[NARIT-API] Restored observer site from settings: Lat={_observerSite.Latitude:F4} Lon={_observerSite.Longitude:F4} Elev={_observerSite.Elevation:F0}m UTC{_observerSite.TimezoneUtcOffset:+0;-0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Failed to restore observer site from settings: {ex.Message}");
            }

            try
            {
                RestoreNtpConfigFromSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Failed to restore NTP settings: {ex.Message}");
            }

            Debug.WriteLine("[NARIT-API] NaritMountController created");
        }

        #endregion

        #region Connection

        /// <inheritdoc />
        public async Task<bool> ConnectAsync()
        {
            ThrowIfDisposed();

            try
            {
                // 1. Create PlatinumDrive (Elmo motor controller)
                _platinumDrive = new PlatinumDrive();
                
                // Scan for available drives and connect to the saved drive if possible.
                bool connected = await Task.Run(() =>
                {
                    var drives = PlatinumDrive.ScanAvailableDrives();
                    if (drives.Count == 0)
                        return false;

                    if (!string.IsNullOrWhiteSpace(_preferredDriveSerialNumber)
                        && _platinumDrive.ConnectBySerialNumber(_preferredDriveSerialNumber))
                    {
                        return true;
                    }

                    var firstDrive = drives.First();
                    return _platinumDrive.ConnectByComPort(firstDrive.Key);
                });
                
                if (!connected)
                {
                    RaiseError("Failed to connect to Platinum Drive", null, false);
                    return false;
                }

                _preferredDriveSerialNumber = _platinumDrive.SerialNumber;
                AppSettings.SaveDriveSerialNumber(_preferredDriveSerialNumber);
                AppSettings.UseSettingsProfileForDriveSerialNumber(_platinumDrive.SerialNumber);
                ReloadPersistedRuntimeSettingsForActiveProfile();

                // 2. Bootstrap the connected runtime through the shared Lib entry point.
                var runtime = MountRuntimeBootstrap.InitializeConnectedRuntime(
                    _platinumDrive,
                    _observerSite != null ? ToInternalObserverLocation() : null,
                    _pointingModelPath);

                _pointingModelManager = runtime.PointingModelManager;
                _pollingManager = runtime.PollingManager;

                // 4. Create axis controllers
                _azimuthAxisController = new AxisController(_pollingManager, AxisType.Azimuth, () => _currentTrackingMode != TrackingModeType.None);
                _altitudeAxisController = new AxisController(_pollingManager, AxisType.Altitude, () => _currentTrackingMode != TrackingModeType.None);

                // 5. Create tracking engines
                _siderealTracking = new SiderealTracking(_pollingManager);
                _satelliteTracking = new SatelliteTracking(
                    _pollingManager.AzimuthInfo,
                    _pollingManager.AltitudeInfo,
                    _pollingManager);

                // 6. Subscribe to polling events for position updates
                _pollingManager.AzimuthDataUpdated += OnAzimuthDataUpdated;

                // 7. Start polling (also starts MountManager internally)
                _pollingManager.StartPolling();

                _isConnected = true;
                Debug.WriteLine("[NARIT-API] Mount connected successfully");
                return true;
            }
            catch (Exception ex)
            {
                RaiseError($"Connection failed: {ex.Message}", ex, false);
                return false;
            }
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            ThrowIfDisposed();

            try
            {
                StopTracking();

                _pollingManager?.StopPolling();

                if (_pollingManager != null)
                    _pollingManager.AzimuthDataUpdated -= OnAzimuthDataUpdated;

                _siderealTracking?.Dispose();
                _satelliteTracking?.Dispose();
                _platinumDrive?.Disconnect();

                // [CRITICAL FIX] Clear App global singleton to prevent stale references
                if (ForkMount_WPF.App.PollingManager == _pollingManager)
                    ForkMount_WPF.App.PollingManager = null;
                
                if (ForkMount_WPF.App.PointingModelManager == _pointingModelManager)
                    ForkMount_WPF.App.PointingModelManager = null;

                _isConnected = false;
                Debug.WriteLine("[NARIT-API] Mount disconnected");
            }
            catch (Exception ex)
            {
                RaiseError($"Disconnect error: {ex.Message}", ex, true);
            }
        }

        #endregion

        #region Combined Mount Control

        /// <inheritdoc />
        public void Enable()
        {
            ThrowIfNotConnected();
            _azimuthAxisController.Enable();
            _altitudeAxisController.Enable();
            Debug.WriteLine("[NARIT-API] Both axes Enable command sent");
        }

        /// <inheritdoc />
        public void Disable()
        {
            ThrowIfNotConnected();
            StopTracking();
            _azimuthAxisController.Disable();
            _altitudeAxisController.Disable();
            Debug.WriteLine("[NARIT-API] Both axes Disable command sent");
        }

        /// <inheritdoc />
        public void GotoPosition(double azimuthDeg, double altitudeDeg, double velocityDegPerSec)
        {
            ThrowIfNotConnected();
            StopTrackingInternal();
            _azimuthAxisController.GotoPosition(azimuthDeg, velocityDegPerSec);
            _altitudeAxisController.GotoPosition(altitudeDeg, velocityDegPerSec);
            Debug.WriteLine($"[NARIT-API] GotoPosition: Az={azimuthDeg:F4} deg Alt={altitudeDeg:F4} deg @ {velocityDegPerSec:F2} deg/s");
        }

        /// <inheritdoc />
        public void MoveVelocity(double azVelocityDegPerSec, double altVelocityDegPerSec)
        {
            ThrowIfNotConnected();
            StopTrackingInternal();
            _azimuthAxisController.MoveVelocity(azVelocityDegPerSec);
            _altitudeAxisController.MoveVelocity(altVelocityDegPerSec);
        }

        /// <inheritdoc />
        public void Jog(double azVelocityDegPerSec, double altVelocityDegPerSec)
        {
            ThrowIfNotConnected();
            StopTrackingInternal();
            _azimuthAxisController.Jog(azVelocityDegPerSec);
            _altitudeAxisController.Jog(altVelocityDegPerSec);
            Debug.WriteLine($"[NARIT-API] Jog: Az={azVelocityDegPerSec:F4} deg/s, Alt={altVelocityDegPerSec:F4} deg/s");
        }

        /// <inheritdoc />
        public void Stop()
        {
            ThrowIfNotConnected();
            StopTrackingInternal();
            _azimuthAxisController.Stop();
            _altitudeAxisController.Stop();
            Debug.WriteLine("[NARIT-API] Both axes Stop command sent");
        }

        /// <inheritdoc />
        public void ApplyOffset(OffsetType offsetType, double stepSizeArcsec, OffsetDirection direction)
        {
            ThrowIfNotConnected();

            if (stepSizeArcsec <= 0)
                throw new ArgumentOutOfRangeException(nameof(stepSizeArcsec), "Offset step size must be greater than zero.");

            var mountInfo = _pollingManager?.MountInfo
                ?? throw new InvalidOperationException("Mount offset state is not available.");

            double deltaAzArcsec = 0;
            double deltaAltArcsec = 0;
            double diagonalArcsec = stepSizeArcsec / Math.Sqrt(2.0);

            switch (direction)
            {
                case OffsetDirection.N:
                    deltaAltArcsec = stepSizeArcsec;
                    break;
                case OffsetDirection.S:
                    deltaAltArcsec = -stepSizeArcsec;
                    break;
                case OffsetDirection.E:
                    deltaAzArcsec = stepSizeArcsec;
                    break;
                case OffsetDirection.W:
                    deltaAzArcsec = -stepSizeArcsec;
                    break;
                case OffsetDirection.NE:
                    deltaAzArcsec = diagonalArcsec;
                    deltaAltArcsec = diagonalArcsec;
                    break;
                case OffsetDirection.NW:
                    deltaAzArcsec = -diagonalArcsec;
                    deltaAltArcsec = diagonalArcsec;
                    break;
                case OffsetDirection.SE:
                    deltaAzArcsec = diagonalArcsec;
                    deltaAltArcsec = -diagonalArcsec;
                    break;
                case OffsetDirection.SW:
                    deltaAzArcsec = -diagonalArcsec;
                    deltaAltArcsec = -diagonalArcsec;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported offset direction.");
            }

            mountInfo.TelescopeOffset_Az_Arcsec += deltaAzArcsec;
            mountInfo.TelescopeOffset_Alt_Arcsec += deltaAltArcsec;

            Debug.WriteLine($"[NARIT-API] ApplyOffset: Type={offsetType}, Direction={direction}, Step={stepSizeArcsec:F2} arcsec, dAz={deltaAzArcsec:F2} arcsec, dAlt={deltaAltArcsec:F2} arcsec");
        }

        /// <inheritdoc />
        public void ResetOffset()
        {
            ThrowIfNotConnected();

            var mountInfo = _pollingManager?.MountInfo
                ?? throw new InvalidOperationException("Mount offset state is not available.");

            mountInfo.TelescopeOffset_Az_Arcsec = 0;
            mountInfo.TelescopeOffset_Alt_Arcsec = 0;

            Debug.WriteLine("[NARIT-API] ResetOffset: telescope offset set to 0 arcsec on both axes");
        }

        #endregion

        #region Tracking

        /// <inheritdoc />
        public void TrackSidereal()
        {
            ThrowIfNotConnected();
            ThrowIfNoObserverSite();

            StopTrackingInternal();

            // Sidereal tracking requires a target; use current position as target.
            var mountInfo = _pollingManager.MountInfo;
            double currentAz = mountInfo?.Act_Azimuth_Pos_Correct ?? mountInfo?.Act_Azimuth_Pos_Raw ?? 0;
            double currentAlt = mountInfo?.Act_Altitude_Pos_Correct ?? mountInfo?.Act_Altitude_Pos_Raw ?? 0;

            var observer = ToInternalObserverLocation();

            // Convert current Az/Alt to RA/Dec for sidereal tracking target
            DateTime utcNow = NtpTimeService.Instance.GetCurrentUtcTime();
            double jd = AstroTime.JulianDay(utcNow);
            var site = new LatLon(
                Angle.FromDegs(_observerSite.Latitude),
                Angle.FromDegs(_observerSite.Longitude));
            var altAz = new AltAz(
                Angle.FromDegs(currentAlt),
                Angle.FromDegs(currentAz));
            var raDec = altAz.ToRaDec(site, jd);

            _siderealTracking.SetTarget(raDec.Ra.Hours, raDec.Dec.Degs, observer, isApparent: true);
            _siderealTracking.StartTracking();

            SetTrackingMode(TrackingModeType.Sidereal, "Sidereal");
            Debug.WriteLine("[NARIT-API] Sidereal tracking started (current position)");
        }

        /// <inheritdoc />
        public void TrackObject(double raHours, double decDeg, string objectName = "")
        {
            ThrowIfNotConnected();
            ThrowIfNoObserverSite();

            if (raHours < 0 || raHours >= 24)
                throw new ArgumentOutOfRangeException(nameof(raHours), "RA must be 0-24 hours");
            if (decDeg < -90 || decDeg > 90)
                throw new ArgumentOutOfRangeException(nameof(decDeg), "Dec must be within -90 to +90 deg");

            var observer = ToInternalObserverLocation();
            string resolvedName = string.IsNullOrWhiteSpace(objectName) ? "Celestial Object" : objectName;
            var target = new TrackingTarget
            {
                ObjectType = TrackingObjectType.DeepSky,
                ObjectName = resolvedName,
                RightAscension = raHours,
                Declination = decDeg
            };

            bool granted = TrackingManager.Instance.RequestStartTracking(
                TrackingObjectType.DeepSky,
                target,
                () => StopTrackingCore(notifyTrackingManager: false));

            if (!granted)
                throw new InvalidOperationException("TrackingManager rejected the TrackObject request.");

            _siderealTracking.SetTarget(raHours, decDeg, observer, isApparent: false);
            _siderealTracking.StartTracking();
            _currentTrackingObjectType = TrackingObjectType.DeepSky;
            SetTrackingMode(TrackingModeType.Sidereal, resolvedName);

            Debug.WriteLine($"[NARIT-API] TrackObject started: RA={raHours:F4}h Dec={decDeg:F4} deg \"{resolvedName}\"");
        }

        /// <inheritdoc />
        public void TrackObject(string raJ2000, string decJ2000, string objectName = "")
        {
            double raHours = ParseRaJ2000Hours(raJ2000);
            double decDeg = ParseDecJ2000Degrees(decJ2000);
            TrackObject(raHours, decDeg, objectName);
        }

        /// <inheritdoc />
        public void TrackSatellite(string tleLine1, string tleLine2, string satelliteName = "")
        {
            ThrowIfNotConnected();
            ThrowIfNoObserverSite();

            if (string.IsNullOrWhiteSpace(tleLine1))
                throw new ArgumentNullException(nameof(tleLine1));
            if (string.IsNullOrWhiteSpace(tleLine2))
                throw new ArgumentNullException(nameof(tleLine2));

            StopTrackingInternal();

            var observer = ToInternalObserverLocation();
            var tle = new SatelliteTLE(satelliteName, tleLine1, tleLine2);
            var tracker = new SatelliteTracker(tle);

            _satelliteTracking.SetTarget(tracker, observer);
            _satelliteTracking.StartTracking();

            SetTrackingMode(TrackingModeType.Satellite, satelliteName);
            Debug.WriteLine($"[NARIT-API] TrackSatellite: \"{satelliteName}\"");
        }

        /// <inheritdoc />
        public void TrackNearEarthObject(Models.OrbitalElements elements)
        {
            ArgumentNullException.ThrowIfNull(elements);

            TrackAsteroid(new AsteroidOrbitalElements
            {
                Name = elements.Name,
                Epoch_JD = elements.Epoch_JD,
                SemiMajorAxis_AU = elements.SemiMajorAxis_AU,
                Eccentricity = elements.Eccentricity,
                Inclination_Deg = elements.Inclination_Deg,
                LongAscendingNode_Deg = elements.LongAscendingNode_Deg,
                ArgPerihelion_Deg = elements.ArgPerihelion_Deg,
                MeanAnomaly_Deg = elements.MeanAnomaly_Deg
            });
        }

        /// <inheritdoc />
        public void StopTracking()
        {
            StopTrackingInternal();

            if (_isConnected)
            {
                _pollingManager?.SetAzimuthCommandVelocity(0.0);
                _pollingManager?.SetAltitudeCommandVelocity(0.0);
            }

            Debug.WriteLine("[NARIT-API] Tracking stopped");
        }

        /// <inheritdoc />
        public TrackingInfo GetTrackingInfo()
        {
            ThrowIfNotConnected();

            var info = new TrackingInfo
            {
                Mode = _currentTrackingMode,
                IsOnTarget = false,
                TargetRA_Hours = _pollingManager.MountInfo?.CommandRa_Hours ?? 0,
                TargetDec_Deg = _pollingManager.MountInfo?.CommandDec_Deg ?? 0,
                TargetRaDecIsApparent = _pollingManager.MountInfo?.CommandRaDecIsApparent ?? false,
                JulianDay = _pollingManager.MountInfo?.JulianDay ?? 0,
                HasActualRaDecJ2000 = _pollingManager.MountInfo?.HasActualRaDecJ2000 ?? false,
                ActualRaJ2000_Hours = _pollingManager.MountInfo?.ActualRaJ2000_Hours ?? 0,
                ActualDecJ2000_Deg = _pollingManager.MountInfo?.ActualDecJ2000_Deg ?? 0
            };

            if (_siderealTracking?.IsTracking == true)
            {
                var status = _siderealTracking.GetStatus();
                if (!(_pollingManager.MountInfo?.HasCommandRaDec ?? false))
                {
                    info.TargetRA_Hours = status.TargetRA;
                    info.TargetDec_Deg = status.TargetDec;
                }
                info.TargetAzimuth_Deg = status.CurrentAzimuth;
                info.TargetAltitude_Deg = status.CurrentAltitude;
                info.IsOnTarget = _siderealTracking.IsOnTarget;
                info.Duration = status.TrackingDuration;
                info.LocalSiderealTime_Hours = status.LocalSiderealTime;
            }

            info.AzimuthRms_Arcsec = _pollingManager.AzimuthInfo?.TrackingRmsError_Arcsec ?? 0;
            info.AltitudeRms_Arcsec = _pollingManager.AltitudeInfo?.TrackingRmsError_Arcsec ?? 0;
            info.ObjectName = _pollingManager.MountInfo?.CurrentTrackingObjectName ?? "";

            return info;
        }

        #endregion

        #region Status & Data

        /// <inheritdoc />
        public MountStatus GetStatus()
        {
            ThrowIfNotConnected();

            var mountInfo = _pollingManager.MountInfo;

            return new MountStatus
            {
                State = DetermineState(),
                IsMountEnabled = IsMountEnabled,
                IsTracking = IsTracking,
                IsTimeSynchronized = NtpTimeService.Instance.IsSynchronized,
                NtpOffsetMs = NtpTimeService.Instance.NtpOffset.TotalMilliseconds,
                SystemUtcTime = NtpTimeService.Instance.GetCurrentUtcTime(),
                JulianDay = mountInfo?.JulianDay ?? 0,
                CurrentTrackingObjectName = mountInfo?.CurrentTrackingObjectName ?? "",
                HasCommandRaDec = mountInfo?.HasCommandRaDec ?? false,
                CommandRa_Hours = mountInfo?.CommandRa_Hours ?? 0,
                CommandDec_Deg = mountInfo?.CommandDec_Deg ?? 0,
                CommandRaDecIsApparent = mountInfo?.CommandRaDecIsApparent ?? false,
                HasActualRaDecJ2000 = mountInfo?.HasActualRaDecJ2000 ?? false,
                ActualRaJ2000_Hours = mountInfo?.ActualRaJ2000_Hours ?? 0,
                ActualDecJ2000_Deg = mountInfo?.ActualDecJ2000_Deg ?? 0,
                CurrentTrackingMode = _currentTrackingMode,
                AzimuthAxis = _azimuthAxisController.GetState(),
                AltitudeAxis = _altitudeAxisController.GetState(),
                Position = GetPosition(),
                TelescopeOffset_Az_Arcsec = mountInfo?.TelescopeOffset_Az_Arcsec ?? 0,
                TelescopeOffset_Alt_Arcsec = mountInfo?.TelescopeOffset_Alt_Arcsec ?? 0
            };
        }

        /// <inheritdoc />
        public MountPosition GetPosition()
        {
            ThrowIfNotConnected();

            var mountInfo = _pollingManager.MountInfo;

            return new MountPosition
            {
                Azimuth_Raw_Deg = mountInfo?.Act_Azimuth_Pos_Raw ?? 0,
                Altitude_Raw_Deg = mountInfo?.Act_Altitude_Pos_Raw ?? 0,
                Azimuth_Corrected_Deg = mountInfo?.Act_Azimuth_Pos_Correct ?? 0,
                Altitude_Corrected_Deg = mountInfo?.Act_Altitude_Pos_Correct ?? 0,
                Timestamp = NtpTimeService.Instance.GetCurrentTime()
            };
        }

        #endregion

        #region Configuration

        /// <inheritdoc />
        public void SetObserverSite(ObserverSite site)
        {
            _observerSite = site ?? throw new ArgumentNullException(nameof(site));

            AppSettings.SaveObserverLocation(new ObserverLocationSettings
            {
                Latitude_Deg = site.Latitude,
                Longitude_Deg = site.Longitude,
                Altitude_Meters = site.Elevation,
                UtcOffset_Hours = site.TimezoneUtcOffset
            });

            _pointingModelManager?.UpdateObserverLocation(site.Latitude, site.Longitude);

            if (_isConnected && !string.IsNullOrWhiteSpace(_pointingModelPath) && _pointingModelManager != null)
            {
                LoadPointingModel(_pointingModelPath);
            }

            Debug.WriteLine($"[NARIT-API] Observer site set: Lat={site.Latitude:F4} deg Lon={site.Longitude:F4} deg Elev={site.Elevation:F0}m UTC{site.TimezoneUtcOffset:+0;-0}");
        }

        /// <inheritdoc />
        public ObserverSite GetObserverSite()
        {
            return _observerSite;
        }

        /// <inheritdoc />
        public void SetPointingModelPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));

            string fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Pointing model file not found: {fullPath}", fullPath);

            _pointingModelPath = fullPath;
            AppSettings.SavePointingModelFilePath(fullPath);

            if (_isConnected && _pointingModelManager != null)
            {
                LoadPointingModel(fullPath);
            }

            Debug.WriteLine($"[NARIT-API] Pointing model path set and saved: {fullPath}");
        }

        /// <inheritdoc />
        public string GetPointingModelPath()
        {
            return _pointingModelPath ?? string.Empty;
        }

        /// <inheritdoc />
        public void SetNtpConfig(NtpConfig config)
        {
            _ntpConfig = config ?? throw new ArgumentNullException(nameof(config));

            var systemSettings = AppSettings.GetSystemSettings();
            systemSettings.NtpServerAddress = config.ServerAddress;
            systemSettings.NtpSyncIntervalMinutes = Math.Max(1, config.SyncIntervalSeconds / 60);
            systemSettings.EnableNtpSync = true;
            systemSettings.AutoSyncEnabled = true;
            AppSettings.SaveSystemSettings(systemSettings);

            ApplyNtpSettings(systemSettings);
            Debug.WriteLine($"[NARIT-API] NTP configured: server={config.ServerAddress} interval={config.SyncIntervalSeconds}s");
        }

        #endregion

        #region Dispose

        /// <inheritdoc />
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (_isConnected)
                    Disconnect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Dispose error: {ex.Message}");
            }

            Debug.WriteLine("[NARIT-API] NaritMountController disposed");
        }

        #endregion

        #region Private Helpers

        private ObserverLocation ToInternalObserverLocation()
        {
            return new ObserverLocation(
                _observerSite.Latitude,
                _observerSite.Longitude,
                _observerSite.Elevation);
        }

        private void StopTrackingInternal()
        {
            StopTrackingCore(notifyTrackingManager: true);
        }

        private void ReloadPersistedRuntimeSettingsForActiveProfile()
        {
            try
            {
                _pointingModelPath = AppSettings.GetPointingModelFilePath();
                if (!string.IsNullOrWhiteSpace(_pointingModelPath))
                    Debug.WriteLine($"[NARIT-API] Active-profile pointing model path: {_pointingModelPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Failed to reload pointing model path from active profile: {ex.Message}");
            }

            try
            {
                var observer = AppSettings.GetObserverLocation();
                _observerSite = new ObserverSite(
                    observer.Latitude_Deg,
                    observer.Longitude_Deg,
                    observer.Altitude_Meters,
                    observer.UtcOffset_Hours);
                Debug.WriteLine($"[NARIT-API] Active-profile observer site restored: Lat={_observerSite.Latitude:F4} Lon={_observerSite.Longitude:F4} Elev={_observerSite.Elevation:F0}m UTC{_observerSite.TimezoneUtcOffset:+0;-0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Failed to reload observer site from active profile: {ex.Message}");
            }

            RestoreNtpConfigFromSettings();
        }

        private void RestoreNtpConfigFromSettings()
        {
            var systemSettings = AppSettings.GetSystemSettings();
            _ntpConfig = new NtpConfig(
                systemSettings.NtpServerAddress ?? "pool.ntp.org",
                Math.Max(1, systemSettings.NtpSyncIntervalMinutes) * 60);

            ApplyNtpSettings(systemSettings);
            Debug.WriteLine($"[NARIT-API] Restored NTP settings from profile: server={_ntpConfig.ServerAddress} interval={_ntpConfig.SyncIntervalSeconds}s autoSync={systemSettings.AutoSyncEnabled} enabled={systemSettings.EnableNtpSync}");
        }

        private static void ApplyNtpSettings(SystemSettings systemSettings)
        {
            var ntpService = NtpTimeService.Instance;
            ntpService.NtpServer = systemSettings.NtpServerAddress ?? "pool.ntp.org";
            ntpService.SyncIntervalMinutes = Math.Max(1, systemSettings.NtpSyncIntervalMinutes);

            if (systemSettings.EnableNtpSync && systemSettings.AutoSyncEnabled)
            {
                ntpService.StartAutoSync();
            }
            else
            {
                ntpService.StopAutoSync();
            }
        }

        private void StopTrackingCore(bool notifyTrackingManager)
        {
            CancelTrackingAcquisition();
            StopSmallBodyTrackingInternal();

            var previousMode = _currentTrackingMode;
            if (notifyTrackingManager)
            {
                TrackingManager.Instance.StopTracking(
                    _currentTrackingObjectType == TrackingObjectType.None
                        ? TrackingObjectType.None
                        : _currentTrackingObjectType);
            }

            if (_siderealTracking?.IsTracking == true)
                _siderealTracking.StopTracking();

            if (_satelliteTracking?.IsTracking == true)
                _satelliteTracking.StopTracking();

            _currentTrackingObjectType = TrackingObjectType.None;

            if (previousMode != TrackingModeType.None)
                SetTrackingMode(TrackingModeType.None, "");
        }

        private void StartCelestialAcquisitionAndTracking(
            double raHours,
            double decDeg,
            ObserverLocation observer,
            bool isApparent,
            string objectName)
        {
            CancelTrackingAcquisition();
            SetTrackingMode(TrackingModeType.Sidereal, objectName);

            _trackingAcquisitionCts = new CancellationTokenSource();
            var cancellationToken = _trackingAcquisitionCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    var targetAltAz = CalculateTargetAltAz(raHours, decDeg, isApparent);
                    Debug.WriteLine($"[NARIT-API] Acquisition target: Az={targetAltAz.azimuthDeg:F4} deg Alt={targetAltAz.altitudeDeg:F4} deg \"{objectName}\"");

                    if (!IsMountNearTarget(targetAltAz.azimuthDeg, targetAltAz.altitudeDeg, TrackingAcquisitionToleranceDeg))
                    {
                        _pollingManager?.SetAzimuthCommandVelocity(0.0);
                        _pollingManager?.SetAltitudeCommandVelocity(0.0);

                        _azimuthAxisController.GotoPosition(targetAltAz.azimuthDeg, DefaultAzimuthSlewVelocityDegPerSec);
                        _altitudeAxisController.GotoPosition(targetAltAz.altitudeDeg, DefaultAltitudeSlewVelocityDegPerSec);

                        int timeoutMs = EstimateTrackingAcquisitionTimeoutMs(targetAltAz.azimuthDeg, targetAltAz.altitudeDeg);
                        bool acquired = await WaitForMountToReachTargetAsync(
                            targetAltAz.azimuthDeg,
                            targetAltAz.altitudeDeg,
                            timeoutMs,
                            cancellationToken);

                        if (!acquired)
                        {
                            Debug.WriteLine($"[NARIT-API] Acquisition incomplete for \"{objectName}\" - handing off to sidereal tracking from current position");
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Clear any residual open-loop slew commands before closed-loop sidereal tracking takes over.
                    _pollingManager?.SetAzimuthCommandVelocity(0.0);
                    _pollingManager?.SetAltitudeCommandVelocity(0.0);
                    await Task.Delay(TrackingAcquisitionSettleDelayMs, cancellationToken);

                    _siderealTracking.SetTarget(raHours, decDeg, observer, isApparent);
                    _siderealTracking.StartTracking();

                    SetTrackingMode(TrackingModeType.Sidereal, objectName);
                    Debug.WriteLine($"[NARIT-API] Celestial tracking started after acquisition: \"{objectName}\"");
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"[NARIT-API] Acquisition cancelled for \"{objectName}\"");
                }
                catch (Exception ex)
                {
                    SetTrackingMode(TrackingModeType.None, "");
                    RaiseError($"TrackObject acquisition failed: {ex.Message}", ex, true);
                }
            }, cancellationToken);
        }

        private (double azimuthDeg, double altitudeDeg) CalculateTargetAltAz(double raHours, double decDeg, bool isApparent)
        {
            DateTime utcNow = NtpTimeService.Instance.GetCurrentUtcTime();
            double jd = AstroTime.JulianDay(utcNow);

            var site = new LatLon(
                Angle.FromDegs(_observerSite.Latitude),
                Angle.FromDegs(_observerSite.Longitude));

            var raDec = RaDec.FromHoursDegs(raHours, decDeg);
            var raDecForAltAz = isApparent ? raDec : raDec.J2000ToNow();
            var altAz = raDecForAltAz.ToAltAz(site, jd);

            return (altAz.Az.Degs, altAz.Alt.Degs);
        }

        private async Task<bool> WaitForMountToReachTargetAsync(
            double targetAzimuthDeg,
            double targetAltitudeDeg,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();

            while (started.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsMountNearTarget(targetAzimuthDeg, targetAltitudeDeg, TrackingAcquisitionToleranceDeg))
                {
                    await Task.Delay(TrackingAcquisitionSettleDelayMs, cancellationToken);

                    if (IsMountNearTarget(targetAzimuthDeg, targetAltitudeDeg, TrackingAcquisitionToleranceDeg))
                        return true;
                }

                if (CanStartTrackingFromCurrentPosition(targetAzimuthDeg, targetAltitudeDeg))
                {
                    await Task.Delay(TrackingAcquisitionSettleDelayMs, cancellationToken);

                    if (CanStartTrackingFromCurrentPosition(targetAzimuthDeg, targetAltitudeDeg))
                        return false;
                }

                await Task.Delay(TrackingAcquisitionPollIntervalMs, cancellationToken);
            }

            return false;
        }

        private bool IsMountNearTarget(double targetAzimuthDeg, double targetAltitudeDeg, double toleranceDeg)
        {
            var position = GetPositionSnapshot();
            double azError = AngularDifferenceDeg(position.azimuthDeg, targetAzimuthDeg);
            double altError = Math.Abs(position.altitudeDeg - targetAltitudeDeg);
            return azError <= toleranceDeg && altError <= toleranceDeg;
        }

        private (double azimuthDeg, double altitudeDeg) GetPositionSnapshot()
        {
            var mountInfo = _pollingManager?.MountInfo;
            if (mountInfo == null)
                throw new InvalidOperationException("Mount position is not available.");

            return (mountInfo.Act_Azimuth_Pos_Correct, mountInfo.Act_Altitude_Pos_Correct);
        }

        private bool CanStartTrackingFromCurrentPosition(double targetAzimuthDeg, double targetAltitudeDeg)
        {
            var position = GetPositionSnapshot();
            double azError = AngularDifferenceDeg(position.azimuthDeg, targetAzimuthDeg);
            double altError = Math.Abs(position.altitudeDeg - targetAltitudeDeg);
            double azVelocity = Math.Abs(_pollingManager?.AzimuthInfo?.Command_Velocity_Deg_Sec ?? 0.0);
            double altVelocity = Math.Abs(_pollingManager?.AltitudeInfo?.Command_Velocity_Deg_Sec ?? 0.0);

            bool nearEnough = azError <= TrackingAcquisitionRelaxedToleranceDeg
                           && altError <= TrackingAcquisitionRelaxedToleranceDeg;
            bool motionSettled = azVelocity <= 0.01 && altVelocity <= 0.01;

            return nearEnough && motionSettled;
        }

        private int EstimateTrackingAcquisitionTimeoutMs(double targetAzimuthDeg, double targetAltitudeDeg)
        {
            var position = GetPositionSnapshot();
            double azimuthDelta = AngularDifferenceDeg(position.azimuthDeg, targetAzimuthDeg);
            double altitudeDelta = Math.Abs(position.altitudeDeg - targetAltitudeDeg);

            double estimatedMs = Math.Max(
                (azimuthDelta / DefaultAzimuthSlewVelocityDegPerSec) * 1000.0,
                (altitudeDelta / DefaultAltitudeSlewVelocityDegPerSec) * 1000.0) + 8000.0;

            return (int)Math.Max(10000.0, Math.Min(90000.0, estimatedMs));
        }

        private void CancelTrackingAcquisition()
        {
            if (_trackingAcquisitionCts == null)
                return;

            try
            {
                _trackingAcquisitionCts.Cancel();
            }
            finally
            {
                _trackingAcquisitionCts.Dispose();
                _trackingAcquisitionCts = null;
            }
        }

        private static double AngularDifferenceDeg(double firstDeg, double secondDeg)
        {
            double difference = Math.Abs(firstDeg - secondDeg) % 360.0;
            return difference > 180.0 ? 360.0 - difference : difference;
        }

        private void SetTrackingMode(TrackingModeType mode, string objectName)
        {
            var previousMode = _currentTrackingMode;
            _currentTrackingMode = mode;

            if (_pollingManager?.MountInfo != null)
                _pollingManager.MountInfo.CurrentTrackingObjectName = objectName;

            TrackingStateChanged?.Invoke(this, new ApiTrackingStateChangedEventArgs
            {
                IsTracking = mode != TrackingModeType.None,
                Mode = mode,
                PreviousMode = previousMode,
                ObjectName = objectName
            });
        }

        private MountState DetermineState()
        {
            if (!_isConnected) return MountState.Disconnected;
            if (!IsMountEnabled) return MountState.Disabled;
            if (IsTracking) return MountState.Tracking;

            // Check if any axis has non-zero command velocity ? Slewing
            var azVel = Math.Abs(_pollingManager?.AzimuthInfo?.Command_Velocity_Deg_Sec ?? 0);
            var altVel = Math.Abs(_pollingManager?.AltitudeInfo?.Command_Velocity_Deg_Sec ?? 0);
            if (azVel > 0.001 || altVel > 0.001) return MountState.Slewing;

            return MountState.Idle;
        }

        private void LoadPointingModel(string filePath)
        {
            try
            {
                if (_pointingModelManager == null)
                    throw new InvalidOperationException("Pointing model manager is not initialized.");

                bool loaded = _pointingModelManager.LoadModel(filePath);
                if (!loaded)
                {
                    throw new InvalidOperationException(
                        "Pointing model was rejected by PointingModelManager. Check mount type, PointXP initialization, or model file validity.");
                }

                Debug.WriteLine($"[NARIT-API] Pointing model loaded: {filePath}");
            }
            catch (Exception ex)
            {
                RaiseError($"Failed to load pointing model: {ex.Message}", ex, true);
                throw;
            }
        }

        private void OnAzimuthDataUpdated(object sender, AzimuthInfo e)
        {
            // Forward position updates to API consumers
            PositionChanged?.Invoke(this, new PositionChangedEventArgs
            {
                Position = GetPosition()
            });
        }

        private void RaiseError(string message, Exception ex, bool recoverable)
        {
            Debug.WriteLine($"[NARIT-API] Error: {message}");
            ErrorOccurred?.Invoke(this, new MountErrorEventArgs
            {
                Message = message,
                Exception = ex,
                IsRecoverable = recoverable
            });
        }

        private static double ParseRaJ2000Hours(string raJ2000)
        {
            if (string.IsNullOrWhiteSpace(raJ2000))
                throw new ArgumentNullException(nameof(raJ2000));

            string input = raJ2000.Trim();
            if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double raHours))
                return ValidateRaHours(raHours, nameof(raJ2000));

            if (TryParseHmsValue(input, ':', out raHours) || TryParseHmsValue(input, ' ', out raHours))
                return ValidateRaHours(raHours, nameof(raJ2000));

            throw new FormatException("RA must be numeric or use HH:MM:SS.XXX / HH MM SS.XXX format.");
        }

        private static double ParseDecJ2000Degrees(string decJ2000)
        {
            if (string.IsNullOrWhiteSpace(decJ2000))
                throw new ArgumentNullException(nameof(decJ2000));

            string input = decJ2000.Trim();
            if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double decDegrees))
                return ValidateDecDegrees(decDegrees, nameof(decJ2000));

            bool isNegative = input.StartsWith("-", StringComparison.Ordinal);
            string absInput = input.TrimStart('+', '-');

            if (TryParseHmsValue(absInput, ':', out double absDegrees) || TryParseHmsValue(absInput, ' ', out absDegrees))
            {
                decDegrees = isNegative ? -absDegrees : absDegrees;
                return ValidateDecDegrees(decDegrees, nameof(decJ2000));
            }

            throw new FormatException("Dec must be numeric or use [+-]DD:MM:SS.XXX / [+-]DD MM SS.XXX format.");
        }

        private static bool TryParseHmsValue(string input, char separator, out double result)
        {
            result = 0;

            string[] parts = input.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return false;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
            {
                return false;
            }

            result = a + (b / 60.0) + (c / 3600.0);
            return true;
        }

        private static double ValidateRaHours(double raHours, string paramName)
        {
            if (raHours < 0 || raHours >= 24)
                throw new ArgumentOutOfRangeException(paramName, "RA must be between 0 and 24 hours.");

            return raHours;
        }

        private static double ValidateDecDegrees(double decDegrees, string paramName)
        {
            if (decDegrees < -90 || decDegrees > 90)
                throw new ArgumentOutOfRangeException(paramName, "Dec must be between -90 and +90 degrees.");

            return decDegrees;
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(NaritMountController));
        }

        private void ThrowIfNotConnected()
        {
            ThrowIfDisposed();
            if (!_isConnected)
                throw new InvalidOperationException("Mount is not connected. Call ConnectAsync() first.");
        }

        private void ThrowIfNoObserverSite()
        {
            if (_observerSite == null)
                throw new InvalidOperationException(
                    "Observer site is not configured. Call SetObserverSite() first.");
        }

        #endregion
    }
}
