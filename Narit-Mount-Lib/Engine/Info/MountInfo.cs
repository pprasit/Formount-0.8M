using System;
using ForkMount_WPF.Engine.Utility;
using PWLib.Astro;

namespace ForkMount_WPF.Engine.Info
{
    /// <summary>
    /// Mount Information Hub - Centralized status for both Azimuth and Altitude axes
    /// Combines status from both axes to provide overall mount state
    /// Similar to AzimuthInfo/AltitudeInfo but for the complete mount system
    /// 
    /// IMPORTANT: For time-critical operations (tracking, astronomical calculations),
    /// use the MountSystemTime properties which are synchronized via NTP.
    /// </summary>
    public class MountInfo
    {
        #region Fields
        
        private readonly object _lock = new object();
        private bool _isMountEnabled;
        private DateTime _isMountEnabledTimestamp;
        private bool _isTracking;
        private DateTime _isTrackingTimestamp;
        
        // [NEW] Actual Mount Position - Raw (from encoders)
        private double _actAzimuthPosRaw;
        private DateTime _actAzimuthPosRawTimestamp;
        private double _actAltitudePosRaw;
        private DateTime _actAltitudePosRawTimestamp;
        
        // [NEW] Actual Mount Position - Corrected (with pointing model)
        private double _actAzimuthPosCorrect;
        private DateTime _actAzimuthPosCorrectTimestamp;
        private double _actAltitudePosCorrect;
        private DateTime _actAltitudePosCorrectTimestamp;

        private bool _hasActualRaDecJ2000;
        private double _actualRaJ2000Hours;
        private double _actualDecJ2000Deg;
        private double _julianDay;
        private DateTime _astronomicalCoordinatesTimestamp;

        private bool _hasCommandRaDec;
        private double _commandRaHours;
        private double _commandDecDeg;
        private bool _commandRaDecIsApparent;
        private DateTime _commandRaDecTimestamp;
        
        // Telescope Offset (from CameraOffsetWindow, in arcseconds)
        private double _telescopeOffsetAz_Arcsec;
        private double _telescopeOffsetAlt_Arcsec;
        private DateTime _telescopeOffsetTimestamp;
        
        // Current tracking object name (set by TrackingManager)
        private string _currentTrackingObjectName = "";
        
        #endregion
        
        #region System Time Properties (NTP Synchronized)
        
        /// <summary>
        /// Get current UTC time synchronized with NTP server.
        /// This is the PRIMARY time reference for all astronomical calculations.
        /// Use this instead of DateTime.UtcNow for accurate tracking.
        /// </summary>
        public DateTime SystemUtcTime => NtpTimeService.Instance.GetCurrentUtcTime();
        
        /// <summary>
        /// Get current local time synchronized with NTP server.
        /// Use this instead of DateTime.Now for UI display.
        /// </summary>
        public DateTime SystemLocalTime => NtpTimeService.Instance.GetCurrentTime();
        
        /// <summary>
        /// Get current time at specified UTC offset.
        /// Useful for displaying time in observer's timezone.
        /// </summary>
        /// <param name="utcOffsetHours">UTC offset in hours (e.g., +7 for Thailand)</param>
        public DateTime GetSystemTimeAtOffset(double utcOffsetHours)
        {
            return NtpTimeService.Instance.GetTimeAtOffset(utcOffsetHours);
        }
        
        /// <summary>
        /// Whether the system time has been synchronized with NTP server.
        /// If false, SystemUtcTime falls back to DateTime.UtcNow.
        /// </summary>
        public bool IsTimeSynchronized => NtpTimeService.Instance.IsSynchronized;
        
        /// <summary>
        /// NTP time offset from local system clock (in milliseconds).
        /// Positive = NTP ahead of local, Negative = NTP behind local.
        /// </summary>
        public double NtpOffsetMs => NtpTimeService.Instance.NtpOffset.TotalMilliseconds;
        
        /// <summary>
        /// Last NTP synchronization time (local time).
        /// </summary>
        public DateTime LastNtpSyncTime => NtpTimeService.Instance.LastSyncTime;
        
