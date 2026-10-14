using System.Diagnostics;
using NARIT.Mount.API.Events;
using NARIT.Mount.API.Interfaces;
using NARIT.Mount.API.Models;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Utility;

namespace NARIT.Mount.API
{
    /// <summary>
    /// Per-axis controller implementation.
    /// Wraps ForkMount-WPF engine components for single-axis control.
    /// </summary>
    internal class AxisController : IAxisController
    {
        private readonly CentralizedPollingManager _pollingManager;
        private readonly AxisType _axis;
        private readonly Func<bool> _isTrackingActive;

        public AxisType Axis => _axis;

        public event EventHandler<DriveStateChangedEventArgs> DriveStateChanged;

        internal AxisController(CentralizedPollingManager pollingManager, AxisType axis, Func<bool> isTrackingActive)
        {
            _pollingManager = pollingManager;
            _axis = axis;
            _isTrackingActive = isTrackingActive;
        }

        public bool IsEnabled
        {
            get
            {
                return _axis == AxisType.Azimuth
                    ? _pollingManager.AzimuthInfo.Is_Drive_Enabled
                    : _pollingManager.AltitudeInfo.Is_Drive_Enabled;
            }
        }

        public AxisState GetState()
        {
            if (_axis == AxisType.Azimuth)
            {
                var info = _pollingManager.AzimuthInfo;
                string axisStatus = DetermineAxisStatus(
                    info.Is_Drive_Enabled,
                    info.TrackingRmsError_Arcsec,
                    GetTrackingSensitivityDeg(),
                    _isTrackingActive());
                return new AxisState
                {
                    RawPosition_Counts = info.Raw_Position,
                    RawPosition_Deg = _pollingManager.MountInfo?.Act_Azimuth_Pos_Raw ?? 0,
                    FilteredPosition_Counts = info.Filtered_Position,
                    RawVelocity_CountsPerSec = info.Raw_Velocity,
                    FilteredVelocity_CountsPerSec = info.Filtered_Velocity,
                    CommandVelocity_DegPerSec = info.Command_Velocity_Deg_Sec,
                    Current_IQ = info.Current_IQ,
                    IsDriveEnabled = info.Is_Drive_Enabled,
                    CCW_Sensor_Active = info.CCW_Sensor_Active,
                    CW_Sensor_Active = info.CW_Sensor_Active,
                    IsTracking = info.IsTracking,
                    Axis_Status = axisStatus,

                    TrackingSensitivity_Deg = GetTrackingSensitivityDeg(),
                    TrackingRmsError_Arcsec = axisStatus == "Tracking" || axisStatus == "Slewing" ? info.TrackingRmsError_Arcsec : 0,
                    TrackingSampleCount = info.TrackingSampleCount,
                    Timestamp = info.Raw_Position_Timestamp
                };
            }
            else
            {
                var info = _pollingManager.AltitudeInfo;
                string axisStatus = DetermineAxisStatus(
                    info.Is_Drive_Enabled,
                    info.TrackingRmsError_Arcsec,
                    GetTrackingSensitivityDeg(),
                    _isTrackingActive());
                return new AxisState
                {
                    RawPosition_Counts = info.Raw_Position,
                    RawPosition_Deg = _pollingManager.MountInfo?.Act_Altitude_Pos_Raw ?? 0,
                    FilteredPosition_Counts = info.Filtered_Position,
                    RawVelocity_CountsPerSec = info.Raw_Velocity,
                    FilteredVelocity_CountsPerSec = info.Filtered_Velocity,
                    CommandVelocity_DegPerSec = info.Command_Velocity_Deg_Sec,
                    Current_IQ = info.Current_IQ,
                    IsDriveEnabled = info.Is_Drive_Enabled,
                    CCW_Sensor_Active = false,
                    CW_Sensor_Active = false,
                    IsTracking = info.IsTracking,
                    Axis_Status = axisStatus,

                    TrackingSensitivity_Deg = GetTrackingSensitivityDeg(),
                    TrackingRmsError_Arcsec = axisStatus == "Tracking" || axisStatus == "Slewing" ? info.TrackingRmsError_Arcsec : 0,
                    TrackingSampleCount = info.TrackingSampleCount,
                    Timestamp = info.Raw_Position_Timestamp
                };
            }
        }

        public void Enable()
        {
            int axisNum = _axis == AxisType.Azimuth 
                ? PlatinumDrive.Axis.AZIMUTH 
                : PlatinumDrive.Axis.ALTITUDE;
            
            _pollingManager.EnqueueExtraCommand(
                $"{_axis}_Enable",
                async () =>
                {
                    string response;
                    bool success = _pollingManager.PlatinumDrive.SendRawCommand(
                        $"AX{axisNum}.MO=1", out response, 2000);
                    return success;
                });

            Debug.WriteLine($"[NARIT-API] {_axis} Enable command sent");
            DriveStateChanged?.Invoke(this, new DriveStateChangedEventArgs { Axis = _axis, IsEnabled = true });
        }

