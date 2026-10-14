using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElmoMotionControlComponents.Drive.EASComponents;
using ElmoMotionControlComponents.Drive.EASComponents.Locator;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Core
{
    /// <summary>
    /// Platinum Drive Controller
    /// For Elmo Drive Platinum series - Supports up to 4 axes
    /// Command Format: AXn.command (e.g., AX1.PX, AX2.VX)
    /// </summary>
    public class PlatinumDrive
    {
        #region Axis Constants
        /// <summary>
        /// Axis number constants for Platinum Drive
        /// </summary>
        public static class Axis
        {
            public const int AZIMUTH = 1;
            public const int ALTITUDE = 2;
            public const int FORK1 = 3;
            public const int FORK2 = 4;
        }
        #endregion

        #region Fields
        private IDriveCommunication _communication;
        private IDriveCommunicationInfo _communicationInfo;
        private string _serialNumber;
        private string _comPort;
        private bool _isConnected;
        private int _axisNumber = 1; // Default to Axis 1 (Azimuth Motor)
        #endregion

        #region Properties
        /// <summary>
        /// Drive Serial Number
        /// </summary>
        public string SerialNumber
        {
            get => _serialNumber;
            private set => _serialNumber = value;
        }

        /// <summary>
        /// Connected COM Port
        /// </summary>
        public string ComPort
        {
            get => _comPort;
            private set => _comPort = value;
        }

        /// <summary>
        /// Connection status
        /// </summary>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// Communication Object for Drive control
        /// </summary>
        public IDriveCommunication Communication => _communication;

        /// <summary>
        /// Current Axis Number (1-4 for Platinum Drive)
        /// Default: 1 (Azimuth Motor)
        /// </summary>
        public int AxisNumber
        {
            get => _axisNumber;
            set
            {
                if (value < 1 || value > 4)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Axis number must be between 1 and 4 for Platinum Drive");
                }
                _axisNumber = value;
            }
        }
        #endregion

        #region Constructor
        public PlatinumDrive()
        {
            _isConnected = false;
            _axisNumber = 1; // Default to Axis 1
        }

        /// <summary>
        /// Constructor with specific axis number
        /// </summary>
        /// <param name="axisNumber">Axis number (1-4)</param>
        public PlatinumDrive(int axisNumber)
        {
            _isConnected = false;
            AxisNumber = axisNumber; // Will validate range
        }
        #endregion

        #region Public Methods

        /// <summary>
        /// Scan for all Elmo Platinum Drives connected via USB
        /// </summary>
        /// <returns>Dictionary with Key=COM Port, Value=Serial Number</returns>
        public static Dictionary<string, string> ScanAvailableDrives()
        {
            var driveMap = new Dictionary<string, string>();

            try
            {
                IList<IDriveCommunicationInfo> devices = DriveLocator.GetUSBDevices();

                if (devices != null && devices.Count > 0)
                {
                    foreach (var device in devices)
                    {
                        try
                        {
                            if (device?.SerialCommInfo == null)
                            {
                                continue;
                            }

                            string comPort = device.SerialCommInfo.ComPort;

                            if (string.IsNullOrEmpty(comPort))
                            {
                                continue;
                            }

                            string serialNumber = GetDriveSerialNumber(device);

                            if (!string.IsNullOrEmpty(serialNumber))
                            {
                                driveMap[comPort] = serialNumber;
                            }
                            else
                            {
                                driveMap[comPort] = "Unknown";
                            }
                        }
                        catch (Exception deviceEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error processing device: {deviceEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error scanning drives: {ex.Message}", ex);
            }

            return driveMap;
        }

        /// <summary>
        /// Connect to Drive by Serial Number
        /// </summary>
        public bool ConnectBySerialNumber(string targetSerialNumber)
        {
            return ConnectBySerialNumber(targetSerialNumber, 0);
        }

        /// <summary>
        /// Connect to Drive by Serial Number with specified baud rate
        /// </summary>
        /// <param name="targetSerialNumber">Target drive serial number</param>
        /// <param name="baudRate">Baud rate (0 = use AppSettings default for Azimuth)</param>
        public bool ConnectBySerialNumber(string targetSerialNumber, int baudRate)
        {
            try
            {
                var availableDrives = ScanAvailableDrives();
                var matchedDrive = availableDrives.FirstOrDefault(d => d.Value == targetSerialNumber);

                if (matchedDrive.Key == null)
                {
                    // Drive not found - return false instead of throwing exception
                    System.Diagnostics.Debug.WriteLine($"[PlatinumDrive] Drive with Serial Number '{targetSerialNumber}' not found");
                    return false;
                }

                // Get baud rate - use provided value or fall back to AppSettings
                int actualBaudRate = baudRate > 0 ? baudRate : AppSettings.GetAzimuthBaudRate();
                
                return ConnectByComPort(matchedDrive.Key, actualBaudRate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlatinumDrive] Failed to connect by Serial Number: {ex.Message}");
                return false; // Return false instead of re-throwing
            }
        }

        /// <summary>
        /// Connect to Drive by COM Port with default baud rate (115200)
        /// </summary>
        public bool ConnectByComPort(string comPort)
        {
            return ConnectByComPort(comPort, 115200); // Default baud rate
        }

        /// <summary>
        /// Connect to Drive by COM Port with specified baud rate
        /// </summary>
        /// <param name="comPort">COM port (e.g., "COM10")</param>
        /// <param name="baudRate">Baud rate (e.g., 115200, 230400, 921600)</param>
        public bool ConnectByComPort(string comPort, int baudRate)
        {
            try
            {
                if (_isConnected)
                {
                    Disconnect();
                }

                _communicationInfo = DriveCommunicationFactory.CreateUSBCommunicationInfo(comPort);
                
                // Try to set baud rate using reflection (if property exists)
                try
                {
                    var baudRateProp = _communicationInfo.GetType().GetProperty("BaudRate");
                    if (baudRateProp != null && baudRateProp.CanWrite)
                    {
                        baudRateProp.SetValue(_communicationInfo, baudRate);
                        System.Diagnostics.Debug.WriteLine($"[PlatinumDrive] Set baud rate to {baudRate}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[PlatinumDrive] WARNING: BaudRate property not found - using default");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlatinumDrive] WARNING: Could not set baud rate: {ex.Message}");
                }
                
                _communication = DriveCommunicationFactory.CreateCommunication(_communicationInfo);

                IDriveErrorObject error;
                if (!_communication.Connect(out error))
                {
                    string errorMsg = error != null ? error.ToString() : "Unknown error";
                    throw new Exception($"Connection failed: {errorMsg}");
                }

                _comPort = comPort;
                _isConnected = true;
                _serialNumber = ReadSerialNumber();

                return true;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                throw new Exception($"Failed to connect to {comPort} @ {baudRate} baud: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Disconnect from Drive
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_communication != null && _isConnected)
                {
                    _communication.Disconnect();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error disconnecting: {ex.Message}", ex);
            }
            finally
            {
                _isConnected = false;
                _comPort = null!;
                _serialNumber = null!;
            }
        }

        /// <summary>
        /// Send command to Drive (automatically adds axis number for Platinum)
        /// </summary>
        /// <param name="command">Command without axis prefix (e.g., "PX" or "MO=1")</param>
        /// <param name="response">Response from drive</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <returns>True if successful</returns>
        public bool SendCommand(string command, out string response, int timeout = 2000)
        {
            response = null;

            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                // For Platinum Drive, send command with axis prefix: AXn.command
                string platinumCommand = $"AX{_axisNumber}.{command}";
                
                IDriveErrorObject error;
                bool success = _communication.SendCommandAnalyzeError(platinumCommand, out response, out error, timeout);

                if (!success && error != null)
                {
                    throw new Exception($"Command failed: {error}");
                }

                return success;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error sending command '{command}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Send raw command without axis formatting
        /// Use this for pre-formatted commands or drive-level commands
        /// Example: "AX1.PX", "AX2.VX", "SN[4]"
        /// </summary>
        /// <param name="rawCommand">Raw command to send (with [axis] if needed)</param>
        /// <param name="response">Response from drive</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <returns>True if successful</returns>
        public bool SendRawCommand(string rawCommand, out string response, int timeout = 2000)
        {
            response = null;

            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                IDriveErrorObject error;
                bool success = _communication.SendCommandAnalyzeError(rawCommand, out response, out error, timeout);

                if (!success && error != null)
                {
                    throw new Exception($"Command failed: {error}");
                }

                return success;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error sending raw command '{rawCommand}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Send raw command asynchronously (non-blocking)
        /// Allows concurrent execution of multiple commands
        /// </summary>
        /// <param name="rawCommand">Raw command to send (with [axis] if needed)</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <returns>Task with (success, response) tuple</returns>
        public async Task<(bool success, string response)> SendRawCommandAsync(string rawCommand, int timeout = 2000)
        {
            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                // Execute command in background thread
                return await Task.Run(() =>
                {
                    string response = null;
                    try
                    {
                        IDriveErrorObject error;
                        bool success = _communication.SendCommandAnalyzeError(rawCommand, out response, out error, timeout);

                        if (!success && error != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Drive Async] Command '{rawCommand}' failed: {error}");
                        }

                        return (success, response);
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Drive Async] Invalid operation while sending '{rawCommand}': {ex.Message}");
                        return (false, response);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Drive Async] Exception while sending '{rawCommand}': {ex.Message}");
                        return (false, response);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Drive Async] Outer error sending '{rawCommand}': {ex.Message}");
                return (false, null);
            }
        }

        /// <summary>
        /// Read Position from Drive (single read)
        /// </summary>
        /// <param name="axisNumber">Optional axis number (defaults to current axis)</param>
        /// <returns>Position in counts</returns>
        public double ReadPosition(int? axisNumber = null)
        {
            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                int targetAxis = axisNumber ?? _axisNumber;

                // Use AXn.PX command to read Position
                if (SendRawCommand($"AX{targetAxis}.PX", out response, 500))
                {
                    if (double.TryParse(response?.Trim(), out double position))
                    {
                        return position;
                    }
                }

                throw new Exception("Failed to read position");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error reading position: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Read Velocity from Drive (single read)
        /// </summary>
        /// <param name="axisNumber">Optional axis number (defaults to current axis)</param>
        /// <returns>Velocity in counts/sec</returns>
        public double ReadVelocity(int? axisNumber = null)
        {
            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                int targetAxis = axisNumber ?? _axisNumber;

                // Use AXn.VX command to read Velocity
                if (SendRawCommand($"AX{targetAxis}.VX", out response, 500))
                {
                    if (double.TryParse(response?.Trim(), out double velocity))
                    {
                        return velocity;
                    }
                }

                throw new Exception("Failed to read velocity");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error reading velocity: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Read Current IQ from Drive (single read)
        /// </summary>
        /// <param name="axisNumber">Optional axis number (defaults to current axis)</param>
        /// <returns>Current IQ in Amps</returns>
        public double ReadCurrentIQ(int? axisNumber = null)
        {
            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                int targetAxis = axisNumber ?? _axisNumber;

                // Use AXn.IQ command to read Current
                if (SendRawCommand($"AX{targetAxis}.IQ", out response, 500))
                {
                    if (double.TryParse(response?.Trim(), out double current))
                    {
                        return current;
                    }
                }

                throw new Exception("Failed to read current");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error reading current: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Read Drive Enable status
        /// </summary>
        /// <param name="axisNumber">Optional axis number (defaults to current axis)</param>
        /// <returns>True if drive is enabled</returns>
        public bool ReadDriveEnabled(int? axisNumber = null)
        {
            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                int targetAxis = axisNumber ?? _axisNumber;

                // Use AXn.MO command to check Motor On status
                if (SendRawCommand($"AX{targetAxis}.MO", out response, 500))
                {
                    return response?.Trim() == "1";
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #region Async Read Methods (Non-blocking, Concurrent)

        /// <summary>
        /// Read Position from Drive asynchronously (non-blocking)
        /// </summary>
        /// <param name="axisNumber">Axis number</param>
        /// <returns>Task with position in counts (or null if failed)</returns>
        public async Task<double?> ReadPositionAsync(int axisNumber)
        {
            try
            {
                // [REVERTED] Back to AX{axis}.PX format - PX[axis] didn't work for Axis 2
                // Axis 2 returns error: PX[2]\n?; (Drive doesn't recognize bracket notation)
                var (success, response) = await SendRawCommandAsync($"AX{axisNumber}.PX", 500);
                
                // [DISABLED] Temporary logging disabled - too verbose in production
                // if (!success)
                // {
                //     System.Diagnostics.Debug.WriteLine($"[ReadPosAsync][Axis {axisNumber}] FAILED - success=false, response='{response}'");
                // }
                
                if (success && double.TryParse(response?.Trim(), out double position))
                {
                    // [DISABLED] Log disabled - too verbose, use chart instead
                    // if (DateTime.Now.Millisecond % 50 < 5)
                    // {
                    //     System.Diagnostics.Debug.WriteLine($"[ReadPosAsync][Axis {axisNumber}] Position={position:F0}");
                    // }
                    return position;
                }
                
                // Only log parse errors (rare)
                // System.Diagnostics.Debug.WriteLine($"[ReadPosAsync][Axis {axisNumber}] Parse failed - response='{response}'");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReadPosAsync][Axis {axisNumber}] Exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read Velocity from Drive asynchronously (non-blocking)
        /// </summary>
        /// <param name="axisNumber">Axis number</param>
        /// <returns>Task with velocity in counts/sec (or null if failed)</returns>
        public async Task<double?> ReadVelocityAsync(int axisNumber)
        {
            try
            {
                var (success, response) = await SendRawCommandAsync($"AX{axisNumber}.VX", 500);
                
                // Removed verbose logging
                // System.Diagnostics.Debug.WriteLine($"[ReadVelAsync][Axis {axisNumber}] Success={success}, Response='{response}'");
                
                if (success && double.TryParse(response?.Trim(), out double velocity))
                {
                    // System.Diagnostics.Debug.WriteLine($"[ReadVelAsync][Axis {axisNumber}] Parsed Velocity={velocity:F0}");
                    return velocity;
                }
                
                // System.Diagnostics.Debug.WriteLine($"[ReadVelAsync][Axis {axisNumber}] FAILED to parse response");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReadVelAsync][Axis {axisNumber}] Exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read Current IQ from Drive asynchronously (non-blocking)
        /// </summary>
        /// <param name="axisNumber">Axis number</param>
        /// <returns>Task with current in Amperes (or null if failed)</returns>
        public async Task<double?> ReadCurrentIQAsync(int axisNumber)
        {
            try
            {
                var (success, response) = await SendRawCommandAsync($"AX{axisNumber}.IQ", 500);
                
                if (success && double.TryParse(response?.Trim(), out double current))
                {
                    return current;
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read Drive Enable status asynchronously (non-blocking)
        /// </summary>
        /// <param name="axisNumber">Axis number</param>
        /// <returns>Task with enable status (or null if failed)</returns>
        public async Task<bool?> ReadDriveEnabledAsync(int axisNumber)
        {
            try
            {
                var (success, response) = await SendRawCommandAsync($"AX{axisNumber}.MO", 500);
                
                if (success)
                {
                    return response?.Trim() == "1";
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read encoder resolution directly from drive parameter CA[18] (counts per revolution).
        /// Falls back to CA18 for compatibility with older command variants.
        /// </summary>
        /// <param name="axisNumber">Optional axis number (defaults to current axis)</param>
        /// <returns>Encoder resolution in counts per revolution, or null if read fails</returns>
        public long? ReadEncoderResolution(int? axisNumber = null)
        {
            try
            {
                int targetAxis = axisNumber ?? _axisNumber;
                string[] candidateCommands =
                {
                    $"AX{targetAxis}.CA[18]",
                    $"AX{targetAxis}.CA18"
                };

                foreach (string command in candidateCommands)
                {
                    if (!SendRawCommand(command, out string response, 1000))
                        continue;

                    string raw = response?.Trim();
                    if (long.TryParse(raw, out long resolution))
                        return resolution;

                    if (double.TryParse(raw, out double resolutionDouble))
                        return (long)Math.Round(resolutionDouble);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        /// <summary>
        /// Enable or Disable Drive
        /// </summary>
        /// <param name="enable">True to enable (MO=1), False to disable (MO=0)</param>
        /// <param name="axisNumber">Optional axis number (defaults to current axis)</param>
        public void SetDriveEnable(bool enable, int? axisNumber = null)
        {
            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                int targetAxis = axisNumber ?? _axisNumber;
                
                // Send AXn.MO=value command
                string command = $"AX{targetAxis}.MO={(enable ? "1" : "0")}";

                // System.Diagnostics.Debug.WriteLine($"[Platinum][SetDriveEnable] Sending command: {command}");
                
                if (!SendRawCommand(command, out response, 2000))
                {
                    throw new Exception($"Failed to set drive enable: {response}");
                }

                // System.Diagnostics.Debug.WriteLine($"[Platinum][SetDriveEnable][Axis {targetAxis}] Drive {(enable ? "enabled" : "disabled")} - Response: {response}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting drive enable: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stop motion immediately (Emergency Stop)
        /// </summary>
        /// <param name="axisNumber">Optional axis number (defaults to current axis)</param>
        public void StopMotion(int? axisNumber = null)
        {
            if (!_isConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                int targetAxis = axisNumber ?? _axisNumber;

                string command = $"AX{targetAxis}.ST";
                
                if (!SendRawCommand(command, out response, 2000))
                {
                    throw new Exception($"Failed to stop motion: {response}");
                }

                // System.Diagnostics.Debug.WriteLine($"[Platinum][StopMotion][Axis {targetAxis}] Motion stopped");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error stopping motion: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// ? ???? Position ??? Counts ???? Degrees
        /// ????: Degrees = (Position_Counts / Encoder_Resolution) � 360�
        /// 
        /// ForkMount Platinum Drive:
        /// - Azimuth: 32-bit Encoder (4,294,967,296 counts/rev)
        /// - Altitude: 32-bit Encoder (4,294,967,296 counts/rev)
        /// 
        /// ???????? (32-bit):
        /// - Position = 1,073,741,824 counts
        /// - Degrees = (1,073,741,824 / 4,294,967,296) � 360 = 90.0�
        /// </summary>
        /// <param name="positionCounts">Position ??????? Counts</param>
        /// <param name="encoderResolution">Encoder Resolution (counts per revolution)</param>
        /// <returns>Position ??????? Degrees (0-360)</returns>
        public static double ConvertPositionToDegrees(double positionCounts, long encoderResolution)
        {
            // ???? counts ???? degrees: (counts / resolution) * 360
            double degrees = (positionCounts / encoderResolution) * 360.0;
            
            // Normalize to 0-360 range
            degrees = degrees % 360.0;
            if (degrees < 0) degrees += 360.0;
            
            return degrees;
        }

        /// <summary>
        /// ? ???? Velocity ??? Counts/sec ???? Degrees/sec
        /// ????: Degrees/sec = (Counts/sec / Encoder_Resolution) � 360�
        /// 
        /// ForkMount Platinum Drive (32-bit Encoder):
        /// 
        /// ????????????????:
        /// - Velocity = 11,930 counts/sec
        /// - Encoder = 4,294,967,296 counts/rev
        /// - Result = (11,930 / 4,294,967,296) � 360 = 0.001�/sec
        /// 
        /// ????????: 
        /// - ??????????? (????????) ?????????? 0.00XX�/sec
        /// - ??????????????? (10 RPM) ? 60�/sec
        /// - ??????????? (100 RPM) ? 600�/sec
        /// </summary>
        /// <param name="velocityCounts">Velocity ??????? Counts/sec</param>
        /// <param name="encoderResolution">Encoder Resolution (counts per revolution)</param>
        /// <returns>Velocity ??????? Degrees/sec</returns>
        public static double ConvertVelocityToDegrees(double velocityCounts, long encoderResolution)
        {
            // ???? counts/sec ???? degrees/sec: (counts/sec / resolution) * 360
            double degreesSec = (velocityCounts / encoderResolution) * 360.0;
            return degreesSec;
        }

        /// <summary>
        /// ? ???? Position ??????????? Degrees
        /// </summary>
        /// <param name="encoderResolution">Encoder Resolution</param>
        /// <param name="axisNumber">Optional axis number</param>
        /// <returns>Position in degrees</returns>
        public double ReadPositionDegrees(long encoderResolution, int? axisNumber = null)
        {
            double positionCounts = ReadPosition(axisNumber);
            return ConvertPositionToDegrees(positionCounts, encoderResolution);
        }

        /// <summary>
        /// ? ???? Velocity ??????????? Degrees/sec
        /// </summary>
        /// <param name="encoderResolution">Encoder Resolution</param>
        /// <param name="axisNumber">Optional axis number</param>
        /// <returns>Velocity in degrees/sec</returns>
        public double ReadVelocityDegrees(long encoderResolution, int? axisNumber = null)
        {
            double velocityCounts = ReadVelocity(axisNumber);
            return ConvertVelocityToDegrees(velocityCounts, encoderResolution);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Read Serial Number from Drive
        /// </summary>
        private string ReadSerialNumber()
        {
            try
            {
                string response;

                // SN[4] is a global command, doesn't need axis prefix
                if (SendRawCommand("SN[4]", out response, 2000))
                {
                    return response?.Trim() ?? "Unknown";
                }

                return "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string GetDriveSerialNumber(IDriveCommunicationInfo commInfo)
        {
            IDriveCommunication tempCommunication = null;
            try
            {
                tempCommunication = DriveCommunicationFactory.CreateCommunication(commInfo);
                IDriveErrorObject error;

                if (tempCommunication.Connect(out error))
                {
                    string response;
                    if (tempCommunication.SendCommandAnalyzeError("SN[4]", out response, out error, 2000))
                    {
                        return response?.Trim() ?? "Unknown";
                    }
                }

                return "Unknown";
            }
            catch
            {
                return "Unknown";
            }
            finally
            {
                try
                {
                    tempCommunication?.Disconnect();
                }
                catch { }
            }
        }

        #endregion
    }
}
