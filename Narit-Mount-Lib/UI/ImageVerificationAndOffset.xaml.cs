using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Core;
using static ForkMount_WPF.Engine.Camera.ZWOASICameraSDK;

namespace ForkMount_WPF.UI
{
    public partial class ImageVerificationAndOffset : Window
    {
        private ZWOASIManager _camera;
        private ICameraProvider _cameraProvider;
        private CameraType _selectedCameraType = CameraType.ZWO_ASI;
        private bool _isUpdatingUI = false;
        private bool _isHandlingTabGuard = false;

        // Camera enumeration list
        private List<ScannedCameraInfo> _scannedCameras = new();

        // Countdown timer for exposure
        private System.Windows.Threading.DispatcherTimer _countdownTimer;
        private DateTime _captureStartTime;
        private double _exposureDurationSeconds;

        // Cooler status polling timer
        private System.Windows.Threading.DispatcherTimer _coolerStatusTimer;

        // Histogram stretch
        private int _histogramMin = 0;
        private int _histogramMax = 65535;
        private StretchProfileType _currentProfileType = StretchProfileType.Auto;
        private byte[] _lastRawData;
        private int _lastWidth;
        private int _lastHeight;
        private bool _lastIsRAW16;

        // Zoom / Pan
        private BitmapSource _originalImage;
        private double _currentZoom = 1.0;
        private const double MIN_ZOOM = 0.05;
        private const double MAX_ZOOM = 10.0;
        private const double ZOOM_SPEED = 0.1;
        private bool _isDragging = false;
        private Point _lastMousePosition;
        private bool _isFirstLoad = true;

        // [NEW] Thumbnail Gallery
        private ObservableCollection<CapturedImageThumbnail> _capturedImages = new ObservableCollection<CapturedImageThumbnail>();
        private int _nextImageId = 1;
        private const int MAX_CAPTURED_IMAGES = 50;
        private CapturedImageThumbnail _currentlySelectedImage;
        private Guid? _currentSeriesId = null;  // Track current series for collimation analysis

        private class ScannedCameraInfo
        {
            public CameraType? Type { get; set; }
            public string DisplayName { get; set; }
            public bool IsNoCamera => Type == null;

            public override string ToString() => DisplayName;
        }

        public ImageVerificationAndOffset(ZWOASIManager camera)
            : this(camera != null ? (ICameraProvider)new ZWOCameraProvider(camera) : null)
        {
        }

        public ImageVerificationAndOffset(ICameraProvider cameraProvider = null)
        {
            InitializeComponent();
            _cameraProvider = cameraProvider;
            _selectedCameraType = cameraProvider?.CameraType ?? CameraType.ZWO_ASI;

            if (cameraProvider is ZWOCameraProvider zwoProvider)
                _camera = zwoProvider.Manager;

            LoadSettingsFromAppSettings();
            LoadStretchProfileFromSettings();
            EnumerateCameras();

            if (_cameraProvider != null && !_cameraProvider.IsConnected)
            {
                _cameraProvider.Connect(0);
            }

            if (IsCameraConnected())
            {
                AutoDetectAndUpdateSettings();
                SetSelectedCameraOption(_cameraProvider.CameraType);
            }
            else
            {
                DisconnectCurrentCamera();
                SetSelectedCameraOption(null);
            }

            LoadCurrentSettings();
            UpdateFOVInfo();

            // Wire LostFocus for auto-save settings
            txtExposureTime.LostFocus += OnSettingLostFocus;
            txtGain.LostFocus += OnSettingLostFocus;
            cmbBinning.SelectionChanged += OnSettingChanged;
            cmbImageType.SelectionChanged += OnSettingChanged;

            // [NEW] Set parent reference for CollimationAnalysisControl
            Debug.WriteLine($"[CameraOffset] Setting parent window, collimationAnalysisControl={collimationAnalysisControl != null}");
            collimationAnalysisControl?.SetParentWindow(this);
            Debug.WriteLine($"[CameraOffset] Parent window set complete");

            // [NEW] Initialize thumbnail gallery
            thumbnailGallery.ItemsSource = _capturedImages;

            // [NEW] Wire up PointingAndTrackingControl events
            if (pointingAndTrackingControl != null)
            {
                pointingAndTrackingControl.CaptureRequested += OnPointingTrackingCaptureRequested;
                pointingAndTrackingControl.SlewRequested += OnPointingTrackingSlewRequested;
                Debug.WriteLine("[CameraOffset] PointingAndTrackingControl events wired");
            }

            // [NEW] Start cooler status polling timer
            StartCoolerStatusTimer();

            // Window closing handler
            this.Closing += OnWindowClosing;
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                StopCoolerStatusTimer();
                Debug.WriteLine("[CameraOffset] Window closing cleanup completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] Cleanup error: {ex.Message}");
            }
        }

        #region Title Bar

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        #endregion

        #region Tab Guard

