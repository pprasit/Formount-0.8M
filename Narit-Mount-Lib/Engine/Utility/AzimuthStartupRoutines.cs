using System;
using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Log Type Enum for logging purposes
    /// </summary>
    public enum LogType
    {
        Message,
        Warning,
        Error,
        Cmd
    }

    /// <summary>
    /// Simplified Startup Routines for Azimuth Platinum Drive
    /// WITHOUT Sensor Homing (no CW/CCW sensors connected)
    /// Uses Absolute Encoder 32-bit with simple Offset and Count-to-Degree conversion
    /// </summary>
    public class AzimuthStartupRoutines
    {
        #region Constants
        private const int STABILIZATION_DELAY_MS = 500;
        private const long MAX_32BIT_ENCODER = 4294967296; // 2^32 for 32-bit absolute encoder
        #endregion

        #region Fields
        private readonly AzimuthManager _drive;
        private readonly Action<string, LogType> _logCallback;
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="drive">AzimuthDrive instance</param>
        /// <param name="logCallback">Log callback function</param>
        public AzimuthStartupRoutines(AzimuthManager drive, Action<string, LogType> logCallback)
        {
            _drive = drive ?? throw new ArgumentNullException(nameof(drive));
            _logCallback = logCallback;
        }
        #endregion

        #region Public Methods

        /// <summary>
        /// Perform simplified startup sequence without sensor homing
        /// </summary>
        public async Task<bool> PerformSimplifiedStartupSequence(CancellationToken cancellationToken = default)
        {
            try
            {
                Log("Azimuth (Platinum): Initializing system...", LogType.Message);

                // 1. Check connection
                if (!_drive.IsConnected)
                {
                    Log("Azimuth: Drive is not connected", LogType.Error);
                    return false;
                }

                // 2. Check and Enable Drive
                if (!await CheckAndEnableDrive(cancellationToken))
                {
                    Log("Azimuth: Failed to enable drive", LogType.Error);
                    return false;
                }

                // 3. Wait for stabilization
                await Task.Delay(STABILIZATION_DELAY_MS, cancellationToken);

                // 4. Read and log initial position
                if (!await ReadAndLogInitialPosition(cancellationToken))
                {
                    Log("Azimuth: Failed to read initial position", LogType.Error);
                    return false;
                }

                // 5. Set initial reference (optional, based on current position)
                if (!await SetInitialReference(cancellationToken))
                {
                    Log("Azimuth: Failed to set initial reference", LogType.Error);
                    return false;
                }

                Log("Azimuth (Platinum): System ready!", LogType.Message);
                return true;
            }
            catch (OperationCanceledException)
            {
                Log("Azimuth: Initialization cancelled", LogType.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log($"Azimuth: Initialization error: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Convert raw encoder counts to degrees
        /// For 32-bit Absolute Encoder
        /// </summary>
        public static double ConvertCountsToDegrees(double rawCounts, long encoderResolution)
        {
            // For 32-bit encoder: 0 to 4294967295 counts = 0 to 360 degrees
            double degrees = (rawCounts / encoderResolution) * 360.0;
            return degrees;
        }

        /// <summary>
        /// Convert degrees to encoder counts
        /// For 32-bit Absolute Encoder
        /// </summary>
        public static double ConvertDegreesToCounts(double degrees, long encoderResolution)
        {
            // degrees = (counts / resolution) * 360
            // counts = (degrees * resolution) / 360
            double counts = (degrees * encoderResolution) / 360.0;
            return counts;
        }

        /// <summary>
        /// Apply position offset to raw position
        /// </summary>
        public static double ApplyPositionOffset(double rawPositionCounts, double offsetDegrees, long encoderResolution)
        {
            // Convert offset from degrees to counts
            double offsetCounts = ConvertDegreesToCounts(offsetDegrees, encoderResolution);
            
            // Add offset to raw position
            double adjustedCounts = rawPositionCounts + offsetCounts;
            
            // Apply modulo to keep within encoder range (0 to MAX_32BIT_ENCODER)
            adjustedCounts = ((adjustedCounts % encoderResolution) + encoderResolution) % encoderResolution;
            
            return adjustedCounts;
        }

        /// <summary>
        /// Get mapped position in degrees with offset applied
        /// </summary>
        public static double GetMappedPositionDegrees(double rawPositionCounts, double offsetDegrees, long encoderResolution)
        {
            // Apply offset
            double adjustedCounts = ApplyPositionOffset(rawPositionCounts, offsetDegrees, encoderResolution);
            
            // Convert to degrees
            double degrees = ConvertCountsToDegrees(adjustedCounts, encoderResolution);
            
            return degrees;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Check if drive is enabled, if not then enable it
        /// </summary>
        private async Task<bool> CheckAndEnableDrive(CancellationToken cancellationToken)
        {
            try
            {
                Log("Azimuth: Checking drive status...", LogType.Message);
                await Task.Delay(200, cancellationToken);

                // Read current drive status
                bool isDriveEnabled = _drive.ReadDriveEnabled();

                // [REMOVED] Auto-enable drive - User must enable manually via UI
                // Automatic enable was removed to prevent conflicts with manual control
                if (!isDriveEnabled)
                {
                    Log("Azimuth: WARNING: Drive is not enabled", LogType.Error);
                    Log("Azimuth: Please enable drive manually via Enable button", LogType.Message);
                }
                else
                {
                    Log("Azimuth: Drive already enabled", LogType.Message);
                }

                // [CRITICAL FIX] Set Velocity High Limit (VH) from AppSettings
                // This was the root cause - VH was limiting velocity to ~20 deg/s!
                try
                {
                    var axisConfig = AppSettings.GetAzimuthAxisConfig();
                    double maxVelocity = axisConfig.Max_Velocity_Deg_Sec;
                    
                    Log($"Azimuth: Setting VH (Velocity High Limit) to {maxVelocity:F2} deg/s...", LogType.Message);
                    
                    await Task.Run(() =>
                    {
                        _drive.SetVelocityHighLimit(maxVelocity);
                    }, cancellationToken);
                    
                    // Verify VH was set correctly
                    await Task.Delay(100, cancellationToken);
                    double currentVH = _drive.ReadVelocityHighLimit();
                    
                    Log($"Azimuth: VH verified: {currentVH:F2} deg/s", LogType.Message);
                }
                catch (Exception vhEx)
                {
                    Log($"Azimuth: Failed to set VH: {vhEx.Message}", LogType.Error);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Azimuth: Enable error: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Read initial position from encoder and log it
        /// </summary>
        private async Task<bool> ReadAndLogInitialPosition(CancellationToken cancellationToken)
        {
            try
            {
                Log("Azimuth: Reading initial position...", LogType.Message);
                await Task.Delay(100, cancellationToken);

                // Read raw position from encoder (PX)
                double rawPosition = _drive.ReadPosition();

                // Get axis configuration
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                // Use Encoder_Resolution from AppSettings (supports both 27-bit and 32-bit encoders)
                long encoderResolution = axisConfig.Encoder_Resolution;
                
                // Convert to degrees (raw, no offset)
                double rawDegrees = ConvertCountsToDegrees(rawPosition, encoderResolution);
                
                // Apply offset and get mapped position
                double mappedDegrees = GetMappedPositionDegrees(
                    rawPosition, 
                    axisConfig.Position_Offset_Deg, 
                    encoderResolution
                );

                Log($"Azimuth: Encoder Resolution: {encoderResolution} counts/rev ({Math.Log2(encoderResolution):F0}-bit)", LogType.Message);
                Log($"Azimuth: Raw Position: {rawPosition:F0} counts ({rawDegrees:F2} deg)", LogType.Message);
                Log($"Azimuth: Mapped Position (with offset): {mappedDegrees:F2} deg", LogType.Message);
                Log($"Azimuth: Position Offset: {axisConfig.Position_Offset_Deg:F2} deg", LogType.Message);

                return true;
            }
            catch (Exception ex)
            {
                Log($"Azimuth: Read position error: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Set initial reference position (optional)
        /// This can be used to set a zero reference at current position
        /// </summary>
        private async Task<bool> SetInitialReference(CancellationToken cancellationToken)
        {
            try
            {
                Log("Azimuth: Setting initial reference...", LogType.Message);
                await Task.Delay(100, cancellationToken);

                // For now, we just acknowledge the current position as valid
                // No need to set PX=0 since we're using absolute encoder
                
                Log("Azimuth: Using absolute encoder position as reference", LogType.Message);

                return true;
            }
            catch (Exception ex)
            {
                Log($"Azimuth: Set reference error: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Log helper method
        /// </summary>
        private void Log(string message, LogType type)
        {
            _logCallback?.Invoke(message, type);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Normalize angle to 0-360 range
        /// </summary>
        public static double NormalizeAngle360(double degrees)
        {
            degrees = degrees % 360.0;
            if (degrees < 0)
                degrees += 360.0;
            return degrees;
        }

        /// <summary>
        /// Normalize angle to -180 to +180 range
        /// </summary>
        public static double NormalizeAngle180(double degrees)
        {
            degrees = degrees % 360.0;
            if (degrees > 180)
                degrees -= 360.0;
            else if (degrees < -180)
                degrees += 360.0;
            return degrees;
        }

        #endregion
    }
}
