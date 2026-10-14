using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static ForkMount_WPF.Engine.Camera.ZWOASICameraSDK;


namespace ForkMount_WPF.UI
{
    public partial class CameraSettingsWindow : Window
    {
        private ZWOASIManager _camera;
        private ICameraProvider _cameraProvider;
        private CameraType _selectedCameraType = CameraType.ZWO_ASI;
        private List<ScannedCameraInfo> _scannedCameras = new List<ScannedCameraInfo>();
        private bool _isUpdatingUI = false;

        // Image Processing variables (NOT WPF Transform)
        private BitmapSource _originalImage;  // Full resolution original image (RAW16 data)
        private byte[] _originalImageData;    // Original RAW data for re-stretching
        private int _originalImageWidth;
        private int _originalImageHeight;
        private bool _isRAW16;                // Is this a 16-bit image?
        
        private Point _lastMousePosition;
        private bool _isDragging = false;
        private double _currentZoom = 1.0;
        private const double MIN_ZOOM = 0.1;   // Minimum 10%
        private const double MAX_ZOOM = 10.0;  // Maximum 1000%
        private const double ZOOM_SPEED = 0.1; // 10% increment
        private bool _isFirstLoad = true;
        
        // Histogram stretch parameters
        private int _histogramMin = 0;
        private int _histogramMax = 65535;
        private StretchProfileType _currentProfileType = StretchProfileType.Auto;
        private StretchProfileType _lastNonManualProfileType = StretchProfileType.Auto;
        
        // Image repository for storing captured images
        private CapturedImageRepository _imageRepository;
        
        // Last captured image info
        private CapturedImageInfo _lastCapturedImage;
        
        // Countdown timer for exposure
        private System.Windows.Threading.DispatcherTimer _countdownTimer;
        private DateTime _captureStartTime;
        private double _exposureDurationSeconds;
        
        // [NEW] Reference to LiveCalibrationImageControl for image sync
        private LiveCalibrationImageControl _liveCalibrationControl;
        
        // [NEW] Currently displayed CameraImageData (for saving stretch parameters back)
        private Engine.Camera.CameraImageData _currentCameraImageData;
        
        // [NEW] Event to notify when histogram stretch changes (for LiveCalibrationImageControl sync)
        public event EventHandler<HistogramChangedEventArgs> HistogramStretchChanged;
        public event EventHandler<ICameraProvider> CameraProviderChanged;

        public CameraSettingsWindow(ZWOASIManager camera)
            : this((ICameraProvider)new ZWOCameraProvider(camera))
        {
        }

        /// <summary>
        /// Constructor accepting any camera via ICameraProvider interface
        /// </summary>
        public CameraSettingsWindow(ICameraProvider cameraProvider)
        {
            InitializeComponent();
            _cameraProvider = cameraProvider;
            _selectedCameraType = cameraProvider?.CameraType ?? CameraType.ZWO_ASI;

            // Set _camera for backward compatibility (ZWO-specific paths)
            if (cameraProvider is ZWOCameraProvider zwoProv)
                _camera = zwoProv.Manager;

            // Initialize image repository
            _imageRepository = new CapturedImageRepository();
            Debug.WriteLine("[CameraSettings] 📁 Image repository initialized");

            // Step 1: Scan for cameras and populate ComboBox
            ScanAndPopulateCameras();

            // Show/hide controls based on camera type
            UpdateControlVisibility();

            // Step 2: Load settings from AppSettings
            Debug.WriteLine("[CameraSettings] Step 2: Loading saved settings from AppSettings...");
            LoadSettingsFromAppSettings();

            // Step 3: Auto-connect if not connected (select first camera)
            if (_cameraProvider != null && !_cameraProvider.IsConnected)
            {
                Debug.WriteLine("[CameraSettings] Step 3: Camera not connected - attempting auto-connect...");
                
                var connected = _cameraProvider.Connect(0);
                
                if (connected)
                {
                    Debug.WriteLine("[CameraSettings] ✓ Auto-connect successful!");
                }
                else
                {
                    Debug.WriteLine("[CameraSettings] ✗ Auto-connect failed");
                }
            }

            // Step 4: If connected, AUTO-DETECT camera specs and update settings
            if (_cameraProvider != null && _cameraProvider.IsConnected)
            {
                Debug.WriteLine("[CameraSettings] Step 4: AUTO-DETECTING camera specs...");
                AutoDetectAndUpdateSettings();
            }

            // Step 5: Load current settings from camera for UI display
            Debug.WriteLine("[CameraSettings] Step 5: Loading current camera state for display...");
            LoadCurrentSettings();
            ApplySavedCoolerSettingsToConnectedCamera();
            
            // Update FOV info after loading settings
            UpdateFOVInfo();
        }

        #region Camera Scan & Selection

        /// <summary>
        /// Lightweight info for a discovered camera (used in ComboBox)
        /// </summary>
        private class ScannedCameraInfo
        {
            public CameraType? Type { get; set; }
            public int Index { get; set; }
            public string DisplayName { get; set; }

            // ZWO-specific
            public int CameraID { get; set; }

            // FLI-specific
            public string FileName { get; set; }
            public string SerialNumber { get; set; }

            public bool IsPlaceholder => Type == null;

            public override string ToString() => DisplayName;
        }

        /// <summary>
        /// Populate camera type options (ZWO + FLI) in the ComboBox.
        /// Connection is tested only when the user selects a type.
        /// </summary>
        private void ScanAndPopulateCameras()
        {
            _isUpdatingUI = true;
            _scannedCameras.Clear();
            cmbCameraDevice.Items.Clear();

            _scannedCameras.Add(new ScannedCameraInfo { Type = null, DisplayName = "Select Camera" });
            _scannedCameras.Add(new ScannedCameraInfo { Type = CameraType.ZWO_ASI, Index = 0, DisplayName = "ZWO" });
            _scannedCameras.Add(new ScannedCameraInfo { Type = CameraType.FLI, Index = 0, DisplayName = "FLI" });

            foreach (var cam in _scannedCameras)
            {
                cmbCameraDevice.Items.Add(new ComboBoxItem { Content = cam.DisplayName, Tag = cam });
            }

            int selectIdx = 0;
            if (_cameraProvider != null)
            {
                selectIdx = _cameraProvider.CameraType == CameraType.FLI ? 2 : 1;
            }

            cmbCameraDevice.SelectedIndex = selectIdx;
            Debug.WriteLine("[CameraSettings] Camera type options initialized: Select Camera, ZWO, FLI");

            _isUpdatingUI = false;
        }

        /// <summary>
        /// Camera device selection changed
        /// </summary>
        private void CmbCameraDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;

            var item = cmbCameraDevice.SelectedItem as ComboBoxItem;
            if (item?.Tag is ScannedCameraInfo camInfo)
            {
                Debug.WriteLine($"[CameraSettings] Camera selected: {camInfo.DisplayName}");

                if (camInfo.IsPlaceholder)
                {
                    if (_cameraProvider != null && _cameraProvider.IsConnected)
                    {
                        _cameraProvider.Disconnect();
                    }

                    _cameraProvider = null;
                    _camera = null;
                    NotifyCameraProviderChanged();
                    UpdateControlVisibility();
                    LoadCurrentSettings();
                    return;
                }

                // Disconnect current camera if connected
                if (_cameraProvider != null && _cameraProvider.IsConnected)
                {
                    _cameraProvider.Disconnect();
                }

                // Create new provider for selected camera
                if (camInfo.Type == CameraType.ZWO_ASI)
                {
                    var mgr = new ZWOASIManager();
                    _camera = mgr;
                    _cameraProvider = new ZWOCameraProvider(mgr);
                    _selectedCameraType = CameraType.ZWO_ASI;
                }
                else if (camInfo.Type == CameraType.FLI)
                {
                    var mgr = new FLICameraManager();
                    _camera = null;
                    _cameraProvider = new FLICameraProvider(mgr);
                    _selectedCameraType = CameraType.FLI;
                }

                UpdateControlVisibility();

                bool connected = _cameraProvider.Connect(camInfo.Index);

                if (connected)
                {
                    AutoDetectAndUpdateSettings();
                }
                else
                {
                    ShowCameraConnectionFailedMessage(camInfo.Type.Value);
                }

                NotifyCameraProviderChanged();
                LoadCurrentSettings();
                ApplySavedCoolerSettingsToConnectedCamera();
                UpdateFOVInfo();
            }
        }

        /// <summary>
        /// Show/hide controls based on the active camera's capabilities.
        /// FLI CCDs do not expose Gain / Offset / Image Type controls to the user
        /// and always operate in RAW16 internally.
        /// </summary>
        private void UpdateControlVisibility()
        {
            bool supportsGain = _cameraProvider?.SupportsGain ?? (_selectedCameraType == CameraType.ZWO_ASI);
            bool supportsOffset = _cameraProvider?.SupportsOffset ?? (_selectedCameraType == CameraType.ZWO_ASI);
            bool supportsImageType = _cameraProvider?.SupportsImageType ?? (_selectedCameraType == CameraType.ZWO_ASI);

            if (lblGain != null) lblGain.Visibility = supportsGain ? Visibility.Visible : Visibility.Collapsed;
            if (panelGain != null) panelGain.Visibility = supportsGain ? Visibility.Visible : Visibility.Collapsed;
            if (lblOffset != null) lblOffset.Visibility = supportsOffset ? Visibility.Visible : Visibility.Collapsed;
            if (panelOffset != null) panelOffset.Visibility = supportsOffset ? Visibility.Visible : Visibility.Collapsed;
            if (lblImageType != null) lblImageType.Visibility = supportsImageType ? Visibility.Visible : Visibility.Collapsed;
            if (cmbImageType != null) cmbImageType.Visibility = supportsImageType ? Visibility.Visible : Visibility.Collapsed;

            if (!supportsImageType)
            {
                SelectImageType(ASI_IMG_TYPE.ASI_IMG_RAW16);
            }
        }

        private void ShowCameraConnectionFailedMessage(CameraType cameraType)
        {
            string cameraLabel = cameraType == CameraType.FLI ? "FLI" : "ZWO";

            MessageBox.Show(
                $"Unable to connect to the {cameraLabel} camera.\n\nPlease verify that:\n- The camera is connected to the computer\n- The camera is not being used by another application\n- The camera driver or SDK is installed and available",
                "Camera Connection Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void NotifyCameraProviderChanged()
        {
            SavePreferredCameraTypeSetting();
            CameraProviderChanged?.Invoke(this, _cameraProvider);
        }

        private void SavePreferredCameraTypeSetting()
        {
            try
            {
                if (_cameraProvider == null)
                    return;

                var settings = AppSettings.GetCameraSettings();
                settings.PreferredCameraType = _cameraProvider.CameraType.ToString();
                AppSettings.SaveCameraSettings(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] Failed to save preferred camera type: {ex.Message}");
            }
        }

        /// <summary>
        /// Cooler checkbox changed
        /// </summary>
        private void ChkCooler_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI)
                return;

            ApplyCoolerStateFromUi(saveSettings: true);
        }

        private void ApplySavedCoolerSettingsToConnectedCamera()
        {
            if (_cameraProvider == null || !_cameraProvider.IsConnected || !_cameraProvider.HasCooler)
                return;

            ApplyCoolerStateFromUi(saveSettings: false);
        }

        private void ApplyCoolerStateFromUi(bool saveSettings)
        {
            if (_isUpdatingUI)
                return;

            if (saveSettings)
            {
                try
                {
                    SaveSettingsToAppSettings();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CameraSettings] Failed to persist cooler settings: {ex.Message}");
                }
            }

            if (_cameraProvider == null || !_cameraProvider.IsConnected || !_cameraProvider.HasCooler)
                return;

            bool enable = chkCoolerOn.IsChecked == true;

