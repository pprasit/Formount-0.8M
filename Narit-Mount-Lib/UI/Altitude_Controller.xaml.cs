using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ForkMount_WPF.Engine;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    public partial class Altitude_Controller : UserControl
    {
        private AltitudeManager _altitudeDrive;
        private bool _isDriveEnabled = false;
        private DispatcherTimer _uiUpdateTimer;
        private AltitudeInfo _altitudeInfo;
        private const int UI_UPDATE_INTERVAL_MS = 50; // 20Hz - reduced to test UI smoothness

        // [NEW] Position Monitor for automatic stopping at target
        private DispatcherTimer _positionMonitorTimer;
        private double _targetPositionDeg = double.NaN;
        private double _moveVelocityDegSec = 0;
        private const double POSITION_TOLERANCE_DEG = 0.1; // Stop when within 0.1 degrees of target (reduced from 0.5)
        private const int POSITION_MONITOR_INTERVAL_MS = 50; // Check every 50ms (20Hz)
        private bool _inDecelerationZone = false; // Track if already in deceleration zone (to reduce log spam)
        
        private CentralizedPollingManager _centralizedPollingManager;
        
        /// <summary>
        /// Get the correct PlatinumDrive for Altitude axis
        /// Returns separate Altitude drive if available, otherwise falls back to shared drive
        /// </summary>
        private PlatinumDrive AltitudeDriveHw => _centralizedPollingManager?.AltitudePlatinumDrive;
        
        /// <summary>
        /// Get the correct axis number for Altitude on its drive
        /// Returns 1 if on separate drive, 2 if sharing with Azimuth
        /// </summary>
        private int AltitudeAxisNumber
        {
            get
            {
                if (_centralizedPollingManager == null) return 2;
                bool isSeparateDrive = _centralizedPollingManager.AltitudePlatinumDrive != _centralizedPollingManager.PlatinumDrive;
                return isSeparateDrive ? 1 : 2;
            }
        }

        // Public properties for external access (e.g., Manual Control Window)
        public AltitudeManager AltitudeDrive => _altitudeDrive;

        public AltitudeInfo GetCurrentAltitudeInfo()
        {
            return _altitudeInfo ?? _altitudeDrive?.CurrentInfo;
        }

        /// <summary>
        /// Get current altitude position in degrees
        /// [NEW] Returns position from MountInfo (already in degrees)
        /// </summary>
        public double GetCurrentPosition()
        {
            if (_centralizedPollingManager?.MountInfo != null)
            {
                return _centralizedPollingManager.MountInfo.Act_Altitude_Pos_Raw;
            }
            
            // Fallback: calculate manually
            var config = AppSettings.GetAltitudeAxisConfig();
            double counts = _altitudeInfo?.Raw_Position ?? 0;
            return (counts / config.Encoder_Resolution) * 360.0;
        }

        /// <summary>
        /// Set velocity command for altitude axis (for tracking)
        /// </summary>
        public void SetVelocity(double velocityDegPerSec)
        {
            if (_altitudeInfo != null)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = velocityDegPerSec;
                System.Diagnostics.Debug.WriteLine($"[ALTITUDE] Velocity set: {velocityDegPerSec:F3} deg/s");
            }
        }

        public Altitude_Controller()
        {
            InitializeComponent();
            
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                return;
            }
            
            InitializeUIUpdateTimer();
            InitializePositionMonitor();
            _altitudeInfo = new AltitudeInfo();
            LoadAxisConfiguration(); // Load Config Tab values from AppSettings
            
            AddLog("[INIT] Altitude Controller initialized (Axis 2)", LogType.Message);
        }

        #region UI Update Timer (50ms = 20Hz)

        private void InitializeUIUpdateTimer()
        {
            _uiUpdateTimer = new DispatcherTimer();
            _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(UI_UPDATE_INTERVAL_MS);
            _uiUpdateTimer.Tick += UIUpdateTimer_Tick;
            
            AddLog($"[INIT] UI update timer initialized: {UI_UPDATE_INTERVAL_MS}ms ({1000.0/UI_UPDATE_INTERVAL_MS:F0} Hz)", LogType.Message);
        }

        private void UIUpdateTimer_Tick(object sender, EventArgs e)
        {
            // Check if CentralizedPollingManager is available (new method)
            if (_centralizedPollingManager != null)
            {
                // [FIX] Get SNAPSHOT of data from CentralizedPollingManager
                // Important: We must copy all values, not keep a reference to the same object!
                // Otherwise UI will always see the same object reference and appear frozen
                var snapshot = _centralizedPollingManager.AltitudeInfo;
                _altitudeInfo.Raw_Position = snapshot.Raw_Position;
                _altitudeInfo.Raw_Velocity = snapshot.Raw_Velocity;
                _altitudeInfo.Current_IQ = snapshot.Current_IQ;
                _altitudeInfo.Command_Velocity_Deg_Sec = snapshot.Command_Velocity_Deg_Sec;
                _altitudeInfo.Is_Drive_Enabled = snapshot.Is_Drive_Enabled;
            }
            else if (_altitudeDrive == null || !_altitudeDrive.IsConnected)
            {
                // Fallback to old method or return if not connected
                return;
            }
            else
            {
                // [OLD] Fallback: Read from AltitudeDrive
                _altitudeInfo = _altitudeDrive.CurrentInfo;
            }

            try
            {
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                
                double positionCounts = _altitudeInfo.Raw_Position;
                double velocityCounts = _altitudeInfo.Raw_Velocity;
                double current = _altitudeInfo.Current_IQ;
                double cmdVel = _altitudeInfo.Command_Velocity_Deg_Sec;
                bool driveEnabled = _altitudeInfo.Is_Drive_Enabled;
                
                // [NEW] Get position in degrees from MountInfo (already converted)
                double positionDeg = 0;
                if (_centralizedPollingManager?.MountInfo != null)
                {
                    positionDeg = _centralizedPollingManager.MountInfo.Act_Altitude_Pos_Raw;
                }
                else
                {
                    // Fallback: calculate manually if MountInfo not available
                    positionDeg = (positionCounts / axisConfig.Encoder_Resolution) * 360.0;
                }
                
                double velocityDeg = (velocityCounts / axisConfig.Encoder_Resolution) * 360.0;
                
                // Update Drive Status Tab
                txtRawPosition.Text = positionCounts.ToString("F0");
                txtRawPositionDeg.Text = positionDeg.ToString("F3");
                txtRawVelocity.Text = velocityCounts.ToString("F0");
                txtRawVelocityDeg.Text = velocityDeg.ToString("F4");
                txtCurrentIQStatus.Text = current.ToString("F3") + " A";
                
                // [NEW] Update Corrected Position from MountInfo
                if (_centralizedPollingManager?.MountInfo != null)
                {
                    double correctedPos = _centralizedPollingManager.MountInfo.Act_Altitude_Pos_Correct;
                    txtCorrectedPosition.Text = correctedPos.ToString("F3");
                }
                else
                {
                    txtCorrectedPosition.Text = "-";
                }
                
                // Update Drive Status display and indicator
                if (driveEnabled)
                {
                    txtDriveStatusDisplay.Text = "Enabled";
                    driveStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                }
                else
                {
                    txtDriveStatusDisplay.Text = "Disabled";
                    driveStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                }
                
                UpdateDriveEnableButton(driveEnabled);
                
                // Update chart
                if (chartPanel != null)
                {
                    double timestamp = DateTime.Now.Subtract(DateTime.Today).TotalSeconds;
                    chartPanel.AddDataPoint(positionDeg, velocityDeg, cmdVel, current, timestamp);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] UI update error: {ex.Message}", LogType.Error);
            }
        }

        private void StartUIUpdate()
        {
            if (!_uiUpdateTimer.IsEnabled)
            {
                _uiUpdateTimer.Start();
                AddLog($"[START] UI update started: {UI_UPDATE_INTERVAL_MS}ms ({1000.0/UI_UPDATE_INTERVAL_MS:F0} Hz)", LogType.Message);
            }
        }

        private void StopUIUpdate()
        {
            if (_uiUpdateTimer.IsEnabled)
            {
                _uiUpdateTimer.Stop();
                AddLog("[STOP] UI update stopped", LogType.Message);
            }
        }

        #endregion

        #region Position Monitor (Auto-Stop at Target)

        /// <summary>
        /// Initialize Position Monitor Timer
        /// </summary>
        private void InitializePositionMonitor()
        {
            _positionMonitorTimer = new DispatcherTimer();
            _positionMonitorTimer.Interval = TimeSpan.FromMilliseconds(POSITION_MONITOR_INTERVAL_MS);
            _positionMonitorTimer.Tick += PositionMonitorTimer_Tick;
            
            AddLog($"[INIT] Position monitor initialized: {POSITION_MONITOR_INTERVAL_MS}ms ({1000.0/POSITION_MONITOR_INTERVAL_MS:F0} Hz)", LogType.Message);
        }

        /// <summary>
        /// Position Monitor Tick - Check if target position reached
        /// [NEW ARCHITECTURE] For Position Mode (UM=2) - monitors Drive's internal position control
        /// Checks MS (Motion Status) to detect when motion is complete
        /// </summary>
        private void PositionMonitorTimer_Tick(object sender, EventArgs e)
        {
            // Skip if no target position set
            if (double.IsNaN(_targetPositionDeg))
            {
                return;
            }

            try
            {
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                
                // Get current position
                double currentPositionCounts = _altitudeInfo.Raw_Position;
                double currentPositionDeg = (currentPositionCounts / axisConfig.Encoder_Resolution) * 360.0;
                
                // Calculate error
                double error = _targetPositionDeg - currentPositionDeg;
                double absError = Math.Abs(error);
                
                // [POSITION MODE] Check if Drive's motion is complete
                bool motionComplete = _altitudeDrive?.IsMotionComplete() ?? false;
                
                // Check if at target (either by Motion Status or by position tolerance)
                bool atTarget = motionComplete || (absError <= POSITION_TOLERANCE_DEG);
                
                // Log progress (only first time and when reaching target)
                if (!_inDecelerationZone)
                {
                    AddLog($"[POSITION MODE] Moving to {_targetPositionDeg:F2}� (Current: {currentPositionDeg:F2}�, Error: {error:F3}�)", LogType.Message);
                    _inDecelerationZone = true;
                }
                
                if (atTarget && _moveVelocityDegSec != 0)
                {
                    AddLog($"[TARGET REACHED] Position: {currentPositionDeg:F2}� (Target: {_targetPositionDeg:F2}�, Error: {absError:F4}�)", LogType.Message);
                    AddLog($"[POSITION MODE] Motion complete - Drive holding position internally", LogType.Message);
                    _moveVelocityDegSec = 0; // Flag to prevent log spam
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Position monitor error: {ex.Message}", LogType.Error);
                StopPositionMonitor();
            }
        }

        /// <summary>
        /// Start Position Monitor
        /// </summary>
        private void StartPositionMonitor()
        {
            if (!_positionMonitorTimer.IsEnabled)
            {
                _positionMonitorTimer.Start();
                _inDecelerationZone = false; // Reset flag when starting new movement
                // Don't log here to avoid spam
            }
        }

        /// <summary>
        /// Stop Position Monitor
        /// </summary>
        private void StopPositionMonitor()
        {
            if (_positionMonitorTimer.IsEnabled)
            {
                _positionMonitorTimer.Stop();
                _inDecelerationZone = false; // Reset flag when stopping
            }
        }

        #endregion

        #region Initialize with CentralizedPollingManager

        /// <summary>
        /// [NEW] Initialize with CentralizedPollingManager from Azimuth
        /// Replaces InitializeWithSharedDrive
        /// </summary>
        public void InitializeWithCentralizedPollingManager(CentralizedPollingManager pollingManager)
        {
            if (pollingManager == null)
            {
                AddLog("[ERROR] Cannot initialize Altitude: CentralizedPollingManager is null", LogType.Error);
                return;
            }

            _centralizedPollingManager = pollingManager;
            
            // [NEW] Set MountInfo reference for chart panel
            if (chartPanel != null && pollingManager.MountInfo != null)
            {
                chartPanel.SetMountInfo(pollingManager.MountInfo);
            }
            
            // [CRITICAL] Initialize AltitudeDrive with the correct PlatinumDrive
            // If separate Altitude drive exists, use it with Axis 1
            // Otherwise, use shared drive with Axis 2
            var altDrive = pollingManager.AltitudePlatinumDrive;
            if (altDrive != null)
            {
                bool isSeparateDrive = altDrive != pollingManager.PlatinumDrive;
                int axisNumber = isSeparateDrive ? 1 : 2; // Axis 1 on separate drive, Axis 2 on shared drive
                
                _altitudeDrive = new AltitudeManager(altDrive, axisNumber);
                
                if (isSeparateDrive)
                {
                    AddLog($"[SUCCESS] AltitudeDrive initialized with SEPARATE PlatinumDrive (SN: {altDrive.SerialNumber}, Axis {axisNumber})", LogType.Message);
                }
                else
                {
                    AddLog($"[SUCCESS] AltitudeDrive initialized with shared PlatinumDrive (Axis {axisNumber})", LogType.Message);
                }
            }
            else
            {
                AddLog("[ERROR] Cannot initialize AltitudeDrive: PlatinumDrive is null", LogType.Error);
            }
            
            AddLog("[INFO] Subscribing to CentralizedPollingManager.AltitudeDataUpdated event...", LogType.Message);
            
            // Subscribe to Altitude data updates
            _centralizedPollingManager.AltitudeDataUpdated += OnAltitudeDataUpdated;
            
            AddLog("[SUCCESS] Altitude Controller initialized with CentralizedPollingManager", LogType.Message);
            AddLog("[INFO] Receiving data @ 200Hz from centralized polling", LogType.Message);
            AddLog($"[DEBUG] Event subscribed: AltitudeDataUpdated", LogType.Message);
            LoadAxisConfiguration();
            
            // Update Drive Connection Tab
            // Note: Connection info is managed by Azimuth, we just display it
            
            // Start UI Update Timer
            StartUIUpdate();
            
            // Set Motion Parameters from Config to Drive
            SetMotionParametersFromConfig();
            
            // [NEW] Read Limits from Drive after initialization
            // This ensures we get fresh values from Drive (VL[3], VH[3])
            if (altDrive != null && altDrive.IsConnected)
            {
                ReadAndDisplayLimitsFromDrive();
            }
        }
        
        /// <summary>
        /// Event handler for Altitude data updates from CentralizedPollingManager
        /// </summary>
        private void OnAltitudeDataUpdated(object sender, AltitudeInfo altitudeInfo)
        {
            // [FIX] Don't just assign reference! Copy values to local _altitudeInfo
            // This event runs @ 200Hz, we just need to update our local copy
            _altitudeInfo.Raw_Position = altitudeInfo.Raw_Position;
            _altitudeInfo.Raw_Velocity = altitudeInfo.Raw_Velocity;
            _altitudeInfo.Current_IQ = altitudeInfo.Current_IQ;
            _altitudeInfo.Command_Velocity_Deg_Sec = altitudeInfo.Command_Velocity_Deg_Sec;
            _altitudeInfo.Is_Drive_Enabled = altitudeInfo.Is_Drive_Enabled;
            
            // Note: UI updates happen in UIUpdateTimer_Tick @ 10Hz (100ms)
            // This just receives data @ 200Hz (5ms) from polling manager
        }

        /// <summary>
        /// [OLD] Initialize with shared PlatinumDrive from Azimuth
        /// Kept for compatibility but not used with CentralizedPollingManager
        /// </summary>
        public void InitializeWithSharedDrive(PlatinumDrive sharedDrive)
        {
            if (sharedDrive == null)
            {
                AddLog("[ERROR] Cannot initialize AltitudeDrive: PlatinumDrive is null", LogType.Error);
                return;
            }

            _altitudeDrive = new AltitudeManager(sharedDrive);
            _altitudeInfo = _altitudeDrive.CurrentInfo;
            
            AddLog($"[SUCCESS] AltitudeDrive initialized with shared drive (Connected: {sharedDrive.IsConnected})", LogType.Message);
            
            if (sharedDrive.IsConnected)
            {
                // Update Drive Connection Tab
                UpdateDriveConnectionInfo(sharedDrive);
                
                AddLog("[INFO] Starting Altitude polling and UI update...", LogType.Message);
                _altitudeDrive.StartPolling();
                StartUIUpdate();
                
                // Set Motion Parameters from Config to Drive
                SetMotionParametersFromConfig();
            }
        }

        /// <summary>
        /// Update Drive Connection Tab information
        /// </summary>
        private void UpdateDriveConnectionInfo(PlatinumDrive drive)
        {
            if (drive == null) return;

            try
            {
                txtDriveSN.Text = drive.SerialNumber ?? "PLATINUM-001";
                txtComPort.Text = drive.ComPort ?? "COM10";
                txtBaudRate.Text = "115200";
                txtPollingIntervalDisplay.Text = "1";
                
                if (drive.IsConnected)
                {
                    txtConnectionStatus.Text = "Connected";
                    connectionStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    tabConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                }
                else
                {
                    txtConnectionStatus.Text = "Disconnected";
                    connectionStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    tabConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to update drive connection info: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Configuration

        private void LoadConfiguration()
        {
            try
            {
                var config = AppSettings.GetAltitudeAxisConfig();
                
                txtPositionOffset.Text = config.Position_Offset_Deg.ToString("F1");
                txtDownLimit.Text = config.Down_Limit_Deg.ToString("F1");
                txtUpLimit.Text = config.Up_Limit_Deg.ToString("F1");
                txtEncoderResolution.Text = config.Encoder_Resolution.ToString("F0");
                txtMaxVelocity.Text = config.Max_Velocity_Deg_Sec.ToString("F1");
                txtAcceleration.Text = config.Acceleration_Deg_Sec2.ToString("F1");
                txtDeceleration.Text = config.Deceleration_Deg_Sec2.ToString("F1");
                txtStopDeceleration.Text = config.Stop_Deceleration_Deg_Sec2.ToString("F1");
                txtSmooth.Text = config.Smooth_Factor_Ms.ToString("F1");
                txtLimitWarningDistance.Text = config.Limit_Warning_Distance_Deg.ToString("F2");
                
                AddLog("[LOAD] Configuration loaded from AppSettings", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to load configuration: {ex.Message}", LogType.Error);
            }
        }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = new AltitudeAxisConfig
                {
                    Position_Offset_Deg = ParseDouble(txtPositionOffset.Text, "Position Offset"),
                    Down_Limit_Deg = ParseDouble(txtDownLimit.Text, "Down Limit"),
                    Up_Limit_Deg = ParseDouble(txtUpLimit.Text, "Up Limit"),
                    Encoder_Resolution = AppSettings.GetAltitudeAxisConfig().Encoder_Resolution,
                    Max_Velocity_Deg_Sec = ParseDouble(txtMaxVelocity.Text, "Max Velocity"),
                    Acceleration_Deg_Sec2 = ParseDouble(txtAcceleration.Text, "Acceleration"),
                    Deceleration_Deg_Sec2 = ParseDouble(txtDeceleration.Text, "Deceleration"),
                    Stop_Deceleration_Deg_Sec2 = ParseDouble(txtStopDeceleration.Text, "Stop Deceleration"),
                    Smooth_Factor_Ms = ParseDouble(txtSmooth.Text, "Smooth Factor"),
                    Limit_Warning_Distance_Deg = ParseDouble(txtLimitWarningDistance.Text, "Limit Warning Distance"),
                    Tracking_Sensitivity_Deg = ParseDouble(txtTrackingSensitivity.Text, "Tracking Sensitivity")
                };
                
                AppSettings.SaveAltitudeAxisConfig(config);
                
                AddLog("[CONFIG] Axis configuration saved successfully", LogType.Message);
                
                // [NEW] Set Position Limits to Drive (VL[3], VH[3])
                if (_altitudeDrive != null && _altitudeDrive.IsConnected)
                {
                    try
                    {
                        AddLog("[SAVE-LIMITS] Sending Down Limit (VL[3]) and Up Limit (VH[3]) to Drive...", LogType.Message);
                        
                        // Set VL[3] - Down Limit
                        _altitudeDrive.SetVelocityLowLimit(config.Down_Limit_Deg);
                        AddLog($"[SAVE-LIMITS] Down Limit: {config.Down_Limit_Deg:F2}� (sent to VL[3])", LogType.Message);
                        
                        // Set VH[3] - Up Limit
                        _altitudeDrive.SetVelocityHighLimit(config.Up_Limit_Deg);
                        AddLog($"[SAVE-LIMITS] Up Limit: {config.Up_Limit_Deg:F2}� (sent to VH[3])", LogType.Message);
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[ERROR] Failed to set limits to Drive: {ex.Message}", LogType.Error);
                    }
                }
                else
                {
                    AddLog("[SAVE-LIMITS] Drive not connected - limits saved to AppSettings only", LogType.Warning);
                }
                
                // Set Motion Parameters to Drive if connected
                SetMotionParametersFromConfig();
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to save configuration: {ex.Message}", LogType.Error);
            }
        }

        private void LoadAxisConfiguration()
        {
            try
            {
                var config = AppSettings.GetAltitudeAxisConfig();
                
                txtPositionOffset.Text = config.Position_Offset_Deg.ToString("F2");
                txtEncoderResolution.Text = config.Encoder_Resolution.ToString();
                txtMaxVelocity.Text = config.Max_Velocity_Deg_Sec.ToString("F2");
                txtAcceleration.Text = config.Acceleration_Deg_Sec2.ToString("F2");
                txtDeceleration.Text = config.Deceleration_Deg_Sec2.ToString("F2");
                txtStopDeceleration.Text = config.Stop_Deceleration_Deg_Sec2.ToString("F2");
                txtSmooth.Text = config.Smooth_Factor_Ms.ToString("F1");
                txtLimitWarningDistance.Text = config.Limit_Warning_Distance_Deg.ToString("F2");
                txtTrackingSensitivity.Text = config.Tracking_Sensitivity_Deg.ToString("F2");
                
                // [NEW] Read Down/Up Limits from Drive (VL[3], VH[3]) when UI loads
                if (_centralizedPollingManager != null && AltitudeDriveHw != null && AltitudeDriveHw.IsConnected)
                {
                    ReadAndDisplayLimitsFromDrive();
                }
                else
                {
                    // Drive not connected - use AppSettings values
                    txtDownLimit.Text = config.Down_Limit_Deg.ToString("F2");
                    txtUpLimit.Text = config.Up_Limit_Deg.ToString("F2");
                    AddLog("[LOAD] Drive not connected - using limits from AppSettings", LogType.Message);
                }
                
                AddLog("[LOAD] Configuration loaded from AppSettings", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to load configuration: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Helper method to read VL[3] and VH[3] from Drive and display in UI
        /// Used by LoadAxisConfiguration() and BtnReadLimitsFromDrive_Click()
        /// </summary>
        private void ReadAndDisplayLimitsFromDrive()
        {
            try
            {
                var config = AppSettings.GetAltitudeAxisConfig();
                AddLog("[READ-LIMITS] Reading Down Limit (VL[3]) and Up Limit (VH[3]) from Drive...", LogType.Message);
                
                // Read VL[3] - Down Limit
                string responseVL;
                if (AltitudeDriveHw.SendRawCommand($"AX{AltitudeAxisNumber}.VL[3]", out responseVL, 1000))
                {
                    if (double.TryParse(responseVL.Trim(), out double vlCounts))
                    {
                        double downLimitDeg = (vlCounts / config.Encoder_Resolution) * 360.0;
                        txtDownLimit.Text = downLimitDeg.ToString("F2");
                        AddLog($"[READ-LIMITS] ? Down Limit: {downLimitDeg:F2}� (VL[3]={vlCounts:F0} counts)", LogType.Message);
                    }
                    else
                    {
                        AddLog($"[ERROR] Failed to parse VL[3] response: '{responseVL}'", LogType.Error);
                    }
                }
                else
                {
                    AddLog($"[ERROR] Failed to read VL[3] from Drive", LogType.Error);
                }
                
                // Read VH[3] - Up Limit
                string responseVH;
                if (AltitudeDriveHw.SendRawCommand($"AX{AltitudeAxisNumber}.VH[3]", out responseVH, 1000))
                {
                    if (double.TryParse(responseVH.Trim(), out double vhCounts))
                    {
                        double upLimitDeg = (vhCounts / config.Encoder_Resolution) * 360.0;
                        txtUpLimit.Text = upLimitDeg.ToString("F2");
                        AddLog($"[READ-LIMITS] ? Up Limit: {upLimitDeg:F2}� (VH[3]={vhCounts:F0} counts)", LogType.Message);
                    }
                    else
                    {
                        AddLog($"[ERROR] Failed to parse VH[3] response: '{responseVH}'", LogType.Error);
                    }
                }
                else
                {
                    AddLog($"[ERROR] Failed to read VH[3] from Drive", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to read limits from Drive: {ex.Message}", LogType.Error);
                var config = AppSettings.GetAltitudeAxisConfig();
                txtDownLimit.Text = config.Down_Limit_Deg.ToString("F2");
                txtUpLimit.Text = config.Up_Limit_Deg.ToString("F2");
                AddLog("[FALLBACK] Using limits from AppSettings instead", LogType.Warning);
            }
        }

        private void SetMotionParametersFromConfig()
        {
            // [FIX] Check CentralizedPollingManager instead of _altitudeDrive
            if (_centralizedPollingManager == null || AltitudeDriveHw == null || !AltitudeDriveHw.IsConnected)
            {
                AddLog("[MOTION] Drive not connected - Skip setting motion parameters", LogType.Message);
                return;
            }

            try
            {
                var config = AppSettings.GetAltitudeAxisConfig();
                
                AddLog("[MOTION] Queuing motion parameters for Drive (Axis 2)...", LogType.Message);
                AddLog($"[MOTION]   ACC={config.Acceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                AddLog($"[MOTION]   DCC={config.Deceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                AddLog($"[MOTION]   Stop DCC={config.Stop_Deceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                AddLog($"[MOTION]   Smooth={config.Smooth_Factor_Ms:F1} ms", LogType.Message);
                
                // Queue AC command (Acceleration)
                _centralizedPollingManager.EnqueueExtraCommand("AC[2]", async () =>
                {
                    string resp;
                    bool success = AltitudeDriveHw.SendRawCommand($"AC[{AltitudeAxisNumber}]={config.Acceleration_Deg_Sec2:F2}", out resp);
                    if (success)
                    {
                        AddLog($"[MOTION] Acceleration set: AC[2]={config.Acceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                    }
                    return success;
                });
                
                // Queue DC command (Deceleration)
                _centralizedPollingManager.EnqueueExtraCommand("DC[2]", async () =>
                {
                    string resp;
                    bool success = AltitudeDriveHw.SendRawCommand($"DC[{AltitudeAxisNumber}]={config.Deceleration_Deg_Sec2:F2}", out resp);
                    if (success)
                    {
                        AddLog($"[MOTION] Deceleration set: DC[2]={config.Deceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                    }
                    return success;
                });
                
                // Queue SD command (Stop Deceleration)
                _centralizedPollingManager.EnqueueExtraCommand("SD[2]", async () =>
                {
                    string resp;
                    bool success = AltitudeDriveHw.SendRawCommand($"SD[{AltitudeAxisNumber}]={config.Stop_Deceleration_Deg_Sec2:F2}", out resp);
                    if (success)
                    {
                        AddLog($"[MOTION] Stop Deceleration set: SD[2]={config.Stop_Deceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                    }
                    return success;
                });
                
                // Queue SM command (Smooth Factor)
                _centralizedPollingManager.EnqueueExtraCommand("SM[2]", async () =>
                {
                    string resp;
                    bool success = AltitudeDriveHw.SendRawCommand($"SM[{AltitudeAxisNumber}]={config.Smooth_Factor_Ms:F1}", out resp);
                    if (success)
                    {
                        AddLog($"[MOTION] Smooth Factor set: SM[2]={config.Smooth_Factor_Ms:F1} ms", LogType.Message);
                    }
                    return success;
                });
                
                AddLog($"[MOTION] 4 motion parameters queued for Axis 2", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[MOTION] ERROR: Failed to set motion parameters: {ex.Message}", LogType.Error);
            }
        }

        private double ParseDouble(string text, string fieldName)
        {
            if (double.TryParse(text, out double result))
            {
                return result;
            }
            throw new ArgumentException($"Invalid value for {fieldName}: {text}");
        }

        private long ParseLong(string text, string fieldName)
        {
            if (long.TryParse(text, out long result))
            {
                return result;
            }
            throw new ArgumentException($"Invalid value for {fieldName}: {text}");
        }

        /// <summary>
        /// Read Limits (VL[3], VH[3]) from Drive button handler
        /// </summary>
        private void BtnReadLimitsFromDrive_Click(object sender, RoutedEventArgs e)
        {
            // [FIX] Use CentralizedPollingManager's PlatinumDrive instead of _altitudeDrive
            if (_centralizedPollingManager == null || AltitudeDriveHw == null || !AltitudeDriveHw.IsConnected)
            {
                AddLog("[ERROR] Drive is not connected - Cannot read limits", LogType.Error);
                return;
            }

            // Use shared helper method
            ReadAndDisplayLimitsFromDrive();
        }

        #endregion

        #region Drive Control

        // NOTE: Enable/Disable and Stop buttons removed from Drive Status Tab
        // Drive control is now only available through Controller Tab and ManualControlWindow

        private void UpdateDriveEnableButton(bool enabled)
        {
            _isDriveEnabled = enabled;
            
            // Update Controller Tab button if it exists
            if (btnToggleAltDrive != null)
            {
                if (enabled)
                {
                    btnToggleAltDrive.Content = "Disable Drive";
                    btnToggleAltDrive.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                }
                else
                {
                    btnToggleAltDrive.Content = "Enable Drive";
                    btnToggleAltDrive.Background = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                }
            }
        }

        #endregion

        #region Controller Tab Event Handlers

        private void BtnToggleAltDrive_Click(object sender, RoutedEventArgs e)
        {
            if (_centralizedPollingManager == null)
            {
                AddLog("[ERROR] CentralizedPollingManager is not initialized", LogType.Error);
                return;
            }
            
            // Use the correct drive for Altitude
            var altDrive = _centralizedPollingManager.AltitudePlatinumDrive;
            bool isSeparateDrive = altDrive != _centralizedPollingManager.PlatinumDrive;
            int axisNumber = isSeparateDrive ? 1 : 2;
            
            if (altDrive == null || !altDrive.IsConnected)
            {
                AddLog("[ERROR] Altitude Drive is not connected", LogType.Error);
                return;
            }

            try
            {
                if (!_isDriveEnabled)
                {
                    altDrive.SetDriveEnable(true, axisNumber);
                    AddLog($"[ENABLE] Altitude Drive enabled (MO[{axisNumber}]=1)", LogType.Message);
                }
                else
                {
                    altDrive.SetDriveEnable(false, axisNumber);
                    AddLog($"[DISABLE] Altitude Drive disabled (MO[{axisNumber}]=0)", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to toggle drive enable: {ex.Message}", LogType.Error);
            }
        }


        private void BtnAltMoveToPosition1_Click(object sender, RoutedEventArgs e)
        {
            MoveToTargetPosition(1);
        }

        private void BtnAltMoveToPosition2_Click(object sender, RoutedEventArgs e)
        {
            MoveToTargetPosition(2);
        }

        private void BtnAltMoveToPosition_Click(object sender, RoutedEventArgs e)
        {
            // Get position from txtAltTargetPosition1 textbox
            if (!double.TryParse(txtAltTargetPosition1.Text, out double targetPosition))
            {
                AddLog("[ERROR] Invalid target position value", LogType.Error);
                return;
            }
            
            MoveToPosition(targetPosition);
        }
        
        /// <summary>
        /// Move to predefined target position (#1 or #2)
        /// </summary>
        private void MoveToTargetPosition(int positionNumber)
        {
            if (_centralizedPollingManager == null || AltitudeDriveHw == null || !AltitudeDriveHw.IsConnected)
            {
                AddLog("[ERROR] Drive is not connected", LogType.Error);
                return;
            }
            
            if (!_isDriveEnabled)
            {
                AddLog("[ERROR] Drive must be enabled before moving", LogType.Error);
                return;
            }
            
            // Get target position from textbox
            string textboxName = positionNumber == 1 ? "txtAltTargetPosition1" : "txtAltTargetPosition2";
            var textbox = positionNumber == 1 ? txtAltTargetPosition1 : txtAltTargetPosition2;
            
            if (!double.TryParse(textbox.Text, out double targetPositionDeg))
            {
                AddLog($"[ERROR] Invalid position value in {textboxName}", LogType.Error);
                return;
            }
            
            MoveToPosition(targetPositionDeg);
        }
        
        /// <summary>
        /// Move to specified position using Drive's Internal Position Control (UM=2)
        /// [NEW ARCHITECTURE] Sends PA (Position Absolute) command directly to Drive
        /// No software PD Controller - Drive handles position control internally
        /// </summary>
        private void MoveToPosition(double targetPositionDeg)
        {
            if (_centralizedPollingManager == null || AltitudeDriveHw == null || !AltitudeDriveHw.IsConnected)
            {
                AddLog("[ERROR] Drive is not connected", LogType.Error);
                return;
            }
            
            // [FIX] Check actual drive status from AltitudeInfo (not local _isDriveEnabled flag)
            bool actualDriveEnabled = false;
            if (_centralizedPollingManager?.AltitudeInfo != null)
            {
                actualDriveEnabled = _centralizedPollingManager.AltitudeInfo.Is_Drive_Enabled;
            }
            else
            {
                actualDriveEnabled = _altitudeInfo?.Is_Drive_Enabled ?? false;
            }
            
            if (!actualDriveEnabled)
            {
                AddLog($"[ERROR] Drive must be enabled before moving (Enabled: {actualDriveEnabled})", LogType.Error);
                return;
            }
            
            try
            {
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                
                // Get current position
                double currentPositionCounts = _altitudeInfo.Raw_Position;
                double currentPositionDeg = (currentPositionCounts / axisConfig.Encoder_Resolution) * 360.0;
                
                // Validate position limits
                if (targetPositionDeg < axisConfig.Down_Limit_Deg || targetPositionDeg > axisConfig.Up_Limit_Deg)
                {
                    AddLog($"[ERROR] Target position {targetPositionDeg:F2}� is outside limits [{axisConfig.Down_Limit_Deg:F2}�, {axisConfig.Up_Limit_Deg:F2}�]", LogType.Error);
                    return;
                }
                
                // Calculate error
                double error = targetPositionDeg - currentPositionDeg;
                
                // Get velocity from UI
                if (!double.TryParse(txtAltMoveVelocity.Text, out double velocityDegSec))
                {
                    velocityDegSec = axisConfig.Max_Velocity_Deg_Sec; // Use default if invalid
                    AddLog($"[WARNING] Invalid velocity value, using default: {velocityDegSec:F2}�/s", LogType.Warning);
                }
                
                AddLog($"[MOVE-POSITION] Moving to {targetPositionDeg:F2}� at {velocityDegSec:F2}�/s (Current: {currentPositionDeg:F2}�, Error: {error:F2}�)", LogType.Message);
                
                // Call AltitudeDrive.MoveToPosition() - sends PA command to Drive
                _altitudeDrive.MoveToPosition(targetPositionDeg, velocityDegSec);
                
                AddLog($"[MOVE-POSITION] PA command sent - Drive will handle position control internally", LogType.Message);
                
                // Set target for monitoring
                _targetPositionDeg = targetPositionDeg;
                _moveVelocityDegSec = velocityDegSec;
                
                // Start Position Monitor to track completion
                StartPositionMonitor();
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to move to position: {ex.Message}", LogType.Error);
            }
        }

        private void BtnAltStopMotion_Click(object sender, RoutedEventArgs e)
        {
            // [FIX] Use CentralizedPollingManager's PlatinumDrive
            if (_centralizedPollingManager == null || AltitudeDriveHw == null || !AltitudeDriveHw.IsConnected)
            {
                AddLog("[ERROR] Drive is not connected", LogType.Error);
                return;
            }

            try
            {
                // [CRITICAL] Reset target position to stop Position Monitor
                _targetPositionDeg = double.NaN;
                
                // Stop Position Monitor
                StopPositionMonitor();
                
                // Send ST command for immediate stop (Position Mode)
                string response;
                AltitudeDriveHw.SendRawCommand($"AX{AltitudeAxisNumber}.ST", out response, 1000);
                
                AddLog("[STOP] Altitude motion stopped (ST command sent)", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to stop motion: {ex.Message}", LogType.Error);
            }
        }
        
        /// <summary>
        /// Open Altitude Control Parameters Window (Settings button)
        /// </summary>
        private void BtnAltitudeControlSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var controlParamsWindow = new Altitude_Control_Parameters_Window(_centralizedPollingManager, _centralizedPollingManager.PlatinumDrive)
                {
                    Owner = Window.GetWindow(this)
                };

                controlParamsWindow.Show();
                AddLog("[INFO] Altitude Control Parameters window opened", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error opening altitude parameters window: {ex.Message}", LogType.Error);
            }
        }

        private void BtnAltMoveVelocity_Click(object sender, RoutedEventArgs e)
        {
            // [FIX] Use CentralizedPollingManager's PlatinumDrive
            if (_centralizedPollingManager == null || AltitudeDriveHw == null || !AltitudeDriveHw.IsConnected)
            {
                AddLog("[ERROR] Altitude Drive is not connected", LogType.Error);
                return;
            }

            if (!_isDriveEnabled)
            {
                AddLog("[ERROR] Drive must be enabled before moving", LogType.Error);
                return;
            }

            try
            {
                if (!double.TryParse(txtAltTargetVelocity.Text, out double velocityDegSec))
                {
                    AddLog("[ERROR] Invalid velocity value", LogType.Error);
                    return;
                }

                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                
                // Validate velocity is within limits
                if (Math.Abs(velocityDegSec) > axisConfig.Max_Velocity_Deg_Sec)
                {
                    AddLog($"[ERROR] Velocity {velocityDegSec:F2} deg/s exceeds limit ({axisConfig.Max_Velocity_Deg_Sec:F2} deg/s)", LogType.Error);
                    return;
                }

                // Convert to counts
                double velocityCounts = (velocityDegSec / 360.0) * axisConfig.Encoder_Resolution;

                string response;
                
                // [SAFER] Use JP (Jog Position) instead of JV (Jog Velocity)
                // JP respects VL[3] and VH[3] limits for hardware safety
                string jogCmd = $"AX{AltitudeAxisNumber}.JP={(long)velocityCounts}";
                if (!AltitudeDriveHw.SendRawCommand(jogCmd, out response, 1000))
                {
                    AddLog($"[ERROR] Failed to send JP command: {response}", LogType.Error);
                    return;
                }
                
                // [CRITICAL] Send BG command to begin motion
                string beginCmd = $"AX{AltitudeAxisNumber}.BG";
                AltitudeDriveHw.SendRawCommand(beginCmd, out response, 1000);
                
                AddLog($"[VELOCITY] Altitude velocity set to {velocityDegSec:F2} deg/s ({velocityCounts:F0} counts/s) [JP Mode - Limit Protected]", LogType.Message);
                AddLog($"[VELOCITY] Commands sent: {jogCmd} + {beginCmd}", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to set velocity: {ex.Message}", LogType.Error);
            }
        }

        private void BtnAltStopVelocity_Click(object sender, RoutedEventArgs e)
        {
            // [FIX] Use CentralizedPollingManager's PlatinumDrive
            if (_centralizedPollingManager == null || AltitudeDriveHw == null || !AltitudeDriveHw.IsConnected)
            {
                AddLog("[ERROR] Altitude Drive is not connected", LogType.Error);
                return;
            }

            try
            {
                string response;
                
                // [SAFER] Use JP=0 (Jog Position) to stop
                string jogCmd = $"AX{AltitudeAxisNumber}.JP=0";
                AltitudeDriveHw.SendRawCommand(jogCmd, out response, 1000);
                
                // [CRITICAL] Send BG to apply the stop
                string beginCmd = $"AX{AltitudeAxisNumber}.BG";
                AltitudeDriveHw.SendRawCommand(beginCmd, out response, 1000);
                
                AddLog("[STOP] Altitude velocity stopped (JP=0 + BG) [Limit Protected]", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to stop velocity: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Logging

        private enum LogType
        {
            Message,
            Error,
            Warning
        }

        private void AddLog(string message, LogType type)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string prefix = type switch
                {
                    LogType.Error => "[ERROR]",
                    LogType.Warning => "[WARNING]",
                    _ => "[INFO]"
                };
                
                string logEntry = $"{timestamp} {prefix} {message}\n";
                
                // NOTE: UI controls will be updated after XAML compilation completes
                // For now, just log to SimpleLogger
                
                // Log to SimpleLogger
                if (type == LogType.Error)
                {
                    SimpleLogger.Error(message, "Altitude");
                }
                else if (type == LogType.Warning)
                {
                    SimpleLogger.Warning(message, "Altitude");
                }
                else
                {
                    SimpleLogger.Info(message, "Altitude");
                }
            }), DispatcherPriority.Background);
        }

        #endregion

        #region Safe Shutdown

        /// <summary>
        /// Safe shutdown sequence - called when application is closing
        /// Stops all motion and disables drive safely
        /// </summary>
        public async System.Threading.Tasks.Task SafeShutdown()
        {
            AddLog("[SHUTDOWN] ==================== ALTITUDE SAFE SHUTDOWN START ====================", LogType.Message);
            
            // [DEBUG] Check if CentralizedPollingManager is available
            if (_centralizedPollingManager == null)
            {
                AddLog("[SHUTDOWN] ERROR: CentralizedPollingManager is NULL", LogType.Error);
                return;
            }
            
            // Determine which drive and axis to use for Altitude
            var altDrive = _centralizedPollingManager.AltitudePlatinumDrive;
            bool isSeparateDrive = altDrive != _centralizedPollingManager.PlatinumDrive;
            int axisNumber = isSeparateDrive ? 1 : 2;
            string axisPrefix = $"AX{axisNumber}";
            
            if (altDrive == null || !altDrive.IsConnected)
            {
                AddLog("[SHUTDOWN] INFO: Altitude Drive is not connected - no shutdown needed", LogType.Message);
                return;
            }

            try
            {
                // Step 1: Stop any motion
                AddLog($"[SHUTDOWN] Stopping Altitude motion ({axisPrefix})...", LogType.Message);
                await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        string response;
                        bool success = altDrive.SendRawCommand($"{axisPrefix}.ST", out response, 1000);
                        if (success)
                        {
                            AddLog($"[SHUTDOWN] Altitude motion stopped ({axisPrefix}.ST)", LogType.Message);
                        }
                        else
                        {
                            AddLog($"[SHUTDOWN] Failed to stop motion. Response: {response}", LogType.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[SHUTDOWN] Error stopping Altitude motion: {ex.Message}", LogType.Error);
                    }
                });
                await System.Threading.Tasks.Task.Delay(200);

                // Step 2: Stop Position Monitor
                AddLog("[SHUTDOWN] Stopping Position Monitor...", LogType.Message);
                StopPositionMonitor();
                await System.Threading.Tasks.Task.Delay(100);

                // Step 3: Stop UI Update
                AddLog("[SHUTDOWN] Stopping UI update...", LogType.Message);
                StopUIUpdate();
                await System.Threading.Tasks.Task.Delay(100);

                // Step 4: Disable Drive
                AddLog($"[SHUTDOWN] Disabling Altitude Drive ({axisPrefix}) with {axisPrefix}.MO=0...", LogType.Message);
                await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        string response;
                        bool success = altDrive.SendRawCommand($"{axisPrefix}.MO=0", out response, 2000);
                        if (success)
                        {
                            AddLog($"[SHUTDOWN] SUCCESS: Altitude Drive disabled ({axisPrefix}.MO=0)", LogType.Message);
                        }
                        else
                        {
                            AddLog($"[SHUTDOWN] ERROR: Failed to disable Altitude Drive. Response: {response}", LogType.Error);
                        }
                        
                        // [DEBUG] Verify drive status after disable
                        System.Threading.Thread.Sleep(100);
                        bool verifySuccess = altDrive.SendRawCommand($"MO[{axisNumber}]", out response, 1000);
                        if (verifySuccess)
                        {
                            AddLog($"[SHUTDOWN] VERIFY: MO[{axisNumber}] status = {response} (0=Disabled, 1=Enabled)", LogType.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[SHUTDOWN] ERROR: Exception disabling Altitude Drive: {ex.Message}", LogType.Error);
                    }
                });
                await System.Threading.Tasks.Task.Delay(300);

                AddLog("[SHUTDOWN] ==================== ALTITUDE SAFE SHUTDOWN COMPLETE ====================", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[SHUTDOWN ERROR] Altitude: {ex.Message}", LogType.Error);
            }
        }

        #endregion
    }
}
