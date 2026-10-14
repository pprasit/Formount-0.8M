using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    public partial class SiderealTuningWindow : Window
    {
        private readonly SiderealTracking _trackingController;
        private bool _isLoading = true; // Prevent live-apply during LoadGains

        public SiderealTuningWindow(SiderealTracking trackingController)
        {
            InitializeComponent();
            _trackingController = trackingController ?? throw new ArgumentNullException(nameof(trackingController));
            LoadGains();
            
            // Wire up live-apply on LostFocus (apply when user finishes editing a field)
            txtAzKp.LostFocus += OnPidfGainChanged;
            txtAzKi.LostFocus += OnPidfGainChanged;
            txtAzKd.LostFocus += OnPidfGainChanged;
            txtAltKp.LostFocus += OnPidfGainChanged;
            txtAltKi.LostFocus += OnPidfGainChanged;
            txtAltKd.LostFocus += OnPidfGainChanged;
            txtAzVff.LostFocus += OnVffGainChanged;
            txtAltVff.LostFocus += OnVffGainChanged;
            chkKalmanEnabled.Checked += OnKalmanSettingChanged;
            chkKalmanEnabled.Unchecked += OnKalmanSettingChanged;
            txtKalmanQPos.LostFocus += OnKalmanSettingChanged;
            txtKalmanQVel.LostFocus += OnKalmanSettingChanged;
            txtKalmanR.LostFocus += OnKalmanSettingChanged;
            txtPredictionMs.LostFocus += OnKalmanSettingChanged;
            
            _isLoading = false;
        }
        
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }
        
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void LoadGains()
        {
            try
            {
                var gains = AppSettings.GetSiderealTrackingPDGains();
                txtAzKp.Text = gains.Azimuth_Kp.ToString("F2");
                txtAzKi.Text = gains.Azimuth_Ki.ToString("F3");
                txtAzKd.Text = gains.Azimuth_Kd.ToString("F3");
                txtAltKp.Text = gains.Altitude_Kp.ToString("F2");
                txtAltKi.Text = gains.Altitude_Ki.ToString("F3");
                txtAltKd.Text = gains.Altitude_Kd.ToString("F3");
                txtAzVff.Text = gains.Azimuth_VffGain.ToString("F2");
                txtAltVff.Text = gains.Altitude_VffGain.ToString("F2");
                
                chkKalmanEnabled.IsChecked = gains.Kalman_Enabled;
                txtKalmanQPos.Text = gains.Kalman_ProcessNoise_Position.ToString("G4");
                txtKalmanQVel.Text = gains.Kalman_ProcessNoise_Velocity.ToString("G4");
                txtKalmanR.Text = gains.Kalman_MeasurementNoise.ToString("F2");
                txtPredictionMs.Text = gains.PredictionTime_Ms.ToString("F1");
            }
            catch { }
        }

        #region Live Apply (on LostFocus � applies when user leaves a field)
        
        /// <summary>
        /// PIDF gains changed � apply to controller on LostFocus
        /// </summary>
        private void OnPidfGainChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            
            if (!double.TryParse(txtAzKp.Text, out double azKp) || azKp < 0) return;
            if (!double.TryParse(txtAzKi.Text, out double azKi) || azKi < 0) return;
            if (!double.TryParse(txtAzKd.Text, out double azKd) || azKd < 0) return;
            if (!double.TryParse(txtAltKp.Text, out double altKp) || altKp < 0) return;
            if (!double.TryParse(txtAltKi.Text, out double altKi) || altKi < 0) return;
            if (!double.TryParse(txtAltKd.Text, out double altKd) || altKd < 0) return;
            
            _trackingController.SetGains(
                new PDControllerGains { Kp = azKp, Ki = azKi, Kd = azKd },
                new PDControllerGains { Kp = altKp, Ki = altKi, Kd = altKd }
            );
        }
        
        /// <summary>
        /// VFF gains changed � apply to controller on LostFocus
        /// </summary>
        private void OnVffGainChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            
            if (!double.TryParse(txtAzVff.Text, out double azVff) || azVff < 0 || azVff > 1.0) return;
            if (!double.TryParse(txtAltVff.Text, out double altVff) || altVff < 0 || altVff > 1.0) return;
            
            _trackingController.SetVffGains(azVff, altVff);
        }
        
        /// <summary>
        /// Kalman settings changed � apply to controller immediately
        /// </summary>
        private void OnKalmanSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            
            bool enabled = chkKalmanEnabled.IsChecked ?? true;
            if (!double.TryParse(txtKalmanQPos.Text, out double qPos) || qPos < 0) return;
            if (!double.TryParse(txtKalmanQVel.Text, out double qVel) || qVel < 0) return;
            if (!double.TryParse(txtKalmanR.Text, out double r) || r <= 0) return;
            if (!double.TryParse(txtPredictionMs.Text, out double predMs) || predMs < 0) return;
            
            _trackingController.SetKalmanSettings(enabled, qPos, qVel, r);
            _trackingController.SetPredictionTime(predMs);
        }
        
        #endregion
        
        #region Save / Reset
        
        /// <summary>
        /// Save current values to MongoDB/JSON (values are already applied live)
        /// </summary>
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!double.TryParse(txtAzKp.Text, out double azKp) || azKp < 0 ||
                    !double.TryParse(txtAzKi.Text, out double azKi) || azKi < 0 ||
                    !double.TryParse(txtAzKd.Text, out double azKd) || azKd < 0 ||
                    !double.TryParse(txtAltKp.Text, out double altKp) || altKp < 0 ||
                    !double.TryParse(txtAltKi.Text, out double altKi) || altKi < 0 ||
                    !double.TryParse(txtAltKd.Text, out double altKd) || altKd < 0 ||
                    !double.TryParse(txtAzVff.Text, out double azVff) || azVff < 0 || azVff > 1.0 ||
                    !double.TryParse(txtAltVff.Text, out double altVff) || altVff < 0 || altVff > 1.0 ||
                    !double.TryParse(txtKalmanQPos.Text, out double kalmanQPos) || kalmanQPos < 0 ||
                    !double.TryParse(txtKalmanQVel.Text, out double kalmanQVel) || kalmanQVel < 0 ||
                    !double.TryParse(txtKalmanR.Text, out double kalmanR) || kalmanR <= 0 ||
                    !double.TryParse(txtPredictionMs.Text, out double predMs) || predMs < 0)
                {
                    MessageBox.Show("Cannot save because one or more values are invalid.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                var gains = new SiderealTrackingPDGains
                {
                    Azimuth_Kp = azKp,
                    Azimuth_Ki = azKi,
                    Azimuth_Kd = azKd,
                    Altitude_Kp = altKp,
                    Altitude_Ki = altKi,
                    Altitude_Kd = altKd,
                    Azimuth_VffGain = azVff,
                    Altitude_VffGain = altVff,
                    Kalman_Enabled = chkKalmanEnabled.IsChecked ?? true,
                    Kalman_ProcessNoise_Position = kalmanQPos,
                    Kalman_ProcessNoise_Velocity = kalmanQVel,
                    Kalman_MeasurementNoise = kalmanR,
                    PredictionTime_Ms = predMs
                };
                AppSettings.SaveSiderealTrackingPDGains(gains);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            txtAzKp.Text = "1.5";
            txtAzKi.Text = "0.000";
            txtAzKd.Text = "0.3";
            txtAltKp.Text = "1.5";
            txtAltKi.Text = "0.000";
            txtAltKd.Text = "0.3";
            txtAzVff.Text = "0.2";
            txtAltVff.Text = "0.2";
            chkKalmanEnabled.IsChecked = true;
            txtKalmanQPos.Text = "0.0000002";
            txtKalmanQVel.Text = "0.000002";
            txtKalmanR.Text = "1.00";
            txtPredictionMs.Text = "0.0";
        }
        
        #endregion
    }
}
