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
    /// Altitude Drive Controller for Platinum Drive
    /// Supports both shared drive (Axis 2) and separate dedicated drive (Axis 1)
    /// Wraps PlatinumDrive with polling and data management for Altitude axis
    /// 
    /// ========================================
    /// VELOCITY COMMAND ARCHITECTURE POLICY:
    /// ========================================
    /// ALL velocity commands MUST follow this architecture:
    /// 
    /// 1. [SOURCE] Satellite Tracking / Position Control / Manual Control
    ///    ? Write to: _altitudeInfo.Command_Velocity_Deg_Sec (with lock!)
    ///    ? DO NOT send JP/BG directly!
    /// 
    /// 2. [SENDER] CommandSenderLoop (200Hz)
    ///    ? Read: _altitudeInfo.Command_Velocity_Deg_Sec (with lock!)
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
    public class AltitudeManager
    {
        #region Fields
        private PlatinumDrive _platinumDrive;
        private readonly int _axisNumber; // Axis number on this drive (1 if separate drive, 2 if shared)

        // Data polling
        private AltitudeInfo _altitudeInfo;
        private CancellationTokenSource _pollingCancellationTokenSource;
        private Task _pollingTask;
        private readonly object _infoLock = new object();
        private int _pollingIntervalMs = 5; // 5ms = 200Hz polling rate (optimal for 2-axis system)
        
        // [NEW] Command Sender Loop - Sends velocity commands to Drive
        private CancellationTokenSource _commandSenderCancellationTokenSource;
        private Task _commandSenderTask;
        private int _commandSenderIntervalMs = 5; // 5ms = 200Hz command rate (optimal for 2-axis system)
        private double _lastSentVelocity = 0; // Track last sent velocity to avoid redundant commands
        
        // [NEW] Kalman Filter for Position and Velocity smoothing
        private KalmanFilter1D _positionFilter;
        private bool _isFilterInitialized = false;
        
        // Cache axis config to avoid file I/O during motion commands
        private AltitudeAxisConfig _cachedAxisConfig;
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
        public event EventHandler<AltitudeInfo> DataUpdated;

        /// <summary>
        /// Event when Polling error occurs
        /// </summary>
        public event EventHandler<string> PollingError;

        /// <summary>
        /// Event when Filter Parameters are updated
        /// </summary>
        public event EventHandler<AltitudeInfo> FilterParametersUpdated;
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
        /// Latest Altitude Info (Thread-safe)
        /// </summary>
        public AltitudeInfo CurrentInfo
        {
            get
            {
                lock (_infoLock)
                {
                    return _altitudeInfo;
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
        /// <summary>
        /// Constructor with shared PlatinumDrive (Altitude = Axis 2 on shared drive)
        /// </summary>
        public AltitudeManager(PlatinumDrive sharedPlatinumDrive)
            : this(sharedPlatinumDrive, PlatinumDrive.Axis.ALTITUDE)
        {
        }

        /// <summary>
        /// Constructor with specific axis number
        /// Use axisNumber=1 when Altitude is on a separate dedicated drive
        /// Use axisNumber=2 when Altitude shares a drive with Azimuth
        /// </summary>
        public AltitudeManager(PlatinumDrive platinumDrive, int axisNumber)
        {
            _platinumDrive = platinumDrive ?? throw new ArgumentNullException(nameof(platinumDrive));
            _axisNumber = axisNumber;
            _altitudeInfo = new AltitudeInfo();
        }
        #endregion

        #region Public Methods

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

            // Load axis config once at start (not in loop!)
            _cachedAxisConfig = AppSettings.GetAltitudeAxisConfig();

            // [KALMAN FILTER] Initialize filter before starting polling
            InitializeKalmanFilter();

            // Start Data Read Loop (200Hz)
            _pollingCancellationTokenSource = new CancellationTokenSource();
            _pollingTask = Task.Run(() => PollingLoop(_pollingCancellationTokenSource.Token));
            
            // [ENABLED] Command Sender Loop - Reads Command_Velocity_Deg_Sec and sends JP/BG commands
            _commandSenderCancellationTokenSource = new CancellationTokenSource();
            _commandSenderTask = Task.Run(() => CommandSenderLoop(_commandSenderCancellationTokenSource.Token));
            
            Debug.WriteLine($"[Altitude] Started: Data Read Loop (200Hz) | Command Sender Loop (200Hz) - Axis {_axisNumber} on drive");
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
            
            Debug.WriteLine("[Altitude] Stopped: Data Read Loop | Command Sender Loop");
        }

        /// <summary>
        /// Read Position from Drive (single read)
        /// </summary>
        public double ReadPosition()
        {
            return _platinumDrive.ReadPosition(_axisNumber);
        }

        /// <summary>
        /// Read Velocity from Drive (single read)
        /// </summary>
        public double ReadVelocity()
        {
            return _platinumDrive.ReadVelocity(_axisNumber);
        }

        /// <summary>
        /// Read Current IQ from Drive (single read)
        /// </summary>
        public double ReadCurrentIQ()
        {
            return _platinumDrive.ReadCurrentIQ(_axisNumber);
        }

        /// <summary>
        /// Read Drive Enable status
        /// </summary>
        public bool ReadDriveEnabled()
        {
            return _platinumDrive.ReadDriveEnabled(_axisNumber);
        }

        /// <summary>
        /// Read all Filter Parameters from Drive (KV parameters)
        /// Uses dynamic axis prefix based on _axisNumber
        /// Returns updated AltitudeInfo with filter data
        /// </summary>
        public AltitudeInfo ReadFilterParameters()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                var info = new AltitudeInfo();
                string response;
                const int TIMEOUT_MS = 500;
                string axPrefix = $"AX{_axisNumber}";

                Debug.WriteLine($"[ReadFilterParameters] Starting to read filter parameters (Axis {_axisNumber})...");
                Debug.WriteLine($"[ReadFilterParameters] Using {axPrefix}.KV[x] format");

                // Filter #1 (Lead-Lag or Low-Pass)
                int filter1Type = 0;
                if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[5]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter1Type))
                        info.Filter1_Type = filter1Type;
                }

                if (filter1Type != 0)
                {
                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[1]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter1Freq))
                            info.Filter1_Frequency = filter1Freq;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[2]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter1Damp))
                            info.Filter1_Damping = filter1Damp;
                    }
                }

                // Filter #2 (Notch)
                int filter2Type = 0;
                if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[10]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter2Type))
                        info.Filter2_Type = filter2Type;
                }

                if (filter2Type != 0)
                {
                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[6]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter2Freq))
                            info.Filter2_Frequency = filter2Freq;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[7]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter2Q))
                            info.Filter2_QFactor = filter2Q;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[8]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter2Mag))
                            info.Filter2_Magnitude = filter2Mag;
                    }
                }

                // Filter #3 (Notch)
                int filter3Type = 0;
                if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[15]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter3Type))
                        info.Filter3_Type = filter3Type;
                }

                if (filter3Type != 0)
                {
                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[11]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter3Freq))
                            info.Filter3_Frequency = filter3Freq;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[12]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter3Q))
                            info.Filter3_QFactor = filter3Q;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[13]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter3Mag))
                            info.Filter3_Magnitude = filter3Mag;
                    }
                }

                // Filter #4 (Notch)
                int filter4Type = 0;
                if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[20]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out filter4Type))
                        info.Filter4_Type = filter4Type;
                }

                if (filter4Type != 0)
                {
                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[16]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter4Freq))
                            info.Filter4_Frequency = filter4Freq;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[17]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter4Q))
                            info.Filter4_QFactor = filter4Q;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[18]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double filter4Mag))
                            info.Filter4_Magnitude = filter4Mag;
                    }
                }

                // Feedback Filter #1 (Notch)
                int fbFilter1Type = 0;
                if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[55]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out fbFilter1Type))
                        info.FeedbackFilter1_Type = fbFilter1Type;
                }

                if (fbFilter1Type != 0)
                {
                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[51]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter1Freq))
                            info.FeedbackFilter1_Frequency = fbFilter1Freq;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[52]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter1Q))
                            info.FeedbackFilter1_QFactor = fbFilter1Q;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[53]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter1Mag))
                            info.FeedbackFilter1_Magnitude = fbFilter1Mag;
                    }
                }

                // Feedback Filter #2 (Notch)
                int fbFilter2Type = 0;
                if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[60]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out fbFilter2Type))
                        info.FeedbackFilter2_Type = fbFilter2Type;
                }

                if (fbFilter2Type != 0)
                {
                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[56]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter2Freq))
                            info.FeedbackFilter2_Frequency = fbFilter2Freq;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[57]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter2Q))
                            info.FeedbackFilter2_QFactor = fbFilter2Q;
                    }

                    if (_platinumDrive.SendRawCommand($"{axPrefix}.KV[58]", out response, TIMEOUT_MS))
                    {
                        if (double.TryParse(response.Trim(), out double fbFilter2Mag))
                            info.FeedbackFilter2_Magnitude = fbFilter2Mag;
                    }
                }

                info.UpdateFilterParametersTimestamp();
                
                Debug.WriteLine($"[ReadFilterParameters] SUCCESS (Axis {_axisNumber})!");
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
                Debug.WriteLine($"[ReadFilterParameters] ERROR (Axis {_axisNumber}): {ex.Message}");
                throw new Exception($"Error reading filter parameters (Axis {_axisNumber}): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Read Velocity Low Limit (VL[3]) from Drive - Position Mode Down Limit
        /// Returns value in degrees
        /// </summary>
        public double ReadVelocityLowLimit()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                const int TIMEOUT_MS = 1000;

                if (_platinumDrive.SendRawCommand($"AX{_axisNumber}.VL[3]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double vlCounts))
                    {
                        if (_cachedAxisConfig == null)
                        {
                            _cachedAxisConfig = AppSettings.GetAltitudeAxisConfig();
                        }
                        
                        double vlDeg = (vlCounts / _cachedAxisConfig.Encoder_Resolution) * 360.0;
                        
                        Debug.WriteLine($"[ReadVL] AX{_axisNumber}.VL[3] = {vlCounts:F0} counts = {vlDeg:F2} deg");
                        return vlDeg;
                    }
                }

                throw new Exception("Failed to read VL parameter");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error reading VL: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Read Velocity High Limit (VH[3]) from Drive - Position Mode Up Limit
        /// Returns value in degrees
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

                if (_platinumDrive.SendRawCommand($"AX{_axisNumber}.VH[3]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double vhCounts))
                    {
                        if (_cachedAxisConfig == null)
                        {
                            _cachedAxisConfig = AppSettings.GetAltitudeAxisConfig();
                        }
                        
                        double vhDeg = (vhCounts / _cachedAxisConfig.Encoder_Resolution) * 360.0;
                        
                        Debug.WriteLine($"[ReadVH] AX{_axisNumber}.VH[3] = {vhCounts:F0} counts = {vhDeg:F2} deg");
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
        /// Set Velocity Low Limit (VL[3]) to Drive - Position Mode Down Limit
        /// Input value in degrees, converts to counts
        /// </summary>
        public void SetVelocityLowLimit(double limitDeg)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                if (_cachedAxisConfig == null)
                {
                    _cachedAxisConfig = AppSettings.GetAltitudeAxisConfig();
                }

                double limitCounts = (limitDeg / 360.0) * _cachedAxisConfig.Encoder_Resolution;

                string response;
                const int TIMEOUT_MS = 2000;

                string vlCmd = $"AX{_axisNumber}.VL[3]={(long)limitCounts}";
                Debug.WriteLine($"[SetVL] Sending command: {vlCmd}");
                Debug.WriteLine($"[SetVL] Target: {limitDeg:F2} deg = {limitCounts:E2} counts");

                if (!_platinumDrive.SendRawCommand(vlCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to set VL: {response}");
                }

                Debug.WriteLine($"[SetVL] SUCCESS - VL[3] set to {limitDeg:F2} deg ({limitCounts:E2} counts)");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting VL: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Set Velocity High Limit (VH[3]) to Drive - Position Mode Up Limit
        /// Input value in degrees, converts to counts
        /// </summary>
        public void SetVelocityHighLimit(double limitDeg)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                if (_cachedAxisConfig == null)
                {
                    _cachedAxisConfig = AppSettings.GetAltitudeAxisConfig();
                }

                double limitCounts = (limitDeg / 360.0) * _cachedAxisConfig.Encoder_Resolution;

                string response;
                const int TIMEOUT_MS = 2000;

                string vhCmd = $"AX{_axisNumber}.VH[3]={(long)limitCounts}";
                Debug.WriteLine($"[SetVH] Sending command: {vhCmd}");
                Debug.WriteLine($"[SetVH] Target: {limitDeg:F2} deg = {limitCounts:E2} counts");

                if (!_platinumDrive.SendRawCommand(vhCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to set VH: {response}");
                }

                Debug.WriteLine($"[SetVH] SUCCESS - VH[3] set to {limitDeg:F2} deg ({limitCounts:E2} counts)");
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
            System.Diagnostics.Debug.WriteLine($"[Altitude] Polling started at {PollingRate}Hz ({_pollingIntervalMs}ms interval)");

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
                            _altitudeInfo.Raw_Position = position;
                            
                            // [KALMAN FILTER] Apply filter if initialized
                            if (_isFilterInitialized && _positionFilter != null)
                            {
                                // Update Kalman Filter with raw measurement
                                double dt = _pollingIntervalMs / 1000.0; // Convert ms to seconds
                                double filteredPosition = _positionFilter.Update(position, dt);
                                double filteredVelocity = _positionFilter.Velocity;
                                
                                // Store filtered values
                                _altitudeInfo.Filtered_Position = filteredPosition;
                                _altitudeInfo.Filtered_Velocity = filteredVelocity;
                            }
                            else
                            {
                                // No filter - use raw values
                                _altitudeInfo.Filtered_Position = position;
                                _altitudeInfo.Filtered_Velocity = 0;
                            }
                        }
                        PositionUpdated?.Invoke(this, position);
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Altitude] Error reading position: {ex.Message}");
                    }

                    // Read Velocity
                    try
                    {
                        double velocity = ReadVelocity();
                        lock (_infoLock)
                        {
                            _altitudeInfo.Raw_Velocity = velocity;
                        }
                        VelocityUpdated?.Invoke(this, velocity);
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Altitude] Error reading velocity: {ex.Message}");
                    }

                    // Read Current IQ
                    try
                    {
                        double current = ReadCurrentIQ();
                        lock (_infoLock)
                        {
                            _altitudeInfo.Current_IQ = current;
                        }
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Altitude] Error reading current: {ex.Message}");
                    }

                    // Read Drive Enable Status
                    try
                    {
                        bool enabled = ReadDriveEnabled();
                        lock (_infoLock)
                        {
                            _altitudeInfo.Is_Drive_Enabled = enabled;
                        }
                        hasUpdate = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Altitude] Error reading drive status: {ex.Message}");
                    }

                    // Raise event if data was updated
                    if (hasUpdate)
                    {
                        AltitudeInfo infoCopy;
                        lock (_infoLock)
                        {
                            infoCopy = _altitudeInfo; // Get reference for event
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
                    System.Diagnostics.Debug.WriteLine($"[Altitude] Polling error: {ex.Message}");
                    PollingError?.Invoke(this, ex.Message);
                }
            }

            System.Diagnostics.Debug.WriteLine("[Altitude] Polling stopped");
        }

        #endregion

        #region Private Command Sender Loop

        /// <summary>
        /// Command Sender Loop - Sends velocity commands to Drive at 200Hz
        /// Reads Command_Velocity_Deg_Sec from AltitudeInfo (written by Manual Control/Position Control/Tracking)
        /// Sends JP[axis] + BG[axis] commands to Drive for smooth continuous motion
        /// [SAFETY] Checks position limits before sending commands to prevent hitting hard limits
        /// </summary>
        private async Task CommandSenderLoop(CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[Altitude-CommandSender] Started at 200Hz ({_commandSenderIntervalMs}ms interval) - Axis {_axisNumber}");
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_commandSenderIntervalMs, cancellationToken);

                    // Load axis config for unit conversion and limits
                    var axisConfig = AppSettings.GetAltitudeAxisConfig();

                    // Check if Drive is ENABLED before sending commands
                    bool isDriveEnabled;
                    double currentPositionDeg;
                    lock (_infoLock)
                    {
                        isDriveEnabled = _altitudeInfo.Is_Drive_Enabled;
                        // Convert raw position to degrees
                        currentPositionDeg = (_altitudeInfo.Raw_Position / axisConfig.Encoder_Resolution) * 360.0;
                    }
                    if (!isDriveEnabled)
                    {
                        // Drive not enabled - skip sending commands
                        continue;
                    }

                    // Read command velocity (thread-safe)
                    double commandVelocity;
                    lock (_infoLock)
                    {
                        commandVelocity = _altitudeInfo.Command_Velocity_Deg_Sec;
                    }
                    
                    // [OPTIMIZATION] Skip sending if velocity is 0 AND last sent was also 0
                    if (commandVelocity == 0 && _lastSentVelocity == 0)
                    {
                        continue;
                    }
                    
                    // [SAFETY CHECK] Position Limit Protection
                    double minLimitDeg = axisConfig.Down_Limit_Deg;
                    double maxLimitDeg = axisConfig.Up_Limit_Deg;
                    double safetyMarginDeg = 1.0;
                    
                    // Check if we're approaching limits
                    bool nearLowerLimit = currentPositionDeg <= (minLimitDeg + safetyMarginDeg);
                    bool nearUpperLimit = currentPositionDeg >= (maxLimitDeg - safetyMarginDeg);
                    
                    // Prevent motion that would exceed limits
                    if ((nearLowerLimit && commandVelocity < 0) || (nearUpperLimit && commandVelocity > 0))
                    {
                        // Stop motion - we're at limit and trying to move further
                        System.Diagnostics.Debug.WriteLine($"[ALTITUDE LIMIT] Position: {currentPositionDeg:F2}°, Velocity: {commandVelocity:F2}°/s - BLOCKED!");
                        System.Diagnostics.Debug.WriteLine($"[ALTITUDE LIMIT] Limits: [{minLimitDeg:F2}°, {maxLimitDeg:F2}°]");
                        
                        // Send stop command
                        string stopCmd = $"AX{_axisNumber}.JP=0";
                        string beginCmd = $"AX{_axisNumber}.BG";
                        _platinumDrive.SendRawCommand(stopCmd, out _, 100);
                        _platinumDrive.SendRawCommand(beginCmd, out _, 100);
                        _lastSentVelocity = 0;
                        continue;
                    }
                    
                    // Convert velocity from deg/s to counts/s
                    double velocityCounts = (commandVelocity / 360.0) * axisConfig.Encoder_Resolution;
                    
                    // Build commands (JP = Jog Position, BG = Begin Motion)
                    string jogCmd = $"AX{_axisNumber}.JP={(long)velocityCounts}";
                    string beginCmdNormal = $"AX{_axisNumber}.BG";
                    
                    // Send commands with timeout
                    string response;
                    if (_platinumDrive.SendRawCommand(jogCmd, out response, 100))
                    {
                        // Always send BG after JP for continuous motion
                        _platinumDrive.SendRawCommand(beginCmdNormal, out response, 100);
                        
                        // Update last sent velocity
                        _lastSentVelocity = commandVelocity;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Altitude-CommandSender] WARNING: Failed to send {jogCmd}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Altitude-CommandSender] Error: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("[Altitude-CommandSender] Stopped");
        }

        #endregion

        #region Public Methods - Motion Control

        /// <summary>
        /// Stop motion immediately (ST command)
        /// Sets Command_Velocity to 0 and sends ST command
        /// </summary>
        public void StopMotion()
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                lock (_infoLock)
                {
                    _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                }
                
                string response;
                _platinumDrive.SendRawCommand($"AX{_axisNumber}.ST", out response, 1000);
                Debug.WriteLine("[Altitude] Motion stopped (ST command sent + Command_Velocity = 0)");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error stopping motion: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Enable/Disable Drive
        /// </summary>
        public void SetDriveEnable(bool enable)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                string response;
                string command = $"AX{_axisNumber}.MO={(enable ? "1" : "0")}";
                _platinumDrive.SendRawCommand(command, out response, 2000);
                Debug.WriteLine($"[Altitude] Drive {(enable ? "enabled" : "disabled")} (Axis {_axisNumber})");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting drive enable: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Move with constant velocity - [DEPRECATED - Use Command_Velocity_Deg_Sec instead!]
        /// This is kept for backward compatibility with existing UI code
        /// In new architecture, use _altitudeInfo.Command_Velocity_Deg_Sec directly
        /// </summary>
        public void MoveVelocity(double velocityDegSec)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            lock (_infoLock)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = velocityDegSec;
            }
            
            Debug.WriteLine($"[MoveVelocity] Set Command_Velocity = {velocityDegSec:F4} deg/s (will be sent by Command Sender Loop)");
        }

        /// <summary>
        /// Set Command Velocity (Thread-Safe)
        /// For use by Satellite Tracking or other external controllers
        /// </summary>
        public void SetCommandVelocity(double velocityDegSec)
        {
            lock (_infoLock)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = velocityDegSec;
            }
        }

        /// <summary>
        /// Move to absolute position using Drive's internal Position Control (UM=2)
        /// Uses PA (Position Absolute) + SP (Set Position Speed) + BG (Begin)
        /// </summary>
        public void MoveToPosition(double targetPositionDeg, double maxVelocityDegSec)
        {
            if (!IsConnected)
            {
                throw new Exception("Drive is not connected");
            }

            try
            {
                if (_cachedAxisConfig == null)
                {
                    _cachedAxisConfig = AppSettings.GetAltitudeAxisConfig();
                }

                double targetPositionCounts = (targetPositionDeg / 360.0) * _cachedAxisConfig.Encoder_Resolution;
                
                double clampedVelocityDegSec = Math.Clamp(maxVelocityDegSec, 
                    0.0, 
                    _cachedAxisConfig.Max_Velocity_Deg_Sec);
                
                if (Math.Abs(clampedVelocityDegSec - maxVelocityDegSec) > 0.01)
                {
                    Debug.WriteLine($"[MoveToPosition] Velocity clamped: {maxVelocityDegSec:F2} ? {clampedVelocityDegSec:F2} deg/s (Max: {_cachedAxisConfig.Max_Velocity_Deg_Sec} deg/s)");
                }
                
                double maxVelocityCounts = (clampedVelocityDegSec / 360.0) * _cachedAxisConfig.Encoder_Resolution;

                string response;
                const int TIMEOUT_MS = 1000;

                string spCmd = $"AX{_axisNumber}.SP={(long)Math.Abs(maxVelocityCounts)}";
                Debug.WriteLine($"[MoveToPosition] Setting speed: {spCmd}");
                if (!_platinumDrive.SendRawCommand(spCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to set SP: {response}");
                }

                string paCmd = $"AX{_axisNumber}.PA={(long)targetPositionCounts}";
                Debug.WriteLine($"[MoveToPosition] Setting target: {paCmd}");
                if (!_platinumDrive.SendRawCommand(paCmd, out response, TIMEOUT_MS))
                {
                    throw new Exception($"Failed to set PA: {response}");
                }

                string bgCmd = $"AX{_axisNumber}.BG";
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
                if (_platinumDrive.SendRawCommand($"MS[{_axisNumber}]", out response, TIMEOUT_MS))
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

        /// <summary>
        /// Set Motion Parameters (Acceleration, Deceleration, Stop Deceleration, Smooth Factor)
        /// Called when: 1) Connected to Drive, 2) Save Configuration
        /// </summary>
        public void SetMotionParameters(double accelerationDegSec2, double decelerationDegSec2, 
            double stopDecelerationDegSec2, double smoothFactorMs)
        {
            if (!IsConnected)
            {
                throw new Exception("Cannot set motion parameters: Drive not connected");
            }

            try
            {
                if (_cachedAxisConfig == null)
                {
                    _cachedAxisConfig = AppSettings.GetAltitudeAxisConfig();
                }

                double accCounts = (accelerationDegSec2 / 360.0) * _cachedAxisConfig.Encoder_Resolution;
                double dccCounts = (decelerationDegSec2 / 360.0) * _cachedAxisConfig.Encoder_Resolution;
                double sdCounts = (stopDecelerationDegSec2 / 360.0) * _cachedAxisConfig.Encoder_Resolution;

                string response;

                string accCmd = $"AX{_axisNumber}.AC={(long)accCounts}";
                if (!_platinumDrive.SendRawCommand(accCmd, out response, 2000))
                {
                    throw new Exception($"Failed to set AC: {response}");
                }
                Debug.WriteLine($"[Altitude] AC set: {accelerationDegSec2:F2} deg/s² ({accCounts:F0} counts/s²)");

                string dccCmd = $"AX{_axisNumber}.DC={(long)dccCounts}";
                if (!_platinumDrive.SendRawCommand(dccCmd, out response, 2000))
                {
                    throw new Exception($"Failed to set DC: {response}");
                }
                Debug.WriteLine($"[Altitude] DC set: {decelerationDegSec2:F2} deg/s² ({dccCounts:F0} counts/s²)");

                string sdCmd = $"AX{_axisNumber}.SD={(long)sdCounts}";
                if (!_platinumDrive.SendRawCommand(sdCmd, out response, 2000))
                {
                    throw new Exception($"Failed to set SD: {response}");
                }
                Debug.WriteLine($"[Altitude] SD set: {stopDecelerationDegSec2:F2} deg/s² ({sdCounts:F0} counts/s²)");

                string sfCmd = $"AX{_axisNumber}.SF={smoothFactorMs}";
                if (!_platinumDrive.SendRawCommand(sfCmd, out response, 2000))
                {
                    throw new Exception($"Failed to set SF: {response}");
                }
                Debug.WriteLine($"[Altitude] SF set: {smoothFactorMs:F1} ms");

                Debug.WriteLine("[Altitude] Motion parameters set successfully");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting motion parameters: {ex.Message}", ex);
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

                Debug.WriteLine($"[KalmanFilter-Altitude] Initialized at Position={initialPosition:F2} counts, Velocity={initialVelocity:F2} counts/s");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KalmanFilter-Altitude] Initialization failed: {ex.Message}");
                _isFilterInitialized = false;
            }
        }

        /// <summary>
        /// Configure Kalman Filter tuning parameters
        /// </summary>
        public void ConfigureKalmanFilter(double processNoisePosition = 0.0001, 
                                          double processNoiseVelocity = 0.01, 
                                          double measurementNoise = 1.0)
        {
            if (_positionFilter == null)
            {
                Debug.WriteLine("[KalmanFilter-Altitude] Filter not initialized - call InitializeKalmanFilter() first");
                return;
            }

            _positionFilter.SetProcessNoise(processNoisePosition, processNoiseVelocity);
            _positionFilter.SetMeasurementNoise(measurementNoise);

            Debug.WriteLine($"[KalmanFilter-Altitude] Configured: ProcNoise_Pos={processNoisePosition}, ProcNoise_Vel={processNoiseVelocity}, MeasNoise={measurementNoise}");
        }

        /// <summary>
        /// Reset Kalman Filter to specific position
        /// Useful when position discontinuity occurs (e.g., manual jog, homing)
        /// </summary>
        public void ResetKalmanFilter(double position, double velocity = 0)
        {
            if (_positionFilter == null)
            {
                Debug.WriteLine("[KalmanFilter-Altitude] Filter not initialized - call InitializeKalmanFilter() first");
                return;
            }

            _positionFilter.Reset(position, velocity);
            Debug.WriteLine($"[KalmanFilter-Altitude] Reset to Position={position:F2} counts, Velocity={velocity:F2} counts/s");
        }

        #endregion
    }
}

