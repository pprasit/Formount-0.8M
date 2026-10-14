using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Win32;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.PointingModel;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Tracking;  // For PDControllerGains
using ForkMount_WPF.Engine.Control;   // For ObserverLocation
using ForkMount_WPF.Engine.PlateSolving;  // For PlateSolveResult

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Mount Calibration Control - NEW PATTERN (Manager-based)
    /// UI polls MountCalibrationManager via timers instead of using Invoke.
    /// All business logic lives in Engine/Manager/MountCalibrationManager.cs
    /// </summary>
    public partial class MountCalibrationControl : UserControl
    {
        // ========================================================================
        // NEW PATTERN: Use MountCalibrationManager (Engine layer)
        // ========================================================================
        private MountCalibrationManager _calibrationManager;

        // UI state (for DataGrid display only - synced from Manager)
        private ObservableCollection<PointingModelPoint> _pointingModelData;
        private readonly Dictionary<int, (int StarsDetected, double RmsError)> _pendingImageSolveUpdates
            = new Dictionary<int, (int StarsDetected, double RmsError)>();

        // UI Timers
        private DispatcherTimer _controlLoopTimer;  // 50ms - calls Manager.Update()
        private DispatcherTimer _uiUpdateTimer;     // 100ms - polls Manager state & images

        // Dependencies
        private CentralizedPollingManager _pollingManager;
        private AzimuthAxisConfig _azimuthConfig;
        private AltitudeAxisConfig _altitudeConfig;

        // Camera provider (passed to Manager)
        private ICameraProvider _cameraProvider;
        private bool _isAutoConnectingCamera;

        public MountCalibrationControl()
        {
            InitializeComponent();
            _pointingModelData = new ObservableCollection<PointingModelPoint>();
            InitializePointingModelBackendSelector();

            // Bind DataGrid
            if (dgPointingModel != null)
            {
                dgPointingModel.ItemsSource = _pointingModelData;
            }

            // Initialize Manager
            InitializeCalibrationManager();
        }

        private void InitializePointingModelBackendSelector()
        {
            if (cmbPointingModelBackend == null)
            {
                return;
            }

            string savedMode = AppSettings.GetMountCalibrationSettings()?.PointingModelBackendMode
                ?? PointingModelBackendMode.LegacyPxpReload.ToString();

            foreach (ComboBoxItem item in cmbPointingModelBackend.Items)
            {
                if (string.Equals(item.Tag?.ToString(), savedMode, StringComparison.OrdinalIgnoreCase))
                {
                    cmbPointingModelBackend.SelectedItem = item;
                    return;
                }
            }

            cmbPointingModelBackend.SelectedIndex = 0;
        }

        private PointingModelBackendMode GetSelectedPointingModelBackendMode()
        {
            if (cmbPointingModelBackend?.SelectedItem is ComboBoxItem item &&
                Enum.TryParse(item.Tag?.ToString(), true, out PointingModelBackendMode parsedMode))
            {
                return parsedMode;
            }

            return PointingModelBackendMode.LegacyPxpReload;
        }

        /// <summary>
        /// Initialize MountCalibrationManager (Engine layer)
        /// </summary>
        private void InitializeCalibrationManager()
        {
            try
            {
                // Get polling manager instance from Azimuth_Controller
                var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                if (mainWindow != null)
                {
                    var azController = VisualTreeHelperExtensions.FindVisualChild<Azimuth_Controller>(mainWindow);
                    _pollingManager = azController?.CentralizedPollingManager;
                }

                // Load axis configs
                _azimuthConfig = AppSettings.GetAzimuthAxisConfig();
                _altitudeConfig = AppSettings.GetAltitudeAxisConfig();

                // Load observer location
                var locationSettings = AppSettings.GetObserverLocation();
                var observerLocation = new ObserverLocation(
                    locationSettings.Latitude_Deg,
                    locationSettings.Longitude_Deg,
                    locationSettings.Altitude_Meters
                );

                // Create MountCalibrationManager (Engine layer)
                _calibrationManager = new MountCalibrationManager(
                    _pollingManager,
                    observerLocation,
                    _azimuthConfig,
                    _altitudeConfig
                );
                _calibrationManager.SetPointingModelBackendMode(GetSelectedPointingModelBackendMode());

                // Set PD gains
                var gains = AppSettings.GetSiderealTrackingPDGains();
                _calibrationManager.SetGains(
                    new PDControllerGains { Kp = gains.Azimuth_Kp, Kd = gains.Azimuth_Kd },
                    new PDControllerGains { Kp = gains.Altitude_Kp, Kd = gains.Altitude_Kd }
                );

                // Initialize camera
                InitializeCamera();

                // Pass to Manager
                _calibrationManager.InitializeCamera(_cameraProvider);

                // [NEW] Subscribe to plate solve completion event to update live image RMS
                _calibrationManager.OnSolveCompleted += OnPlateSolveCompleted;
                _calibrationManager.CalibrationRestartRequested += HandleCalibrationRestartRequested;

                EnsureCameraConnected();

                // Create Control Loop Timer (50ms) - calls Manager.Update()
                _controlLoopTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _controlLoopTimer.Tick += ControlLoopTimer_Tick;

                // Create UI Update Timer (100ms) - polls Manager state & images
                _uiUpdateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _uiUpdateTimer.Tick += UIUpdateTimer_Tick;

                // Start UI timer immediately for mount position updates!
                _uiUpdateTimer.Start();

                System.Diagnostics.Debug.WriteLine("[MountCalibration] NEW PATTERN initialized:");
                System.Diagnostics.Debug.WriteLine("[MountCalibration]   - MountCalibrationManager (Engine)");
                System.Diagnostics.Debug.WriteLine("[MountCalibration]   - Control Loop Timer: 50ms (calls Manager.Update())");
                System.Diagnostics.Debug.WriteLine("[MountCalibration]   - UI Update Timer: 100ms (polls state & images) - STARTED!");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Control Loop Timer (50ms) - calls Manager.Update()
        /// NO UI UPDATES HERE - just business logic
        /// </summary>
        private void ControlLoopTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Just call Manager.Update() - no UI code!
                _calibrationManager?.Update();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Control loop error: {ex.Message}");
            }
        }

        /// <summary>
        /// UI Update Timer (100ms) - polls Manager and updates UI
        /// ALL UI UPDATES HERE - running in UI thread (no Invoke needed!)
        /// </summary>
        private void UIUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_calibrationManager == null) return;

                // Retry getting polling manager if it's null
                if (_pollingManager == null)
                {
                    var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (mainWindow != null)
                    {
                        var azController = VisualTreeHelperExtensions.FindVisualChild<Azimuth_Controller>(mainWindow);
                        _pollingManager = azController?.CentralizedPollingManager;

                        if (_pollingManager != null)
                        {
                            System.Diagnostics.Debug.WriteLine("[MountCalibration] SUCCESS: Retry got _pollingManager!");
                        }
                    }
                }

                // Poll state from Manager
                UpdateProgressBarFromManager();
                UpdateStatusPanelFromManager();
                UpdateSaveModelButtonState();

                // Poll new images from queue
                var imageData = _calibrationManager.PollNextImage();
                if (imageData != null)
                {
                    DisplayCapturedImage(imageData);
                }

                // Sync DataGrid with Manager points
                SyncPointsWithManager();

                // Update sky map
                UpdateCalibrationSkyMapFromManager();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] UI update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize camera provider (without connecting)
        /// </summary>
        private void InitializeCamera()
        {
            try
            {
                var preferredCameraType = GetPreferredCameraType();

                if (preferredCameraType == CameraType.FLI)
                {
                    _cameraProvider = new FLICameraProvider(new FLICameraManager());
                }
                else
                {
                    _cameraProvider = new ZWOCameraProvider(new ZWOASIManager());
                }

                _calibrationManager?.InitializeCamera(_cameraProvider);
                UpdateCameraStatusIndicator();
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Default camera provider created ({_cameraProvider.CameraType})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error creating camera controller: {ex.Message}");
                UpdateCameraStatusIndicator();
            }
        }

        private CameraType GetPreferredCameraType()
        {
            try
            {
                var cameraSettings = AppSettings.GetCameraSettings();
                if (cameraSettings != null &&
                    !string.IsNullOrWhiteSpace(cameraSettings.PreferredCameraType) &&
                    Enum.TryParse(cameraSettings.PreferredCameraType, true, out CameraType preferredType))
                {
                    return preferredType;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Failed to read preferred camera type: {ex.Message}");
            }

            return CameraType.ZWO_ASI;
        }

        private static int GetConfiguredCalibrationExposureMs()
        {
            try
            {
                var cameraSettings = AppSettings.GetCameraSettings();
                if (cameraSettings?.ExposureTime_ms > 0)
                    return cameraSettings.ExposureTime_ms;
            }
            catch
            {
            }

            return 5000;
        }

        private static MountCalibrationSettings GetCurrentCalibrationSettingsSafe()
        {
            try
            {
                return AppSettings.GetMountCalibrationSettings() ?? new MountCalibrationSettings();
            }
            catch
            {
                return new MountCalibrationSettings();
            }
        }

        private static string FormatExposureLabel(int exposureMs)
        {
            if (exposureMs >= 1000)
                return $"{exposureMs / 1000.0:F1}s";

            return $"{exposureMs}ms";
        }

        private void SavePreferredCameraType(CameraType cameraType)
        {
            try
            {
                var cameraSettings = AppSettings.GetCameraSettings();
                cameraSettings.PreferredCameraType = cameraType.ToString();
                AppSettings.SaveCameraSettings(cameraSettings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Failed to save preferred camera type: {ex.Message}");
            }
        }

        private void UpdateCameraStatusIndicator()
        {
            if (ellipseCameraStatus == null)
                return;

            bool isConnected = _cameraProvider?.IsConnected == true;
            ellipseCameraStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                isConnected ? "#10B981" : "#6B7280"));
            ellipseCameraStatus.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                isConnected ? "#A7F3D0" : "#9CA3AF"));
            ellipseCameraStatus.StrokeThickness = 1;

            if (btnCameraSettings != null)
            {
                string cameraName = _cameraProvider?.CameraName;
                string toolTip = _isAutoConnectingCamera
                    ? $"Connecting to {_cameraProvider?.CameraType.ToString() ?? "camera"}..."
                    : isConnected
                    ? $"Camera connected: {cameraName}"
                    : $"Camera not connected ({_cameraProvider?.CameraType.ToString() ?? "Unknown"})";
                btnCameraSettings.ToolTip = toolTip;
            }
        }

        /// <summary>
        /// Ensure camera is connected (auto-connect if needed)
        /// </summary>
        private async void EnsureCameraConnected()
        {
            await EnsureCameraReadyForCalibrationAsync(showWarning: false);
        }

        private async Task<bool> EnsureCameraReadyForCalibrationAsync(bool showWarning)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[MountCalibration] ====== EnsureCameraConnected START ======");
                _isAutoConnectingCamera = true;

                if (_cameraProvider == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] Camera is null - initializing...");
                    InitializeCamera();
                }

                if (_cameraProvider == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] ? Camera initialization failed - camera is still null!");
                    if (showWarning)
                    {
                        MessageBox.Show("Camera is not initialized yet.\n\nPlease open Camera Control and connect a camera before starting calibration.",
                            "Camera Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Camera provider exists, Type={_cameraProvider.CameraType}, IsConnected={_cameraProvider.IsConnected}");

                if (!_cameraProvider.IsConnected)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] Camera not connected - attempting connection...");

                    var cameraIndex = 0;
                    var connected = await Task.Run(() => _cameraProvider.Connect(cameraIndex));

                    if (connected)
                    {
                        _calibrationManager?.InitializeCamera(_cameraProvider);
                        SavePreferredCameraType(_cameraProvider.CameraType);
                        System.Diagnostics.Debug.WriteLine($"[MountCalibration] ? Camera connected successfully (index {cameraIndex})");
                        System.Diagnostics.Debug.WriteLine($"[MountCalibration]    Camera Name: {_cameraProvider.CameraName}");
                        System.Diagnostics.Debug.WriteLine($"[MountCalibration]    Resolution: {_cameraProvider.MaxWidth}x{_cameraProvider.MaxHeight}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[MountCalibration] ? Camera connection failed - will fallback to synthetic");
                        System.Diagnostics.Debug.WriteLine("[MountCalibration]    Possible reasons:");
                        System.Diagnostics.Debug.WriteLine($"[MountCalibration]      1. No {_cameraProvider.CameraType} camera connected via USB");
                        System.Diagnostics.Debug.WriteLine("[MountCalibration]      2. Camera in use by another application");
                        System.Diagnostics.Debug.WriteLine("[MountCalibration]      3. Camera SDK / DLL mismatch");
                        System.Diagnostics.Debug.WriteLine("[MountCalibration]      4. USB driver issues");

                        if (showWarning)
                        {
                            MessageBox.Show("Camera is not connected.\n\nPlease connect the camera in Camera Control before using Add Point or Start.",
                                "Camera Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        return false;
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] ? Camera already connected");
                }

                System.Diagnostics.Debug.WriteLine("[MountCalibration] ====== EnsureCameraConnected END ======");
                UpdateCameraStatusIndicator();
                return _cameraProvider.IsConnected;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] ? Camera connection exception: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MountCalibration]    Stack: {ex.StackTrace}");
                if (showWarning)
                {
                    MessageBox.Show($"Camera connection check failed:\n{ex.Message}",
                        "Camera Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                UpdateCameraStatusIndicator();
                return false;
            }
            finally
            {
                _isAutoConnectingCamera = false;
                UpdateCameraStatusIndicator();
            }
        }

        // ========================================================================
        // UI POLLING METHODS (No Invoke needed - already in UI thread!)
        // ========================================================================

        /// <summary>
        /// Update progress bar from Manager state (NO INVOKE - already in UI thread!)
        /// </summary>
        private void UpdateProgressBarFromManager()
        {
            try
            {
                if (progressBar == null || txtStatusText == null || txtProgress == null || txtCompletedCount == null)
                    return;

                // Get data from Manager
                int totalPoints = _calibrationManager.TotalPoints;
                int processedCount = _calibrationManager.ProcessedPoints;
                int solvedCount = _calibrationManager.SolvedPoints;
                int failedCount = _calibrationManager.FailedPoints;
                double percentage = totalPoints > 0 ? (processedCount / (double)totalPoints) * 100.0 : 0.0;
                var state = _calibrationManager.State;
                var currentPoint = _calibrationManager.CurrentPoint;

                if (totalPoints == 0) return;

                // Update progress bar
                progressBar.Value = percentage;
                txtProgress.Text = $"{percentage:F0}%";
                txtCompletedCount.Text = $"{processedCount}/{totalPoints}";

                // Update status text
                string statusText = FormatStatusText(state, currentPoint, totalPoints, processedCount);
                txtStatusText.Text = statusText;

                if (txtEta != null)
                {
                    DateTime? eta = _calibrationManager.EstimatedCompletionLocalTime;
                    string rmsSummary = GetOverallRmsSummaryText();
                    txtEta.Text = eta.HasValue
                        ? $"ETA: {eta.Value:HH:mm}  |  Total RMS: {rmsSummary}  |  Solved {solvedCount}  Failed {failedCount}"
                        : $"ETA: --  |  Total RMS: {rmsSummary}  |  Solved {solvedCount}  Failed {failedCount}";
                }

                // Update overall RMS pointing error
                UpdateOverallRmsPointingError();
                
                // [NEW] Auto-reset Start button when calibration is not running
                // This handles both manual Add Point completion and normal calibration completion
                if (!_calibrationManager.IsRunning && btnStartResume.Content?.ToString() == "Stop")
                {
                    SetStartButtonToStartMode();
                    System.Diagnostics.Debug.WriteLine("[UI] Calibration stopped - Reset button to Start mode");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] UpdateProgressBarFromManager error: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate and update overall RMS pointing error from all solved points
        /// </summary>
        private void UpdateOverallRmsPointingError()
        {
            try
            {
                // Debug: Check if UI element exists
                if (txtOverallRmsPointingError == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] ?? txtOverallRmsPointingError is NULL - UI not loaded yet?");
                    return;
                }

                if (_calibrationManager == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] ?? _calibrationManager is NULL");
                    return;
                }

                if (_calibrationManager.PointingModelBackendMode == PointingModelBackendMode.ExperimentalCoreDirect)
                {
                    txtOverallMetricLabel.Text = "PointXPCore Model RMS:";

                    var metrics = _calibrationManager.ExperimentalMetrics;
                    if (metrics == null)
                    {
                        txtOverallMetricDetail.Text = "Waiting for solved points";
                        txtOverallRmsPointingError.Text = "--";
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
                        return;
                    }

                    txtOverallMetricDetail.Text = metrics.IsCalibrated
                        ? $"Used {metrics.NumPointsUsed}/{metrics.NumCalPoints} | Max {metrics.MaxErrorArcsec:F2}\""
                        : metrics.StatusMessage;

                    if (!metrics.IsCalibrated)
                    {
                        txtOverallRmsPointingError.Text = "--";
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
                        return;
                    }

                    double experimentalRms = metrics.RMSErrorArcsec;
                    txtOverallRmsPointingError.Text = $"{experimentalRms:F2}";

                    if (experimentalRms < 5.0)
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    else if (experimentalRms < 10.0)
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    else if (experimentalRms < 20.0)
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));
                    else
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));

                    return;
                }

                if (App.PointingModelManager?.TryGetLoadedModelMetrics(out double loadedRmsArcsec, out double loadedMaxArcsec, out int loadedPointsUsed, out int loadedPointsTotal) == true)
                {
                    txtOverallMetricLabel.Text = "Loaded Model RMS:";
                    txtOverallMetricDetail.Text = $"Used {loadedPointsUsed}/{loadedPointsTotal} | Max {loadedMaxArcsec:F2}\"";
                    txtOverallRmsPointingError.Text = $"{loadedRmsArcsec:F2}";

                    if (loadedRmsArcsec < 5.0)
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    else if (loadedRmsArcsec < 10.0)
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    else if (loadedRmsArcsec < 20.0)
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));
                    else
                        txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));

                    return;
                }

                txtOverallMetricLabel.Text = "Overall RMS Pointing Error:";
                txtOverallMetricDetail.Text = "Engine: Legacy";

                // Get all points from manager
                var allPoints = _calibrationManager.GetCalibrationPoints();

                // Filter only solved points with valid errors
                var solvedPoints = allPoints
                    .Where(p => p.IsSolved &&
                               p.RaError_Arcsec.HasValue &&
                               p.DecError_Arcsec.HasValue)
                    .ToList();

                // Only log if we have solved points (reduce spam)
                if (solvedPoints.Count == 0)
                {
                    txtOverallRmsPointingError.Text = "--";
                    txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)); // Gray
                    return; // Don't spam "No solved points" every 50ms!
                }

                // Calculate RMS of total errors
                // RMS = sqrt(mean(totalError^2))
                double sumSquares = 0.0;
                foreach (var point in solvedPoints)
                {
                    double raErr = point.RaError_Arcsec.Value;
                    double decErr = point.DecError_Arcsec.Value;
                    double totalErr = Math.Sqrt(raErr * raErr + decErr * decErr);
                    sumSquares += totalErr * totalErr;
                    // Removed per-point logging to reduce spam
                }

                double rms = Math.Sqrt(sumSquares / solvedPoints.Count);

                // Update text
                txtOverallRmsPointingError.Text = $"{rms:F2}";

                // Color code based on quality
                if (rms < 5.0)
                    txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green - Excellent
                else if (rms < 10.0)
                    txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow - Good
                else if (rms < 20.0)
                    txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22)); // Orange - Acceptable
                else
                    txtOverallRmsPointingError.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red - Poor

                // Removed spam logging (was printing every 50ms)
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] ? UpdateOverallRmsPointingError error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MountCalibration]    Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Format status text from Manager state
        /// </summary>
        private string FormatStatusText(CalibrationState state, Engine.Manager.CalibrationPoint currentPoint, int totalPoints, int completedCount)
        {
            if (currentPoint == null)
            {
                if (completedCount == totalPoints && totalPoints > 0)
                    return $"Calibration Complete! All {totalPoints} points finished.";
                return "Idle";
            }

            int pointNum = currentPoint.Index;

            switch (state)
            {
                case CalibrationState.Slewing:
                    return $"Slewing to Point {pointNum}/{totalPoints} (Az: {currentPoint.TargetAzimuth:F1}°, Alt: {currentPoint.TargetAltitude:F1}°)";

                case CalibrationState.CollectingSamples:
                {
                    var settings = GetCurrentCalibrationSettingsSafe();
                    int minimumSamples = settings.MinimumTrackingSamples > 0 ? settings.MinimumTrackingSamples : 6;
                    return $"Point {pointNum}/{totalPoints}: Collecting samples... ({currentPoint.SampleCount}/{minimumSamples})";
                }

                case CalibrationState.WaitingForStability:
                {
                    var settings = GetCurrentCalibrationSettingsSafe();
                    return $"Point {pointNum}/{totalPoints}: Waiting for stability (RMS: {currentPoint.CurrentRmsError:F2}\", need < {settings.RmsStabilityThreshold_Arcsec:F1}\")";
                }

                case CalibrationState.Stabilizing:
                    return $"Point {pointNum}/{totalPoints}: Stabilizing... (RMS: {currentPoint.CurrentRmsError:F2}\")";

                case CalibrationState.Capturing:
                    return $"Point {pointNum}/{totalPoints}: Capturing image... (Exposure: {FormatExposureLabel(GetConfiguredCalibrationExposureMs())})";

                case CalibrationState.Tracking:
                {
                    var settings = GetCurrentCalibrationSettingsSafe();
                    return $"Point {pointNum}/{totalPoints}: Tracking... ({currentPoint.TrackingTime:F1}s / {settings.TrackingDuration_Seconds}s, RMS: {currentPoint.CurrentRmsError:F2}\")";
                }

                case CalibrationState.Processing:
                {
                    // Check if waiting for a previous point's solve
                    int pendingIdx = _calibrationManager.PendingSolvePointIndex;
                    if (pendingIdx >= 0 && pendingIdx < totalPoints)
                    {
                        var pendingPt = _calibrationManager.GetCalibrationPoints()?.ElementAtOrDefault(pendingIdx);
                        if (pendingPt != null && !pendingPt.IsSolved && !pendingPt.SolveFailed)
                            return $"Point {pointNum}/{totalPoints}: Waiting for point {pendingIdx + 1} plate solve...";
                    }
                    if (currentPoint.IsSolving)
                        return $"Point {pointNum}/{totalPoints}: Plate solving...";
                    return $"Point {pointNum}/{totalPoints}: Processing image...";
                }

                case CalibrationState.Completed:
                    return $"Calibration Complete! All {totalPoints} points finished.";

                case CalibrationState.Paused:
                    return $"Paused at Point {pointNum}/{totalPoints}";

                default:
                    return $"Processing Point {pointNum}/{totalPoints}...";
            }
        }

        /// <summary>
        /// Update status panel from Manager (NO INVOKE - already in UI thread!)
        /// </summary>
        private void UpdateStatusPanelFromManager()
        {
            try
            {
                var currentPoint = _calibrationManager.CurrentPoint; // Engine.Manager.CalibrationPoint
                var azInfo = _pollingManager?.AzimuthInfo;
                var altInfo = _pollingManager?.AltitudeInfo;

                // [P3 FIX] Always update Sky Map with CORRECTED mount position (even when idle!)
                // Use Corrected position = true sky position (after pointing model)
                // This matches what SiderealTracking and Update() use for target comparison
                if (azInfo != null && altInfo != null && _pollingManager?.MountInfo != null)
                {
                    double currentAz = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                    double currentAlt = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;

                    calibrationSkyMap?.SetCurrentPoint(currentAz, currentAlt);
                }
                else if (azInfo != null && altInfo != null)
                {
                    // Fallback to Raw if MountInfo not available yet
                    double currentAz = (azInfo.Raw_Position / (double)_azimuthConfig.Encoder_Resolution) * 360.0 + _azimuthConfig.Position_Offset_Deg;
                    double currentAlt = (altInfo.Raw_Position / (double)_altitudeConfig.Encoder_Resolution) * 360.0 + _altitudeConfig.Position_Offset_Deg;

                    calibrationSkyMap?.SetCurrentPoint(currentAz, currentAlt);
                }

                // If no active point, clear text fields
                if (currentPoint == null)
                {
                    txtTargetAz.Text = "-";
                    txtTargetAlt.Text = "-";
                    txtTargetRA.Text = "-";
                    txtTargetDec.Text = "-";
                    txtMountAz.Text = "-";
                    txtMountAlt.Text = "-";
                    txtRmsAz.Text = "-";
                    txtRmsAlt.Text = "-";
                    txtRmsTotal.Text = "-";
                    return;
                }

                // Target Position
                txtTargetAz.Text = $"{currentPoint.TargetAzimuth:F4}";
                txtTargetAlt.Text = $"{currentPoint.TargetAltitude:F4}";
                txtTargetRA.Text = "0h 00m 00s";  // TODO: Get from Manager
                txtTargetDec.Text = "+00° 00m 00s";

                // Actual Mount Position
                txtMountAz.Text = $"{currentPoint.CurrentAzimuth:F4}";
                txtMountAlt.Text = $"{currentPoint.CurrentAltitude:F4}";

                // RMS Error
                if (azInfo != null && altInfo != null)
                {
                    double rmsAz = azInfo.TrackingRmsError_Arcsec;
                    double rmsAlt = altInfo.TrackingRmsError_Arcsec;
                    double rmsTotal = Math.Sqrt(rmsAz * rmsAz + rmsAlt * rmsAlt);

                    txtRmsAz.Text = $"{rmsAz:F2}\"\"";
                    txtRmsAlt.Text = $"{rmsAlt:F2}\"\"";
                    txtRmsTotal.Text = $"{rmsTotal:F2}\"\"";

                    // Color code RMS
                    if (rmsTotal < 5.0)
                        txtRmsTotal.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    else if (rmsTotal < 10.0)
                        txtRmsTotal.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    else
                        txtRmsTotal.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] UpdateStatusPanelFromManager error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle plate solve completion - update live image RMS display
        /// Called from background thread - must use Dispatcher!
        /// </summary>
        private void OnPlateSolveCompleted(Engine.Manager.CalibrationPoint point, PlateSolveResult result, double totalError)
        {
            try
            {
                // Must invoke to UI thread!
                Dispatcher.Invoke(() =>
                {
                    if (liveCalibrationImageControl != null)
                    {
                        // Try to update existing image
                        bool updated = liveCalibrationImageControl.UpdateCalibrationImage(
                            pointNumber: point.Index,
                            image: null,  // Don't change image
                            starsDetected: result.NumMatchedStars,
                            rmsError: totalError,
                            statusText: ""  // No overlay text (clean image)
                        );

                        if (updated)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MountCalibration] ? Updated live image for point {point.Index}: RMS={totalError:F2}\", Stars={result.NumMatchedStars}");
                        }
                        else
                        {
                            _pendingImageSolveUpdates[point.Index] = (result.NumMatchedStars, totalError);
                            System.Diagnostics.Debug.WriteLine($"[MountCalibration] ? Image not ready yet for point {point.Index} - cached solve result until image is displayed");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] OnPlateSolveCompleted error: {ex.Message}");
            }
        }


        /// <summary>
        /// Display captured image from Manager queue (NO INVOKE - already in UI thread!)
        /// </summary>
        private void DisplayCapturedImage(CalibrationImageData imageData)
        {
            try
            {
                if (liveCalibrationImageControl != null)
                {
                    // [NEW] Store CameraImageData for this point (for sharing with CameraSettingsWindow)
                    if (imageData.RawImageData != null)
                    {
                        liveCalibrationImageControl.StoreCameraImageData(imageData.PointNumber, imageData.RawImageData);
                        System.Diagnostics.Debug.WriteLine($"[MountCalibration] CameraImageData stored for point {imageData.PointNumber}: {imageData.RawImageData.Width}x{imageData.RawImageData.Height}");
                    }

                    liveCalibrationImageControl.AddCalibrationImage(
                        pointNumber: imageData.PointNumber,
                        totalPoints: _calibrationManager.TotalPoints,
                        azimuth: imageData.Azimuth,
                        altitude: imageData.Altitude,
                        image: imageData.Image,
                        starsDetected: 0,
                        rmsError: 0.0
                    );

                    // [NEW] Apply auto-stretch AFTER image is in UI!
                    if (imageData.RawImageData != null)
                    {
                        liveCalibrationImageControl.ApplyAutoStretchToLastImage();
                    }

                    // Always bring the newest capture to the main preview first.
                    liveCalibrationImageControl.SelectLatestImage();

                    if (_pendingImageSolveUpdates.TryGetValue(imageData.PointNumber, out var pendingUpdate))
                    {
                        liveCalibrationImageControl.UpdateCalibrationImage(
                            pointNumber: imageData.PointNumber,
                            image: null,
                            starsDetected: pendingUpdate.StarsDetected,
                            rmsError: pendingUpdate.RmsError,
                            statusText: "");
                        _pendingImageSolveUpdates.Remove(imageData.PointNumber);
                        System.Diagnostics.Debug.WriteLine($"[MountCalibration] Applied cached solve result to newly displayed image for point {imageData.PointNumber}");
                    }

                    // [MEMORY FIX] Release the raw image data reference from queue item
                    // The raw data is now stored in LiveCalibrationImageControl (with eviction)
                    // and saved to FITS file on disk. No need to keep it in the queue item.
                    imageData.RawImageData = null;
                    imageData.Image = null;

                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Image displayed for point {imageData.PointNumber} (NO INVOKE!)");

                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] DisplayCapturedImage error: {ex.Message}");
            }
        }



        /// <summary>
        /// Sync DataGrid points with Manager (NO INVOKE!)
        /// </summary>
        private void SyncPointsWithManager()
        {
            try
            {
                var managerPoints = _calibrationManager.GetCalibrationPoints().ToList();

                // Update existing points or add new ones
                for (int i = 0; i < managerPoints.Count; i++)
                {
                    var managerPoint = managerPoints[i]; // Engine.Manager.CalibrationPoint

                    if (i >= _pointingModelData.Count)
                    {
                        // Add new point
                        _pointingModelData.Add(CreatePointingModelPoint(managerPoint));
                    }
                    else
                    {
                        // Update existing point
                        UpdatePointingModelPoint(_pointingModelData[i], managerPoint);
                    }
                }

                while (_pointingModelData.Count > managerPoints.Count)
                {
                    _pointingModelData.RemoveAt(_pointingModelData.Count - 1);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] SyncPointsWithManager error: {ex.Message}");
            }
        }

        /// <summary>
        /// Create PointingModelPoint from Engine.Manager.CalibrationPoint
        /// </summary>
        private PointingModelPoint CreatePointingModelPoint(Engine.Manager.CalibrationPoint managerPoint)
        {
            return new PointingModelPoint
            {
                Index = managerPoint.Index,
                IsIncludedInModel = managerPoint.IsIncludedInModel,
                Azimuth = managerPoint.TargetAzimuth,
                Altitude = managerPoint.TargetAltitude,
                AzimuthRmsError_Arcsec = managerPoint.AzimuthRmsError_Arcsec,
                AltitudeRmsError_Arcsec = managerPoint.AltitudeRmsError_Arcsec,
                RaError_Arcsec = managerPoint.RaError_Arcsec,
                DecError_Arcsec = managerPoint.DecError_Arcsec,
                NumMatchedStars = managerPoint.NumMatchedStars,
                RotationAngle_Deg = managerPoint.RotationAngle_Deg,
                Status = FormatPointStatus(managerPoint)
            };
        }

        /// <summary>
        /// Update PointingModelPoint from Engine.Manager.CalibrationPoint
        /// </summary>
        private void UpdatePointingModelPoint(PointingModelPoint uiPoint, Engine.Manager.CalibrationPoint managerPoint)
        {
            uiPoint.IsIncludedInModel = managerPoint.IsIncludedInModel;

            // RMS errors (from tracking)
            uiPoint.AzimuthRmsError_Arcsec = managerPoint.AzimuthRmsError_Arcsec;
            uiPoint.AltitudeRmsError_Arcsec = managerPoint.AltitudeRmsError_Arcsec;

            // ========== PLATE SOLVING RESULTS (NEW) ==========
            uiPoint.RaError_Arcsec = managerPoint.RaError_Arcsec;
            uiPoint.DecError_Arcsec = managerPoint.DecError_Arcsec;
            uiPoint.NumMatchedStars = managerPoint.NumMatchedStars;
            uiPoint.RotationAngle_Deg = managerPoint.RotationAngle_Deg;
            // =================================================

            // Status (update to show [SOLVING] or [SOLVED])
            uiPoint.Status = FormatPointStatus(managerPoint);
        }

        /// <summary>
        /// Format point status from Engine.Manager.CalibrationPoint
        /// </summary>
        private string FormatPointStatus(Engine.Manager.CalibrationPoint managerPoint)
        {
            // [PRIORITY ORDER] Final/resolved states first, then active states

            // Solve result states (HIGHEST priority - once solved, always show result)
            if (managerPoint.IsSolved)
                return "[COMPLETED]";
            if (managerPoint.SolveFailed)
                return "[SOLVE FAILED]";

            // Plate solving in progress
            if (managerPoint.IsSolving)
                return "[SOLVING]";

            // Captured but solve not started yet (queued)
            if (managerPoint.IsCompleted && !managerPoint.IsSolved && !managerPoint.SolveFailed)
                return "[PROCESSING]";

            // Active operation states
            if (managerPoint.IsCapturing)
                return "[EXPOSING]";
            if (managerPoint.IsTracking)
                return "[TRACKING]";
            if (managerPoint.IsSlewing)
                return "[SLEWING]";

            // Default
            return "[PENDING]";
        }

        /// <summary>
        /// Update CalibrationSkyMap from Manager (NO INVOKE!)
        /// </summary>
        private void UpdateCalibrationSkyMapFromManager()
        {
            try
            {
                if (calibrationSkyMap == null) return;

                var managerPoints = _calibrationManager.GetCalibrationPoints();
                var skyMapPoints = new List<UI.CalibrationPoint>();

                foreach (var point in managerPoints)
                {
                    PointStatus status = point.IsCompleted ? PointStatus.Completed :
                                        point.IsTracking ? PointStatus.Tracking :
                                        point.IsSlewing ? PointStatus.Slewing :
                                        PointStatus.Pending;

                    double displayAz = point.TargetAzimuth;
                    double displayAlt = point.TargetAltitude;
                    
                    // Apply corrected position to ALL points (not just completed)
                    // so that pending (grey) and completed (green) dots are visually
                    // consistent on the sky chart — otherwise grey dots stay at raw
                    // positions while green dots shift to corrected, which looks wrong.
                    if (App.PointingModelManager?.IsModelLoaded == true)
                    {
                        var corrected = App.PointingModelManager.GetCorrectedPosition(
                            point.TargetAzimuth, point.TargetAltitude);
                        if (corrected.HasValue)
                        {
                            displayAz = corrected.Value.correctedAz;
                            displayAlt = corrected.Value.correctedAlt;
                        }
                    }

                    skyMapPoints.Add(new UI.CalibrationPoint
                    {
                        Azimuth = displayAz,
                        Altitude = displayAlt,
                        Status = status
                    });
                }

                calibrationSkyMap.SetPoints(skyMapPoints);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] UpdateCalibrationSkyMapFromManager error: {ex.Message}");
            }
        }

        // ========================================================================
        // BUTTON HANDLERS (NEW PATTERN - call Manager methods)
        // ========================================================================

        /// <summary>
        /// [NEW] CheckBox changed - update model calculation
        /// </summary>
        private void IncludeInModel_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_calibrationManager == null)
                    return;
                
                // Get the checkbox and its data context
                if (sender is CheckBox checkBox && checkBox.DataContext is PointingModelPoint uiPoint)
                {
                    // Sync with CalibrationManager
                    var managerPoint = _calibrationManager.CalibrationPoints.FirstOrDefault(p => p.Index == uiPoint.Index);
                    if (managerPoint != null)
                    {
                        managerPoint.IsIncludedInModel = uiPoint.IsIncludedInModel;
                        System.Diagnostics.Debug.WriteLine($"[UI] Point {uiPoint.Index} IsIncludedInModel = {uiPoint.IsIncludedInModel}");
                    }
                }

                // Rebuild model with updated inclusion flags
                // Get all solved points that are included
                int includedCount = _calibrationManager.CalibrationPoints.Count(p => p.IsSolved && p.IsIncludedInModel);
                int excludedCount = _calibrationManager.CalibrationPoints.Count(p => p.IsSolved && !p.IsIncludedInModel);

                System.Diagnostics.Debug.WriteLine($"[UI] Model points - Included: {includedCount}, Excluded: {excludedCount}");

                // Update sky map to reflect changes
                UpdateCalibrationSkyMapFromManager();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] Error in CheckBox handler: {ex.Message}");
            }
        }

        /// <summary>
        /// [REMOVED] Delete button - replaced by CheckBox
        /// </summary>
        /*
        private void BtnDeletePoint_Click(object sender, RoutedEventArgs e)
        {
            ...
        }
        */

        /// <summary>
        /// [REMOVED] Resume button - merged into Start/Resume
        /// </summary>
        /*
        private void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            ...
        }
        */


        /// <summary>
        /// [NEW] Add calibration point at current mount position
        /// - Read current Az/Alt position
        /// - Convert to RA/Dec
        /// - Start sidereal tracking
        /// - Wait for stability
        /// - Capture & solve
        /// - Add to model (or create new if first point)
        /// - Apply offset if first point
        /// </summary>
        private async void BtnAddPoint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_calibrationManager == null)
                {
                    MessageBox.Show("Calibration manager not initialized", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (_pollingManager == null || !_pollingManager.IsPolling)
                {
                    MessageBox.Show("Mount not connected!", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!await EnsureCameraReadyForCalibrationAsync(showWarning: true))
                {
                    return;
                }

                // Get current mount position
                var azInfo = _pollingManager.AzimuthInfo;
                var altInfo = _pollingManager.AltitudeInfo;

                if (azInfo == null || altInfo == null)
                {
                    MessageBox.Show("Cannot read mount position!", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // [NEW] Check if both axes are enabled
                bool azEnabled = azInfo.Is_Drive_Enabled;
                bool altEnabled = altInfo.Is_Drive_Enabled;
                
                if (!azEnabled || !altEnabled)
                {
                    string disabledAxes = "";
                    if (!azEnabled) disabledAxes += "Azimuth";
                    if (!azEnabled && !altEnabled) disabledAxes += " and ";
                    if (!altEnabled) disabledAxes += "Altitude";
                    
                    MessageBox.Show($"Please enable {disabledAxes} axis before adding calibration points.\n\n" +
                        "Go to the respective axis controller and click 'Enable' button first.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                // [REMOVED] Don't start tracking here - AddManualCalibrationPoint() handles it properly
                // with TrackingManager registration to prevent race conditions.
                // The old code was causing duplicate tracking instances.

                // [P3 FIX] Use CORRECTED position (sky coordinates) for manual point
                // This ensures the calibration point target matches true sky position
                double currentAz = _pollingManager.MountInfo?.Act_Azimuth_Pos_Correct
                    ?? ((azInfo.Raw_Position / (double)_azimuthConfig.Encoder_Resolution * 360.0) + _azimuthConfig.Position_Offset_Deg);
                double currentAlt = _pollingManager.MountInfo?.Act_Altitude_Pos_Correct
                    ?? ((altInfo.Raw_Position / (double)_altitudeConfig.Encoder_Resolution * 360.0) + _altitudeConfig.Position_Offset_Deg);

                btnAddPoint.IsEnabled = false;
                btnAddPoint.Content = "Adding...";

                SetStartButtonToStopMode();

                await _calibrationManager.AddManualCalibrationPoint(currentAz, currentAlt);

                if (!_controlLoopTimer.IsEnabled)
                {
                    _controlLoopTimer.Start();
                    _uiUpdateTimer.Start();
                }

                // Update UI
                SyncPointsWithManager();
                UpdateCalibrationSkyMapFromManager();
                UpdateSaveModelButtonState();

                System.Diagnostics.Debug.WriteLine($"[UI] Manual point capture started at Az={currentAz:F2}°, Alt={currentAlt:F2}°");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] Error adding manual point: {ex.Message}");
                MessageBox.Show($"Error adding point: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnAddPoint.IsEnabled = true;
                btnAddPoint.Content = "Add Point";
                UpdateSaveModelButtonState();
            }
        }

        /// <summary>
        /// Set Start button to Stop mode (red, shows "Stop")
        /// </summary>
        private void SetStartButtonToStopMode()
        {
            btnStartResume.Content = "Stop";
            btnStartResume.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#EF4444")); // Red
        }

        /// <summary>
        /// Set Start button to Start mode (green, shows "Start")
        /// </summary>
        private void SetStartButtonToStartMode()
        {
            btnStartResume.Content = "Start";
            btnStartResume.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#10B981")); // Green
        }

        /// <summary>
        /// Set Start button to Resume mode (green, shows "Resume")
        /// </summary>
        private void SetStartButtonToResumeMode()
        {
            btnStartResume.Content = "Resume";
            btnStartResume.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#10B981")); // Green
        }

        private void BtnCreatePoint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_calibrationManager == null) return;

                // Validate input
                if (!int.TryParse(txtAzimuthPoints.Text, out int azPoints) ||
                    !int.TryParse(txtAltitudePoints.Text, out int altPoints))
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] Invalid number of points entered");
                    return;
                }

                if (azPoints < 1 || azPoints > 36 || altPoints < 1 || altPoints > 18)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] Points out of valid range (Az:1-36, Alt:1-18)");
                    return;
                }

                // Parse min/max altitude from UI
                double minAltitude = 30.0;
                double maxAltitude = 85.0;
                if (double.TryParse(txtMinAltitude?.Text, out double parsedMin))
                    minAltitude = parsedMin;
                if (double.TryParse(txtMaxAltitude?.Text, out double parsedMax))
                    maxAltitude = parsedMax;

                // NEW PATTERN: Call Manager to create grid with altitude range
                _calibrationManager.CreateCalibrationGrid(azPoints, altPoints, minAltitude, maxAltitude);

                // Sync UI immediately (since timer not running yet)
                SyncPointsWithManager();
                UpdateCalibrationSkyMapFromManager();

                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Created calibration grid (Az={azPoints}, Alt={altPoints}, MinAlt={minAltitude}°, MaxAlt={maxAltitude}°)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error creating points: {ex.Message}");
            }
        }

        private async void BtnStartResume_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_calibrationManager == null) return;

                // Check if currently in Stop mode (button shows "Stop")
                string buttonContent = btnStartResume.Content?.ToString() ?? "";
                if (buttonContent == "Stop")
                {
                    // STOP operation - completely stop calibration
                    StopCalibration();
                    return;
                }

                if (_calibrationManager.TotalPoints == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] No points to process. Create points first.");
                    return;
                }

                if (!await EnsureCameraReadyForCalibrationAsync(showWarning: true))
                {
                    return;
                }

                // Toggle running state
                if (!_calibrationManager.IsRunning)
                {
                    // [NEW] Smart detection for Resume Failed
                    int solvedCount = _calibrationManager.CalibrationPoints.Count(p => p.IsSolved);
                    int totalCount = _calibrationManager.TotalPoints;
                    int unsolvedCount = totalCount - solvedCount;

                    // Check if there are failed points to resume
                    if (unsolvedCount > 0 && solvedCount > 0 && _calibrationManager.CurrentPointIndex > 0)
                    {
                        // Has some solved + some unsolved → Ask user
                        var result = MessageBox.Show(
                            $"Found {unsolvedCount} unsolved point(s).\n\n" +
                            $"✓ Solved: {solvedCount}\n" +
                            $"✗ Unsolved: {unsolvedCount}\n\n" +
                            $"Resume from unsolved points?",
                            "Confirmation",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            // Resume from failures
                            _calibrationManager.ResumeFromFailures();
                            System.Diagnostics.Debug.WriteLine($"[MountCalibration] Resumed from {unsolvedCount} failed points");
                        }
                        else
                        {
                            // Continue from current index
                            if (_calibrationManager.CurrentPointIndex == 0)
                            {
                                _calibrationManager.Start();
                            }
                            else
                            {
                                _calibrationManager.Resume();
                            }
                        }
                    }
                    else
                    {
                        // Normal Start/Resume
                        if (_calibrationManager.CurrentPointIndex == 0)
                        {
                            _calibrationManager.Start();
                        }
                        else
                        {
                            _calibrationManager.Resume();
                        }
                    }

                    // Change button to Stop mode
                    SetStartButtonToStopMode();

                    // Clear previous images if starting from beginning
                    if (_calibrationManager.CurrentPointIndex == 0 && liveCalibrationImageControl != null)
                    {
                        liveCalibrationImageControl.ClearAllImages();
                        System.Diagnostics.Debug.WriteLine("[LiveCalibration] Cleared previous images");
                    }

                    // NEW PATTERN: Start Manager + Start Timers
                    // (Note: Manager.Start/Resume already called above)

                    // Start both timers
                    _controlLoopTimer?.Start();
                    _uiUpdateTimer?.Start();

                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Started/Resumed from point {_calibrationManager.CurrentPointIndex + 1}");
                }
                else
                {
                    // Currently running - STOP (not just pause)
                    StopCalibration();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error in Start/Resume: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop calibration completely - stops all operations
        /// </summary>
        private void StopCalibration()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[MountCalibration] STOP button pressed - stopping all operations...");

                // 1. Stop the calibration manager
                _calibrationManager?.Stop();

                // 2. Stop control loop timer
                _controlLoopTimer?.Stop();

                // 3. Keep UI update timer running (to update status panel)
                // _uiUpdateTimer stays running

                // 4. Reset button to Start mode
                SetStartButtonToStartMode();

                // 5. Re-enable Add Point button
                btnAddPoint.IsEnabled = true;
                btnAddPoint.Content = "Add Point";
                UpdateSaveModelButtonState();

                System.Diagnostics.Debug.WriteLine("[MountCalibration] Calibration STOPPED");

                // Show status
                if (txtProgress != null)
                {
                    txtProgress.Text = "Stopped";
                }

                ShowStoppedCalibrationSummary();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error stopping calibration: {ex.Message}");
            }
        }

        private void UpdateSaveModelButtonState()
        {
            if (btnSaveModel == null)
            {
                return;
            }

            int totalPoints = _calibrationManager?.CalibrationPoints?.Count ?? 0;
            int solvedIncludedPoints = _calibrationManager?.CalibrationPoints?.Count(p => p.IsSolved && p.IsIncludedInModel) ?? 0;

            btnSaveModel.IsEnabled = _calibrationManager != null && totalPoints > 0;
            btnSaveModel.ToolTip = solvedIncludedPoints >= 3
                ? "Build and activate a runtime-ready pointing model from the latest solved points"
                : $"Save current session model snapshot ({solvedIncludedPoints} included solved point(s); 3+ recommended for a usable runtime model)";
        }

        private void BtnSaveModel_Click(object sender, RoutedEventArgs e)
        {
            SaveAndActivateCurrentPointingModel(showSuccessDialog: true);
        }

        private void SaveAndActivateCurrentPointingModel(bool showSuccessDialog)
        {
            try
            {
                if (_calibrationManager == null)
                {
                    return;
                }

                if (!_calibrationManager.TryCreateAndActivateDefaultPointingModel(out string runtimeModelPath, out string statusMessage) ||
                    string.IsNullOrWhiteSpace(runtimeModelPath) ||
                    !File.Exists(runtimeModelPath))
                {
                    MessageBox.Show(statusMessage, "Save Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (showSuccessDialog)
                {
                    MessageBox.Show(
                        $"Pointing model saved and activated.\n\n{runtimeModelPath}\n\n{statusMessage}",
                        "Save Model",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Save Model error: {ex.Message}");
                MessageBox.Show($"Failed to save the pointing model:\n{ex.Message}", "Save Model",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowStoppedCalibrationSummary()
        {
            if (_calibrationManager == null)
            {
                return;
            }

            var points = _calibrationManager.CalibrationPoints?.ToList() ?? new List<Engine.Manager.CalibrationPoint>();
            int totalPoints = points.Count;
            int solvedPoints = points.Count(p => p.IsSolved);
            int failedPoints = points.Count(p => p.SolveFailed);
            int pendingPoints = points.Count - solvedPoints - failedPoints;

            if (totalPoints == 0 || solvedPoints == 0)
            {
                return;
            }

            var solvedErrors = points.Where(p => p.IsSolved && p.TotalError_Arcsec.HasValue)
                .Select(p => p.TotalError_Arcsec.Value)
                .ToList();

            double? overallRms = solvedErrors.Count > 0 ? Math.Sqrt(solvedErrors.Average(v => v * v)) : null;
            double? avgError = solvedErrors.Count > 0 ? solvedErrors.Average() : null;
            double? maxError = solvedErrors.Count > 0 ? solvedErrors.Max() : null;
            double? minError = solvedErrors.Count > 0 ? solvedErrors.Min() : null;

            bool canUseLatestModel = solvedPoints > 0;
            string autosavePath = _calibrationManager.AutosaveModelPath;

            var summaryDialog = new CalibrationCompleteDialog
            {
                Owner = Application.Current.MainWindow,
                TotalPoints = totalPoints,
                CompletedPoints = solvedPoints + failedPoints,
                SolvedPoints = solvedPoints,
                FailedPoints = failedPoints,
                PendingPoints = pendingPoints,
                OverallRMS = overallRms,
                AverageError = avgError,
                MaxError = maxError,
                MinError = minError,
                CalibrationPoints = points,
                DialogTitle = "Calibration Stopped",
                DialogSubtitle = "Choose whether to build and use the latest pointing model, keep the previous model, or rebuild.",
                LatestModelPath = File.Exists(autosavePath) ? autosavePath : null,
                LatestModelStatus = File.Exists(autosavePath)
                    ? $"Latest session data is available. The system will build a complete runtime model before activating it."
                    : "Solved points are available. The system will build a complete runtime model before activating it.",
                CanUseLatestModel = canUseLatestModel,
                UseLatestModelButtonText = "Build And Use Model",
                ShowRestartButton = true
            };

            summaryDialog.ShowDialog();

            switch (summaryDialog.SelectedAction)
            {
                case CalibrationDialogAction.UseLatestModel:
                    SaveAndActivateCurrentPointingModel(showSuccessDialog: false);
                    break;

                case CalibrationDialogAction.KeepCurrentModel:
                    if (!string.IsNullOrWhiteSpace(_calibrationManager.ModelPathBeforeCalibration) &&
                        File.Exists(_calibrationManager.ModelPathBeforeCalibration))
                    {
                        App.PointingModelManager?.LoadModel(_calibrationManager.ModelPathBeforeCalibration);
                    }
                    break;

                case CalibrationDialogAction.RestartCalibration:
                    RestartCalibrationSession(skipConfirmation: true);
                    break;
            }
        }

        private void HandleCalibrationRestartRequested()
        {
            Dispatcher.Invoke(() => RestartCalibrationSession(skipConfirmation: true));
        }

        private string GetOverallRmsSummaryText()
        {
            if (App.PointingModelManager?.TryGetLoadedModelMetrics(out double loadedRmsArcsec, out _, out _, out _) == true)
            {
                return $"{loadedRmsArcsec:F2} arcsec";
            }

            if (_calibrationManager == null)
            {
                return "--";
            }

            if (_calibrationManager.PointingModelBackendMode == PointingModelBackendMode.ExperimentalCoreDirect)
            {
                var metrics = _calibrationManager.ExperimentalMetrics;
                return metrics?.IsCalibrated == true ? $"{metrics.RMSErrorArcsec:F2} arcsec" : "--";
            }

            var errors = _calibrationManager.GetCalibrationPoints()
                .Where(p => p.IsSolved && p.TotalError_Arcsec.HasValue)
                .Select(p => p.TotalError_Arcsec.Value)
                .ToList();

            if (errors.Count == 0)
            {
                return "--";
            }

            double overallRms = Math.Sqrt(errors.Average(v => v * v));
            return $"{overallRms:F2} arcsec";
        }

        private void CmbPointingModelBackend_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbPointingModelBackend == null)
            {
                return;
            }

            var selectedMode = GetSelectedPointingModelBackendMode();
            var calibrationSettings = AppSettings.GetMountCalibrationSettings();
            calibrationSettings.PointingModelBackendMode = selectedMode.ToString();
            AppSettings.SaveMountCalibrationSettings(calibrationSettings);

            _calibrationManager?.SetPointingModelBackendMode(selectedMode);

            if (selectedMode == PointingModelBackendMode.LegacyPxpReload)
            {
                App.PointingModelManager?.RestoreLegacyModel();
            }

            UpdateOverallRmsPointingError();
        }

        private void BtnRerun_Click(object sender, RoutedEventArgs e)
        {
            RestartCalibrationSession(skipConfirmation: false);
        }

        private void RestartCalibrationSession(bool skipConfirmation)
        {
            try
            {
                if (_calibrationManager == null) return;

                if (!skipConfirmation)
                {
                    string sessionFolder = _calibrationManager.GetSessionFitsDirectory();
                    string folderInfo = !string.IsNullOrEmpty(sessionFolder)
                        ? $"\n\n?? FITS Folder: {System.IO.Path.GetFileName(sessionFolder)}"
                        : "";

                    var result = MessageBox.Show(
                        $"Clear all calibration data and start fresh?{folderInfo}\n\n" +
                        "This will:\n" +
                        "• Delete all captured FITS images\n" +
                        "• Delete session pointing model file\n" +
                        "• Clear all calibration points\n" +
                        "• Create NEW session folder on next Start\n\n" +
                        "? Current pointing model file will be KEPT",
                        "Confirmation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result != MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Debug.WriteLine("[MountCalibration] Restart cancelled");
                        return;
                    }
                }

                if (_calibrationManager.IsRunning)
                {
                    _calibrationManager.Stop();
                }

                _controlLoopTimer?.Stop();
                _uiUpdateTimer?.Stop();
                _calibrationManager.ClearSessionFitsFiles();
                _calibrationManager.ClearCalibrationPoints();
                _pointingModelData.Clear();
                calibrationSkyMap?.ClearPoints();
                liveCalibrationImageControl?.ClearAllImages();

                if (progressBar != null)
                {
                    progressBar.Value = 0;
                }
                if (txtProgress != null)
                {
                    txtProgress.Text = "0/0";
                }
                if (txtRmsTotal != null)
                {
                    txtRmsTotal.Text = "-- arcsec";
                }

                btnStartResume.Content = "Start";
                btnStartResume.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#10B981"));

                _uiUpdateTimer?.Start();

                System.Diagnostics.Debug.WriteLine("[MountCalibration] ? Restart complete:");
                System.Diagnostics.Debug.WriteLine("[MountCalibration]    - All points cleared");
                System.Diagnostics.Debug.WriteLine("[MountCalibration]    - FITS files deleted");
                System.Diagnostics.Debug.WriteLine("[MountCalibration]    - Session model deleted");
                System.Diagnostics.Debug.WriteLine("[MountCalibration]    - Pointing model KEPT");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error in Restart: {ex.Message}");
                MessageBox.Show($"Error during restart: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnNewPointingModel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_calibrationManager?.IsRunning == true)
                {
                    MessageBox.Show("Please stop the current calibration before creating a new pointing model.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show(
                    "This will start a completely new Pointing Model session.\n\n" +
                    "The system will:\n" +
                    "• unload the current active model\n" +
                    "• clear the saved model path from config\n" +
                    "• clear all loaded points from the table\n" +
                    "• clear current calibration session data and previews\n" +
                    "• create a brand-new empty Pointing Model file for this session\n\n" +
                    "Do you want to continue?",
                    "New Pointing Model",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] New pointing model cancelled by user");
                    return;
                }

                _controlLoopTimer?.Stop();
                _uiUpdateTimer?.Stop();

                var pointingModelManager = App.PointingModelManager;
                pointingModelManager?.UnloadModel();
                System.Diagnostics.Debug.WriteLine("[MountCalibration] ✅ Pointing model unloaded");

                AppSettings.SavePointingModelFilePath(string.Empty);

                try
                {
                    var calibSettings = AppSettings.GetMountCalibrationSettings();
                    calibSettings.PointingModelFilePath = string.Empty;
                    AppSettings.SaveMountCalibrationSettings(calibSettings);
                }
                catch (Exception dbEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Warning: Failed to clear calibration DB path: {dbEx.Message}");
                }

                if (App.MountInfo != null)
                {
                    App.MountInfo.TelescopeOffset_Az_Arcsec = 0;
                    App.MountInfo.TelescopeOffset_Alt_Arcsec = 0;
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] ✅ Telescope offsets reset to 0,0");
                }

                string newSessionFile = null;
                if (_calibrationManager != null)
                {
                    newSessionFile = _calibrationManager.PrepareFreshPointingModelSession(deleteExistingSessionFiles: false);
                }

                _pointingModelData.Clear();
                calibrationSkyMap?.ClearPoints();
                liveCalibrationImageControl?.ClearAllImages();

                if (progressBar != null)
                {
                    progressBar.Value = 0;
                }

                if (txtProgress != null)
                {
                    txtProgress.Text = "0/0";
                }

                if (txtEta != null)
                {
                    txtEta.Text = "ETA: --  |  Total RMS: --  |  Solved 0  Failed 0";
                }

                UpdateProgressBarFromManager();
                UpdateOverallRmsPointingError();
                UpdateSaveModelButtonState();
                SetStartButtonToStartMode();

                var mainWindow = Application.Current.MainWindow as MainWindow;
                mainWindow?.UpdatePointingModelStatus();

                MessageBox.Show(
                    "A brand-new Pointing Model session has been created.\n\n" +
                    $"Session file: {Path.GetFileName(newSessionFile ?? "(not created)")}\n\n" +
                    "All currently loaded pointing data has been cleared from the system.",
                    "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                System.Diagnostics.Debug.WriteLine($"[MountCalibration] ✅ New fresh pointing model session created: {newSessionFile}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error creating new pointing model: {ex.Message}");
                MessageBox.Show($"Error creating new pointing model:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnLoadPointingModel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Default to CalibrationData folder
                string defaultDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData");
                if (!Directory.Exists(defaultDir))
                    defaultDir = AppDomain.CurrentDomain.BaseDirectory;

                var openFileDialog = new OpenFileDialog
                {
                    Title = "Load Pointing Model File",
                    Filter = "PlaneWave Pointing Model (*.pxp)|*.pxp|Pointing Model Data (*.pmd)|*.pmd|All Files (*.*)|*.*",
                    DefaultExt = "pxp",
                    InitialDirectory = defaultDir
                };

                if (openFileDialog.ShowDialog() != true)
                    return;

                string selectedFile = openFileDialog.FileName;
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] User selected pointing model file: {selectedFile}");

                // Validate: check if file has calibration stars
                int starCount = GetCalStarCountFromFile(selectedFile);
                if (starCount <= 0)
                {
                    MessageBox.Show(
                        $"The selected file has 0 calibration stars.\n\n" +
                        $"File: {Path.GetFileName(selectedFile)}\n\n" +
                        $"Please select a file with valid calibration data.",
                        "Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Load into global PointingModelManager (App.PointingModelManager)
                var pointingModelManager = App.PointingModelManager;
                if (pointingModelManager == null)
                {
                    MessageBox.Show(
                        "Pointing Model Manager is not initialized.\n" +
                        "Please restart the application.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                bool loaded = pointingModelManager.LoadModel(selectedFile);

                if (!loaded)
                {
                    MessageBox.Show(
                        $"Failed to load pointing model file.\n\n" +
                        $"File: {Path.GetFileName(selectedFile)}\n\n" +
                        $"The file may be corrupted or incompatible with the current mount configuration.",
                        "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Save the selected file path to database (AppSettings/MongoDB)
                try
                {
                    var calibSettings = AppSettings.GetMountCalibrationSettings();
                    calibSettings.PointingModelFilePath = selectedFile;
                    AppSettings.SaveMountCalibrationSettings(calibSettings);
                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Saved pointing model path to DB: {selectedFile}");
                }
                catch (Exception saveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Warning: Failed to save path to DB: {saveEx.Message}");
                }

                bool loadedIntoManager = false;
                int importedPointCount = 0;

                if (string.Equals(Path.GetExtension(selectedFile), ".pxp", StringComparison.OrdinalIgnoreCase) &&
                    _calibrationManager != null)
                {
                    _calibrationManager.ClearCalibrationPoints();
                    importedPointCount = _calibrationManager.LoadExistingModelPoints(selectedFile);

                    if (importedPointCount > 0)
                    {
                        SyncPointsWithManager();
                        ApplyLoadedModelDiagnosticsToUi(selectedFile);
                        UpdateCalibrationSkyMapFromManager();
                        loadedIntoManager = true;
                    }
                }

                if (!loadedIntoManager)
                {
                    LoadPointingModelFromFile(selectedFile);
                    UpdateCalibrationSkyMap();
                }

                UpdateProgressBarFromManager();
                UpdateOverallRmsPointingError();
                UpdateSaveModelButtonState();

                // Show success message
                MessageBox.Show(
                    $"Pointing model loaded successfully!\n\n" +
                    $"File: {Path.GetFileName(selectedFile)}\n" +
                    $"Calibration Stars: {(importedPointCount > 0 ? importedPointCount : starCount)}\n\n" +
                    $"The model is now active and corrections are being applied.",
                    "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                System.Diagnostics.Debug.WriteLine($"[MountCalibration] ✅ Pointing model loaded and applied: {Path.GetFileName(selectedFile)} ({starCount} stars)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error loading pointing model: {ex.Message}");
                MessageBox.Show($"Error loading pointing model:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Read "Number of Cal Stars" from a .pxp file header
        /// Returns 0 if file is empty or cannot be parsed
        /// </summary>
        private static int GetCalStarCountFromFile(string filePath)
        {
            try
            {
                using (var reader = new StreamReader(filePath))
                {
                    string line1 = reader.ReadLine(); // "Number of Cal Stars:"
                    string line2 = reader.ReadLine(); // e.g., "8"
                    if (line2 != null && int.TryParse(line2.Trim(), out int count))
                        return count;
                }
            }
            catch { }
            return 0;
        }

        private void BtnCameraSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cameraProvider == null)
                {
                    MessageBox.Show("Camera controller is not initialized!", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Create and show Camera Settings Window (non-modal)
                var cameraSettingsWindow = new CameraSettingsWindow(_cameraProvider)
                {
                    Owner = Window.GetWindow(this)
                };

                cameraSettingsWindow.CameraProviderChanged += CameraSettingsWindow_CameraProviderChanged;
                
                // [NEW] Connect CameraSettingsWindow to LiveCalibrationImageControl for image sync
                if (liveCalibrationImageControl != null)
                {
                    cameraSettingsWindow.SetLiveCalibrationControl(liveCalibrationImageControl);
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] Connected CameraSettingsWindow to LiveCalibrationImageControl");
                }

                System.Diagnostics.Debug.WriteLine("[MountCalibration] Opening Camera Settings Window...");
                cameraSettingsWindow.Show(); // Non-blocking window
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration ERROR] Failed to open Camera Settings: {ex.Message}");
                MessageBox.Show($"Failed to open Camera Settings:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CameraSettingsWindow_CameraProviderChanged(object sender, ICameraProvider cameraProvider)
        {
            _cameraProvider = cameraProvider;
            _calibrationManager?.InitializeCamera(_cameraProvider);
            if (_cameraProvider != null)
            {
                SavePreferredCameraType(_cameraProvider.CameraType);
            }
            UpdateCameraStatusIndicator();

            string providerName = _cameraProvider?.CameraName ?? "None";
            string providerType = _cameraProvider?.CameraType.ToString() ?? "None";
            System.Diagnostics.Debug.WriteLine($"[MountCalibration] Camera provider updated from settings window: {providerType} / {providerName}");
        }

        // ========================================================================
        // EVENT HANDLERS
        // ========================================================================

        private void PointsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Null check: prevent error during initialization
            if (txtAzimuthPoints == null || txtAltitudePoints == null)
                return;

            // Auto-generate grid when points are changed
            if (int.TryParse(txtAzimuthPoints.Text, out int azPoints) &&
                int.TryParse(txtAltitudePoints.Text, out int altPoints))
            {
                if (azPoints > 0 && azPoints <= 36 && altPoints > 0 && altPoints <= 18)
                {
                    // Valid range - will generate on button click
                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Grid config: Az={azPoints}, Alt={altPoints}");
                }
            }
        }

        private void TxtAltitudeRange_LostFocus(object sender, RoutedEventArgs e)
        {
            if (txtMinAltitude == null || txtMaxAltitude == null)
                return;

            if (double.TryParse(txtMinAltitude.Text, out double minAlt))
            {
                minAlt = Math.Max(0, Math.Min(89, minAlt));
                txtMinAltitude.Text = minAlt.ToString("0");
            }
            else
            {
                txtMinAltitude.Text = "30";
            }

            if (double.TryParse(txtMaxAltitude.Text, out double maxAlt))
            {
                maxAlt = Math.Max(1, Math.Min(90, maxAlt));
                txtMaxAltitude.Text = maxAlt.ToString("0");
            }
            else
            {
                txtMaxAltitude.Text = "85";
            }

            if (double.TryParse(txtMinAltitude.Text, out double min) &&
                double.TryParse(txtMaxAltitude.Text, out double max))
            {
                if (min >= max)
                {
                    txtMinAltitude.Text = "30";
                    txtMaxAltitude.Text = "85";
                }
            }

            System.Diagnostics.Debug.WriteLine($"[MountCalibration] Altitude range: {txtMinAltitude.Text}° - {txtMaxAltitude.Text}°");
        }

        private void DgPointingModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                // Null check: ensure DataGrid is loaded
                if (dgPointingModel == null || calibrationSkyMap == null)
                    return;

                if (dgPointingModel.SelectedItem is PointingModelPoint selectedPoint)
                {
                    // Update sky map to highlight selected point
                    calibrationSkyMap.SetCurrentPoint(selectedPoint.Azimuth, selectedPoint.Altitude);

                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Selected point: Az={selectedPoint.Azimuth}, Alt={selectedPoint.Altitude}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Selection error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Delete a single point when clicking the trash icon in the row
        /// </summary>
        private void BtnDeleteSinglePoint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is int pointIndex)
                {
                    DeletePointByIndex(pointIndex);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration ERROR] Delete single point failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Delete selected points from the pointing model (batch delete from bottom button)
        /// </summary>
        private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // If no selected item, show message
                if (dgPointingModel.SelectedItem == null)
                {
                    MessageBox.Show("Please select a point to delete by clicking its row.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Get selected point
                if (dgPointingModel.SelectedItem is PointingModelPoint selectedPoint)
                {
                    DeletePointByIndex(selectedPoint.Index);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration ERROR] Delete failed: {ex.Message}");
                MessageBox.Show($"Error deleting points:\n{ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Delete a point by its index
        /// </summary>
        private void DeletePointByIndex(int pointIndex)
        {
            try
            {
                // Find the point in UI collection
                var point = _pointingModelData.FirstOrDefault(p => p.Index == pointIndex);
                if (point == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MountCalibration] Point {pointIndex} not found");
                    return;
                }
                
                // Remove from CalibrationManager
                _calibrationManager?.DeleteCalibrationPoint(pointIndex);
                
                // Remove from LiveCalibrationImageControl
                liveCalibrationImageControl?.RemoveCalibrationImage(pointIndex);
                
                // Remove from UI collection
                _pointingModelData.Remove(point);
                
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Deleted point {pointIndex}");
                
                // Reindex remaining points
                ReindexPointingModelData();
                
                // Update sky map
                UpdateCalibrationSkyMap();
                
                // Update progress bar
                UpdateProgressBarFromManager();
                
                // Clear CameraSettingsWindow preview if it's showing the deleted point
                ClearCameraSettingsWindowPreview();
                
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] After delete: {_pointingModelData.Count} points remaining");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration ERROR] DeletePointByIndex failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clear CameraSettingsWindow preview image
        /// </summary>
        private void ClearCameraSettingsWindowPreview()
        {
            try
            {
                // Find open CameraSettingsWindow and clear its preview
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is CameraSettingsWindow cameraSettings)
                    {
                        cameraSettings.ClearPreviewImage();
                        System.Diagnostics.Debug.WriteLine("[MountCalibration] Cleared CameraSettingsWindow preview");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] ClearCameraSettingsWindowPreview error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Reindex pointing model data after deletion
        /// </summary>
        private void ReindexPointingModelData()
        {
            for (int i = 0; i < _pointingModelData.Count; i++)
            {
                _pointingModelData[i].Index = i + 1;
            }
        }

        // ========================================================================
        // UTILITY METHODS
        // ========================================================================

        private void GeneratePointingGrid()
        {
            try
            {
                // Null check: ensure TextBoxes are loaded
                if (txtAzimuthPoints == null || txtAltitudePoints == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] TextBoxes not yet initialized");
                    return;
                }

                if (!int.TryParse(txtAzimuthPoints.Text, out int azPoints) ||
                    !int.TryParse(txtAltitudePoints.Text, out int altPoints))
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] Invalid number of points entered");
                    return;
                }

                if (azPoints < 1 || azPoints > 36 || altPoints < 1 || altPoints > 18)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] Points out of valid range (Az:1-36, Alt:1-18)");
                    return;
                }

                _pointingModelData.Clear();

                // Calculate step sizes
                double azStep = 360.0 / azPoints;
                double altRange = 85.0 - 30.0;
                double altStep = altRange / altPoints;

                int index = 1;

                // Generate points: For each azimuth angle, create all altitude points
                for (int azIdx = 0; azIdx < azPoints; azIdx++)
                {
                    double azimuth = azIdx * azStep;

                    for (int altIdx = 0; altIdx < altPoints; altIdx++)
                    {
                        double altitude = 30.0 + (altIdx * altStep);

                        _pointingModelData.Add(new PointingModelPoint
                        {
                            Index = index++,
                            Azimuth = azimuth,
                            Altitude = altitude,
                            AzimuthError = null,
                            AltitudeError = null,
                            Status = "[PENDING]"
                        });
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Generated {_pointingModelData.Count} points");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error generating pointing grid: {ex.Message}");
            }
        }

        /// <summary>
        /// Update CalibrationSkyMap with current pointing model data
        /// </summary>
        private void UpdateCalibrationSkyMap()
        {
            try
            {
                if (calibrationSkyMap == null)
                    return;

                // Convert pointing model data to calibration points
                var calibrationPoints = new List<UI.CalibrationPoint>();

                foreach (var point in _pointingModelData)
                {
                    PointStatus status;

                    // Map text status to PointStatus enum
                    if (point.Status == "[COMPLETED]")
                    {
                        status = PointStatus.Completed;
                    }
                    else if (point.Status == "[TRACKING]")
                    {
                        status = PointStatus.Tracking;
                    }
                    else if (point.Status == "[SLEWING]")
                    {
                        status = PointStatus.Slewing;
                    }
                    else if (point.Status == "[MEASURED]")
                    {
                        status = PointStatus.Measured;
                    }
                    else if (point.AzimuthError.HasValue && point.AltitudeError.HasValue)
                    {
                        // If error values exist, check if they're within tolerance
                        double totalError = Math.Sqrt(
                            Math.Pow(point.AzimuthError.Value, 2) +
                            Math.Pow(point.AltitudeError.Value, 2));

                        status = totalError > 0.01 ? PointStatus.Error : PointStatus.Measured;
                    }
                    else
                    {
                        status = PointStatus.Pending;
                    }

                    double displayAz = point.Azimuth;
                    double displayAlt = point.Altitude;
                    
                    // Apply corrected position to ALL points (not just completed)
                    // so pending and completed dots are visually consistent.
                    if (App.PointingModelManager?.IsModelLoaded == true)
                    {
                        var corrected = App.PointingModelManager.GetCorrectedPosition(
                            point.Azimuth, point.Altitude);
                        if (corrected.HasValue)
                        {
                            displayAz = corrected.Value.correctedAz;
                            displayAlt = corrected.Value.correctedAlt;
                        }
                    }

                    calibrationPoints.Add(new UI.CalibrationPoint
                    {
                        Azimuth = displayAz,
                        Altitude = displayAlt,
                        Status = status
                    });
                }

                calibrationSkyMap.SetPoints(calibrationPoints);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Error updating sky map: {ex.Message}");
            }
        }

        private void LoadPointingModelFromFile(string filePath)
        {
            try
            {
                _pointingModelData.Clear();

                string extension = Path.GetExtension(filePath)?.ToLowerInvariant();
                if (extension == ".pxp")
                {
                    var points = ParsePxpCalibrationPoints(filePath);
                    int index = 1;
                    foreach (var point in points)
                    {
                        _pointingModelData.Add(new PointingModelPoint
                        {
                            Index = index++,
                            Azimuth = point.AzimuthDeg,
                            Altitude = point.AltitudeDeg,
                            Status = "[COMPLETED]",
                            IsIncludedInModel = true,
                            NumMatchedStars = 0
                        });
                    }
                }
                else
                {
                    var lines = File.ReadAllLines(filePath);
                    int startIndex = lines.Length > 0 && lines[0].Contains("Azimuth") ? 1 : 0;

                    int index = 1;
                    for (int i = startIndex; i < lines.Length; i++)
                    {
                        var parts = lines[i].Split(',');
                        if (parts.Length >= 2 &&
                            double.TryParse(parts[0], out double azimuth) &&
                            double.TryParse(parts[1], out double altitude))
                        {
                            double? azError = null;
                            double? altError = null;

                            if (parts.Length >= 4)
                            {
                                if (double.TryParse(parts[2], out double azErr))
                                    azError = azErr;
                                if (double.TryParse(parts[3], out double altErr))
                                    altError = altErr;
                            }

                            _pointingModelData.Add(new PointingModelPoint
                            {
                                Index = index++,
                                Azimuth = azimuth,
                                Altitude = altitude,
                                AzimuthError = azError,
                                AltitudeError = altError,
                                Status = azError.HasValue ? "[MEASURED]" : "[PENDING]"
                            });
                        }
                    }
                }

                // Update textboxes with detected grid
                DetectGridConfiguration();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to parse file: {ex.Message}");
            }
        }

        private void ApplyLoadedModelDiagnosticsToUi(string filePath)
        {
            try
            {
                var diagnostics = PointingModelManager.GetCalibrationDiagnosticsFromFile(filePath)
                    .OrderBy(d => d.Index)
                    .ToList();

                if (diagnostics.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[MountCalibration] No loaded-model diagnostics were returned for UI import");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Loaded-model diagnostics count = {diagnostics.Count}");

                var managerPoints = _calibrationManager?.GetCalibrationPoints()?.ToList();
                for (int i = 0; i < diagnostics.Count && i < _pointingModelData.Count; i++)
                {
                    var diagnostic = diagnostics[i];
                    System.Diagnostics.Debug.WriteLine(
                        $"[MountCalibration] UI diagnostic #{i + 1}: RA={diagnostic.RASkyErrorArcsec:F2}\" DEC={diagnostic.DecErrorArcsec:F2}\" TOT={diagnostic.TotalErrorArcsec:F2}\"");

                    if (managerPoints != null && i < managerPoints.Count)
                    {
                        managerPoints[i].RaError_Arcsec = diagnostic.RASkyErrorArcsec;
                        managerPoints[i].DecError_Arcsec = diagnostic.DecErrorArcsec;
                        managerPoints[i].NumMatchedStars = 0;
                    }

                    _pointingModelData[i].RaError_Arcsec = diagnostic.RASkyErrorArcsec;
                    _pointingModelData[i].DecError_Arcsec = diagnostic.DecErrorArcsec;
                    _pointingModelData[i].NumMatchedStars = 0;
                }

                System.Diagnostics.Debug.WriteLine($"[MountCalibration] Applied {diagnostics.Count} loaded-model diagnostics directly to UI");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MountCalibration] ApplyLoadedModelDiagnosticsToUi error: {ex.Message}");
            }
        }

        private List<(double AzimuthDeg, double AltitudeDeg)> ParsePxpCalibrationPoints(string filePath)
        {
            var points = new List<(double AzimuthDeg, double AltitudeDeg)>();
            var lines = File.ReadAllLines(filePath);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("Cal Star ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double? thetaEnc = null;
                double? phiEnc = null;

                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].StartsWith("Cal Star ", StringComparison.OrdinalIgnoreCase) ||
                        lines[j].StartsWith("Latitude, Longitude", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (lines[j].StartsWith("ThetaEnc, PhiEnc:", StringComparison.OrdinalIgnoreCase))
                    {
                        int thetaLine = j + 1;
                        int phiLine = j + 2;
                        if (thetaLine < lines.Length &&
                            phiLine < lines.Length &&
                            double.TryParse(lines[thetaLine].Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double thetaValue) &&
                            double.TryParse(lines[phiLine].Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double phiValue))
                        {
                            thetaEnc = thetaValue;
                            phiEnc = phiValue;
                        }
                        break;
                    }
                }

                if (thetaEnc.HasValue && phiEnc.HasValue)
                {
                    double altitudeDeg = thetaEnc.Value * 180.0 / Math.PI;
                    double azimuthDeg = phiEnc.Value * 180.0 / Math.PI;

                    while (azimuthDeg < 0.0)
                    {
                        azimuthDeg += 360.0;
                    }
                    while (azimuthDeg >= 360.0)
                    {
                        azimuthDeg -= 360.0;
                    }

                    points.Add((azimuthDeg, altitudeDeg));
                }
            }

            return points;
        }

        private void DetectGridConfiguration()
        {
            // Null check: ensure TextBoxes are loaded
            if (txtAzimuthPoints == null || txtAltitudePoints == null)
            {
                System.Diagnostics.Debug.WriteLine("[MountCalibration] TextBoxes not yet initialized in DetectGridConfiguration");
                return;
            }

            if (_pointingModelData.Count == 0)
                return;

            // Count unique azimuth and altitude values
            var uniqueAzimuths = _pointingModelData.Select(p => Math.Round(p.Azimuth, 1)).Distinct().Count();
            var uniqueAltitudes = _pointingModelData.Select(p => Math.Round(p.Altitude, 1)).Distinct().Count();

            txtAzimuthPoints.Text = uniqueAzimuths.ToString();
            txtAltitudePoints.Text = uniqueAltitudes.ToString();

            System.Diagnostics.Debug.WriteLine($"[MountCalibration] Detected grid: Az={uniqueAzimuths}, Alt={uniqueAltitudes}");
        }

        // ========================================================================
        // SETTINGS
        // ========================================================================

        public void LoadSettings()
        {
            // TODO: Load mount calibration settings from AppSettings
        }

        public bool SaveSettings()
        {
            // TODO: Save mount calibration settings to AppSettings
            return true;
        }
    }

    // ========================================================================
    // UI DATA MODEL (for DataGrid display)
    // ========================================================================

    public class PointingModelPoint : INotifyPropertyChanged
    {
        private int _index;
        private double _azimuth;
        private double _altitude;
        private double? _azimuthError;
        private double? _altitudeError;
        private double? _azimuthRmsError_Arcsec;
        private double? _altitudeRmsError_Arcsec;
        private string _status;
        private bool _isSelectedForDelete;
        private bool _isIncludedInModel = true;

        // Plate Solving properties
        private double? _raError_Arcsec;
        private double? _decError_Arcsec;
        private int _numMatchedStars;
        private double? _rotationAngle_Deg;

        // Position samples for RMS calculation
        public List<double> AzimuthSamples { get; set; } = new List<double>();
        public List<double> AltitudeSamples { get; set; } = new List<double>();

        /// <summary>
        /// Whether this point is selected for deletion
        /// </summary>
        public bool IsSelectedForDelete
        {
            get => _isSelectedForDelete;
            set { _isSelectedForDelete = value; OnPropertyChanged(nameof(IsSelectedForDelete)); }
        }

        /// <summary>
        /// Whether this point is included in the pointing model calculation
        /// </summary>
        public bool IsIncludedInModel
        {
            get => _isIncludedInModel;
            set { _isIncludedInModel = value; OnPropertyChanged(nameof(IsIncludedInModel)); }
        }

        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(nameof(Index)); }
        }

        public double Azimuth
        {
            get => _azimuth;
            set { _azimuth = value; OnPropertyChanged(nameof(Azimuth)); }
        }

        public double Altitude
        {
            get => _altitude;
            set { _altitude = value; OnPropertyChanged(nameof(Altitude)); }
        }

        public double? AzimuthError
        {
            get => _azimuthError;
            set { _azimuthError = value; OnPropertyChanged(nameof(AzimuthError)); }
        }

        public double? AltitudeError
        {
            get => _altitudeError;
            set { _altitudeError = value; OnPropertyChanged(nameof(AltitudeError)); }
        }

        public double? AzimuthRmsError_Arcsec
        {
            get => _azimuthRmsError_Arcsec;
            set { _azimuthRmsError_Arcsec = value; OnPropertyChanged(nameof(AzimuthRmsError_Arcsec)); }
        }

        public double? AltitudeRmsError_Arcsec
        {
            get => _altitudeRmsError_Arcsec;
            set { _altitudeRmsError_Arcsec = value; OnPropertyChanged(nameof(AltitudeRmsError_Arcsec)); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        // ========================================================================
        // Plate Solving Properties (NEW)
        // ========================================================================

        /// <summary>
        /// RA error from plate solving (arcseconds)
        /// </summary>
        public double? RaError_Arcsec
        {
            get => _raError_Arcsec;
            set
            {
                _raError_Arcsec = value;
                OnPropertyChanged(nameof(RaError_Arcsec));
                OnPropertyChanged(nameof(TotalError_Arcsec));
                OnPropertyChanged(nameof(IsGoodSolve));
                OnPropertyChanged(nameof(RaErrorDisplay));
                OnPropertyChanged(nameof(TotalErrorDisplay));
            }
        }

        /// <summary>
        /// Dec error from plate solving (arcseconds)
        /// </summary>
        public double? DecError_Arcsec
        {
            get => _decError_Arcsec;
            set
            {
                _decError_Arcsec = value;
                OnPropertyChanged(nameof(DecError_Arcsec));
                OnPropertyChanged(nameof(TotalError_Arcsec));
                OnPropertyChanged(nameof(IsGoodSolve));
                OnPropertyChanged(nameof(DecErrorDisplay));
                OnPropertyChanged(nameof(TotalErrorDisplay));
            }
        }

        /// <summary>
        /// Total pointing error (RMS of RA + Dec)
        /// </summary>
        public double? TotalError_Arcsec
        {
            get
            {
                if (RaError_Arcsec.HasValue && DecError_Arcsec.HasValue)
                {
                    double raErr = RaError_Arcsec.Value;
                    double decErr = DecError_Arcsec.Value;
                    return Math.Sqrt(raErr * raErr + decErr * decErr);
                }
                return null;
            }
        }

        public string RaErrorDisplay => RaError_Arcsec.HasValue ? $"{RaError_Arcsec.Value:F2}" : "--";

        public string DecErrorDisplay => DecError_Arcsec.HasValue ? $"{DecError_Arcsec.Value:F2}" : "--";

        public string TotalErrorDisplay => TotalError_Arcsec.HasValue ? $"{TotalError_Arcsec.Value:F2}" : "--";

        /// <summary>
        /// Number of stars matched by plate solver
        /// </summary>
        public int NumMatchedStars
        {
            get => _numMatchedStars;
            set
            {
                _numMatchedStars = value;
                OnPropertyChanged(nameof(NumMatchedStars));
                OnPropertyChanged(nameof(NumMatchedStarsDisplay));
            }
        }

        public string NumMatchedStarsDisplay => NumMatchedStars > 0 ? NumMatchedStars.ToString() : "--";

        /// <summary>
        /// Image rotation angle (degrees)
        /// </summary>
        public double? RotationAngle_Deg
        {
            get => _rotationAngle_Deg;
            set
            {
                _rotationAngle_Deg = value;
                OnPropertyChanged(nameof(RotationAngle_Deg));
            }
        }

        /// <summary>
        /// Is this a good solve? (Total error < 10 arcsec)
        /// </summary>
        public bool IsGoodSolve => TotalError_Arcsec.HasValue && TotalError_Arcsec.Value < 10.0;

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Helper class for MountCalibrationControl
    /// </summary>
    public static class VisualTreeHelperExtensions
    {
        /// <summary>
        /// Find visual child of specific type in visual tree
        /// </summary>
        public static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
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
    }
}
