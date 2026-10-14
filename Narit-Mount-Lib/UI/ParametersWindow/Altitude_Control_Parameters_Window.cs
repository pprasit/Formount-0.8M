using System;
using System.Windows;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Altitude_Control_Parameters_Window - Altitude Axis Configuration and Tuning
    /// Separate from Azimuth parameters (Axis 2 instead of Axis 1)
    /// </summary>
    public partial class Altitude_Control_Parameters_Window : Window
    {
        private CentralizedPollingManager _pollingManager;
        private PlatinumDrive _platinumDrive;
        private bool _isLoading = false;
        private int _axisNumber = 2; // Default: Axis 2 on shared drive
        private string _axisPrefix = "AX2"; // Axis command prefix

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
        
        // Default step sizes
        private const double VEL_GAIN_STEP = 1e-6;
        private const double VEL_INT_STEP = 0.1;
        private const double POS_GAIN_STEP = 0.1;
        private const double ACC_FF_STEP = 0.001;
        private const double VEL_FF_STEP = 0.01;

        public Altitude_Control_Parameters_Window(CentralizedPollingManager pollingManager, PlatinumDrive platinumDrive)
            : this(pollingManager, platinumDrive, 2)
        {
        }
        
        public Altitude_Control_Parameters_Window(CentralizedPollingManager pollingManager, PlatinumDrive platinumDrive, int axisNumber)
        {
            InitializeComponent();
            
            _pollingManager = pollingManager ?? throw new ArgumentNullException(nameof(pollingManager));
            _platinumDrive = platinumDrive ?? throw new ArgumentNullException(nameof(platinumDrive));
            _axisNumber = axisNumber;
            _axisPrefix = $"AX{axisNumber}";
            
            // Compute VELOCITY_GAIN_CONVERSION_FACTOR from Altitude Encoder Resolution
            var altConfig = AppSettings.GetAltitudeAxisConfig();
            VELOCITY_GAIN_CONVERSION_FACTOR = altConfig.Encoder_Resolution / 360.0;
            
            this.Loaded += Window_Loaded;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("[INIT] Altitude Control Parameters Window loaded", LogType.Message);
            
            // Load PD Controller parameters from AppSettings
            LoadPDControllerFromAppSettings();
            LoadPDControllerOffsetsFromAppSettings();
            LoadTuningOffsetsFromAppSettings();
            
            // Load Elmo Internal Loop parameters from Drive (Axis 2)
            LoadParametersFromDrive();
        }

        #region Load/Save PD Controller

        private void LoadPDControllerFromAppSettings()
        {
            try
            {
                _isLoading = true;
                
                var pdParams = AppSettings.GetPDControllerParameters();
                
                txtKp.Text = pdParams.Kp.ToString("F7");
                txtKi.Text = pdParams.Ki.ToString("F7");
                txtKd.Text = pdParams.Kd.ToString("F7");
                txtDeadband.Text = pdParams.Deadband.ToString("F7");
                
                // Load motion parameters from AltitudeAxisConfig
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                txtAcceleration.Text = axisConfig.Acceleration_Deg_Sec2.ToString("F7");
                txtDeceleration.Text = axisConfig.Deceleration_Deg_Sec2.ToString("F7");
                txtStopDeceleration.Text = axisConfig.Stop_Deceleration_Deg_Sec2.ToString("F7");
                
                AddLog("[LOAD] PD Controller parameters loaded from AppSettings", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[LOAD] ERROR: Failed to load PD parameters: {ex.Message}", LogType.Error);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void LoadPDControllerOffsetsFromAppSettings()
        {
            try
            {
                _isLoading = true;
                
                var offsets = AppSettings.GetTuningOffsets();
                
                txtKpOffset.Text = offsets.Kp_Offset.ToString("F6");
                txtKiOffset.Text = offsets.Ki_Offset.ToString("F6");
                txtKdOffset.Text = offsets.Kd_Offset.ToString("F6");
                txtDeadbandOffset.Text = offsets.Deadband_Offset.ToString("F6");
                txtAccelerationOffset.Text = offsets.Acceleration_Offset.ToString("F2");
                txtDecelerationOffset.Text = offsets.Deceleration_Offset.ToString("F2");
                txtStopDecelerationOffset.Text = offsets.Stop_Deceleration_Offset.ToString("F2");
                
                AddLog("[LOAD] PD Controller offsets loaded from AppSettings", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[LOAD] ERROR: Failed to load PD offsets: {ex.Message}", LogType.Error);
            }
            finally
            {
                _isLoading = false;
            }
        }

        #endregion

        #region Load/Save Tuning Offsets

        private void LoadTuningOffsetsFromAppSettings()
        {
            try
            {
                _isLoading = true;
                
                var offsets = AppSettings.GetTuningOffsets();
                
                txtVelocityGainOffset.Text = offsets.Velocity_Gain_Offset.ToString("E6");
                txtVelocityIntegralOffset.Text = offsets.Velocity_Integral_Offset.ToString("F6");
                txtPositionGainOffset.Text = offsets.Position_Gain_Offset.ToString("F6");
                txtAccFeedForwardOffset.Text = offsets.Acc_Feed_Forward_Offset.ToString("F6");
                txtVelFeedForwardOffset.Text = offsets.Vel_Feed_Forward_Offset.ToString("F6");
                
                AddLog("[LOAD] Tuning offsets loaded from AppSettings", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[LOAD] ERROR: Failed to load tuning offsets: {ex.Message}", LogType.Error);
            }
            finally
            {
                _isLoading = false;
            }
        }

        #endregion

        #region Logging

        private void AddLog(string message, LogType logType = LogType.Message)
        {
            switch (logType)
            {
                case LogType.Error:
                    SimpleLogger.Error(message, "Altitude");
                    break;
                case LogType.Cmd:
                    SimpleLogger.Command(message, "Altitude");
                    break;
                case LogType.Warning:
                    SimpleLogger.Warning(message, "Altitude");
                    break;
                default:
                    SimpleLogger.Info(message, "Altitude");
                    break;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Format number to standard decimal notation with 7 decimal places
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

        private double ParseDouble(string text, double defaultValue)
        {
            if (double.TryParse(text, out double result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// FF[1] is stored in Drive raw units. Convert it to the engineering unit
        /// shown in UI/EASII: A/deg/sec^2.
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

        #region Window Controls

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                this.DragMove();
            }
        }


        #endregion
    }
}
