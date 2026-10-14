using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Camera;

namespace ForkMount_WPF.UI
{
    public partial class LiveCalibrationImageControl : UserControl
    {
        private int _imageCount = 0;
        
        // [MEMORY FIX] Maximum number of images to keep in memory
        // Older images are evicted to prevent OutOfMemoryException on large calibration runs (100+ points)
        // Each RAW16 image is ~12.8MB, so 20 images = ~256MB max
        private const int MAX_STORED_IMAGES = 20;
        
        // [NEW] Track containers by point number to enable updates
        private Dictionary<int, Border> _pointContainers = new Dictionary<int, Border>();
        
        // [NEW] Store CameraImageData per point for sharing with CameraSettingsWindow
        private Dictionary<int, CameraImageData> _pointImageData = new Dictionary<int, CameraImageData>();
        
        // Histogram stretch parameters (synced from CameraSettingsWindow)
        private int _histogramMin = 0;
        private int _histogramMax = 65535;
        
        // Store last image data for re-stretching
        private byte[] _lastImageData;
        private int _lastImageWidth;
        private int _lastImageHeight;
        private bool _lastImageIsRAW16 = true;
        
        // Currently selected point number
        private int _selectedPointNumber = -1;
        
        // [NEW] Latest point number (for "Switch to Latest" feature)
        private int _latestPointNumber = -1;
        
        // [FIX] Debounce RestretchSelectedImageFromData to prevent UI thread stalling
        // Multiple callers (SelectImage, RerenderSelectedImage, ApplyAutoStretch) can trigger
        // 5-6 rapid re-renders of the same 25M-pixel image, blocking the dispatcher and causing
        // the sidereal tracking loop to miss cycles (max dt 97ms ? overshoot)
        private int _pendingRestretchPoint = -1;
        private DateTime _lastRestretchTime = DateTime.MinValue;
        private const int RESTRETCH_DEBOUNCE_MS = 100;
        private bool _isUpdatingStretchControls;
        private const int MIN_STRETCH_WINDOW = 64;
        
        // [NEW] Event to notify when an image is selected (for CameraSettingsWindow sync)
        public event EventHandler<ImageSelectedEventArgs> ImageSelected;
        
        // [NEW] Event to notify when a new image is added
        public event EventHandler<NewImageAddedEventArgs> NewImageAdded;

        public LiveCalibrationImageControl()
        {
            InitializeComponent();
             
            // Load Histogram Stretch Profile from AppSettings
            LoadHistogramStretchFromSettings();
            SetStretchControlsEnabled(false);
            UpdateStretchValueLabels(_histogramMin, _histogramMax);
        }
        
