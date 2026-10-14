using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.Utility;
using static ForkMount_WPF.Engine.Camera.FLICameraSDK;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// High-level manager for FLI (Finger Lakes Instrumentation) CCD cameras
    /// Pattern follows ZWOASIManager for consistency
    /// 
    /// Usage flow (from CCD_FLI\CCD_MODULE + CCD_RUNNING):
    ///   1. GetConnectedCameras() � enumerate USB cameras
    ///   2. Connect(serialNumber) � open device by serial
    ///   3. SetExposure / SetBinning / SetTemperature � configure
    ///   4. CaptureImageAsync() � expose + row-by-row download
    ///   5. Disconnect() � close device
    /// </summary>
    public class FLICameraManager
    {
        #region Private Fields

        private FLICameraSDK _fli = null;
        private bool _isConnected = false;

        // Camera info cached after connect
        private string _cameraModel = "";
        private string _serialNumber = "";
        private double _pixelSizeX = 0;
        private double _pixelSizeY = 0;

        // Visible area (sensor dimensions)
        private int _visUlX, _visUlY, _visLrX, _visLrY;
        private int _fullWidth = 0;
        private int _fullHeight = 0;
        private int _activeUlX = 0;
        private int _activeUlY = 0;
        private int _activeLrX = 0;
        private int _activeLrY = 0;

        // Current settings
        private int _exposureTime_ms = 1000;
        private int _hBin = 1;
        private int _vBin = 1;
        private double _targetTemp = -20;

        // Camera modes
        private Dictionary<int, string> _cameraModes = new Dictionary<int, string>();

        private readonly object _captureLock = new object();

        #endregion

        #region Properties

        public CameraInfo CurrentCameraInfo { get; private set; }

        public bool IsConnected => _isConnected;
        public string CameraName => _isConnected ? _cameraModel : (CurrentCameraInfo?.CameraName ?? "Not Connected");
        public string SerialNumber => _serialNumber;

        public int MaxWidth => _fullWidth;
        public int MaxHeight => _fullHeight;
        public double PixelSizeX => _pixelSizeX;
        public double PixelSizeY => _pixelSizeY;
        public bool HasCooler => true; // FLI cameras always have coolers
        private int ActiveSensorWidth => Math.Max(1, _activeLrX - _activeUlX);
        private int ActiveSensorHeight => Math.Max(1, _activeLrY - _activeUlY);

        public int CurrentWidth => Math.Max(1, (_activeLrX - _activeUlX) / Math.Max(1, _hBin));
        public int CurrentHeight => Math.Max(1, (_activeLrY - _activeUlY) / Math.Max(1, _vBin));
        public int CurrentHBin => _hBin;
        public int CurrentVBin => _vBin;

        public Dictionary<int, string> CameraModes => _cameraModes;

        public int ExposureTime_ms
        {
            get => _exposureTime_ms;
            set => _exposureTime_ms = value;
        }

        #endregion

        #region Constructor

        public FLICameraManager()
        {
            CurrentCameraInfo = CameraInfo.CreateFromSettings();

            Debug.WriteLine("[FLICamera] Manager initialized");

            // Test SDK availability
            try
            {
                string libVersion = FLICameraSDK.GetLibVersion();
                Debug.WriteLine($"[FLICamera] SDK Version: {libVersion}");
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] libfli64.dll not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SDK test failed: {ex.Message}");
            }
        }

        #endregion

        #region Camera Discovery

        /// <summary>
        /// Information about a discovered FLI camera
        /// </summary>
        public class FLICameraEnumerationInfo
        {
            public string FileName { get; set; }
            public string ModelName { get; set; }
            public string SerialNumber { get; set; }

            public override string ToString()
            {
                return $"{ModelName} (SN: {SerialNumber})";
            }
        }

        /// <summary>
        /// Enumerate all connected FLI USB cameras
        /// </summary>
        public static List<FLICameraEnumerationInfo> GetConnectedCameras()
        {
            var cameras = new List<FLICameraEnumerationInfo>();

            try
            {
                var names = FLICameraSDK.List(FLI_DOMAIN.CAMERA | FLI_DOMAIN.USB);

                foreach (var name in names)
                {
                    try
                    {
                        // Temporarily open device to get serial number
                        using (var tempFli = new FLICameraSDK(name.FileName, FLI_DOMAIN.CAMERA | FLI_DOMAIN.USB))
                        {
                            cameras.Add(new FLICameraEnumerationInfo
                            {
                                FileName = name.FileName,
                                ModelName = name.ModelName,
                                SerialNumber = tempFli.GetSerialString()
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[FLICamera ERROR] Failed to query device {name.FileName}: {ex.Message}");
                    }
                }

                Debug.WriteLine($"[FLICamera] Enumerated {cameras.Count} camera(s)");
            }
            catch (DllNotFoundException)
            {
                Debug.WriteLine("[FLICamera ERROR] libfli64.dll not found!");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] Failed to enumerate cameras: {ex.Message}");
            }

            return cameras;
        }

        /// <summary>
        /// Get number of connected FLI cameras
        /// </summary>
        public static int GetConnectedCameraCount()
        {
            try
            {
                var names = FLICameraSDK.List(FLI_DOMAIN.CAMERA | FLI_DOMAIN.USB);
                return names.Length;
            }
            catch
            {
                return 0;
            }
        }

        #endregion

        #region Connection

        /// <summary>
        /// Connect to FLI camera by serial number
        /// (Follows CCD_FLI\CCD_MODULE.Connect pattern)
        /// </summary>
        public bool Connect(string serialNumber)
        {
            try
            {
                if (_isConnected)
                {
                    Debug.WriteLine("[FLICamera] Already connected");
                    return true;
                }

                Debug.WriteLine($"[FLICamera] Searching for camera SN: {serialNumber}...");

                var cameras = GetConnectedCameras();
                if (cameras.Count == 0)
                {
                    Debug.WriteLine("[FLICamera ERROR] No FLI cameras found!");
                    return false;
                }

                var target = cameras.FirstOrDefault(c => c.SerialNumber == serialNumber);
                if (target == null)
                {
                    Debug.WriteLine($"[FLICamera ERROR] Camera SN '{serialNumber}' not found");
                    Debug.WriteLine($"[FLICamera] Available cameras:");
                    foreach (var c in cameras)
                        Debug.WriteLine($"  - {c.ModelName} (SN: {c.SerialNumber})");
                    return false;
                }

                return ConnectByFileName(target.FileName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] Connect failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Connect to the first available FLI camera (index 0)
        /// </summary>
        public bool Connect(int cameraIndex = 0)
        {
            try
            {
                if (_isConnected)
                {
                    Debug.WriteLine("[FLICamera] Already connected");
                    return true;
                }

                var cameras = GetConnectedCameras();
                if (cameraIndex >= cameras.Count)
                {
                    Debug.WriteLine($"[FLICamera ERROR] Camera index {cameraIndex} invalid (found {cameras.Count})");
                    return false;
                }

                return ConnectByFileName(cameras[cameraIndex].FileName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] Connect failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Internal: open FLI device by file name, initialize, and read hardware info
        /// </summary>
        private bool ConnectByFileName(string fileName)
        {
            try
            {
                _fli = new FLICameraSDK(fileName, FLI_DOMAIN.CAMERA | FLI_DOMAIN.USB);

                // Read hardware info
                _cameraModel = _fli.GetModel() ?? "Unknown FLI";
                _serialNumber = _fli.GetSerialString() ?? "";
                _fli.GetPixelSize(out _pixelSizeX, out _pixelSizeY);
                double rawPixelSizeX = _pixelSizeX;
                double rawPixelSizeY = _pixelSizeY;
                _pixelSizeX = NormalizePixelSizeMicrons(_pixelSizeX);
                _pixelSizeY = NormalizePixelSizeMicrons(_pixelSizeY);
                ApplyKnownModelPixelSizeFallback();
                _fli.GetVisibleArea(out _visUlX, out _visUlY, out _visLrX, out _visLrY);
                _fullWidth = _visLrX - _visUlX;
                _fullHeight = _visLrY - _visUlY;
                _activeUlX = _visUlX;
                _activeUlY = _visUlY;
                _activeLrX = _visLrX;
                _activeLrY = _visLrY;

                Debug.WriteLine($"[FLICamera] Camera Info:");
                Debug.WriteLine($"  Model: {_cameraModel}");
                Debug.WriteLine($"  Serial: {_serialNumber}");
                Debug.WriteLine($"  Raw PixelSize: {rawPixelSizeX} x {rawPixelSizeY}");
                Debug.WriteLine($"  Resolution: {_fullWidth}x{_fullHeight}");
                Debug.WriteLine($"  PixelSize: {_pixelSizeX:F2} x {_pixelSizeY:F2} �m");

                // Load camera modes
                _cameraModes.Clear();
                for (int i = 0; ; i++)
                {
                    string modeStr = _fli.GetCameraModeString(i);
                    if (modeStr == null) break;
                    _cameraModes.Add(i, modeStr);
                    Debug.WriteLine($"  Mode {i}: {modeStr}");
                }

                // Initialize defaults (from CCD_RUNNING pattern)
                _fli.CancelExposure();
                _fli.ControlBackgroundFlush(FLI_BGFLUSH.START);

                // Set full visible area at 1x1 binning
                _fli.SetHBin(1);
                _fli.SetVBin(1);
                ApplyAlignedImageArea(1, 1);
                _hBin = 1;
                _vBin = 1;

                // Update CameraInfo
                UpdateCameraInfoFromHardware();

                _isConnected = true;
                Debug.WriteLine($"[FLICamera] ? Connected: {_cameraModel} ({_fullWidth}x{_fullHeight})");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] ConnectByFileName failed: {ex.Message}");
                _fli?.Dispose();
                _fli = null;
                return false;
            }
        }

        /// <summary>
        /// Update CameraInfo with current hardware data
        /// </summary>
        private void UpdateCameraInfoFromHardware()
        {
            try
            {
                if (CurrentCameraInfo == null)
                    CurrentCameraInfo = new CameraInfo();

                CurrentCameraInfo.CameraName = _cameraModel;
                CurrentCameraInfo.MaxWidth = _fullWidth;
                CurrentCameraInfo.MaxHeight = _fullHeight;
                CurrentCameraInfo.PixelSize_um = NormalizePixelSizeMicrons(_pixelSizeX);
                CurrentCameraInfo.IsColorCamera = false; // FLI CCD cameras are mono
                CurrentCameraInfo.HasCooler = true;
                CurrentCameraInfo.IsConnected = true;

                CurrentCameraInfo.CurrentWidth = CurrentWidth;
                CurrentCameraInfo.CurrentHeight = CurrentHeight;
                CurrentCameraInfo.Binning = _hBin;
                CurrentCameraInfo.ExposureTime_ms = _exposureTime_ms;

                if (CurrentCameraInfo.FocalLength_mm <= 0)
                    CurrentCameraInfo.LoadFromAppSettings();

                CurrentCameraInfo.RecalculateAll();

                Debug.WriteLine($"[FLICamera] CameraInfo updated from hardware");
                Debug.WriteLine($"  PlateScale: {CurrentCameraInfo.PlateScale_ArcsecPerPixel:F3} arcsec/pixel");
                Debug.WriteLine($"  FOV: {CurrentCameraInfo.GetFOVString()}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] Failed to update CameraInfo: {ex.Message}");
            }
        }

        private static double NormalizePixelSizeMicrons(double pixelSize)
        {
            if (pixelSize <= 0 || double.IsNaN(pixelSize) || double.IsInfinity(pixelSize))
                return pixelSize;

            if (pixelSize < 0.001)
            {
                double converted = pixelSize * 1_000_000.0;
                Debug.WriteLine($"[FLICamera] Pixel size normalized from meters to microns: {pixelSize} -> {converted}");
                return converted;
            }

            if (pixelSize < 1.0)
            {
                double converted = pixelSize * 1000.0;
                Debug.WriteLine($"[FLICamera] Pixel size normalized from mm to microns: {pixelSize} -> {converted}");
                return converted;
            }

            return pixelSize;
        }

        private void ApplyKnownModelPixelSizeFallback()
        {
            if (string.IsNullOrWhiteSpace(_cameraModel))
                return;

            if (_cameraModel.IndexOf("PL16803", StringComparison.OrdinalIgnoreCase) >= 0 ||
                _cameraModel.IndexOf("16803", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool invalidX = _pixelSizeX <= 0 || _pixelSizeX < 1.0 || _pixelSizeX > 100.0;
                bool invalidY = _pixelSizeY <= 0 || _pixelSizeY < 1.0 || _pixelSizeY > 100.0;

                if (invalidX || invalidY)
                {
                    Debug.WriteLine($"[FLICamera] Applying PL16803 fallback pixel size: X={_pixelSizeX}, Y={_pixelSizeY} -> 9.0 um");
                    _pixelSizeX = 9.0;
                    _pixelSizeY = 9.0;
                }
            }
        }

        /// <summary>
        /// Disconnect from camera
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_isConnected && _fli != null)
                {
                    try { _fli.UnlockDevice(); } catch { }
                    _fli.Close();
                    _fli.Dispose();
                }

                _fli = null;
                _isConnected = false;

                if (CurrentCameraInfo != null)
                {
                    CurrentCameraInfo.IsConnected = false;
                    CurrentCameraInfo.CameraName = "Disconnected";
                }

                Debug.WriteLine("[FLICamera] Disconnected");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] Disconnect failed: {ex.Message}");
            }
        }

        #endregion

        #region Camera Settings

        /// <summary>
        /// Set exposure time in milliseconds
        /// Note: FLI SDK uses milliseconds (unlike ZWO which uses microseconds)
        /// </summary>
        public bool SetExposure(int exposureTime_ms)
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetExposureTime(exposureTime_ms);
                _exposureTime_ms = exposureTime_ms;
                Debug.WriteLine($"[FLICamera] Exposure set to {exposureTime_ms} ms");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetExposure failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set horizontal and vertical binning
        /// </summary>
        public bool SetBinning(int hBin, int vBin)
        {
            if (!_isConnected) return false;
            try
            {
                if (hBin <= 0 || vBin <= 0)
                {
                    Debug.WriteLine($"[FLICamera ERROR] Invalid binning requested: {hBin}x{vBin}");
                    return false;
                }

                if (hBin == _hBin && vBin == _vBin)
                {
                    Debug.WriteLine($"[FLICamera] Binning already set to {hBin}x{vBin}");
                    return true;
                }

                _hBin = hBin;
                _vBin = vBin;

                _fli.SetHBin(hBin);
                _fli.SetVBin(vBin);

                // FLI requires image area coordinates that align with the current binning.
                ApplyAlignedImageArea(hBin, vBin);

                UpdateCameraInfoFromHardware();

                Debug.WriteLine($"[FLICamera] Binning set to {hBin}x{vBin} -> image: {CurrentWidth}x{CurrentHeight}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetBinning failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set binning (same for H and V)
        /// </summary>
        public bool SetBinning(int binning)
        {
            return SetBinning(binning, binning);
        }

        /// <summary>
        /// Set image area (ROI)
        /// </summary>
        public bool SetImageArea(int ul_x, int ul_y, int lr_x, int lr_y)
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetImageArea(ul_x, ul_y, lr_x, lr_y);
                UpdateActiveImageArea(ul_x, ul_y, lr_x, lr_y);
                UpdateCameraInfoFromHardware();
                Debug.WriteLine($"[FLICamera] Image area set to ({ul_x},{ul_y})-({lr_x},{lr_y})");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetImageArea failed: {ex.Message}");
                return false;
            }
        }

        private void ApplyAlignedImageArea(int hBin, int vBin)
        {
            int sensorWidth = _visLrX - _visUlX;
            int sensorHeight = _visLrY - _visUlY;

            int alignedWidth = (sensorWidth / hBin) * hBin;
            int alignedHeight = (sensorHeight / vBin) * vBin;

            if (alignedWidth <= 0 || alignedHeight <= 0)
            {
                throw new InvalidOperationException(
                    $"Cannot apply FLI image area for binning {hBin}x{vBin}. Sensor={sensorWidth}x{sensorHeight}");
            }

            int ulX = _visUlX;
            int ulY = _visUlY;
            int lrX = ulX + alignedWidth;
            int lrY = ulY + alignedHeight;

            Debug.WriteLine($"[FLICamera] Applying aligned image area for {hBin}x{vBin}: ({ulX},{ulY})-({lrX},{lrY})");
            _fli.SetImageArea(ulX, ulY, lrX, lrY);
            UpdateActiveImageArea(ulX, ulY, lrX, lrY);
        }

        private void UpdateActiveImageArea(int ulX, int ulY, int lrX, int lrY)
        {
            _activeUlX = ulX;
            _activeUlY = ulY;
            _activeLrX = lrX;
            _activeLrY = lrY;

            Debug.WriteLine($"[FLICamera] Active ROI updated: ({_activeUlX},{_activeUlY})-({_activeLrX},{_activeLrY})");
            Debug.WriteLine($"[FLICamera] Active image size: {CurrentWidth}x{CurrentHeight} at {_hBin}x{_vBin} binning");
        }

        /// <summary>
        /// Set frame type (Normal or Dark)
        /// </summary>
        public bool SetFrameType(FLI_FRAME_TYPE frameType)
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetFrameType(frameType);
                Debug.WriteLine($"[FLICamera] Frame type: {frameType}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetFrameType failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set camera readout mode
        /// </summary>
        public bool SetCameraMode(int modeIndex)
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetCameraMode(modeIndex);
                Debug.WriteLine($"[FLICamera] Camera mode: {modeIndex} ({(_cameraModes.ContainsKey(modeIndex) ? _cameraModes[modeIndex] : "?")})");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetCameraMode failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set bit depth (8-bit or 16-bit)
        /// </summary>
        public bool SetBitDepth(FLI_BIT_DEPTH bitDepth)
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetBitDepth(bitDepth);
                Debug.WriteLine($"[FLICamera] Bit depth: {bitDepth}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetBitDepth failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Temperature & Cooler

        /// <summary>
        /// Get current CCD temperature in degrees Celsius
        /// </summary>
        public double? GetTemperature()
        {
            if (!_isConnected) return null;
            try
            {
                return _fli.GetTemperature();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] GetTemperature failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get cooler power percentage
        /// </summary>
        public double? GetCoolerPower()
        {
            if (!_isConnected) return null;
            try
            {
                return _fli.GetCoolerPower();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] GetCoolerPower failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Set target cooler temperature in degrees Celsius
        /// </summary>
        public bool SetTargetTemperature(double tempCelsius)
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetTemperature(tempCelsius);
                _targetTemp = tempCelsius;
                Debug.WriteLine($"[FLICamera] Target temperature set to {tempCelsius}�C");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetTargetTemperature failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Turn cooler off (set to max temp = off)
        /// </summary>
        public bool SetCoolerOff()
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetTemperature(COOLER_MAX_TEMP);
                Debug.WriteLine("[FLICamera] Cooler OFF");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetCoolerOff failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set fan speed
        /// </summary>
        public bool SetFanSpeed(FLI_FAN_SPEED speed)
        {
            if (!_isConnected) return false;
            try
            {
                _fli.SetFanSpeed(speed);
                Debug.WriteLine($"[FLICamera] Fan: {speed}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] SetFanSpeed failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Status

        /// <summary>
        /// Get camera device status as a human-readable string
        /// (From CCD_MODULE.GetDeviceStatus pattern)
        /// </summary>
        public string GetDeviceStatusString()
        {
            if (!_isConnected) return "Disconnected";
            try
            {
                int statusVal = _fli.GetDeviceStatus();
                uint masked = (uint)statusVal & (uint)FLI_STATUS.CAMERA_STATUS_MASK;

                return masked switch
                {
                    (uint)FLI_STATUS.CAMERA_STATUS_IDLE => "Idle",
                    (uint)FLI_STATUS.CAMERA_STATUS_WAITING_FOR_TRIGGER => "Waiting",
                    (uint)FLI_STATUS.CAMERA_STATUS_EXPOSING => "Exposing",
                    (uint)FLI_STATUS.CAMERA_STATUS_READING_CCD => "Reading CCD",
                    _ => $"Unknown (0x{statusVal:X8})"
                };
            }
            catch
            {
                return "Error";
            }
        }

        /// <summary>
        /// Cancel current exposure
        /// </summary>
        public bool CancelExposure()
        {
            if (!_isConnected) return false;
            try
            {
                _fli.CancelExposure();
                Debug.WriteLine("[FLICamera] Exposure cancelled");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera ERROR] CancelExposure failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Image Capture

        /// <summary>
        /// Capture single image asynchronously
        /// </summary>
        public async Task<CameraImageData> CaptureImageAsync(CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => CaptureImage(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Capture single image (blocking)
        /// FLI workflow: SetExposureTime ? ExposeFrame ? poll IsDownloadReady ? GrabRow row-by-row
        /// </summary>
        public CameraImageData CaptureImage(CancellationToken cancellationToken = default)
        {
            lock (_captureLock)
            {
                if (!_isConnected)
                {
                    Debug.WriteLine("[FLICamera ERROR] Camera not connected");
                    return null;
                }

                try
                {
                    var readout = GetReadoutDimensionsSafe();
                    int width = readout.EffectiveWidth;
                    int height = readout.EffectiveHeight;
                    int readoutHBin = readout.HBin;
                    int readoutVBin = readout.VBin;
                    int nativeRowWidth = Math.Max(readout.RawWidth, ActiveSensorWidth);

                    Debug.WriteLine($"[FLICamera] Starting exposure: {_exposureTime_ms} ms");
                    Debug.WriteLine($"[FLICamera]   Image size: {width}x{height}, Bin={_hBin}x{_vBin}");
                    Debug.WriteLine($"[FLICamera]   Native row buffer: {nativeRowWidth}, ReadoutDims={readout.RawWidth}x{readout.RawHeight}, Effective={width}x{height}, HW Bin={readoutHBin}x{readoutVBin}");

                    // Set exposure time and start
                    _fli.SetExposureTime(_exposureTime_ms);
                    _fli.SetFrameType(FLI_FRAME_TYPE.NORMAL);
                    _fli.ExposeFrame();

                    Debug.WriteLine("[FLICamera] Exposure started, waiting for download ready...");

                    // Poll until download is ready
                    int timeout_ms = _exposureTime_ms + 10000; // exposure + 10s buffer
                    int elapsed_ms = 0;
                    int pollInterval_ms = 100;

                    while (elapsed_ms < timeout_ms)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _fli.CancelExposure();
                            Debug.WriteLine("[FLICamera] Exposure cancelled by user");
                            return null;
                        }

                        if (_fli.IsDownloadReady(out int remaining))
                        {
                            Debug.WriteLine($"[FLICamera] Download ready after {elapsed_ms} ms");
                            break;
                        }

                        if (elapsed_ms % 1000 == 0 && elapsed_ms > 0)
                        {
                            Debug.WriteLine($"[FLICamera]   Remaining: {remaining} ms");
                        }

                        Thread.Sleep(pollInterval_ms);
                        elapsed_ms += pollInterval_ms;
                    }

                    if (elapsed_ms >= timeout_ms)
                    {
                        Debug.WriteLine($"[FLICamera ERROR] Exposure timeout after {elapsed_ms} ms");
                        _fli.CancelExposure();
                        return null;
                    }

                    // Download image row-by-row (16-bit)
                    // FLI cameras always output 16-bit data
                    Debug.WriteLine($"[FLICamera] Downloading image {width}x{height}...");

                    int bytesPerPixel = 2; // 16-bit
                    byte[] imageData = new byte[width * height * bytesPerPixel];
                    ushort[] rowBuffer = new ushort[nativeRowWidth];

                    for (int y = 0; y < height; y++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Debug.WriteLine("[FLICamera] Download cancelled");
                            return null;
                        }

                        Array.Clear(rowBuffer, 0, rowBuffer.Length);
                        _fli.GrabRow(rowBuffer, nativeRowWidth);

                        // Convert ushort[] to byte[] in the image buffer
                        Buffer.BlockCopy(rowBuffer, 0, imageData, y * width * bytesPerPixel, width * bytesPerPixel);
                    }

                    Debug.WriteLine($"[FLICamera] Image downloaded: {imageData.Length / (1024.0 * 1024.0):F2} MB");

                    // Create result
                    var result = new CameraImageData
                    {
                        ImageData = imageData,
                        Width = width,
                        Height = height,
                        ImageType = ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16, // FLI is always 16-bit
                        Binning = _hBin,
                        ExposureTime_us = _exposureTime_ms * 1000L,
                        Gain = 0,  // FLI CCD has no digital gain
                        Offset = 0,
                        CaptureTime = NtpTimeService.Instance.GetCurrentTime(),
                        Temperature_C = GetTemperature()
                    };

                    Debug.WriteLine($"[FLICamera] ? Image captured successfully");
                    Debug.WriteLine($"  Size: {result.Width}x{result.Height}, {result.ImageData.Length / (1024.0 * 1024.0):F2} MB");
                    if (result.Temperature_C.HasValue)
                        Debug.WriteLine($"  Temperature: {result.Temperature_C.Value:F1}�C");

                    return result;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FLICamera ERROR] Capture failed: {ex.Message}");
                    Debug.WriteLine($"[FLICamera ERROR] Stack: {ex.StackTrace}");
                    return null;
                }
            }
        }

        private (int RawWidth, int RawHeight, int EffectiveWidth, int EffectiveHeight, int HBin, int VBin) GetReadoutDimensionsSafe()
        {
            try
            {
                _fli.GetReadoutDimensions(out int width, out int hoffset, out int hbin, out int height, out int voffset, out int vbin);
                int safeHBin = Math.Max(1, hbin);
                int safeVBin = Math.Max(1, vbin);

                if (width > 0 && height > 0)
                {
                    Debug.WriteLine($"[FLICamera] SDK readout dimensions: width={width}, height={height}, hoffset={hoffset}, voffset={voffset}, hbin={hbin}, vbin={vbin}");
                    int effectiveWidth = Math.Max(1, width / safeHBin);
                    int effectiveHeight = Math.Max(1, height / safeVBin);
                    return (width, height, effectiveWidth, effectiveHeight, safeHBin, safeVBin);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FLICamera WARNING] GetReadoutDimensions failed, falling back to active ROI: {ex.Message}");
            }

            return (ActiveSensorWidth, ActiveSensorHeight, CurrentWidth, CurrentHeight, _hBin, _vBin);
        }

        #endregion
    }
}