        #endregion

        #region Properties

        /// <summary>
        /// Mount Enabled Status - true if BOTH axes are enabled
        /// Updated from: AzimuthInfo.Is_Drive_Enabled AND AltitudeInfo.Is_Drive_Enabled
        /// </summary>
        public bool IsMountEnabled
        {
            get { lock (_lock) return _isMountEnabled; }
            set
            {
                lock (_lock)
                {
                    _isMountEnabled = value;
                    _isMountEnabledTimestamp = SystemLocalTime; // Use NTP-synced time
                }
            }
        }

        public DateTime IsMountEnabledTimestamp
        {
            get { lock (_lock) return _isMountEnabledTimestamp; }
        }

        /// <summary>
        /// Tracking Status - true if BOTH axes RMS error is within tracking sensitivity threshold
        /// Updated from: AzimuthInfo.TrackingRmsError_Arcsec AND AltitudeInfo.TrackingRmsError_Arcsec
        /// Threshold: AzimuthAxisConfig.Tracking_Sensitivity_Deg AND AltitudeAxisConfig.Tracking_Sensitivity_Deg
        /// </summary>
        public bool IsTracking
        {
            get { lock (_lock) return _isTracking; }
            set
            {
                lock (_lock)
                {
                    _isTracking = value;
                    _isTrackingTimestamp = SystemLocalTime; // Use NTP-synced time
                }
            }
        }

        public DateTime IsTrackingTimestamp
        {
            get { lock (_lock) return _isTrackingTimestamp; }
        }

        /// <summary>
        /// Actual Azimuth Position - Raw (from encoder, in degrees)
        /// Calculated from: AzimuthInfo.Raw_Position / Encoder_Resolution * 360.0 + Position_Offset_Deg
        /// This is the ACTUAL measured position WITHOUT pointing model correction
        /// </summary>
        public double Act_Azimuth_Pos_Raw
        {
            get { lock (_lock) return _actAzimuthPosRaw; }
            set
            {
                lock (_lock)
                {
                    _actAzimuthPosRaw = value;
                    _actAzimuthPosRawTimestamp = SystemLocalTime; // Use NTP-synced time
                }
            }
        }

        public DateTime Act_Azimuth_Pos_Raw_Timestamp
        {
            get { lock (_lock) return _actAzimuthPosRawTimestamp; }
        }

        /// <summary>
        /// Actual Altitude Position - Raw (from encoder, in degrees)
        /// Calculated from: AltitudeInfo.Raw_Position / Encoder_Resolution * 360.0 + Position_Offset_Deg
        /// This is the ACTUAL measured position WITHOUT pointing model correction
        /// </summary>
        public double Act_Altitude_Pos_Raw
        {
            get { lock (_lock) return _actAltitudePosRaw; }
            set
            {
                lock (_lock)
                {
                    _actAltitudePosRaw = value;
                    _actAltitudePosRawTimestamp = SystemLocalTime; // Use NTP-synced time
                }
            }
        }

        public DateTime Act_Altitude_Pos_Raw_Timestamp
        {
            get { lock (_lock) return _actAltitudePosRawTimestamp; }
        }

        /// <summary>
        /// Actual Azimuth Position - Corrected (with pointing model, in degrees)
        /// This is Act_Azimuth_Pos_Raw MINUS pointing model error correction
        /// This represents the TRUE sky position that the mount is pointing at
        /// Use this for coordinate display and calculations
        /// </summary>
        public double Act_Azimuth_Pos_Correct
        {
            get { lock (_lock) return _actAzimuthPosCorrect; }
            set
            {
                lock (_lock)
                {
                    _actAzimuthPosCorrect = value;
                    _actAzimuthPosCorrectTimestamp = SystemLocalTime; // Use NTP-synced time
                }
            }
        }

        public DateTime Act_Azimuth_Pos_Correct_Timestamp
        {
            get { lock (_lock) return _actAzimuthPosCorrectTimestamp; }
        }