        /// <summary>
        /// Load Histogram Stretch Profile from AppSettings (shared with CameraSettingsWindow)
        /// </summary>
        public void LoadHistogramStretchFromSettings()
        {
            try
            {
                var profile = AppSettings.GetHistogramStretchProfile();
                if (profile != null)
                {
                    _histogramMin = profile.MinValue;
                    _histogramMax = profile.MaxValue;
                    
                    System.Diagnostics.Debug.WriteLine($"[LiveCalibrationImage] ?? Histogram loaded from AppSettings: Min={_histogramMin}, Max={_histogramMax}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibrationImage] ?? Failed to load histogram settings: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Refresh histogram settings from AppSettings (call when CameraSettings changes)
        /// </summary>
        public void RefreshHistogramSettings()
        {
            LoadHistogramStretchFromSettings();
            
            // Re-stretch all images if needed
            if (_lastImageData != null)
            {
                RestretchLastImage();
            }
        }
        
        /// <summary>
        /// Update histogram stretch values and re-stretch current image
        /// Called from CameraSettingsWindow when histogram is changed
        /// </summary>
        public void UpdateHistogramStretch(int min, int max)
        {
            _histogramMin = min;
            _histogramMax = max;
            
            // Re-stretch the currently displayed/selected image
            if (_lastImageData != null)
            {
                RestretchLastImage();
            }
        }
        
        /// <summary>
        /// Re-render the selected image with new stretch parameters from CameraImageData
        /// Called by CameraSettingsWindow when user adjusts histogram
        /// </summary>
        public void RerenderSelectedImage(CameraImageData imageData)
        {
            if (imageData == null || imageData.ImageData == null)
                return;
            
            // [CRITICAL FIX] Find the point number from the imageData, not use _selectedPointNumber
            // This ensures we update the correct point even if selection is out of sync
            int targetPointNumber = -1;
            foreach (var kvp in _pointImageData)
            {
                // Check if this is the same CameraImageData object (by reference)
                if (ReferenceEquals(kvp.Value, imageData))
                {
                    targetPointNumber = kvp.Key;
                    break;
                }
            }
            
            if (targetPointNumber <= 0)
            {
                // Fallback: use _selectedPointNumber if we can't find by reference
                targetPointNumber = _selectedPointNumber;
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RerenderSelectedImage: Using fallback _selectedPointNumber={targetPointNumber}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RerenderSelectedImage: Found target point {targetPointNumber} by reference");
            }
            
            if (targetPointNumber <= 0)
                return;
            
            try
            {
                // Update local stretch parameters
                _histogramMin = imageData.StretchMin;
                _histogramMax = imageData.StretchMax;
                
                // [CRITICAL FIX] Update the CameraImageData in dictionary with new stretch values
                if (_pointImageData.ContainsKey(targetPointNumber))
                {
                    _pointImageData[targetPointNumber].StretchMin = imageData.StretchMin;
                    _pointImageData[targetPointNumber].StretchMax = imageData.StretchMax;
                    _pointImageData[targetPointNumber].StretchProfileIndex = imageData.StretchProfileIndex;
                    _pointImageData[targetPointNumber].HasCustomStretch = imageData.HasCustomStretch;
                }
                
                // Re-stretch and update the displayed image
                RestretchSelectedImageFromData(targetPointNumber, imageData);
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Re-rendered point {targetPointNumber} with stretch Min={_histogramMin}, Max={_histogramMax}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RerenderSelectedImage error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Re-stretch and update the image for a specific point using its CameraImageData
        /// </summary>
        private async void RestretchSelectedImageFromData(int pointNumber, CameraImageData imageData)
        {
            if (!_pointContainers.TryGetValue(pointNumber, out var container))
                return;
            
            if (imageData == null || imageData.ImageData == null)
                return;
            
            // [FIX] Debounce: skip if same point was just re-stretched within RESTRETCH_DEBOUNCE_MS
            // This prevents 5-6 rapid re-renders that block the UI dispatcher and cause
            // sidereal tracking loop gaps (97ms ? overshoot)
            var now = DateTime.UtcNow;
            if (pointNumber == _pendingRestretchPoint && 
                (now - _lastRestretchTime).TotalMilliseconds < RESTRETCH_DEBOUNCE_MS)
            {
                return; // Skip — a recent render for this point is already done/queued
            }
            _pendingRestretchPoint = pointNumber;
            _lastRestretchTime = now;
            
            try
            {
                byte[] rawData = imageData.ImageData;
                int width = imageData.Width;
                int height = imageData.Height;
                bool isRAW16 = imageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;
                
                int stretchMin = imageData.StretchMin;
                int stretchMax = imageData.StretchMax;
                
                // Heavy pixel processing on background thread to avoid UI stutter
                var bitmap = await Task.Run(() =>
                {
                    byte[] displayData = new byte[width * height];
                    double scale = 255.0 / Math.Max(1, stretchMax - stretchMin);
                    
                    if (isRAW16)
                    {
                        int pixelCount = width * height;
                        for (int i = 0; i < pixelCount; i++)
                        {
                            ushort value = BitConverter.ToUInt16(rawData, i * 2);
                            double stretched = (value - stretchMin) * scale;
                            displayData[i] = (byte)Math.Max(0, Math.Min(255, stretched));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < rawData.Length && i < displayData.Length; i++)
                        {
                            double stretched = (rawData[i] - stretchMin) * scale;
                            displayData[i] = (byte)Math.Max(0, Math.Min(255, stretched));
                        }
                    }
                    
                    var bmp = BitmapSource.Create(
                        width, height,
                        96, 96,
                        System.Windows.Media.PixelFormats.Gray8,
                        null,
                        displayData,
                        width);
                    
                    bmp.Freeze(); // Freeze to allow cross-thread access
                    return bmp;
                });
                
                // Back on UI thread — only update the Image control
                UpdateImageInContainer(container, bitmap);
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RestretchSelectedImageFromData completed for point {pointNumber}, Stretch={stretchMin}-{stretchMax}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RestretchSelectedImageFromData error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Re-stretch and update the image for a specific point (legacy - uses _lastImageData)
        /// </summary>
        private async void RestretchSelectedImage(int pointNumber)
        {
            if (!_pointContainers.TryGetValue(pointNumber, out var container))
                return;
            
            if (_lastImageData == null || _lastImageWidth == 0 || _lastImageHeight == 0)
                return;
            
            try
            {
                // Capture locals for background thread
                byte[] rawData = _lastImageData;
                int width = _lastImageWidth;
                int height = _lastImageHeight;
                bool isRAW16 = _lastImageIsRAW16;
                int stretchMin = _histogramMin;
                int stretchMax = _histogramMax;
                
                // Heavy pixel processing on background thread
                var bitmap = await Task.Run(() =>
                {
                    byte[] displayData = new byte[width * height];
                    double scale = 255.0 / Math.Max(1, stretchMax - stretchMin);
                    
                    if (isRAW16)
                    {
                        int pixelCount = width * height;
                        for (int i = 0; i < pixelCount; i++)
                        {
                            ushort value = BitConverter.ToUInt16(rawData, i * 2);
                            double stretched = (value - stretchMin) * scale;
                            displayData[i] = (byte)Math.Max(0, Math.Min(255, stretched));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < rawData.Length && i < displayData.Length; i++)
                        {
                            double stretched = (rawData[i] - stretchMin) * scale;
                            displayData[i] = (byte)Math.Max(0, Math.Min(255, stretched));
                        }
                    }
                    
                    var bmp = BitmapSource.Create(
                        width, height,
                        96, 96,
                        System.Windows.Media.PixelFormats.Gray8,
                        null,
                        displayData,
                        width);
                    
                    bmp.Freeze();
                    return bmp;
                });
                
                // Back on UI thread
                UpdateImageInContainer(container, bitmap);
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RestretchSelectedImage completed for point {pointNumber}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RestretchSelectedImage error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update the image in a specific container
        /// </summary>
        private void UpdateImageInContainer(Border container, BitmapSource newBitmap)
        {
            try
            {
                if (container?.Child is StackPanel stack && stack.Children.Count > 0)
                {
                    var imageGrid = stack.Children[0] as Grid;
                    if (imageGrid != null && imageGrid.Children.Count > 0)
                    {
                        var imageBorder = imageGrid.Children[0] as Border;
                        if (imageBorder?.Child is Grid previewGrid)
                        {
                            var imageControl = previewGrid.Children.OfType<Image>().FirstOrDefault();
                            if (imageControl == null)
                            {
                                imageControl = new Image
                                {
                                    Stretch = Stretch.Uniform,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                previewGrid.Children.Insert(0, imageControl);
                            }

                            imageControl.Source = newBitmap;

                            foreach (var placeholder in previewGrid.Children.OfType<StackPanel>())
                            {
                                placeholder.Visibility = Visibility.Collapsed;
                            }

                            if (_selectedPointNumber > 0 &&
                                _pointContainers.TryGetValue(_selectedPointNumber, out var selectedContainer) &&
                                ReferenceEquals(selectedContainer, container))
                            {
                                UpdateSelectedPreview(newBitmap, _selectedPointNumber);
                            }

                            System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Image updated in container");
                            return;
                        }
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] WARNING: Could not find Image control in container");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] UpdateImageInContainer error: {ex.Message}");
            }
        }

        private void UpdateSelectedPreview(BitmapSource bitmap, int pointNumber)
        {
            if (selectedPreviewImage == null || selectedPreviewPlaceholder == null || txtPreviewMeta == null)
                return;

            selectedPreviewImage.Source = bitmap;
            selectedPreviewPlaceholder.Visibility = bitmap != null ? Visibility.Collapsed : Visibility.Visible;
            txtPreviewMeta.Text = pointNumber > 0 ? $"Selected: Point {pointNumber}" : "No image selected";
        }

        private void SetStretchControlsEnabled(bool enabled)
        {
            if (sliderStretchBlack != null) sliderStretchBlack.IsEnabled = enabled;
            if (sliderStretchWhite != null) sliderStretchWhite.IsEnabled = enabled;
            if (btnResetStretch != null) btnResetStretch.IsEnabled = enabled;
        }

        private void UpdateStretchValueLabels(int min, int max)
        {
            if (txtStretchBlackValue != null) txtStretchBlackValue.Text = min.ToString();
            if (txtStretchWhiteValue != null) txtStretchWhiteValue.Text = max.ToString();
        }

        private void SyncStretchControlsFromImage(CameraImageData imageData)
        {
            _isUpdatingStretchControls = true;
            try
            {
                if (imageData == null)
                {
                    SetStretchControlsEnabled(false);
                    if (sliderStretchBlack != null) sliderStretchBlack.Value = 0;
                    if (sliderStretchWhite != null) sliderStretchWhite.Value = 65535;
                    UpdateStretchValueLabels(0, 65535);
                    return;
                }

                SetStretchControlsEnabled(true);

                int stretchMin = Math.Max(0, Math.Min(65535, imageData.StretchMin));
                int stretchMax = Math.Max(stretchMin + MIN_STRETCH_WINDOW, Math.Min(65535, imageData.StretchMax));
                if (stretchMax > 65535)
                {
                    stretchMax = 65535;
                    stretchMin = Math.Max(0, stretchMax - MIN_STRETCH_WINDOW);
                }

                imageData.StretchMin = stretchMin;
                imageData.StretchMax = stretchMax;

                if (sliderStretchBlack != null) sliderStretchBlack.Value = stretchMin;
                if (sliderStretchWhite != null) sliderStretchWhite.Value = stretchMax;
                UpdateStretchValueLabels(stretchMin, stretchMax);
            }
            finally
            {
                _isUpdatingStretchControls = false;
            }
        }

        private void UpdateSelectedPreviewFromContainer(int pointNumber)
        {
            if (!_pointContainers.TryGetValue(pointNumber, out var container))
                return;

            BitmapSource bitmap = null;
            if (container.Child is StackPanel stack && stack.Children.Count > 0 &&
                stack.Children[0] is Grid imageGrid && imageGrid.Children.Count > 0 &&
                imageGrid.Children[0] is Border imageBorder && imageBorder.Child is Grid previewGrid)
            {
                bitmap = previewGrid.Children.OfType<Image>().FirstOrDefault()?.Source as BitmapSource;
            }

            UpdateSelectedPreview(bitmap, pointNumber);
        }
        
        /// <summary>
        /// Get current selected point number
        /// </summary>
        public int SelectedPointNumber => _selectedPointNumber;
        
        /// <summary>
        /// Get latest point number
        /// </summary>
        public int LatestPointNumber => _latestPointNumber;
        
        /// <summary>
        /// Get raw image data for selected point (for CameraSettingsWindow)
        /// </summary>
        public (byte[] data, int width, int height, bool isRAW16) GetSelectedImageData()
        {
            return (_lastImageData, _lastImageWidth, _lastImageHeight, _lastImageIsRAW16);
        }
        
        /// <summary>
        /// Get CameraImageData for a specific point
        /// </summary>
        public CameraImageData GetCameraImageDataForPoint(int pointNumber)
        {
            if (_pointImageData.TryGetValue(pointNumber, out var imageData))
            {
                return imageData;
            }
            return null;
        }
        
        /// <summary>
        /// Get CameraImageData for currently selected point
        /// </summary>
        public CameraImageData GetSelectedCameraImageData()
        {
            if (_selectedPointNumber > 0)
            {
                return GetCameraImageDataForPoint(_selectedPointNumber);
            }
            return null;
        }
        
        /// <summary>
        /// Get CameraImageData for latest point
        /// </summary>
        public CameraImageData GetLatestCameraImageData()
        {
            if (_latestPointNumber > 0)
            {
                return GetCameraImageDataForPoint(_latestPointNumber);
            }
            return null;
        }
        
        /// <summary>
        /// Store CameraImageData for a point
        /// [FIXED] Initialize default stretch values if not set
        /// [MEMORY FIX] Evict old images to prevent memory exhaustion
        /// </summary>
        public void StoreCameraImageData(int pointNumber, CameraImageData imageData)
        {
            _pointImageData[pointNumber] = imageData;
            _latestPointNumber = pointNumber;
            
            // [NEW] Initialize default stretch values if not already set
            if (imageData != null && !imageData.HasCustomStretch)
            {
                // Use current histogram settings as default (will be overwritten by auto-stretch or user)
                imageData.StretchMin = _histogramMin;
                imageData.StretchMax = _histogramMax;
                imageData.HasCustomStretch = false;  // Mark as not custom yet
            }
            
            // Update last image data for backward compatibility
            if (imageData != null && imageData.ImageData != null)
            {
                _lastImageData = imageData.ImageData;
                _lastImageWidth = imageData.Width;
                _lastImageHeight = imageData.Height;
                _lastImageIsRAW16 = imageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;
            }
            
            // [MEMORY FIX] Evict oldest images when exceeding the limit
            EvictOldImages();
            
            System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Stored CameraImageData for point {pointNumber} (total stored: {_pointImageData.Count}), Stretch={imageData?.StretchMin}-{imageData?.StretchMax}");
        }
        
        /// <summary>
        /// [MEMORY FIX] Evict oldest images beyond MAX_STORED_IMAGES limit.
        /// Releases raw byte[] data from CameraImageData to free memory.
        /// Each RAW16 image is ~12.8MB — after 100+ points this causes OutOfMemoryException.
        /// </summary>
        private void EvictOldImages()
        {
            if (_pointImageData.Count <= MAX_STORED_IMAGES)
                return;
            
            // Find the oldest point numbers to evict (keep the latest MAX_STORED_IMAGES)
            var sortedKeys = _pointImageData.Keys.OrderBy(k => k).ToList();
            int countToEvict = sortedKeys.Count - MAX_STORED_IMAGES;
            
            for (int i = 0; i < countToEvict; i++)
            {
                int evictKey = sortedKeys[i];
                
                // Don't evict currently selected point
                if (evictKey == _selectedPointNumber)
                    continue;
                
                // Release raw image data (the big byte[] array) from the CameraImageData
                if (_pointImageData.TryGetValue(evictKey, out var oldData) && oldData != null)
                {
                    oldData.ImageData = null;  // Release the ~12.8MB byte array
                }
                _pointImageData.Remove(evictKey);
                
                // Also remove UI container to reduce visual tree memory
                if (_pointContainers.TryGetValue(evictKey, out var container))
                {
                    imageStackPanel.Children.Remove(container);
                    _pointContainers.Remove(evictKey);
                }
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] ??? Evicted old image data for point {evictKey} (memory management)");
            }
            
            _imageCount = _pointContainers.Count;
        }
        
        /// <summary>
        /// Get raw image data for a specific point
        /// </summary>
        public (byte[] data, int width, int height, bool isRAW16) GetImageDataForPoint(int pointNumber)
        {
            if (_pointImageData.TryGetValue(pointNumber, out var imageData))
            {
                return (imageData.ImageData, imageData.Width, imageData.Height, 
                    imageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16);
            }
            return (null, 0, 0, false);
        }


        /// <summary>
        /// Add a new calibration image to the top of the stack
        /// </summary>
        public void AddCalibrationImage(int pointNumber, int totalPoints, double azimuth, double altitude, BitmapSource image = null, int starsDetected = 0, double rmsError = 0.0, string statusText = "")
        {
            // [CRITICAL] If container already exists for this point, UPDATE it instead of adding new one!
            if (_pointContainers.ContainsKey(pointNumber))
            {
                UpdateCalibrationImage(pointNumber, image, starsDetected, rmsError, statusText);
                return;
            }
            
            _imageCount++;

            // Hide placeholder on first image
            if (noImagesPlaceholder != null && noImagesPlaceholder.Visibility == Visibility.Visible)
            {
                noImagesPlaceholder.Visibility = Visibility.Collapsed;
            }

            // Create thumbnail container for the bottom filmstrip
            var container = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4B5563")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 0),
                Width = 190,
                Height = 118,
                Tag = pointNumber,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            
            // Add click handler for image selection
            container.MouseLeftButtonDown += Container_MouseLeftButtonDown;

            var stack = new StackPanel();
            var imageGrid = new Grid();
            
            var imageBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1F2937")),
                Margin = new Thickness(8, 8, 8, 0),
                Height = 72,
                CornerRadius = new CornerRadius(4)
            };

            var previewGrid = new Grid();

            if (image != null)
            {
                var imageControl = new Image
                {
                    Source = image,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                previewGrid.Children.Add(imageControl);
            }
            else
            {
                var placeholderStack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var cameraIcon = new TextBlock
                {
                    Text = "[CAMERA]",
                    FontSize = 48,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 10)
                };

                var placeholderText = new TextBlock
                {
                    Text = $"Image placeholder #{_imageCount}",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280")),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var timestampText = new TextBlock
                {
                    Text = DateTime.Now.ToString("HH:mm:ss"),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 0)
                };

                placeholderStack.Children.Add(cameraIcon);
                placeholderStack.Children.Add(placeholderText);
                placeholderStack.Children.Add(timestampText);
                previewGrid.Children.Add(placeholderStack);
            }

            imageBorder.Child = previewGrid;
            imageGrid.Children.Add(imageBorder);
            
            // [NEW] Status overlay (Capturing, Processing, Completed, Failed)
            if (!string.IsNullOrEmpty(statusText))
            {
                var statusOverlay = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), // Semi-transparent black
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 4, 10, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                
                var statusTextBlock = new TextBlock
                {
                    Text = statusText,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = statusText.Contains("Completed") 
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))  // Green
                        : statusText.Contains("Failed")
                            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"))  // Red
                            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"))  // Orange (Processing/Capturing)
                };
                
                statusOverlay.Child = statusTextBlock;
                imageGrid.Children.Add(statusOverlay);
            }

            var titleText = new TextBlock
            {
                Text = $"Point {pointNumber}/{totalPoints}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 6, 8, 0)
            };

            string rmsText = rmsError > 0 ? $"  RMS {rmsError:F2}\"" : string.Empty;
            var metaText = new TextBlock
            {
                Text = $"Az {azimuth:F2}°  Alt {altitude:F2}°  Stars {starsDetected}{rmsText}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                FontSize = 9,
                Margin = new Thickness(8, 2, 8, 8),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            stack.Children.Add(imageGrid);
            stack.Children.Add(titleText);
            stack.Children.Add(metaText);
            container.Child = stack;

            int insertIndex = noImagesPlaceholder != null ? 1 : 0;
            imageStackPanel.Children.Insert(insertIndex, container);
            
            // Store container reference for updates
            _pointContainers[pointNumber] = container;

            imageScrollViewer.ScrollToRightEnd();
            
            // Update latest point number
            _latestPointNumber = pointNumber;
            
            // Fire NewImageAdded event (for CameraSettingsWindow to know about new images)
            var latestImageData = GetCameraImageDataForPoint(pointNumber);
            NewImageAdded?.Invoke(this, new NewImageAddedEventArgs
            {
                PointNumber = pointNumber,
                RawImageData = _lastImageData,
                Width = _lastImageWidth,
                Height = _lastImageHeight,
                IsRAW16 = _lastImageIsRAW16,
                CameraImageData = latestImageData  // Include full CameraImageData
            });

            System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Added image #{_imageCount} for point {pointNumber}/{totalPoints}, Status={statusText}");

            if (_selectedPointNumber <= 0)
            {
                SelectImage(pointNumber);
            }
        }
        
        /// <summary>
        /// Handle click on image container to select it
        /// </summary>
        private void Container_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border container && container.Tag is int pointNumber)
            {
                SelectImage(pointNumber);
            }
        }
        
        /// <summary>
        /// Select an image by point number and fire ImageSelected event
        /// </summary>
        public void SelectImage(int pointNumber)
        {
            // Validate point exists
            if (!_pointContainers.ContainsKey(pointNumber))
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] SelectImage: Point {pointNumber} not found in containers");
                return;
            }
            
            // Update selection visual
            foreach (var kvp in _pointContainers)
            {
                if (kvp.Value is Border border)
                {
                    border.BorderBrush = kvp.Key == pointNumber
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")) // Blue for selected
                        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4B5563")); // Gray for unselected
                    border.BorderThickness = kvp.Key == pointNumber
                        ? new Thickness(2)
                        : new Thickness(1);
                }
            }
            
            _selectedPointNumber = pointNumber;
            UpdateSelectedPreviewFromContainer(pointNumber);
            
            // [CRITICAL FIX] Get image data from _pointImageData dictionary, not _lastImageData
            var selectedImageData = GetCameraImageDataForPoint(pointNumber);
            if (selectedImageData != null && selectedImageData.ImageData != null)
            {
                // Update _lastImageData from the selected point's CameraImageData
                _lastImageData = selectedImageData.ImageData;
                _lastImageWidth = selectedImageData.Width;
                _lastImageHeight = selectedImageData.Height;
                _lastImageIsRAW16 = selectedImageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;
                
                // [REMOVED] Don't update local _histogramMin/_histogramMax here
                // Each image has its own stretch values stored in CameraImageData
                // The local values are only used for backward compatibility
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] SelectImage: Loaded data from point {pointNumber}, Size={_lastImageWidth}x{_lastImageHeight}, Stretch={selectedImageData.StretchMin}-{selectedImageData.StretchMax}");
                
                // [NEW] Re-render the selected image with its stored stretch values
                // This ensures the thumbnail always shows the correct stretch
                RestretchSelectedImageFromData(pointNumber, selectedImageData);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] SelectImage: No CameraImageData for point {pointNumber}");
            }

            SyncStretchControlsFromImage(selectedImageData);
             
            // Fire ImageSelected event with CameraImageData
            ImageSelected?.Invoke(this, new ImageSelectedEventArgs
            {
                PointNumber = pointNumber,
                RawImageData = _lastImageData,
                Width = _lastImageWidth,
                Height = _lastImageHeight,
                IsRAW16 = _lastImageIsRAW16,
                CameraImageData = selectedImageData  // Include full CameraImageData
            });
            
            System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Selected image for point {pointNumber}");
        }
        
        /// <summary>
        /// Select the latest (most recent) image
        /// </summary>
        public void SelectLatestImage()
        {
            if (_latestPointNumber > 0)
            {
                SelectImage(_latestPointNumber);
            }
            else if (_pointContainers.Count > 0)
            {
                // Fallback: Find the highest point number
                int latestPoint = _pointContainers.Keys.Max();
                SelectImage(latestPoint);
            }
        }
        
        /// <summary>
        /// Update existing calibration image (instead of adding duplicate)
        /// Returns true if successful, false if point not found
        /// </summary>
        public bool UpdateCalibrationImage(int pointNumber, BitmapSource image = null, int starsDetected = 0, double rmsError = 0.0, string statusText = "")
        {
            if (!_pointContainers.ContainsKey(pointNumber))
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] WARNING: Cannot update point {pointNumber} - not found!");
                return false;  // Point not in UI yet
            }
            
