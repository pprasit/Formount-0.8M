using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Camera;
using static ForkMount_WPF.Engine.Camera.FLICameraSDK;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// ICameraProvider wrapper for FLICameraManager
    /// Bridges FLI implementation to unified camera interface
    /// </summary>
    public class FLICameraProvider : ICameraProvider
    {
        private readonly FLICameraManager _manager;

        public FLICameraProvider(FLICameraManager manager)
        {
            _manager = manager;
        }

        public FLICameraManager Manager => _manager;

        // ===== Connection =====
        public CameraType CameraType => CameraType.FLI;
        public bool IsConnected => _manager.IsConnected;
        public string CameraName => _manager.CameraName;

        public bool Connect(int cameraIndex = 0) => _manager.Connect(cameraIndex);
        public void Disconnect() => _manager.Disconnect();

        /// <summary>
        /// Connect to FLI camera by serial number
        /// </summary>
        public bool ConnectBySerial(string serialNumber) => _manager.Connect(serialNumber);

        // ===== Camera Info =====
        public CameraInfo CurrentCameraInfo => _manager.CurrentCameraInfo;
        public int MaxWidth => _manager.MaxWidth;
        public int MaxHeight => _manager.MaxHeight;
        public double PixelSize => _manager.PixelSizeX;
        public bool HasCooler => _manager.HasCooler;
        public int CurrentWidth => _manager.CurrentWidth;
        public int CurrentHeight => _manager.CurrentHeight;
        public int CurrentBinning => _manager.CurrentHBin;

        // ===== Settings =====
        public int ExposureTime_ms
        {
            get => _manager.ExposureTime_ms;
            set => _manager.ExposureTime_ms = value;
        }

        // FLI CCD cameras do not have software Gain/Offset/ImageType controls
        public bool SupportsGain => false;
        public bool SupportsOffset => false;
        public bool SupportsImageType => false;
        public string LastErrorMessage => string.Empty;

        public bool SetExposure(int exposureTime_ms) => _manager.SetExposure(exposureTime_ms);
        public bool SetBinning(int binning) => _manager.SetBinning(binning);

        // ===== Temperature =====
        public double? GetTemperature() => _manager.GetTemperature();
        public double? GetCoolerPower() => _manager.GetCoolerPower();
        public bool SetTargetTemperature(double tempCelsius) => _manager.SetTargetTemperature(tempCelsius);
        public bool SetCoolerOff() => _manager.SetCoolerOff();

        // ===== Capture =====
        public Task<CameraImageData> CaptureImageAsync(CancellationToken cancellationToken = default)
            => _manager.CaptureImageAsync(cancellationToken);
        public CameraImageData CaptureImage(CancellationToken cancellationToken = default)
            => _manager.CaptureImage(cancellationToken);

        // ===== FLI-specific methods =====
        public bool SetFrameType(FLI_FRAME_TYPE frameType) => _manager.SetFrameType(frameType);
        public bool SetCameraMode(int modeIndex) => _manager.SetCameraMode(modeIndex);
        public bool SetFanSpeed(FLI_FAN_SPEED speed) => _manager.SetFanSpeed(speed);
        public string GetDeviceStatusString() => _manager.GetDeviceStatusString();
        public string SerialNumber => _manager.SerialNumber;
    }
}
