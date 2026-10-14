using System;
using System.Collections.Generic;
using System.Linq;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using System.Windows.Threading;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Tracking
{
    /// <summary>
    /// Centralized RMS Error Tracking Manager
    /// Calculates RMS tracking error for all tracking modes (Sidereal, Satellite)
    /// Runs at 20Hz (50ms interval) independent of tracking loop speed
    /// 
    /// Usage:
    /// - Tracking controls call SetAzimuthTarget()/SetAltitudeTarget() to update target position
    /// - When tracking stops, call SetAzimuthTarget(null) to clear
    /// - RMS automatically calculated and written to AzimuthInfo/AltitudeInfo
    /// </summary>
    public class RmsTrackingManager
    {
        #region Fields

        // [CRITICAL] Target positions (null = not tracking)
        private double? _azimuthTargetPos_Deg;
        private double? _altitudeTargetPos_Deg;
        private object _targetLock = new object();
        
        // [CRITICAL FIX] Position source must match SiderealTracking!
        // When SiderealTracking uses Raw (calibration), RMS must also use Raw.
        // When SiderealTracking uses Corrected (normal), RMS must also use Corrected.
        // Mismatch causes RMS = |model correction| (e.g., 1689" for 0.47° correction)
        private bool _useRawPosition = false;

        // Sample collections (Moving Window)
        private List<double> _azimuthErrorSamples = new List<double>();
        private List<double> _altitudeErrorSamples = new List<double>();

        // References
        private CentralizedPollingManager _pollingManager;
        private DispatcherTimer _rmsCalculationTimer;

        // Configuration
        private const int RMS_CALCULATION_INTERVAL_MS = 50;  // 20Hz
        private const int MAX_RMS_SAMPLES = 100;             // 100 samples = 5 seconds at 20Hz
        private const int MIN_RMS_SAMPLES = 1;               // Show immediately
        
        // [NEW] IsTracking threshold
        private const double RMS_TRACKING_THRESHOLD_ARCSEC = 10.0;  // RMS < 10" = IsTracking = true
        private const int MIN_SAMPLES_FOR_TRACKING_FLAG = 10;        // Need 10 samples before setting flag

        #endregion

        #region Constructor

        public RmsTrackingManager(CentralizedPollingManager pollingManager)
        {
            _pollingManager = pollingManager ?? throw new ArgumentNullException(nameof(pollingManager));

            // Start RMS calculation timer (50ms = 20Hz)
            _rmsCalculationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(RMS_CALCULATION_INTERVAL_MS)
            };
            _rmsCalculationTimer.Tick += RmsCalculationTimer_Tick;
            _rmsCalculationTimer.Start();

            SimpleLogger.Info("[RMS MANAGER] Initialized - Calculation rate: 20Hz (50ms)", "RmsManager");
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Set Azimuth target position for RMS calculation
        /// Call with null to stop tracking
        /// </summary>
        public void SetAzimuthTarget(double? targetDeg)
        {
            lock (_targetLock)
            {
                bool wasNull = !_azimuthTargetPos_Deg.HasValue;
                _azimuthTargetPos_Deg = targetDeg;

                // Clear samples when starting new tracking
                if (wasNull && targetDeg.HasValue)
                {
                    _azimuthErrorSamples.Clear();
                    SimpleLogger.Info($"[RMS MANAGER] Azimuth tracking started - Target: {targetDeg:F2}°", "RmsManager");
                }
                else if (!targetDeg.HasValue && !wasNull)
                {
                    SimpleLogger.Info($"[RMS MANAGER] Azimuth tracking stopped - Final RMS: {_pollingManager?.AzimuthInfo?.TrackingRmsError_Arcsec:F2}\" (samples={_azimuthErrorSamples.Count})", "RmsManager");
                }
            }
        }

        /// <summary>
        /// Set Altitude target position for RMS calculation
        /// Call with null to stop tracking
        /// </summary>
        public void SetAltitudeTarget(double? targetDeg)
        {
            lock (_targetLock)
            {
                bool wasNull = !_altitudeTargetPos_Deg.HasValue;
                _altitudeTargetPos_Deg = targetDeg;

                if (wasNull && targetDeg.HasValue)
                {
                    _altitudeErrorSamples.Clear();
                    SimpleLogger.Info($"[RMS MANAGER] Altitude tracking started - Target: {targetDeg:F2}°", "RmsManager");
                }
                else if (!targetDeg.HasValue && !wasNull)
                {
                    SimpleLogger.Info($"[RMS MANAGER] Altitude tracking stopped - Final RMS: {_pollingManager?.AltitudeInfo?.TrackingRmsError_Arcsec:F2}\" (samples={_altitudeErrorSamples.Count})", "RmsManager");
                }
            }
        }

        /// <summary>
        /// Set whether to use Raw encoder position (true) or Corrected position (false)
        /// MUST match SiderealTracking.UseRawPosition to avoid coordinate mismatch!
        /// </summary>
        public void SetUseRawPosition(bool useRaw)
        {
            if (_useRawPosition != useRaw)
            {
                _useRawPosition = useRaw;
                ClearSamples();  // Reset samples when switching coordinate system
                SimpleLogger.Info($"[RMS MANAGER] Position source: {(useRaw ? "RAW" : "CORRECTED")}", "RmsManager");
            }
        }
        
        /// <summary>
        /// Clear all RMS samples (e.g., when changing target)
        /// </summary>
        public void ClearSamples()
        {
            lock (_targetLock)
            {
                _azimuthErrorSamples.Clear();
                _altitudeErrorSamples.Clear();
                SimpleLogger.Info("[RMS MANAGER] Samples cleared", "RmsManager");
            }
        }

        /// <summary>
        /// Stop RMS calculation timer and clear all data
        /// </summary>
        public void Stop()
        {
            _rmsCalculationTimer?.Stop();
            lock (_targetLock)
            {
                _azimuthTargetPos_Deg = null;
                _altitudeTargetPos_Deg = null;
                ClearSamples();
            }
            SimpleLogger.Info("[RMS MANAGER] Stopped", "RmsManager");
        }

        #endregion

        #region RMS Calculation Timer

        private void RmsCalculationTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                lock (_targetLock)
                {
                    // Calculate Azimuth RMS if tracking
                    if (_azimuthTargetPos_Deg.HasValue)
                    {
                        CalculateAzimuthRms();
                    }
                    else
                    {
                        // Not tracking - clear samples if any exist
                        if (_azimuthErrorSamples.Count > 0)
                        {
                            _azimuthErrorSamples.Clear();
                        }
                    }

                    // Calculate Altitude RMS if tracking
                    if (_altitudeTargetPos_Deg.HasValue)
                    {
                        CalculateAltitudeRms();
                    }
                    else
                    {
                        if (_altitudeErrorSamples.Count > 0)
                        {
                            _altitudeErrorSamples.Clear();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RMS MANAGER ERROR] {ex.Message}");
            }
        }

        #endregion

        #region Private Calculation Methods

        private void CalculateAzimuthRms()
        {
            try
            {
                var azConfig = AppSettings.GetAzimuthAxisConfig();
                if (_pollingManager?.AzimuthInfo == null || azConfig == null)
                    return;

                // [CRITICAL FIX] Use same position source as SiderealTracking!
                // Raw = during calibration (UseRawPosition=true)
                // Corrected = normal tracking (UseRawPosition=false)
                double currentAzDeg = 0;
                if (_useRawPosition)
                {
                    // Calibration mode: use Raw encoder position (matches SiderealTracking target)
                    currentAzDeg = (_pollingManager.AzimuthInfo.Raw_Position / (double)azConfig.Encoder_Resolution) * 360.0 + azConfig.Position_Offset_Deg;
                }
                else if (_pollingManager?.MountInfo != null)
                {
                    // Normal mode: use Corrected position (pointing model applied)
                    currentAzDeg = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                }
                else
                {
                    // Fallback: calculate manually (Raw)
                    currentAzDeg = (_pollingManager.AzimuthInfo.Raw_Position / (double)azConfig.Encoder_Resolution) * 360.0 + azConfig.Position_Offset_Deg;
                }

                // Calculate error (target - actual)
                double azErrorDeg = _azimuthTargetPos_Deg.Value - currentAzDeg;

                // Handle azimuth wrap-around (360° boundary)
                if (azErrorDeg > 180.0) azErrorDeg -= 360.0;
                if (azErrorDeg < -180.0) azErrorDeg += 360.0;

                // Add to samples (Moving Window)
                _azimuthErrorSamples.Add(azErrorDeg);
                if (_azimuthErrorSamples.Count > MAX_RMS_SAMPLES)
                    _azimuthErrorSamples.RemoveAt(0);

                // Calculate RMS if we have enough samples
                if (_azimuthErrorSamples.Count >= MIN_RMS_SAMPLES)
                {
                    double sumSquared = _azimuthErrorSamples.Sum(e => e * e);
                    double rms_deg = Math.Sqrt(sumSquared / _azimuthErrorSamples.Count);
                    double rms_arcsec = rms_deg * 3600.0;

                    // Update AzimuthInfo
                    _pollingManager.AzimuthInfo.TrackingRmsError_Arcsec = rms_arcsec;
                    _pollingManager.AzimuthInfo.TrackingSampleCount = _azimuthErrorSamples.Count;
                    
                    // [NEW] Set IsTracking flag based on RMS threshold
                    // Only set flag if we have enough samples (avoid false positives)
                    if (_azimuthErrorSamples.Count >= MIN_SAMPLES_FOR_TRACKING_FLAG)
                    {
                        bool isTrackingGood = rms_arcsec <= RMS_TRACKING_THRESHOLD_ARCSEC;
                        _pollingManager.AzimuthInfo.IsTracking = isTrackingGood;
                        _pollingManager.AzimuthInfo.TrackingMode = TrackingMode.Sidereal; // Assume sidereal for now
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RMS MANAGER] Azimuth calculation error: {ex.Message}");
            }
        }

        private void CalculateAltitudeRms()
        {
            try
            {
                var altConfig = AppSettings.GetAltitudeAxisConfig();
                if (_pollingManager?.AltitudeInfo == null || altConfig == null)
                    return;

                // [CRITICAL FIX] Use same position source as SiderealTracking!
                double currentAltDeg = 0;
                if (_useRawPosition)
                {
                    // Calibration mode: use Raw encoder position
                    currentAltDeg = (_pollingManager.AltitudeInfo.Raw_Position / (double)altConfig.Encoder_Resolution) * 360.0 + altConfig.Position_Offset_Deg;
                }
                else if (_pollingManager?.MountInfo != null)
                {
                    // Normal mode: use Corrected position
                    currentAltDeg = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;
                }
                else
                {
                    // Fallback: calculate manually (Raw)
                    currentAltDeg = (_pollingManager.AltitudeInfo.Raw_Position / (double)altConfig.Encoder_Resolution) * 360.0 + altConfig.Position_Offset_Deg;
                }

                // Calculate error (target - actual)
                double altErrorDeg = _altitudeTargetPos_Deg.Value - currentAltDeg;

                // Add to samples (Moving Window)
                _altitudeErrorSamples.Add(altErrorDeg);
                if (_altitudeErrorSamples.Count > MAX_RMS_SAMPLES)
                    _altitudeErrorSamples.RemoveAt(0);

                // Calculate RMS if we have enough samples
                if (_altitudeErrorSamples.Count >= MIN_RMS_SAMPLES)
                {
                    double sumSquared = _altitudeErrorSamples.Sum(e => e * e);
                    double rms_deg = Math.Sqrt(sumSquared / _altitudeErrorSamples.Count);
                    double rms_arcsec = rms_deg * 3600.0;

                    // Update AltitudeInfo
                    _pollingManager.AltitudeInfo.TrackingRmsError_Arcsec = rms_arcsec;
                    _pollingManager.AltitudeInfo.TrackingSampleCount = _altitudeErrorSamples.Count;
                    
                    // [NEW] Set IsTracking flag based on RMS threshold
                    if (_altitudeErrorSamples.Count >= MIN_SAMPLES_FOR_TRACKING_FLAG)
                    {
                        bool isTrackingGood = rms_arcsec <= RMS_TRACKING_THRESHOLD_ARCSEC;
                        _pollingManager.AltitudeInfo.IsTracking = isTrackingGood;
                        _pollingManager.AltitudeInfo.TrackingMode = TrackingMode.Sidereal;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RMS MANAGER] Altitude calculation error: {ex.Message}");
            }
        }

        #endregion
    }
}