        private void MainTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, MainTab) || _isHandlingTabGuard)
                return;

            if (MainTab.SelectedIndex is 1 or 2 && !IsCameraConnected())
            {
                _isHandlingTabGuard = true;

                MessageBox.Show(
                    "The camera is not connected. Please open the Camera tab and connect to a camera from the list before using this tab.",
                    "Camera Not Connected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                MainTab.SelectedIndex = 0;
                _isHandlingTabGuard = false;
            }
        }

        #endregion

        #region Capture Status

        private enum CaptureState { Idle, Exposing, Readout, Processing, Ready }

        private void SetCaptureStatus(CaptureState state)
        {
            string text;
            System.Windows.Media.Color dotColor;

            switch (state)
            {
                case CaptureState.Exposing:
                    text = "Exposing...";
                    dotColor = Color.FromRgb(245, 158, 11);   // amber
                    break;
                case CaptureState.Readout:
                    text = "Readout...";
                    dotColor = Color.FromRgb(96, 165, 250);   // blue
                    break;
                case CaptureState.Processing:
                    text = "Processing...";
                    dotColor = Color.FromRgb(168, 85, 247);   // purple
                    break;
                case CaptureState.Ready:
                    text = "Ready";
                    dotColor = Color.FromRgb(16, 185, 129);   // green
                    break;
                default:
                    text = "Idle";
                    dotColor = Color.FromRgb(107, 114, 128);  // gray
                    break;
            }

            txtCaptureStatus.Text = text;
            txtCaptureStatus.Foreground = new SolidColorBrush(dotColor);
            captureStatusDot.Fill = new SolidColorBrush(dotColor);
        }

        #endregion

        #region Load Settings

        private void LoadSettingsFromAppSettings()
        {
            try
            {
                var saved = AppSettings.GetCameraSettings();
                _isUpdatingUI = true;

                txtExposureTime.Text = saved.ExposureTime_ms.ToString();
                sliderGain.Value = saved.Gain;
                txtGain.Text = saved.Gain.ToString();

                for (int i = 0; i < cmbBinning.Items.Count; i++)
                {
                    if (cmbBinning.Items[i] is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int bin) && bin == saved.Binning)
                    {
                        cmbBinning.SelectedIndex = i;
                        break;
                    }
                }

                for (int i = 0; i < cmbImageType.Items.Count; i++)
                {
                    if (cmbImageType.Items[i] is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int type) && type == (int)saved.ImageType)
                    {
                        cmbImageType.SelectedIndex = i;
                        break;
                    }
                }

                _isUpdatingUI = false;
            }
            catch (Exception ex)
            {
                _isUpdatingUI = false;
                Debug.WriteLine($"[CameraOffset] LoadSettings error: {ex.Message}");
            }
        }

        private void LoadStretchProfileFromSettings()
        {
            try
            {
                var profile = AppSettings.GetCameraOffsetStretchProfile();
                _currentProfileType = profile.ProfileType;
                _histogramMin = profile.MinValue;
                _histogramMax = profile.MaxValue;

                _isUpdatingUI = true;
                SelectProfileInComboBox(_currentProfileType);
                if (sliderHistogramMin != null)
                {
                    sliderHistogramMin.Value = _histogramMin;
                    sliderHistogramMax.Value = _histogramMax;
                    txtHistogramMin.Text = _histogramMin.ToString();
                    txtHistogramMax.Text = _histogramMax.ToString();
                }
                UpdateProfileInfo();
                _isUpdatingUI = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] LoadStretchProfile error: {ex.Message}");
            }
        }

        private void SaveStretchProfileToSettings()
        {
            try
            {
                var profile = new HistogramStretchProfile
                {
                    ProfileType = _currentProfileType,
                    MinValue = _histogramMin,
                    MaxValue = _histogramMax
                };
                AppSettings.SaveCameraOffsetStretchProfile(profile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] SaveStretchProfile error: {ex.Message}");
            }
        }

        // Disabled-state label brushes
        private static readonly SolidColorBrush _lblEnabled = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        private static readonly SolidColorBrush _lblDisabled = new SolidColorBrush(Color.FromRgb(0x2D, 0x3D, 0x55));
        private static readonly SolidColorBrush _unitEnabled = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private static readonly SolidColorBrush _unitDisabled = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

        /// <summary>
        /// Disable all camera settings controls (no camera connected)
        /// </summary>
        private void DisableCameraControls()
        {
            txtExposureTime.IsEnabled = false;
            sliderGain.IsEnabled = false;
            txtGain.IsEnabled = false;
            cmbBinning.IsEnabled = false;
            cmbImageType.IsEnabled = false;
            chkCoolerOn.IsEnabled = false;
            txtCoolerTemp.IsEnabled = false;
            btnTestCapture.IsEnabled = false;

            // Dim sliders
            sliderGain.Opacity = 0.3;
            chkCoolerOn.Opacity = 0.3;

            // Dim labels: iterate direct children of the camera settings grid
            foreach (var child in gridCameraSettings.Children)
            {
                if (child is TextBlock tb)
                {
                    // Camera row label stays bright (row 0)
                    if (Grid.GetRow(tb) == 0) continue;
                    tb.Foreground = tb.Text == "ms" || tb.Text.EndsWith("C") ? _unitDisabled : _lblDisabled;
                }
            }

            // Cooler row labels
            foreach (var child in gridCoolerRow.Children)
            {
                if (child is TextBlock tb)
                    tb.Foreground = _unitDisabled;
            }
        }

        /// <summary>
        /// Enable camera settings controls based on current camera capabilities.
        /// </summary>
        private void EnableCameraControls()
        {
            bool supportsGain = _cameraProvider?.SupportsGain == true;
            bool supportsImageType = _cameraProvider?.SupportsImageType == true;
            bool hasCooler = _cameraProvider?.HasCooler == true;

            txtExposureTime.IsEnabled = true;
            sliderGain.IsEnabled = supportsGain;
            txtGain.IsEnabled = supportsGain;
            cmbBinning.IsEnabled = true;
            cmbImageType.IsEnabled = supportsImageType;
            chkCoolerOn.IsEnabled = hasCooler;
            txtCoolerTemp.IsEnabled = hasCooler;
            btnTestCapture.IsEnabled = true;

            sliderGain.Opacity = supportsGain ? 1.0 : 0.3;
            chkCoolerOn.Opacity = hasCooler ? 1.0 : 0.3;

            if (!supportsGain)
            {
                _isUpdatingUI = true;
                sliderGain.Value = 0;
                txtGain.Text = "0";
                _isUpdatingUI = false;
            }

            if (!supportsImageType)
            {
                foreach (ComboBoxItem item in cmbImageType.Items)
                {
                    if (item.Tag is string tag && int.TryParse(tag, out int type) && type == (int)ASI_IMG_TYPE.ASI_IMG_RAW16)
                    {
                        cmbImageType.SelectedItem = item;
                        break;
                    }
                }
            }

            // Restore labels
            foreach (var child in gridCameraSettings.Children)
            {
                if (child is TextBlock tb)
                {
                    tb.Foreground = tb.Text == "ms" || tb.Text.EndsWith("C") ? _unitEnabled : _lblEnabled;
                }
            }

            foreach (var child in gridCoolerRow.Children)
            {
                if (child is TextBlock tb)
                    tb.Foreground = tb.Text.EndsWith("C") ? _unitEnabled : new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
            }
        }

                /// <summary>
        /// Populate the ComboBox with the supported camera families only.
        /// </summary>
        private void EnumerateCameras()
        {
            _isUpdatingUI = true;
            cmbCamera.Items.Clear();
            _scannedCameras.Clear();

            _scannedCameras.Add(new ScannedCameraInfo { Type = null, DisplayName = "No Camera" });
            _scannedCameras.Add(new ScannedCameraInfo { Type = CameraType.ZWO_ASI, DisplayName = "ZWO" });
            _scannedCameras.Add(new ScannedCameraInfo { Type = CameraType.FLI, DisplayName = "FLI" });

            foreach (var cameraInfo in _scannedCameras)
            {
                cmbCamera.Items.Add(new ComboBoxItem { Content = cameraInfo.DisplayName, Tag = cameraInfo });
            }

            cmbCamera.SelectedIndex = IsCameraConnected() && _cameraProvider != null
                ? (_cameraProvider.CameraType == CameraType.FLI ? 2 : 1)
                : 0;

            _isUpdatingUI = false;
        }
        private void AutoDetectAndUpdateSettings()
        {
            if (_cameraProvider == null || !_cameraProvider.IsConnected) return;
            try
            {
                var settings = AppSettings.GetCameraSettings();
                int w = _cameraProvider.MaxWidth;
                int h = _cameraProvider.MaxHeight;
                double px = _cameraProvider.PixelSize;
                if (settings.Width != w || settings.Height != h || Math.Abs(settings.PixelSize_um - px) > 0.01)
                {
                    settings.Width = w;
                    settings.Height = h;
                    settings.PixelSize_um = px;
                    settings.PlateScale_ArcsecPerPixel = settings.FocalLength_mm > 0
                        ? (px * settings.Binning / settings.FocalLength_mm) * 206.265
                        : settings.PlateScale_ArcsecPerPixel;
                    AppSettings.SaveCameraSettings(settings);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] AutoDetect error: {ex.Message}");
            }
        }
        private void LoadCurrentSettings()
        {
            try
            {
                if (!IsCameraConnected())
                {
                    statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    txtCameraStatus.Text = "Not Connected";
                    txtCameraStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    DisableCameraControls();
                    return;
                }
                EnableCameraControls();
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                txtCameraStatus.Text = GetCameraStatusText();
                txtCameraStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] LoadCurrentSettings error: {ex.Message}");
            }
        }
        private string GetCameraStatusText()
        {
            if (!IsCameraConnected())
                return "Not Connected";
            if (_cameraProvider is ZWOCameraProvider zwoProvider)
            {
                string usbSpeed = zwoProvider.Manager.IsUSB3Host ? "USB3.0" : "USB2.0";
                return $"{_cameraProvider.CameraName} ({usbSpeed})";
            }
            return $"{_cameraProvider.CameraName} (FLI CCD)";
        }
        private bool IsCameraConnected()
        {
            return _cameraProvider != null && _cameraProvider.IsConnected;
        }

        private void SetSelectedCameraOption(CameraType? cameraType)
        {
            _isUpdatingUI = true;

            foreach (ComboBoxItem item in cmbCamera.Items)
            {
                if (item.Tag is ScannedCameraInfo info && info.Type == cameraType)
                {
                    cmbCamera.SelectedItem = item;
                    break;
                }
            }

            _isUpdatingUI = false;
        }

        private void DisconnectCurrentCamera()
        {
            try
            {
                _cameraProvider?.Disconnect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] Disconnect error: {ex.Message}");
            }

            _cameraProvider = null;
            _camera = null;
        }

        private static string GetCameraTypeLabel(CameraType cameraType)
        {
            return cameraType == CameraType.FLI ? "FLI" : "ZWO";
        }

        private void ConnectToScannedCamera(ScannedCameraInfo camInfo)
        {
            if (camInfo == null)
                return;

            if (camInfo.IsNoCamera)
            {
                DisconnectCurrentCamera();
                LoadCurrentSettings();
                UpdateFOVInfo();
                SimpleLogger.Info("[Camera] Camera disconnected (No Camera selected).", "System");
                return;
            }

            string cameraTypeLabel = GetCameraTypeLabel(camInfo.Type.Value);

            try
            {
                DisconnectCurrentCamera();

                if (camInfo.Type == CameraType.ZWO_ASI)
                {
                    var manager = new ZWOASIManager();
                    _camera = manager;
                    _cameraProvider = new ZWOCameraProvider(manager);
                    _selectedCameraType = CameraType.ZWO_ASI;
                }
                else
                {
                    var manager = new FLICameraManager();
                    _camera = null;
                    _cameraProvider = new FLICameraProvider(manager);
                    _selectedCameraType = CameraType.FLI;
                }

                bool connected = _cameraProvider.Connect(0);
                if (connected)
                {
                    AutoDetectAndUpdateSettings();
                    LoadCurrentSettings();
                    UpdateFOVInfo();
                    SimpleLogger.Success($"[Camera] Connected to {cameraTypeLabel} camera: {_cameraProvider.CameraName}", "System");
                    Debug.WriteLine($"[CameraOffset] Connected to {cameraTypeLabel}");
                    return;
                }

                SimpleLogger.Error($"[Camera] {cameraTypeLabel} CCD/camera not found or could not connect.", "System");
                Debug.WriteLine($"[CameraOffset] Failed to connect to {cameraTypeLabel}");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[Camera] Failed to connect {cameraTypeLabel} CCD/camera: {ex.Message}", "System");
                Debug.WriteLine($"[CameraOffset] Camera switch error: {ex.Message}");
            }

            DisconnectCurrentCamera();
            SetSelectedCameraOption(null);
            LoadCurrentSettings();
            UpdateFOVInfo();
        }
        private bool ApplyCaptureSettings(int exposureTime_us, int gain, int binning, ASI_IMG_TYPE imageType, bool showValidationMessage = false)
        {
            if (!IsCameraConnected())
                return false;
            try
            {
                if (_cameraProvider is ZWOCameraProvider zwoProvider)
                {
                    if (binning > 1 && imageType == ASI_IMG_TYPE.ASI_IMG_RGB24)
                    {
                        if (showValidationMessage)
                        {
                            MessageBox.Show("RGB24 does not support binning > 1x1.\nUse RAW16 or set binning to 1x1.",
                                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        return false;
                    }
                    int fullWidth = zwoProvider.MaxWidth;
                    int fullHeight = zwoProvider.MaxHeight;
                    if (binning > 1)
                    {
                        fullWidth = (fullWidth / binning) * binning;
                        fullHeight = (fullHeight / binning) * binning;
                    }
                    if (!zwoProvider.SetROIAndImageType(fullWidth, fullHeight, binning, imageType, out _))
                    {
                        return false;
                    }
                    if (!_cameraProvider.SetExposure(Math.Max(1, exposureTime_us / 1000)))
                    {
                        return false;
                    }
                    if (_cameraProvider.SupportsGain)
                    {
                        zwoProvider.SetGain(gain);
                    }
                    if (_cameraProvider.SupportsOffset)
                    {
                        var savedSettings = AppSettings.GetCameraSettings();
                        zwoProvider.SetOffset(savedSettings.Offset);
                    }
                    return true;
                }
                bool exposureApplied = _cameraProvider.SetExposure(Math.Max(1, exposureTime_us / 1000));
                bool binningApplied = _cameraProvider.SetBinning(Math.Max(1, binning));
                return exposureApplied && binningApplied;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] ApplyCaptureSettings error: {ex.Message}");
                return false;
            }
        }
        private void UpdateFOVInfo()
        {
            if (txtFOVInfo == null || cmbBinning == null) return;
            try
            {
                var settings = AppSettings.GetCameraSettings();
                double fl = settings.FocalLength_mm;
                double pixelSize = IsCameraConnected() ? _cameraProvider.PixelSize : settings.PixelSize_um;
                int sensorWidth = IsCameraConnected() ? _cameraProvider.MaxWidth : settings.Width;
                int sensorHeight = IsCameraConnected() ? _cameraProvider.MaxHeight : settings.Height;
                if (fl > 0 && pixelSize > 0 && sensorWidth > 0)
                {
                    int binning = 1;
                    if (cmbBinning.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                        int.TryParse(tag, out binning);
                    double effectivePixel = pixelSize * binning;
                    double plateScale = (effectivePixel / fl) * 206.265;
                    if (pointingAndTrackingControl != null)
                    {
                        pointingAndTrackingControl.PixelScale_arcsecPerPixel = plateScale;
                    }
                    double fovW = (sensorWidth / binning) * plateScale / 3600.0;
                    double fovH = (sensorHeight / binning) * plateScale / 3600.0;
                    txtFOVInfo.Text = $"FOV: {fovW:F2}? x {fovH:F2}? ({plateScale:F2}\"/px)";
                }
                else
                {
                    txtFOVInfo.Text = "FOV: --";
                }
            }
            catch
            {
                txtFOVInfo.Text = "FOV: --";
            }
        }

        #endregion

        #region Camera Control Events

        /// <summary>
        /// User selected a different camera from the ComboBox
        /// </summary>
        private void CmbCamera_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI || cmbCamera.SelectedItem == null) return;
            if (cmbCamera.SelectedItem is ComboBoxItem selected && selected.Tag is ScannedCameraInfo camInfo)
            {
                ConnectToScannedCamera(camInfo);
            }
        }

        private void SliderGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUI || txtGain == null) return;
            _isUpdatingUI = true;
            txtGain.Text = ((int)sliderGain.Value).ToString();
            _isUpdatingUI = false;
        }

        private void TxtGain_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (int.TryParse(txtGain.Text, out int gain) && gain >= 0 && gain <= 600)
            {
                _isUpdatingUI = true;
                sliderGain.Value = gain;
                _isUpdatingUI = false;
            }
        }

        private void CmbBinning_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            UpdateFOVInfo();
        }

        private void CmbImageType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Placeholder for future logic
        }

        #endregion

        #region Auto-Save Settings (LostFocus / SelectionChanged)

        private void OnSettingLostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            SaveSettingsToAppSettings();
        }

        private void OnSettingChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            SaveSettingsToAppSettings();
        }

        private void SaveSettingsToAppSettings()
        {
            try
            {
                var settings = AppSettings.GetCameraSettings();
                if (int.TryParse(txtExposureTime.Text, out int exp)) settings.ExposureTime_ms = exp;
                if (int.TryParse(txtGain.Text, out int gain)) settings.Gain = gain;
                if (cmbBinning.SelectedItem is ComboBoxItem binItem && binItem.Tag is string binTag && int.TryParse(binTag, out int bin))
                    settings.Binning = bin;
                if (cmbImageType.SelectedItem is ComboBoxItem typeItem && typeItem.Tag is string typeTag && int.TryParse(typeTag, out int imgType))
                    settings.ImageType = imgType;

                AppSettings.SaveCameraSettings(settings);
                Debug.WriteLine("[CameraOffset] Settings auto-saved");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] Auto-save error: {ex.Message}");
            }
        }

        #endregion

        #region Capture

        private void ApplySettingsToCamera()
        {
            if (!IsCameraConnected()) return;
            try
            {
                var (exposureTime_us, gain, binning, imageType) = GetCurrentCameraSettings();
                ApplyCaptureSettings(exposureTime_us, gain, binning, imageType, showValidationMessage: true);
                Debug.WriteLine("[CameraOffset] Settings applied to camera");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] ApplySettings error: {ex.Message}");
            }
        }
        private async void BtnTestCapture_Click(object sender, RoutedEventArgs e)
        {
            if (!IsCameraConnected())
            {
                MessageBox.Show("Camera is not connected!", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            try
            {
                if (int.TryParse(txtExposureTime.Text, out int exposureMs))
                    _exposureDurationSeconds = exposureMs / 1000.0;
                else
                    _exposureDurationSeconds = 1.0;
                loadingPanel.Visibility = Visibility.Visible;
                btnTestCapture.IsEnabled = false;
                imgPreview.Source = null;
                txtPlaceholder.Visibility = Visibility.Collapsed;
                SetCaptureStatus(CaptureState.Exposing);
                _captureStartTime = DateTime.Now;
                StartCountdownTimer();
                var (exposureTime_us, gain, binning, imageType) = GetCurrentCameraSettings();
                if (!ApplyCaptureSettings(exposureTime_us, gain, binning, imageType, showValidationMessage: true))
                {
                    return;
                }
                var imageData = await Task.Run(() => _cameraProvider.CaptureImage());
                StopCountdownTimer();
                SetCaptureStatus(CaptureState.Readout);
                if (imageData != null)
                {
                    SetCaptureStatus(CaptureState.Processing);
                    _lastRawData = imageData.ImageData;
                    _lastWidth = imageData.Width;
                    _lastHeight = imageData.Height;
                    _lastIsRAW16 = imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16;
                    if (_currentProfileType != StretchProfileType.Manual)
                    {
                        ApplyProfileStretch();
                    }
                    await RestretchAndDisplay();
                    if (_isFirstLoad)
                    {
                        _isFirstLoad = false;
                        FitImageToWindow();
                    }
                    txtImageInfo.Text = $"{imageData.Width}x{imageData.Height} {imageData.GetBitsPerPixel()}-bit";
                    Debug.WriteLine($"[CameraOffset] Captured: {imageData.Width}x{imageData.Height}");
                    await autoSaveFitsControl.OnImageCaptured(imageData);
                    AddToThumbnailGallery(imageData, _originalImage);
                    SetCaptureStatus(CaptureState.Ready);
                }
                else
                {
                    string details = string.IsNullOrWhiteSpace(_cameraProvider.LastErrorMessage)
                        ? "Capture returned no image."
                        : $"Capture returned no image.\n\nDetails: {_cameraProvider.LastErrorMessage}";
                    MessageBox.Show(details, "Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetCaptureStatus(CaptureState.Idle);
                }
            }
            catch (Exception ex)
            {
                StopCountdownTimer();
                Debug.WriteLine($"[CameraOffset] Capture error: {ex.Message}");
                MessageBox.Show($"Capture failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetCaptureStatus(CaptureState.Idle);
            }
            finally
            {
                loadingPanel.Visibility = Visibility.Collapsed;
                btnTestCapture.IsEnabled = true;
            }
        }
        private void StartCountdownTimer()
        {
            _countdownTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _countdownTimer.Tick += (s, e) =>
            {
                double elapsed = (DateTime.Now - _captureStartTime).TotalSeconds;
                double remaining = Math.Max(0, _exposureDurationSeconds - elapsed);
                txtCountdown.Text = $"{remaining:F2} s";
            };
            _countdownTimer.Start();
        }

        private void StopCountdownTimer()
        {
            _countdownTimer?.Stop();
            _countdownTimer = null;
        }

        #endregion

        #region Telescope Offset

        private void BtnOffset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string direction) return;

            if (!double.TryParse(txtOffsetStep.Text, out double step) || step <= 0)
            {
                MessageBox.Show("Please enter a valid step size (> 0).", "Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double dAz = 0, dAlt = 0;
            double diag = step / Math.Sqrt(2);

            switch (direction)
            {
                case "N":  dAlt = step; break;
                case "S":  dAlt = -step; break;
                case "E":  dAz = step; break;
                case "W":  dAz = -step; break;
                case "NE": dAz = diag; dAlt = diag; break;
                case "NW": dAz = -diag; dAlt = diag; break;
                case "SE": dAz = diag; dAlt = -diag; break;
                case "SW": dAz = -diag; dAlt = -diag; break;
            }

            // Update MountInfo
            var mountInfo = App.MountInfo;
            if (mountInfo != null)
            {
                mountInfo.TelescopeOffset_Az_Arcsec += dAz;
                mountInfo.TelescopeOffset_Alt_Arcsec += dAlt;
            }

            bool isRaDec = GetIsRaDecMode();
            UpdateOffsetDisplay();
            Debug.WriteLine($"[CameraOffset] Offset {direction} ({(isRaDec ? "RA/Dec" : "Alt/Azm")}): d1={dAz:F2}\", d2={dAlt:F2}\"");
        }

        private void BtnResetOffset_Click(object sender, RoutedEventArgs e)
        {
            var mountInfo = App.MountInfo;
            if (mountInfo != null)
            {
                mountInfo.TelescopeOffset_Az_Arcsec = 0;
                mountInfo.TelescopeOffset_Alt_Arcsec = 0;
            }

            UpdateOffsetDisplay();
            Debug.WriteLine("[CameraOffset] Offset reset to 0,0");
        }

        private bool GetIsRaDecMode()
        {
            if (cmbOffsetType?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag == "RaDec";
            return true;
        }

        private void UpdateOffsetDisplay()
        {
            bool isRaDec = GetIsRaDecMode();
            txtOffsetLabel1.Text = isRaDec ? "RA:" : "Azimuth:";
            txtOffsetLabel2.Text = isRaDec ? "Dec:" : "Altitude:";

            var mountInfo = App.MountInfo;
            if (mountInfo != null)
            {
                txtOffsetAz.Text = $"{mountInfo.TelescopeOffset_Az_Arcsec:F2}\"";
                txtOffsetAlt.Text = $"{mountInfo.TelescopeOffset_Alt_Arcsec:F2}\"";
            }
            else
            {
                txtOffsetAz.Text = "0.00\"";
                txtOffsetAlt.Text = "0.00\"";
            }
        }

        #endregion

        #region Cooler Control

        private void ChkCoolerOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI || !IsCameraConnected()) return;
            bool enable = chkCoolerOn.IsChecked == true;
            if (!_cameraProvider.HasCooler)
            {
                _isUpdatingUI = true;
                chkCoolerOn.IsChecked = false;
                _isUpdatingUI = false;
                txtCoolerStatus.Text = "Camera has no cooler";
                return;
            }
            Debug.WriteLine($"[CameraOffset] Setting cooler: {(enable ? "ON" : "OFF")}");
            bool result;
            if (enable)
            {
                if (!double.TryParse(txtCoolerTemp.Text, out double temp))
                {
                    temp = -10;
                    txtCoolerTemp.Text = temp.ToString("F0");
                }
                result = _cameraProvider.SetTargetTemperature(temp);
            }
            else
            {
                result = _cameraProvider.SetCoolerOff();
            }
            if (!result)
            {
                Debug.WriteLine("[CameraOffset] Failed to set cooler state");
                txtCoolerStatus.Text = "Failed to set cooler";
                return;
            }
            UpdateCoolerStatus();
        }
        private void TxtCoolerTemp_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!IsCameraConnected() || !_cameraProvider.HasCooler) return;
            if (chkCoolerOn.IsChecked != true) return;
            if (double.TryParse(txtCoolerTemp.Text, out double temp))
            {
                _cameraProvider.SetTargetTemperature(temp);
                Debug.WriteLine($"[CameraOffset] Cooler target set to {temp:F1}?C");
            }
            UpdateCoolerStatus();
        }
        private void UpdateCoolerStatus()
        {
            if (!IsCameraConnected() || !_cameraProvider.HasCooler)
            {
                txtCoolerStatus.Text = "";
                return;
            }
            try
            {
                var currentTemp = _cameraProvider.GetTemperature();
                var power = _cameraProvider.GetCoolerPower();
                bool on = chkCoolerOn.IsChecked == true;
                string status = on ? "Cooler ON" : "Cooler OFF";
                if (currentTemp.HasValue)
                {
                    status += $" | {currentTemp.Value:F1}?C";
                }
                if (power.HasValue)
                {
                    status += $" | Power: {power.Value}%";
                }
                else
                {
                    status += " | Power: --";
                }
                txtCoolerStatus.Text = status;
                Debug.WriteLine($"[CameraOffset] Cooler status: {status}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] UpdateCoolerStatus error: {ex.Message}");
                txtCoolerStatus.Text = "Error reading status";
            }
        }
        /// <summary>
        /// Start cooler status polling timer (updates every 2 seconds)
        /// </summary>
        private void StartCoolerStatusTimer()
        {
            _coolerStatusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _coolerStatusTimer.Tick += (s, e) =>
            {
                if (IsCameraConnected() && _cameraProvider.HasCooler)
                {
                    UpdateCoolerStatus();
                }
            };
            _coolerStatusTimer.Start();
            Debug.WriteLine("[CameraOffset] Cooler status timer started (2s interval)");
        }
        /// <summary>
        /// Stop cooler status timer
        /// </summary>
        private void StopCoolerStatusTimer()
        {
            if (_coolerStatusTimer != null)
            {
                _coolerStatusTimer.Stop();
                _coolerStatusTimer = null;
                Debug.WriteLine("[CameraOffset] Cooler status timer stopped");
            }
        }
        /// <summary>
        /// Capture image for collimation analysis (called by CollimationAnalysisControl)
        /// Reuses the existing capture logic from BtnTestCapture_Click
        /// </summary>
        public async Task<CameraImageData> CaptureImageForAnalysis()
        {
            try
            {
                Debug.WriteLine("[CameraOffset] CaptureImageForAnalysis called");
                if (!IsCameraConnected())
                {
                    Debug.WriteLine("[CameraOffset] Camera not connected");
                    return null;
                }
                var imageData = await Task.Run(() => PerformCapture());
                return imageData;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] CaptureImageForAnalysis error: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// Perform capture with current settings (extracted from BtnTestCapture_Click)
        /// </summary>
        private CameraImageData PerformCapture()
        {
            try
            {
                var (exposureTime_us, gain, binning, imageType) = GetCurrentCameraSettings();
                if (!ApplyCaptureSettings(exposureTime_us, gain, binning, imageType))
                {
                    return null;
                }
                return _cameraProvider.CaptureImage();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] PerformCapture error: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// Capture a series of images for collimation analysis with series metadata
        /// </summary>
        public async Task<List<CameraImageData>> CaptureImageSeriesForAnalysis(int count, Guid seriesId)
        {
            var images = new List<CameraImageData>();
            try
            {
                Debug.WriteLine($"[CameraOffset] Starting series capture: {count} images, SeriesId={seriesId:N}");
                var (exposureTime_us, gain, binning, imageType) = GetCurrentCameraSettings();
                Debug.WriteLine($"[CameraOffset] Series settings: Exposure={exposureTime_us / 1000.0}ms, Gain={gain}, Binning={binning}x{binning}, Type={imageType}");
                for (int i = 0; i < count; i++)
                {
                    if (!IsCameraConnected())
                    {
                        Debug.WriteLine("[CameraOffset] Camera disconnected during series");
                        break;
                    }
                    Debug.WriteLine($"[CameraOffset] Capturing series image {i + 1}/{count}...");
                    var imageData = await Task.Run(() => PerformCaptureWithSettings(exposureTime_us, gain, binning, imageType));
                    if (imageData != null)
                    {
                        images.Add(imageData);
                        _lastRawData = imageData.ImageData;
                        _lastWidth = imageData.Width;
                        _lastHeight = imageData.Height;
                        _lastIsRAW16 = imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16;
                        if (_currentProfileType != StretchProfileType.Manual)
                        {
                            ApplyProfileStretch();
                        }
                        await RestretchAndDisplay();
                        await AddToThumbnailGalleryInternal(imageData, _originalImage, seriesId, i + 1);
                        Debug.WriteLine($"[CameraOffset] Series image {i + 1}/{count} captured successfully");
                    }
                    else
                    {
                        Debug.WriteLine($"[CameraOffset] Series image {i + 1}/{count} capture failed");
                    }
                    if (i < count - 1)
                    {
                        await Task.Delay(100);
                    }
                }
                Debug.WriteLine($"[CameraOffset] Series capture complete: {images.Count}/{count} images");
                _currentSeriesId = seriesId;
                return images;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] CaptureImageSeriesForAnalysis error: {ex.Message}");
                return images;
            }
        }
        /// <summary>
        /// Perform capture with PRE-READ settings (safe for background thread)
        /// </summary>
        private CameraImageData PerformCaptureWithSettings(int exposureTime_us, int gain, int binning, ASI_IMG_TYPE imageType)
        {
            try
            {
                if (!ApplyCaptureSettings(exposureTime_us, gain, binning, imageType))
                {
                    return null;
                }
                return _cameraProvider.CaptureImage();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] PerformCaptureWithSettings error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get all images from the current series
        /// </summary>
        public List<CapturedImageThumbnail> GetImagesFromCurrentSeries()
        {
            if (!_currentSeriesId.HasValue)
                return new List<CapturedImageThumbnail>();

            return _capturedImages
                .Where(img => img.SeriesId == _currentSeriesId)
                .OrderBy(img => img.SeriesIndex)
                .ToList();
        }

        #endregion

        #region Thumbnail Gallery

        /// <summary>
        /// Add captured image to thumbnail gallery
        /// </summary>
        private async void AddToThumbnailGallery(CameraImageData imageData, BitmapSource fullImage)
        {
            await AddToThumbnailGalleryInternal(imageData, fullImage, null, null);
        }

        /// <summary>
        /// Add captured image to thumbnail gallery with optional series metadata
        /// </summary>
        private async Task AddToThumbnailGalleryInternal(CameraImageData imageData, BitmapSource fullImage, Guid? seriesId, int? seriesIndex)
        {
            try
            {
                // Capture stretch profile name before going async
                string profileName = "Unknown";
                try
                {
                    if (cmbStretchProfile != null && cmbStretchProfile.SelectedItem is ComboBoxItem selectedItem)
                    {
                        profileName = selectedItem.Content?.ToString() ?? "Unknown";
                    }
                }
                catch { }

                int stretchMin = _histogramMin;
                int stretchMax = _histogramMax;
                int imageId = _nextImageId++;

                // Create thumbnail on BACKGROUND THREAD to avoid UI stutter
                var thumbnailRaw = await Task.Run(() => CreateGentlyStretchedThumbnail(imageData, 160));

                // Create info object with FULL metadata
                var capturedInfo = new CapturedImageThumbnail
                {
                    Id = imageId,
                    Thumbnail = thumbnailRaw,
                    FullImage = fullImage,
                    RawData = imageData,
                    CaptureTime = DateTime.Now,
                    IsSelected = false,
                    StretchMin = stretchMin,
                    StretchMax = stretchMax,
                    StretchProfile = profileName,
                    SeriesId = seriesId,
                    SeriesIndex = seriesIndex
                };

                string seriesInfo = seriesId.HasValue ? $", Series={seriesId.Value:N}[{seriesIndex}]" : "";
                Debug.WriteLine($"[ThumbnailGallery] Captured metadata: ID={capturedInfo.Id}, Stretch={capturedInfo.StretchProfile} ({capturedInfo.StretchMin}-{capturedInfo.StretchMax}){seriesInfo}");

                // Insert at beginning so newest image appears on the LEFT
                _capturedImages.Insert(0, capturedInfo);

                // Remove oldest (from END) if exceeds max
                if (_capturedImages.Count > MAX_CAPTURED_IMAGES)
                {
                    _capturedImages.RemoveAt(_capturedImages.Count - 1);
                    Debug.WriteLine($"[ThumbnailGallery] Removed oldest image (max {MAX_CAPTURED_IMAGES} reached)");
                }

                // Hide placeholder
                if (txtThumbnailPlaceholder != null)
                {
                    txtThumbnailPlaceholder.Visibility = Visibility.Collapsed;
                }

                // Auto-select new image
                SelectThumbnail(capturedInfo);

                // Auto-scroll to LEFT (where newest image is)
                thumbnailScrollViewer?.ScrollToLeftEnd();

                Debug.WriteLine($"[ThumbnailGallery] Added image #{capturedInfo.Id} at LEFT ({_capturedImages.Count}/{MAX_CAPTURED_IMAGES})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThumbnailGallery] Error adding thumbnail: {ex.Message}");
            }
        }

        /// <summary>
        /// Create thumbnail from raw data with gentle histogram stretch
        /// This prevents overexposed thumbnails
        /// </summary>
        private BitmapSource CreateGentlyStretchedThumbnail(CameraImageData imageData, int maxSize)
        {
            try
            {
                bool isRAW16 = imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16;
                int pixelCount = imageData.Width * imageData.Height;

                // Estimate percentiles using SAMPLING instead of full sort
                // Sample ~10000 evenly-spaced pixels, O(n) instead of O(n log n)
                int sampleSize = Math.Min(10000, pixelCount);
                int step = Math.Max(1, pixelCount / sampleSize);
                var samples = new ushort[sampleSize];
                int sampleIdx = 0;

                if (isRAW16)
                {
                    for (int i = 0; i < pixelCount && sampleIdx < sampleSize; i += step)
                    {
                        samples[sampleIdx++] = BitConverter.ToUInt16(imageData.ImageData, i * 2);
                    }
                }
                else
                {
                    for (int i = 0; i < pixelCount && sampleIdx < sampleSize; i += step)
                    {
                        samples[sampleIdx++] = (ushort)(imageData.ImageData[i] * 257);
                    }
                }

                Array.Sort(samples, 0, sampleIdx);
                int blackPoint = samples[sampleIdx / 50];       // 2nd percentile
                int whitePoint = samples[sampleIdx * 49 / 50];  // 98th percentile

                if (whitePoint - blackPoint < 1000)
                {
                    blackPoint = Math.Max(0, whitePoint - 10000);
                }

                Debug.WriteLine($"[Thumbnail] Gentle stretch (sampled): Black={blackPoint}, White={whitePoint}");

                // Downscale FIRST, then stretch, much faster than stretch-then-downscale
                double scaleFactor = Math.Min(maxSize / (double)imageData.Width, maxSize / (double)imageData.Height);
                if (scaleFactor > 1.0) scaleFactor = 1.0;

                int thumbW = (int)(imageData.Width * scaleFactor);
                int thumbH = (int)(imageData.Height * scaleFactor);
                if (thumbW < 1) thumbW = 1;
                if (thumbH < 1) thumbH = 1;

                double invScale = 1.0 / scaleFactor;
                double range = Math.Max(1, whitePoint - blackPoint);

                byte[] thumbData = new byte[thumbW * thumbH];

                for (int ty = 0; ty < thumbH; ty++)
                {
                    int srcY = (int)(ty * invScale);
                    if (srcY >= imageData.Height) srcY = imageData.Height - 1;

                    for (int tx = 0; tx < thumbW; tx++)
                    {
                        int srcX = (int)(tx * invScale);
                        if (srcX >= imageData.Width) srcX = imageData.Width - 1;

                        int srcIdx = srcY * imageData.Width + srcX;
                        int val;

                        if (isRAW16)
                            val = BitConverter.ToUInt16(imageData.ImageData, srcIdx * 2);
                        else
                            val = imageData.ImageData[srcIdx] * 257;

                        if (val <= blackPoint)
                            thumbData[ty * thumbW + tx] = 0;
                        else if (val >= whitePoint)
                            thumbData[ty * thumbW + tx] = 255;
                        else
                            thumbData[ty * thumbW + tx] = (byte)(((val - blackPoint) * 255.0) / range);
                    }
                }

                var thumbnail = BitmapSource.Create(thumbW, thumbH, 96, 96,
                    PixelFormats.Gray8, null, thumbData, thumbW);
                thumbnail.Freeze();

                return thumbnail;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Thumbnail] Error creating gentle stretched thumbnail: {ex.Message}");
                return CreateThumbnail(imageData.ImageData, imageData.Width, imageData.Height, maxSize);
            }
        }

        /// <summary>
        /// Create downscaled thumbnail from raw byte array (fallback)
        /// </summary>
        private BitmapSource CreateThumbnail(byte[] data, int width, int height, int maxSize)
        {
            try
            {
                // Simple grayscale bitmap
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, data, width);
                bitmap.Freeze();

                double scale = Math.Min(maxSize / (double)width, maxSize / (double)height);
                if (scale >= 1.0) return bitmap;

                var transform = new ScaleTransform(scale, scale);
                var thumbnail = new TransformedBitmap(bitmap, transform);
                thumbnail.Freeze();
                
                return thumbnail;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Handle thumbnail click - display full image
        /// </summary>
        private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is Border border && border.Tag is CapturedImageThumbnail imageInfo)
                {
                    SelectThumbnail(imageInfo);
                    Debug.WriteLine($"[ThumbnailGallery] Selected image #{imageInfo.Id}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThumbnailGallery] Error selecting thumbnail: {ex.Message}");
            }
        }

        /// <summary>
        /// Select thumbnail and display full image
        /// </summary>
        private async void SelectThumbnail(CapturedImageThumbnail imageInfo)
        {
            try
            {
                Debug.WriteLine($"[ThumbnailGallery] Selecting image #{imageInfo.Id}...");

                // Clear star annotations when switching images
                _lastAnalysisResult = null;
                starOverlay?.Children.Clear();

                // Deselect all (INotifyPropertyChanged handles UI update)
                foreach (var img in _capturedImages)
                {
                    img.IsSelected = false;
                }

                // Select clicked image
                imageInfo.IsSelected = true;
                _currentlySelectedImage = imageInfo;

                // Restore raw data for re-stretching
                _lastRawData = imageInfo.RawData.ImageData;
                _lastWidth = imageInfo.RawData.Width;
                _lastHeight = imageInfo.RawData.Height;
                _lastIsRAW16 = imageInfo.RawData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16;
                txtPlaceholder.Visibility = Visibility.Collapsed;

                // Restore histogram stretch parameters (suppress change events)
                _isUpdatingUI = true;
                try
                {
                    _histogramMin = imageInfo.StretchMin;
                    _histogramMax = imageInfo.StretchMax;

                    if (sliderHistogramMin != null)
                        sliderHistogramMin.Value = imageInfo.StretchMin;

                    if (sliderHistogramMax != null)
                        sliderHistogramMax.Value = imageInfo.StretchMax;

                    if (txtHistogramMin != null)
                        txtHistogramMin.Text = imageInfo.StretchMin.ToString();

                    if (txtHistogramMax != null)
                        txtHistogramMax.Text = imageInfo.StretchMax.ToString();
                }
                finally
                {
                    _isUpdatingUI = false;
                }

                // Re-stretch from raw data using restored params.
                // This guarantees the displayed image matches the thumbnail's stretch,
                // regardless of any race condition with the cached FullImage.
                await RestretchAndDisplay();

                // Update the stored FullImage for consistency
                imageInfo.FullImage = _originalImage;

                // Update info text with comprehensive metadata
                txtImageInfo.Text = $"#{imageInfo.Id} | {imageInfo.ImageSize} {imageInfo.ImageTypeInfo} | {imageInfo.ExposureInfo}";
                if (imageInfo.RawData.Temperature_C.HasValue)
                {
                    txtImageInfo.Text += $" | {imageInfo.TemperatureInfo}";
                }

                // Fit to window if first time
                if (_isFirstLoad)
                {
                    _isFirstLoad = false;
                    FitImageToWindow();
                }

                Debug.WriteLine($"[ThumbnailGallery] Image #{imageInfo.Id} selected (stretch {imageInfo.StretchMin}-{imageInfo.StretchMax})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThumbnailGallery] Error selecting thumbnail: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all thumbnails
        /// </summary>
        public void ClearThumbnailGallery()
        {
            _capturedImages.Clear();
            _nextImageId = 1;
            _currentlySelectedImage = null;

            if (txtThumbnailPlaceholder != null)
            {
                txtThumbnailPlaceholder.Visibility = Visibility.Visible;
            }

            Debug.WriteLine("[ThumbnailGallery] Cleared all thumbnails");
        }

        /// <summary>
        /// Get currently selected image info (for external use)
        /// </summary>
        public CapturedImageThumbnail GetCurrentlySelectedImage()
        {
            return _currentlySelectedImage;
        }

        /// <summary>
        /// Get all captured images (for analysis/export)
        /// </summary>
        public System.Collections.Generic.IReadOnlyCollection<CapturedImageThumbnail> GetAllCapturedImages()
        {
            return _capturedImages.ToList().AsReadOnly();
        }

        /// <summary>
        /// Get current camera settings from UI controls
        /// </summary>
        public (int ExposureTime_us, int Gain, int Binning, ASI_IMG_TYPE ImageType) GetCurrentCameraSettings()
        {
            int exposureTime_us = 1000000; // Default 1s
            int gain = 0;
            int binning = 1;
            ASI_IMG_TYPE imageType = ASI_IMG_TYPE.ASI_IMG_RAW16;
            try
            {
                if (int.TryParse(txtExposureTime.Text, out int exposureMs))
                    exposureTime_us = exposureMs * 1000;
                if (_cameraProvider?.SupportsGain == true && int.TryParse(txtGain.Text, out int g))
                    gain = g;
                if (cmbBinning?.SelectedItem is ComboBoxItem binItem &&
                    binItem.Tag is string binTag &&
                    int.TryParse(binTag, out int bin))
                {
                    binning = bin;
                }
                if (_cameraProvider?.SupportsImageType == true &&
                    cmbImageType?.SelectedItem is ComboBoxItem typeItem &&
                    typeItem.Tag is string typeTag &&
                    int.TryParse(typeTag, out int type))
                {
                    imageType = (ASI_IMG_TYPE)type;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] GetCurrentCameraSettings error: {ex.Message}");
            }
            return (exposureTime_us, gain, binning, imageType);
        }

        /// <summary>
        /// Set last raw data for re-stretching (public access for CollimationAnalysis)
        /// </summary>
        public void SetLastRawData(byte[] rawData, int width, int height, bool isRAW16)
        {
            _lastRawData = rawData;
            _lastWidth = width;
            _lastHeight = height;
            _lastIsRAW16 = isRAW16;
        }

        /// <summary>
        /// Re-stretch and display current image (public access for CollimationAnalysis)
        /// Returns the stretched image
        /// </summary>
        public async Task<BitmapSource> RestretchAndDisplayPublic()
        {
            await RestretchAndDisplay();
            return _originalImage;
        }

        /// <summary>
        /// Add combined/stacked image to thumbnail gallery with special marker
        /// </summary>
        public async Task AddCombinedImageToThumbnail(CameraImageData imageData, Guid seriesId, int stackCount, BitmapSource stretchedImage)
        {
            try
            {
                string profileName = "Combined";
                try
                {
                    if (cmbStretchProfile != null && cmbStretchProfile.SelectedItem is ComboBoxItem selectedItem)
                    {
                        profileName = "Combined (" + selectedItem.Content?.ToString() + ")";
                    }
                }
                catch { }

                int stretchMin = _histogramMin;
                int stretchMax = _histogramMax;
                int imageId = _nextImageId++;

                // Create thumbnail on BACKGROUND THREAD
                var thumbnailRaw = await Task.Run(() => CreateGentlyStretchedThumbnail(imageData, 160));

                // Create info object with combined marker
                var capturedInfo = new CapturedImageThumbnail
                {
                    Id = imageId,
                    Thumbnail = thumbnailRaw,
                    FullImage = stretchedImage,  // Use the stretched image passed as parameter
                    RawData = imageData,
                    CaptureTime = DateTime.Now,
                    IsSelected = false,
                    StretchMin = stretchMin,
                    StretchMax = stretchMax,
                    StretchProfile = profileName,
                    SeriesId = seriesId,
                    SeriesIndex = 0  // 0 = combined/stacked image
                };

                Debug.WriteLine($"[ThumbnailGallery] Added COMBINED image #{capturedInfo.Id} (stacked from {stackCount} images)");

                // Insert at beginning
                _capturedImages.Insert(0, capturedInfo);

                // Remove oldest if exceeds max
                if (_capturedImages.Count > MAX_CAPTURED_IMAGES)
                {
                    _capturedImages.RemoveAt(_capturedImages.Count - 1);
                }

                // Hide placeholder
                if (txtThumbnailPlaceholder != null)
                {
                    txtThumbnailPlaceholder.Visibility = Visibility.Collapsed;
                }

                // Auto-select combined image
                SelectThumbnail(capturedInfo);

                // Auto-scroll to LEFT
                thumbnailScrollViewer?.ScrollToLeftEnd();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThumbnailGallery] Error adding combined thumbnail: {ex.Message}");
            }
        }

        /// <summary>
        /// Draw star detection annotations on image
        /// Shows circles around detected stars with FWHM info
        /// </summary>
        public void DrawStarAnnotations(CollimationResult analysisResult)
        {
            try
            {
                starOverlay.Children.Clear();

                if (analysisResult?.StarResults == null || analysisResult.StarResults.Count == 0)
                {
                    Debug.WriteLine("[StarAnnotation] No stars to annotate");
                    return;
                }

                // Store result for re-drawing on zoom change
                _lastAnalysisResult = analysisResult;

                RedrawStarAnnotations();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StarAnnotation] Error drawing annotations: {ex.Message}");
            }
        }

        /// <summary>
        /// Stored analysis result for re-drawing annotations on zoom
        /// </summary>
        private CollimationResult _lastAnalysisResult;

        /// <summary>
        /// Re-draw star annotations at current zoom level
        /// Star positions are in ORIGINAL pixel coordinates ? must scale by _currentZoom
        /// </summary>
        private void RedrawStarAnnotations()
        {
            starOverlay.Children.Clear();

            if (_lastAnalysisResult?.StarResults == null || _lastAnalysisResult.StarResults.Count == 0)
                return;

            double zoom = _currentZoom;

            // Ensure overlay matches image size
            starOverlay.Width = imgPreview.Width;
            starOverlay.Height = imgPreview.Height;

            int count = 0;
            foreach (var star in _lastAnalysisResult.StarResults)
            {
                // Scale star position by zoom
                double x = star.X * zoom;
                double y = star.Y * zoom;
                double radius = Math.Max(star.FWHM_Pixels * 2 * zoom, 8);

                // Circle around star
                var circle = new System.Windows.Shapes.Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Stroke = new SolidColorBrush(Color.FromRgb(0, 255, 255)),
                    StrokeThickness = Math.Max(1.5, 2 * zoom),
                    Fill = System.Windows.Media.Brushes.Transparent
                };

                Canvas.SetLeft(circle, x - radius);
                Canvas.SetTop(circle, y - radius);
                starOverlay.Children.Add(circle);

                // FWHM label (only show at reasonable zoom to avoid clutter)
                if (zoom >= 0.03)
                {
                    double fontSize = Math.Max(9, 11 * zoom);
                    var label = new TextBlock
                    {
                        Text = $"{star.FWHM_Arcsec:F2}\"",
                        Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 255)),
                        FontSize = fontSize,
                        FontWeight = FontWeights.Bold,
                        Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                        Padding = new Thickness(2, 1, 2, 1)
                    };

                    Canvas.SetLeft(label, x + radius + 3);
                    Canvas.SetTop(label, y - fontSize / 2);
                    starOverlay.Children.Add(label);
                }

                count++;
            }

            Debug.WriteLine($"[StarAnnotation] Drew {count} stars at zoom {zoom:P0}");
        }

        /// <summary>
        /// Clear star annotations
        /// </summary>
        public void ClearStarAnnotations()
        {
            _lastAnalysisResult = null;
            starOverlay?.Children.Clear();
        }

        #endregion

        // ARRAY INSERT
        #region Image Display

        private void SelectProfileInComboBox(StretchProfileType profileType)
        {
            if (cmbStretchProfile == null) return;

            for (int i = 0; i < cmbStretchProfile.Items.Count; i++)
            {
                if (cmbStretchProfile.Items[i] is ComboBoxItem item &&
                    item.Tag is string tag &&
                    int.TryParse(tag, out int value) &&
                    value == (int)profileType)
                {
                    cmbStretchProfile.SelectedIndex = i;
                    break;
                }
            }
        }

        private void UpdateProfileInfo()
        {
            if (txtProfileInfo == null) return;

            try
            {
                var (blackPct, whitePct) = HistogramStretchProfile.GetPresetPercentiles(_currentProfileType);
                txtProfileInfo.Text = _currentProfileType == StretchProfileType.Manual
                    ? $"Manual: Black={_histogramMin}, White={_histogramMax}"
                    : $"Percentiles: Black={blackPct:F1}%, White={whitePct:F1}%";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] UpdateProfileInfo error: {ex.Message}");
            }
        }

        private void ApplyProfileStretch()
        {
            if (_lastRawData == null || !_lastIsRAW16) return;

            try
            {
                var (blackPct, whitePct) = HistogramStretchProfile.GetPresetPercentiles(_currentProfileType);
                var (minValue, maxValue) = CalculatePercentilesFromHistogram(blackPct, whitePct);

                _isUpdatingUI = true;
                _histogramMin = minValue;
                _histogramMax = maxValue;
                if (sliderHistogramMin != null) sliderHistogramMin.Value = _histogramMin;
                if (sliderHistogramMax != null) sliderHistogramMax.Value = _histogramMax;
                if (txtHistogramMin != null) txtHistogramMin.Text = _histogramMin.ToString();
                if (txtHistogramMax != null) txtHistogramMax.Text = _histogramMax.ToString();
                UpdateProfileInfo();
                _isUpdatingUI = false;

                SaveStretchProfileToSettings();
                _ = RestretchAndDisplay();
            }
            catch (Exception ex)
            {
                _isUpdatingUI = false;
                Debug.WriteLine($"[CameraOffset] ApplyProfileStretch error: {ex.Message}");
            }
        }

        private (int minValue, int maxValue) CalculatePercentilesFromHistogram(double blackPercentile, double whitePercentile)
        {
            if (_lastRawData == null || !_lastIsRAW16 || _lastWidth == 0 || _lastHeight == 0)
                return (0, 65535);

            try
            {
                int pixelCount = _lastWidth * _lastHeight;
                int[] histogram = new int[256];
                for (int i = 0; i < pixelCount; i++)
                {
                    ushort value = BitConverter.ToUInt16(_lastRawData, i * 2);
                    histogram[value >> 8]++;
                }

                long[] cumulative = new long[256];
                cumulative[0] = histogram[0];
                for (int i = 1; i < 256; i++)
                    cumulative[i] = cumulative[i - 1] + histogram[i];

                long blackThreshold = (long)(pixelCount * blackPercentile / 100.0);
                long whiteThreshold = (long)(pixelCount * whitePercentile / 100.0);
                int blackBin = 0;
                int whiteBin = 255;

                for (int i = 0; i < 256; i++)
                {
                    if (cumulative[i] >= blackThreshold)
                    {
                        blackBin = i;
                        break;
                    }
                }

                for (int i = 0; i < 256; i++)
                {
                    if (cumulative[i] >= whiteThreshold)
                    {
                        whiteBin = i;
                        break;
                    }
                }

                int minValue = blackBin << 8;
                int maxValue = (whiteBin << 8) + 255;
                return minValue >= maxValue ? (0, 65535) : (minValue, maxValue);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] CalculatePercentilesFromHistogram error: {ex.Message}");
                return (0, 65535);
            }
        }

        private async Task RestretchAndDisplay()
        {
            if (_lastRawData == null || _lastWidth == 0 || _lastHeight == 0)
                return;

            try
            {
                var raw = _lastRawData;
                int width = _lastWidth;
                int height = _lastHeight;
                bool isRaw16 = _lastIsRAW16;
                int minValue = _histogramMin;
                int maxValue = _histogramMax;

                var bitmap = await Task.Run(() =>
                {
                    if (raw.Length == width * height * 3)
                    {
                        var rgb = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, raw, width * 3);
                        rgb.Freeze();
                        return (BitmapSource)rgb;
                    }

                    byte[] displayData = new byte[width * height];
                    double scale = 255.0 / Math.Max(1, maxValue - minValue);

                    if (isRaw16)
                    {
                        for (int i = 0; i < width * height; i++)
                        {
                            ushort value = BitConverter.ToUInt16(raw, i * 2);
                            if (value <= minValue) displayData[i] = 0;
                            else if (value >= maxValue) displayData[i] = 255;
                            else displayData[i] = (byte)((value - minValue) * scale);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < width * height && i < raw.Length; i++)
                        {
                            int value = raw[i] * 256;
                            if (value <= minValue) displayData[i] = 0;
                            else if (value >= maxValue) displayData[i] = 255;
                            else displayData[i] = (byte)((value - minValue) * scale);
                        }
                    }

                    var gray = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, displayData, width);
                    gray.Freeze();
                    return (BitmapSource)gray;
                });

                _originalImage = bitmap;
                ProcessAndDisplayImage();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] RestretchAndDisplay error: {ex.Message}");
            }
        }

        private void ProcessAndDisplayImage()
        {
            if (_originalImage == null) return;

            try
            {
                BitmapSource displayImage = _originalImage;
                if (Math.Abs(_currentZoom - 1.0) > 0.001)
                {
                    var transformed = new TransformedBitmap(_originalImage, new ScaleTransform(_currentZoom, _currentZoom));
                    transformed.Freeze();
                    displayImage = transformed;
                }

                imgPreview.Source = displayImage;
                imgPreview.Width = displayImage.PixelWidth;
                imgPreview.Height = displayImage.PixelHeight;
                if (starOverlay != null)
                {
                    starOverlay.Width = displayImage.PixelWidth;
                    starOverlay.Height = displayImage.PixelHeight;
                }
                UpdateZoomInfo();
                if (_lastAnalysisResult != null)
                {
                    RedrawStarAnnotations();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] ProcessAndDisplayImage error: {ex.Message}");
            }
        }

        private void UpdateZoomInfo()
        {
            if (txtZoomInfo != null)
                txtZoomInfo.Text = $"Zoom: {_currentZoom * 100:F0}%";
        }

        private void FitImageToWindow()
        {
            try
            {
                if (_originalImage == null || borderPreview == null)
                    return;

                double imageWidth = _originalImage.PixelWidth;
                double imageHeight = _originalImage.PixelHeight;
                double containerWidth = Math.Max(1, borderPreview.ActualWidth - 16);
                double containerHeight = Math.Max(1, borderPreview.ActualHeight - 16);
                double scaleX = containerWidth / imageWidth;
                double scaleY = containerHeight / imageHeight;
                _currentZoom = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, Math.Min(scaleX, scaleY)));
                ProcessAndDisplayImage();
                scrollViewer?.ScrollToHorizontalOffset(0);
                scrollViewer?.ScrollToVerticalOffset(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] FitImageToWindow error: {ex.Message}");
            }
        }

        private void ResetImageTransform()
        {
            _currentZoom = 1.0;
            _isDragging = false;
            ProcessAndDisplayImage();
            scrollViewer?.ScrollToHorizontalOffset(0);
            scrollViewer?.ScrollToVerticalOffset(0);
            UpdateZoomInfo();
        }

        private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            FitImageToWindow();
        }

        private void BtnReset1to1_Click(object sender, RoutedEventArgs e)
        {
            if (imgPreview.Source == null) return;
            ResetImageTransform();
        }

        private void BorderPreview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_originalImage != null && !_isDragging)
                ProcessAndDisplayImage();
        }

        private void ImgPreview_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_originalImage == null) return;
            double delta = e.Delta > 0 ? ZOOM_SPEED : -ZOOM_SPEED;
            _currentZoom = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, _currentZoom + delta));
            ProcessAndDisplayImage();
            e.Handled = true;
        }

        private void ImgPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_originalImage == null) return;
            _isDragging = true;
            _lastMousePosition = e.GetPosition(scrollViewer);
            imgPreview.CaptureMouse();
            imgPreview.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private void ImgPreview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _originalImage == null) return;
            Point currentPosition = e.GetPosition(scrollViewer);
            double deltaX = currentPosition.X - _lastMousePosition.X;
            double deltaY = currentPosition.Y - _lastMousePosition.Y;
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - deltaX);
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - deltaY);
            _lastMousePosition = currentPosition;
            e.Handled = true;
        }

        private void ImgPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            imgPreview.ReleaseMouseCapture();
            imgPreview.Cursor = Cursors.Hand;
            e.Handled = true;
        }

        private void ImgPreview_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            imgPreview.ReleaseMouseCapture();
            imgPreview.Cursor = Cursors.Hand;
        }

        private void CmbStretchProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            try
            {
                if (cmbStretchProfile.SelectedItem is ComboBoxItem item &&
                    item.Tag is string tag &&
                    int.TryParse(tag, out int profileValue))
                {
                    _currentProfileType = (StretchProfileType)profileValue;
                    UpdateProfileInfo();
                    SaveStretchProfileToSettings();
                    if (_lastRawData != null && _currentProfileType != StretchProfileType.Manual)
                    {
                        ApplyProfileStretch();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] CmbStretchProfile_SelectionChanged error: {ex.Message}");
            }
        }

        private void SliderHistogram_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUI || sliderHistogramMin == null || sliderHistogramMax == null)
                return;

            try
            {
                _histogramMin = (int)sliderHistogramMin.Value;
                _histogramMax = (int)sliderHistogramMax.Value;
                if (_histogramMin >= _histogramMax)
                {
                    if (sender == sliderHistogramMin)
                        _histogramMin = _histogramMax - 1;
                    else
                        _histogramMax = _histogramMin + 1;
                    _isUpdatingUI = true;
                    sliderHistogramMin.Value = _histogramMin;
                    sliderHistogramMax.Value = _histogramMax;
                    _isUpdatingUI = false;
                }
                if (txtHistogramMin != null) txtHistogramMin.Text = _histogramMin.ToString();
                if (txtHistogramMax != null) txtHistogramMax.Text = _histogramMax.ToString();
                if (_currentProfileType != StretchProfileType.Manual && _lastRawData != null)
                {
                    _isUpdatingUI = true;
                    _currentProfileType = StretchProfileType.Manual;
                    SelectProfileInComboBox(StretchProfileType.Manual);
                    UpdateProfileInfo();
                    _isUpdatingUI = false;
                }
                SaveStretchProfileToSettings();
                if (_lastRawData != null)
                {
                    _ = RestretchAndDisplay();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] SliderHistogram_ValueChanged error: {ex.Message}");
            }
        }

        private void SliderHistogram_MouseUp(object sender, MouseButtonEventArgs e)
        {
            SaveStretchProfileToSettings();
        }

        #endregion
        #region Pointing and Tracking Control Events

        /// <summary>
        /// Handle capture request from PointingAndTrackingControl
        /// </summary>
        private async void OnPointingTrackingCaptureRequested(object sender, CaptureRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"[CameraOffset] Capture requested: Purpose={e.Purpose}, Exposure={e.ExposureTime_ms}ms, PosIndex={e.PositionIndex}, ImgIndex={e.ImageIndex}");
                if (!IsCameraConnected())
                {
                    Debug.WriteLine("[CameraOffset] Camera not connected for pointing/tracking capture");
                    return;
                }
                var (_, gain, binning, imageType) = GetCurrentCameraSettings();
                int exposureTime_us = (int)(e.ExposureTime_ms * 1000);
                if (!ApplyCaptureSettings(exposureTime_us, gain, binning, imageType))
                {
                    Debug.WriteLine("[CameraOffset] Failed to apply pointing/tracking capture settings");
                    return;
                }
                SetCaptureStatus(CaptureState.Exposing);
                loadingPanel.Visibility = Visibility.Visible;
                CameraImageData imageData = await Task.Run(() => _cameraProvider.CaptureImage());
                SetCaptureStatus(CaptureState.Processing);
                if (imageData != null)
                {
                    _lastRawData = imageData.ImageData;
                    _lastWidth = imageData.Width;
                    _lastHeight = imageData.Height;
                    _lastIsRAW16 = imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16;
                    if (_currentProfileType == StretchProfileType.Auto)
                    {
                        ApplyProfileStretch();
                    }
                    await RestretchAndDisplay();
                    txtPlaceholder.Visibility = Visibility.Collapsed;
                    if (_isFirstLoad)
                    {
                        _isFirstLoad = false;
                        FitImageToWindow();
                    }
                    AddToThumbnailGallery(imageData, _originalImage);
                    await ProcessForPointingTracking(e, imageData);
                    Debug.WriteLine($"[CameraOffset] Pointing/Tracking capture complete: {imageData.Width}x{imageData.Height}");
                }
                else
                {
                    Debug.WriteLine("[CameraOffset] Capture returned null for pointing/tracking");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] Pointing/Tracking capture error: {ex.Message}");
            }
            finally
            {
                loadingPanel.Visibility = Visibility.Collapsed;
                SetCaptureStatus(CaptureState.Idle);
            }
        }

        /// <summary>
        /// Process captured image for pointing/tracking test
        /// Detect brightest star and send position back to control
        /// </summary>
        private async Task ProcessForPointingTracking(CaptureRequestEventArgs e, CameraImageData imageData)
        {
            try
            {
                // Detect stars using StarDetector
                var detector = new StarDetector();

                // Get stars from Engine.Camera.DetectedStar
                var stars = await Task.Run(() => detector.DetectStars(imageData));

                if (stars == null || stars.Count == 0)
                {
                    Debug.WriteLine("[CameraOffset] No stars detected for pointing/tracking");
                    if (e.Purpose == CapturePurpose.SeeingMeasurement)
                    {
                        pointingAndTrackingControl?.UpdateSeeingFromStars(new List<UI.DetectedStar>(), e.MeasurementIndex);
                    }
                    else if (e.Purpose == CapturePurpose.PointingMeasurement)
                    {
                        pointingAndTrackingControl?.UpdatePointingFromStars(new List<UI.DetectedStar>(), e.PositionIndex, e.ImageIndex);
                    }
                    return;
                }

                // Find brightest star (Engine.Camera.DetectedStar uses X, Y properties)
                var brightestStar = stars.OrderByDescending(s => s.Brightness).First();
                Debug.WriteLine($"[CameraOffset] Brightest star at ({brightestStar.X:F2}, {brightestStar.Y:F2}), Brightness={brightestStar.Brightness:F0}");

                // Send result back to control based on purpose
                switch (e.Purpose)
                {
                    case CapturePurpose.SeeingMeasurement:
                        // Convert Engine.Camera.DetectedStar to UI.DetectedStar for seeing measurement
                        var detectedStars = stars.Select(s => new UI.DetectedStar
                        {
                            X = s.X,
                            Y = s.Y,
                            FWHM = s.FWHM,
                            Brightness = s.Brightness
                        }).ToList();
                        pointingAndTrackingControl?.UpdateSeeingFromStars(detectedStars, e.MeasurementIndex);
                        break;

                    case CapturePurpose.PointingMeasurement:
                        var pointingStars = stars.Select(s => new UI.DetectedStar
                        {
                            X = s.X,
                            Y = s.Y,
                            FWHM = s.FWHM,
                            Brightness = s.Brightness
                        }).ToList();
                        pointingAndTrackingControl?.UpdatePointingFromStars(pointingStars, e.PositionIndex, e.ImageIndex);
                        break;

                    case CapturePurpose.TrackingReference:
                        // Set reference star for tracking
                        pointingAndTrackingControl?.SetReferenceStar(
                            brightestStar.X, brightestStar.Y, brightestStar.Brightness);
                        break;

                    case CapturePurpose.TrackingMeasurement:
                        // Update tracking measurement with current star position
                        pointingAndTrackingControl?.UpdateTrackingMeasurement(
                            brightestStar.X, brightestStar.Y, e.MeasurementIndex);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] ProcessForPointingTracking error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle slew request from PointingAndTrackingControl
        /// Moves mount to specified Alt/Az coordinates
        /// </summary>
        private async void OnPointingTrackingSlewRequested(object sender, SlewRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"[CameraOffset] Slew requested: Alt={e.TargetAltitude_deg:F2} deg, Az={e.TargetAzimuth_deg:F2} deg");

                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow == null)
                {
                    pointingAndTrackingControl?.CompletePointingSlew(false, "Cannot find MainWindow.");
                    return;
                }

                var azimuthController = FindVisualChild<Azimuth_Controller>(mainWindow);
                var altitudeController = FindVisualChild<Altitude_Controller>(mainWindow);

                if (azimuthController == null || altitudeController == null)
                {
                    pointingAndTrackingControl?.CompletePointingSlew(false, "Cannot find axis controllers.");
                    return;
                }

                if (azimuthController.AzimuthDrive == null || altitudeController.AltitudeDrive == null)
                {
                    pointingAndTrackingControl?.CompletePointingSlew(false, "Mount drives are not available.");
                    return;
                }

                bool azEnabled = azimuthController.GetAzimuthInfo()?.Is_Drive_Enabled ?? false;
                bool altEnabled = altitudeController.GetCurrentAltitudeInfo()?.Is_Drive_Enabled ?? false;
                if (!azEnabled || !altEnabled)
                {
                    Debug.WriteLine($"[CameraOffset] WARNING: Drives not enabled (Az={azEnabled}, Alt={altEnabled})");
                }

                double currentAz = azimuthController.GetCurrentPosition();
                double currentAlt = altitudeController.GetCurrentPosition();
                int timeoutMs = EstimatePointingSlewTimeoutMs(currentAlt, currentAz, e.TargetAltitude_deg, e.TargetAzimuth_deg);

                Debug.WriteLine($"[CameraOffset] Commanding Azimuth to {e.TargetAzimuth_deg:F2} deg...");
                azimuthController.AzimuthDrive.MoveToPosition(e.TargetAzimuth_deg, 5.0);

                Debug.WriteLine($"[CameraOffset] Commanding Altitude to {e.TargetAltitude_deg:F2} deg...");
                altitudeController.AltitudeDrive.MoveToPosition(e.TargetAltitude_deg, 3.0);

                await WaitForMountToReachTargetAsync(
                    azimuthController,
                    altitudeController,
                    e.TargetAzimuth_deg,
                    e.TargetAltitude_deg,
                    timeoutMs);

                Debug.WriteLine("[CameraOffset] Slew reached target");
                pointingAndTrackingControl?.CompletePointingSlew(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraOffset] Slew request error: {ex.Message}");
                pointingAndTrackingControl?.CompletePointingSlew(false, ex.Message);
            }
        }

        private async Task WaitForMountToReachTargetAsync(
            Azimuth_Controller azimuthController,
            Altitude_Controller altitudeController,
            double targetAzimuth_deg,
            double targetAltitude_deg,
            int timeoutMs)
        {
            const double toleranceDeg = 0.15;
            const int pollIntervalMs = 250;
            const int settleDelayMs = 500;

            var started = Stopwatch.StartNew();
            while (started.ElapsedMilliseconds < timeoutMs)
            {
                double azError = AngularDifferenceDeg(azimuthController.GetCurrentPosition(), targetAzimuth_deg);
                double altError = Math.Abs(altitudeController.GetCurrentPosition() - targetAltitude_deg);

                if (azError <= toleranceDeg && altError <= toleranceDeg)
                {
                    await Task.Delay(settleDelayMs);

                    double settledAzError = AngularDifferenceDeg(azimuthController.GetCurrentPosition(), targetAzimuth_deg);
                    double settledAltError = Math.Abs(altitudeController.GetCurrentPosition() - targetAltitude_deg);
                    if (settledAzError <= toleranceDeg && settledAltError <= toleranceDeg)
                    {
                        return;
                    }
                }

                await Task.Delay(pollIntervalMs);
            }

            throw new TimeoutException($"Slew did not settle within {timeoutMs / 1000.0:F0} seconds.");
        }

        private static int EstimatePointingSlewTimeoutMs(
            double currentAltitude_deg,
            double currentAzimuth_deg,
            double targetAltitude_deg,
            double targetAzimuth_deg)
        {
            double azimuthDelta = AngularDifferenceDeg(currentAzimuth_deg, targetAzimuth_deg);
            double altitudeDelta = Math.Abs(currentAltitude_deg - targetAltitude_deg);
            double estimatedMs = Math.Max((azimuthDelta / 5.0) * 1000.0, (altitudeDelta / 3.0) * 1000.0) + 8000.0;
            return (int)Math.Max(10000.0, Math.Min(90000.0, estimatedMs));
        }

        private static double AngularDifferenceDeg(double first_deg, double second_deg)
        {
            double difference = Math.Abs(first_deg - second_deg) % 360.0;
            return difference > 180.0 ? 360.0 - difference : difference;
        }

        /// <summary>
        /// Helper to find a visual child of specified type
        /// </summary>
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T result)
                    return result;

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        #endregion
    }
}



