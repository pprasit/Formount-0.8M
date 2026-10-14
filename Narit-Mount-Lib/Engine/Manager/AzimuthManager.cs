using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Control;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Azimuth Drive Controller for Platinum Drive (Axis 1)
    /// Wraps PlatinumDrive with polling and data management for Azimuth axis
    /// 
    /// KEY POINTS FOR AI ASSISTANTS:
    /// ========================================
    /// 1. This wraps PlatinumDrive for AXIS 1 only (Azimuth)
    /// 2. Uses AZIMUTH_AXIS_NUMBER = 1 for all commands
    /// 3. Commands are sent as: command[1] via PlatinumDrive.SendCommandToAxis()
    /// 4. Polling rate: 5ms (200Hz) - async, non-blocking - optimized for 2-axis system
    /// 5. All methods (ReadPosition, ReadVelocity, etc.) automatically use Axis 1
    /// 
    /// Command Flow:
    ///    AzimuthManager.ReadPosition() 
    ///    -> PlatinumDrive.ReadPosition(1)
    ///    -> SendCommandToAxis("PX", 1, ...)
    ///    -> Sends: "PX[1]" to drive
    /// 
    /// DO NOT send commands without axis number!
    /// [CORRECT] Use methods in this class (they add axis automatically)
    /// [WRONG] Directly send "PX" to communication layer
    /// 
    /// ========================================
    /// VELOCITY COMMAND ARCHITECTURE POLICY:
    /// ========================================
    /// ALL velocity commands MUST follow this architecture:
    /// 
    /// 1. [SOURCE] Satellite Tracking / PID Control / Manual Control
    ///    ? Write to: _azimuthInfo.Command_Velocity_Deg_Sec (with lock!)
    ///    ? DO NOT send JP/BG directly!
    /// 
    /// 2. [SENDER] CommandSenderLoop (200Hz)
    ///    ? Read: _azimuthInfo.Command_Velocity_Deg_Sec (with lock!)
    ///    ? Send: JP + BG commands to Drive
    ///    ? Only this loop should send JP/BG commands!
    /// 
    /// This ensures:
    /// - Thread-safe velocity updates
    /// - Centralized command queue management
    /// - No race conditions or duplicate commands
    /// - Clean separation of concerns
    /// ========================================
    /// </summary>
    public class AzimuthManager
    {
        #region Fields
        private PlatinumDrive _platinumDrive;
        private const int AZIMUTH_AXIS_NUMBER = PlatinumDrive.Axis.AZIMUTH; // = 1

        // Data polling
        private AzimuthInfo _azimuthInfo;
        private CancellationTokenSource _pollingCancellationTokenSource;
        private Task _pollingTask;
        private readonly object _infoLock = new object();
        private int _pollingIntervalMs = 5; // 5ms = 200Hz polling rate (optimal for 2-axis system)
        
        // [NEW] Command Sender Loop - Sends velocity commands to Drive
        private CancellationTokenSource _commandSenderCancellationTokenSource;
        private Task _commandSenderTask;
        private int _commandSenderIntervalMs = 5; // 5ms = 200Hz command rate (optimal for 2-axis system)
        private double _lastSentVelocity = 0; // Track last sent velocity to avoid redundant commands
        private int _lastIpValue = -1; // Track last IP value for sensor change detection
        
        // [NEW] Kalman Filter for Position and Velocity smoothing
        private KalmanFilter1D _positionFilter;
        private bool _isFilterInitialized = false;
        
        // Cache axis config to avoid file I/O during motion commands
        private AzimuthAxisConfig _cachedAxisConfig;
        #endregion

        #region Events
        /// <summary>
        /// Event when Position data is updated
        /// </summary>
        public event EventHandler<double> PositionUpdated;

        /// <summary>
        /// Event when Velocity data is updated
        /// </summary>
        public event EventHandler<double> VelocityUpdated;

        /// <summary>
        /// Event when all data is updated
        /// </summary>
        public event EventHandler<AzimuthInfo> DataUpdated;

        /// <summary>
        /// Event when Polling error occurs
        /// </summary>
        public event EventHandler<string> PollingError;

        /// <summary>
        /// Event when Filter Parameters are updated
        /// </summary>
        public event EventHandler<AzimuthInfo> FilterParametersUpdated;
        #endregion

        #region Properties
        /// <summary>
        /// Drive Serial Number
        /// </summary>
        public string SerialNumber => _platinumDrive?.SerialNumber;

        /// <summary>
        /// Connected COM Port
        /// </summary>
        public string ComPort => _platinumDrive?.ComPort;

        /// <summary>
        /// Connection status
        /// </summary>
        public bool IsConnected => _platinumDrive?.IsConnected ?? false;

        /// <summary>
        /// Latest Azimuth Info (Thread-safe)
        /// </summary>
        public AzimuthInfo CurrentInfo
        {
            get
            {
                lock (_infoLock)
                {
                    return _azimuthInfo;
                }
            }
        }

        /// <summary>
        /// Polling Rate (Hz)
        /// </summary>
        public int PollingRate
        {
            get => 1000 / _pollingIntervalMs;
            set
            {
                // Range: 1-1000 Hz (1ms-1000ms) - Async polling won't block
                if (value > 0 && value <= 1000)
                {
                    _pollingIntervalMs = 1000 / value;
                }
            }
        }

        /// <summary>
        /// Polling status
        /// </summary>
        public bool IsPolling => _pollingTask != null && !_pollingTask.IsCompleted;

        /// <summary>
        /// Access to underlying Platinum Drive
        /// </summary>
        public PlatinumDrive PlatinumDrive => _platinumDrive;
        #endregion

        #region Constructor
        public AzimuthManager()
        {
            _platinumDrive = new PlatinumDrive();
            _azimuthInfo = new AzimuthInfo();
        }
        #endregion

        #region Public Methods

        /// <summary>
        /// Scan for all Elmo Platinum Drives connected via USB
        /// </summary>
        public static Dictionary<string, string> ScanAvailableDrives()
        {
            return PlatinumDrive.ScanAvailableDrives();
        }

        /// <summary>
        /// Connect to Drive by Serial Number
        /// </summary>
        public bool ConnectBySerialNumber(string targetSerialNumber)
        {
            try
            {
                bool result = _platinumDrive.ConnectBySerialNumber(targetSerialNumber);
                
                if (result)
                {
                    // Reset info on successful connection
                    lock (_infoLock)
                    {
                        _azimuthInfo.Reset();
                    }
                }
                
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AzimuthDrive] Failed to connect by Serial Number: {ex.Message}");
                return false; // Return false instead of re-throwing
            }
        }

        /// <summary>
        /// Connect to Drive by COM Port
        /// </summary>
        public bool ConnectByComPort(string comPort)
        {
            return ConnectByComPort(comPort, 0);
        }

        /// <summary>
        /// Connect to Drive by COM Port with specified baud rate
        /// </summary>
        public bool ConnectByComPort(string comPort, int baudRate)
        {
            try
            {
                bool result = baudRate > 0 
                    ? _platinumDrive.ConnectByComPort(comPort, baudRate)
                    : _platinumDrive.ConnectByComPort(comPort);
                
                if (result)
                {
                    // Reset info on successful connection
                    lock (_infoLock)
                    {
                        _azimuthInfo.Reset();
                    }
                }
                
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AzimuthDrive] Failed to connect to {comPort}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Disconnect from Drive
        /// </summary>
        public void Disconnect()
        {
            try
            {
                // Stop Polling first
                StopPolling();

                if (_platinumDrive != null)
                {
                    _platinumDrive.Disconnect();
                }

                // Reset AzimuthInfo
                lock (_infoLock)
                {
                    _azimuthInfo.Reset();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error disconnecting: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Start data polling from Drive
        /// [ENABLED] Command Sender Loop enabled - Sends JP/BG commands based on Command_Velocity_Deg_Sec
        /// [KALMAN FILTER] Initializes position/velocity filter for noise reduction
        /// </summary>
        public void StartPolling()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            if (IsPolling)
            {
                return; // Already polling
            }

            // [KALMAN FILTER] Initialize filter before starting polling
            InitializeKalmanFilter();

            // Start Data Read Loop (200Hz)
            _pollingCancellationTokenSource = new CancellationTokenSource();
            _pollingTask = Task.Run(() => PollingLoop(_pollingCancellationTokenSource.Token));
            
            // [ENABLED] Command Sender Loop - Reads Command_Velocity_Deg_Sec and sends JP/BG commands
            _commandSenderCancellationTokenSource = new CancellationTokenSource();
            _commandSenderTask = Task.Run(() => CommandSenderLoop(_commandSenderCancellationTokenSource.Token));
            
            Debug.WriteLine("[Azimuth] Started: Data Read Loop (200Hz) | Command Sender Loop (200Hz) | Kalman Filter enabled");
        }

        /// <summary>
        /// Stop data polling
        /// [ENABLED] Command Sender Loop enabled - stops JP/BG command sending
        /// </summary>
        public void StopPolling()
        {
            // Stop Data Read Loop
            if (_pollingCancellationTokenSource != null)
            {
                _pollingCancellationTokenSource.Cancel();
            }
            _pollingTask?.Wait(1000); // Wait up to 1 second
            
            // [ENABLED] Command Sender Loop - stop it properly
            if (_commandSenderCancellationTokenSource != null)
            {
                _commandSenderCancellationTokenSource.Cancel();
            }
            _commandSenderTask?.Wait(1000); // Wait up to 1 second
            
            Debug.WriteLine("[Azimuth] Stopped: Data Read Loop | Command Sender Loop");
        }

        /// <summary>
        /// Read Position from Drive (single read)
        /// </summary>
        public double ReadPosition()
        {
            return _platinumDrive.ReadPosition(AZIMUTH_AXIS_NUMBER);
        }

        /// <summary>
        /// Read Velocity from Drive (single read)
        /// </summary>
        public double ReadVelocity()
        {
            return _platinumDrive.ReadVelocity(AZIMUTH_AXIS_NUMBER);
        }

        /// <summary>
        /// Read Current IQ from Drive (single read)
        /// </summary>
        public double ReadCurrentIQ()
        {
            return _platinumDrive.ReadCurrentIQ(AZIMUTH_AXIS_NUMBER);
        }

        /// <summary>
        /// Read Drive Enable status
        /// </summary>
        public bool ReadDriveEnabled()
        {
            return _platinumDrive.ReadDriveEnabled(AZIMUTH_AXIS_NUMBER);
        }

        /// <summary>
        /// Read Digital Input status (IP command)
        /// Returns raw IP value which encodes sensor states:
        ///   0 = No sensors active
        ///   65537 = CW Sensor active (bit 16)
        ///   131073 = CCW Sensor active (bit 17)
        /// </summary>
        public int ReadDigitalInput()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                const int TIMEOUT_MS = 500;

                // Send IP[axis] command to read digital inputs
                if (_platinumDrive.SendRawCommand($"IP[{AZIMUTH_AXIS_NUMBER}]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int ipValue))
                    {
                        return ipValue;
                    }
                }

                throw new Exception("Failed to read IP parameter");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error reading Digital Input: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Update sensor status from IP value
        /// IP Values:
        ///   0 = No sensors
        ///   65537 = CW Sensor (bit 16 set)
        ///   131073 = CCW Sensor (bit 17 set)
        /// </summary>
        private void UpdateSensorStatus(int ipValue)
        {
            lock (_infoLock)
            {
                // Check bit 16 for CW Sensor
                bool cwActive = (ipValue == 65537);
                _azimuthInfo.CW_Sensor_Active = cwActive;
                
                // Check bit 17 for CCW Sensor
                bool ccwActive = (ipValue == 131073);
                _azimuthInfo.CCW_Sensor_Active = ccwActive;
                
                // Debug log (only when sensor state changes)
                if (ipValue != _lastIpValue)
                {
                    System.Diagnostics.Debug.WriteLine($"[Sensors] IP={ipValue} | CW={cwActive} | CCW={ccwActive}");
                    _lastIpValue = ipValue;
                }
            }
        }

        /// <summary>
        /// Read all Filter Parameters from Drive (KV parameters)
        /// Returns updated AzimuthInfo with filter data
        /// </summary>
        public AzimuthInfo ReadFilterParameters()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                var info = new AzimuthInfo();
                string response;
                const int TIMEOUT_MS = 500;

                Debug.WriteLine("[ReadFilterParameters] Starting to read filter parameters...");
                Debug.WriteLine("[ReadFilterParameters] Using SendRawCommand (no axis wrapper)");

                // IMPORTANT: Use SendRawCommand WITHOUT SendCommandToAxis wrapper
                // SendCommandToAxis adds [axis] automatically, causing "KV[X][1]" -> Error 19
                // SendRawCommand sends exactly what we specify
                
                // Filter #1 (Lead-Lag or Low-Pass)
                int filter1Type = 0;
                if (_platinumDrive.SendRawCommand("KV[5]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter1Type))
                        info.Filter1_Type = filter1Type;
                }

                if (filter1Type != 0)
                {
                    if (_platinumDrive.SendRawCommand("KV[1]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter1Freq))
                            info.Filter1_Frequency = filter1Freq;
                    }

                    if (_platinumDrive.SendRawCommand("KV[2]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter1Damp))
                            info.Filter1_Damping = filter1Damp;
                    }
                }

                // Filter #2 (Notch)
                int filter2Type = 0;
                if (_platinumDrive.SendRawCommand("KV[10]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter2Type))
                        info.Filter2_Type = filter2Type;
                }

                if (filter2Type != 0)
                {
                    if (_platinumDrive.SendRawCommand("KV[6]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter2Freq))
                            info.Filter2_Frequency = filter2Freq;
                    }

                    if (_platinumDrive.SendRawCommand("KV[7]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter2Q))
                            info.Filter2_QFactor = filter2Q;
                    }

                    if (_platinumDrive.SendRawCommand("KV[8]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter2Mag))
                            info.Filter2_Magnitude = filter2Mag;
                    }
                }

                // Filter #3 (Notch)
                int filter3Type = 0;
                if (_platinumDrive.SendRawCommand("KV[15]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter3Type))
                        info.Filter3_Type = filter3Type;
                }

                if (filter3Type != 0)
                {
                    if (_platinumDrive.SendRawCommand("KV[11]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter3Freq))
                            info.Filter3_Frequency = filter3Freq;
                    }

                    if (_platinumDrive.SendRawCommand("KV[12]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter3Q))
                            info.Filter3_QFactor = filter3Q;
                    }

                    if (_platinumDrive.SendRawCommand("KV[13]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter3Mag))
                            info.Filter3_Magnitude = filter3Mag;
                    }
                }

                // Filter #4 (Notch)
                int filter4Type = 0;
                if (_platinumDrive.SendRawCommand("KV[20]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter4Type))
                        info.Filter4_Type = filter4Type;
                }

                if (filter4Type != 0)
                {
                    if (_platinumDrive.SendRawCommand("KV[16]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter4Freq))
                            info.Filter4_Frequency = filter4Freq;
                    }

                    if (_platinumDrive.SendRawCommand("KV[17]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter4Q))
                            info.Filter4_QFactor = filter4Q;
                    }

                    if (_platinumDrive.SendRawCommand("KV[18]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter4Mag))
                            info.Filter4_Magnitude = filter4Mag;
                    }
                }

                // Feedback Filter #1 (Notch)
                int fbFilter1Type = 0;
                if (_platinumDrive.SendRawCommand("KV[55]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out fbFilter1Type))
                        info.FeedbackFilter1_Type = fbFilter1Type;
                }

                if (fbFilter1Type != 0)
                {
                    if (_platinumDrive.SendRawCommand("KV[51]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter1Freq))
                            info.FeedbackFilter1_Frequency = fbFilter1Freq;
                    }

                    if (_platinumDrive.SendRawCommand("KV[52]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter1Q))
                            info.FeedbackFilter1_QFactor = fbFilter1Q;
                    }

                    if (_platinumDrive.SendRawCommand("KV[53]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter1Mag))
                            info.FeedbackFilter1_Magnitude = fbFilter1Mag;
                    }
                }

                // Feedback Filter #2 (Notch)
                int fbFilter2Type = 0;
                if (_platinumDrive.SendRawCommand("KV[60]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out fbFilter2Type))
                        info.FeedbackFilter2_Type = fbFilter2Type;
                }

                if (fbFilter2Type != 0)
                {
                    if (_platinumDrive.SendRawCommand("KV[56]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter2Freq))
                            info.FeedbackFilter2_Frequency = fbFilter2Freq;
                    }

                    if (_platinumDrive.SendRawCommand("KV[57]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter2Q))
                            info.FeedbackFilter2_QFactor = fbFilter2Q;
                    }

                    if (_platinumDrive.SendRawCommand("KV[58]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter2Mag))
                            info.FeedbackFilter2_Magnitude = fbFilter2Mag;
                    }
                }

                info.UpdateFilterParametersTimestamp();
                
                Debug.WriteLine($"[ReadFilterParameters] SUCCESS!");
                Debug.WriteLine($"  Filter#1: Type={info.Filter1_Type}, Freq={info.Filter1_Frequency}, Damp={info.Filter1_Damping}");
                Debug.WriteLine($"  Filter#2: Type={info.Filter2_Type}, Freq={info.Filter2_Frequency}");
                Debug.WriteLine($"  Filter#3: Type={info.Filter3_Type}, Freq={info.Filter3_Frequency}");
                Debug.WriteLine($"  Filter#4: Type={info.Filter4_Type}, Freq={info.Filter4_Frequency}");
                Debug.WriteLine($"  FbFilter#1: Type={info.FeedbackFilter1_Type}, Freq={info.FeedbackFilter1_Frequency}");
                Debug.WriteLine($"  FbFilter#2: Type={info.FeedbackFilter2_Type}, Freq={info.FeedbackFilter2_Frequency}");
                
                // Raise FilterParametersUpdated event
                FilterParametersUpdated?.Invoke(this, info);
                
                return info;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ReadFilterParameters] ERROR: {ex.Message}");
                throw new Exception($"Error reading filter parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Read Velocity High Limit (VH) from Drive
        /// VH limits the maximum velocity that can be commanded via JP
        /// </summary>
        public double ReadVelocityHighLimit()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                const int TIMEOUT_MS = 1000;

                if (PlatinumDrive.SendRawCommand($"VH[{AZIMUTH_AXIS_NUMBER}]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double vhCounts))
                    {
                        var axisConfig = AppSettings.GetAzimuthAxisConfig();
                        double vhDeg = (vhCounts / axisConfig.Encoder_Resolution) * 360.0;
                        
                        Debug.WriteLine($"[ReadVH] VH[{AZIMUTH_AXIS_NUMBER}] = {vhCounts:F0} counts = {vhDeg:F2} deg/s");
                        return vhDeg;
                    }
                }

                throw new Exception("Failed to read VH parameter");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error reading VH: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Set Velocity High Limit (VH) to Drive
        /// VH limits the maximum velocity that can be commanded via JP
        /// Should be called during initialization or when Max_Velocity_Deg_Sec changes
        /// </summary>
        public void SetVelocityHighLimit(double velocityDegSec)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                double velocityCounts = (velocityDegSec / 360.0) * axisConfig.Encoder_Resolution;

                // [CRITICAL FIX] Increase hardware limit to support higher velocities
                // For 32-bit encoder (4,294,967,296 counts/rev):
                //   100 deg/s = 1,193,046,471 counts/s
                //   360 deg/s = 4,294,967,296 counts/s (full revolution per second)
                // Safe maximum: 2e9 counts/s (~167 deg/s) to avoid overflow
                const double MAX_VELOCITY_COUNTS = 2e9; // 2 billion counts/s (~167 deg/s)
                
                if (Math.Abs(velocityCounts) > MAX_VELOCITY_COUNTS)
                {
                    double maxDegSec = (MAX_VELOCITY_COUNTS / axisConfig.Encoder_Resolution) * 360.0;
                    Debug.WriteLine($"[SetVH] WARNING: Velocity too high ({velocityDegSec:F2} deg/s)");
                    Debug.WriteLine($"[SetVH] Maximum safe velocity: {maxDegSec:F2} deg/s ({MAX_VELOCITY_COUNTS:E2} counts/s)");
                    Debug.WriteLine($"[SetVH] Clamping to maximum...");
                    velocityCounts = Math.Sign(velocityCounts) * MAX_VELOCITY_COUNTS;
                    velocityDegSec = maxDegSec; // Update for logging
                }

                string response;
                const int TIMEOUT_MS = 2000;

                string vhCmd = $"VH[{AZIMUTH_AXIS_NUMBER}]={(long)Math.Abs(velocityCounts)}";
                Debug.WriteLine($"[SetVH] Sending command: {vhCmd}");
                Debug.WriteLine($"[SetVH] Target: {velocityDegSec:F2} deg/s = {velocityCounts:E2} counts/s");

                if (!PlatinumDrive.SendRawCommand(vhCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to set VH: {response}");
                }

                Debug.WriteLine($"[SetVH] SUCCESS - VH set to {velocityDegSec:F2} deg/s ({velocityCounts:E2} counts/s)");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting VH: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Polling Methods

        /// <summary>
        /// Polling Loop for Real-time data acquisition
        /// [REDESIGNED] Update fields in-place instead of replacing object
        /// This prevents losing Command_Velocity_Deg_Sec written by PID Control Loop
        /// </summary>
        private async Task PollingLoop(CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[Azimuth] Polling started at {PollingRate}Hz ({_pollingIntervalMs}ms interval)");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_pollingIntervalMs, cancellationToken);

                    bool hasUpdate = false;

                    // Read Position
                    try
                    {
                        double position = ReadPosition();
                        lock (_infoLock)
                        {
                            _azimuthInfo.Raw_Position = position;
                            
                            // [KALMAN FILTER] Apply filter if initialized
                            if (_isFilterInitialized && _positionFilter != null)
                            {
                                // Update Kalman Filter with raw measurement
                                double dt = _pollingIntervalMs / 1000.0; // Convert ms to seconds
                                double filteredPosition = _positionFilter.Update(position, dt);
                                double filteredVelocity = _positionFilter.Velocity;
                                
                                // Store filtered values
                                _azimuthInfo.Filtered_Position = filteredPosition;
                                _azimuthInfo.Filtered_Velocity = filteredVelocity;
                            }
                            else
                            {
                                // No filter - use raw values
                                _azimuthInfo.Filtered_Position = position;
                                _azimuthInfo.Filtered_Velocity = 0;
                            }
                        }
                        PositionUpdated?.Invoke(this, position);
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azimuth] Error reading position: {ex.Message}");
                    }

                    // Read Velocity
                    try
                    {
                        double velocity = ReadVelocity();
                        lock (_infoLock)
                        {
                            _azimuthInfo.Raw_Velocity = velocity;
                        }
                        VelocityUpdated?.Invoke(this, velocity);
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azimuth] Error reading velocity: {ex.Message}");
                    }

                    // Read Current IQ
                    try
                    {
                        double current = ReadCurrentIQ();
                        lock (_infoLock)
                        {
                            _azimuthInfo.Current_IQ = current;
                        }
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azimuth] Error reading current: {ex.Message}");
                    }

                    // Read Drive Enable Status
                    try
                    {
                        bool enabled = ReadDriveEnabled();
                        lock (_infoLock)
                        {
                            _azimuthInfo.Is_Drive_Enabled = enabled;
                        }
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azimuth] Error reading drive status: {ex.Message}");
                    }

                    // Read Digital Input (Sensors)
                    try
                    {
                        int ipValue = ReadDigitalInput();
                        UpdateSensorStatus(ipValue);
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azimuth] Error reading digital input: {ex.Message}");
                    }

                    // Raise event if data was updated
                    if (hasUpdate)
                    {
                        AzimuthInfo infoCopy;
                        lock (_infoLock)
                        {
                            infoCopy = _azimuthInfo; // Get reference for event
                        }
                        DataUpdated?.Invoke(this, infoCopy);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Azimuth] Polling error: {ex.Message}");
                    PollingError?.Invoke(this, ex.Message);
                }
            }

            System.Diagnostics.Debug.WriteLine("[Azimuth] Polling stopped");
        }

        #endregion

        #region Private Command Sender Loop

        /// <summary>
        /// Command Sender Loop - Sends velocity commands to Drive at 200Hz
        /// Reads Command_Velocity_Deg_Sec from AzimuthInfo (written by Manual Control/PID/Tracking)
        /// Sends JP[axis] + BG[axis] commands to Drive for smooth continuous motion
        /// [SIMPLIFIED] Position limits and soft landing handled by Drive internally
        /// </summary>
        private async Task CommandSenderLoop(CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[Azimuth-CommandSender] Started at 200Hz ({_commandSenderIntervalMs}ms interval)");
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_commandSenderIntervalMs, cancellationToken);

                    // Load axis config for unit conversion
                    var axisConfig = AppSettings.GetAzimuthAxisConfig();

                    // Check if Drive is ENABLED before sending commands
                    bool isDriveEnabled = CurrentInfo.Is_Drive_Enabled;
                    double currentPositionDeg = (CurrentInfo.Raw_Position / axisConfig.Encoder_Resolution) * 360.0;
                    
                    if (!isDriveEnabled)
                    {
                        // Drive not enabled - skip sending commands
                        continue;
                    }

                    // Read command velocity (thread-safe)
                    double commandVelocity = CurrentInfo.Command_Velocity_Deg_Sec;
                    
                    // [OPTIMIZATION] Skip sending if velocity is 0 AND last sent was also 0
                    if (commandVelocity == 0 && _lastSentVelocity == 0)
                    {
                        continue;
                    }
                    
                    // [UNWRAP FLAG] Check if PD Control is unwrapping (crossing 360° boundary)
                    bool isUnwrapping = CurrentInfo.IsUnwrapping;
                    
                    // [SAFETY CHECK] Position Limit Protection for Azimuth
                    // ?? CRITICAL: Check both CW (+300°) and CCW (-300°) limits!
                    double minLimitDeg = axisConfig.CCW_Limit_Deg;  // Counter-clockwise limit (e.g., -300°)
                    double maxLimitDeg = axisConfig.CW_Limit_Deg;   // Clockwise limit (e.g., +300°)
                    double safetyMarginDeg = 2.0; // Stop 2 degrees before limit (Azimuth rotates faster)
                    
                    // [CRITICAL CHECK] ????????????????? Limit ???????
                    bool beyondCWLimit = currentPositionDeg > maxLimitDeg;   // ???? +300°
                    bool beyondCCWLimit = currentPositionDeg < minLimitDeg;  // ???? -300°
                    bool outsideLimits = beyondCWLimit || beyondCCWLimit;
                    
                    // Check if we're approaching limits
                    bool nearLowerLimit = currentPositionDeg <= (minLimitDeg + safetyMarginDeg);  // e.g., < -298°
                    bool nearUpperLimit = currentPositionDeg >= (maxLimitDeg - safetyMarginDeg);  // e.g., > +298°
                    
                    // [CRITICAL SAFETY] ?????????? Limit ? ???????????????????????????!
                    if (outsideLimits)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CRITICAL] Azimuth OUTSIDE limits! Pos={currentPositionDeg:F2}°");
                        System.Diagnostics.Debug.WriteLine($"[CRITICAL] Limits: CCW={minLimitDeg:F0}° | CW=+{maxLimitDeg:F0}°");
                        
                        if (beyondCWLimit && commandVelocity > 0)
                        {
                            // ???????? CW Limit (+300°) ???????????????? CW ? ?????!
                            System.Diagnostics.Debug.WriteLine($"[CRITICAL BLOCK] Beyond CW limit but trying to go further CW - BLOCKED!");
                            string stopCmd = $"AX{AZIMUTH_AXIS_NUMBER}.JP=0";
                            string beginCmdStop = $"AX{AZIMUTH_AXIS_NUMBER}.BG";
                            _platinumDrive.SendRawCommand(stopCmd, out _, 100);
                            _platinumDrive.SendRawCommand(beginCmdStop, out _, 100);
                            _lastSentVelocity = 0;
                            continue;
                        }
                        else if (beyondCCWLimit && commandVelocity < 0)
                        {
                            // ???????? CCW Limit (-300°) ???????????????? CCW ? ?????!
                            System.Diagnostics.Debug.WriteLine($"[CRITICAL BLOCK] Beyond CCW limit but trying to go further CCW - BLOCKED!");
                            string stopCmd = $"AX{AZIMUTH_AXIS_NUMBER}.JP=0";
                            string beginCmdStop = $"AX{AZIMUTH_AXIS_NUMBER}.BG";
                            _platinumDrive.SendRawCommand(stopCmd, out _, 100);
                            _platinumDrive.SendRawCommand(beginCmdStop, out _, 100);
                            _lastSentVelocity = 0;
                            continue;
                        }
                        else
                        {
                            // ??????? Limit ????????????????? (?????????) ? ??????!
                            System.Diagnostics.Debug.WriteLine($"[UNWRAP ALLOWED] Outside limit but moving towards opposite direction - ALLOWED!");
                            // Allow motion - ??????????????????????
                        }
                    }
                    // [UNWRAP EXCEPTION] If unwrapping, bypass limit checks to allow crossing boundary
                    else if (isUnwrapping)
                    {
                        System.Diagnostics.Debug.WriteLine($"[UNWRAP BYPASS] Azimuth unwrapping in progress - bypassing limit checks");
                        System.Diagnostics.Debug.WriteLine($"[UNWRAP BYPASS] Current: {currentPositionDeg:F2}°, Velocity: {commandVelocity:F2}°/s");
                        System.Diagnostics.Debug.WriteLine($"[UNWRAP BYPASS] Limits: CCW={minLimitDeg:F0}° | CW=+{maxLimitDeg:F0}°");
                        // Skip limit check - allow motion beyond limits during unwrap
                    }
                    // [NORMAL MODE] Prevent motion that would exceed limits
                    else if ((nearLowerLimit && commandVelocity < 0) || (nearUpperLimit && commandVelocity > 0))
                    {
                        // Stop motion - we're at limit and trying to move further
                        System.Diagnostics.Debug.WriteLine($"[AZIMUTH LIMIT] Position: {currentPositionDeg:F2}°, Velocity: {commandVelocity:F2}°/s - BLOCKED!");
                        System.Diagnostics.Debug.WriteLine($"[AZIMUTH LIMIT] Limits: CCW={minLimitDeg:F0}° | CW=+{maxLimitDeg:F0}°");
                        
                        // Send stop command
                        string stopCmd = $"AX{AZIMUTH_AXIS_NUMBER}.JP=0";
                        string beginCmdStop = $"AX{AZIMUTH_AXIS_NUMBER}.BG";
                        _platinumDrive.SendRawCommand(stopCmd, out _, 100);
                        _platinumDrive.SendRawCommand(beginCmdStop, out _, 100);
                        _lastSentVelocity = 0;
                        continue;
                    }
                    
                    // Convert velocity from deg/s to counts/s
                    double velocityCounts = (commandVelocity / 360.0) * axisConfig.Encoder_Resolution;
                    
                    // Build commands (JP = Jog Position, BG = Begin Motion)
                    string jogCmd = $"AX{AZIMUTH_AXIS_NUMBER}.JP={(long)velocityCounts}";
                    string beginCmd = $"AX{AZIMUTH_AXIS_NUMBER}.BG";
                    
                    // Send commands with timeout
                    string response;
                    if (_platinumDrive.SendRawCommand(jogCmd, out response, 100))
                    {
                        // Always send BG after JP for continuous motion
                        _platinumDrive.SendRawCommand(beginCmd, out response, 100);
                        
                        // Update last sent velocity
                        _lastSentVelocity = commandVelocity;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azimuth-CommandSender] WARNING: Failed to send {jogCmd}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Azimuth-CommandSender] Error: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("[Azimuth-CommandSender] Stopped");
        }

        #endregion

        #region Public Methods - Motion Control

        /// <summary>
        /// Set Motion Parameters to Drive (AC, DC, SD, SF)
        /// These parameters affect how velocity motion behaves
        /// MUST be called BEFORE sending JP (velocity) command
        /// </summary>
        /// <param name="accelerationDegSec2">Acceleration in deg/sec²</param>
        /// <param name="decelerationDegSec2">Deceleration in deg/sec²</param>
        /// <param name="stopDecelerationDegSec2">Stop Deceleration (Emergency) in deg/sec²</param>
        /// <param name="smoothFactorMs">Smooth Factor in milliseconds</param>
        public void SetMotionParameters(double accelerationDegSec2, double decelerationDegSec2, 
                                       double stopDecelerationDegSec2, double smoothFactorMs)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                string response;
                const int TIMEOUT_MS = 2000;

                // Convert degrees/sec² to counts/sec²
                double acCounts = (accelerationDegSec2 / 360.0) * axisConfig.Encoder_Resolution;
                double dcCounts = (decelerationDegSec2 / 360.0) * axisConfig.Encoder_Resolution;
                double sdCounts = (stopDecelerationDegSec2 / 360.0) * axisConfig.Encoder_Resolution;

                Debug.WriteLine($"[MotionParams] Conversion calculation:");
                Debug.WriteLine($"  ACC: {accelerationDegSec2:F2} deg/s² × ({axisConfig.Encoder_Resolution} / 360) = {acCounts:F0} counts/s²");
                Debug.WriteLine($"  DCC: {decelerationDegSec2:F2} deg/s² × ({axisConfig.Encoder_Resolution} / 360) = {dcCounts:F0} counts/s²");
                Debug.WriteLine($"  SD: {stopDecelerationDegSec2:F2} deg/s² × ({axisConfig.Encoder_Resolution} / 360) = {sdCounts:F0} counts/s²");

                // Validate Smooth Factor (max ~2000ms is safe for most configurations)
                const double MAX_SMOOTH_FACTOR_MS = 2000;
                if (smoothFactorMs > MAX_SMOOTH_FACTOR_MS)
                {
                    Debug.WriteLine($"[MotionParams] WARNING: SF too high ({smoothFactorMs}ms), limiting to {MAX_SMOOTH_FACTOR_MS}ms");
                    smoothFactorMs = MAX_SMOOTH_FACTOR_MS;
                }
                if (smoothFactorMs < 0)
                {
                    Debug.WriteLine($"[MotionParams] WARNING: SF cannot be negative ({smoothFactorMs}ms), setting to 0ms");
                    smoothFactorMs = 0;
                }

                // Set parameters (use AXn.command= format for Platinum Drive)
                string acCmd = $"AX{AZIMUTH_AXIS_NUMBER}.AC={(int)acCounts}";
                string dcCmd = $"AX{AZIMUTH_AXIS_NUMBER}.DC={(int)dcCounts}";
                string sdCmd = $"AX{AZIMUTH_AXIS_NUMBER}.SD={(int)sdCounts}";
                string sfCmd = $"AX{AZIMUTH_AXIS_NUMBER}.SF={(int)smoothFactorMs}";

                Debug.WriteLine($"[MotionParams] Sending commands: {acCmd}, {dcCmd}, {sdCmd}, {sfCmd}");

                // Note: UM=5 (Velocity Mode) is already set in Drive - no need to send it
                // Send commands
                PlatinumDrive.SendRawCommand(acCmd, out response, TIMEOUT_MS);
                PlatinumDrive.SendRawCommand(dcCmd, out response, TIMEOUT_MS);
                PlatinumDrive.SendRawCommand(sdCmd, out response, TIMEOUT_MS);
                PlatinumDrive.SendRawCommand(sfCmd, out response, TIMEOUT_MS);

                Debug.WriteLine($"[MotionParams] SUCCESS - Motion parameters set");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting motion parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stop motion immediately (ST command)
        /// Sets Command_Velocity to 0 and sends ST command
        /// [POLICY] Uses lock when writing to Command_Velocity_Deg_Sec
        /// </summary>
        public void StopMotion()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                // [POLICY] Set command velocity to 0 with lock (thread-safe)
                lock (_infoLock)
                {
                    _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                }
                
                // Also send ST command for immediate stop (use AXn.ST format)
                string response;
                _platinumDrive.SendRawCommand($"AX{AZIMUTH_AXIS_NUMBER}.ST", out response, 1000);
                Debug.WriteLine("[Azimuth] Motion stopped (ST command sent + Command_Velocity = 0)");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error stopping motion: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Move with constant velocity - [DEPRECATED - Use SetCommandVelocity() instead!]
        /// This is kept for backward compatibility with existing UI code
        /// [POLICY] ALL velocity commands must write to Command_Velocity_Deg_Sec with lock
        /// CommandSenderLoop will read it and send JV+BG to Drive
        /// </summary>
        public void MoveVelocity(double velocityDegSec)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            // [POLICY] Write to Command_Velocity with lock (thread-safe)
            // CommandSenderLoop will read this and send JP+BG commands
            lock (_infoLock)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = velocityDegSec;
            }
            
            Debug.WriteLine($"[MoveVelocity] Set Command_Velocity = {velocityDegSec:F4} deg/s (will be sent by CommandSenderLoop)");
        }

        /// <summary>
        /// Set Command Velocity (Thread-Safe)
        /// [POLICY] This is the PREFERRED way to set velocity commands
        /// For use by Satellite Tracking, PID Control, or Manual Control
        /// CommandSenderLoop will automatically send JV+BG to Drive
        /// </summary>
        public void SetCommandVelocity(double velocityDegSec)
        {
            lock (_infoLock)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = velocityDegSec;
            }
        }
        
        /// <summary>
        /// Move to absolute position using Drive's internal Position Control (UM=2)
        /// [NEW] For Position Mode - bypasses software PD controller
        /// Uses PA (Position Absolute) + SP (Set Position Speed) + BG (Begin)
        /// </summary>
        /// <param name="targetPositionDeg">Target position in degrees</param>
        /// <param name="maxVelocityDegSec">Maximum velocity for the move in deg/s</param>
        public void MoveToPosition(double targetPositionDeg, double maxVelocityDegSec)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                // Load axis config if not cached
                if (_cachedAxisConfig == null)
                {
                    _cachedAxisConfig = AppSettings.GetAzimuthAxisConfig();
                }

                // Convert position from degrees to counts
                double targetPositionCounts = (targetPositionDeg / 360.0) * _cachedAxisConfig.Encoder_Resolution;
                
                // [FIX] Clamp velocity to Max_Control_Velocity_Deg_Sec from AppSettings
                double clampedVelocityDegSec = Math.Clamp(maxVelocityDegSec, 
                    0.0, 
                    _cachedAxisConfig.Max_Control_Velocity_Deg_Sec);
                
                if (Math.Abs(clampedVelocityDegSec - maxVelocityDegSec) > 0.01)
                {
                    Debug.WriteLine($"[MoveToPosition] Velocity clamped: {maxVelocityDegSec:F2} ? {clampedVelocityDegSec:F2} deg/s (Max: {_cachedAxisConfig.Max_Control_Velocity_Deg_Sec} deg/s)");
                }
                
                // Convert velocity from deg/s to counts/s
                double maxVelocityCounts = (clampedVelocityDegSec / 360.0) * _cachedAxisConfig.Encoder_Resolution;

                string response;
                const int TIMEOUT_MS = 1000;

                // 1. Set Position Speed (SP) - Maximum velocity for position move
                string spCmd = $"AX{AZIMUTH_AXIS_NUMBER}.SP={(long)Math.Abs(maxVelocityCounts)}";
                Debug.WriteLine($"[MoveToPosition] Setting speed: {spCmd}");
                if (!_platinumDrive.SendRawCommand(spCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to set SP: {response}");
                }

                // 2. Set Position Absolute (PA) - Target position
                string paCmd = $"AX{AZIMUTH_AXIS_NUMBER}.PA={(long)targetPositionCounts}";
                Debug.WriteLine($"[MoveToPosition] Setting target: {paCmd}");
                if (!_platinumDrive.SendRawCommand(paCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to set PA: {response}");
                }

                // 3. Begin Motion (BG) - Start moving to target
                string bgCmd = $"AX{AZIMUTH_AXIS_NUMBER}.BG";
                Debug.WriteLine($"[MoveToPosition] Starting motion: {bgCmd}");
                if (!_platinumDrive.SendRawCommand(bgCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to send BG: {response}");
                }

                Debug.WriteLine($"[MoveToPosition] SUCCESS - Moving to {targetPositionDeg:F2}° at {maxVelocityDegSec:F2}°/s (Target: {targetPositionCounts:F0} counts, Speed: {maxVelocityCounts:F0} counts/s)");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error moving to position: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Check if motion is complete (Motion Status)
        /// Returns true if drive is in position and not moving
        /// </summary>
        public bool IsMotionComplete()
        {
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                string response;
                const int TIMEOUT_MS = 500;

                // Read MS (Motion Status) - bit 0 indicates motion complete
                if (_platinumDrive.SendRawCommand($"MS[{AZIMUTH_AXIS_NUMBER}]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int ms))
                    {
                        // MS bit 0 = 1: Motion complete, 0: Motion in progress
                        bool motionComplete = (ms & 0x01) == 0x01;
                        return motionComplete;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Kalman Filter Configuration

        /// <summary>
        /// Initialize Kalman Filter with current position
        /// Call this after connection or when filter reset is needed
        /// </summary>
        public void InitializeKalmanFilter()
        {
            if (!IsConnected)
            {
                Debug.WriteLine("[KalmanFilter] Cannot initialize - Drive not connected");
                return;
            }

            try
            {
                // Read current position as initial state
                double initialPosition = ReadPosition();
                double initialVelocity = ReadVelocity();

                // Create new Kalman Filter
                _positionFilter = new KalmanFilter1D(initialPosition, initialVelocity);

                // Configure filter parameters for encoder noise characteristics
                // [TUNED v2] Trust Model EVEN MORE and trust Measurements EVEN LESS
                // Process noise: How much we trust the motion model
                // - Position: Extremely low - encoder + model are very predictable
                // - Velocity: Very low - smooth motion expected
                _positionFilter.SetProcessNoise(0.00002, 0.002); // Reduced 5x - Trust model MORE

                // Measurement noise: How much we trust the encoder readings
                // Higher value = more filtering, smoother but slower response
                // For high-resolution encoder (32-bit), noise is low
                _positionFilter.SetMeasurementNoise(2.0); // Increased 2x - Trust measurements LESS (more smoothing)

                // Set time step to match polling rate (5ms = 0.005s)
                _positionFilter.SetTimeStep(0.005);

                _isFilterInitialized = true;

                Debug.WriteLine($"[KalmanFilter-Azimuth] Initialized at Position={initialPosition:F2} counts, Velocity={initialVelocity:F2} counts/s");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KalmanFilter-Azimuth] Initialization failed: {ex.Message}");
                _isFilterInitialized = false;
            }
        }

        /// <summary>
        /// Configure Kalman Filter tuning parameters
        /// </summary>
        /// <param name="processNoisePosition">Position process noise (default: 0.0001)</param>
        /// <param name="processNoiseVelocity">Velocity process noise (default: 0.01)</param>
        /// <param name="measurementNoise">Encoder measurement noise (default: 1.0 counts)</param>
        public void ConfigureKalmanFilter(double processNoisePosition = 0.0001, 
                                          double processNoiseVelocity = 0.01, 
                                          double measurementNoise = 1.0)
        {
            if (_positionFilter == null)
            {
                Debug.WriteLine("[KalmanFilter-Azimuth] Filter not initialized - call InitializeKalmanFilter() first");
                return;
            }

            _positionFilter.SetProcessNoise(processNoisePosition, processNoiseVelocity);
            _positionFilter.SetMeasurementNoise(measurementNoise);

            Debug.WriteLine($"[KalmanFilter-Azimuth] Configured: ProcNoise_Pos={processNoisePosition}, ProcNoise_Vel={processNoiseVelocity}, MeasNoise={measurementNoise}");
        }

        /// <summary>
        /// Reset Kalman Filter to specific position
        /// Useful when position discontinuity occurs (e.g., manual jog, homing)
        /// </summary>
        public void ResetKalmanFilter(double position, double velocity = 0)
        {
            if (_positionFilter == null)
            {
                Debug.WriteLine("[KalmanFilter-Azimuth] Filter not initialized - call InitializeKalmanFilter() first");
                return;
            }

            _positionFilter.Reset(position, velocity);
            Debug.WriteLine($"[KalmanFilter-Azimuth] Reset to Position={position:F2} counts, Velocity={velocity:F2} counts/s");
        }

        #endregion
    }
}