        /// <summary>
        /// Actual Altitude Position - Corrected (with pointing model, in degrees)
        /// This is Act_Altitude_Pos_Raw MINUS pointing model error correction
        /// This represents the TRUE sky position that the mount is pointing at
        /// Use this for coordinate display and calculations
        /// </summary>
        public double Act_Altitude_Pos_Correct
        {
            get { lock (_lock) return _actAltitudePosCorrect; }
            set
            {
                lock (_lock)
                {
                    _actAltitudePosCorrect = value;
                    _actAltitudePosCorrectTimestamp = SystemLocalTime; // Use NTP-synced time
                }
            }
        }

        public DateTime Act_Altitude_Pos_Correct_Timestamp
        {
            get { lock (_lock) return _actAltitudePosCorrectTimestamp; }
        }

        /// <summary>
        /// Whether Actual RA/Dec J2000 has been derived from the corrected mount position.
        /// </summary>
        public bool HasActualRaDecJ2000
        {
            get { lock (_lock) return _hasActualRaDecJ2000; }
        }

        /// <summary>
        /// Actual Right Ascension (J2000, hours) derived from corrected Alt/Az.
        /// </summary>
        public double ActualRaJ2000_Hours
        {
            get { lock (_lock) return _actualRaJ2000Hours; }
        }

        /// <summary>
        /// Actual Declination (J2000, degrees) derived from corrected Alt/Az.
        /// </summary>
        public double ActualDecJ2000_Deg
        {
            get { lock (_lock) return _actualDecJ2000Deg; }
        }

        /// <summary>
        /// Julian Day used for the current astronomical coordinate snapshot.
        /// </summary>
        public double JulianDay
        {
            get { lock (_lock) return _julianDay; }
        }

        public DateTime AstronomicalCoordinatesTimestamp
        {
            get { lock (_lock) return _astronomicalCoordinatesTimestamp; }
        }

        /// <summary>
        /// Whether Lib has received a tracking command RA/Dec from API/UI.
        /// </summary>
        public bool HasCommandRaDec
        {
            get { lock (_lock) return _hasCommandRaDec; }
        }

        /// <summary>
        /// Exact command Right Ascension received by Lib from API/UI (hours).
        /// </summary>
        public double CommandRa_Hours
        {
            get { lock (_lock) return _commandRaHours; }
        }

        /// <summary>
        /// Exact command Declination received by Lib from API/UI (degrees).
        /// </summary>
        public double CommandDec_Deg
        {
            get { lock (_lock) return _commandDecDeg; }
        }

        /// <summary>
        /// True when the received command coordinates are apparent/current-epoch.
        /// </summary>
        public bool CommandRaDecIsApparent
        {
            get { lock (_lock) return _commandRaDecIsApparent; }
        }

        public DateTime CommandRaDecTimestamp
        {
            get { lock (_lock) return _commandRaDecTimestamp; }
        }

        /// <summary>
        /// Telescope Offset - Azimuth component (in arcseconds)
        /// Accumulated offset applied from CameraOffsetWindow directional controls.
        /// Positive = offset East, Negative = offset West
        /// </summary>
        public double TelescopeOffset_Az_Arcsec
        {
            get { lock (_lock) return _telescopeOffsetAz_Arcsec; }
            set
            {
                lock (_lock)
                {
                    _telescopeOffsetAz_Arcsec = value;
                    _telescopeOffsetTimestamp = SystemLocalTime;
                }
            }
        }

        /// <summary>
        /// Telescope Offset - Altitude component (in arcseconds)
        /// Accumulated offset applied from CameraOffsetWindow directional controls.
        /// Positive = offset Up, Negative = offset Down
        /// </summary>
        public double TelescopeOffset_Alt_Arcsec
        {
            get { lock (_lock) return _telescopeOffsetAlt_Arcsec; }
            set
            {
                lock (_lock)
                {
                    _telescopeOffsetAlt_Arcsec = value;
                    _telescopeOffsetTimestamp = SystemLocalTime;
                }
            }
        }