        public void Disable()
        {
            int axisNum = _axis == AxisType.Azimuth 
                ? PlatinumDrive.Axis.AZIMUTH 
                : PlatinumDrive.Axis.ALTITUDE;
            
            _pollingManager.EnqueueExtraCommand(
                $"{_axis}_Disable",
                async () =>
                {
                    string response;
                    bool success = _pollingManager.PlatinumDrive.SendRawCommand(
                        $"AX{axisNum}.MO=0", out response, 2000);
                    return success;
                });

            Debug.WriteLine($"[NARIT-API] {_axis} Disable command sent");
            DriveStateChanged?.Invoke(this, new DriveStateChangedEventArgs { Axis = _axis, IsEnabled = false });
        }

        public void GotoPosition(double positionDeg, double velocityDegPerSec)
        {
            var config = _axis == AxisType.Azimuth
                ? (object)AppSettings.GetAzimuthAxisConfig()
                : AppSettings.GetAltitudeAxisConfig();

            double encoderResolution;
            double positionOffsetDeg;

            if (_axis == AxisType.Azimuth)
            {
                var azConfig = (AzimuthAxisConfig)config;
                encoderResolution = azConfig.Encoder_Resolution;
                positionOffsetDeg = azConfig.Position_Offset_Deg;
            }
            else
            {
                var altConfig = (AltitudeAxisConfig)config;
                encoderResolution = altConfig.Encoder_Resolution;
                positionOffsetDeg = altConfig.Position_Offset_Deg;
            }

            double positionCounts = (positionDeg - positionOffsetDeg) / 360.0 * encoderResolution;
            double velocityCounts = velocityDegPerSec / 360.0 * encoderResolution;

            _pollingManager.EnqueueExtraCommand(
                $"{_axis}_GotoPosition",
                async () =>
                {
                    int axisNum = _axis == AxisType.Azimuth 
                        ? PlatinumDrive.Axis.AZIMUTH 
                        : PlatinumDrive.Axis.ALTITUDE;
                    
                    string response;
                    // Set target position (PA = Position Absolute)
                    bool paSuccess = _pollingManager.PlatinumDrive.SendRawCommand(
                        $"AX{axisNum}.PA={(long)positionCounts}", out response, 500);
                    // Set velocity (SP = Speed)
                    bool spSuccess = _pollingManager.PlatinumDrive.SendRawCommand(
                        $"AX{axisNum}.SP={(long)Math.Abs(velocityCounts)}", out response, 500);
                    // Begin motion (BG)
                    bool bgSuccess = _pollingManager.PlatinumDrive.SendRawCommand(
                        $"AX{axisNum}.BG", out response, 500);
                    
                    return paSuccess && spSuccess && bgSuccess;
                });

            Debug.WriteLine($"[NARIT-API] {_axis} GotoPosition: {positionDeg:F4}° @ {velocityDegPerSec:F2}°/s");
        }

        public void MoveVelocity(double velocityDegPerSec)
        {
            if (_axis == AxisType.Azimuth)
                _pollingManager.SetAzimuthCommandVelocity(velocityDegPerSec);
            else
                _pollingManager.SetAltitudeCommandVelocity(velocityDegPerSec);

            Debug.WriteLine($"[NARIT-API] {_axis} MoveVelocity: {velocityDegPerSec:F4}°/s");
        }

        public void Jog(double velocityDegPerSec)
        {
            MoveVelocity(velocityDegPerSec);
        }

        public void Stop()
        {
            int axisNum = _axis == AxisType.Azimuth 
                ? PlatinumDrive.Axis.AZIMUTH 
                : PlatinumDrive.Axis.ALTITUDE;
            
            // Set velocity to zero immediately
            if (_axis == AxisType.Azimuth)
                _pollingManager.SetAzimuthCommandVelocity(0.0);
            else
                _pollingManager.SetAltitudeCommandVelocity(0.0);
            
            // Send stop command to drive
            _pollingManager.EnqueueExtraCommand(
                $"{_axis}_Stop",
                async () =>
                {
                    string response;
                    bool success = _pollingManager.PlatinumDrive.SendRawCommand(
                        $"AX{axisNum}.ST", out response, 500);
                    return success;
                });

            Debug.WriteLine($"[NARIT-API] {_axis} Stop command sent");
        }

        private double GetTrackingSensitivityDeg()
        {
            return _axis == AxisType.Azimuth
                ? AppSettings.GetAzimuthAxisConfig().Tracking_Sensitivity_Deg
                : AppSettings.GetAltitudeAxisConfig().Tracking_Sensitivity_Deg;
        }

        private static string DetermineAxisStatus(
            bool isDriveEnabled,
            double trackingRmsErrorArcsec,
            double trackingSensitivityDeg,
            bool isTrackingActive)
        {
            if (!isDriveEnabled)
                return "Disabled";

            if (!isTrackingActive)
                return "Idle";

            double trackingSensitivityArcsec = Math.Max(0, trackingSensitivityDeg) * 3600.0;
            return trackingRmsErrorArcsec <= trackingSensitivityArcsec
                ? "Tracking"
                : "Slewing";
        }
    }
}