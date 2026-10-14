using System;
using System.Diagnostics;
using System.Threading;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Mount Manager - Dedicated manager for Mount-level status calculation
    /// Runs on dedicated System Timer (5ms = 200Hz) to update MountInfo status
    /// Separates mount status logic from UI and polling loops
    /// </summary>
    public class MountManager
    {
        #region Constants
        
        private const int MOUNT_STATUS_UPDATE_INTERVAL_MS = 5; // 5ms = 200Hz
        
        #endregion

        #region Fields

        private System.Threading.Timer _statusUpdateTimer;
        private MountInfo _mountInfo;
        private bool _isRunning;
        private readonly object _lock = new object();

        #endregion

        #region Properties

        /// <summary>
        /// Mount Info object (managed by this manager)
        /// </summary>
        public MountInfo MountInfo => _mountInfo;

        /// <summary>
        /// Is manager running
        /// </summary>
        public bool IsRunning
        {
            get { lock (_lock) return _isRunning; }
        }

        /// <summary>
        /// Update Rate (Hz)
        /// </summary>
        public int UpdateRate => 1000 / MOUNT_STATUS_UPDATE_INTERVAL_MS;

        #endregion

        #region Constructor

        public MountManager(MountInfo mountInfo)
        {
            _mountInfo = mountInfo ?? throw new ArgumentNullException(nameof(mountInfo));
            
            // Create System Timer (more accurate than DispatcherTimer)
            _statusUpdateTimer = new System.Threading.Timer(
                StatusUpdateCallback,
                null,
                Timeout.Infinite, // Don't start yet
                Timeout.Infinite);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Start Mount Status Update Timer
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    Debug.WriteLine("[MOUNT MANAGER] Already running");
                    return;
                }

                _statusUpdateTimer.Change(0, MOUNT_STATUS_UPDATE_INTERVAL_MS);
                _isRunning = true;
                
                Debug.WriteLine($"[MOUNT MANAGER] Started - Update rate: {UpdateRate}Hz ({MOUNT_STATUS_UPDATE_INTERVAL_MS}ms interval)");
            }
        }

        /// <summary>
        /// Stop Mount Status Update Timer
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;

                _statusUpdateTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _isRunning = false;
                
                Debug.WriteLine("[MOUNT MANAGER] Stopped");
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            Stop();
            _statusUpdateTimer?.Dispose();
            _statusUpdateTimer = null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Timer callback - Update Mount Status from axis info
        /// Runs at 200Hz (5ms) in background thread
        /// [CRITICAL] All mount status calculation logic is HERE (not in MountInfo!)
        /// </summary>
        private void StatusUpdateCallback(object state)
        {
            try
            {
                // Get axis info from global App singleton
                var azimuthInfo = App.AzimuthInfo;
                var altitudeInfo = App.AltitudeInfo;

                // Null check
                if (azimuthInfo == null || altitudeInfo == null)
                    return;

                // ========== CALCULATE IsMountEnabled ==========
                // Both axes must be enabled
                bool wasEnabled = _mountInfo.IsMountEnabled;
                bool newEnabled = azimuthInfo.Is_Drive_Enabled && altitudeInfo.Is_Drive_Enabled;
                
                if (newEnabled != wasEnabled)
                {
                    _mountInfo.IsMountEnabled = newEnabled;
                    Debug.WriteLine($"[MOUNT MANAGER] Mount enabled: {(newEnabled ? "YES" : "NO")} (Az:{azimuthInfo.Is_Drive_Enabled}, Alt:{altitudeInfo.Is_Drive_Enabled})");
                }

                // ========== CALCULATE IsTracking ==========
                // Both axes RMS must be within calibration stability threshold
                // Uses SAME threshold as mount calibration for consistency
                
                // Load RMS stability threshold from mount calibration settings
                var calibrationSettings = AppSettings.GetMountCalibrationSettings();
                double rmsThresholdArcsec = calibrationSettings.RmsStabilityThreshold_Arcsec; // Default: 3.0"

                bool azTracking = azimuthInfo.TrackingRmsError_Arcsec <= rmsThresholdArcsec;
                bool altTracking = altitudeInfo.TrackingRmsError_Arcsec <= rmsThresholdArcsec;
                
                bool wasTracking = _mountInfo.IsTracking;
                bool newTracking = azTracking && altTracking;

                if (newTracking != wasTracking)
                {
                    _mountInfo.IsTracking = newTracking;
                    Debug.WriteLine($"[MOUNT MANAGER] Tracking: {(newTracking ? "ACQUIRED" : "LOST")}");
                    Debug.WriteLine($"[MOUNT MANAGER]   Az RMS: {azimuthInfo.TrackingRmsError_Arcsec:F2}\" (threshold: {rmsThresholdArcsec:F2}\") - {(azTracking ? "OK" : "HIGH")}");
                    Debug.WriteLine($"[MOUNT MANAGER]   Alt RMS: {altitudeInfo.TrackingRmsError_Arcsec:F2}\" (threshold: {rmsThresholdArcsec:F2}\") - {(altTracking ? "OK" : "HIGH")}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MOUNT MANAGER ERROR] {ex.Message}");
            }
        }

        #endregion
    }
}


