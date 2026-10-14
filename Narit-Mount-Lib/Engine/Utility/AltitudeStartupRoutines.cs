using System;
using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Simplified Startup Routines for Altitude Platinum Drive (Axis 2)
    /// WITHOUT Sensor Homing (no sensors connected)
    /// Uses Absolute Encoder 32-bit with simple Offset and Count-to-Degree conversion
    /// </summary>
    public class AltitudeStartupRoutines
    {
        #region Constants
        private const int STABILIZATION_DELAY_MS = 500;
        #endregion

        #region Fields
        private readonly AltitudeManager _drive;
        private readonly Action<string, LogType> _logCallback;
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="drive">AltitudeDrive instance</param>
        /// <param name="logCallback">Log callback function</param>
        public AltitudeStartupRoutines(AltitudeManager drive, Action<string, LogType> logCallback)
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
                Log("Altitude (Platinum): Initializing system...", LogType.Message);

                // 1. Check connection
                if (!_drive.IsConnected)
                {
                    Log("Altitude: Drive is not connected", LogType.Error);
                    return false;
                }

                // 2. Check and Enable Drive
                if (!await CheckAndEnableDrive(cancellationToken))
                {
                    Log("Altitude: Failed to enable drive", LogType.Error);
                    return false;
                }

                // 3. Wait for stabilization
                await Task.Delay(STABILIZATION_DELAY_MS, cancellationToken);

                // 4. Read and log initial position
                if (!await ReadAndLogInitialPosition(cancellationToken))
                {
                    Log("Altitude: Failed to read initial position", LogType.Error);
                    return false;
                }

                // 5. Set initial reference (optional, based on current position)
                if (!await SetInitialReference(cancellationToken))
                {
                    Log("Altitude: Failed to set initial reference", LogType.Error);
                    return false;
                }

                Log("Altitude (Platinum): System ready!", LogType.Message);
                return true;
            }
            catch (OperationCanceledException)
            {
                Log("Altitude: Initialization cancelled", LogType.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log($"Altitude: Initialization error: {ex.Message}", LogType.Error);
                return false;
            }
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Check Drive Enable status and enable if needed
        /// </summary>
        private async Task<bool> CheckAndEnableDrive(CancellationToken cancellationToken)
        {
            try
            {
                Log("Altitude: Checking drive enable status...", LogType.Message);

                // Read current enable status
                bool isEnabled = _drive.ReadDriveEnabled();

                if (isEnabled)
                {
                    Log("Altitude: Drive is already enabled", LogType.Message);
                    return true;
                }

                // Enable Drive using MO[2]=1
                Log("Altitude: Enabling drive (MO[2]=1)...", LogType.Message);

                string response;
                bool success = _drive.PlatinumDrive.SendRawCommand("MO[2]=1", out response, 2000);

                if (!success)
                {
                    Log($"Altitude: Failed to enable drive. Response: {response}", LogType.Error);
                    return false;
                }

                // Wait for drive to enable
                await Task.Delay(300, cancellationToken);

                // Verify enable status
                isEnabled = _drive.ReadDriveEnabled();

                if (isEnabled)
                {
                    Log("Altitude: Drive enabled successfully", LogType.Message);
                    return true;
                }
                else
                {
                    Log("Altitude: Drive enable verification failed", LogType.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Altitude: Error in CheckAndEnableDrive: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Read and log initial position from encoder
        /// </summary>
        private async Task<bool> ReadAndLogInitialPosition(CancellationToken cancellationToken)
        {
            try
            {
                Log("Altitude: Reading initial position...", LogType.Message);

                double rawPosition = _drive.ReadPosition();
                Log($"Altitude: Raw Position = {rawPosition:F0} counts", LogType.Message);

                // Use Encoder_Resolution from AppSettings (supports both 27-bit and 32-bit encoders)
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                long encoderResolution = axisConfig.Encoder_Resolution;

                // Log encoder range info
                double percentage = (rawPosition / encoderResolution) * 100.0;
                Log($"Altitude: Encoder Resolution: {encoderResolution} counts/rev ({Math.Log2(encoderResolution):F0}-bit)", LogType.Message);
                Log($"Altitude: Encoder usage = {percentage:F2}% of range", LogType.Message);

                return true;
            }
            catch (Exception ex)
            {
                Log($"Altitude: Error reading initial position: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Set initial reference position
        /// For absolute encoders, this is typically just logging current position
        /// </summary>
        private async Task<bool> SetInitialReference(CancellationToken cancellationToken)
        {
            try
            {
                Log("Altitude: Setting initial reference...", LogType.Message);

                // For absolute encoder, we just accept current position as reference
                double currentPosition = _drive.ReadPosition();
                
                Log($"Altitude: Reference position set to {currentPosition:F0} counts", LogType.Message);
                Log("Altitude: Ready for operation", LogType.Message);

                return true;
            }
            catch (Exception ex)
            {
                Log($"Altitude: Error setting initial reference: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Log message to callback
        /// </summary>
        private void Log(string message, LogType logType)
        {
            _logCallback?.Invoke(message, logType);
        }

        #endregion
    }
}