        /// <summary>
        /// Timestamp of last telescope offset change
        /// </summary>
        public DateTime TelescopeOffset_Timestamp
        {
            get { lock (_lock) return _telescopeOffsetTimestamp; }
        }

        /// <summary>
        /// Name of the object currently being tracked (star, satellite, planet, DSO).
        /// Set by TrackingManager when tracking starts/stops.
        /// Empty string when not tracking.
        /// </summary>
        public string CurrentTrackingObjectName
        {
            get { lock (_lock) return _currentTrackingObjectName; }
            set { lock (_lock) _currentTrackingObjectName = value ?? ""; }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Reset all status
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _isMountEnabled = false;
                _isTracking = false;
                
                var now = SystemLocalTime; // Use NTP-synced time
                _isMountEnabledTimestamp = now;
                _isTrackingTimestamp = now;
                
                // [NEW] Reset position data
                _actAzimuthPosRaw = 0.0;
                _actAltitudePosRaw = 0.0;
                _actAzimuthPosCorrect = 0.0;
                _actAltitudePosCorrect = 0.0;
                
                _actAzimuthPosRawTimestamp = now;
                _actAltitudePosRawTimestamp = now;
                _actAzimuthPosCorrectTimestamp = now;
                _actAltitudePosCorrectTimestamp = now;

                _hasActualRaDecJ2000 = false;
                _actualRaJ2000Hours = 0.0;
                _actualDecJ2000Deg = 0.0;
                _julianDay = 0.0;
                _astronomicalCoordinatesTimestamp = now;

                _hasCommandRaDec = false;
                _commandRaHours = 0.0;
                _commandDecDeg = 0.0;
                _commandRaDecIsApparent = false;
                _commandRaDecTimestamp = now;
                
                // Reset telescope offset
                _telescopeOffsetAz_Arcsec = 0.0;
                _telescopeOffsetAlt_Arcsec = 0.0;
                _telescopeOffsetTimestamp = now;
                
                _currentTrackingObjectName = "";
            }
        }
        
        /// <summary>
        /// Update mount position from encoder readings AND apply pointing model correction atomically.
        /// Call this from CentralizedPollingManager after reading encoder positions.
        /// [CRITICAL] Raw + Corrected are computed in a single lock � no gap for other threads to read stale data.
        /// Uses App.PointingModelManager (SINGLE PointXP instance for entire application).
        /// </summary>
        /// <param name="azimuthDeg">Raw azimuth position in degrees (from encoder)</param>
        /// <param name="altitudeDeg">Raw altitude position in degrees (from encoder)</param>
        /// <param name="azimuthInfo">AzimuthInfo with raw encoder counts (for PointXP)</param>
        /// <param name="altitudeInfo">AltitudeInfo with raw encoder counts (for PointXP)</param>
        public void UpdateRawPosition(double azimuthDeg, double altitudeDeg, AzimuthInfo azimuthInfo = null, AltitudeInfo altitudeInfo = null)
        {
            lock (_lock)
            {
                var now = SystemLocalTime;
                
                // Set Raw position
                _actAzimuthPosRaw = azimuthDeg;
                _actAltitudePosRaw = altitudeDeg;
                _actAzimuthPosRawTimestamp = now;
                _actAltitudePosRawTimestamp = now;
                
                // [CRITICAL] Do NOT set corrected = raw here!
                // Between unlock and ApplyCorrections, SiderealTracking (15ms timer) may read
                // corrected and get the RAW value ? 4.7� position jump ? mount bump!
                // Instead, keep the previous corrected value (at most 15ms old ? ~0.00006� drift).
                // ApplyCorrections will set the correct value immediately after.
            }
            
            // Apply pointing model correction IMMEDIATELY after setting raw.
            // PointXP computation takes < 1ms.
            // Uses App.PointingModelManager (SINGLE PointXP instance).
            // - Model loaded: sets corrected = pointing-model-corrected value
            // - No model:     sets corrected = raw (handled inside ApplyCorrections)
            var pointingModelManager = ForkMount_WPF.App.PointingModelManager;
            if (pointingModelManager != null && azimuthInfo != null && altitudeInfo != null)
            {
                pointingModelManager.ApplyCorrections(this, azimuthInfo, altitudeInfo);
            }
        }
        
