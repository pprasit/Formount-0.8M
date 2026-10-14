using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Text.RegularExpressions;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Control;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Interaction logic for Azimuth_Control_Parameters_Window.xaml
    /// Main class - Core logic, fields, initialization
    /// </summary>
    public partial class Azimuth_Control_Parameters_Window : Window
    {
        #region Fields
        
        private bool _isLoading = false;
        private PlatinumDrive _platinumDrive;
        private AzimuthManager _azimuthDrive;
        private CentralizedPollingManager _pollingManager; // [NEW] For command queue
        private AzimuthPositionControl _positionControl; // [NEW] For real-time parameter updates

        // Velocity Gain Conversion Factor
        // 
        // Parameter: KP[2] = Velocity Loop Gain (Current Loop)
        // 
        // Conversion formula:
        // KP[2]_UI [A/(deg/sec)] = KP[2]_Drive [A/(cnt/sec)] * (Encoder_Resolution / 360)
        // KP[2]_Drive [A/(cnt/sec)] = KP[2]_UI [A/(deg/sec)] / (Encoder_Resolution / 360)
        //
        // VELOCITY_GAIN_CONVERSION_FACTOR = Encoder_Resolution / 360
        // Computed from AppSettings at initialization
        private double VELOCITY_GAIN_CONVERSION_FACTOR;

        // Step sizes for increment/decrement
        private const double KP_STEP = 0.01;
        private const double KI_STEP = 0.01;
        private const double KD_STEP = 0.01;
        private const double MAX_VEL_STEP = 0.5;
        private const double DEADBAND_STEP = 0.001;
        private const double VEL_GAIN_STEP = 1e-7;
        private const double VEL_INT_STEP = 0.01;
        private const double POS_GAIN_STEP = 0.01;
        private const double ACC_FF_STEP = 0.01;
        private const double VEL_FF_STEP = 0.01;
        private const double ACC_STEP = 10.0;
        private const double DCC_STEP = 10.0;
        private const double STOP_DCC_STEP = 10.0;
        private const double SMOOTH_STEP = 1.0;
        
        #endregion

        #region Constructor

        public Azimuth_Control_Parameters_Window()
        {
            InitializeComponent();
            
            // Compute VELOCITY_GAIN_CONVERSION_FACTOR from Azimuth Encoder Resolution
            var azConfig = AppSettings.GetAzimuthAxisConfig();
            VELOCITY_GAIN_CONVERSION_FACTOR = azConfig.Encoder_Resolution / 360.0;
            
            Loaded += Window_Loaded;
            Closing += Window_Closing;
        }

        #endregion

        #region Window Events

        /// <summary>
        /// Event when Window is loaded
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            
            AddLog("[WINDOW] Opening Control Parameters Window...");
            
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                AddLog("[WINDOW] MainWindow found");
                
                var azimuthController = FindAzimuthController(mainWindow);
                if (azimuthController != null)
                {
                    AddLog("[WINDOW] Azimuth_Controller found");
                    
                    // Access _azimuthDrive field
                    var azimuthDriveField = azimuthController.GetType().GetField("_azimuthDrive", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    // [NEW] Access _positionControl field
                    var positionControlField = azimuthController.GetType().GetField("_positionControl",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    if (azimuthDriveField != null)
                    {
                        _azimuthDrive = azimuthDriveField.GetValue(azimuthController) as AzimuthManager;
                        
                        _pollingManager = ResolvePollingManager(azimuthController);
                        if (_pollingManager != null)
                        {
                            AddLog("[WINDOW] CentralizedPollingManager retrieved - Command queue available");
                        }
                        else
                        {
                            AddLog("[WINDOW] WARNING: CentralizedPollingManager not available - queued Drive writes will be disabled", LogType.Error);
                        }
                        
                        if (positionControlField != null)
                        {
                            _positionControl = positionControlField.GetValue(azimuthController) as AzimuthPositionControl;
                            if (_positionControl != null)
                            {
                                AddLog("[WINDOW] AzimuthPositionControl retrieved - Real-time parameter updates available");
                            }
                            else
                            {
                                AddLog("[WINDOW] WARNING: PositionControl is null", LogType.Error);
                            }
                        }
                        else
                        {
                            AddLog("[WINDOW] WARNING: PositionControl field not found in Azimuth_Controller", LogType.Error);
                        }
                        
                        if (_azimuthDrive != null)
                        {
                            _platinumDrive = _azimuthDrive.PlatinumDrive;
                            string connectionStatus = _azimuthDrive.IsConnected ? "Connected" : "Disconnected";
                            string comPort = _azimuthDrive.IsConnected ? _azimuthDrive.ComPort : "N/A";
                            AddLog($"[WINDOW] AzimuthDrive retrieved (Status: {connectionStatus}, Port: {comPort})");
                            
                            // Calculate conversion factor from actual encoder resolution
                            var axisConfig = AppSettings.GetAzimuthAxisConfig();
                            VELOCITY_GAIN_CONVERSION_FACTOR = axisConfig.Encoder_Resolution / 360.0;
                            AddLog($"[WINDOW] Velocity Gain conversion factor calculated: {VELOCITY_GAIN_CONVERSION_FACTOR:E6} (Encoder: {axisConfig.Encoder_Resolution} counts/rev)");
                            
                            // Subscribe to FilterParametersUpdated event
                            _azimuthDrive.FilterParametersUpdated += OnFilterParametersUpdated;
                            
                            if (_azimuthDrive.IsConnected)
                            {
                                AddLog("[WINDOW] SUCCESS: Drive connection active - Loading parameters from Drive");
                                LoadParametersFromDrive();
                            }
                            else
                            {
                                AddLog("[WINDOW] WARNING: Drive not connected - Loading parameters from AppSettings", LogType.Error);
                                LoadParametersFromSettings();
                            }
                        }
                        else
                        {
                            AddLog("[WINDOW] ERROR: _azimuthDrive field is null", LogType.Error);
                        }
                    }
                    else
                    {
                        AddLog("[WINDOW] ERROR: _azimuthDrive field not found via reflection", LogType.Error);
                    }
                }
                else
                {
                    AddLog("[WINDOW] ERROR: Azimuth_Controller not found in visual tree", LogType.Error);
                }
            }
            else
            {
                AddLog("[WINDOW] ERROR: MainWindow is null", LogType.Error);
            }
            
            LoadOffsets();
            _isLoading = false;
            AddLog("[WINDOW] Control Parameters Window ready");
        }

        /// <summary>
        /// Event when window is closing
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Unsubscribe from events
            if (_azimuthDrive != null)
            {
                _azimuthDrive.FilterParametersUpdated -= OnFilterParametersUpdated;
                AddLog("[WINDOW] Unsubscribed from FilterParametersUpdated event");
            }
            AddLog("[WINDOW] Control Parameters Window closing");
        }

        /// <summary>
        /// Event handler for FilterParametersUpdated
        /// Updates UI when filter parameters are changed
        /// </summary>
        private void OnFilterParametersUpdated(object sender, AzimuthInfo info)
        {
            // Update UI on UI thread
            Dispatcher.Invoke(() =>
            {
                if (!_isLoading)
                {
                    UpdateFilterParametersUI(info);
                    AddLog("[WINDOW] Filter parameters updated from Drive event");
                }
            });
        }

        #endregion

        #region UI Events

        /// <summary>
        /// Close window
        /// </summary>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Close button click handler
        /// </summary>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Handle titlebar drag to move window
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                this.DragMove();
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Find Azimuth_Controller in visual tree
        /// </summary>
        private Azimuth_Controller FindAzimuthController(DependencyObject parent)
        {
            if (parent == null) return null;

            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                
                if (child is Azimuth_Controller controller)
                {
                    return controller;
                }
                
                var result = FindAzimuthController(child);
                if (result != null)
                {
                    return result;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Resolve the shared polling manager used by the main Azimuth controller.
        /// Uses the public property first, then the private field, and finally the
        /// global App fallback so this window always talks to the active command queue.
        /// </summary>
        private CentralizedPollingManager ResolvePollingManager(Azimuth_Controller azimuthController)
        {
            if (azimuthController != null)
            {
                var controllerType = azimuthController.GetType();
                const System.Reflection.BindingFlags publicFlags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                const System.Reflection.BindingFlags privateFlags =
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

                var property = controllerType.GetProperty("CentralizedPollingManager", publicFlags);
                if (property?.GetValue(azimuthController) is CentralizedPollingManager propertyManager)
                {
                    return propertyManager;
                }

                var field = controllerType.GetField("_centralizedPollingManager", privateFlags);
                if (field?.GetValue(azimuthController) is CentralizedPollingManager fieldManager)
                {
                    return fieldManager;
                }
            }

            return App.PollingManager;
        }

        /// <summary>
        /// Add Log Message
        /// </summary>
        private void AddLog(string message, LogType type = LogType.Message)
        {
            switch (type)
            {
                case LogType.Error:
                    SimpleLogger.Error(message, "Azimuth");
                    break;
                case LogType.Cmd:
                    SimpleLogger.Command(message, "Azimuth");
                    break;
                default:
                    SimpleLogger.Info(message, "Azimuth");
                    break;
            }
        }

        /// <summary>
        /// Parse double with default value
        /// </summary>
        private double ParseDouble(string text, double defaultValue)
        {
            return double.TryParse(text, out double result) ? result : defaultValue;
        }

        /// <summary>
        /// Load Offsets from AppSettings
        /// </summary>
        private void LoadOffsets()
        {
            try
            {
                var offsets = AppSettings.GetTuningOffsets();
                
                txtKpOffset.Text = offsets.Kp_Offset.ToString("F4");
                txtKiOffset.Text = offsets.Ki_Offset.ToString("F4");
                txtKdOffset.Text = offsets.Kd_Offset.ToString("F4");
                // [REMOVED] txtMaxVelocityOffset - Max Velocity now in Config tab
                txtDeadbandOffset.Text = offsets.Deadband_Offset.ToString("F4");
                
                // [FIX] Use F10 format for very small numbers like 0.000001 (1E-06)
                if (Math.Abs(offsets.Velocity_Gain_Offset) < 0.001)
                {
                    txtVelocityGainOffset.Text = offsets.Velocity_Gain_Offset.ToString("F10").TrimEnd('0').TrimEnd('.');
                }
                else
                {
                    txtVelocityGainOffset.Text = offsets.Velocity_Gain_Offset.ToString("F6").TrimEnd('0').TrimEnd('.');
                }
                
                txtVelocityIntegralOffset.Text = offsets.Velocity_Integral_Offset.ToString("F4");
                txtPositionGainOffset.Text = offsets.Position_Gain_Offset.ToString("F4");
                txtAccFeedForwardOffset.Text = offsets.Acc_Feed_Forward_Offset.ToString("F4");
                txtVelFeedForwardOffset.Text = offsets.Vel_Feed_Forward_Offset.ToString("F4");
                txtAccelerationOffset.Text = offsets.Acceleration_Offset.ToString("F2");
                txtDecelerationOffset.Text = offsets.Deceleration_Offset.ToString("F2");
                txtStopDecelerationOffset.Text = offsets.Stop_Deceleration_Offset.ToString("F2");
                // txtSmoothOffset removed
                
                AddLog("[OFFSET] Tuning offsets loaded from config file");
            }
            catch (Exception ex)
            {
                AddLog($"[OFFSET] ERROR: Failed to load offsets: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Numeric Validation

        /// <summary>
        /// Validate numeric input
        /// </summary>
        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex(@"^[0-9\-\+\.\,eE]+$");
            e.Handled = !regex.IsMatch(e.Text);
        }

        /// <summary>
        /// Format number to standard decimal notation (NO scientific notation E+XXX)
        /// Policy: NO_SCIENTIFIC_NOTATION_IN_UI_POLICY.md
        /// </summary>
        private string FormatToDecimal(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            if (double.TryParse(text, out double value))
            {
                // Always show 7 decimal places for Elmo parameters
                return value.ToString("F7");
            }

            return text;
        }

        /// <summary>
        /// FF[1] is stored in Drive raw units. Convert it to the engineering unit
        /// shown in UI/EASII: A/deg/sec^2.
        /// Current project convention:
        /// UI = DriveRaw * CountsPerDegree
        /// </summary>
        private double ConvertAccFeedForwardDriveToUi(double driveValue)
        {
            return driveValue * VELOCITY_GAIN_CONVERSION_FACTOR;
        }

        /// <summary>
        /// Convert UI engineering unit A/deg/sec^2 back to the raw FF[1] value
        /// expected by the Elmo Drive.
        /// </summary>
        private double ConvertAccFeedForwardUiToDrive(double uiValue)
        {
            return uiValue / VELOCITY_GAIN_CONVERSION_FACTOR;
        }

        #endregion
    }
}
