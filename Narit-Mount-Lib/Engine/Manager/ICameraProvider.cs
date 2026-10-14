using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Camera;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Camera type enumeration for multi-camera support
    /// </summary>
    public enum CameraType
    {
        ZWO_ASI,
        FLI
    }

    /// <summary>
    /// Unified camera interface for ZWO ASI and FLI cameras
    /// Allows CameraSettingsWindow and other UI to work with any camera type
    /// </summary>
    public interface ICameraProvider
    {
        // ===== Connection =====
        CameraType CameraType { get; }
        bool IsConnected { get; }
        string CameraName { get; }

        /// <summary>
        /// Connect to camera by index (0 = first camera)
        /// </summary>
        bool Connect(int cameraIndex = 0);

        /// <summary>
        /// Disconnect from camera
        /// </summary>
        void Disconnect();

        // ===== Camera Info =====
        CameraInfo CurrentCameraInfo { get; }
        int MaxWidth { get; }
        int MaxHeight { get; }
        double PixelSize { get; }
        bool HasCooler { get; }

        /// <summary>
        /// Current image width (after binning)
        /// </summary>
        int CurrentWidth { get; }

        /// <summary>
        /// Current image height (after binning)
        /// </summary>
        int CurrentHeight { get; }

        /// <summary>
        /// Current binning mode
        /// </summary>
        int CurrentBinning { get; }

        // ===== Settings =====

        /// <summary>
        /// Exposure time in milliseconds
        /// </summary>
        int ExposureTime_ms { get; set; }

        /// <summary>
        /// Whether this camera supports Gain control (ZWO: yes, FLI CCD: no)
        /// </summary>
        bool SupportsGain { get; }

        /// <summary>
        /// Whether this camera supports Offset control (ZWO: yes, FLI CCD: no)
        /// </summary>
        bool SupportsOffset { get; }

        /// <summary>
        /// Whether this camera supports image type selection (ZWO: RAW8/RAW16/RGB24, FLI: 16-bit only)
        /// </summary>
        bool SupportsImageType { get; }

        /// <summary>
        /// Most recent provider-specific error message, when available.
        /// </summary>
        string LastErrorMessage { get; }

        bool SetExposure(int exposureTime_ms);
        bool SetBinning(int binning);

        // ===== Temperature =====
        double? GetTemperature();
        double? GetCoolerPower();
        bool SetTargetTemperature(double tempCelsius);
        bool SetCoolerOff();

        // ===== Capture =====
        Task<CameraImageData> CaptureImageAsync(CancellationToken cancellationToken = default);
        CameraImageData CaptureImage(CancellationToken cancellationToken = default);
    }
}