            var container = _pointContainers[pointNumber];
            if (container.Child is StackPanel stack && stack.Children.Count >= 1)
            {
                var imageGrid = stack.Children[0] as Grid;
                if (imageGrid != null)
                {
                    if (imageGrid.Children.Count > 0 && imageGrid.Children[0] is Border imageBorder &&
                        imageBorder.Child is Grid previewGrid)
                    {
                        if (image != null)
                        {
                            var imageControl = previewGrid.Children.OfType<Image>().FirstOrDefault();
                            if (imageControl == null)
                            {
                                imageControl = new Image
                                {
                                    Stretch = Stretch.Uniform,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                previewGrid.Children.Insert(0, imageControl);
                            }

                            imageControl.Source = image;
                            foreach (var placeholder in previewGrid.Children.OfType<StackPanel>())
                            {
                                placeholder.Visibility = Visibility.Collapsed;
                            }
                            System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Updated image for point {pointNumber}");
                        }
                    }
                    
                    // Update or create status overlay
                    Border statusOverlay = null;
                    
                    // Find existing status overlay (should be second child if exists)
                    if (imageGrid.Children.Count > 1 && imageGrid.Children[1] is Border existingOverlay)
                    {
                        statusOverlay = existingOverlay;
                        if (statusOverlay.Child is TextBlock existingText)
                        {
                            existingText.Text = statusText;
                            existingText.Foreground = statusText.Contains("Completed") 
                                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))
                                : statusText.Contains("Failed")
                                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"))
                                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                        }
                    }
                    else if (!string.IsNullOrEmpty(statusText))
                    {
                        // Create new status overlay
                        statusOverlay = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(15, 8, 15, 8),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(0, 20, 0, 0)
                        };
                        
                        var statusTextBlock = new TextBlock
                        {
                            Text = statusText,
                            FontSize = 18,
                            FontWeight = FontWeights.Bold,
                            Foreground = statusText.Contains("Completed") 
                                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))
                                : statusText.Contains("Failed")
                                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"))
                                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"))
                        };
                        
