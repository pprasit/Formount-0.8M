using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Camera;
using static ForkMount_WPF.Engine.Camera.ZWOASICameraSDK;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// ICameraProvider wrapper for ZWOASIManager
    /// Bridges existing ZWO implementation to unified camera interface
    /// </summary>
    public class ZWOCameraProvider : ICameraProvider
    {
        private readonly ZWOASIManager _manager;

        public ZWOCameraProvider(ZWOASIManager manager)
        {
            _manager = manager;
        }

        public ZWOASIManager Manager => _manager;

        // ===== Connection =====
        public CameraType CameraType => CameraType.ZWO_ASI;
        public bool IsConnected => _manager.IsConnected;
        public string CameraName => _manager.CameraName;

        public bool Connect(int cameraIndex = 0) => _manager.Connect(cameraIndex);
        public void Disconnect() => _manager.Disconnect();

        // ===== Camera Info =====
        public CameraInfo CurrentCameraInfo => _manager.CurrentCameraInfo;
        public int MaxWidth => _manager.MaxWidth;
        public int MaxHeight => _manager.MaxHeight;
        public double PixelSize => _manager.PixelSize;
        public bool HasCooler => _manager.HasCooler;
        public int CurrentWidth => _manager.CurrentWidth;
        public int CurrentHeight => _manager.CurrentHeight;
        public int CurrentBinning => _manager.CurrentBinning;

        // ===== Settings =====
        public int ExposureTime_ms
        {
            get => _manager.ExposureTime_us / 1000;
            set => _manager.ExposureTime_us = value * 1000;
        }

        public bool SupportsGain => true;
        public bool SupportsOffset => true;
        public bool SupportsImageType => true;
        public string LastErrorMessage => _manager.LastErrorMessage;

        public bool SetExposure(int exposureTime_ms) => _manager.SetExposure(exposureTime_ms * 1000);
        public bool SetBinning(int binning) => _manager.SetROI(_manager.MaxWidth, _manager.MaxHeight, binning);

        // ===== Temperature =====
        public double? GetTemperature() => _manager.GetTemperature();
        public double? GetCoolerPower() => _manager.GetCoolerPower();
        public bool SetTargetTemperature(double tempCelsius) => _manager.SetTargetTemperature((int)tempCelsius);
        public bool SetCoolerOff() => _manager.SetCoolerOn(false);

        // ===== Capture =====
        public Task<CameraImageData> CaptureImageAsync(CancellationToken cancellationToken = default)
            => _manager.CaptureImageAsync(cancellationToken);
        public CameraImageData CaptureImage(CancellationToken cancellationToken = default)
            => _manager.CaptureImage(cancellationToken);

        // ===== ZWO-specific methods =====
        public int Gain
        {
            get => _manager.Gain;
            set => _manager.Gain = value;
        }

        public int Offset
        {
            get => _manager.Offset;
            set => _manager.Offset = value;
        }

        public bool SetGain(int gain) => _manager.SetGain(gain);
        public bool SetOffset(int offset) => _manager.SetOffset(offset);
        public ASI_IMG_TYPE CurrentImageType => _manager.CurrentImageType;

        public bool SetROIAndImageType(int width, int height, int binning, ASI_IMG_TYPE imageType, out ASI_ERROR_CODE errorCode)
            => _manager.SetROIAndImageType(width, height, binning, imageType, out errorCode);

        public ASI_EXPOSURE_STATUS GetExposureStatus() => _manager.GetExposureStatus();
    }
}
