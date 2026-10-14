using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.UI;
using System.Linq;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Camera;

namespace ForkMount_WPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _areDrivesEnabled = false;
        private System.Windows.Threading.DispatcherTimer _buttonUpdateTimer;

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "ForkMount-WPF - Platinum Drive Control System";
            
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            
            // [NEW] Initialize timer to update button state based on actual drive status
            _buttonUpdateTimer = new System.Windows.Threading.DispatcherTimer();
            _buttonUpdateTimer.Interval = TimeSpan.FromMilliseconds(200); // Update every 200ms
            _buttonUpdateTimer.Tick += ButtonUpdateTimer_Tick;
            _buttonUpdateTimer.Start();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SimpleLogger.Info("ForkMount-WPF started", "System");
            UpdateToggleDrivesButton();
            
            // [NEW] Update Pointing Model status display
            UpdatePointingModelStatus();
            
            // Subscribe to Azimuth Controller's DriveStatusChanged event
            var azimuthController = FindVisualChild<Azimuth_Controller>(this);
            if (azimuthController != null)
            {
                azimuthController.DriveStatusChanged += OnAzimuthDriveStatusChanged;
                
                // [NEW] Initialize chart with MountInfo for corrected position display
                azimuthController.InitializeChartWithMountInfo();
            }

            // Initialize Satellite Tracking Engine
            InitializeSatelliteTracking();
        }
        
        /// <summary>
        /// [NEW] Update Pointing Model status display in title bar
        /// </summary>
        public void UpdatePointingModelStatus()
        {
            try
            {
                var pointingModelManager = App.PointingModelManager;
                
                // Check if manager exists
                if (pointingModelManager == null)
                {
                    SetPointingModelError("Manager not created");
                    SimpleLogger.Error("Pointing Model Manager is null!", "System");
                    return;
                }
                
                // Check if PointXP was initialized
                if (!pointingModelManager.IsPointXPInitialized)
                {
                    string error = pointingModelManager.InitializationError ?? "Unknown error";
                    SetPointingModelError($"Init failed: {error}");
                    SimpleLogger.Error($"PointXP initialization failed: {error}", "System");
                    return;
                }
                
                bool isLoaded = pointingModelManager.IsModelLoaded;
                string modelFilePath = pointingModelManager.ModelFilePath;
                string modelFileName = !string.IsNullOrEmpty(modelFilePath) 
                    ? System.IO.Path.GetFileName(modelFilePath) 
                    : null;
                
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Pointing Model status: IsLoaded={isLoaded}, File={modelFileName}");
                
                if (isLoaded && !string.IsNullOrEmpty(modelFileName))
                {
                    // Model loaded - show green indicator with filename
                    PointingModelState.Text = $"✓ {modelFileName}";
                    PointingModelState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                    PointingModelIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                    PointingModelBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#064E3B"));
                    PointingModelBadge.ToolTip = $"Pointing Model: ACTIVE\nFile: {modelFileName}\nCorrections are being applied to mount positions.";
                    
                    SimpleLogger.Info($"Pointing Model loaded: {modelFileName}", "System");
                }
                else
                {
                    // No model - show gray indicator
                    PointingModelState.Text = "No Model";
                    PointingModelState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"));
                    PointingModelIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280"));
                    PointingModelBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151"));
                    PointingModelBadge.ToolTip = "Pointing Model: NOT LOADED\nUsing raw encoder positions (no corrections).\n\nCheck Debug Output for details.";
                    
                    SimpleLogger.Warning("Pointing Model not loaded - using raw positions", "System");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Error updating pointing model status: {ex.Message}");
                SetPointingModelError("Error");
            }
        }
        
        /// <summary>
        /// Set pointing model badge to error state
        /// </summary>
        private void SetPointingModelError(string errorMessage)
        {
            PointingModelState.Text = $"⚠ {errorMessage}";
            PointingModelState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            PointingModelIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            PointingModelBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7F1D1D"));
            PointingModelBadge.ToolTip = $"Pointing Model: ERROR\n{errorMessage}\n\nCheck Debug Output for details.";
        }

        private void InitializeSatelliteTracking()
        {
            try
            {
                // Find SatelliteTrackingControl in ManualControlWindow
                var manualControlWindow = Application.Current.Windows.OfType<ManualControlWindow>().FirstOrDefault();
                if (manualControlWindow != null)
                {
                    var satTrackControl = FindVisualChild<SatelliteTrackingControl>(manualControlWindow);
                    var azimuthController = FindVisualChild<Azimuth_Controller>(this);
                    var altitudeController = FindVisualChild<Altitude_Controller>(this);
                    
                    if (satTrackControl != null && azimuthController != null && altitudeController != null)
                    {
                        // [CRITICAL FIX] Get CentralizedPollingManager instead of AltitudeDrive
                        var azimuthInfo = azimuthController.GetAzimuthInfo();
                        var altitudeInfo = altitudeController.GetCurrentAltitudeInfo();
                        var pollingManager = azimuthController.CentralizedPollingManager; // [CRITICAL FIX] Use CentralizedPollingManager
                        
                        // [CRITICAL FIX] Pass CentralizedPollingManager (where CommandSenderLoop runs)
                        satTrackControl.InitializeTrackingEngine(azimuthInfo, altitudeInfo, pollingManager);
                        SimpleLogger.Info("[MAIN] Satellite tracking initialized with CentralizedPollingManager", "System");
                    }
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MAIN] Failed to initialize satellite tracking: {ex.Message}", "System");
            }
        }

        /// <summary>
        /// Timer tick to update button state based on actual drive status
        /// </summary>
        private void ButtonUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {                // Find controllers
                var azimuthController = FindVisualChild<Azimuth_Controller>(this);
                var altitudeController = FindVisualChild<Altitude_Controller>(this);
                
                if (azimuthController == null || altitudeController == null)
                    return;
                
                // Get actual drive status from AzimuthInfo and AltitudeInfo
                var azimuthInfo = azimuthController.GetAzimuthInfo();
                var altitudeInfo = altitudeController.GetCurrentAltitudeInfo();
                
                if (azimuthInfo == null || altitudeInfo == null)
                    return;
                
                bool azimuthEnabled = azimuthInfo.Is_Drive_Enabled;
                bool altitudeEnabled = altitudeInfo.Is_Drive_Enabled;
                bool instrumentEnabled = azimuthController.CentralizedPollingManager?.FieldDerotatorInfo?.Is_Motor_Enabled ?? false;
                bool m3Enabled = azimuthController.CentralizedPollingManager?.M3Info?.Is_Motor_Enabled ?? false;
                
                // Update button state:
                // - All enabled → Show "DISABLE" (orange/red)
                // - Any disabled → Show "ENABLE" (green)
                bool allEnabled = azimuthEnabled && altitudeEnabled && instrumentEnabled && m3Enabled;
                
                if (allEnabled != _areDrivesEnabled)
                {
                    _areDrivesEnabled = allEnabled;
                    UpdateToggleDrivesButton();
                }
            }
            catch
            {
                // Ignore errors during button update
            }
        }
        
        
        
        
        
        
        private void OnAzimuthDriveStatusChanged(object sender, bool isEnabled)
        {
            // This event is no longer used - button state is updated by timer
            // Keeping for backward compatibility
        }

        private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                SimpleLogger.Info("==================== APPLICATION SHUTDOWN START ====================", "System");
                
                // Find Azimuth_Controller and Altitude_Controller
                var azimuthController = FindVisualChild<Azimuth_Controller>(this);
                var altitudeController = FindVisualChild<Altitude_Controller>(this);

                // Cancel the closing temporarily to allow async shutdown
                if (!_shutdownComplete)
                {
                    e.Cancel = true;
                    
                    // [CRITICAL FIX] Disable BOTH axes FIRST (before any disconnect!)
                    // This ensures Altitude can still send AX2.MO=0 while PlatinumDrive is connected
                    
                    // Step 1: Disable Axis 2 (Altitude) FIRST
                    if (altitudeController != null && azimuthController?.AzimuthDrive != null && azimuthController.AzimuthDrive.IsConnected)
                    {
                        SimpleLogger.Info("Disabling Altitude Drive (Axis 2) before shutdown...", "System");
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                string response;
                                bool success = azimuthController.AzimuthDrive.PlatinumDrive.SendRawCommand("AX2.MO=0", out response, 2000);
                                if (success)
                                {
                                    SimpleLogger.Info("[SUCCESS] Altitude disabled (AX2.MO=0)", "System");
                                }
                                else
                                {
                                    SimpleLogger.Error($"[ERROR] Failed to disable Altitude. Response: {response}", "System");
                                }
                            }
                            catch (Exception ex)
                            {
                                SimpleLogger.Error($"[ERROR] Exception disabling Altitude: {ex.Message}", "System");
                            }
                        });
                        await System.Threading.Tasks.Task.Delay(200);
                    }
                    
                    // Step 2: Disable Axis 1 (Azimuth)
                    if (azimuthController != null && azimuthController.AzimuthDrive != null && azimuthController.AzimuthDrive.IsConnected)
                    {
                        SimpleLogger.Info("Disabling Azimuth Drive (Axis 1) before shutdown...", "System");
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                string response;
                                bool success = azimuthController.AzimuthDrive.PlatinumDrive.SendRawCommand("AX1.MO=0", out response, 2000);
                                if (success)
                                {
                                    SimpleLogger.Info("[SUCCESS] Azimuth disabled (AX1.MO=0)", "System");
                                }
                                else
                                {
                                    SimpleLogger.Error($"[ERROR] Failed to disable Azimuth. Response: {response}", "System");
                                }
                            }
                            catch (Exception ex)
                            {
                                SimpleLogger.Error($"[ERROR] Exception disabling Azimuth: {ex.Message}", "System");
                            }
                        });
                        await System.Threading.Tasks.Task.Delay(200);
                    }
                    
                    // Step 3: Now safely shutdown Azimuth Controller (will stop motion and polling)
                    if (azimuthController != null)
                    {
                        SimpleLogger.Info("Shutting down Azimuth Controller (Axis 1)...", "System");
                        await azimuthController.SafeShutdown();
                    }
                    else
                    {
                        SimpleLogger.Info("Azimuth Controller not found (skipped)", "System");
                    }
                    
                    // Step 4: Shutdown Altitude Controller (will stop position monitor and UI)
                    if (altitudeController != null)
                    {
                        SimpleLogger.Info("Shutting down Altitude Controller (Axis 2)...", "System");
                        await altitudeController.SafeShutdown();
                    }
                    else
                    {
                        SimpleLogger.Info("Altitude Controller not found (skipped)", "System");
                    }
                    
                    _shutdownComplete = true;
                    
                    SimpleLogger.Info("==================== APPLICATION SHUTDOWN COMPLETE ====================", "System");
                    SimpleLogger.Info("ForkMount-WPF closed safely", "System");
                    
                    // Wait a bit for logs to be written
                    await System.Threading.Tasks.Task.Delay(500);
                    
                    // Now close for real
                    Application.Current.Shutdown();
                }
            }
            catch (System.Exception ex)
            {
                SimpleLogger.Error($"Error during shutdown: {ex.Message}", "System");
            }
        }

        private bool _shutdownComplete = false;

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

        #region Title Bar Events

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeRestoreWindow();
            }
            else
            {
                this.DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            MaximizeRestoreWindow();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new UI.SettingsWindow
                {
                    Owner = this
                };

                // Show as non-modal window instead of dialog
                settingsWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error opening settings window: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                SimpleLogger.Error($"Failed to open settings window: {ex.Message}", "System");
            }
        }

        private void MaximizeRestoreWindow()
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                MaximizeIcon.Text = "\u25A1"; // Restore icon
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                MaximizeIcon.Text = "\u2750"; // Maximize icon
            }
        }

        #endregion

        #region Control Buttons

        private async void BtnToggleDrives_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Find Azimuth Controller and Altitude Controller
                var azimuthController = FindVisualChild<Azimuth_Controller>(this);
                var altitudeController = FindVisualChild<Altitude_Controller>(this);
                
                if (azimuthController == null)
                {
                    SimpleLogger.Error("Azimuth Controller not found", "System");
                    return;
                }

                // [FIX] Check ACTUAL drive status from AzimuthInfo and AltitudeInfo
                var azimuthInfo = azimuthController.GetAzimuthInfo();
                var altitudeInfo = altitudeController?.GetCurrentAltitudeInfo();
                var pollingManager = azimuthController.CentralizedPollingManager;
                
                bool azimuthEnabled = azimuthInfo?.Is_Drive_Enabled ?? false;
                bool altitudeEnabled = altitudeInfo?.Is_Drive_Enabled ?? false;
                bool instrumentEnabled = pollingManager?.FieldDerotatorInfo?.Is_Motor_Enabled ?? false;
                bool m3Enabled = pollingManager?.M3Info?.Is_Motor_Enabled ?? false;
                bool allEnabled = azimuthEnabled && altitudeEnabled && instrumentEnabled && m3Enabled;
                
                // Toggle logic:
                // - If all enabled → Disable all
                // - If any disabled → Enable all
                bool targetState = !allEnabled;

                SimpleLogger.Info($"==================== DRIVE TOGGLE: {(targetState ? "ENABLE" : "DISABLE")} ALL AXES ====================", "System");
                SimpleLogger.Info($"[STATUS] Before: Axis1(Az)={azimuthEnabled}, Axis2(Alt)={altitudeEnabled}, Axis3(Inst)={instrumentEnabled}, Axis4(M3)={m3Enabled}", "System");
                
                var azimuthDrive = azimuthController?.AzimuthDrive?.PlatinumDrive;
                var altitudeDrive = altitudeController?.AltitudeDrive?.PlatinumDrive;

                if (azimuthDrive != null && azimuthController.AzimuthDrive.IsConnected)
                {
                    await Task.Run(() => ToggleAxisMotor(azimuthDrive, 1, targetState, "Axis 1 / Azimuth", "System"));

                    // Axis 3 and Axis 4 share the azimuth-side Platinum drive.
                    await Task.Run(() => ToggleAxisMotor(azimuthDrive, 3, targetState, "Axis 3 / Instrument", "System"));
                    await Task.Run(() => ToggleAxisMotor(azimuthDrive, 4, targetState, "Axis 4 / M3", "System"));
                }
                else
                {
                    SimpleLogger.Warning("[Axis 1/3/4] Azimuth-side Platinum drive not connected - skipping", "System");
                }
                
                if (altitudeDrive != null && altitudeController != null && altitudeController.AltitudeDrive != null && altitudeController.AltitudeDrive.IsConnected)
                {
                    await Task.Run(() => ToggleAxisMotor(altitudeDrive, 2, targetState, "Axis 2 / Altitude", "System"));
                }
                else
                {
                    SimpleLogger.Warning("[Axis 2] Altitude Controller/Drive not found or not connected - skipping", "System");
                }

                // Wait a bit for drive to update status
                await Task.Delay(300);
                
                // Status will be updated automatically by ButtonUpdateTimer_Tick
                SimpleLogger.Info($"==================== DRIVE TOGGLE COMPLETE ====================", "System");
            }
            catch (System.Exception ex)
            {
                SimpleLogger.Error($"Error toggling drives: {ex.Message}", "System");
            }
        }

        private static void ToggleAxisMotor(PlatinumDrive drive, int axisNumber, bool enable, string axisLabel, string logCategory)
        {
            if (drive == null || !drive.IsConnected)
            {
                SimpleLogger.Warning($"[{axisLabel}] Drive not connected - skipping motor toggle", logCategory);
                return;
            }

            try
            {
                string moCommand = enable ? $"AX{axisNumber}.MO=1" : $"AX{axisNumber}.MO=0";
                string response;
                bool success = drive.SendRawCommand(moCommand, out response, 2000);

                if (success)
                {
                    SimpleLogger.Info($"[{axisLabel}] {(enable ? "ENABLED" : "DISABLED")} successfully ({moCommand})", logCategory);
                }
                else
                {
                    SimpleLogger.Error($"[{axisLabel}] Toggle failed ({moCommand}). Response: {response}", logCategory);
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[{axisLabel}] Toggle failed: {ex.Message}", logCategory);
            }
        }

        private async void BtnEmergencyStop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SimpleLogger.Warning("==================== EMERGENCY STOP ACTIVATED ====================", "System");
                
                // Find Azimuth Controller and Altitude Controller
                var azimuthController = FindVisualChild<Azimuth_Controller>(this);
                var altitudeController = FindVisualChild<Altitude_Controller>(this);
                
                // [FIX] Send AX1.ST to Azimuth Drive (Axis 1)
                if (azimuthController?.AzimuthDrive != null && azimuthController.AzimuthDrive.IsConnected)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            string response;
                            bool success = azimuthController.AzimuthDrive.PlatinumDrive.SendRawCommand("AX1.ST", out response, 2000);
                            
                            if (success)
                            {
                                SimpleLogger.Warning("[Axis 1] Azimuth STOPPED (AX1.ST)", "System");
                            }
                            else
                            {
                                SimpleLogger.Error($"[Axis 1] Azimuth stop failed. Response: {response}", "System");
                            }
                            
                            // Also set Command_Velocity to 0 for safety
                            var azimuthInfo = azimuthController.GetAzimuthInfo();
                            if (azimuthInfo != null)
                            {
                                azimuthInfo.Command_Velocity_Deg_Sec = 0;
                            }
                        }
                        catch (Exception ex)
                        {
                            SimpleLogger.Error($"[Axis 1] Azimuth stop error: {ex.Message}", "System");
                        }
                    });
                }
                else
                {
                    SimpleLogger.Warning("[Axis 1] Azimuth Controller/Drive not found or not connected", "System");
                }

                // [FIX] Send AX2.ST to Altitude Drive (Axis 2)
                if (altitudeController?.AltitudeDrive != null && altitudeController.AltitudeDrive.IsConnected)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            string response;
                            bool success = altitudeController.AltitudeDrive.PlatinumDrive.SendRawCommand("AX2.ST", out response, 2000);
                            
                            if (success)
                            {
                                SimpleLogger.Warning("[Axis 2] Altitude STOPPED (AX2.ST)", "System");
                            }
                            else
                            {
                                SimpleLogger.Error($"[Axis 2] Altitude stop failed. Response: {response}", "System");
                            }
                            
                            // Also set Command_Velocity to 0 for safety
                            var altitudeInfo = altitudeController.GetCurrentAltitudeInfo();
                            if (altitudeInfo != null)
                            {
                                altitudeInfo.Command_Velocity_Deg_Sec = 0;
                            }
                        }
                        catch (Exception ex)
                        {
                            SimpleLogger.Error($"[Axis 2] Altitude stop error: {ex.Message}", "System");
                        }
                    });
                }
                else
                {
                    SimpleLogger.Warning("[Axis 2] Altitude Controller/Drive not found or not connected", "System");
                }

                // Force stop all tracking (celestial, satellite, etc.)
                try
                {
                    TrackingManager.Instance.ForceStopAllTracking();
                    SimpleLogger.Warning("[EMERGENCY STOP] All tracking forcefully stopped via TrackingManager", "System");
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[EMERGENCY STOP] Error stopping tracking: {ex.Message}", "System");
                }

                SimpleLogger.Warning("==================== EMERGENCY STOP COMPLETE ====================", "System");
            }
            catch (System.Exception ex)
            {
                SimpleLogger.Error($"Error during emergency stop: {ex.Message}", "System");
            }
        }

        private void BtnManualControl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var azimuthController = FindVisualChild<Azimuth_Controller>(this);
                var altitudeController = FindVisualChild<Altitude_Controller>(this);

                if (azimuthController == null)
                {
                    SimpleLogger.Error("Azimuth Controller not found - Cannot open Manual Control", "System");
                    return;
                }

                // [FIX] Get AzimuthInfo and AltitudeInfo from CentralizedPollingManager
                // This ensures we use the SAME reference that is being updated by polling loop
                var centralizedPollingManager = azimuthController.CentralizedPollingManager;
                
                if (centralizedPollingManager == null)
                {
                    SimpleLogger.Error("CentralizedPollingManager not initialized - Cannot open Manual Control", "System");
                    return;
                }
                
                var azimuthInfo = centralizedPollingManager.AzimuthInfo;
                var altitudeInfo = centralizedPollingManager.AltitudeInfo;
                
                // Get Drive references
                var azimuthDrive = azimuthController.AzimuthDrive;
                var altitudeDrive = altitudeController?.AltitudeDrive;

                var manualControlWindow = new UI.ManualControlWindow(
                    azimuthController,  // Pass controller reference
                    azimuthDrive,
                    altitudeDrive,
                    azimuthInfo,        // Use reference from CentralizedPollingManager
                    altitudeInfo        // Use reference from CentralizedPollingManager
                );

                SimpleLogger.Info("Manual Control window opened", "System");
                manualControlWindow.Owner = this;
                manualControlWindow.Show();
            }
            catch (System.Exception ex)
            {
                SimpleLogger.Error($"Failed to open Manual Control: {ex.Message}", "System");
            }
        }

        private void BtnImageVerificationAndOffset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var camera = new ZWOASIManager();
                var imageVerificationAndOffset = new UI.ImageVerificationAndOffset(camera);
                imageVerificationAndOffset.Owner = this;
                imageVerificationAndOffset.Show();
                SimpleLogger.Info("Image Verification and Offset window opened", "System");
            }
            catch (System.Exception ex)
            {
                SimpleLogger.Error($"Failed to open Image Verification and Offset: {ex.Message}", "System");
            }
        }

        private void UpdateToggleDrivesButton()
        {
            if (_areDrivesEnabled)
            {
                txtToggleDrives.Text = "DISABLE ALL";
                btnToggleDrives.ToolTip = "Disable all 4 axes";
                btnToggleDrives.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")); // Red
            }
            else
            {
                txtToggleDrives.Text = "ENABLE ALL";
                btnToggleDrives.ToolTip = "Enable all 4 axes";
                btnToggleDrives.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")); // Green
            }
        }

        #endregion
    }
}
