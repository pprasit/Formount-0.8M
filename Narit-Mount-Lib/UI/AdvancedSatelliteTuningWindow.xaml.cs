using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    public partial class AdvancedSatelliteTuningWindow : Window
    {
        public bool WasApplied { get; private set; } = false;

        public AdvancedSatelliteTuningWindow()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[ADVANCED TUNING] Constructor started");
                InitializeComponent();
                System.Diagnostics.Debug.WriteLine("[ADVANCED TUNING] InitializeComponent completed");
                
                LoadCurrentSettings();
                System.Diagnostics.Debug.WriteLine("[ADVANCED TUNING] LoadCurrentSettings completed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ADVANCED TUNING] Constructor FAILED: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ADVANCED TUNING] Stack trace: {ex.StackTrace}");
                SimpleLogger.Error($"[ADVANCED TUNING] Failed to initialize window: {ex.Message}", "SatTrack");
                MessageBox.Show($"Failed to open Advanced Tuning window:\n\n{ex.Message}\n\nPlease check the logs.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }
        
        /// <summary>
        /// Handle title bar drag to move window
        /// </summary>
        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                {
                    this.DragMove();
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Close button clicked in custom title bar
        /// </summary>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SimpleLogger.Info("[ADVANCED TUNING] Window closed via title bar", "SatTrack");
            Close();
        }

        /// <summary>
        /// Load current settings from AppSettings
        /// </summary>
        private void LoadCurrentSettings()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[ADVANCED TUNING] LoadCurrentSettings - Start");
                
                // Check if controls are initialized
                if (txtAzKp == null || txtAzKd == null || txtAzVffGain == null ||
                    txtAltKp == null || txtAltKd == null || txtAltVffGain == null)
                {
                    throw new InvalidOperationException("Controls are not initialized. InitializeComponent() may have failed.");
                }
                
                var gains = AppSettings.GetSatelliteTrackingPDGains();
                System.Diagnostics.Debug.WriteLine($"[ADVANCED TUNING] Loaded gains - Az: Kp={gains.Azimuth_Kp}, Kd={gains.Azimuth_Kd}, VFF={gains.Azimuth_VffGain}");
                System.Diagnostics.Debug.WriteLine($"[ADVANCED TUNING] Loaded gains - Alt: Kp={gains.Altitude_Kp}, Kd={gains.Altitude_Kd}, VFF={gains.Altitude_VffGain}");
                
                // Update Azimuth textboxes
                txtAzKp.Text = gains.Azimuth_Kp.ToString("F2");
                txtAzKi.Text = gains.Azimuth_Ki.ToString("F3");
                txtAzKd.Text = gains.Azimuth_Kd.ToString("F3");
                txtAzVffGain.Text = gains.Azimuth_VffGain.ToString("F2");
                txtAzBasePrediction.Text = gains.Azimuth_BasePredictionTimeMs.ToString("F0");
                txtAzMaxPrediction.Text = gains.Azimuth_MaxPredictionTimeMs.ToString("F0");
                
                // Update Altitude textboxes
                txtAltKp.Text = gains.Altitude_Kp.ToString("F2");
                txtAltKi.Text = gains.Altitude_Ki.ToString("F3");
                txtAltKd.Text = gains.Altitude_Kd.ToString("F3");
                txtAltVffGain.Text = gains.Altitude_VffGain.ToString("F2");
                txtAltBasePrediction.Text = gains.Altitude_BasePredictionTimeMs.ToString("F0");
                txtAltMaxPrediction.Text = gains.Altitude_MaxPredictionTimeMs.ToString("F0");
                
                // Velocity Filter Alpha & Max Integral
                txtVelFilterAlpha.Text = gains.Velocity_Filter_Alpha.ToString("F2");
                txtMaxIntegral.Text = gains.Max_Integral_Deg.ToString("F1");
                
                System.Diagnostics.Debug.WriteLine("[ADVANCED TUNING] LoadCurrentSettings - Completed successfully");
                SimpleLogger.Info("[ADVANCED TUNING] Window opened, settings loaded for both axes", "SatTrack");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ADVANCED TUNING] LoadCurrentSettings FAILED: {ex.Message}");
                SimpleLogger.Error($"[ADVANCED TUNING] Failed to load settings: {ex.Message}", "SatTrack");
                throw;
            }
        }

        /// <summary>
        /// Apply button clicked - Save settings for both axes
        /// </summary>
        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ===== AZIMUTH AXIS VALIDATION =====
                if (!double.TryParse(txtAzKp.Text, out double azKp))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Azimuth Kp value: {txtAzKp.Text}", "SatTrack");
                    txtAzKp.Focus();
                    txtAzKp.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAzKi.Text, out double azKi))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Azimuth Ki value: {txtAzKi.Text}", "SatTrack");
                    txtAzKi.Focus();
                    txtAzKi.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAzKd.Text, out double azKd))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Azimuth Kd value: {txtAzKd.Text}", "SatTrack");
                    txtAzKd.Focus();
                    txtAzKd.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAzVffGain.Text, out double azVff))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Azimuth VFF value: {txtAzVffGain.Text}", "SatTrack");
                    txtAzVffGain.Focus();
                    txtAzVffGain.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAzBasePrediction.Text, out double azBasePred))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Azimuth Base Prediction Time: {txtAzBasePrediction.Text}", "SatTrack");
                    txtAzBasePrediction.Focus();
                    txtAzBasePrediction.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAzMaxPrediction.Text, out double azMaxPred))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Azimuth Max Prediction Time: {txtAzMaxPrediction.Text}", "SatTrack");
                    txtAzMaxPrediction.Focus();
                    txtAzMaxPrediction.SelectAll();
                    return;
                }

                // ===== ALTITUDE AXIS VALIDATION =====
                if (!double.TryParse(txtAltKp.Text, out double altKp))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Altitude Kp value: {txtAltKp.Text}", "SatTrack");
                    txtAltKp.Focus();
                    txtAltKp.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAltKi.Text, out double altKi))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Altitude Ki value: {txtAltKi.Text}", "SatTrack");
                    txtAltKi.Focus();
                    txtAltKi.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAltKd.Text, out double altKd))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Altitude Kd value: {txtAltKd.Text}", "SatTrack");
                    txtAltKd.Focus();
                    txtAltKd.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAltVffGain.Text, out double altVff))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Altitude VFF value: {txtAltVffGain.Text}", "SatTrack");
                    txtAltVffGain.Focus();
                    txtAltVffGain.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAltBasePrediction.Text, out double altBasePred))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Altitude Base Prediction Time: {txtAltBasePrediction.Text}", "SatTrack");
                    txtAltBasePrediction.Focus();
                    txtAltBasePrediction.SelectAll();
                    return;
                }

                if (!double.TryParse(txtAltMaxPrediction.Text, out double altMaxPred))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Altitude Max Prediction Time: {txtAltMaxPrediction.Text}", "SatTrack");
                    txtAltMaxPrediction.Focus();
                    txtAltMaxPrediction.SelectAll();
                    return;
                }

                // ===== RANGE VALIDATION (AZIMUTH) =====
                if (azKp < 0 || azKp > 10.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Azimuth Kp out of range: {azKp} (must be 0-10)", "SatTrack");
                    txtAzKp.Focus();
                    txtAzKp.SelectAll();
                    return;
                }

                if (azKi < 0 || azKi > 5.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Azimuth Ki out of range: {azKi} (must be 0-5)", "SatTrack");
                    txtAzKi.Focus();
                    txtAzKi.SelectAll();
                    return;
                }

                if (azKd < 0 || azKd > 2.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Azimuth Kd out of range: {azKd} (must be 0-2)", "SatTrack");
                    txtAzKd.Focus();
                    txtAzKd.SelectAll();
                    return;
                }

                if (azVff < 0 || azVff > 2.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Azimuth VFF out of range: {azVff} (must be 0-2)", "SatTrack");
                    txtAzVffGain.Focus();
                    txtAzVffGain.SelectAll();
                    return;
                }

                if (azBasePred < 0 || azBasePred > 1000.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Azimuth Base Prediction Time out of range: {azBasePred} (must be 0-1000ms)", "SatTrack");
                    txtAzBasePrediction.Focus();
                    txtAzBasePrediction.SelectAll();
                    return;
                }

                if (azMaxPred < azBasePred || azMaxPred > 2000.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Azimuth Max Prediction Time invalid: {azMaxPred} (must be >= Base and <= 2000ms)", "SatTrack");
                    txtAzMaxPrediction.Focus();
                    txtAzMaxPrediction.SelectAll();
                    return;
                }

                // ===== RANGE VALIDATION (ALTITUDE) =====
                if (altKp < 0 || altKp > 10.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Altitude Kp out of range: {altKp} (must be 0-10)", "SatTrack");
                    txtAltKp.Focus();
                    txtAltKp.SelectAll();
                    return;
                }

                if (altKi < 0 || altKi > 5.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Altitude Ki out of range: {altKi} (must be 0-5)", "SatTrack");
                    txtAltKi.Focus();
                    txtAltKi.SelectAll();
                    return;
                }

                if (altKd < 0 || altKd > 2.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Altitude Kd out of range: {altKd} (must be 0-2)", "SatTrack");
                    txtAltKd.Focus();
                    txtAltKd.SelectAll();
                    return;
                }

                if (altVff < 0 || altVff > 2.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Altitude VFF out of range: {altVff} (must be 0-2)", "SatTrack");
                    txtAltVffGain.Focus();
                    txtAltVffGain.SelectAll();
                    return;
                }

                if (altBasePred < 0 || altBasePred > 1000.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Altitude Base Prediction Time out of range: {altBasePred} (must be 0-1000ms)", "SatTrack");
                    txtAltBasePrediction.Focus();
                    txtAltBasePrediction.SelectAll();
                    return;
                }

                if (altMaxPred < altBasePred || altMaxPred > 2000.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Altitude Max Prediction Time invalid: {altMaxPred} (must be >= Base and <= 2000ms)", "SatTrack");
                    txtAltMaxPrediction.Focus();
                    txtAltMaxPrediction.SelectAll();
                    return;
                }

                // ===== VELOCITY FILTER VALIDATION =====
                if (!double.TryParse(txtVelFilterAlpha.Text, out double velFilterAlpha))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Velocity Filter Alpha: {txtVelFilterAlpha.Text}", "SatTrack");
                    txtVelFilterAlpha.Focus();
                    txtVelFilterAlpha.SelectAll();
                    return;
                }

                if (velFilterAlpha < 0.01 || velFilterAlpha > 1.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Velocity Filter Alpha out of range: {velFilterAlpha} (must be 0.01-1.0)", "SatTrack");
                    txtVelFilterAlpha.Focus();
                    txtVelFilterAlpha.SelectAll();
                    return;
                }

                if (!double.TryParse(txtMaxIntegral.Text, out double maxIntegral))
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Invalid Max Integral: {txtMaxIntegral.Text}", "SatTrack");
                    txtMaxIntegral.Focus();
                    txtMaxIntegral.SelectAll();
                    return;
                }

                if (maxIntegral < 0.1 || maxIntegral > 50.0)
                {
                    SimpleLogger.Warning($"[ADVANCED TUNING] Max Integral out of range: {maxIntegral} (must be 0.1-50)", "SatTrack");
                    txtMaxIntegral.Focus();
                    txtMaxIntegral.SelectAll();
                    return;
                }

                // ===== SAVE TO APPSETTINGS =====
                var gains = new SatelliteTrackingPDGains
                {
                    Azimuth_Kp = azKp,
                    Azimuth_Ki = azKi,
                    Azimuth_Kd = azKd,
                    Azimuth_VffGain = azVff,
                    Azimuth_BasePredictionTimeMs = azBasePred,
                    Azimuth_MaxPredictionTimeMs = azMaxPred,
                    
                    Altitude_Kp = altKp,
                    Altitude_Ki = altKi,
                    Altitude_Kd = altKd,
                    Altitude_VffGain = altVff,
                    Altitude_BasePredictionTimeMs = altBasePred,
                    Altitude_MaxPredictionTimeMs = altMaxPred,
                    
                    Velocity_Filter_Alpha = velFilterAlpha,
                    Max_Integral_Deg = maxIntegral
                };
                AppSettings.SaveSatelliteTrackingPDGains(gains);

                // Mark as applied
                WasApplied = true;

                // Log success
                SimpleLogger.Info($"[ADVANCED TUNING] Azimuth applied: Kp={azKp:F2}, Ki={azKi:F3}, Kd={azKd:F3}, VFF={azVff:F2}, Base={azBasePred:F0}ms, Max={azMaxPred:F0}ms", "SatTrack");
                SimpleLogger.Info($"[ADVANCED TUNING] Altitude applied: Kp={altKp:F2}, Ki={altKi:F3}, Kd={altKd:F3}, VFF={altVff:F2}, Base={altBasePred:F0}ms, Max={altMaxPred:F0}ms", "SatTrack");
                SimpleLogger.Info($"[ADVANCED TUNING] Velocity Filter Alpha={velFilterAlpha:F2}, Max Integral={maxIntegral:F1}�", "SatTrack");

                // Flash button to indicate success
                var originalContent = btnApply.Content;
                btnApply.Content = "? Applied";
                btnApply.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (s, args) =>
                {
                    btnApply.Content = originalContent;
                    btnApply.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
                    timer.Stop();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[ADVANCED TUNING] Apply failed: {ex.Message}", "SatTrack");
                System.Diagnostics.Debug.WriteLine($"[ADVANCED TUNING] Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancel button clicked
        /// </summary>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            SimpleLogger.Info("[ADVANCED TUNING] Window closed without applying", "SatTrack");
            Close();
        }
    }
}