        /// <summary>
        /// Apply pointing model correction to current raw position
        /// Call this after UpdateRawPosition() if pointing model is loaded
        /// </summary>
        /// <param name="azimuthCorrectionDeg">Azimuth correction in degrees (will be SUBTRACTED from raw)</param>
        /// <param name="altitudeCorrectionDeg">Altitude correction in degrees (will be SUBTRACTED from raw)</param>
        public void ApplyPointingModelCorrection(double azimuthCorrectionDeg, double altitudeCorrectionDeg)
        {
            lock (_lock)
            {
                // Corrected position = Raw position - Model error
                // (Model error is how much the mount is OFF, so we subtract it)
                _actAzimuthPosCorrect = _actAzimuthPosRaw - azimuthCorrectionDeg;
                _actAltitudePosCorrect = _actAltitudePosRaw - altitudeCorrectionDeg;
                
                var now = SystemLocalTime; // Use NTP-synced time
                _actAzimuthPosCorrectTimestamp = now;
                _actAltitudePosCorrectTimestamp = now;
            }

            UpdateAstronomicalCoordinates();
        }

        /// <summary>
        /// Recalculate JD and Actual RA/Dec J2000 from the corrected mount position.
        /// </summary>
        public void UpdateAstronomicalCoordinates()
        {
            DateTime utcNow = SystemUtcTime;
            double julianDay = AstroTime.JulianDay(utcNow);

            try
            {
                double correctedAzimuthDeg;
                double correctedAltitudeDeg;
                lock (_lock)
                {
                    correctedAzimuthDeg = _actAzimuthPosCorrect;
                    correctedAltitudeDeg = _actAltitudePosCorrect;
                }

                var observerLocation = AppSettings.GetObserverLocation();
                var site = new LatLon(
                    Angle.FromDegs(observerLocation.Latitude_Deg),
                    Angle.FromDegs(observerLocation.Longitude_Deg));

                var altAz = new AltAz(
                    Angle.FromDegs(correctedAltitudeDeg),
                    Angle.FromDegs(correctedAzimuthDeg));

                var actualRaDecJ2000 = altAz.ToRaDec(site, julianDay).NowToJ2000();

                lock (_lock)
                {
                    _julianDay = julianDay;
                    _actualRaJ2000Hours = actualRaDecJ2000.Ra.Hours;
                    _actualDecJ2000Deg = actualRaDecJ2000.Dec.Degs;
                    _hasActualRaDecJ2000 = true;
                    _astronomicalCoordinatesTimestamp = SystemLocalTime;
                }
            }
            catch
            {
                lock (_lock)
                {
                    _julianDay = julianDay;
                    _actualRaJ2000Hours = 0.0;
                    _actualDecJ2000Deg = 0.0;
                    _hasActualRaDecJ2000 = false;
                    _astronomicalCoordinatesTimestamp = SystemLocalTime;
                }
            }
        }

        /// <summary>
        /// Store the exact RA/Dec command received by Lib from API/UI.
        /// </summary>
        public void SetTrackingCommandRaDec(double raHours, double decDeg, bool isApparent)
        {
            lock (_lock)
            {
                _commandRaHours = raHours;
                _commandDecDeg = decDeg;
                _commandRaDecIsApparent = isApparent;
                _hasCommandRaDec = true;
                _commandRaDecTimestamp = SystemLocalTime;
            }
        }

        /// <summary>
        /// Clear the cached tracking command RA/Dec.
        /// </summary>
        public void ClearTrackingCommandRaDec()
        {
            lock (_lock)
            {
                _commandRaHours = 0.0;
                _commandDecDeg = 0.0;
                _commandRaDecIsApparent = false;
                _hasCommandRaDec = false;
                _commandRaDecTimestamp = SystemLocalTime;
            }
        }

        #endregion

        #region Constructor

        public MountInfo()
        {
            Reset();
        }

        #endregion
    }
}