            if (enable)
            {
                if (double.TryParse(txtCoolerTemp.Text, out double temp))
                {
                    _cameraProvider.SetTargetTemperature(temp);
                    Debug.WriteLine($"[CameraSettings] Cooler ON → {temp}°C");
                }
                else
                {
                    Debug.WriteLine($"[CameraSettings] Cooler target temperature invalid: '{txtCoolerTemp.Text}'");
                }
            }
            else
            {
                _cameraProvider.SetCoolerOff();
                Debug.WriteLine("[CameraSettings] Cooler OFF");
            }
        }

        #endregion
        
        /// <summary>
        /// Set reference to LiveCalibrationImageControl for image sync
        /// </summary>
        public void SetLiveCalibrationControl(LiveCalibrationImageControl control)
        {
            _liveCalibrationControl = control;
            
            if (_liveCalibrationControl != null)
            {
                // Subscribe to image selected event
                _liveCalibrationControl.ImageSelected += OnLiveCalibrationImageSelected;
                _liveCalibrationControl.NewImageAdded += OnLiveCalibrationNewImageAdded;
                
                Debug.WriteLine("[CameraSettings] LiveCalibrationImageControl connected for image sync");
                
                // [NEW] If there's already a latest image, load it automatically
                var latestImageData = _liveCalibrationControl.GetLatestCameraImageData();
                if (latestImageData != null && latestImageData.ImageData != null)
                {
                    LoadCameraImageData(latestImageData);
                    Debug.WriteLine($"[CameraSettings] Auto-loaded latest image (point {_liveCalibrationControl.LatestPointNumber})");
                }
            }
        }
        
        /// <summary>
        /// Handle image selected from LiveCalibrationImageControl
        /// </summary>
        private void OnLiveCalibrationImageSelected(object sender, ImageSelectedEventArgs e)
        {
            // Use CameraImageData if available, otherwise fallback to raw data
            if (e.CameraImageData != null)
            {
                LoadCameraImageData(e.CameraImageData);
                Debug.WriteLine($"[CameraSettings] Loaded selected CameraImageData from point {e.PointNumber}");
            }
            else if (e.RawImageData != null)
            {
                // Fallback to raw data
                LoadExternalImageData(e.RawImageData, e.Width, e.Height, e.IsRAW16);
                Debug.WriteLine($"[CameraSettings] Loaded selected raw image from point {e.PointNumber}");
            }
        }
        
        
        /// <summary>
        /// Handle new image added to LiveCalibrationImageControl
        /// [NEW] Auto-switch to latest image when a new image is captured
        /// </summary>
        private void OnLiveCalibrationNewImageAdded(object sender, NewImageAddedEventArgs e)
        {
            Debug.WriteLine($"[CameraSettings] New image added for point {e.PointNumber}");
            
            // [CRITICAL FIX] Auto-select the new point in LiveCalibrationImageControl
            // This ensures _selectedPointNumber matches _currentCameraImageData
            _liveCalibrationControl?.SelectImage(e.PointNumber);
            
            // [NEW] Auto-switch to latest image
            if (e.CameraImageData != null)
            {
                LoadCameraImageData(e.CameraImageData);
                Debug.WriteLine($"[CameraSettings] Auto-loaded new CameraImageData for point {e.PointNumber}");
            }
            else if (e.RawImageData != null)
            {
                LoadExternalImageData(e.RawImageData, e.Width, e.Height, e.IsRAW16);
                Debug.WriteLine($"[CameraSettings] Auto-loaded new raw image for point {e.PointNumber}");
            }
        }
        
        /// <summary>
        /// Load CameraImageData into preview (recommended method)
        /// Also loads stored histogram stretch parameters if available
        /// </summary>
        public void LoadCameraImageData(Engine.Camera.CameraImageData imageData)
        {
            if (imageData == null || imageData.ImageData == null)
                return;
            
            // Store reference for saving stretch parameters back
            _currentCameraImageData = imageData;
            
            _originalImageData = imageData.ImageData;
            _originalImageWidth = imageData.Width;
            _originalImageHeight = imageData.Height;
            _isRAW16 = imageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;
            _isFirstLoad = true;
            
            // Update image info display
            UpdateImageInfo(imageData.Width, imageData.Height, imageData.GetBitsPerPixel());
            
            // [NEW] Load stretch parameters from CameraImageData if it has custom stretch
            if (imageData.HasCustomStretch)
            {
                _isUpdatingUI = true;
                
                // Load stored stretch values
                _histogramMin = imageData.StretchMin;
                _histogramMax = imageData.StretchMax;
                
                // Update sliders
                sliderHistogramMin.Value = _histogramMin;
                sliderHistogramMax.Value = _histogramMax;
                txtHistogramMin.Text = _histogramMin.ToString();
                txtHistogramMax.Text = _histogramMax.ToString();
                
                // Load profile index
                if (cmbStretchProfile != null && imageData.StretchProfileIndex >= 0 && 
                    imageData.StretchProfileIndex < cmbStretchProfile.Items.Count)
                {
                    cmbStretchProfile.SelectedIndex = imageData.StretchProfileIndex;
                }
                
                _isUpdatingUI = false;
                
                // Apply saved stretch (not auto-stretch)
                RestretchImage();
                
                Debug.WriteLine($"[CameraSettings] Loaded saved stretch: Min={_histogramMin}, Max={_histogramMax}, Profile={imageData.StretchProfileIndex}");
            }
            else
            {
                // No custom stretch - apply auto-stretch based on current profile
                ApplyProfileStretch();
            }
            
            Debug.WriteLine($"[CameraSettings] Loaded CameraImageData: {imageData.Width}x{imageData.Height} ({imageData.ImageType})");
        }
        
        /// <summary>
        /// Update image info display
        /// </summary>
        private void UpdateImageInfo(int width, int height, int bitsPerPixel)
        {
            if (txtImageInfo != null)
            {
                txtImageInfo.Text = $"{width}x{height} {bitsPerPixel}-bit";
            }
        }
        
        /// <summary>
        /// Clear the preview image and reset state
        /// Called when points are deleted from MountCalibrationControl
        /// </summary>
        public void ClearPreviewImage()
        {
            try
            {
                // Clear image data
                _originalImageData = null;
                _originalImageWidth = 0;
                _originalImageHeight = 0;
                _currentCameraImageData = null;
                
                // Clear image display
                if (imgPreview != null)
                {
                    imgPreview.Source = null;
                }
                
                // Reset image info
                if (txtImageInfo != null)
                {
                    txtImageInfo.Text = "";
                }
                
                // Reset histogram sliders to defaults
                _isUpdatingUI = true;
                if (sliderHistogramMin != null) sliderHistogramMin.Value = 0;
                if (sliderHistogramMax != null) sliderHistogramMax.Value = 65535;
                if (txtHistogramMin != null) txtHistogramMin.Text = "0";
                if (txtHistogramMax != null) txtHistogramMax.Text = "65535";
                _histogramMin = 0;
                _histogramMax = 65535;
                _isUpdatingUI = false;
                
                Debug.WriteLine("[CameraSettings] Preview image cleared");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] ClearPreviewImage error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Load external image data into preview (from LiveCalibrationImageControl)
        /// </summary>
        public void LoadExternalImageData(byte[] rawData, int width, int height, bool isRAW16)
        {
            if (rawData == null || width == 0 || height == 0)
                return;
            
            _originalImageData = rawData;
            _originalImageWidth = width;
            _originalImageHeight = height;
            _isRAW16 = isRAW16;
            _isFirstLoad = true;
            
            // Update image info display
            UpdateImageInfo(width, height, isRAW16 ? 16 : 8);
            
            // Apply current stretch profile
            ApplyProfileStretch();
        }
        
        /// <summary>
        /// Switch to Latest Image button click handler
        /// </summary>
        private void BtnSwitchToLastestImage_Click(object sender, RoutedEventArgs e)
        {
            if (_liveCalibrationControl != null)
            {
                _liveCalibrationControl.SelectLatestImage();
            }
            else
            {
                MessageBox.Show("LiveCalibrationImageControl not connected!", "Warning", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        
        /// <summary>
        /// Auto-detect camera specs and update settings if camera resolution changed
        /// This ensures settings match the actual connected camera
        /// </summary>
        private void AutoDetectAndUpdateSettings()
        {
            if (_cameraProvider == null || !_cameraProvider.IsConnected)
                return;
                
            try
            {
                // Read actual values from connected camera
                int actualWidth = _cameraProvider.MaxWidth;
                int actualHeight = _cameraProvider.MaxHeight;
                double actualPixelSize = NormalizePixelSizeMicrons(_cameraProvider.PixelSize);
                string cameraName = _cameraProvider.CameraName;
                
                Debug.WriteLine("[CameraSettings] =========================================");
                Debug.WriteLine("[CameraSettings] 🔍 AUTO-DETECT CAMERA SPECS");
                Debug.WriteLine("[CameraSettings] =========================================");
                Debug.WriteLine($"  Camera: {cameraName}");
                Debug.WriteLine($"  Resolution: {actualWidth}x{actualHeight}");
                Debug.WriteLine($"  Pixel Size: {actualPixelSize:F2} µm");
                
                // Get current saved settings
                var savedSettings = AppSettings.GetCameraSettings();
                
                // Check if camera resolution is different from saved settings
                bool resolutionChanged = savedSettings.Width != actualWidth || savedSettings.Height != actualHeight;
                bool pixelSizeChanged = Math.Abs(savedSettings.PixelSize_um - actualPixelSize) > 0.01;
                
                if (resolutionChanged || pixelSizeChanged)
                {
                    Debug.WriteLine("[CameraSettings] ?? CAMERA CHANGED DETECTED!");
                    Debug.WriteLine($"  Old Resolution: {savedSettings.Width}x{savedSettings.Height}");
                    Debug.WriteLine($"  New Resolution: {actualWidth}x{actualHeight}");
                    Debug.WriteLine($"  Old Pixel Size: {savedSettings.PixelSize_um:F2} ?m");
                    Debug.WriteLine($"  New Pixel Size: {actualPixelSize:F2} ?m");
                    
                    // Update settings with actual camera values
                    savedSettings.Width = actualWidth;
                    savedSettings.Height = actualHeight;
                    savedSettings.PixelSize_um = actualPixelSize;
                    
                    // Recalculate PlateScale with new pixel size
                    if (savedSettings.FocalLength_mm > 0)
                    {
                        savedSettings.PlateScale_ArcsecPerPixel = (actualPixelSize * savedSettings.Binning / savedSettings.FocalLength_mm) * 206.265;
                        Debug.WriteLine($"  New PlateScale: {savedSettings.PlateScale_ArcsecPerPixel:F3} arcsec/pixel");
                    }
                    
                    // Save updated settings
                    AppSettings.SaveCameraSettings(savedSettings);
                    Debug.WriteLine("[CameraSettings] ? Settings updated with actual camera specs!");
                    
                    // Update UI with new values (don't use old settings)
                    _isUpdatingUI = true;
                    // Keep user settings like Exposure, Gain, Offset, Focal Length - they're not camera-specific
                    _isUpdatingUI = false;
                    
                    // Show notification to user
                    MessageBox.Show(
                        $"Camera detected: {cameraName}\n\n" +
                        $"Resolution: {actualWidth} × {actualHeight}\n" +
                        $"Pixel Size: {actualPixelSize:F2} ?m\n\n" +
                        "Settings have been automatically updated!",
                        "Camera Auto-Detected",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    Debug.WriteLine("[CameraSettings] ? Camera matches saved settings - no update needed");
                    
                    // Apply saved settings to camera (only if resolution matches!)
                    Debug.WriteLine("[CameraSettings] Applying saved settings to camera...");
                    _ = ApplySettingsToCamera();
                }
                
                Debug.WriteLine("[CameraSettings] =========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] ? Auto-detect error: {ex.Message}");
            }
        }

        /// <summary>
        /// Load settings from AppSettings.json
        /// </summary>
        private void LoadSettingsFromAppSettings()
        {
            try
            {
                var savedSettings = AppSettings.GetCameraSettings();

                Debug.WriteLine("[CameraSettings] ===========================================");
                Debug.WriteLine("[CameraSettings] ?? LOADING SETTINGS FROM APPSETTINGS...");
                Debug.WriteLine("[CameraSettings] ===========================================");
                Debug.WriteLine($"  Exposure: {savedSettings.ExposureTime_ms} ms");
                Debug.WriteLine($"  Gain: {savedSettings.Gain}");
                Debug.WriteLine($"  Offset: {savedSettings.Offset}");
                Debug.WriteLine($"  Resolution: {savedSettings.Width}x{savedSettings.Height}");
                Debug.WriteLine($"  ? Binning: {savedSettings.Binning}x{savedSettings.Binning}");
                Debug.WriteLine($"  Image Type: {savedSettings.ImageType}");
                Debug.WriteLine($"  Focal Length: {savedSettings.FocalLength_mm} mm");
                Debug.WriteLine($"  Cooler Enabled: {savedSettings.CoolerEnabled}");
                Debug.WriteLine($"  Cooler Target: {savedSettings.CoolerTargetTemp_C:0.#} °C");

                _isUpdatingUI = true;

                // Load into UI
                txtExposureTime.Text = savedSettings.ExposureTime_ms.ToString();
                sliderGain.Value = savedSettings.Gain;
                txtGain.Text = savedSettings.Gain.ToString();
                sliderOffset.Value = savedSettings.Offset;
                txtOffset.Text = savedSettings.Offset.ToString();
                txtFocalLength.Text = savedSettings.FocalLength_mm.ToString();
                chkCoolerOn.IsChecked = savedSettings.CoolerEnabled;
                txtCoolerTemp.Text = savedSettings.CoolerTargetTemp_C.ToString("0.#");

                // Load binning
                Debug.WriteLine($"[CameraSettings] ?? Searching for binning {savedSettings.Binning} in combo box...");
                var binningItems = cmbBinning.Items;
                bool binningFound = false;
                for (int i = 0; i < binningItems.Count; i++)
                {
                    if (binningItems[i] is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int bin))
                    {
                        Debug.WriteLine($"[CameraSettings]   Checking item {i}: Tag={tag}, Binning={bin}");
                        if (bin == savedSettings.Binning)
                        {
                            cmbBinning.SelectedIndex = i;
                            binningFound = true;
                            Debug.WriteLine($"[CameraSettings] ? Binning {savedSettings.Binning} found and set at index {i}");
                            break;
                        }
                    }
                }
                
                if (!binningFound)
                {
                    Debug.WriteLine($"[CameraSettings] ?? WARNING: Binning {savedSettings.Binning} not found in combo box! Using default.");
                }

                // Load image type
                if (_cameraProvider?.SupportsImageType == false)
                {
                    SelectImageType(ASI_IMG_TYPE.ASI_IMG_RAW16);
                    Debug.WriteLine("[CameraSettings] Image type forced to RAW16 for camera without Image Type control");
                }
                else
                {
                    Debug.WriteLine($"[CameraSettings] 🔍 Searching for image type {savedSettings.ImageType} in combo box...");
                    var imageTypeItems = cmbImageType.Items;
                    bool imageTypeFound = false;
                    for (int i = 0; i < imageTypeItems.Count; i++)
                    {
                        if (imageTypeItems[i] is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int type))
                        {
                            Debug.WriteLine($"[CameraSettings]   Checking item {i}: Tag={tag}, Type={type}");
                            if (type == savedSettings.ImageType)
                            {
                                cmbImageType.SelectedIndex = i;
                                imageTypeFound = true;
                                Debug.WriteLine($"[CameraSettings] ✓ Image type {savedSettings.ImageType} found and set at index {i}");
                                break;
                            }
                        }
                    }

                    if (!imageTypeFound)
                    {
                        Debug.WriteLine($"[CameraSettings] ⚠️ WARNING: Image type {savedSettings.ImageType} not found in combo box! Using default.");
                    }
                }
                
                // [NEW] Load Histogram Stretch Profile
                if (savedSettings.HistogramStretch != null)
                {
                    _histogramMin = savedSettings.HistogramStretch.MinValue;
                    _histogramMax = savedSettings.HistogramStretch.MaxValue;
                    _currentProfileType = savedSettings.HistogramStretch.ProfileType;
                    
                    // Update UI sliders
                    if (sliderHistogramMin != null && sliderHistogramMax != null)
                    {
                        sliderHistogramMin.Value = _histogramMin;
                        sliderHistogramMax.Value = _histogramMax;
                        txtHistogramMin.Text = _histogramMin.ToString();
                        txtHistogramMax.Text = _histogramMax.ToString();
                    }
                    
                    // Update Profile ComboBox
                    if (cmbStretchProfile != null)
                    {
                        for (int i = 0; i < cmbStretchProfile.Items.Count; i++)
                        {
                            if (cmbStretchProfile.Items[i] is ComboBoxItem profileItem && 
                                profileItem.Tag is string profileTag && 
                                int.TryParse(profileTag, out int profileValue) &&
                                profileValue == (int)_currentProfileType)
                            {
                                cmbStretchProfile.SelectedIndex = i;
                                break;
                            }
                        }
                        UpdateProfileInfo();
                    }
                    
                    Debug.WriteLine($"[CameraSettings] 📊 Histogram Stretch loaded: Profile={_currentProfileType}, Min={_histogramMin}, Max={_histogramMax}");
                }

                _isUpdatingUI = false;

                Debug.WriteLine("[CameraSettings] ? Settings loaded from AppSettings successfully");
                Debug.WriteLine("[CameraSettings] ===========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings ERROR] Failed to load from AppSettings: {ex.Message}");
            }
        }

        private void LoadCurrentSettings()
        {
            try
            {
                if (_cameraProvider == null || !_cameraProvider.IsConnected)
                {
                    // Update status indicator - RED (Not Connected / No Camera)
                    statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red

                    if (_cameraProvider == null)
                    {
                        txtCameraName.Text = "No Camera";
                    }
                    else
                    {
                        // Cameras found but not connected
                        var savedSettings = AppSettings.GetCameraSettings();
                        int fallbackWidth = savedSettings.Width > 0 ? savedSettings.Width : 0;
                        int fallbackHeight = savedSettings.Height > 0 ? savedSettings.Height : 0;
                        txtCameraName.Text = fallbackWidth > 0
                            ? $"Not Connected (Last: {fallbackWidth}x{fallbackHeight})"
                            : "Not Connected";
                    }

                    txtCameraName.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    DisableControls();
                    
                    Debug.WriteLine("[CameraSettings] Camera not connected - controls disabled");
                    return;
                }

                // ============================================================
                // [AUTO-DETECT] Camera is connected - read actual specs!
                // ============================================================
                Debug.WriteLine("[CameraSettings] =========================================");
                Debug.WriteLine("[CameraSettings] 📷 AUTO-DETECTING CAMERA SPECS...");
                Debug.WriteLine("[CameraSettings] =========================================");
                
                // Read actual values from connected camera
                int actualWidth = _cameraProvider.MaxWidth;
                int actualHeight = _cameraProvider.MaxHeight;
                double actualPixelSize = NormalizePixelSizeMicrons(_cameraProvider.PixelSize);
                string cameraName = _cameraProvider.CameraName;
                
                Debug.WriteLine($"[CameraSettings] Camera Name: {cameraName}");
                Debug.WriteLine($"[CameraSettings] Max Resolution: {actualWidth}x{actualHeight}");
                Debug.WriteLine($"[CameraSettings] Pixel Size: {actualPixelSize} µm");
                
                // Update status indicator - GREEN (Connected)
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                UpdateCameraNameDisplay();
                txtCameraName.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green

                // [AUTO-UPDATE] Update settings with actual camera values
                var currentSettings = AppSettings.GetCameraSettings();
                bool settingsChanged = false;
                
                // Check if camera resolution changed (different camera connected)
                if (currentSettings.Width != actualWidth || currentSettings.Height != actualHeight)
                {
                    Debug.WriteLine($"[CameraSettings] ?? Camera resolution changed!");
                    Debug.WriteLine($"[CameraSettings]   Old: {currentSettings.Width}x{currentSettings.Height}");
                    Debug.WriteLine($"[CameraSettings]   New: {actualWidth}x{actualHeight}");
                    
                    currentSettings.Width = actualWidth;
                    currentSettings.Height = actualHeight;
                    settingsChanged = true;
                }
                
                // Check if pixel size changed
                if (Math.Abs(currentSettings.PixelSize_um - actualPixelSize) > 0.01)
                {
                    Debug.WriteLine($"[CameraSettings] ?? Pixel size changed!");
                    Debug.WriteLine($"[CameraSettings]   Old: {currentSettings.PixelSize_um} ?m");
                    Debug.WriteLine($"[CameraSettings]   New: {actualPixelSize} ?m");
                    
                    currentSettings.PixelSize_um = actualPixelSize;
                    settingsChanged = true;
                }
                
                // Recalculate PlateScale if pixel size or resolution changed
                if (settingsChanged && currentSettings.FocalLength_mm > 0)
                {
                    double newPlateScale = (actualPixelSize * currentSettings.Binning / currentSettings.FocalLength_mm) * 206.265;
                    currentSettings.PlateScale_ArcsecPerPixel = newPlateScale;
                    Debug.WriteLine($"[CameraSettings] ?? PlateScale recalculated: {newPlateScale:F3} arcsec/pixel");
                }
                
                // Save updated settings if changed
                if (settingsChanged)
                {
                    Debug.WriteLine("[CameraSettings] ?? Saving auto-detected camera specs to settings...");
                    AppSettings.SaveCameraSettings(currentSettings);
                    Debug.WriteLine("[CameraSettings] ? Camera specs saved successfully!");
                    
                    // Reload UI with new settings
                    _isUpdatingUI = true;
                    // Settings already updated, no need to reload from AppSettings
                    _isUpdatingUI = false;
                }
                
                Debug.WriteLine("[CameraSettings] =========================================");

                // Enable controls
                EnableControls();
                
                // Update FOV info with actual values
                UpdateFOVInfo();

                Debug.WriteLine("[CameraSettings] Camera info loaded, controls enabled");
                Debug.WriteLine($"[CameraSettings] Current UI values: Exposure={txtExposureTime.Text}ms, Gain={txtGain.Text}, Offset={txtOffset.Text}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings ERROR] Failed to load current settings: {ex.Message}");
                MessageBox.Show($"Failed to load camera settings:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static readonly SolidColorBrush _labelEnabled = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private static readonly SolidColorBrush _labelDisabled = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
        private static readonly SolidColorBrush _unitDisabled = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

        private void EnableControls()
        {
            bool supportsGain = _cameraProvider?.SupportsGain ?? false;
            bool supportsOffset = _cameraProvider?.SupportsOffset ?? false;
            bool supportsImageType = _cameraProvider?.SupportsImageType ?? false;
            bool supportsCooler = _cameraProvider?.HasCooler ?? false;

            // Input controls
            txtExposureTime.IsEnabled = true;
            sliderGain.IsEnabled = supportsGain;
            txtGain.IsEnabled = supportsGain;
            sliderOffset.IsEnabled = supportsOffset;
            txtOffset.IsEnabled = supportsOffset;
            cmbBinning.IsEnabled = true;
            cmbImageType.IsEnabled = supportsImageType;
            txtFocalLength.IsEnabled = true;
            chkCoolerOn.IsEnabled = supportsCooler;
            txtCoolerTemp.IsEnabled = supportsCooler;

            // Action buttons
            btnTestCapture.IsEnabled = true;
            btnApply.IsEnabled = true;

            // Labels back to normal
            SetLabelsEnabled(true);
        }

        private void SelectImageType(ASI_IMG_TYPE imageType)
        {
            if (cmbImageType == null)
                return;

            bool previousUpdatingState = _isUpdatingUI;
            _isUpdatingUI = true;

            try
            {
                for (int i = 0; i < cmbImageType.Items.Count; i++)
                {
                    if (cmbImageType.Items[i] is ComboBoxItem item &&
                        item.Tag is string tag &&
                        int.TryParse(tag, out int type) &&
                        type == (int)imageType)
                    {
                        cmbImageType.SelectedIndex = i;
                        return;
                    }
                }
            }
            finally
            {
                _isUpdatingUI = previousUpdatingState;
            }
        }

        private void DisableControls()
        {
            // Input controls
            txtExposureTime.IsEnabled = false;
            sliderGain.IsEnabled = false;
            txtGain.IsEnabled = false;
            sliderOffset.IsEnabled = false;
            txtOffset.IsEnabled = false;
            cmbBinning.IsEnabled = false;
            cmbImageType.IsEnabled = false;
            txtFocalLength.IsEnabled = false;
            chkCoolerOn.IsEnabled = false;
            txtCoolerTemp.IsEnabled = false;

            // Action buttons
            btnTestCapture.IsEnabled = false;
            btnApply.IsEnabled = false;

            // Dim labels
            SetLabelsEnabled(false);
        }

        /// <summary>
        /// Dim or restore all setting label TextBlocks
        /// </summary>
        private void SetLabelsEnabled(bool enabled)
        {
            var brush = enabled ? _labelEnabled : _labelDisabled;
            var unitBrush = enabled ? _labelEnabled : _unitDisabled;

            // Iterate direct TextBlock children in gridSettings
            foreach (var child in gridSettings.Children)
            {
                if (child is TextBlock tb && tb != txtFOVInfo)
                {
                    // Skip camera label at Row 0, Col 0 (the "Camera:" label)
                    var row = Grid.GetRow(tb);
                    var col = Grid.GetColumn(tb);
                    if (row == 0 && col == 0) continue;

                    tb.Foreground = tb.Text is "ms" or "mm" or "°C" ? unitBrush : brush;
                }
            }

            // Also dim FOV info
            if (txtFOVInfo != null && !enabled)
            {
                txtFOVInfo.Foreground = _labelDisabled;
            }
        }

        private void SliderGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isUpdatingUI && txtGain != null)
            {
                _isUpdatingUI = true;
                txtGain.Text = ((int)sliderGain.Value).ToString();
                _isUpdatingUI = false;
            }
        }

        private void TxtGain_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUpdatingUI && sliderGain != null)
            {
                if (int.TryParse(txtGain.Text, out int value))
                {
                    _isUpdatingUI = true;
                    sliderGain.Value = Math.Clamp(value, 0, 600);
                    _isUpdatingUI = false;
                }
            }
        }

        private void SliderOffset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isUpdatingUI && txtOffset != null)
            {
                _isUpdatingUI = true;
                txtOffset.Text = ((int)sliderOffset.Value).ToString();
                _isUpdatingUI = false;
            }
        }

        private void TxtOffset_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUpdatingUI && sliderOffset != null)
            {
                if (int.TryParse(txtOffset.Text, out int value))
                {
                    _isUpdatingUI = true;
                    sliderOffset.Value = Math.Clamp(value, 0, 100);
                    _isUpdatingUI = false;
                }
            }
        }

        private void CmbResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Resolution dropdown removed - no longer used
            // Always use full frame with binning
        }

        private void CmbBinning_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;

            var item = cmbBinning.SelectedItem as ComboBoxItem;
            if (item?.Tag is string tag && int.TryParse(tag, out int binning))
            {
                Debug.WriteLine($"[CameraSettings] ?? Binning changed in UI to {binning}x{binning}");
                Debug.WriteLine($"[CameraSettings] Current camera binning: {_cameraProvider?.CurrentBinning ?? -1}x{_cameraProvider?.CurrentBinning ?? -1}");
                Debug.WriteLine($"[CameraSettings] NOTE: Binning will be applied when 'Apply' or 'Test Capture' is clicked");
                UpdateCameraNameDisplay();
                UpdateFOVInfo(); // Recalculate FOV
            }
        }

        private void CmbImageType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;

            var item = cmbImageType.SelectedItem as ComboBoxItem;
            if (item != null)
            {
                Debug.WriteLine($"[CameraSettings] Image type changed to {item.Content}");
            }
        }
        
        /// <summary>
        /// Focal Length TextChanged - Update FOV info
        /// </summary>
        private void TxtFocalLength_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            UpdateFOVInfo(); // Recalculate FOV when focal length changes
        }
        
        /// <summary>
        /// Update Field of View info when settings change
        /// </summary>
        private void UpdateFOVInfo()
        {
            try
            {
                if (_cameraProvider == null || txtFOVInfo == null)
                    return;

                // Get focal length
                if (!double.TryParse(txtFocalLength.Text, out double focalLength) || focalLength <= 0)
                {
                    txtFOVInfo.Text = "FOV: Enter focal length";
                    txtFOVInfo.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Orange
                    return;
                }

                // Get binning
                int binning = 1;
                var binItem = cmbBinning.SelectedItem as ComboBoxItem;
                if (binItem?.Tag is string binTag && int.TryParse(binTag, out int bin))
                {
                    binning = bin;
                }

                // Get camera info
                int width = _cameraProvider.MaxWidth;
                int height = _cameraProvider.MaxHeight;
                double pixelSize_um = NormalizePixelSizeMicrons(_cameraProvider.PixelSize);

                // Validate PixelSize
                if (pixelSize_um <= 0 || pixelSize_um > 100 || double.IsNaN(pixelSize_um) || double.IsInfinity(pixelSize_um))
                {
                    pixelSize_um = 3.76; // ASI461MM Pro default
                }

                // Calculate PlateScale
                double plateScale = (pixelSize_um * binning / focalLength) * 206.265;

                // Calculate FOV (binned image size)
                int binnedWidth = width / binning;
                int binnedHeight = height / binning;
                double fovWidth_deg = (binnedWidth * plateScale) / 3600.0;
                double fovHeight_deg = (binnedHeight * plateScale) / 3600.0;

                // Update display
                txtFOVInfo.Text = $"FOV: {fovWidth_deg:F3}° × {fovHeight_deg:F3}° (PlateScale: {plateScale:F3}\"/px)";
                txtFOVInfo.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] UpdateFOVInfo error: {ex.Message}");
            }
        }

        private int GetSelectedBinning()
        {
            var binItem = cmbBinning.SelectedItem as ComboBoxItem;
            if (binItem?.Tag is string binTag && int.TryParse(binTag, out int binning) && binning > 0)
                return binning;

            return _cameraProvider?.CurrentBinning > 0 ? _cameraProvider.CurrentBinning : 1;
        }

        private void UpdateCameraNameDisplay()
        {
            if (txtCameraName == null || _cameraProvider == null || !_cameraProvider.IsConnected)
                return;

            int binning = GetSelectedBinning();
            int displayedWidth = Math.Max(1, _cameraProvider.MaxWidth / Math.Max(1, binning));
            int displayedHeight = Math.Max(1, _cameraProvider.MaxHeight / Math.Max(1, binning));
            string cameraName = _cameraProvider.CameraName;

            txtCameraName.Text = binning > 1
                ? $"{cameraName} ({displayedWidth}x{displayedHeight}, Bin {binning}x{binning})"
                : $"{cameraName} ({displayedWidth}x{displayedHeight})";
        }



        private async void BtnTestCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraProvider == null || !_cameraProvider.IsConnected)
            {
                MessageBox.Show("Camera is not connected!", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // Get exposure time for countdown
                if (int.TryParse(txtExposureTime.Text, out int exposureMs))
                {
                    _exposureDurationSeconds = exposureMs / 1000.0;
                }
                else
                {
                    _exposureDurationSeconds = 1.0;
                }
                
                // Show loading with countdown
                loadingPanel.Visibility = Visibility.Visible;
                btnTestCapture.IsEnabled = false;
                imgPreview.Source = null; // Clear previous image
                
                // Start countdown timer
                _captureStartTime = DateTime.Now;
                StartCountdownTimer();

                Debug.WriteLine("[CameraSettings] ===========================================");
                Debug.WriteLine("[CameraSettings] 📷 STARTING TEST CAPTURE...");
                Debug.WriteLine("[CameraSettings] ===========================================");
                
                // Log UI settings before apply
                var binItem = cmbBinning.SelectedItem as ComboBoxItem;
                int uiBinning = binItem?.Tag is string binTag && int.TryParse(binTag, out int bin) ? bin : 1;
                Debug.WriteLine($"[CameraSettings] UI Binning setting: {uiBinning}x{uiBinning}");
                Debug.WriteLine($"[CameraSettings] Camera Binning before apply: {_cameraProvider.CurrentBinning}x{_cameraProvider.CurrentBinning}");

                // Apply current settings before capture
                if (!ApplySettingsToCamera())
                    return;
                
                Debug.WriteLine($"[CameraSettings] Camera Binning after apply: {_cameraProvider.CurrentBinning}x{_cameraProvider.CurrentBinning}");

                // Capture image in background thread
                var imageData = await _cameraProvider.CaptureImageAsync();
                
                // Stop countdown timer
                StopCountdownTimer();

                if (imageData != null)
                {
                    Debug.WriteLine($"[CameraSettings] Image captured: {imageData.Width}x{imageData.Height}, Size={imageData.ImageData.Length} bytes");

                    // Store RAW data for histogram stretching
                    _originalImageData = imageData.ImageData;
                    _originalImageWidth = imageData.Width;
                    _originalImageHeight = imageData.Height;
                    _isRAW16 = (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16);
                    
                    // Auto-detect min/max for first image
                    if (_isFirstLoad)
                    {
                        AutoDetectHistogramRange(imageData);
                    }
                    
                    
                    // ?? ALL HEAVY OPERATIONS IN PARALLEL BACKGROUND TASKS!
                    var tasks = new List<Task>();
                    
                    // Task 1: Store image in repository (ASYNC!)
                    Task<CapturedImageInfo> storeTask = null;
                    if (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16)
                    {
                        storeTask = Task.Run(() =>
                        {
                            try
                            {
                                // Get current mount position
                                double azimuth = 0.0;
                                double altitude = 45.0;
                                
                                // Get camera settings
                                var cameraSettings = AppSettings.GetCameraSettings();
                                
                                // Create CapturedImageInfo (heavy: converts byte[] ? ushort[,])
                                var capturedImage = CapturedImageInfo.FromCameraImageData(
                                    imageData,
                                    azimuth,
                                    altitude,
                                    cameraSettings.FocalLength_mm,
                                    cameraSettings.PlateScale_ArcsecPerPixel);
                                
                                // Add to repository
                                if (_imageRepository.AddImage(capturedImage))
                                {
                                    Debug.WriteLine($"[CameraSettings] ?? Image stored in repository:");
                                    Debug.WriteLine($"  ID: {capturedImage.ImageId:N}");
                                    Debug.WriteLine($"  Size: {capturedImage.GetImageSize_MB():F2} MB");
                                    Debug.WriteLine($"  Stats: Min={capturedImage.MinPixelValue}, Max={capturedImage.MaxPixelValue}, Mean={capturedImage.MeanPixelValue:F1}");
                                    Debug.WriteLine($"  Repository: {_imageRepository.Count} images, {_imageRepository.TotalMemoryUsage_MB:F2} MB total");
                                }
                                
                                return capturedImage;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[CameraSettings ERROR] Failed to store image in repository: {ex.Message}");
                                return null;
                            }
                        });
                    }
                    
                    // Task 2: Convert to Bitmap (ASYNC!)
                    var bitmapTask = Task.Run(() => ConvertToBitmap(imageData));

                    // Wait for both tasks to complete
                    if (storeTask != null)
                        _lastCapturedImage = await storeTask;
                    
                    var bitmap = await bitmapTask;
                    
                    if (bitmap != null)
                    {
                        // Update UI on UI thread (quick operations only!)
                        Dispatcher.Invoke(() =>
                        {
                            // Store original image for image processing
                            _originalImage = bitmap;
                            _originalImage.Freeze(); // Freeze for thread safety
                            
                            // Update image info with ACTUAL binned image size
                            if (txtImageInfo != null)
                            {
                                // Show actual captured image size (after binning)
                                txtImageInfo.Text = $"Image: {imageData.Width}x{imageData.Height} (Bin {imageData.Binning}x{imageData.Binning})";
                            }
                            
                            // [CHANGED] Auto-fit to window on first load
                            if (_isFirstLoad)
                            {
                                _isFirstLoad = false;
                                // Fit image to preview area instead of 1:1
                                FitImageToWindow();
                            }
                            else
                            {
                                // Process and display image with current zoom
                                ProcessAndDisplayImage();
                            }
                            
                            Debug.WriteLine("[CameraSettings] Image displayed successfully");
                            Debug.WriteLine($"[CameraSettings] Image size: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
                            Debug.WriteLine($"[CameraSettings] Border size: {borderPreview.ActualWidth}x{borderPreview.ActualHeight}");
                        });
                    }
                    else
                    {
                        Debug.WriteLine("[CameraSettings ERROR] Failed to convert image to bitmap");
                        MessageBox.Show("Failed to convert image for display!", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    Debug.WriteLine("[CameraSettings ERROR] Capture returned null");
                    MessageBox.Show("Failed to capture image!\n\nPossible causes:\n- Exposure timeout\n- Camera disconnected\n- USB bandwidth issue", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings ERROR] Test capture failed: {ex.Message}");
                Debug.WriteLine($"[CameraSettings ERROR] Stack: {ex.StackTrace}");
                MessageBox.Show($"Failed to capture image:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopCountdownTimer();
                loadingPanel.Visibility = Visibility.Collapsed;
                btnTestCapture.IsEnabled = true;
            }
        }
        
        /// <summary>
        /// Start countdown timer for exposure progress
        /// </summary>
        private void StartCountdownTimer()
        {
            if (_countdownTimer == null)
            {
                _countdownTimer = new System.Windows.Threading.DispatcherTimer();
                _countdownTimer.Interval = TimeSpan.FromMilliseconds(50); // Update every 50ms for smooth display
                _countdownTimer.Tick += CountdownTimer_Tick;
            }
            
            
            txtCountdown.Text = $"{_exposureDurationSeconds:F3} s";
            _countdownTimer.Start();
        }
        
        /// <summary>
        /// Stop countdown timer
        /// </summary>
        private void StopCountdownTimer()
        {
            if (_countdownTimer != null && _countdownTimer.IsEnabled)
            {
                _countdownTimer.Stop();
            }
            txtCountdown.Text = "Done!";
            txtCountdown.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
        }
        
        /// <summary>
        /// Countdown timer tick event - update elapsed time display based on camera status
        /// </summary>
        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Get actual exposure status from camera (ZWO-specific, fallback for FLI)
                var expStatus = ZWOASICameraSDK.ASI_EXPOSURE_STATUS.ASI_EXP_IDLE;
                if (_cameraProvider is ZWOCameraProvider zwoProv)
                {
                    expStatus = zwoProv.GetExposureStatus();
                }
                
                double elapsedSeconds = (DateTime.Now - _captureStartTime).TotalSeconds;
                double remainingSeconds = _exposureDurationSeconds - elapsedSeconds;
                
                if (expStatus == ZWOASICameraSDK.ASI_EXPOSURE_STATUS.ASI_EXP_SUCCESS)
                {
                    // Exposure complete, now reading out data
                    txtCountdown.Text = "Reading out...";
                    txtCountdown.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow/Orange
                }
                else if (expStatus == ZWOASICameraSDK.ASI_EXPOSURE_STATUS.ASI_EXP_WORKING)
                {
                    // Exposure in progress - show remaining time or "Reading out..." when time is up
                    if (remainingSeconds > 0)
                    {
                        txtCountdown.Text = $"{remainingSeconds:F3} s";
                        txtCountdown.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                    }
                    else
                    {
                        // Time is up but camera still working = Reading out
                        txtCountdown.Text = "Reading out...";
                        txtCountdown.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow
                    }
                }
                else if (expStatus == ZWOASICameraSDK.ASI_EXPOSURE_STATUS.ASI_EXP_FAILED)
                {
                    txtCountdown.Text = "Failed!";
                    txtCountdown.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                }
                else
                {
                    // IDLE or unknown - show remaining time based on calculation
                    if (remainingSeconds > 0)
                    {
                        txtCountdown.Text = $"{remainingSeconds:F3} s";
                        txtCountdown.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                    }
                    else
                    {
                        txtCountdown.Text = "Reading out...";
                        txtCountdown.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] CountdownTimer_Tick error: {ex.Message}");
            }
        }

        private BitmapSource ConvertToBitmap(CameraImageData imageData)
        {
            try
            {
                Debug.WriteLine($"[CameraSettings] Converting image: {imageData.Width}x{imageData.Height}, Type={imageData.ImageType}");
                Debug.WriteLine($"[CameraSettings] Data size: {imageData.ImageData.Length} bytes");

                if (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16)
                {
                    // Convert RAW16 to 8-bit for display with proper scaling
                    byte[] displayData = new byte[imageData.Width * imageData.Height];
                    
                    // ? OPTIMIZATION: Use Parallel.For for faster processing
                    // Find min/max in parallel
                    object lockObj = new object();
                    ushort min = ushort.MaxValue;
                    ushort max = ushort.MinValue;
                    
                    Parallel.For(0, displayData.Length, () => (Min: ushort.MaxValue, Max: ushort.MinValue),
                        (i, state, local) =>
                        {
                            ushort value = BitConverter.ToUInt16(imageData.ImageData, i * 2);
                            if (value < local.Min) local.Min = value;
                            if (value > local.Max) local.Max = value;
                            return local;
                        },
                        local =>
                        {
                            lock (lockObj)
                            {
                                if (local.Min < min) min = local.Min;
                                if (local.Max > max) max = local.Max;
                            }
                        });
                    
                    Debug.WriteLine($"[CameraSettings] RAW16 range: {min} - {max} (found using parallel processing)");
                    
                    // Check if image is completely dark
                    if (max == min)
                    {
                        Debug.WriteLine("[CameraSettings WARNING] Image is completely flat (min == max)!");
                        Debug.WriteLine("[CameraSettings] This means the camera captured a completely dark image.");
                        Debug.WriteLine("[CameraSettings] Possible causes:");
                        Debug.WriteLine("[CameraSettings]   - Lens cap is on");
                        Debug.WriteLine("[CameraSettings]   - Room is completely dark");
                        Debug.WriteLine("[CameraSettings]   - Exposure time too short");
                        Debug.WriteLine("[CameraSettings]   - Gain too low");
                        
                        // Fill with mid-gray pattern for visibility
                        for (int i = 0; i < displayData.Length; i++)
                        {
                            displayData[i] = (byte)((i % 100) < 50 ? 100 : 150); // Pattern to show it's working
                        }
                    }
                    else
                    {
                        // ? OPTIMIZATION: Apply auto-stretch with gamma in parallel
                        double scale = 255.0 / (max - min);
                        const double gamma = 2.2; // Gamma correction for dark images
                        
                        Parallel.For(0, displayData.Length, i =>
                        {
                            ushort value = BitConverter.ToUInt16(imageData.ImageData, i * 2);
                            double normalized = (value - min) * scale / 255.0; // 0-1
                            double corrected = Math.Pow(normalized, 1.0 / gamma); // Apply gamma
                            displayData[i] = (byte)Math.Min(255, Math.Max(0, corrected * 255));
                        });
                        
                        Debug.WriteLine($"[CameraSettings] Applied auto-stretch with gamma={gamma} (parallel)");
                    }

                    var bitmap = BitmapSource.Create(
                        imageData.Width,
                        imageData.Height,
                        96, 96,
                        PixelFormats.Gray8,
                        null,
                        displayData,
                        imageData.Width);
                    
                    bitmap.Freeze(); // Important for UI thread
                    Debug.WriteLine("[CameraSettings] RAW16 image converted successfully");
                    
                    return bitmap;
                }
                else if (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW8)
                {
                    var bitmap = BitmapSource.Create(
                        imageData.Width,
                        imageData.Height,
                        96, 96,
                        PixelFormats.Gray8,
                        null,
                        imageData.ImageData,
                        imageData.Width);
                    
                    bitmap.Freeze();
                    Debug.WriteLine("[CameraSettings] RAW8 image converted successfully");
                    return bitmap;
                }
                else if (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RGB24)
                {
                    var bitmap = BitmapSource.Create(
                        imageData.Width,
                        imageData.Height,
                        96, 96,
                        PixelFormats.Rgb24,
                        null,
                        imageData.ImageData,
                        imageData.Width * 3);
                    
                    bitmap.Freeze();
                    Debug.WriteLine("[CameraSettings] RGB24 image converted successfully");
                    return bitmap;
                }

                Debug.WriteLine($"[CameraSettings] Unsupported image type: {imageData.ImageType}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings ERROR] Failed to convert image: {ex.Message}");
                Debug.WriteLine($"[CameraSettings ERROR] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Apply to camera
                if (!ApplySettingsToCamera())
                    return;

                // Save to AppSettings
                SaveSettingsToAppSettings();
                AddLog("[SAV] Camera setting has been saved", LogType.Message);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to apply settings:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                    SimpleLogger.Error(message, "Camera");
                }
                else if (type == LogType.Warning)
                {
                    SimpleLogger.Warning(message, "Camera");
                }
                else
                {
                    SimpleLogger.Info(message, "Camera");
                }
            }), DispatcherPriority.Background);
        }

        /// <summary>
        /// Save current settings to AppSettings.json
        /// </summary>
        private void SaveSettingsToAppSettings()
        {
            try
            {
                // Get values from UI (authoritative source for saving)
                int exposureMs = 1000;
                int gain = 0;
                int offset = 0;
                bool supportsGain = _cameraProvider?.SupportsGain ?? false;
                bool supportsOffset = _cameraProvider?.SupportsOffset ?? false;
                bool supportsImageType = _cameraProvider?.SupportsImageType ?? false;

                if (int.TryParse(txtExposureTime.Text, out int uiExposureMs))
                    exposureMs = uiExposureMs;
                if (supportsGain && int.TryParse(txtGain.Text, out int uiGain))
                    gain = uiGain;
                if (supportsOffset && int.TryParse(txtOffset.Text, out int uiOffset))
                    offset = uiOffset;

                // Get full frame resolution from provider (or 0 if no camera)
                int width = _cameraProvider?.MaxWidth ?? 0;
                int height = _cameraProvider?.MaxHeight ?? 0;

                // Get binning
                int binning = 1;
                var binItem = cmbBinning.SelectedItem as ComboBoxItem;
                if (binItem?.Tag is string binTag && int.TryParse(binTag, out int bin))
                {
                    binning = bin;
                    Debug.WriteLine($"[CameraSettings] ? Binning from UI: {binning}x{binning} (Tag: {binTag})");
                }
                else
                {
                    Debug.WriteLine($"[CameraSettings] ?? WARNING: Failed to read binning from UI! Using default binning=1");
                    Debug.WriteLine($"[CameraSettings]   cmbBinning.SelectedItem: {cmbBinning.SelectedItem}");
                    Debug.WriteLine($"[CameraSettings]   binItem: {binItem}");
                    if (binItem != null)
                    {
                        Debug.WriteLine($"[CameraSettings]   binItem.Tag: {binItem.Tag}");
                        Debug.WriteLine($"[CameraSettings]   binItem.Content: {binItem.Content}");
                    }
                }

                // Get image type
                int imageType = (int)ASI_IMG_TYPE.ASI_IMG_RAW16;
                var imgTypeItem = cmbImageType.SelectedItem as ComboBoxItem;
                if (supportsImageType && imgTypeItem?.Tag is string typeTag && int.TryParse(typeTag, out int type))
                {
                    imageType = type;
                }

                // Get focal length
                double focalLength = 200.0; // Default
                if (double.TryParse(txtFocalLength.Text, out double fl))
                {
                    focalLength = fl;
                }

                bool coolerEnabled = chkCoolerOn.IsChecked == true;
                double coolerTargetTemp = -10.0;
                if (double.TryParse(txtCoolerTemp.Text, out double uiCoolerTarget))
                {
                    coolerTargetTemp = uiCoolerTarget;
                }

                // [NEW] Calculate PlateScale from camera PixelSize + FocalLength + Binning
                // Formula: PlateScale = (PixelSize_um * Binning / FocalLength_mm) * 206.265
                double plateScale = 0.0;
                double pixelSize_um = NormalizePixelSizeMicrons(_cameraProvider?.PixelSize ?? 0);
                
                Debug.WriteLine($"[CameraSettings] === PlateScale Calculation ===");
                Debug.WriteLine($"  Camera: {_cameraProvider?.CameraName ?? "Unknown"}");
                Debug.WriteLine($"  Camera PixelSize: {pixelSize_um} µm");
                Debug.WriteLine($"  Focal Length: {focalLength} mm");
                Debug.WriteLine($"  Binning: {binning}x{binning}");
                
                // Validate PixelSize (detect corruption)
                if (pixelSize_um > 0 && pixelSize_um < 100 && !double.IsNaN(pixelSize_um) && !double.IsInfinity(pixelSize_um))
                {
                    // PixelSize is valid - calculate PlateScale
                    plateScale = (pixelSize_um * binning / focalLength) * 206.265;
                    Debug.WriteLine($"  Calculated PlateScale: {plateScale:F3} arcsec/pixel");
                }
                else
                {
                    Debug.WriteLine($"  ?? WARNING: PixelSize invalid ({pixelSize_um}) - using default 3.76µm");
                    pixelSize_um = 3.76; // ASI461MM Pro default
                    plateScale = (pixelSize_um * binning / focalLength) * 206.265;
                    Debug.WriteLine($"  Fallback PlateScale: {plateScale:F3} arcsec/pixel");
                }
                
                // Validate PlateScale
                if (plateScale <= 0 || plateScale > 100 || double.IsNaN(plateScale) || double.IsInfinity(plateScale))
                {
                    Debug.WriteLine($"  ? ERROR: PlateScale validation failed ({plateScale})!");
                    plateScale = 0.0; // Mark as invalid
                }
                
                Debug.WriteLine($"[CameraSettings] ? Final PlateScale: {plateScale:F3} arcsec/pixel");

                // Calculate Field of View (binned image size)
                int binnedWidth = width / binning;
                int binnedHeight = height / binning;
                double fovWidth_deg = (binnedWidth * plateScale) / 3600.0;
                double fovHeight_deg = (binnedHeight * plateScale) / 3600.0;
                
                Debug.WriteLine($"[CameraSettings] === Field of View ===");
                Debug.WriteLine($"  Binned Resolution: {binnedWidth}x{binnedHeight}");
                Debug.WriteLine($"  FOV: {fovWidth_deg:F3}° × {fovHeight_deg:F3}°");
                
                // Update FOV display in UI
                if (txtFOVInfo != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        txtFOVInfo.Text = $"FOV: {fovWidth_deg:F3}° × {fovHeight_deg:F3}° (PlateScale: {plateScale:F3}\"/px)";
                        txtFOVInfo.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                    });
                }

                // [FIX] Ensure PixelSize is valid (already calculated above)
                if (pixelSize_um <= 0)
                {
                    // Fallback to calculation from plateScale
                    pixelSize_um = (plateScale * focalLength) / (binning * 206.265);
                }

                // Create settings object with ALL camera data
                var settings = new ZWOCameraSettings
                {
                    ExposureTime_ms = exposureMs,
                    Gain = gain,
                    Offset = offset,
                    Width = width,
                    Height = height,
                    Binning = binning,
                    ImageType = imageType,
                    FocalLength_mm = focalLength,
                    PlateScale_ArcsecPerPixel = plateScale,
                    PixelSize_um = pixelSize_um,  // [IMPORTANT] Save actual PixelSize from camera
                    CoolerEnabled = coolerEnabled,
                    CoolerTargetTemp_C = coolerTargetTemp,
                    PreferredCameraType = (_cameraProvider?.CameraType ?? CameraType.ZWO_ASI).ToString(),
                    // [NEW] Save Histogram Stretch Profile with ProfileType
                    HistogramStretch = new HistogramStretchProfile
                    {
                        ProfileType = _currentProfileType,
                        MinValue = _histogramMin,
                        MaxValue = _histogramMax,
                        AutoStretchOnCapture = false,
                        BlackPercentile = HistogramStretchProfile.GetPresetPercentiles(_currentProfileType).Black,
                        WhitePercentile = HistogramStretchProfile.GetPresetPercentiles(_currentProfileType).White
                    }
                };

                Debug.WriteLine("[CameraSettings] =========================================");
                Debug.WriteLine("[CameraSettings] 📝 SAVING TO APPSETTINGS...");
                Debug.WriteLine("[CameraSettings] =========================================");
                Debug.WriteLine($"  Camera: {_cameraProvider?.CameraName ?? "Unknown"}");
                Debug.WriteLine($"  Exposure: {exposureMs} ms");
                Debug.WriteLine($"  Gain: {gain}");
                Debug.WriteLine($"  Offset: {offset}");
                Debug.WriteLine($"  Resolution: {width}x{height}");
                Debug.WriteLine($"  📌 Binning: {binning}x{binning}");
                Debug.WriteLine($"  Image Type: {imageType}");
                Debug.WriteLine($"  Focal Length: {focalLength} mm");
                Debug.WriteLine($"  Pixel Size: {pixelSize_um:F2} µm");
                Debug.WriteLine($"  PlateScale: {plateScale:F3} arcsec/pixel");
                Debug.WriteLine($"  Cooler Enabled: {coolerEnabled}");
                Debug.WriteLine($"  Cooler Target: {coolerTargetTemp:0.#} °C");
                Debug.WriteLine($"  📊 Histogram Profile: {_currentProfileType}, Min={_histogramMin}, Max={_histogramMax}");

                // Save to AppSettings
                AppSettings.SaveCameraSettings(settings);

                Debug.WriteLine("[CameraSettings] ? Settings saved to AppSettings successfully");
                Debug.WriteLine("[CameraSettings] =========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings ERROR] Failed to save to AppSettings: {ex.Message}");
                throw;
            }
        }

        private bool ApplySettingsToCamera()
        {
            if (_cameraProvider == null || !_cameraProvider.IsConnected)
                return false;

            Debug.WriteLine("[CameraSettings] ========================================");
            Debug.WriteLine("[CameraSettings] 🔧 APPLYING SETTINGS TO CAMERA...");
            Debug.WriteLine("[CameraSettings] ========================================");

            var binItem = cmbBinning.SelectedItem as ComboBoxItem;
            int binning = binItem?.Tag is string binTag && int.TryParse(binTag, out int bin) ? bin : 1;

            // ---- ZWO-specific apply ----
            if (_cameraProvider is ZWOCameraProvider zwoProv)
            {
                var imgTypeItem = cmbImageType.SelectedItem as ComboBoxItem;
                int imgType = imgTypeItem?.Tag is string typeTag && int.TryParse(typeTag, out int type) ? type : 2;

                Debug.WriteLine($"[CameraSettings]   Binning: {binning}x{binning}  ImageType: {(ASI_IMG_TYPE)imgType}");
                Debug.WriteLine($"[CameraSettings]   Current: Bin={zwoProv.Manager.CurrentBinning}, Type={zwoProv.CurrentImageType}, Size={zwoProv.CurrentWidth}x{zwoProv.CurrentHeight}");

                // RGB24 does not support binning > 1
                if (binning > 1 && imgType == 1)
                {
                    MessageBox.Show(
                        "RGB24 (Color) does not support binning > 1x1!\n\nPlease use RAW16 or RAW8 with binning.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                int fullWidth = zwoProv.MaxWidth;
                int fullHeight = zwoProv.MaxHeight;
                if (binning > 1)
                {
                    fullWidth = (fullWidth / binning) * binning;
                    fullHeight = (fullHeight / binning) * binning;
                }

                bool success = zwoProv.SetROIAndImageType(fullWidth, fullHeight, binning, (ASI_IMG_TYPE)imgType, out var errorCode);
                if (!success)
                {
                    MessageBox.Show($"Failed to set ZWO camera format!\nError: {errorCode}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                // Exposure (ZWO uses µs internally)
                if (int.TryParse(txtExposureTime.Text, out int exposureMs))
                    zwoProv.SetExposure(exposureMs);

                // Gain & Offset
                if (int.TryParse(txtGain.Text, out int gain))
                    zwoProv.SetGain(gain);
                if (int.TryParse(txtOffset.Text, out int offset))
                    zwoProv.SetOffset(offset);
            }
            // ---- FLI-specific apply ----
            else if (_cameraProvider is FLICameraProvider fliProv)
            {
                Debug.WriteLine($"[CameraSettings]   Binning: {binning}x{binning} (FLI CCD, always 16-bit)");

                bool binningApplied = fliProv.Manager.SetBinning(binning);
                if (!binningApplied)
                {
                    MessageBox.Show(
                        $"Failed to set FLI binning to {binning}x{binning}.\nPlease check the camera log/output window for ROI details.",
                        "FLI Camera Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return false;
                }

                if (int.TryParse(txtExposureTime.Text, out int exposureMs))
                {
                    bool exposureApplied = fliProv.SetExposure(exposureMs);
                    if (!exposureApplied)
                    {
                        MessageBox.Show(
                            $"Failed to set FLI exposure to {exposureMs} ms.",
                            "FLI Camera Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return false;
                    }
                }
            }

            UpdateCameraNameDisplay();

            Debug.WriteLine("[CameraSettings] ========================================");
            Debug.WriteLine("[CameraSettings] ✓ SETTINGS APPLIED SUCCESSFULLY");
            Debug.WriteLine("[CameraSettings] ========================================");
            return true;
        }

        private static double NormalizePixelSizeMicrons(double pixelSize)
        {
            if (pixelSize <= 0 || double.IsNaN(pixelSize) || double.IsInfinity(pixelSize))
                return pixelSize;

            if (pixelSize < 0.001)
                return pixelSize * 1_000_000.0;

            if (pixelSize < 1.0)
                return pixelSize * 1000.0;

            return pixelSize;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettingsToAppSettings();
                Debug.WriteLine("[CameraSettings] Persisted current settings on close for calibration use");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] Failed to persist settings on close: {ex.Message}");
            }

            Close();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        #region Image Zoom & Pan (Image Processing - NOT WPF Transform)

        /// <summary>
        /// Process and display image with current zoom and pan
        /// This performs actual image processing (crop + resize)
        /// </summary>
        private void ProcessAndDisplayImage()
        {
            if (_originalImage == null)
            {
                Debug.WriteLine("[ImageProcessing] No original image to process");
                return;
            }

            try
            {
                var sw = Stopwatch.StartNew();

                // Get original image dimensions
                int originalWidth = _originalImage.PixelWidth;
                int originalHeight = _originalImage.PixelHeight;

                // Get container size
                double containerWidth = borderPreview.ActualWidth;
                double containerHeight = borderPreview.ActualHeight;

                if (containerWidth <= 0 || containerHeight <= 0)
                {
                    Debug.WriteLine("[ImageProcessing] Container not ready, skipping");
                    return;
                }

                BitmapSource processedImage;
                double displayWidth;
                double displayHeight;

                if (_currentZoom >= 1.0)
                {
                    // ZOOM IN (>= 100%): Show full scaled image
                    // ScrollViewer will handle panning
                    displayWidth = originalWidth * _currentZoom;
                    displayHeight = originalHeight * _currentZoom;

                    // Scale up full image (ScrollViewer handles viewport)
                    processedImage = ScaleImage(_originalImage, (int)displayWidth, (int)displayHeight);
                }
                else
                {
                    // ZOOM OUT (< 100%): Use full image + Scale Down
                    // Calculate display size (full image * zoom)
                    displayWidth = originalWidth * _currentZoom;
                    displayHeight = originalHeight * _currentZoom;

                    // Scale down full image (no crop)
                    processedImage = ScaleImage(_originalImage, (int)displayWidth, (int)displayHeight);
                }

                if (processedImage != null)
                {
                    imgPreview.Source = processedImage;
                    
                    // Set explicit size for proper scrollbar behavior
                    imgPreview.Width = displayWidth;
                    imgPreview.Height = displayHeight;
                    
                    UpdateZoomInfo();

                    sw.Stop();
                }
                else
                {
                    Debug.WriteLine("[ImageProcessing] ? Failed to process image");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageProcessing ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Crop and scale image using Image Processing (for zoom >= 1.0)
        /// </summary>
        private BitmapSource CropAndScaleImage(BitmapSource source, int cropX, int cropY, int cropWidth, int cropHeight, int targetWidth, int targetHeight)
        {
            try
            {
                // Step 1: Crop
                var croppedBitmap = new CroppedBitmap(source, new Int32Rect(cropX, cropY, cropWidth, cropHeight));

                // Step 2: Scale
                var scaledBitmap = new TransformedBitmap(croppedBitmap, new ScaleTransform(
                    (double)targetWidth / cropWidth,
                    (double)targetHeight / cropHeight));

                // Freeze for performance
                scaledBitmap.Freeze();

                return scaledBitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageProcessing ERROR] Crop/Scale failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Scale down image using Image Processing (for zoom < 1.0)
        /// </summary>
        private BitmapSource ScaleImage(BitmapSource source, int targetWidth, int targetHeight)
        {
            try
            {
                // Ensure minimum size
                if (targetWidth < 1) targetWidth = 1;
                if (targetHeight < 1) targetHeight = 1;

                // Calculate scale factors
                double scaleX = (double)targetWidth / source.PixelWidth;
                double scaleY = (double)targetHeight / source.PixelHeight;

                // Scale image
                var scaledBitmap = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));

                // Freeze for performance
                scaledBitmap.Freeze();

                return scaledBitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageProcessing ERROR] Scale failed: {ex.Message}");
                return null;
            }
        }



        /// <summary>
        /// Mouse Wheel - Zoom In/Out (Image Processing)
        /// </summary>
        private void ImgPreview_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (_originalImage == null) return;

            try
            {
                // Calculate new zoom level
                double delta = e.Delta > 0 ? ZOOM_SPEED : -ZOOM_SPEED;
                double newZoom = _currentZoom + delta;

                // Clamp zoom level
                newZoom = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, newZoom));

                if (Math.Abs(newZoom - _currentZoom) < 0.001)
                    return;

                _currentZoom = newZoom;

                // Reprocess and display image
                ProcessAndDisplayImage();

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageProcessing ERROR] Mouse wheel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mouse Left Button Down - Start Dragging (Image Processing)
        /// </summary>
        private void ImgPreview_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_originalImage == null) return;

            try
            {
                _isDragging = true;
                _lastMousePosition = e.GetPosition(scrollViewer); // Use scrollViewer for reference
                imgPreview.CaptureMouse();
                imgPreview.Cursor = System.Windows.Input.Cursors.SizeAll;

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageProcessing ERROR] Mouse down failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mouse Move - Pan Image using ScrollViewer
        /// </summary>
        private void ImgPreview_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDragging || _originalImage == null)
                return;

            try
            {
                Point currentPosition = e.GetPosition(scrollViewer);

                // Calculate delta movement
                double deltaX = currentPosition.X - _lastMousePosition.X;
                double deltaY = currentPosition.Y - _lastMousePosition.Y;

                // Pan using ScrollViewer (inverted because we're scrolling the viewport)
                scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - deltaX);
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - deltaY);

                _lastMousePosition = currentPosition;

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageProcessing ERROR] Mouse move failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mouse Left Button Up - Stop Dragging (Image Processing)
        /// </summary>
        private void ImgPreview_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                imgPreview.ReleaseMouseCapture();
                imgPreview.Cursor = System.Windows.Input.Cursors.Hand;

                e.Handled = true;
            }
        }

        /// <summary>
        /// Mouse Leave - Stop Dragging (Image Processing)
        /// </summary>
        private void ImgPreview_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                imgPreview.ReleaseMouseCapture();
                imgPreview.Cursor = System.Windows.Input.Cursors.Hand;
            }
        }

        /// <summary>
        /// Fit image to window using Image Processing
        /// </summary>
        private void FitImageToWindow()
        {
            try
            {
                if (_originalImage == null)
                {
                    Debug.WriteLine("[ImageProcessing] No original image to fit");
                    return;
                }

                // Get original image dimensions
                double imageWidth = _originalImage.PixelWidth;
                double imageHeight = _originalImage.PixelHeight;

                // Get container dimensions
                double containerWidth = borderPreview.ActualWidth;
                double containerHeight = borderPreview.ActualHeight;

                Debug.WriteLine($"[ImageProcessing] Fit to window: Image={imageWidth}x{imageHeight}, Container={containerWidth}x{containerHeight}");

                if (containerWidth <= 0 || containerHeight <= 0)
                {
                    Debug.WriteLine("[ImageProcessing] Container not ready, retrying...");
                    Task.Delay(100).ContinueWith(_ => 
                    {
                        Dispatcher.BeginInvoke(new Action(() => FitImageToWindow()));
                    });
                    return;
                }

                // Calculate scale to fit
                double scaleX = containerWidth / imageWidth;
                double scaleY = containerHeight / imageHeight;
                double scale = Math.Min(scaleX, scaleY);

                // Clamp scale
                scale = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, scale));

                _currentZoom = scale;

                // Reprocess and display
                ProcessAndDisplayImage();
                
                // Reset scroll position
                scrollViewer.ScrollToHorizontalOffset(0);
                scrollViewer.ScrollToVerticalOffset(0);

                Debug.WriteLine($"[ImageProcessing] ? Fitted to window at {_currentZoom * 100:F0}%");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageProcessing ERROR] Fit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Border Size Changed - Reprocess image if needed
        /// </summary>
        private void BorderPreview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Debug.WriteLine($"[ImageProcessing] Container resized to {e.NewSize.Width}x{e.NewSize.Height}");
            
            // Only reprocess if image exists
            if (_originalImage != null && !_isDragging)
            {
                ProcessAndDisplayImage();
            }
        }

        /// <summary>
        /// Reset to 100% zoom using Image Processing
        /// </summary>
        private void ResetImageTransform()
        {
            _currentZoom = 1.0;
            _isDragging = false;
            
            if (_originalImage != null)
            {
                ProcessAndDisplayImage();
                
                // Reset scroll position
                scrollViewer.ScrollToHorizontalOffset(0);
                scrollViewer.ScrollToVerticalOffset(0);
            }
            
            Debug.WriteLine("[ImageProcessing] Reset to 100% scale");
        }

        /// <summary>
        /// Update Zoom Info Text (Image Processing)
        /// </summary>
        private void UpdateZoomInfo()
        {
            if (txtZoomInfo != null)
            {
                // Display zoom as percentage (1.0 = 100%)
                txtZoomInfo.Text = $"Zoom: {_currentZoom * 100:F0}%";
            }
        }

        /// <summary>
        /// "Fit to Window" Button - Fit image to window
        /// </summary>
        private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            FitImageToWindow();
            Debug.WriteLine("[ImageZoom] View reset to fit window");
        }

        /// <summary>
        /// "Fill View" Button - Show image at 1:1 (100%) scale
        /// </summary>
        private void BtnReset1to1_Click(object sender, RoutedEventArgs e)
        {
            if (imgPreview.Source == null)
            {
                MessageBox.Show("No image loaded!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Reset to 1:1 scale (100%)
            ResetImageTransform();
            Debug.WriteLine("[ImageZoom] View reset to 100% scale (1:1)");
        }

        #endregion
        
        #region Histogram Stretch
        
        /// <summary>
        /// Auto-detect histogram range from image data using current profile's percentiles
        /// </summary>
        private void AutoDetectHistogramRange(CameraImageData imageData)
        {
            try
            {
                if (imageData == null || imageData.ImageData == null)
                    return;

                // Store for percentile calculation
                _originalImageData = imageData.ImageData;
                _originalImageWidth = imageData.Width;
                _originalImageHeight = imageData.Height;
                _isRAW16 = (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16);

                // If profile is Auto, use auto-detect percentiles
                // Otherwise use the current profile's percentiles
                var (blackPct, whitePct) = HistogramStretchProfile.GetPresetPercentiles(_currentProfileType);
                
                if (_isRAW16)
                {
                    // Calculate percentile-based values
                    var (minValue, maxValue) = CalculatePercentilesFromHistogram(blackPct, whitePct);
                    
                    Debug.WriteLine($"[CameraSettings] Auto-detected range using {_currentProfileType} profile:");
                    Debug.WriteLine($"[CameraSettings]   Percentiles: Black={blackPct:F1}%, White={whitePct:F1}%");
                    Debug.WriteLine($"[CameraSettings]   Values: Min={minValue}, Max={maxValue}");
                    
                    // Update UI
                    Dispatcher.Invoke(() =>
                    {
                        _isUpdatingUI = true;
                        _histogramMin = minValue;
                        _histogramMax = maxValue;
                        sliderHistogramMin.Value = _histogramMin;
                        sliderHistogramMax.Value = _histogramMax;
                        txtHistogramMin.Text = _histogramMin.ToString();
                        txtHistogramMax.Text = _histogramMax.ToString();
                        UpdateProfileInfo();
                        _isUpdatingUI = false;
                    });
                }
                else
                {
                    // 8-bit fallback - find actual min/max
                    ushort min = ushort.MaxValue;
                    ushort max = ushort.MinValue;
                    
                    foreach (byte value in imageData.ImageData)
                    {
                        ushort value16 = (ushort)(value * 256);
                        if (value16 < min) min = value16;
                        if (value16 > max) max = value16;
                    }
                    
                    Debug.WriteLine($"[CameraSettings] Auto-detected range (8-bit): {min} - {max}");
                    
                    Dispatcher.Invoke(() =>
                    {
                        _isUpdatingUI = true;
                        _histogramMin = min;
                        _histogramMax = max;
                        sliderHistogramMin.Value = _histogramMin;
                        sliderHistogramMax.Value = _histogramMax;
                        txtHistogramMin.Text = _histogramMin.ToString();
                        txtHistogramMax.Text = _histogramMax.ToString();
                        _isUpdatingUI = false;
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] AutoDetectHistogramRange error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Histogram slider changed - update display
        /// </summary>
        private void SliderHistogram_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUI) return;
            
            // Check if UI controls are initialized
            if (sliderHistogramMin == null || sliderHistogramMax == null || 
                txtHistogramMin == null || txtHistogramMax == null)
                return;
            
            try
            {
                _histogramMin = (int)sliderHistogramMin.Value;
                _histogramMax = (int)sliderHistogramMax.Value;

                EnsureMinimumHistogramWindow(sender);
                
                // Ensure min < max
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
                
                txtHistogramMin.Text = _histogramMin.ToString();
                txtHistogramMax.Text = _histogramMax.ToString();
                
                // Switch to Manual mode when sliders are manually adjusted
                if (_currentProfileType != StretchProfileType.Manual && _originalImageData != null)
                {
                    _lastNonManualProfileType = _currentProfileType;
                    _isUpdatingUI = true;
                    _currentProfileType = StretchProfileType.Manual;
                    ExpandManualWindowIfNeeded();
                    SelectProfileInComboBox(StretchProfileType.Manual);
                    UpdateProfileInfo();
                    _isUpdatingUI = false;
                }
                
                // Re-stretch image
                if (_originalImageData != null)
                {
                    RestretchImage();
                }
                
                // [NEW] Save stretch parameters to CameraImageData
                SaveStretchParametersToCameraImageData();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] SliderHistogram_ValueChanged error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Save current stretch parameters to CameraImageData and notify LiveCalibrationImageControl
        /// </summary>
        private void SaveStretchParametersToCameraImageData()
        {
            if (_currentCameraImageData != null)
            {
                _currentCameraImageData.StretchMin = _histogramMin;
                _currentCameraImageData.StretchMax = _histogramMax;
                _currentCameraImageData.StretchProfileIndex = cmbStretchProfile?.SelectedIndex ?? 0;
                _currentCameraImageData.HasCustomStretch = true;
                
                Debug.WriteLine($"[CameraSettings] Saved stretch to CameraImageData: Min={_histogramMin}, Max={_histogramMax}, Profile={_currentCameraImageData.StretchProfileIndex}");
                
                // [NEW] Notify LiveCalibrationImageControl to re-render the selected image
                NotifyLiveCalibrationToRerender();
            }
        }
        
        /// <summary>
        /// Notify LiveCalibrationImageControl to re-render the currently selected image
        /// </summary>
        private void NotifyLiveCalibrationToRerender()
        {
            if (_liveCalibrationControl == null || _currentCameraImageData == null)
                return;
            
            try
            {
                // Get the selected point number
                int selectedPoint = _liveCalibrationControl.SelectedPointNumber;
                if (selectedPoint <= 0)
                    return;
                
                // Update histogram stretch in LiveCalibrationImageControl
                _liveCalibrationControl.UpdateHistogramStretch(_histogramMin, _histogramMax);
                
                // Re-render the selected image with new stretch
                _liveCalibrationControl.RerenderSelectedImage(_currentCameraImageData);
                
                Debug.WriteLine($"[CameraSettings] Notified LiveCalibration to re-render point {selectedPoint}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] NotifyLiveCalibrationToRerender error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Profile ComboBox selection changed - apply preset percentiles
        /// </summary>
        private void CmbStretchProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            
            try
            {
                var item = cmbStretchProfile.SelectedItem as ComboBoxItem;
                if (item?.Tag is string tag && int.TryParse(tag, out int profileValue))
                {
                    _currentProfileType = (StretchProfileType)profileValue;
                    if (_currentProfileType != StretchProfileType.Manual)
                        _lastNonManualProfileType = _currentProfileType;
                    Debug.WriteLine($"[CameraSettings] Profile changed to: {_currentProfileType}");
                    
                    UpdateProfileInfo();
                    
                    // If image is loaded, apply the profile stretch
                    if (_originalImageData != null && _currentProfileType != StretchProfileType.Manual)
                    {
                        ApplyProfileStretch();
                    }
                    
                    // [NEW] Save profile index to CameraImageData
                    SaveStretchParametersToCameraImageData();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] CmbStretchProfile_SelectionChanged error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update the profile info text showing current percentile settings
        /// </summary>
        private void UpdateProfileInfo()
        {
            if (txtProfileInfo == null) return;
            
            try
            {
                var (blackPct, whitePct) = HistogramStretchProfile.GetPresetPercentiles(_currentProfileType);
                double toneStrength = GetActiveToneStrength();
                
                if (_currentProfileType == StretchProfileType.Manual)
                {
                    txtProfileInfo.Text = $"Manual: Black={_histogramMin}, White={_histogramMax} | Tone={toneStrength:F1}";
                }
                else
                {
                    txtProfileInfo.Text = $"Percentiles: Black={blackPct:F2}%, White={whitePct:F2}% | Tone={toneStrength:F1}";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] UpdateProfileInfo error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Select a profile in the ComboBox by its StretchProfileType value
        /// </summary>
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
        
        
        /// <summary>
        /// Apply histogram stretch based on current profile's percentiles
        /// </summary>
        private void ApplyProfileStretch()
        {
            if (_originalImageData == null || !_isRAW16) return;
            
            try
            {
                // Get percentiles for current profile
                var (blackPct, whitePct) = HistogramStretchProfile.GetPresetPercentiles(_currentProfileType);
                double toneStrength = GetActiveToneStrength();
                
                Debug.WriteLine($"[CameraSettings] Applying profile stretch: {_currentProfileType}");
                Debug.WriteLine($"[CameraSettings]   Black percentile: {blackPct:F1}%");
                Debug.WriteLine($"[CameraSettings]   White percentile: {whitePct:F1}%");
                Debug.WriteLine($"[CameraSettings]   Tone strength: {toneStrength:F1}");
                
                // Build histogram to find percentile values
                var (minValue, maxValue) = CalculatePercentilesFromHistogram(blackPct, whitePct);
                
                Debug.WriteLine($"[CameraSettings]   Calculated Min: {minValue}, Max: {maxValue}");
                
                // Update sliders and re-stretch
                _isUpdatingUI = true;
                _histogramMin = minValue;
                _histogramMax = maxValue;
                sliderHistogramMin.Value = _histogramMin;
                sliderHistogramMax.Value = _histogramMax;
                txtHistogramMin.Text = _histogramMin.ToString();
                txtHistogramMax.Text = _histogramMax.ToString();
                UpdateProfileInfo();
                _isUpdatingUI = false;
                
                // Re-stretch with new values
                RestretchImage();
                
                // [NEW] Save stretch parameters to CameraImageData (auto-calculated values)
                SaveStretchParametersToCameraImageData();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] ApplyProfileStretch error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Calculate min/max values from histogram percentiles
        /// </summary>
        private (int minValue, int maxValue) CalculatePercentilesFromHistogram(double blackPercentile, double whitePercentile)
        {
            if (_originalImageData == null || !_isRAW16)
                return (0, 65535);
            
            try
            {
                int pixelCount = _originalImageWidth * _originalImageHeight;
                
                // Build histogram with more resolution than 256 bins so the
                // stretch behaves more like Maxim DL and is less blocky.
                const int histogramBins = 4096;
                const int binShift = 4; // 16-bit -> 12-bit
                int[] histogram = new int[histogramBins];
                
                for (int i = 0; i < pixelCount; i++)
                {
                    ushort value = BitConverter.ToUInt16(_originalImageData, i * 2);
                    int bin = value >> binShift; // Map 16-bit to 12-bit (0-4095)
                    histogram[bin]++;
                }
                
                // Calculate cumulative histogram
                long[] cumulative = new long[histogramBins];
                cumulative[0] = histogram[0];
                for (int i = 1; i < histogramBins; i++)
                {
                    cumulative[i] = cumulative[i - 1] + histogram[i];
                }
                
                // Find black point (lower percentile)
                long blackThreshold = (long)(pixelCount * blackPercentile / 100.0);
                int blackBin = 0;
                for (int i = 0; i < histogramBins; i++)
                {
                    if (cumulative[i] >= blackThreshold)
                    {
                        blackBin = i;
                        break;
                    }
                }
                
                // Find white point (upper percentile)
                long whiteThreshold = (long)(pixelCount * whitePercentile / 100.0);
                int whiteBin = histogramBins - 1;
                for (int i = 0; i < histogramBins; i++)
                {
                    if (cumulative[i] >= whiteThreshold)
                    {
                        whiteBin = i;
                        break;
                    }
                }
                
                // Convert bins back to 16-bit values
                int minValue = blackBin << binShift;
                int maxValue = Math.Min(65535, (whiteBin << binShift) + ((1 << binShift) - 1));
                
                // Ensure valid range
                if (minValue >= maxValue)
                {
                    minValue = 0;
                    maxValue = 65535;
                }
                
                return (minValue, maxValue);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] CalculatePercentilesFromHistogram error: {ex.Message}");
                return (0, 65535);
            }
        }

        
        /// <summary>
        /// Apply button clicked - apply stretch using current profile
        /// </summary>
        private void BtnAutoStretch_Click(object sender, RoutedEventArgs e)
        {
            if (_originalImageData == null)
            {
                MessageBox.Show("No image loaded!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Apply stretch based on current profile
            if (_currentProfileType == StretchProfileType.Manual)
            {
                // For Manual mode, just re-stretch with current values
                RestretchImage();
            }
            else
            {
                // For preset profiles, recalculate from percentiles
                ApplyProfileStretch();
            }
            
            // [NEW] Save stretch parameters to CameraImageData
            SaveStretchParametersToCameraImageData();
        }
        
        /// <summary>
        /// Re-stretch image with current histogram settings
        /// </summary>
        private void RestretchImage()
        {
            try
            {
            if (_originalImageData == null || _originalImageWidth == 0 || _originalImageHeight == 0)
                    return;
                
                byte[] displayData = new byte[_originalImageWidth * _originalImageHeight];
                double scale = 255.0 / (_histogramMax - _histogramMin);
                double toneStrength = GetActiveToneStrength();
                
                if (_isRAW16)
                {
                    // 16-bit → 8-bit with custom stretch
                    int pixelCount = _originalImageWidth * _originalImageHeight;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        ushort value = BitConverter.ToUInt16(_originalImageData, i * 2);
                        
                        // Clip to histogram range
                        if (value <= _histogramMin)
                            displayData[i] = 0;
                        else if (value >= _histogramMax)
                            displayData[i] = 255;
                        else
                        {
                            double normalized = (value - _histogramMin) * scale / 255.0;
                            displayData[i] = (byte)Math.Round(ApplyProfileToneCurve(normalized, toneStrength) * 255.0);
                        }
                    }
                }
                else
                {
                    // 8-bit with custom stretch
                    for (int i = 0; i < displayData.Length; i++)
                    {
                        int value = _originalImageData[i] * 256; // Scale to 16-bit range
                        
                        if (value <= _histogramMin)
                            displayData[i] = 0;
                        else if (value >= _histogramMax)
                            displayData[i] = 255;
                        else
                        {
                            double normalized = (value - _histogramMin) * scale / 255.0;
                            displayData[i] = (byte)Math.Round(ApplyProfileToneCurve(normalized, toneStrength) * 255.0);
                        }
                    }
                }
                
                var bitmap = BitmapSource.Create(
                    _originalImageWidth,
                    _originalImageHeight,
                    96, 96,
                    PixelFormats.Gray8,
                    null,
                    displayData,
                    _originalImageWidth);
                
                bitmap.Freeze();
                
                // Update display
                _originalImage = bitmap;
                
                // [CHANGED] Auto-fit to window on first load
                if (_isFirstLoad)
                {
                    _isFirstLoad = false;
                    FitImageToWindow();
                }
                else
                {
                    ProcessAndDisplayImage();
                }
                
                // Fire histogram changed event to sync with LiveCalibrationImageControl
                HistogramStretchChanged?.Invoke(this, new HistogramChangedEventArgs
                {
                    MinValue = _histogramMin,
                    MaxValue = _histogramMax,
                    ProfileType = _currentProfileType
                });
                
                // Also directly update LiveCalibrationImageControl if connected
                _liveCalibrationControl?.UpdateHistogramStretch(_histogramMin, _histogramMax);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraSettings] RestretchImage error: {ex.Message}");
            }
        }

        private static double ApplyProfileToneCurve(double normalized, double toneStrength)
        {
            normalized = Math.Clamp(normalized, 0.0, 1.0);

            if (toneStrength <= 1.05)
                return normalized;

            // Asinh stretch gives a much more astronomy-friendly preview than
            // linear scaling alone because it opens shadow detail while keeping
            // bright stars from instantly blowing out.
            double stretched = Math.Asinh(normalized * toneStrength) / Math.Asinh(toneStrength);
            return Math.Clamp(stretched, 0.0, 1.0);
        }

        private void EnsureMinimumHistogramWindow(object sender)
        {
            int minWindow = GetMinimumManualWindowSize();
            int currentWindow = _histogramMax - _histogramMin;

            if (currentWindow >= minWindow)
                return;

            if (sender == sliderHistogramMin)
            {
                _histogramMin = Math.Max(0, _histogramMax - minWindow);
            }
            else if (sender == sliderHistogramMax)
            {
                _histogramMax = Math.Min(65535, _histogramMin + minWindow);
                if (_histogramMax - _histogramMin < minWindow)
                    _histogramMin = Math.Max(0, _histogramMax - minWindow);
            }
            else
            {
                int midpoint = (_histogramMin + _histogramMax) / 2;
                _histogramMin = Math.Max(0, midpoint - (minWindow / 2));
                _histogramMax = Math.Min(65535, _histogramMin + minWindow);
                if (_histogramMax - _histogramMin < minWindow)
                    _histogramMin = Math.Max(0, _histogramMax - minWindow);
            }

            _isUpdatingUI = true;
            sliderHistogramMin.Value = _histogramMin;
            sliderHistogramMax.Value = _histogramMax;
            _isUpdatingUI = false;
        }

        private void ExpandManualWindowIfNeeded()
        {
            int minWindow = GetMinimumManualWindowSize();
            if ((_histogramMax - _histogramMin) >= minWindow)
                return;

            int midpoint = (_histogramMin + _histogramMax) / 2;
            _histogramMin = Math.Max(0, midpoint - (minWindow / 2));
            _histogramMax = Math.Min(65535, _histogramMin + minWindow);
            if ((_histogramMax - _histogramMin) < minWindow)
                _histogramMin = Math.Max(0, _histogramMax - minWindow);

            sliderHistogramMin.Value = _histogramMin;
            sliderHistogramMax.Value = _histogramMax;
            txtHistogramMin.Text = _histogramMin.ToString();
            txtHistogramMax.Text = _histogramMax.ToString();
        }

        private int GetMinimumManualWindowSize()
        {
            if (!_isRAW16)
                return 256;

            // Prevent the manual sliders from collapsing into a tiny 10-20 ADU window
            // which makes the preview blow out instantly when the user nudges a slider.
            return 1024;
        }

        private double GetActiveToneStrength()
        {
            if (_currentProfileType == StretchProfileType.Manual)
                return HistogramStretchProfile.GetPresetToneStrength(_lastNonManualProfileType);

            return HistogramStretchProfile.GetPresetToneStrength(_currentProfileType);
        }
        
        #endregion
    }
    
    /// <summary>
    /// Event args for histogram stretch changes
    /// </summary>
    public class HistogramChangedEventArgs : EventArgs
    {
        public int MinValue { get; set; }
        public int MaxValue { get; set; }
        public StretchProfileType ProfileType { get; set; }
    }
}
