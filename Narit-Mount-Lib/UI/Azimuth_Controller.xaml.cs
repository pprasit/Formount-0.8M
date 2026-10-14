using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Control;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    public partial class Azimuth_Controller : UserControl
    {
        private AzimuthManager _azimuthDrive;
        private bool _isConnected = false;
        private bool _isDriveEnabled = false;
        private DispatcherTimer _pollingTimer;
        private AzimuthInfo _azimuthInfo;
        private int _pollingErrorCount = 0;
        private const int MAX_POLLING_ERRORS = 10;

        // [NEW] Centralized Polling Manager for both axes
        private CentralizedPollingManager _centralizedPollingManager;

        // PID Control Loop fields
        private AzimuthPositionControl _positionControl;
        
        // Limit warning tracking
        private bool _lastCCWWarningState = false;
        private bool _lastCWWarningState = false;
        
        // Limit exceeded tracking (to prevent log spam)
        private bool _lastCCWExceededState = false;
        private bool _lastCWExceededState = false;

        // Event to notify MainWindow about drive status changes
        public event EventHandler<bool> DriveStatusChanged;

        // Public property for external access (e.g., Manual Control Window)
        public AzimuthManager AzimuthDrive => _azimuthDrive;
        
        // [NEW] Public property for CentralizedPollingManager (for Altitude_Controller)
        public CentralizedPollingManager CentralizedPollingManager => _centralizedPollingManager;
        
        // Public method to get AzimuthInfo for satellite tracking
        public AzimuthInfo GetAzimuthInfo() => _azimuthInfo;
        
        // Public method to get AltitudeInfo (TODO: implement when AltitudeInfo exists)
        public AltitudeInfo GetAltitudeInfo()
        {
            // TODO: Get from Altitude_Controller
            return new AltitudeInfo();
        }

        /// <summary>
        /// Get current azimuth position in degrees
        /// [NEW] Returns position from MountInfo (already in degrees)
        /// </summary>
        public double GetCurrentPosition()
        {
            if (_centralizedPollingManager?.MountInfo != null)
            {
                return _centralizedPollingManager.MountInfo.Act_Azimuth_Pos_Raw;
            }
            
            // Fallback: calculate manually
            var config = AppSettings.GetAzimuthAxisConfig();
            double counts = _azimuthInfo?.Raw_Position ?? 0;
            return (counts / config.Encoder_Resolution) * 360.0;
        }

        /// <summary>
        /// Set velocity command for azimuth axis (for tracking)
        /// </summary>
        public void SetVelocity(double velocityDegPerSec)
        {
            if (_azimuthInfo != null)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = velocityDegPerSec;
                System.Diagnostics.Debug.WriteLine($"[AZIMUTH] Velocity set: {velocityDegPerSec:F3} deg/s");
            }
        }

        public Azimuth_Controller()
        {
            InitializeComponent();
            
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                return;
            }
            
            // Initialize with AzimuthDrive (wraps PlatinumDrive Axis 1)
            _azimuthDrive = new AzimuthManager();
            _azimuthInfo = new AzimuthInfo();
            
            // Initialize Position Control
            _positionControl = new AzimuthPositionControl(_azimuthDrive, _azimuthInfo);
            _positionControl.ControlError += OnPositionControlError;
            
            // Subscribe to TrackingManager to auto-stop PositionControl when tracking starts
            TrackingManager.Instance.TrackingStateChanged += OnTrackingStateChanged;
            
            UpdateDriveEnableButton(false);
            InitializePollingTimer();
            InitializeUIUpdateTimer(); // Add UI Update Timer
            LoadSettingsFromFile();
            
            this.Loaded += Azimuth_Controller_Loaded;
        }

        #region Polling Timer (Data Collection - Background Thread)

        private void InitializePollingTimer()
        {
            _pollingTimer = new DispatcherTimer();
            _pollingTimer.Interval = TimeSpan.FromMilliseconds(1); // 1ms = 1000 Hz
            _pollingTimer.Tick += PollingTimer_Tick;
            
            AddLog($"[INIT] Polling timer initialized: {_pollingTimer.Interval.TotalMilliseconds}ms ({1000.0/_pollingTimer.Interval.TotalMilliseconds:F0} Hz)", LogType.Message);
        }

        /// <summary>
        /// Polling Loop - Read data from Drive and write to AzimuthInfo
        /// NO UI UPDATES HERE! Just collect data to AzimuthInfo.
        /// UI Update Timer will read from AzimuthInfo separately.
        /// </summary>
        private void PollingTimer_Tick(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                return;
            }

            try
            {
                bool hasError = false;
                
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                // Read Position
                try
                {
                    double positionCounts = _azimuthDrive.ReadPosition();
                    _azimuthInfo.Raw_Position = positionCounts;
                }
                catch
                {
                    hasError = true;
                }

                // Read Velocity
                try
                {
                    double velocityCounts = _azimuthDrive.ReadVelocity();
                    _azimuthInfo.Raw_Velocity = velocityCounts;
                }
                catch
                {
                    hasError = true;
                }

                // Read Current
                try
                {
                    double current = _azimuthDrive.ReadCurrentIQ();
                    _azimuthInfo.Current_IQ = current;
                }
                catch
                {
                    hasError = true;
                }

                // Read Drive Enable Status
                try
                {
                    bool enabled = _azimuthDrive.ReadDriveEnabled();
                    _azimuthInfo.Is_Drive_Enabled = enabled;
                    _isDriveEnabled = enabled;
                }
                catch { }

                if (hasError)
                {
                    _pollingErrorCount++;
                    if (_pollingErrorCount >= MAX_POLLING_ERRORS)
                    {
                        AddLog($"[ERROR] Too many polling errors ({_pollingErrorCount}), stopping polling", LogType.Error);
                        StopPolling();
                    }
                    return;
                }

                _pollingErrorCount = 0;
            }
            catch (Exception ex)
            {
                _pollingErrorCount++;
                AddLog($"[ERROR] Polling error ({_pollingErrorCount}/{MAX_POLLING_ERRORS}): {ex.Message}", LogType.Error);
                
                if (_pollingErrorCount >= MAX_POLLING_ERRORS)
                {
                    AddLog("[ERROR] Too many errors, stopping polling", LogType.Error);
                    StopPolling();
                }
            }
        }

        private void StartPolling()
        {
            // Use AzimuthDrive's built-in polling (includes Command Sender Loop!)
            if (!_azimuthDrive.IsPolling)
            {
                _pollingErrorCount = 0;
                _azimuthDrive.StartPolling();
                AddLog($"[START] Drive polling started (includes Command Sender Loop @ 1000Hz)", LogType.Message);
            }
            else
            {
                AddLog("[INFO] Polling already running", LogType.Message);
            }
            
            // Start UI Update Timer separately
            StartUIUpdate();
        }

        private void StopPolling()
        {
            if (_azimuthDrive.IsPolling)
            {
                _azimuthDrive.StopPolling();
                AddLog("[STOP] Drive polling stopped", LogType.Message);
            }
            
            StopUIUpdate();
        }

        #endregion
        
        #region Initialize Chart with MountInfo
        
        /// <summary>
        /// Set MountInfo reference for chart panel to display corrected position
        /// Call this after CentralizedPollingManager is initialized
        /// </summary>
        public void InitializeChartWithMountInfo()
        {
            if (_centralizedPollingManager?.MountInfo != null && chartPanel != null)
            {
                chartPanel.SetMountInfo(_centralizedPollingManager.MountInfo);
                AddLog("[INFO] Chart panel initialized with MountInfo for corrected position display", LogType.Message);
            }
        }
        
        #endregion

        #region UI Update Timer (50ms = 20Hz)

        private DispatcherTimer _uiUpdateTimer;
        private const int UI_UPDATE_INTERVAL_MS = 50; // 20Hz - reduced to test UI smoothness

        private void InitializeUIUpdateTimer()
        {
            _uiUpdateTimer = new DispatcherTimer();
            _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(UI_UPDATE_INTERVAL_MS);
            _uiUpdateTimer.Tick += UIUpdateTimer_Tick;
            
            AddLog($"[INIT] UI update timer initialized: {UI_UPDATE_INTERVAL_MS}ms ({1000.0/UI_UPDATE_INTERVAL_MS:F0} Hz)", LogType.Message);
        }

        /// <summary>
        /// UI Update Loop - Read from AzimuthInfo and update UI
        /// Runs on UI thread at 10Hz (100ms interval)
        /// No blocking calls! Just read from AzimuthInfo and update UI.
        /// </summary>
        private void UIUpdateTimer_Tick(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                return;
            }

            try
            {
                // [FIX] Get SNAPSHOT of data from CentralizedPollingManager
                // Important: We must get a copy, not a reference to the object!
                // Otherwise UI will always see the same object reference and appear frozen
                if (_centralizedPollingManager != null)
                {
                    // Get snapshot by reading all values (this creates a copy via thread-safe property access)
                    var snapshot = _centralizedPollingManager.AzimuthInfo;
                    _azimuthInfo.Raw_Position = snapshot.Raw_Position;
                    _azimuthInfo.Raw_Velocity = snapshot.Raw_Velocity;
                    _azimuthInfo.Current_IQ = snapshot.Current_IQ;
                    _azimuthInfo.Command_Velocity_Deg_Sec = snapshot.Command_Velocity_Deg_Sec;
                    _azimuthInfo.Is_Drive_Enabled = snapshot.Is_Drive_Enabled;
                    _azimuthInfo.CCW_Sensor_Active = snapshot.CCW_Sensor_Active;
                    _azimuthInfo.CW_Sensor_Active = snapshot.CW_Sensor_Active;
                }
                else
                {
                    // Fallback to old method if CentralizedPollingManager not initialized
                    _azimuthInfo = _azimuthDrive?.CurrentInfo;
                }
                
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                // Read from AzimuthInfo (thread-safe)
                double positionCounts = _azimuthInfo.Raw_Position;
                double velocityCounts = _azimuthInfo.Raw_Velocity;
                double current = _azimuthInfo.Current_IQ;
                double cmdVel = _azimuthInfo.Command_Velocity_Deg_Sec;
                bool driveEnabled = _azimuthInfo.Is_Drive_Enabled;
                bool ccwSensorActive = _azimuthInfo.CCW_Sensor_Active;
                bool cwSensorActive = _azimuthInfo.CW_Sensor_Active;
                
                // [NEW] Get position in degrees from MountInfo (already converted)
                double positionDeg = 0;
                if (_centralizedPollingManager?.MountInfo != null)
                {
                    positionDeg = _centralizedPollingManager.MountInfo.Act_Azimuth_Pos_Raw;
                }
                else
                {
                    // Fallback: calculate manually if MountInfo not available
                    positionDeg = (positionCounts / axisConfig.Encoder_Resolution) * 360.0;
                }
                
                // Convert velocity to degrees
                double velocityDeg = (velocityCounts / axisConfig.Encoder_Resolution) * 360.0;
                
                // Update UI (already on UI thread - no Dispatcher needed!)
                txtPosition.Text = positionCounts.ToString("F0");
                txtPositionDeg.Text = positionDeg.ToString("F3");
                txtVelocity.Text = velocityCounts.ToString("F0");
                txtVelocityDeg.Text = velocityDeg.ToString("F4");
                txtCurrentIQ.Text = current.ToString("F3") + " A";
                
                // [NEW] Update Corrected Position from MountInfo
                if (_centralizedPollingManager?.MountInfo != null)
                {
                    double correctedPos = _centralizedPollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                    txtCorrectedPosition.Text = correctedPos.ToString("F3");
                }
                else
                {
                    txtCorrectedPosition.Text = "-";
                }
                
                UpdateDriveEnableButton(driveEnabled);
                
                // [NEW] Update sensor status indicators
                UpdateSensorStatus(ccwSensorActive, cwSensorActive);
                
                // [NEW] Check and warn about position limits
                CheckPositionLimits(positionDeg, velocityDeg, axisConfig);
                
                // Check Position Control status messages
                if (_positionControl != null && _positionControl.HasStatusMessage)
                {
                    string statusMsg = _positionControl.StatusMessage;
                    AddLog(statusMsg, LogType.Message);
                    _positionControl.ClearStatusMessage();
                }
                
                // [FIX] Enable buttons when in HOLD Mode OR when Position Control is not active
                // This allows user to command new positions while in Position Hold Mode
                if (_positionControl != null)
                {
                    bool shouldEnableButtons = !_positionControl.IsActive || _positionControl.IsInHoldMode;
                    
                    if (shouldEnableButtons && !btnMoveToPosition.IsEnabled)
                    {
                        btnMoveToPosition.IsEnabled = true;
                        btnMoveToPosition.Content = "Move to Position #1";
                        btnMoveToPosition1.IsEnabled = true;
                        btnMoveToPosition2.IsEnabled = true;
                        
                        if (_positionControl.IsInHoldMode)
                        {
                            AddLog("[UI] Buttons enabled - Ready for next position command (Position Hold Mode active)", LogType.Message);
                        }
                    }
                }
                
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

        private async void Azimuth_Controller_Loaded(object sender, RoutedEventArgs e)
        {
            this.Loaded -= Azimuth_Controller_Loaded;
            
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                AddLog("?? Design Mode - Skipping auto-connect", LogType.Message);
                return;
            }
            
            AddLog("?? Azimuth_Controller loaded", LogType.Message);
            await System.Threading.Tasks.Task.Delay(500);
            await AutoConnectDrive();
        }

        private async System.Threading.Tasks.Task AutoConnectDrive()
        {
            try
            {
                string serialNumber = AppSettings.GetDriveSerialNumber();
                
                if (string.IsNullOrEmpty(serialNumber))
                {
                    AddLog("[INFO] No Drive Serial Number configured. Please set it in settings.", LogType.Message);
                    return;
                }

                AddLog($"[CONNECT] Auto-connecting to Drive SN: {serialNumber}...", LogType.Message);

                var availableDrives = await System.Threading.Tasks.Task.Run(() => 
                    AzimuthManager.ScanAvailableDrives()
                );

                if (availableDrives.Count == 0)
                {
                    AddLog("[ERROR] No Platinum Drives detected on USB", LogType.Error);
                    ShowDriveConnectionError("No Platinum Drives detected on USB.\n\nPlease check:\n- Drive power cable\n- USB cable connection\n- USB driver installation");
                    return;
                }

                AddLog($"[INFO] Found {availableDrives.Count} drive(s):", LogType.Message);
                foreach (var drive in availableDrives)
                {
                    AddLog($"   · {drive.Key}: SN {drive.Value}", LogType.Message);
                }

                // If configured SN not found but only 1 drive available, use that drive
                bool snFound = availableDrives.Any(d => d.Value == serialNumber);
                if (!snFound && availableDrives.Count == 1)
                {
                    var onlyDrive = availableDrives.First();
                    AddLog($"[INFO] Configured SN '{serialNumber}' not found. Auto-connecting to only available drive: SN {onlyDrive.Value} on {onlyDrive.Key}", LogType.Message);
                    serialNumber = onlyDrive.Value;
                    AppSettings.SaveDriveSerialNumber(serialNumber);
                    if (txtDriveSN != null) txtDriveSN.Text = serialNumber;
                }

                bool success = false;
                try
                {
                    success = await System.Threading.Tasks.Task.Run(() =>
                    {
                        return _azimuthDrive.ConnectBySerialNumber(serialNumber);
                    });
                }
                catch (Exception connectEx)
                {
                    AddLog($"[ERROR] Connection exception: {connectEx.Message}", LogType.Error);
                    ShowDriveConnectionError($"Failed to connect to Drive with Serial Number: {serialNumber}\n\n{connectEx.Message}\n\nFound drives:\n{string.Join("\n", availableDrives.Select(d => $"  - {d.Key}: SN {d.Value}"))}\n\nPlease check:\n- Serial Number in AppSetting.json\n- Drive power and USB connection");
                    return;
                }

                if (success)
                {
                    AppSettings.UseSettingsProfileForDriveSerialNumber(_azimuthDrive.SerialNumber);
                    _isConnected = true;
                    UpdateConnectionStatus(true);
                    txtComPort.Text = _azimuthDrive.ComPort;
                    AddLog($"[SUCCESS] Connected to SN: {_azimuthDrive.SerialNumber} on {_azimuthDrive.ComPort}", LogType.Message);
                    AddLog("[INFO] Drive connected but NOT enabled. Click 'Enable Drive' button when ready.", LogType.Message);
                    
                    CheckDriveStatus();
                    
                    // Set Motion Parameters from AppSettings after successful connection
                    await System.Threading.Tasks.Task.Delay(200);
                    SetMotionParametersFromConfig();
                    
                    // [NEW] Initialize CentralizedPollingManager (replaces StartPolling())
                    AddLog("[INFO] Initializing CentralizedPollingManager for both axes...", LogType.Message);
                    InitializeCentralizedPollingManager();
                    
                    StartUIUpdate(); // Start UI Update Timer
                    
                    // Initialize Altitude Controller with CentralizedPollingManager
                    await System.Threading.Tasks.Task.Delay(200);
                    InitializeAltitudeController();
                }
                else
                {
                    AddLog($"[ERROR] Failed to connect to Drive SN: {serialNumber}", LogType.Error);
                    ShowDriveConnectionError($"Failed to connect to Drive with Serial Number: {serialNumber}\n\nFound drives:\n{string.Join("\n", availableDrives.Select(d => $"  - {d.Key}: SN {d.Value}"))}\n\nPlease check:\n- Serial Number in AppSetting.json\n- Drive power and USB connection");
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Auto-connect failed: {ex.Message}", LogType.Error);
                ShowDriveConnectionError($"Drive connection failed:\n\n{ex.Message}\n\nPlease check:\n- Drive power cable\n- USB cable connection\n- COM port availability");
            }
        }

        /// <summary>
        /// Show Drive Connection Error MessageBox with OK/Cancel options
        /// OK = Continue without drive
        /// Cancel = Close application
        /// </summary>
        private void ShowDriveConnectionError(string errorMessage)
        {
            var result = MessageBox.Show(
                errorMessage,
                "Drive Connection Error",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Error
            );

            if (result == MessageBoxResult.Cancel)
            {
                // User clicked Cancel - close application
                AddLog("[INFO] User chose to close application", LogType.Message);
                Application.Current.Shutdown();
            }
            else
            {
                // User clicked OK - continue without drive
                AddLog("[INFO] User chose to continue without drive connection", LogType.Message);
            }
        }

        private void TestImmediateRead()
        {
            try
            {
                AddLog("[INFO] Testing immediate read...", LogType.Message);
                
                double position = _azimuthDrive.ReadPosition();
                AddLog($"[INFO] Position: {position} counts", LogType.Message);
                
                double velocity = _azimuthDrive.ReadVelocity();
                AddLog($"[INFO] Velocity: {velocity} counts/s", LogType.Message);
                
                double current = _azimuthDrive.ReadCurrentIQ();
                AddLog($"[INFO] Current: {current} A", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Test read error: {ex.Message}", LogType.Error);
            }
        }

        #region Settings Management

        private void LoadSettingsFromFile()
        {
            try
            {
                if (AppSettings.SettingsFileExists())
                {
                    string serialNumber = AppSettings.GetDriveSerialNumber();
                    int baudRate = AppSettings.GetAzimuthBaudRate();
                    int pollingInterval = AppSettings.GetAzimuthPollingInterval();

                    if (txtDriveSN != null)
                    {
                        txtDriveSN.Text = serialNumber;
                    }

                    if (cmbBaudRate != null)
                    {
                        foreach (ComboBoxItem item in cmbBaudRate.Items)
                        {
                            if (item.Content.ToString() == baudRate.ToString())
                            {
                                cmbBaudRate.SelectedItem = item;
                                break;
                            }
                        }
                    }

                    if (txtPollingInterval != null)
                    {
                        // ? ????????? settings ???? default 1ms
                        int actualPollingInterval = pollingInterval > 0 ? pollingInterval : 1;
                        txtPollingInterval.Text = actualPollingInterval.ToString();
                        
                        // Update timer interval
                        if (_pollingTimer != null)
                        {
                            _pollingTimer.Interval = TimeSpan.FromMilliseconds(actualPollingInterval);
                        }
                    }

                    LoadAxisConfiguration();

                    AddLog($"?? Settings loaded | SN: {serialNumber} | Baud: {baudRate} | Polling: {_pollingTimer?.Interval.TotalMilliseconds:F0}ms", LogType.Message);
                }
                else
                {
                    AddLog("?? No settings file found, using defaults (1ms polling = 1000 Hz)", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"? Error loading settings: {ex.Message}", LogType.Error);
            }
        }

        private void LoadAxisConfiguration()
        {
            try
            {
                var axisConfig = AppSettings.GetAzimuthAxisConfig();

                txtPositionOffset.Text = axisConfig.Position_Offset_Deg.ToString("F2");
                txtCCWLimit.Text = axisConfig.CCW_Limit_Deg.ToString("F2");
                txtCWLimit.Text = axisConfig.CW_Limit_Deg.ToString("F2");
                txtEncoderResolution.Text = axisConfig.Encoder_Resolution.ToString();
                txtMaxVelocity.Text = axisConfig.Max_Velocity_Deg_Sec.ToString("F2");
                
                // Load Motion Parameters
                txtAcceleration.Text = axisConfig.Acceleration_Deg_Sec2.ToString("F2");
                txtDeceleration.Text = axisConfig.Deceleration_Deg_Sec2.ToString("F2");
                txtStopDeceleration.Text = axisConfig.Stop_Deceleration_Deg_Sec2.ToString("F2");
                txtSmooth.Text = axisConfig.Smooth_Factor_Ms.ToString("F1");
                
                // Load Limit Warning Distance
                txtLimitWarningDistance.Text = axisConfig.Limit_Warning_Distance_Deg.ToString("F2");
                
                // Load Tracking Sensitivity
                txtTrackingSensitivity.Text = axisConfig.Tracking_Sensitivity_Deg.ToString("F2");

                var tuningParams = AppSettings.GetAzimuthTuningParameters();
                txtVelocityGain.Text = tuningParams.Velocity_Gain.ToString("E6");
                txtVelocityIntegral.Text = tuningParams.Velocity_Integral.ToString("F4");
                txtPositionGain.Text = tuningParams.Position_Gain.ToString("F4");
                txtAccFeedForward.Text = tuningParams.Acc_Feed_Forward.ToString("F4");
                txtVelFeedForward.Text = tuningParams.Vel_Feed_Forward.ToString("F4");

                var tuningSteps = AppSettings.GetTuningSteps();
                txtVelocityGainStep.Text = tuningSteps.Velocity_Gain_Step.ToString("E6");
                txtVelocityIntegralStep.Text = tuningSteps.Velocity_Integral_Step.ToString("F4");
                txtPositionGainStep.Text = tuningSteps.Position_Gain_Step.ToString("F1");
                txtAccFeedForwardStep.Text = tuningSteps.Acc_Feed_Forward_Step.ToString("E6");
                txtVelFeedForwardStep.Text = tuningSteps.Vel_Feed_Forward_Step.ToString("F1");
            }
            catch (Exception ex)
            {
                AddLog($"? Error loading axis configuration: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Connection Management

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                try
                {
                    AddLog("?? Connecting to Platinum Drive...", LogType.Message);

                    string serialNumber = txtDriveSN?.Text?.Trim();
                    if (string.IsNullOrEmpty(serialNumber))
                        serialNumber = AppSettings.GetDriveSerialNumber();

                    if (string.IsNullOrEmpty(serialNumber))
                    {
                        AddLog("? No Drive serial number configured", LogType.Error);
                        return;
                    }

                    // Save SN to AppSettings if it differs
                    if (serialNumber != AppSettings.GetDriveSerialNumber())
                    {
                        AppSettings.SaveDriveSerialNumber(serialNumber);
                        AddLog($"[INFO] Saved new Drive SN: {serialNumber}", LogType.Message);
                    }

                    bool success = await System.Threading.Tasks.Task.Run(() =>
                    {
                        return _azimuthDrive.ConnectBySerialNumber(serialNumber);
                    });

                    if (success)
                    {
                        AppSettings.UseSettingsProfileForDriveSerialNumber(_azimuthDrive.SerialNumber);
                        _isConnected = true;
                        UpdateConnectionStatus(true);
                        txtComPort.Text = _azimuthDrive.ComPort;
                        AddLog($"? Connected to {_azimuthDrive.SerialNumber} on {_azimuthDrive.ComPort}", LogType.Message);
                        AddLog("?? Drive connected but NOT enabled. Click 'Enable Drive' button to enable.", LogType.Message);
                        
                        CheckDriveStatus();
                        StartPolling();
                        StartUIUpdate(); // Start UI Update Timer
                        
                }
                }
                catch (Exception ex)
                {
                    AddLog($"? Connection failed: {ex.Message}", LogType.Error);
                    UpdateConnectionStatus(false);
                }
            }
            else
            {
                DisconnectDrive();
            }
        }

        private void DisconnectDrive()
        {
            try
            {
                StopPolling();
                StopUIUpdate(); // Stop UI Update Timer
                
                if (_azimuthDrive != null && _isConnected)
                {
                    if (_isDriveEnabled)
                    {
                        AddLog("[INFO] Disabling drive before disconnect...", LogType.Message);
                        try
                        {
                            _azimuthDrive.PlatinumDrive.SetDriveEnable(false, 1); // Axis 1 = Azimuth
                            _isDriveEnabled = false;
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[WARNING] Could not disable drive: {ex.Message}", LogType.Error);
                        }
                    }
                    
                    _azimuthDrive.Disconnect();
                    _isConnected = false;
                    UpdateConnectionStatus(false);
                    UpdateDriveEnableButton(false);
                    txtComPort.Text = "-";
                    
                    txtPosition.Text = "-";
                    txtVelocity.Text = "-";
                    txtCurrentIQ.Text = "-";
                    
                    AddLog("[SUCCESS] Disconnected", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error disconnecting: {ex.Message}", LogType.Error);
            }
        }

        private void UpdateConnectionStatus(bool isConnected)
        {
            if (isConnected)
            {
                btnConnect.Content = "Disconnect";
                txtConnectionStatus.Text = "Connected";
                statusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                tabConnectionIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
            else
            {
                btnConnect.Content = "Connect";
                txtConnectionStatus.Text = "Disconnected";
                statusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                tabConnectionIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
        }

        private void CheckDriveStatus()
        {
            try
            {
                if (!_isConnected)
                    return;

                _isDriveEnabled = _azimuthDrive.ReadDriveEnabled();
                UpdateDriveEnableButton(_isDriveEnabled);
                
                // Notify MainWindow about initial status
                DriveStatusChanged?.Invoke(this, _isDriveEnabled);
                
                if (_isDriveEnabled)
                {
                    AddLog("[INFO] Drive is currently ENABLED", LogType.Message);
                }
                else
                {
                    AddLog("[INFO] Drive is currently DISABLED", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error checking drive status: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Settings Buttons

        private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int baudRate = GetSelectedBaudRate();
                
                int pollingInterval = 1; // default
                if (txtPollingInterval != null && !string.IsNullOrEmpty(txtPollingInterval.Text))
                {
                    if (int.TryParse(txtPollingInterval.Text, out int parsedInterval))
                    {
                        if (parsedInterval >= 1 && parsedInterval <= 1000)
                        {
                            pollingInterval = parsedInterval;
                        }
                        else
                        {
                            AddLog($"[WARNING] Polling interval must be between 1-1000ms, using default 1ms", LogType.Message);
                            pollingInterval = 1;
                        }
                    }
                }

                AppSettings.SaveAzimuthBaudRate(baudRate);
                AppSettings.SaveAzimuthPollingInterval(pollingInterval);

                // Update timer interval
                bool wasRunning = _pollingTimer.IsEnabled;
                if (wasRunning)
                {
                    StopPolling();
                }
                
                _pollingTimer.Interval = TimeSpan.FromMilliseconds(pollingInterval);
                
                if (wasRunning)
                {
                    StartPolling();
                }

                AddLog($"[CONFIG] Settings saved successfully (Polling: {pollingInterval}ms = {1000.0/pollingInterval:F0} Hz)", LogType.Message);

                if (_isConnected)
                {
                    AddLog("[CONFIG] Applying settings...", LogType.Message);
                    await System.Threading.Tasks.Task.Delay(500);
                    AddLog("[SUCCESS] Settings applied", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error saving settings: {ex.Message}", LogType.Error);
            }
        }

        private void BtnSaveAxisConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var axisConfig = new AzimuthAxisConfig
                {
                    Position_Offset_Deg = ParseDouble(txtPositionOffset.Text, "Position Offset"),
                    CCW_Limit_Deg = ParseDouble(txtCCWLimit.Text, "CCW Limit"),
                    CW_Limit_Deg = ParseDouble(txtCWLimit.Text, "CW Limit"),
                    Encoder_Resolution = AppSettings.GetAzimuthAxisConfig().Encoder_Resolution,
                    Max_Control_Velocity_Deg_Sec = ParseDouble(txtMaxControlVelocity.Text, "Max Control Velocity"),
                    Max_Acceleration_Deg_Sec2 = ParseDouble(txtMaxAcceleration.Text, "Max Acceleration"),
                    Current_Limit_Amp = ParseDouble(txtCurrentLimit.Text, "Current Limit"),
                    Max_Velocity_Deg_Sec = ParseDouble(txtMaxVelocity.Text, "Max Velocity"),
                    Time_Duration_Ms = ParseDouble(txtTimeDuration.Text, "Time Duration"),
                    
                    // Save Motion Parameters
                    Acceleration_Deg_Sec2 = ParseDouble(txtAcceleration.Text, "Acceleration"),
                    Deceleration_Deg_Sec2 = ParseDouble(txtDeceleration.Text, "Deceleration"),
                    Stop_Deceleration_Deg_Sec2 = ParseDouble(txtStopDeceleration.Text, "Stop Deceleration"),
                    Smooth_Factor_Ms = ParseDouble(txtSmooth.Text, "Smooth Factor"),
                    
                    // Save Limit Warning Distance
                    Limit_Warning_Distance_Deg = ParseDouble(txtLimitWarningDistance.Text, "Limit Warning Distance"),
                    
                    // Save Tracking Sensitivity
                    Tracking_Sensitivity_Deg = ParseDouble(txtTrackingSensitivity.Text, "Tracking Sensitivity")
                };

                AppSettings.SaveAzimuthAxisConfig(axisConfig);

                var tuningParams = new TuningParameters
                {
                    Velocity_Gain = ParseDouble(txtVelocityGain.Text, "Velocity Gain"),
                    Velocity_Integral = ParseDouble(txtVelocityIntegral.Text, "Velocity Integral"),
                    Position_Gain = ParseDouble(txtPositionGain.Text, "Position Gain"),
                    Acc_Feed_Forward = ParseDouble(txtAccFeedForward.Text, "Acc Feed Forward"),
                    Vel_Feed_Forward = ParseDouble(txtVelFeedForward.Text, "Vel Feed Forward")
                };

                AppSettings.SaveAzimuthTuningParameters(tuningParams);

                var tuningSteps = new TuningSteps
                {
                    Velocity_Gain_Step = ParseDouble(txtVelocityGainStep.Text, "Velocity Gain Step"),
                    Velocity_Integral_Step = ParseDouble(txtVelocityIntegralStep.Text, "Velocity Integral Step"),
                    Position_Gain_Step = ParseDouble(txtPositionGainStep.Text, "Position Gain Step"),
                    Acc_Feed_Forward_Step = ParseDouble(txtAccFeedForwardStep.Text, "Acc Feed Forward Step"),
                    Vel_Feed_Forward_Step = ParseDouble(txtVelFeedForwardStep.Text, "Vel Feed Forward Step")
                };

                AppSettings.SaveTuningSteps(tuningSteps);

                AddLog("[CONFIG] Axis configuration saved successfully", LogType.Message);
                
                // Set Motion Parameters to Drive if connected
                SetMotionParametersFromConfig();
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error saving axis config: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Set Motion Parameters (AC, DC, SD, SF) to Drive from AppSettings
        /// Called when: 1) Connected to Drive, 2) Save Configuration
        /// </summary>
        private void SetMotionParametersFromConfig()
        {
            if (!_isConnected)
            {
                AddLog("[MOTION] Drive not connected - Skip setting motion parameters", LogType.Message);
                return;
            }

            try
            {
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                // DEBUG: Log actual values read from AppSettings
                AddLog($"[DEBUG] AppSettings values:", LogType.Message);
                AddLog($"[DEBUG]   Encoder Resolution: {axisConfig.Encoder_Resolution}", LogType.Message);
                AddLog($"[DEBUG]   ACC from file: {axisConfig.Acceleration_Deg_Sec2} deg/s�", LogType.Message);
                AddLog($"[DEBUG]   DCC from file: {axisConfig.Deceleration_Deg_Sec2} deg/s�", LogType.Message);
                AddLog($"[DEBUG]   Stop DCC from file: {axisConfig.Stop_Deceleration_Deg_Sec2} deg/s�", LogType.Message);
                AddLog($"[DEBUG]   Smooth from file: {axisConfig.Smooth_Factor_Ms} ms", LogType.Message);
                
                AddLog("[MOTION] Setting motion parameters to Drive...", LogType.Message);
                AddLog($"[MOTION]   ACC={axisConfig.Acceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                AddLog($"[MOTION]   DCC={axisConfig.Deceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                AddLog($"[MOTION]   Stop DCC={axisConfig.Stop_Deceleration_Deg_Sec2:F2} deg/s�", LogType.Message);
                AddLog($"[MOTION]   Smooth={axisConfig.Smooth_Factor_Ms:F1} ms", LogType.Message);
                
                _azimuthDrive.SetMotionParameters(
                    axisConfig.Acceleration_Deg_Sec2,
                    axisConfig.Deceleration_Deg_Sec2,
                    axisConfig.Stop_Deceleration_Deg_Sec2,
                    axisConfig.Smooth_Factor_Ms
                );
                
                AddLog("[MOTION] SUCCESS: Motion parameters set to Drive", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[MOTION] ERROR: Failed to set motion parameters: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Public Methods for MainWindow

        /// <summary>
        /// Toggle drive enable/disable called from MainWindow Title Bar
        /// </summary>
        public async System.Threading.Tasks.Task ToggleDriveFromMainWindow(bool enable)
        {
            if (!_isConnected)
            {
                AddLog("[ERROR] Drive not connected", LogType.Error);
                return;
            }

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    _azimuthDrive.PlatinumDrive.SetDriveEnable(enable, 1);
                });
                
                await System.Threading.Tasks.Task.Delay(100);
                
                bool actualState = await System.Threading.Tasks.Task.Run(() =>
                {
                    return _azimuthDrive.ReadDriveEnabled();
                });
                
                _isDriveEnabled = actualState;
                UpdateDriveEnableButton(actualState);
                
                // Notify MainWindow about status change
                DriveStatusChanged?.Invoke(this, actualState);
                
                if (actualState)
                {
                    AddLog("[SUCCESS] Drive ENABLED from Title Bar", LogType.Message);
                }
                else
                {
                    AddLog("[SUCCESS] Drive DISABLED from Title Bar", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error toggling drive from Title Bar: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Emergency stop called from MainWindow Title Bar
        /// </summary>
        public async System.Threading.Tasks.Task EmergencyStopFromMainWindow()
        {
            if (!_isConnected)
            {
                AddLog("[ERROR] Drive not connected", LogType.Error);
                return;
            }
            
            try
            {
                AddLog("[EMERGENCY] EMERGENCY STOP from Title Bar!", LogType.Error);
                
                // 1. Stop Position Control
                if (_positionControl != null && _positionControl.IsActive)
                {
                    _positionControl.Stop();
                    AddLog("[EMERGENCY] Position control stopped", LogType.Error);
                }
                
                // 2. Set Command Velocity to 0
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                AddLog("[EMERGENCY] Command velocity set to 0", LogType.Error);
                
                // 3. Send ST command
                await System.Threading.Tasks.Task.Run(() =>
                {
                    _azimuthDrive.StopMotion();
                });
                
                AddLog("[EMERGENCY] ST command sent - All motion stopped", LogType.Error);
                AddLog("[SUCCESS] Emergency stop complete", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Emergency stop failed: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Get current AzimuthInfo for MainWindow to read sensor status
        /// </summary>
        public AzimuthInfo GetCurrentAzimuthInfo()
        {
            if (_azimuthDrive != null && _isConnected)
            {
                return _azimuthDrive.CurrentInfo;
            }
            return _azimuthInfo; // Return local copy if not connected
        }

        /// <summary>
        /// Safe shutdown sequence - called when application is closing
        /// Stops all motion, disables drive, and disconnects safely
        /// </summary>
        public async System.Threading.Tasks.Task SafeShutdown()
        {
            if (!_isConnected)
            {
                AddLog("[SHUTDOWN] Drive not connected - no shutdown needed", LogType.Message);
                return;
            }

            try
            {
                AddLog("[SHUTDOWN] ==================== SAFE SHUTDOWN START ====================", LogType.Message);
                
                // Step 1: Stop Position Control if active
                if (_positionControl != null && _positionControl.IsActive)
                {
                    AddLog("[SHUTDOWN] Stopping Position Control...", LogType.Message);
                    _positionControl.Stop();
                    await System.Threading.Tasks.Task.Delay(100);
                }

                // Step 2: Stop Velocity Control by setting command velocity to 0
                AddLog("[SHUTDOWN] Stopping Velocity Control (Command_Velocity = 0)...", LogType.Message);
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                await System.Threading.Tasks.Task.Delay(100);

                // Step 3: Send ST (Stop Motion) command
                AddLog("[SHUTDOWN] Sending ST (Stop Motion) command...", LogType.Message);
                await System.Threading.Tasks.Task.Run(() =>
                {
                    _azimuthDrive.StopMotion();
                });
                await System.Threading.Tasks.Task.Delay(200);

                // Step 4: Disable Drive
                AddLog("[SHUTDOWN] Disabling Drive (MO=0)...", LogType.Message);
                await System.Threading.Tasks.Task.Run(() =>
                {
                    _azimuthDrive.PlatinumDrive.SetDriveEnable(false, 1);
                });
                await System.Threading.Tasks.Task.Delay(200);

                // Step 5: Stop Polling
                AddLog("[SHUTDOWN] Stopping Polling Loop...", LogType.Message);
                _azimuthDrive.StopPolling();
                await System.Threading.Tasks.Task.Delay(100);

                // Step 6: Disconnect
                AddLog("[SHUTDOWN] Disconnecting from Drive...", LogType.Message);
                _azimuthDrive.Disconnect();

                AddLog("[SHUTDOWN] ==================== SAFE SHUTDOWN COMPLETE ====================", LogType.Message);
                
                _isConnected = false;
                _isDriveEnabled = false;
            }
            catch (Exception ex)
            {
                AddLog($"[SHUTDOWN ERROR] {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Stop Position Control for Manual Mode
        /// Called from Manual Control Window when user starts manual movement
        /// </summary>
        public void StopPositionControlForManualMode()
        {
            if (_positionControl != null && _positionControl.IsActive)
            {
                _positionControl.Stop();
                AddLog("[MANUAL] Position Control stopped - Manual mode active", LogType.Cmd);
            }
        }

        /// <summary>
        /// Auto-stop PositionControl when any tracking starts via TrackingManager.
        /// Prevents two control loops (PositionControl + Tracking Engine) from
        /// writing conflicting velocity commands simultaneously.
        /// </summary>
        private void OnTrackingStateChanged(object sender, TrackingStateChangedEventArgs e)
        {
            if (e.IsTracking && _positionControl != null && _positionControl.IsActive)
            {
                Dispatcher.Invoke(() =>
                {
                    _positionControl.Stop();
                    AddLog($"[TRACKING] Position Control auto-stopped � {e.ObjectType} tracking started", LogType.Cmd);
                });
            }
        }

        #endregion

        #region Controller Buttons

        private async void BtnToggleDrive_Click(object sender, RoutedEventArgs e)
        {
            await ToggleDriveFromMainWindow(!_isDriveEnabled);
        }

        private async void BtnStopMotion_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("[ERROR] Drive not connected", LogType.Error);
                return;
            }
            
            try
            {
                AddLog("[STOP] ==================== STOP ALL MOTION ====================", LogType.Cmd);
                
                // Step 1: Stop Position Control (including Hold Mode)
                if (_positionControl != null && _positionControl.IsActive)
                {
                    _positionControl.Stop();
                    AddLog("[STOP] Position Control stopped (Hold Mode disabled)", LogType.Cmd);
                }
                
                // Step 2: Stop Velocity Control by setting command velocity to 0
                AddLog("[STOP] Stopping Velocity Control (Command_Velocity = 0)...", LogType.Cmd);
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                await System.Threading.Tasks.Task.Delay(50);
                
                // Step 3: Send ST (Stop Motion) command to Drive
                AddLog("[STOP] Sending ST (Stop Motion) command to Drive...", LogType.Cmd);
                await System.Threading.Tasks.Task.Run(() =>
                {
                    _azimuthDrive.StopMotion();
                });
                
                AddLog("[SUCCESS] All motion stopped - Motor will coast to a stop", LogType.Message);
                AddLog("[INFO] Current will drop to zero - Motor is no longer maintaining position", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Stop motion failed: {ex.Message}", LogType.Error);
            }
        }

        private void BtnMoveToPosition1_Click(object sender, RoutedEventArgs e)
        {
            MoveToTargetPosition(1);
        }

        private void BtnMoveToPosition2_Click(object sender, RoutedEventArgs e)
        {
            MoveToTargetPosition(2);
        }

        private void BtnMoveToPosition_Click(object sender, RoutedEventArgs e)
        {
            // Get position from txtTargetPosition1 textbox
            if (!double.TryParse(txtTargetPosition1.Text, out double targetPosition))
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
            // [FIX] Check actual drive status from AzimuthInfo (not local _isDriveEnabled flag)
            bool actualDriveEnabled = false;
            if (_centralizedPollingManager?.AzimuthInfo != null)
            {
                actualDriveEnabled = _centralizedPollingManager.AzimuthInfo.Is_Drive_Enabled;
            }
            else
            {
                actualDriveEnabled = _azimuthInfo?.Is_Drive_Enabled ?? false;
            }
            
            if (!_isConnected || !actualDriveEnabled)
            {
                AddLog($"[ERROR] Cannot move - Drive not connected or not enabled (Connected: {_isConnected}, Enabled: {actualDriveEnabled})", LogType.Error);
                return;
            }
            
            // Get target position from textbox
            string textboxName = positionNumber == 1 ? "txtTargetPosition1" : "txtTargetPosition2";
            var textbox = positionNumber == 1 ? txtTargetPosition1 : txtTargetPosition2;
            
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
            // [FIX] Check actual drive status from AzimuthInfo (not local _isDriveEnabled flag)
            bool actualDriveEnabled = false;
            if (_centralizedPollingManager?.AzimuthInfo != null)
            {
                actualDriveEnabled = _centralizedPollingManager.AzimuthInfo.Is_Drive_Enabled;
            }
            else
            {
                actualDriveEnabled = _azimuthInfo?.Is_Drive_Enabled ?? false;
            }
            
            if (!_isConnected || !actualDriveEnabled)
            {
                AddLog($"[ERROR] Cannot move - Drive not connected or not enabled (Connected: {_isConnected}, Enabled: {actualDriveEnabled})", LogType.Error);
                return;
            }
            
            try
            {
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                // Get current position
                double currentPositionCounts = _azimuthInfo.Raw_Position;
                double currentPositionDeg = (currentPositionCounts / axisConfig.Encoder_Resolution) * 360.0;
                
                // Validate position limits
                if (targetPositionDeg < axisConfig.CCW_Limit_Deg || targetPositionDeg > axisConfig.CW_Limit_Deg)
                {
                    AddLog($"[ERROR] Target position {targetPositionDeg:F2}� is outside limits [{axisConfig.CCW_Limit_Deg:F2}�, {axisConfig.CW_Limit_Deg:F2}�]", LogType.Error);
                    return;
                }
                
                // Calculate error
                double error = targetPositionDeg - currentPositionDeg;
                
                // Get velocity from UI
                if (!double.TryParse(txtMoveVelocity.Text, out double velocityDegSec))
                {
                    velocityDegSec = axisConfig.Max_Velocity_Deg_Sec; // Use default if invalid
                    AddLog($"[WARNING] Invalid velocity value, using default: {velocityDegSec:F2}�/s", LogType.Warning);
                }
                
                AddLog($"[MOVE-POSITION] Moving to {targetPositionDeg:F2}� at {velocityDegSec:F2}�/s (Current: {currentPositionDeg:F2}�, Error: {error:F2}�)", LogType.Message);
                
                // Call AzimuthDrive.MoveToPosition() - sends PA command to Drive
                _azimuthDrive.MoveToPosition(targetPositionDeg, velocityDegSec);
                
                AddLog($"[MOVE-POSITION] PA command sent - Drive will handle position control internally", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to move to position: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Position Control Event Handlers
        
        private void OnPositionControlError(object sender, string message)
        {
            System.Diagnostics.Debug.WriteLine($"[PositionControl] Error: {message}");
        }
        
        #endregion

        #region Velocity Control and Settings

        private async void BtnMoveVelocity_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || !_isDriveEnabled)
            {
                AddLog("[ERROR] Drive not connected or not enabled", LogType.Error);
                return;
            }
            
            try
            {
                if (!double.TryParse(txtTargetVelocity.Text, out double velocityDegSec))
                {
                    AddLog("[ERROR] Invalid target velocity", LogType.Error);
                    return;
                }
                
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
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
                // JP respects position limits for hardware safety
                string jogCmd = $"AX{PlatinumDrive.Axis.AZIMUTH}.JP={(long)velocityCounts}";
                if (!_azimuthDrive.PlatinumDrive.SendRawCommand(jogCmd, out response, 1000))
                {
                    AddLog($"[ERROR] Failed to send JP command: {response}", LogType.Error);
                    return;
                }
                
                // [CRITICAL] Send BG command to begin motion
                string beginCmd = $"AX{PlatinumDrive.Axis.AZIMUTH}.BG";
                _azimuthDrive.PlatinumDrive.SendRawCommand(beginCmd, out response, 1000);
                
                AddLog($"[VELOCITY] Azimuth velocity set to {velocityDegSec:F2} deg/s ({velocityCounts:F0} counts/s) [JP Mode - Limit Protected]", LogType.Message);
                AddLog($"[VELOCITY] Commands sent: {jogCmd} + {beginCmd}", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Move velocity failed: {ex.Message}", LogType.Error);
            }
            finally
            {
                btnMoveVelocity.IsEnabled = true;
            }
        }

        private async void BtnStopVelocity_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AddLog("[ERROR] Drive not connected", LogType.Error);
                return;
            }
            
            try
            {
                AddLog("[STOP] Stopping velocity motion...", LogType.Cmd);
                
                btnStopVelocity.IsEnabled = false;
                
                string response;
                
                // [SAFER] Use JP=0 (Jog Position) to stop
                string jogCmd = $"AX{PlatinumDrive.Axis.AZIMUTH}.JP=0";
                _azimuthDrive.PlatinumDrive.SendRawCommand(jogCmd, out response, 1000);
                
                // [CRITICAL] Send BG to apply the stop
                string beginCmd = $"AX{PlatinumDrive.Axis.AZIMUTH}.BG";
                _azimuthDrive.PlatinumDrive.SendRawCommand(beginCmd, out response, 1000);
                
                AddLog("[STOP] Azimuth velocity stopped (JP=0 + BG) [Limit Protected]", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Stop velocity failed: {ex.Message}", LogType.Error);
            }
            finally
            {
                btnStopVelocity.IsEnabled = true;
            }
        }

        private void BtnPositionControlSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var controlParamsWindow = new Azimuth_Control_Parameters_Window
                {
                    Owner = Window.GetWindow(this)
                };

                controlParamsWindow.Show();
                AddLog("[INFO] Control parameters window opened", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error opening control parameters window: {ex.Message}", LogType.Error);
            }
        }

        private void BtnVelocityControlSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var controlParamsWindow = new Azimuth_Control_Parameters_Window
                {
                    Owner = Window.GetWindow(this)
                };

                controlParamsWindow.Show();
                AddLog("[INFO] Control parameters window opened", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Error opening control parameters window: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        private void UpdateDriveEnableButton(bool isEnabled)
        {
            if (btnToggleDrive == null)
                return;

            if (isEnabled)
            {
                btnToggleDrive.Content = "Disable Drive";
                btnToggleDrive.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                
                if (driveStatusIndicator != null)
                    driveStatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                
                if (txtDriveStatus != null)
                    txtDriveStatus.Text = "Enabled";
            }
            else
            {
                btnToggleDrive.Content = "Enable Drive";
                btnToggleDrive.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                
                if (driveStatusIndicator != null)
                    driveStatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                
                if (txtDriveStatus != null)
                    txtDriveStatus.Text = "Disabled";
            }
        }

        /// <summary>
        /// Check position limits and warn if approaching limits
        /// </summary>
        private void CheckPositionLimits(double positionDeg, double velocityDeg, AzimuthAxisConfig axisConfig)
        {
            double warningThreshold = axisConfig.Limit_Warning_Distance_Deg * 2.0; // Warning at 2x Limit Warning Distance
            
            // Check CCW Limit (lower limit)
            double distanceFromCCW = positionDeg - axisConfig.CCW_Limit_Deg;
            bool ccwWarning = distanceFromCCW <= warningThreshold && distanceFromCCW > 0;
            
            if (ccwWarning && !_lastCCWWarningState)
            {
                // Approaching CCW limit - silently track (Drive handles limits)
                _lastCCWWarningState = true;
            }
            else if (!ccwWarning && _lastCCWWarningState)
            {
                _lastCCWWarningState = false;
            }
            
            // Check CW Limit (upper limit)
            double distanceFromCW = axisConfig.CW_Limit_Deg - positionDeg;
            bool cwWarning = distanceFromCW <= warningThreshold && distanceFromCW > 0;
            
            if (cwWarning && !_lastCWWarningState)
            {
                // Approaching CW limit - silently track (Drive handles limits)
                _lastCWWarningState = true;
            }
            else if (!cwWarning && _lastCWWarningState)
            {
                _lastCWWarningState = false;
            }
            
            // Check if already exceeded limits (silently track state changes)
            bool ccwExceeded = positionDeg < axisConfig.CCW_Limit_Deg;
            if (ccwExceeded && !_lastCCWExceededState)
            {
                // CCW limit exceeded - silently track (Drive handles limits)
                _lastCCWExceededState = true;
            }
            else if (!ccwExceeded && _lastCCWExceededState)
            {
                // CCW limit cleared - silently track (Drive handles limits)
                _lastCCWExceededState = false;
            }
            
            bool cwExceeded = positionDeg > axisConfig.CW_Limit_Deg;
            if (cwExceeded && !_lastCWExceededState)
            {
                // CW limit exceeded - silently track (Drive handles limits)
                _lastCWExceededState = true;
            }
            else if (!cwExceeded && _lastCWExceededState)
            {
                // CW limit cleared - silently track (Drive handles limits)
                _lastCWExceededState = false;
            }
        }

        /// <summary>
        /// Update sensor status indicators in UI
        /// </summary>
        private void UpdateSensorStatus(bool ccwSensorActive, bool cwSensorActive)
        {
            try
            {
                // Update CCW Sensor indicator (both ellipse and text)
                if (ccwSensorIndicator != null)
                {
                    if (ccwSensorActive)
                    {
                        ccwSensorIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    }
                    else
                    {
                        ccwSensorIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)); // Gray
                    }
                }
                
                if (txtCCWSensor != null)
                {
                    if (ccwSensorActive)
                    {
                        txtCCWSensor.Text = "Active";
                        txtCCWSensor.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    }
                    else
                    {
                        txtCCWSensor.Text = "Inactive";
                        txtCCWSensor.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)); // Light Gray (normal text)
                    }
                }
                
                // Update CW Sensor indicator (both ellipse and text)
                if (cwSensorIndicator != null)
                {
                    if (cwSensorActive)
                    {
                        cwSensorIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    }
                    else
                    {
                        cwSensorIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)); // Gray
                    }
                }
                
                if (txtCWSensor != null)
                {
                    if (cwSensorActive)
                    {
                        txtCWSensor.Text = "Active";
                        txtCWSensor.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                    }
                    else
                    {
                        txtCWSensor.Text = "Inactive";
                        txtCWSensor.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)); // Light Gray (normal text)
                    }
                }
                
                // Update Chart Panel sensor indicators (buttons at top-right of chart)
                if (chartPanel != null)
                {
                    chartPanel.UpdateSensorIndicators(ccwSensorActive, cwSensorActive);
                }
            }
            catch (Exception ex)
            {
                // Silently ignore UI update errors
                System.Diagnostics.Debug.WriteLine($"[UpdateSensorStatus] Error: {ex.Message}");
            }
        }

        private double ParseDouble(string text, string paramName)
        {
            if (double.TryParse(text, out double value))
            {
                return value;
            }
            else
            {
                throw new ArgumentException($"Invalid {paramName}: '{text}'");
            }
        }

        private long ParseLong(string text, string paramName)
        {
            if (long.TryParse(text, out long value))
            {
                return value;
            }
            else
            {
                throw new ArgumentException($"Invalid {paramName}: '{text}'");
            }
        }

        private int GetSelectedBaudRate()
        {
            if (cmbBaudRate.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Content.ToString(), out int baudRate))
                {
                    return baudRate;
                }
            }
            return 0;
        }

        private void AddLog(string message, LogType type)
        {
            // Use SimpleLogger instead of lstLog
            switch (type)
            {
                case LogType.Cmd:
                    SimpleLogger.Command(message, "Azimuth");
                    break;
                case LogType.Error:
                    SimpleLogger.Error(message, "Azimuth");
                    break;
                case LogType.Message:
                default:
                    SimpleLogger.Info(message, "Azimuth");
                    break;
            }
        }

        #region Public Methods for Manual Control

        /// <summary>
        /// Move to specified position with specified velocity
        /// Called from Manual Control Window or external controllers
        /// </summary>
        public void MoveToPosition(double targetPositionDeg, double velocityDegSec)
        {
            if (!_isConnected || !_isDriveEnabled)
            {
                AddLog("[MOVE TO POS] Drive not connected or not enabled", LogType.Error);
                return;
            }

            try
            {
                // Validate position limits
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                // Debug: Log exact values
                AddLog($"[MOVE TO POS] DEBUG: CCW_Limit={axisConfig.CCW_Limit_Deg:F2}, CW_Limit={axisConfig.CW_Limit_Deg:F2}, Target={targetPositionDeg:F2}", LogType.Message);
                
                if (targetPositionDeg < axisConfig.CCW_Limit_Deg || targetPositionDeg > axisConfig.CW_Limit_Deg)
                {
                    AddLog($"[MOVE TO POS] ERROR: Target position {targetPositionDeg:F2}deg exceeds limits [CCW:{axisConfig.CCW_Limit_Deg:F2}deg, CW:{axisConfig.CW_Limit_Deg:F2}deg]", LogType.Error);
                    return;
                }

                // Get PID parameters
                var pdParams = AppSettings.GetPDControllerParameters();

                // Stop if already running
                if (_positionControl.IsActive)
                {
                    _positionControl.Stop();
                }

                // Start position control
                _positionControl.Start(
                    targetPositionDeg,
                    velocityDegSec,
                    pdParams.Kp,
                    pdParams.Ki,
                    pdParams.Kd,
                    pdParams.Deadband
                );

                AddLog($"[MOVE TO POS] Started - Target: {targetPositionDeg:F2}deg, Velocity: {velocityDegSec:F1}deg/s", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[MOVE TO POS] Error: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Initialize Altitude Controller

        /// <summary>
        /// Initialize CentralizedPollingManager for both Azimuth and Altitude axes
        /// Replaces individual polling loops in AzimuthDrive and AltitudeDrive
        /// </summary>
        private void InitializeCentralizedPollingManager()
        {
            try
            {
                if (_azimuthDrive?.PlatinumDrive == null)
                {
                    AddLog("[ERROR] PlatinumDrive not available", LogType.Error);
                    return;
                }
                
                var runtime = MountRuntimeBootstrap.InitializeConnectedRuntime(_azimuthDrive.PlatinumDrive);
                _centralizedPollingManager = runtime.PollingManager;
                AddLog("[GLOBAL] Shared runtime bootstrap initialized", LogType.Message);
                
                // [CRITICAL] Set CentralizedPollingManager reference in Position Control
                _positionControl?.SetPollingManager(_centralizedPollingManager);
                
                // Subscribe to Azimuth data updates
                _centralizedPollingManager.AzimuthDataUpdated += OnCentralizedAzimuthDataUpdated;
                
                // Subscribe to Altitude data updates (for Altitude_Controller to use later)
                _centralizedPollingManager.AltitudeDataUpdated += OnCentralizedAltitudeDataUpdated;
                
                // Start centralized polling @ 200Hz
                _centralizedPollingManager.StartPolling();
                LoadAxisConfiguration();
                
                AddLog("[SUCCESS] CentralizedPollingManager started @ 200Hz (5ms)", LogType.Message);
                AddLog("[INFO] Concurrent async polling for 2 axes enabled", LogType.Message);
                AddLog("[INFO] Position Control linked to CentralizedPollingManager", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to initialize CentralizedPollingManager: {ex.Message}", LogType.Error);
            }
        }
        
        /// <summary>
        /// Test Axis 2 direct read to verify command format
        /// </summary>
        private void TestAxis2DirectRead()
        {
            try
            {
                var drive = _azimuthDrive.PlatinumDrive;
                string response;
                
                // Test 1: Read Position PX[2]
                bool success1 = drive.SendRawCommand("PX[2]", out response, 1000);
                AddLog($"[TEST] PX[2] ? Success={success1}, Response='{response}'", LogType.Message);
                
                // Test 2: Read Velocity VX[2]
                bool success2 = drive.SendRawCommand("VX[2]", out response, 1000);
                AddLog($"[TEST] VX[2] ? Success={success2}, Response='{response}'", LogType.Message);
                
                // Test 3: Read Motor Status MO[2]
                bool success3 = drive.SendRawCommand("MO[2]", out response, 1000);
                AddLog($"[TEST] MO[2] ? Success={success3}, Response='{response}'", LogType.Message);
                
                if (!success1 || !success2 || !success3)
                {
                    AddLog("[INFO] Axis 2 direct test did not respond on this drive. This probe is optional and does not block operation.", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[INFO] Axis 2 direct test skipped: {ex.Message}", LogType.Message);
            }
        }
        
        /// <summary>
        /// Event handler for Azimuth data updates from CentralizedPollingManager
        /// </summary>
        private void OnCentralizedAzimuthDataUpdated(object sender, AzimuthInfo azimuthInfo)
        {
            // [FIX] Don't just assign reference! Copy values to local _azimuthInfo
            // This event runs @ 200Hz, we just need to update our local copy
            _azimuthInfo.Raw_Position = azimuthInfo.Raw_Position;
            _azimuthInfo.Raw_Velocity = azimuthInfo.Raw_Velocity;
            _azimuthInfo.Current_IQ = azimuthInfo.Current_IQ;
            _azimuthInfo.Command_Velocity_Deg_Sec = azimuthInfo.Command_Velocity_Deg_Sec;
            _azimuthInfo.Is_Drive_Enabled = azimuthInfo.Is_Drive_Enabled;
            _azimuthInfo.CCW_Sensor_Active = azimuthInfo.CCW_Sensor_Active;
            _azimuthInfo.CW_Sensor_Active = azimuthInfo.CW_Sensor_Active;
            
            // Note: UI updates happen in UIUpdateTimer_Tick @ 10Hz (100ms)
            // This just receives data @ 200Hz (5ms) from polling manager
        }
        
        /// <summary>
        /// Event handler for Altitude data updates from CentralizedPollingManager
        /// This is just a placeholder - Altitude_Controller will subscribe to this event
        /// </summary>
        private void OnCentralizedAltitudeDataUpdated(object sender, AltitudeInfo altitudeInfo)
        {
            // Placeholder - Altitude_Controller will handle this
            // We just need to keep the event alive
        }

        /// <summary>
        /// Initialize Altitude Controller with CentralizedPollingManager
        /// Called after Azimuth successfully connects to Drive
        /// 
        /// POLICY: NEVER auto-enable Axis 2! Only read status.
        /// User must manually enable axes when ready.
        /// </summary>
        private void InitializeAltitudeController()
        {
            try
            {
                AddLog("[ALTITUDE] Initializing Altitude Controller with CentralizedPollingManager...", LogType.Message);
                
                // Find Altitude_Controller in MainWindow
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow == null)
                {
                    AddLog("[ALTITUDE] ERROR: MainWindow not found", LogType.Error);
                    return;
                }
                
                AddLog("[ALTITUDE] MainWindow found", LogType.Message);
                
                // Find Altitude_Controller
                var altitudeController = FindVisualChild<Altitude_Controller>(mainWindow);
                if (altitudeController == null)
                {
                    AddLog("[ALTITUDE] ERROR: Altitude_Controller not found in visual tree", LogType.Error);
                    return;
                }
                
                AddLog("[ALTITUDE] Altitude_Controller found", LogType.Message);
                
                // Share CentralizedPollingManager with Altitude
                if (_centralizedPollingManager != null)
                {
                    AddLog($"[ALTITUDE] CentralizedPollingManager is ready (Polling: {_centralizedPollingManager.IsPolling})", LogType.Message);
                    
                    altitudeController.InitializeWithCentralizedPollingManager(_centralizedPollingManager);
                    
                    AddLog("[ALTITUDE] SUCCESS: Altitude Controller initialized with CentralizedPollingManager", LogType.Message);
                    AddLog("[ALTITUDE] User must manually enable Axis 2 when ready", LogType.Message);
                }
                else
                {
                    AddLog("[ALTITUDE] ERROR: CentralizedPollingManager is null", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[ALTITUDE] ERROR: Failed to initialize Altitude Controller: {ex.Message}", LogType.Error);
                AddLog($"[ALTITUDE] Stack: {ex.StackTrace}", LogType.Error);
            }
        }
        
        /// <summary>
        /// Test read Axis 2 availability (WITHOUT enabling it!)
        /// This is a read-only check to see if Drive recognizes Axis 2
        /// </summary>
        private void TestAxis2Availability()
        {
            try
            {
                var drive = _azimuthDrive.PlatinumDrive;
                string response;
                
                AddLog("[ALTITUDE] Testing Axis 2 availability (read-only)...", LogType.Message);
                
                // Test 1: Read Motor Status MO[2] (should work even if disabled)
                bool success1 = drive.SendRawCommand("MO[2]", out response, 1000);
                if (success1)
                {
                    AddLog($"[ALTITUDE] MO[2] status: '{response ?? "null"}' (0=Disabled, 1=Enabled)", LogType.Message);
                    
                    if (response?.Trim() == "0")
                    {
                        AddLog("[ALTITUDE] Axis 2 is recognized but DISABLED (expected)", LogType.Message);
                    }
                    else if (response?.Trim() == "1")
                    {
                        AddLog("[ALTITUDE] Axis 2 is already ENABLED", LogType.Message);
                    }
                }
                else
                {
                    AddLog($"[ALTITUDE] INFO: MO[2] probe did not respond. Response='{response ?? "null"}'", LogType.Message);
                    AddLog("[ALTITUDE] INFO: Axis 2 availability probe is optional and does not block operation.", LogType.Message);
                    return;
                }
                
                // Test 2: Try reading position (might fail if axis is not configured)
                bool success2 = drive.SendRawCommand("PX[2]", out response, 1000);
                if (success2 && double.TryParse(response?.Trim(), out double position))
                {
                    AddLog($"[ALTITUDE] PX[2] = {position:F0} counts (Axis 2 encoder active)", LogType.Message);
                }
                else
                {
                    AddLog($"[ALTITUDE] INFO: PX[2] read failed - may need configuration in EAS Studio", LogType.Message);
                }
                
                AddLog("[ALTITUDE] Availability check complete", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[ALTITUDE] INFO: Axis 2 availability probe skipped: {ex.Message}", LogType.Message);
            }
        }
        
        /// <summary>
        /// Helper method to find visual child in tree
        /// </summary>
        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                T childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }

        #endregion
    }
}