                        statusOverlay.Child = statusTextBlock;
                        imageGrid.Children.Add(statusOverlay);
                    }
                }
                
                if (stack.Children.Count >= 3 && stack.Children[2] is TextBlock metaText)
                {
                    string rmsText = rmsError > 0 ? $"  RMS {rmsError:F2}\"" : string.Empty;
                    metaText.Text = $"Stars {starsDetected}{rmsText}";
                }
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] ? Updated point {pointNumber} complete");
            }
            
            return true;  // Successfully updated
        }
        
        /// <summary>
        /// Remove calibration image by point number
        /// </summary>
        public bool RemoveCalibrationImage(int pointNumber)
        {
            try
            {
                // Remove from containers dictionary
                if (_pointContainers.TryGetValue(pointNumber, out var container))
                {
                    // Remove from UI
                    imageStackPanel.Children.Remove(container);
                    _pointContainers.Remove(pointNumber);
                    
                    System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Removed image container for point {pointNumber}");
                }
                
                // Remove from image data dictionary
                if (_pointImageData.ContainsKey(pointNumber))
                {
                    _pointImageData.Remove(pointNumber);
                    System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Removed image data for point {pointNumber}");
                }
                
                // Update image count
                _imageCount = _pointContainers.Count;
                
                // Reset selection if deleted point was selected
                if (_selectedPointNumber == pointNumber)
                {
                    _selectedPointNumber = -1;
                    
                    // [CRITICAL FIX] Reset _lastImageData when selected point is deleted
                    _lastImageData = null;
                    _lastImageWidth = 0;
                    _lastImageHeight = 0;
                }
                
                // Reset latest point if it was deleted
                if (_latestPointNumber == pointNumber)
                {
                    _latestPointNumber = _pointContainers.Keys.Count > 0 ? _pointContainers.Keys.Max() : -1;
                    
                    // [CRITICAL FIX] Update _lastImageData to latest point's data
                    if (_latestPointNumber > 0)
                    {
                        var latestData = GetCameraImageDataForPoint(_latestPointNumber);
                        if (latestData != null && latestData.ImageData != null)
                        {
                            _lastImageData = latestData.ImageData;
                            _lastImageWidth = latestData.Width;
                            _lastImageHeight = latestData.Height;
                            _lastImageIsRAW16 = latestData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;
                        }
                    }
                }
                
                // Show placeholder if no images left
                if (_imageCount == 0 && noImagesPlaceholder != null)
                {
                    noImagesPlaceholder.Visibility = Visibility.Visible;
                    
                    // [CRITICAL FIX] Clear all state when no images left
                    _lastImageData = null;
                    _lastImageWidth = 0;
                    _lastImageHeight = 0;
                }
                else if (_selectedPointNumber < 0 && _latestPointNumber > 0)
                {
                    SelectImage(_latestPointNumber);
                }
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Removed point {pointNumber}, {_imageCount} images remaining");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] RemoveCalibrationImage error: {ex.Message}");
                return false;
            }
        }


        /// <summary>
        /// Create status bar for an image
        /// </summary>
        private Border CreateStatusBar(int pointNumber, int totalPoints, double azimuth, double altitude, int starsDetected, double rmsError)
        {
            var statusBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46")),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(15, 10, 15, 10)
            };

            var statusGrid = new Grid();
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Point Number
            var pointStack = CreateStatusItem("Point:", $"{pointNumber}/{totalPoints}", "#FFFFFF", 0);
            Grid.SetColumn(pointStack, 0);
            statusGrid.Children.Add(pointStack);

            // Position
            var positionStack = CreateStatusItem("Position:", $"Az: {azimuth:F2}°  Alt: {altitude:F2}°", "#3B82F6", 1);
            Grid.SetColumn(positionStack, 1);
            statusGrid.Children.Add(positionStack);

            // Stars
            var starsStack = CreateStatusItem("Stars:", $"{starsDetected}", "#F59E0B", 2);
            Grid.SetColumn(starsStack, 2);
            statusGrid.Children.Add(starsStack);

            // RMS Error
            var rmsStack = CreateStatusItem("RMS Error:", $"{rmsError:F2} arcsec", "#EF4444", 3);
            Grid.SetColumn(rmsStack, 3);
            statusGrid.Children.Add(rmsStack);

            statusBorder.Child = statusGrid;
            return statusBorder;
        }

        /// <summary>
        /// Create a status item (label + value)
        /// </summary>
        private StackPanel CreateStatusItem(string label, string value, string valueColor, int columnIndex)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 30, 0)
            };

            var labelText = new TextBlock
            {
                Text = label + " ",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

            var valueText = new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(valueColor)),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(labelText);
            stack.Children.Add(valueText);

            return stack;
        }

        /// <summary>
        /// Clear all images
        /// </summary>
        public void ClearAllImages()
        {
            // Remove all children except placeholder
            for (int i = imageStackPanel.Children.Count - 1; i >= 0; i--)
            {
                if (imageStackPanel.Children[i] != noImagesPlaceholder)
                {
                    imageStackPanel.Children.RemoveAt(i);
                }
            }

            _imageCount = 0;
            _pointContainers.Clear();
            _pointImageData.Clear();  // Clear stored image data
            _selectedPointNumber = -1;
            _latestPointNumber = -1;

            // Show placeholder
            if (noImagesPlaceholder != null)
            {
                noImagesPlaceholder.Visibility = Visibility.Visible;
            }

            UpdateSelectedPreview(null, -1);
            SyncStretchControlsFromImage(null);

            System.Diagnostics.Debug.WriteLine("[LiveCalibration] Cleared all images");
        }

        /// <summary>
        /// Get current image count
        /// </summary>
        public int ImageCount => _imageCount;
        
        /// <summary>
        /// Store RAW image data for histogram stretching (call before AddCalibrationImage)
        /// </summary>
        public void SetRawImageData(byte[] rawData, int width, int height, bool isRAW16)
        {
            _lastImageData = rawData;
            _lastImageWidth = width;
            _lastImageHeight = height;
            _lastImageIsRAW16 = isRAW16;
        }
        
        /// <summary>
        /// Apply auto-stretch to the last added image (call AFTER AddCalibrationImage!)
        /// [FIXED] Calculates proper auto-stretch values from RAW pixel data
        /// instead of using default 0-65535 range which makes images appear black.
        /// </summary>
        public void ApplyAutoStretchToLastImage()
        {
            if (_latestPointNumber <= 0)
                return;
            
            // Get CameraImageData for the latest point
            var latestImageData = GetCameraImageDataForPoint(_latestPointNumber);
            if (latestImageData == null || latestImageData.ImageData == null)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] ApplyAutoStretchToLastImage: No CameraImageData for point {_latestPointNumber}");
                return;
            }
            
            // [FIX] Calculate proper auto-stretch from actual pixel data when no custom stretch is set
            if (!latestImageData.HasCustomStretch)
            {
                bool isRAW16 = latestImageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;
                if (isRAW16 && latestImageData.ImageData.Length >= 2)
                {
                    CalculateAutoStretchValues(latestImageData);
                }
            }
            
            // Use stretch values from CameraImageData (now properly calculated)
            RestretchSelectedImageFromData(_latestPointNumber, latestImageData);
            if (_selectedPointNumber == _latestPointNumber)
            {
                SyncStretchControlsFromImage(latestImageData);
            }
            
            System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Applied auto-stretch to point {_latestPointNumber}, Stretch={latestImageData.StretchMin}-{latestImageData.StretchMax}");
        }
        
        /// <summary>
        /// Calculate auto-stretch values from RAW16 pixel data using percentile-based histogram.
        /// Uses 1st percentile as black point and 99.5th percentile as white point.
        /// This ensures faint stars are visible instead of being crushed to black.
        /// </summary>
        private void CalculateAutoStretchValues(CameraImageData imageData)
        {
            try
            {
                byte[] rawData = imageData.ImageData;
                int pixelCount = imageData.Width * imageData.Height;
                
                if (pixelCount <= 0 || rawData.Length < pixelCount * 2)
                    return;
                
                // Build histogram (256 bins for speed)
                const int NUM_BINS = 256;
                int[] histogram = new int[NUM_BINS];
                ushort globalMin = ushort.MaxValue;
                ushort globalMax = ushort.MinValue;
                
                for (int i = 0; i < pixelCount; i++)
                {
                    ushort value = BitConverter.ToUInt16(rawData, i * 2);
                    if (value < globalMin) globalMin = value;
                    if (value > globalMax) globalMax = value;
                    
                    // Map to bin (0-255)
                    int bin = (int)((long)value * (NUM_BINS - 1) / 65535);
                    histogram[bin]++;
                }
                
                // Find percentile values (1% black, 99.5% white)
                double blackPercentile = 0.01;
                double whitePercentile = 0.995;
                int blackCount = (int)(pixelCount * blackPercentile);
                int whiteCount = (int)(pixelCount * whitePercentile);
                
                int cumulative = 0;
                int blackBin = 0, whiteBin = NUM_BINS - 1;
                
                for (int i = 0; i < NUM_BINS; i++)
                {
                    cumulative += histogram[i];
                    if (cumulative >= blackCount && blackBin == 0)
                        blackBin = i;
                    if (cumulative >= whiteCount)
                    {
                        whiteBin = i;
                        break;
                    }
                }
                
                // Convert bins back to 16-bit values
                int range = globalMax - globalMin;
                int stretchMin = globalMin + (blackBin * range / (NUM_BINS - 1));
                int stretchMax = globalMin + (whiteBin * range / (NUM_BINS - 1));
                
                // Ensure minimum range
                if (stretchMax - stretchMin < 100)
                    stretchMax = stretchMin + 1000;
                
                imageData.StretchMin = Math.Max(0, stretchMin);
                imageData.StretchMax = Math.Min(65535, stretchMax);
                
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] Auto-stretch calculated: Min={imageData.StretchMin}, Max={imageData.StretchMax} (pixel range: {globalMin}-{globalMax})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveCalibration] CalculateAutoStretchValues error: {ex.Message}");
            }
        }

        private void SliderStretch_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingStretchControls || _selectedPointNumber <= 0)
                return;

            var selectedImageData = GetCameraImageDataForPoint(_selectedPointNumber);
            if (selectedImageData == null || selectedImageData.ImageData == null)
                return;

            int black = sliderStretchBlack != null ? (int)sliderStretchBlack.Value : selectedImageData.StretchMin;
            int white = sliderStretchWhite != null ? (int)sliderStretchWhite.Value : selectedImageData.StretchMax;

            if (sender == sliderStretchBlack && black > white - MIN_STRETCH_WINDOW)
            {
                black = Math.Max(0, white - MIN_STRETCH_WINDOW);
                _isUpdatingStretchControls = true;
                sliderStretchBlack.Value = black;
                _isUpdatingStretchControls = false;
            }
            else if (sender == sliderStretchWhite && white < black + MIN_STRETCH_WINDOW)
            {
                white = Math.Min(65535, black + MIN_STRETCH_WINDOW);
                _isUpdatingStretchControls = true;
                sliderStretchWhite.Value = white;
                _isUpdatingStretchControls = false;
            }

            selectedImageData.StretchMin = black;
            selectedImageData.StretchMax = white;
            selectedImageData.HasCustomStretch = true;

            UpdateStretchValueLabels(black, white);
            RestretchSelectedImageFromData(_selectedPointNumber, selectedImageData);
        }

        private void BtnResetStretch_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPointNumber <= 0)
                return;

            var selectedImageData = GetCameraImageDataForPoint(_selectedPointNumber);
            if (selectedImageData == null || selectedImageData.ImageData == null)
                return;

            selectedImageData.HasCustomStretch = false;
            CalculateAutoStretchValues(selectedImageData);
            SyncStretchControlsFromImage(selectedImageData);
            RestretchSelectedImageFromData(_selectedPointNumber, selectedImageData);
        }


        



        
        #region Histogram Stretch
        
        /// <summary>
        /// Re-stretch last image with current histogram settings (LEGACY - kept for backward compatibility)
        /// [DEPRECATED] Use RestretchSelectedImageFromData instead
        /// </summary>
        private void RestretchLastImage()
        {
            // [DEPRECATED] Redirect to new method using latest point's CameraImageData
            if (_latestPointNumber > 0)
            {
                var latestImageData = GetCameraImageDataForPoint(_latestPointNumber);
                if (latestImageData != null)
                {
                    RestretchSelectedImageFromData(_latestPointNumber, latestImageData);
                }
            }
        }
        
        /// <summary>
        /// Update the last displayed image with new bitmap (LEGACY)
        /// [DEPRECATED] Use UpdateImageInContainer instead
        /// </summary>
        private void UpdateLastDisplayedImage(BitmapSource newBitmap)
        {
            // [DEPRECATED] Redirect to new method using latest point's container
            if (_latestPointNumber > 0 && _pointContainers.TryGetValue(_latestPointNumber, out var container))
            {
                UpdateImageInContainer(container, newBitmap);
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Event args for when an image is selected in LiveCalibrationImageControl
    /// </summary>
    public class ImageSelectedEventArgs : EventArgs
    {
        public int PointNumber { get; set; }
        public byte[] RawImageData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsRAW16 { get; set; }
        
        /// <summary>
        /// Full CameraImageData object for advanced processing
        /// </summary>
        public CameraImageData CameraImageData { get; set; }
    }
    
    /// <summary>
    /// Event args for when a new image is added to LiveCalibrationImageControl
    /// </summary>
    public class NewImageAddedEventArgs : EventArgs
    {
        public int PointNumber { get; set; }
        public byte[] RawImageData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsRAW16 { get; set; }
        
        /// <summary>
        /// Full CameraImageData object for advanced processing
        /// </summary>
    public CameraImageData CameraImageData { get; set; }
    }
}
