using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.Utility;
using static ForkMount_WPF.Engine.Camera.ZWOASICameraSDK;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// High-level manager for ZWO ASI cameras
    /// Uses custom P/Invoke wrapper (ZWOASICameraSDK)
    /// </summary>
    public class ZWOASIManager
    {
        #region Private Fields

        private int _cameraID = -1;
        private bool _isConnected = false;
        private ASI_CAMERA_INFO _cameraInfo;
        private Dictionary<ASI_CONTROL_TYPE, ASI_CONTROL_CAPS> _controlCaps;

        // Current settings
        private int _exposureTime_us = 1000000;
        private int _gain = 0;
        private int _offset = 0;
        private int _width = 0;          // BINNED image size (actual pixels captured)
        private int _height = 0;         // BINNED image size (actual pixels captured)
        private int _fullWidth = 0;      // Full frame width (before binning)
        private int _fullHeight = 0;     // Full frame height (before binning)
        private int _binning = 1;
        private ASI_IMG_TYPE _imageType = ASI_IMG_TYPE.ASI_IMG_RAW16;

        private readonly object _captureLock = new object();

        #endregion

        #region Properties

        /// <summary>
        /// Centralized camera information (single source of truth)
        /// </summary>
        public CameraInfo CurrentCameraInfo { get; private set; }

        public bool IsConnected => _isConnected;
        public int CameraID => _cameraID;
        public string CameraName => _isConnected ? _cameraInfo.GetName() : (CurrentCameraInfo?.CameraName ?? "Not Connected");
        
        /// <summary>
        /// Max Width - ALWAYS use hardware value when connected
        /// </summary>
        public int MaxWidth => _isConnected ? _cameraInfo.MaxWidth : (CurrentCameraInfo?.MaxWidth ?? 0);
        
        /// <summary>
        /// Max Height - ALWAYS use hardware value when connected
        /// </summary>
        public int MaxHeight => _isConnected ? _cameraInfo.MaxHeight : (CurrentCameraInfo?.MaxHeight ?? 0);
        
        public bool IsColorCamera => _isConnected ? (_cameraInfo.IsColorCam == ASI_BOOL.ASI_TRUE) : (CurrentCameraInfo?.IsColorCamera ?? false);
        public bool HasCooler => _isConnected ? (_cameraInfo.IsCoolerCam == ASI_BOOL.ASI_TRUE) : (CurrentCameraInfo?.HasCooler ?? false);
        
        /// <summary>
        /// Whether the camera is connected via USB 3.0 host port
        /// </summary>
        public bool IsUSB3Host => _isConnected && _cameraInfo.IsUSB3Host == ASI_BOOL.ASI_TRUE;
        
        /// <summary>
        /// Pixel Size - ALWAYS use hardware value when connected
        /// </summary>
        public double PixelSize => _isConnected ? _cameraInfo.PixelSize : (CurrentCameraInfo?.PixelSize_um ?? 0);

        /// <summary>
        /// Current image width (binned size)
        /// </summary>
        public int CurrentWidth => CurrentCameraInfo?.CurrentWidth ?? 0;

        /// <summary>
        /// Current image height (binned size)
        /// </summary>
        public int CurrentHeight => CurrentCameraInfo?.CurrentHeight ?? 0;

        /// <summary>
        /// Current binning mode
        /// </summary>
        public int CurrentBinning => CurrentCameraInfo?.Binning ?? 1;

        /// <summary>
        /// Current image type
        /// </summary>
        public ASI_IMG_TYPE CurrentImageType => _imageType;
        public string LastErrorMessage { get; private set; } = string.Empty;

        public int ExposureTime_us
        {
            get => _exposureTime_us;
            set => _exposureTime_us = value;
        }

        public int Gain
        {
            get => _gain;
            set => _gain = value;
        }

        public int Offset
        {
            get => _offset;
            set => _offset = value;
        }
        
        /// <summary>
        /// Get current exposure status from camera
        /// </summary>
        public ASI_EXPOSURE_STATUS GetExposureStatus()
        {
            if (!_isConnected)
                return ASI_EXPOSURE_STATUS.ASI_EXP_IDLE;
            
            try
            {
                var error = ASIGetExpStatus(_cameraID, out ASI_EXPOSURE_STATUS status);
                if (error == ASI_ERROR_CODE.ASI_SUCCESS)
                    return status;
                
                return ASI_EXPOSURE_STATUS.ASI_EXP_IDLE;
            }
            catch
            {
                return ASI_EXPOSURE_STATUS.ASI_EXP_IDLE;
            }
        }

        #endregion

        #region Constructor

        public ZWOASIManager()
        {
            _controlCaps = new Dictionary<ASI_CONTROL_TYPE, ASI_CONTROL_CAPS>();
            
            // Initialize CameraInfo from settings (fallback)
            CurrentCameraInfo = CameraInfo.CreateFromSettings();
            
            System.Diagnostics.Debug.WriteLine("[ZWOASICamera] Controller initialized");
            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Default CameraInfo: {CurrentCameraInfo}");
            
            // Test SDK availability
            try
            {
                var sdkVersion = ASIGetSDKVersion();
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] SDK Version: {sdkVersion}");
                
                var numCameras = ASIGetNumOfConnectedCameras();
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Detected {numCameras} camera(s) at initialization");
            }
            catch (DllNotFoundException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] ASICamera2.dll not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] SDK test failed: {ex.Message}");
            }
        }

        #endregion

        #region Camera Discovery

        /// <summary>
        /// Get number of connected ZWO ASI cameras
        /// </summary>
        public static int GetConnectedCameraCount()
        {
            try
            {
                int count = ASIGetNumOfConnectedCameras();
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Found {count} connected cameras");
                return count;
            }
            catch (DllNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine("[ZWOASICamera ERROR] ASICamera2.dll not found! Install ZWOptical.SDK NuGet package or place DLL manually.");
                return 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to get camera count: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get list of all connected cameras with details
        /// </summary>
        public static List<CameraEnumerationInfo> GetConnectedCameras()
        {
            var cameras = new List<CameraEnumerationInfo>();

            try
            {
                int count = ASIGetNumOfConnectedCameras();

                for (int i = 0; i < count; i++)
                {
                    var error = ASIGetCameraProperty(out ASI_CAMERA_INFO info, i);
                    if (error == ASI_ERROR_CODE.ASI_SUCCESS)
                    {
                        cameras.Add(new CameraEnumerationInfo
                        {
                            Index = i,
                            CameraID = info.CameraID,
                            Name = info.GetName(),
                            MaxWidth = info.MaxWidth,
                            MaxHeight = info.MaxHeight,
                            IsColorCamera = info.IsColorCam == ASI_BOOL.ASI_TRUE,
                            HasCooler = info.IsCoolerCam == ASI_BOOL.ASI_TRUE,
                            PixelSize = info.PixelSize
                        });
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Enumerated {cameras.Count} cameras");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to enumerate cameras: {ex.Message}");
            }

            return cameras;
        }

        #endregion

        #region Connection

        /// <summary>
        /// Connect to camera by index (0 = first camera)
        /// </summary>
        public bool Connect(int cameraIndex = 0)
        {
            try
            {
                if (_isConnected)
                {
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera] Already connected");
                    return true;
                }

                // Check how many cameras are connected
                int numCameras = ASIGetNumOfConnectedCameras();
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Found {numCameras} camera(s) connected");
                
                if (numCameras == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera ERROR] No cameras found! Check USB connection and drivers.");
                    return false;
                }
                
                if (cameraIndex >= numCameras)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Camera index {cameraIndex} invalid (only {numCameras} cameras found)");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Attempting to connect to camera index {cameraIndex}...");

                // Get camera info using official SDK
                var error = ASIGetCameraProperty(out _cameraInfo, cameraIndex);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to get camera property: {error}");
                    return false;
                }

                // Debug: Display camera info
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] ? Camera Info:");
                System.Diagnostics.Debug.WriteLine($"  Name: '{_cameraInfo.GetName()}'");
                System.Diagnostics.Debug.WriteLine($"  CameraID: {_cameraInfo.CameraID}");
                System.Diagnostics.Debug.WriteLine($"  Resolution: {_cameraInfo.MaxWidth}x{_cameraInfo.MaxHeight}");
                System.Diagnostics.Debug.WriteLine($"  PixelSize: {_cameraInfo.PixelSize} um");
                System.Diagnostics.Debug.WriteLine($"  BitDepth: {_cameraInfo.BitDepth}");
                System.Diagnostics.Debug.WriteLine($"  IsUSB3: {_cameraInfo.IsUSB3Camera}");
                System.Diagnostics.Debug.WriteLine($"  HasCooler: {_cameraInfo.IsCoolerCam}");

                if (_cameraInfo.MaxWidth == 0 || _cameraInfo.MaxHeight == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera ERROR] Invalid camera info - MaxWidth or MaxHeight is 0!");
                    return false;
                }

                _cameraID = _cameraInfo.CameraID;

                // Open camera
                error = ASIOpenCamera(_cameraID);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to open camera: {error}");
                    return false;
                }

                // Initialize camera
                error = ASIInitCamera(_cameraID);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to initialize camera: {error}");
                    ASICloseCamera(_cameraID);
                    return false;
                }

                // Load control capabilities
                LoadControlCapabilities();

                // Set default ROI (full frame, no binning)
                _fullWidth = _cameraInfo.MaxWidth;
                _fullHeight = _cameraInfo.MaxHeight;
                _binning = 1;
                // For binning=1, binned size = full size
                _width = _fullWidth;
                _height = _fullHeight;

                // IMPORTANT: SDK expects binned dimensions (which equals full size when binning=1)
                error = ASISetROIFormat(_cameraID, _width, _height, _binning, _imageType);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera WARNING] Failed to set ROI: {error}");
                }

                // Apply default settings
                ApplyExposure();
                ApplyGain();
                ApplyOffset();
                
                // [USB 3.0 OPTIMIZATION] Set bandwidth to maximum (100) for fastest readout
                if (HasControl("BandWidth"))
                {
                    SetControl("BandWidth", 100);
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera] USB 3.0 High-Speed Mode enabled (Bandwidth=100)");
                }
                
                if (HasControl("HighSpeedMode"))
                {
                    SetControl("HighSpeedMode", 1);
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera] High-Speed Mode enabled");
                }

                // [NEW] Update CameraInfo with hardware data
                UpdateCameraInfoFromHardware();

                _isConnected = true;
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] ? Connected: {CameraName} ({_width}x{_height})");
                System.Diagnostics.Debug.WriteLine($"  Pixel: {PixelSize:F2} um, USB3: {_cameraInfo.IsUSB3Camera == ASI_BOOL.ASI_TRUE}");
                System.Diagnostics.Debug.WriteLine($"  CameraInfo: {CurrentCameraInfo}");

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Connection failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Stack: {ex.StackTrace}");
                return false;
            }
        }
        
        /// <summary>
        /// Update CameraInfo with current hardware data
        /// This is called after camera connects, so we check if _cameraInfo is valid instead of _isConnected
        /// </summary>
        private void UpdateCameraInfoFromHardware()
        {
            try
            {
                // Check if we have valid hardware info (don't check _isConnected here - it's set AFTER this call)
                if (_cameraInfo.MaxWidth == 0 || _cameraInfo.MaxHeight == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera] UpdateCameraInfoFromHardware: No valid hardware info");
                    return;
                }
                
                if (CurrentCameraInfo == null)
                {
                    CurrentCameraInfo = new CameraInfo();
                }

                System.Diagnostics.Debug.WriteLine("[ZWOASICamera] =========================================");
                System.Diagnostics.Debug.WriteLine("[ZWOASICamera] ?? UPDATING CameraInfo FROM HARDWARE");
                System.Diagnostics.Debug.WriteLine("[ZWOASICamera] =========================================");
                System.Diagnostics.Debug.WriteLine($"  Old Resolution: {CurrentCameraInfo.MaxWidth}x{CurrentCameraInfo.MaxHeight}");
                System.Diagnostics.Debug.WriteLine($"  New Resolution: {_cameraInfo.MaxWidth}x{_cameraInfo.MaxHeight}");
                System.Diagnostics.Debug.WriteLine($"  Old PixelSize: {CurrentCameraInfo.PixelSize_um} ?m");
                System.Diagnostics.Debug.WriteLine($"  New PixelSize: {_cameraInfo.PixelSize} ?m");

                // Update hardware properties from actual camera
                CurrentCameraInfo.CameraName = _cameraInfo.GetName();
                CurrentCameraInfo.MaxWidth = _cameraInfo.MaxWidth;
                CurrentCameraInfo.MaxHeight = _cameraInfo.MaxHeight;
                CurrentCameraInfo.PixelSize_um = _cameraInfo.PixelSize;
                CurrentCameraInfo.IsColorCamera = _cameraInfo.IsColorCam == ASI_BOOL.ASI_TRUE;
                CurrentCameraInfo.HasCooler = _cameraInfo.IsCoolerCam == ASI_BOOL.ASI_TRUE;
                CurrentCameraInfo.IsConnected = true;

                // Update current dimensions
                CurrentCameraInfo.CurrentWidth = _width;
                CurrentCameraInfo.CurrentHeight = _height;
                CurrentCameraInfo.Binning = _binning;

                // Update exposure settings
                CurrentCameraInfo.ExposureTime_ms = _exposureTime_us / 1000;
                CurrentCameraInfo.Gain = _gain;
                CurrentCameraInfo.Offset = _offset;

                // Load focal length from settings (if not already set)
                if (CurrentCameraInfo.FocalLength_mm <= 0)
                {
                    CurrentCameraInfo.LoadFromAppSettings();
                }

                // Recalculate PlateScale and FOV
                CurrentCameraInfo.RecalculateAll();

                System.Diagnostics.Debug.WriteLine("[ZWOASICamera] CameraInfo updated from hardware");
                System.Diagnostics.Debug.WriteLine($"  PlateScale: {CurrentCameraInfo.PlateScale_ArcsecPerPixel:F3} arcsec/pixel");
                System.Diagnostics.Debug.WriteLine($"  FOV: {CurrentCameraInfo.GetFOVString()}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to update CameraInfo: {ex.Message}");
            }
        }

        /// <summary>
        /// Disconnect from camera
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_isConnected)
                {
                    ASICloseCamera(_cameraID);
                    _isConnected = false;
                    _cameraID = -1;
                    
                    // Update CameraInfo
                    if (CurrentCameraInfo != null)
                    {
                        CurrentCameraInfo.IsConnected = false;
                        CurrentCameraInfo.CameraName = "Disconnected";
                    }
                    
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera] Disconnected");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Disconnect failed: {ex.Message}");
            }
        }

        #endregion

        #region Control Capabilities

        /// <summary>

        /// Load all available control capabilities from camera
        /// </summary>
        private void LoadControlCapabilities()
        {
            try
            {
                var error = ASIGetNumOfControls(_cameraID, out int numControls);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to get control count: {error}");
                    return;
                }

                _controlCaps.Clear();

                for (int i = 0; i < numControls; i++)
                {
                    error = ASIGetControlCaps(_cameraID, i, out ASI_CONTROL_CAPS caps);
                    if (error == ASI_ERROR_CODE.ASI_SUCCESS)
                    {
                        _controlCaps[caps.ControlType] = caps;
                var controlName = caps.GetName();
                        System.Diagnostics.Debug.WriteLine($"  Control: {controlName} (Min={caps.MinValue}, Max={caps.MaxValue}, Default={caps.DefaultValue})");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Loaded {_controlCaps.Count} controls");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to load controls: {ex.Message}");
            }
        }

        /// <summary>
        /// Get control capability
        /// </summary>
        public ASI_CONTROL_CAPS? GetControlCapability(ASI_CONTROL_TYPE controlType)
        {
            if (_controlCaps.TryGetValue(controlType, out var caps))
                return caps;

            return null;
        }

        #endregion

        #region Camera Settings

        /// <summary>
        /// Set exposure time in microseconds
        /// </summary>
        public bool SetExposure(int exposureTime_us)
        {
            _exposureTime_us = exposureTime_us;
            return ApplyExposure();
        }

        /// <summary>
        /// Apply exposure time to camera
        /// </summary>
        private bool ApplyExposure()
        {
            if (!_isConnected)
                return false;

            try
            {
                var error = ASISetControlValue(_cameraID, ASI_CONTROL_TYPE.ASI_EXPOSURE, _exposureTime_us, ASI_BOOL.ASI_FALSE);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set exposure: {error}");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Exposure set to {_exposureTime_us / 1000.0:F1} ms");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to apply exposure: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set gain
        /// </summary>
        public bool SetGain(int gain)
        {
            _gain = gain;
            return ApplyGain();
        }

        /// <summary>
        /// Apply gain to camera
        /// </summary>
        private bool ApplyGain()
        {
            if (!_isConnected)
                return false;

            try
            {
                var error = ASISetControlValue(_cameraID, ASI_CONTROL_TYPE.ASI_GAIN, _gain, ASI_BOOL.ASI_FALSE);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set gain: {error}");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Gain set to {_gain}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to apply gain: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set offset
        /// </summary>
        public bool SetOffset(int offset)
        {
            _offset = offset;
            return ApplyOffset();
        }

        /// <summary>
        /// Apply offset to camera
        /// </summary>
        private bool ApplyOffset()
        {
            if (!_isConnected)
                return false;

            try
            {
                var error = ASISetControlValue(_cameraID, ASI_CONTROL_TYPE.ASI_OFFSET, _offset, ASI_BOOL.ASI_FALSE);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set offset: {error}");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Offset set to {_offset}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to apply offset: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Check if camera has specific control
        /// </summary>
        private bool HasControl(string controlName)
        {
            return _controlCaps.Values.Any(c => c.GetName() == controlName);
        }
        
        /// <summary>
        /// Set control value by name
        /// </summary>
        private bool SetControl(string controlName, long value)
        {
            if (!_isConnected)
                return false;
                
            try
            {
                var control = _controlCaps.FirstOrDefault(kvp => kvp.Value.GetName() == controlName);
                    
                if (string.IsNullOrEmpty(control.Value.GetName()))
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Control '{controlName}' not found");
                    return false;
                }
                
                var error = ASISetControlValue(_cameraID, control.Key, (int)value, ASI_BOOL.ASI_FALSE);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set {controlName}: {error}");
                    return false;
                }
                
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] {controlName} set to {value}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set {controlName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set ROI (Region of Interest) and binning
        /// 
        /// IMPORTANT: ZWO SDK expects BINNED dimensions!
        /// For example: Full frame 11656x8750 with binning 2x2 ? Pass 5828x4375
        /// </summary>
        public bool SetROI(int width, int height, int binning)
        {
            if (!_isConnected)
                return false;

            try
            {
                // Validate dimensions
                if (width > _cameraInfo.MaxWidth || height > _cameraInfo.MaxHeight)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] ROI exceeds maximum size");
                    return false;
                }

                // Store FULL frame dimensions
                _fullWidth = width;
                _fullHeight = height;

                // Make sure width/height are divisible by binning
                if (binning > 1)
                {
                    width = (width / binning) * binning;
                    height = (height / binning) * binning;
                    _fullWidth = width;
                    _fullHeight = height;
                }

                // CRITICAL: Calculate BINNED dimensions for SDK
                int binnedWidth = width / binning;
                int binnedHeight = height / binning;

                var error = ASISetROIFormat(_cameraID, binnedWidth, binnedHeight, binning, _imageType);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set ROI: {error}");
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Attempted: {binnedWidth}x{binnedHeight}, Bin={binning}, Type={_imageType}");
                    return false;
                }

                // Store settings
                _binning = binning;
                _width = binnedWidth;
                _height = binnedHeight;

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] ROI set successfully:");
                System.Diagnostics.Debug.WriteLine($"  Full frame: {_fullWidth}x{_fullHeight}");
                System.Diagnostics.Debug.WriteLine($"  Binning: {binning}x{binning}");
                System.Diagnostics.Debug.WriteLine($"  Actual image size: {_width}x{_height}");
                System.Diagnostics.Debug.WriteLine($"  Image Type: {_imageType}");
                
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set ROI: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set ROI, binning, and image type together (atomic operation)
        /// This is the recommended way to change camera format to avoid conflicts
        /// 
        /// IMPORTANT: ZWO SDK expects BINNED dimensions, not full frame dimensions!
        /// For example: Full frame 11656x8750 with binning 2x2 ? Pass 5828x4375
        /// </summary>
        public bool SetROIAndImageType(int width, int height, int binning, ASI_IMG_TYPE imageType, out ASI_ERROR_CODE errorCode)
        {
            errorCode = ASI_ERROR_CODE.ASI_ERROR_INVALID_ID;
            
            if (!_isConnected)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Camera not connected");
                return false;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Setting ROI and image type...");
                System.Diagnostics.Debug.WriteLine($"  Camera: {CameraName}");
                System.Diagnostics.Debug.WriteLine($"  Max resolution: {_cameraInfo.MaxWidth}x{_cameraInfo.MaxHeight}");
                System.Diagnostics.Debug.WriteLine($"  Requested: {width}x{height}, Bin={binning}, Type={imageType}");
                
                // Store FULL frame dimensions for internal tracking
                _fullWidth = width;
                _fullHeight = height;
                
                // Validate dimensions
                if (width > _cameraInfo.MaxWidth || height > _cameraInfo.MaxHeight)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] ROI exceeds maximum size");
                    errorCode = ASI_ERROR_CODE.ASI_ERROR_INVALID_SIZE;
                    return false;
                }

                // Make sure width/height are divisible by binning
                if (binning > 1)
                {
                    width = (width / binning) * binning;
                    height = (height / binning) * binning;
                    _fullWidth = width;
                    _fullHeight = height;
                }

                // CRITICAL: Calculate BINNED dimensions for SDK call
                // ZWO SDK expects binned size, not full frame size!
                int binnedWidth = width / binning;
                int binnedHeight = height / binning;
                
                System.Diagnostics.Debug.WriteLine($"  Full frame (internal): {width}x{height}");
                System.Diagnostics.Debug.WriteLine($"  Binned size (for SDK): {binnedWidth}x{binnedHeight}");

                // Set everything at once using BINNED dimensions
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Calling ASISetROIFormat with BINNED dimensions...");
                errorCode = ASISetROIFormat(_cameraID, binnedWidth, binnedHeight, binning, imageType);
                
                if (errorCode != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] ? ASISetROIFormat failed with error: {errorCode}");
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Parameters: width={binnedWidth}, height={binnedHeight}, bin={binning}, type={imageType}");
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Current settings: _imageType={_imageType}, _binning={_binning}");
                    
                    // Provide specific error explanations
                    switch (errorCode)
                    {
                        case ASI_ERROR_CODE.ASI_ERROR_INVALID_SIZE:
                            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] ? Invalid size! Camera doesn't support this resolution/binning combination");
                            break;
                        case ASI_ERROR_CODE.ASI_ERROR_INVALID_IMGTYPE:
                            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] ? Invalid image type! Camera doesn't support {imageType}");
                            break;
                        case ASI_ERROR_CODE.ASI_ERROR_GENERAL_ERROR:
                            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] ? General error! Check if camera is in correct mode");
                            break;
                    }
                    
                    return false;
                }

                // Store all settings
                _binning = binning;
                _imageType = imageType;
                // BINNED size = actual pixels captured
                _width = binnedWidth;
                _height = binnedHeight;

                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] ? ROI and image type set successfully!");
                System.Diagnostics.Debug.WriteLine($"  Full frame: {_fullWidth}x{_fullHeight}");
                System.Diagnostics.Debug.WriteLine($"  Binning: {binning}x{binning}");
                System.Diagnostics.Debug.WriteLine($"  Actual image size: {_width}x{_height}");
                System.Diagnostics.Debug.WriteLine($"  Image Type: {_imageType}");
                
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Exception in SetROIAndImageType: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Set image type (RAW8, RAW16, RGB24, Y8)
        /// 
        /// IMPORTANT: ZWO SDK expects BINNED dimensions!
        /// Uses stored _width and _height which are already binned size
        /// </summary>
        public bool SetImageType(ASI_IMG_TYPE imageType)
        {
            if (!_isConnected)
                return false;

            try
            {
                // CRITICAL: Use BINNED dimensions (_width, _height are already binned)
                var error = ASISetROIFormat(_cameraID, _width, _height, _binning, imageType);
                if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set image type: {error}");
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Attempted: {_width}x{_height}, Bin={_binning}, Type={imageType}");
                    return false;
                }

                _imageType = imageType;
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Image type set to {_imageType}");
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera]   Binned size: {_width}x{_height}, Binning: {_binning}x{_binning}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to set image type: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get current temperature (if cooler available)
        /// </summary>
        public double? GetTemperature()
        {
            if (!_isConnected || !HasCooler)
                return null;

            try
            {
                var error = ASIGetControlValue(_cameraID, ASI_CONTROL_TYPE.ASI_TEMPERATURE, out int temp, out _);
                if (error == ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    return temp / 10.0; // Temperature is in 0.1 degrees
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to get temperature: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Enable or disable the cooler
        /// </summary>
        public bool SetCoolerOn(bool enable)
        {
            if (!_isConnected || !HasCooler) return false;
            try
            {
                var error = ASISetControlValue(_cameraID, ASI_CONTROL_TYPE.ASI_COOLER_ON, enable ? 1 : 0, ASI_BOOL.ASI_FALSE);
                if (error == ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Cooler {(enable ? "ON" : "OFF")}");
                    return true;
                }
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] SetCoolerOn failed: {error}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] SetCoolerOn: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Set target cooler temperature in degrees Celsius
        /// </summary>
        public bool SetTargetTemperature(int tempCelsius)
        {
            if (!_isConnected || !HasCooler) return false;
            try
            {
                var error = ASISetControlValue(_cameraID, ASI_CONTROL_TYPE.ASI_TARGET_TEMP, tempCelsius, ASI_BOOL.ASI_FALSE);
                if (error == ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Target temperature set to {tempCelsius} C");
                    return true;
                }
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] SetTargetTemperature failed: {error}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] SetTargetTemperature: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Get cooler power percentage (0-100)
        /// </summary>
        public int? GetCoolerPower()
        {
            if (!_isConnected || !HasCooler) return null;
            try
            {
                var error = ASIGetControlValue(_cameraID, ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC, out int power, out _);
                if (error == ASI_ERROR_CODE.ASI_SUCCESS)
                    return power;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] GetCoolerPower: {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Image Capture

        /// <summary>
        /// Capture single image asynchronously
        /// </summary>
        public async Task<Camera.CameraImageData> CaptureImageAsync(CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => CaptureImage(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Capture single image (blocking)
        /// </summary>
        public Camera.CameraImageData CaptureImage(CancellationToken cancellationToken = default)
        {
            lock (_captureLock)
            {
                LastErrorMessage = string.Empty;

                if (!_isConnected)
                {
                    LastErrorMessage = "Camera not connected.";
                    System.Diagnostics.Debug.WriteLine("[ZWOASICamera ERROR] Camera not connected");
                    return null;
                }

                try
                {
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Starting exposure: {_exposureTime_us / 1000.0:F1} ms");
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera]   Camera ID: {_cameraID}");
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera]   Image Type: {_imageType}");
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera]   ROI: {_width}x{_height}, Bin={_binning}");

                    // Start exposure (ASI_FALSE = single snap mode, not dark frame)
                    var error = ASIStartExposure(_cameraID, ASI_BOOL.ASI_FALSE);
                    if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                    {
                        LastErrorMessage = $"Failed to start exposure: {error}.";
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to start exposure: {error}");
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR]   Possible causes:");
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR]   - Camera in wrong mode (video instead of snap)");
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR]   - Previous exposure not completed");
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR]   - ROI/Format not set correctly");
                        return null;
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Exposure started, waiting for completion...");

                    // Wait for exposure to complete
                    ASI_EXPOSURE_STATUS status = ASI_EXPOSURE_STATUS.ASI_EXP_IDLE;
                    // [USB 3.0 MODE] High-speed readout
                    // ASI461MM Pro (101MP full frame) via USB 3.0 readout time: ~2-3 seconds
                    // Timeout = Exposure time + 5 seconds buffer (optimized for USB 3.0)
                    int timeout_ms = _exposureTime_us / 1000 + 5000; // Exposure time + 5 seconds buffer
                    int elapsed_ms = 0;
                    int pollInterval_ms = 100;
                    
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Waiting for exposure (timeout={timeout_ms}ms)...");

                    while (elapsed_ms < timeout_ms)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            ASIStopExposure(_cameraID);
                            LastErrorMessage = "Exposure was cancelled.";
                            System.Diagnostics.Debug.WriteLine("[ZWOASICamera] Exposure cancelled");
                            return null;
                        }

                        error = ASIGetExpStatus(_cameraID, out status);
                        if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                        {
                            LastErrorMessage = $"Failed to get exposure status: {error}.";
                            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to get exposure status: {error}");
                            return null;
                        }
                        
                        // Log status every 1 second
                        if (elapsed_ms % 1000 == 0 && elapsed_ms > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera]   Exposure status after {elapsed_ms}ms: {status}");
                        }

                        if (status == ASI_EXPOSURE_STATUS.ASI_EXP_SUCCESS)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Exposure completed after {elapsed_ms}ms");
                            break;
                        }
                        else if (status == ASI_EXPOSURE_STATUS.ASI_EXP_FAILED)
                        {
                            LastErrorMessage = "Exposure failed in camera driver.";
                            System.Diagnostics.Debug.WriteLine("[ZWOASICamera ERROR] Exposure failed");
                            return null;
                        }

                        Thread.Sleep(pollInterval_ms);
                        elapsed_ms += pollInterval_ms;
                    }

                    if (elapsed_ms >= timeout_ms)
                    {
                        LastErrorMessage = $"Exposure timed out after {elapsed_ms} ms. Last status: {status}.";
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Exposure timeout after {elapsed_ms}ms");
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR]   Last status: {status}");
                        System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR]   Expected exposure time: {_exposureTime_us / 1000}ms");
                        return null;
                    }

                    // Calculate buffer size
                    int bytesPerPixel = _imageType switch
                    {
                        ASI_IMG_TYPE.ASI_IMG_RAW8 => 1,
                        ASI_IMG_TYPE.ASI_IMG_Y8 => 1,
                        ASI_IMG_TYPE.ASI_IMG_RAW16 => 2,
                        ASI_IMG_TYPE.ASI_IMG_RGB24 => 3,
                        _ => 1
                    };

                    int bufferSize = _width * _height * bytesPerPixel;
                    byte[] imageData = new byte[bufferSize];

                    // Get image data
                    IntPtr pBuffer = Marshal.AllocHGlobal(bufferSize);
                    try
                    {
                        error = ASIGetDataAfterExp(_cameraID, pBuffer, bufferSize);
                        if (error != ASI_ERROR_CODE.ASI_SUCCESS)
                        {
                            LastErrorMessage = $"Failed to read image data after exposure: {error}.";
                            System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Failed to get image data: {error}");
                            return null;
                        }

                        Marshal.Copy(pBuffer, imageData, 0, bufferSize);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pBuffer);
                    }

                    // Create image data object
                    var result = new Camera.CameraImageData
                    {
                        ImageData = imageData,
                        Width = _width,
                        Height = _height,
                        ImageType = _imageType,
                        Binning = _binning,
                        ExposureTime_us = _exposureTime_us,
                        Gain = _gain,
                        Offset = _offset,
                        CaptureTime = NtpTimeService.Instance.GetCurrentTime(),
                        Temperature_C = GetTemperature()
                    };

                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera] Image captured successfully");
                    System.Diagnostics.Debug.WriteLine($"  Size: {result.GetImageSize_MB():F2} MB");
                    if (result.Temperature_C.HasValue)
                        System.Diagnostics.Debug.WriteLine($"  Temperature: {result.Temperature_C.Value:F1} C");

                    return result;
                }
                catch (Exception ex)
                {
                    LastErrorMessage = $"Capture failed: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine($"[ZWOASICamera ERROR] Capture failed: {ex.Message}");
                    return null;
                }
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Camera information for enumeration (legacy - for GetConnectedCameras)
        /// Use Camera.CameraInfo for full camera state
        /// </summary>
        public class CameraEnumerationInfo
        {
            public int Index { get; set; }
            public int CameraID { get; set; }
            public string Name { get; set; }
            public int MaxWidth { get; set; }
            public int MaxHeight { get; set; }
            public bool IsColorCamera { get; set; }
            public bool HasCooler { get; set; }
            public double PixelSize { get; set; }

            public override string ToString()
            {
                return $"{Name} ({MaxWidth}x{MaxHeight}, {PixelSize:F2}um)";
            }
        }

        #endregion
    }
}

