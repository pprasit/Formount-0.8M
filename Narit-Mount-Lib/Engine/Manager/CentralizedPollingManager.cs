using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Tracking;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Centralized Polling Manager for Multi-Axis Drive System
    /// Manages data polling and command sending for all axes in one place
    /// Prevents race conditions and optimizes communication bandwidth
    /// </summary>
    public class CentralizedPollingManager
    {
        #region Fields
        private PlatinumDrive _platinumDrive;
        private PlatinumDrive _altitudePlatinumDrive; // [NEW] Separate drive for Altitude axis
        private AzimuthInfo _azimuthInfo;
        private AltitudeInfo _altitudeInfo;
        private MountInfo _mountInfo; // [NEW] Centralized mount status hub
        
        // Thread synchronization
        private readonly object _azimuthLock = new object();
        private readonly object _altitudeLock = new object();
        private readonly object _mountLock = new object(); // [NEW] Lock for MountInfo
        
        // Polling control
        private CancellationTokenSource _pollingCancellationTokenSource;
        private Task _pollingTask;
        private int _pollingIntervalMs = 15; // 15ms = 66.67Hz (reduced from 100Hz to prevent bandwidth congestion when tuning)
        
        // Session tracking for relative timestamps
        private DateTime _sessionStartTime;
        
        // Cache axis configs
        private AzimuthAxisConfig _azimuthConfig;
        private AltitudeAxisConfig _altitudeConfig;
        
        // [NEW] Extra command dictionary for tuning/configuration commands
        // Dictionary ensures "Latest Value Wins" - overwrites duplicate commands
        private Dictionary<string, Func<Task<bool>>> _extraCommandDict = new Dictionary<string, Func<Task<bool>>>();
        private readonly object _commandLock = new object();

        // ── M3 / Field-Derotator Info (AX3, AX4) ───────────────────────────────────
        private M3Info              _m3Info;
        private FieldDerotatorInfo  _fieldDerotatorInfo;
        private readonly object     _m3Lock             = new object();
        private readonly object     _fieldDerotatorLock = new object();
        private int                 _lightAxisPollCounter = 0;
        private const int           LIGHT_AXIS_POLL_INTERVAL = 5; // Poll AX3/AX4 every 5 main cycles ≈ 13 Hz
        private M3AxisConfig              _m3Config;
        private FieldDerotatorAxisConfig  _fieldDerotatorConfig;

        // [NEW] Time budget constants (at 921600 baud)
        private const int MAIN_BUDGET_MS = 10;  // Main polling commands
        private const int TOTAL_BUDGET_MS = 15; // Total cycle time
        
        // [NEW] RMS Tracking Manager (Centralized RMS calculation)
        private RmsTrackingManager _rmsTrackingManager;
        
        // [NEW] Mount Manager (Dedicated mount status calculation at 200Hz)
        private MountManager _mountManager;
        
        // [NEW] Pointing Model Manager (Apply corrections to mount positions)
        private PointingModel.PointingModelManager _pointingModelManager;
        
        #endregion
        
        #region Events
        /// <summary>
        /// Event when Azimuth data is updated
        /// </summary>
        public event EventHandler<AzimuthInfo> AzimuthDataUpdated;
        
        /// <summary>
        /// Event when Altitude data is updated
        /// </summary>
        public event EventHandler<AltitudeInfo> AltitudeDataUpdated;
        
        /// <summary>
        /// Event when polling error occurs
        /// </summary>
        public event EventHandler<string> PollingError;
        #endregion
        
        #region Properties
        /// <summary>
        /// Current Azimuth Info (Thread-safe)
        /// </summary>
        public AzimuthInfo AzimuthInfo
        {
            get
            {
                lock (_azimuthLock)
                {
                    return _azimuthInfo;
                }
            }
        }
        
        /// <summary>
        /// Current Altitude Info (Thread-safe)
        /// </summary>
        public AltitudeInfo AltitudeInfo
        {
            get
            {
                lock (_altitudeLock)
                {
                    return _altitudeInfo;
                }
            }
        }
        
        /// <summary>
        /// Public access to PlatinumDrive for Enable/Disable and Stop commands (Azimuth)
        /// </summary>
        public PlatinumDrive PlatinumDrive => _platinumDrive;
        
        /// <summary>
        /// Public access to Altitude PlatinumDrive for Enable/Disable and Stop commands
        /// Returns the separate Altitude drive if available, otherwise falls back to the shared drive
        /// </summary>
        public PlatinumDrive AltitudePlatinumDrive => _altitudePlatinumDrive ?? _platinumDrive;
        
        /// <summary>
        /// Polling status
        /// </summary>
        public bool IsPolling => _pollingTask != null && !_pollingTask.IsCompleted;
        
        /// <summary>
        /// Polling Rate (Hz)
        /// </summary>
        public int PollingRate => 1000 / _pollingIntervalMs;
        
        /// <summary>
        /// RMS Tracking Manager for centralized RMS calculation
        /// </summary>
        public RmsTrackingManager RmsTrackingManager => _rmsTrackingManager;

        /// <summary>
        /// Mount Manager for dedicated mount status calculation (200Hz)
        /// </summary>
        public MountManager MountManager => _mountManager;

        /// <summary>
        /// Mount Info - Centralized status hub for both axes
        /// </summary>
        public MountInfo MountInfo
        {
            get
            {
                lock (_mountLock)
                {
                    return _mountInfo;
                }
            }
        }
        
        /// <summary>
        /// Pointing Model Manager - Apply corrections to mount positions
        /// </summary>
        public PointingModel.PointingModelManager PointingModelManager => _pointingModelManager;

        /// <summary>
        /// M3 Info — Axis 4 / FORK2 (filter wheel / port selector)
        /// Polled at ~13 Hz by CentralizedPollingManager.
        /// </summary>
        public M3Info M3Info
        {
            get { lock (_m3Lock) return _m3Info; }
        }

        /// <summary>
        /// Field-Derotator Info — Axis 3 / FORK1
        /// Drive state polled at ~13 Hz; derotation state updated by InstrumentControl.
        /// </summary>
        public FieldDerotatorInfo FieldDerotatorInfo
        {
            get { lock (_fieldDerotatorLock) return _fieldDerotatorInfo; }
        }
        #endregion
        
        #region Constructor
        public CentralizedPollingManager(PlatinumDrive platinumDrive)
            : this(platinumDrive, null)
        {
        }

        public CentralizedPollingManager(PlatinumDrive azimuthPlatinumDrive, PlatinumDrive altitudePlatinumDrive)
        {
            _platinumDrive = azimuthPlatinumDrive ?? throw new ArgumentNullException(nameof(azimuthPlatinumDrive));
            _altitudePlatinumDrive = altitudePlatinumDrive; // Can be null (falls back to shared drive)
            _azimuthInfo = new AzimuthInfo();
            _altitudeInfo = new AltitudeInfo();
            _mountInfo = new MountInfo(); // [NEW] Initialize mount status hub
            
            // [NEW] Load axis configs and setup auto-update to MountInfo
            var azConfig = AppSettings.GetAzimuthAxisConfig();
            var altConfig = AppSettings.GetAltitudeAxisConfig();
            SyncEncoderResolutionsFromDrive(azConfig, altConfig);
            
            // Setup AzimuthInfo to auto-update MountInfo when Raw_Position changes
            _azimuthInfo.SetMountInfo(_mountInfo, azConfig.Encoder_Resolution, azConfig.Position_Offset_Deg);
            
            // Setup AltitudeInfo to auto-update MountInfo when Raw_Position changes
            _altitudeInfo.SetMountInfo(_mountInfo, altConfig.Encoder_Resolution, altConfig.Position_Offset_Deg);
            
            // [NEW] Initialize RMS Tracking Manager
            _rmsTrackingManager = new RmsTrackingManager(this);
            
            // [NEW] Initialize Mount Manager (dedicated 200Hz mount status calculator)
            _mountManager = new MountManager(_mountInfo);

            // ── M3 and Field-Derotator Info ─────────────────────────────────────────
            _m3Config             = AppSettings.GetM3AxisConfig();
            _fieldDerotatorConfig = AppSettings.GetFieldDerotatorAxisConfig();
            _m3Info               = new M3Info();
            _fieldDerotatorInfo   = new FieldDerotatorInfo();
            _m3Info.SetEncoderResolution(_m3Config.Encoder_Resolution);
            _fieldDerotatorInfo.SetEncoderResolution(_fieldDerotatorConfig.Encoder_Resolution);

            // [NEW] Use global Pointing Model Manager (loaded at app startup)
            // If app didn't initialize it, create new instance as fallback
            _pointingModelManager = ForkMount_WPF.App.PointingModelManager 
                ?? new PointingModel.PointingModelManager();
            
            if (_altitudePlatinumDrive != null)
            {
                Debug.WriteLine("[CENTRALIZED] Dual-Drive mode: Azimuth and Altitude on SEPARATE drives");
            }
            else
            {
                Debug.WriteLine("[CENTRALIZED] Single-Drive mode: Azimuth and Altitude on SAME drive");
            }
        }
        #endregion

        #region Encoder Resolution Sync

        private void SyncEncoderResolutionsFromDrive(AzimuthAxisConfig azConfig, AltitudeAxisConfig altConfig)
        {
            if (_platinumDrive?.IsConnected != true)
                return;

            try
            {
                long? azResolution = _platinumDrive.ReadEncoderResolution(1);
                if (azResolution.HasValue && azResolution.Value > 0 && azConfig.Encoder_Resolution != azResolution.Value)
                {
                    azConfig.Encoder_Resolution = azResolution.Value;
                    AppSettings.SaveAzimuthAxisConfig(azConfig);
                    Debug.WriteLine($"[CENTRALIZED] Azimuth encoder resolution synced from drive CA18: {azResolution.Value}");
                }

                var altitudeDrive = _altitudePlatinumDrive ?? _platinumDrive;
                int altitudeAxisNumber = altitudeDrive == _platinumDrive ? 2 : 1;
                long? altResolution = altitudeDrive.ReadEncoderResolution(altitudeAxisNumber);
                if (altResolution.HasValue && altResolution.Value > 0 && altConfig.Encoder_Resolution != altResolution.Value)
                {
                    altConfig.Encoder_Resolution = altResolution.Value;
                    AppSettings.SaveAltitudeAxisConfig(altConfig);
                    Debug.WriteLine($"[CENTRALIZED] Altitude encoder resolution synced from drive CA18: {altResolution.Value}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CENTRALIZED] Failed to sync encoder resolution from drive: {ex.Message}");
            }
        }

        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Start centralized polling for all axes
        /// </summary>
        public void StartPolling()
        {
            if (!_platinumDrive.IsConnected)
            {
                throw new Exception("Drive is not connected");
            }
            
            if (IsPolling)
            {
                return; // Already polling
            }
            
            // Initialize session start time for relative timestamps
            _sessionStartTime = NtpTimeService.Instance.GetCurrentTime();
            
            // Load axis configs once
            _azimuthConfig = AppSettings.GetAzimuthAxisConfig();
            _altitudeConfig = AppSettings.GetAltitudeAxisConfig();
            
            // [NEW] Check pointing model status (already loaded at app startup)
            if (_pointingModelManager?.IsModelLoaded == true)
            {
                Debug.WriteLine($"[CENTRALIZED] ? Pointing model active (loaded at startup)");
            }
            else
            {
                Debug.WriteLine($"[CENTRALIZED] ?? No pointing model loaded (raw positions will be used)");
            }
            
            _pollingCancellationTokenSource = new CancellationTokenSource();
            _pollingTask = Task.Run(() => CentralizedPollingLoop(_pollingCancellationTokenSource.Token));
            
            // [NEW] Start Mount Manager (dedicated 200Hz mount status calculator)
            _mountManager?.Start();
            
            // Debug.WriteLine($"[CENTRALIZED] Polling started at {PollingRate}Hz ({_pollingIntervalMs}ms interval)");
            // Debug.WriteLine($"[CENTRALIZED] Session start time: {_sessionStartTime:HH:mm:ss.fff}");
        }
        
        /// <summary>
        /// Stop centralized polling
        /// </summary>
        public void StopPolling()
        {
            if (_pollingCancellationTokenSource != null)
            {
                _pollingCancellationTokenSource.Cancel();
            }
            
            // [NEW] Stop Mount Manager
            _mountManager?.Stop();
            
            _pollingTask?.Wait(1000); // Wait up to 1 second
            
            // Debug.WriteLine("[CENTRALIZED] Polling stopped");
        }
        
        /// <summary>
        /// Set Azimuth command velocity (Latest Value Wins - overwrites previous)
        /// [NEW ARCHITECTURE] Writes directly to AzimuthInfo.Command_Velocity_Deg_Sec
        /// AzimuthDrive.CommandSenderLoop will read and send to Drive automatically
        /// This prevents conflicts between CentralizedPollingManager and AzimuthDrive.CommandSenderLoop
        /// </summary>
        public void SetAzimuthCommandVelocity(double velocityDegSec)
        {
            // [NEW ARCHITECTURE] Simply write to Command_Velocity_Deg_Sec
            // AzimuthDrive.CommandSenderLoop will handle:
            // - Position limit checking with soft landing
            // - Sending JV/BG commands to Drive
            // - Stop detection and optimization
            lock (_azimuthLock)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = velocityDegSec;
            }
        }
        
        /// <summary>
        /// Set Altitude command velocity (Latest Value Wins - overwrites previous)
        /// [NEW ARCHITECTURE] Writes directly to AltitudeInfo.Command_Velocity_Deg_Sec
        /// AltitudeDrive.CommandSenderLoop will read and send to Drive automatically
        /// This prevents conflicts between CentralizedPollingManager and AltitudeDrive.CommandSenderLoop
        /// </summary>
        public void SetAltitudeCommandVelocity(double velocityDegSec)
        {
            // [NEW ARCHITECTURE] Simply write to Command_Velocity_Deg_Sec
            // AltitudeDrive.CommandSenderLoop will handle:
            // - Position limit checking with soft landing
            // - Sending JV/BG commands to Drive
            // - Stop detection and optimization
            lock (_altitudeLock)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = velocityDegSec;
            }
        }
        
        /// <summary>
        /// Enqueue extra command for execution in available time slot
        /// Uses Dictionary for "Latest Value Wins" - overwrites duplicate commands automatically
        /// </summary>
        /// <param name="commandKey">Unique key for command (e.g., "AX1.KP[2]", "JV[1]") - duplicates will be overwritten</param>
        /// <param name="command">Async command that returns true if successful</param>
        public void EnqueueExtraCommand(string commandKey, Func<Task<bool>> command)
        {
            lock (_commandLock)
            {
                bool isNewCommand = !_extraCommandDict.ContainsKey(commandKey);
                _extraCommandDict[commandKey] = command; // Overwrite if exists!
                
                if (isNewCommand)
                {
                    Debug.WriteLine($"[CMD] New command added: {commandKey}, Total: {_extraCommandDict.Count}");
                }
                else
                {
                    Debug.WriteLine($"[CMD] Command updated: {commandKey} (Latest Value Wins)");
                }
            }
        }
        
        /// <summary>
        /// Get current extra command dictionary size
        /// </summary>
        public int ExtraCommandQueueSize
        {
            get
            {
                lock (_commandLock)
                {
                    return _extraCommandDict.Count;
                }
            }
        }
        
        #endregion
        
        #region Private Polling Methods
        
        /// <summary>
        /// Centralized Polling Loop - Handles all axes in sequence
        /// Order: Read Data (with Extra Commands in available slot) ? Repeat
        /// </summary>
        private async Task CentralizedPollingLoop(CancellationToken cancellationToken)
        {
            Debug.WriteLine($"[CENTRALIZED] Starting polling loop at {PollingRate}Hz (15ms interval)");
            Debug.WriteLine($"[CENTRALIZED] Baudrate: 921600 | Main Budget: {MAIN_BUDGET_MS}ms | Total Budget: {TOTAL_BUDGET_MS}ms");
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_pollingIntervalMs, cancellationToken);
                    
                    // === SINGLE PHASE: READ DATA + EXTRA COMMANDS ===
                    // All handled in ReadAllAxesData() with time budget
                    await ReadAllAxesData();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CENTRALIZED] Polling error: {ex.Message}");
                    PollingError?.Invoke(this, ex.Message);
                }
            }
            
            Debug.WriteLine("[CENTRALIZED] Polling loop stopped");
        }
        
        /// <summary>
        /// Read data from all axes SEQUENTIALLY with time budget tracking
        /// Sequential is better because Elmo Library serializes commands internally anyway
        /// At 921600 baud: ~0.5-1ms per command, 9 commands = 5-9ms
        /// </summary>
        private async Task ReadAllAxesData()
        {
            var sw = Stopwatch.StartNew();
            
            try
            {
                // === MAIN SLOT: Sequential polling commands ===
                // Send commands one by one (Elmo Library serializes internally anyway)
                
                // Determine which drive to use for Altitude
                // If separate Altitude drive exists, use it with Axis 1 commands
                // Otherwise, use shared drive with Axis 2 commands
                var altDrive = _altitudePlatinumDrive ?? _platinumDrive;
                int altAxisNumber = _altitudePlatinumDrive != null ? PlatinumDrive.Axis.AZIMUTH : PlatinumDrive.Axis.ALTITUDE;
                // Note: On a separate single-axis drive, Altitude is Axis 1 (AZIMUTH constant = 1)
                
                // Azimuth commands (always on primary drive, Axis 1)
                var azPos = await _platinumDrive.ReadPositionAsync(PlatinumDrive.Axis.AZIMUTH);
                var azVel = await _platinumDrive.ReadVelocityAsync(PlatinumDrive.Axis.AZIMUTH);
                var azCur = await _platinumDrive.ReadCurrentIQAsync(PlatinumDrive.Axis.AZIMUTH);
                var azEn = await _platinumDrive.ReadDriveEnabledAsync(PlatinumDrive.Axis.AZIMUTH);
                
                // Read IP (Digital Input) for sensor status
                var azIpResult = await _platinumDrive.SendRawCommandAsync($"AX{PlatinumDrive.Axis.AZIMUTH}.IP", 500);
                
                // Altitude commands (on separate drive if available, otherwise shared drive)
                var altPos = await altDrive.ReadPositionAsync(altAxisNumber);
                var altVel = await altDrive.ReadVelocityAsync(altAxisNumber);
                var altCur = await altDrive.ReadCurrentIQAsync(altAxisNumber);
                var altEn = await altDrive.ReadDriveEnabledAsync(altAxisNumber);
                
                long mainSlotTime = sw.ElapsedMilliseconds;
                
                // Timestamp NOW - when we got all responses
                DateTime timestamp = NtpTimeService.Instance.GetCurrentTime();
                double relativeTime = (timestamp - _sessionStartTime).TotalSeconds;
                
                // Parse Azimuth results
                var azimuthData = new AzimuthInfo();
                azimuthData.Raw_Position = azPos ?? 0;
                azimuthData.Raw_Velocity = azVel ?? 0;
                azimuthData.Current_IQ = azCur ?? 0;
                azimuthData.Is_Drive_Enabled = azEn ?? false;
                
                // Parse IP (Digital Input) value and update sensor status
                if (azIpResult.success && int.TryParse(azIpResult.response?.Trim(), out int ipValue))
                {
                    azimuthData.CW_Sensor_Active = (ipValue == 65537);
                    azimuthData.CCW_Sensor_Active = (ipValue == 131073);
                }
                else
                {
                    if (NtpTimeService.Instance.GetCurrentTime().Second % 10 == 0 && NtpTimeService.Instance.GetCurrentTime().Millisecond < 100)
                    {
                        Debug.WriteLine($"[CENTRALIZED] IP read failed - success={azIpResult.success}, response='{azIpResult.response}'");
                    }
                }
                
                // Add data point to buffer (for chart plotting)
                var azDataPoint = new DataPoint(
                    azimuthData.Raw_Position,
                    azimuthData.Raw_Velocity,
                    azimuthData.Current_IQ,
                    _azimuthInfo.Command_Velocity_Deg_Sec,
                    timestamp,
                    relativeTime
                );
                azimuthData.DataBuffer = _azimuthInfo.DataBuffer;
                azimuthData.DataBuffer.Add(azDataPoint);
                
                // Update with lock
                lock (_azimuthLock)
                {
                    azimuthData.Command_Velocity_Deg_Sec = _azimuthInfo.Command_Velocity_Deg_Sec;
                    azimuthData.TrackingRmsError_Arcsec = _azimuthInfo.TrackingRmsError_Arcsec;
                    azimuthData.TrackingSampleCount = _azimuthInfo.TrackingSampleCount;
                    azimuthData.TrackingMode = _azimuthInfo.TrackingMode;
                    azimuthData.IsTracking = _azimuthInfo.IsTracking;
                    azimuthData.IsUnwrapping = _azimuthInfo.IsUnwrapping;
                    _azimuthInfo = azimuthData;
                }
                
                AzimuthDataUpdated?.Invoke(this, azimuthData);
                
                // Parse Altitude results
                var altitudeData = new AltitudeInfo();
                altitudeData.Raw_Position = altPos ?? 0;
                altitudeData.Raw_Velocity = altVel ?? 0;
                altitudeData.Current_IQ = altCur ?? 0;
                altitudeData.Is_Drive_Enabled = altEn ?? false;
                
                // Add data point to buffer (for chart plotting)
                var altDataPoint = new DataPoint(
                    altitudeData.Raw_Position,
                    altitudeData.Raw_Velocity,
                    altitudeData.Current_IQ,
                    _altitudeInfo.Command_Velocity_Deg_Sec,
                    timestamp,
                    relativeTime
                );
                altitudeData.DataBuffer = _altitudeInfo.DataBuffer;
                altitudeData.DataBuffer.Add(altDataPoint);
                
                // Update with lock
                lock (_altitudeLock)
                {
                    altitudeData.Command_Velocity_Deg_Sec = _altitudeInfo.Command_Velocity_Deg_Sec;
                    altitudeData.TrackingRmsError_Arcsec = _altitudeInfo.TrackingRmsError_Arcsec;
                    altitudeData.TrackingSampleCount = _altitudeInfo.TrackingSampleCount;
                    altitudeData.TrackingMode = _altitudeInfo.TrackingMode;
                    altitudeData.IsTracking = _altitudeInfo.IsTracking;
                    _altitudeInfo = altitudeData;
                }
                
                AltitudeDataUpdated?.Invoke(this, altitudeData);

                // === LIGHT AXIS POLL: AX3 (Field-Derotator) and AX4 (M3) every N cycles ===
                _lightAxisPollCounter++;
                if (_lightAxisPollCounter >= LIGHT_AXIS_POLL_INTERVAL)
                {
                    _lightAxisPollCounter = 0;

                    // -- Field-Derotator (AX3) --
                    var ax3Pos = await _platinumDrive.SendRawCommandAsync($"AX{_fieldDerotatorConfig.Axis_Number}.PX", 300);
                    var ax3Vel = await _platinumDrive.SendRawCommandAsync($"AX{_fieldDerotatorConfig.Axis_Number}.VX", 300);
                    var ax3Mo  = await _platinumDrive.SendRawCommandAsync($"AX{_fieldDerotatorConfig.Axis_Number}.MO", 300);

                    if (ax3Pos.success && double.TryParse(ax3Pos.response?.Trim(), out double fd_pos) &&
                        ax3Vel.success && double.TryParse(ax3Vel.response?.Trim(), out double fd_vel) &&
                        ax3Mo.success  && int.TryParse   (ax3Mo.response?.Trim(),  out int    fd_mo))
                    {
                        lock (_fieldDerotatorLock)
                            _fieldDerotatorInfo.UpdateFromDrive(fd_pos, fd_vel, fd_mo == 1);
                    }

                    // -- M3 (AX4) --
                    var ax4Pos = await _platinumDrive.SendRawCommandAsync($"AX{_m3Config.Axis_Number}.PX", 300);
                    var ax4Vel = await _platinumDrive.SendRawCommandAsync($"AX{_m3Config.Axis_Number}.VX", 300);
                    var ax4Mo  = await _platinumDrive.SendRawCommandAsync($"AX{_m3Config.Axis_Number}.MO", 300);

                    if (ax4Pos.success && double.TryParse(ax4Pos.response?.Trim(), out double m3_pos) &&
                        ax4Vel.success && double.TryParse(ax4Vel.response?.Trim(), out double m3_vel) &&
                        ax4Mo.success  && int.TryParse   (ax4Mo.response?.Trim(),  out int    m3_mo))
                    {
                        lock (_m3Lock)
                            _m3Info.Update(m3_pos, m3_vel, m3_mo == 1);
                    }
                }

                // Update MountInfo with raw positions AND apply pointing model atomically
                double azimuthRaw = (azimuthData.Raw_Position / (double)_azimuthConfig.Encoder_Resolution) * 360.0 
                                    + _azimuthConfig.Position_Offset_Deg;
                double altitudeRaw = (altitudeData.Raw_Position / (double)_altitudeConfig.Encoder_Resolution) * 360.0 
                                     + _altitudeConfig.Position_Offset_Deg;
                
                _mountInfo.UpdateRawPosition(azimuthRaw, altitudeRaw, azimuthData, altitudeData);
                
                // === CONTINUOUS LIMIT MONITORING ===
                CheckAndEnforceLimits();
                
                // === SEND COMMAND VELOCITIES ===
                await SendCommandVelocities();
                
                // === EXTRA SLOT: Process extra commands if time available ===
                int extraCommandsProcessed = 0;
                while (sw.ElapsedMilliseconds < TOTAL_BUDGET_MS)
                {
                    KeyValuePair<string, Func<Task<bool>>>? commandPair = null;
                    
                    lock (_commandLock)
                    {
                        if (_extraCommandDict.Count == 0)
                            break;
                        
                        var firstItem = _extraCommandDict.First();
                        commandPair = firstItem;
                        _extraCommandDict.Remove(firstItem.Key);
                    }
                    
                    if (commandPair.HasValue)
                    {
                        try
                        {
                            bool success = await commandPair.Value.Value();
                            extraCommandsProcessed++;
                            Debug.WriteLine($"[EXTRA] {commandPair.Value.Key}: {(success ? "SUCCESS" : "FAILED")} at {sw.ElapsedMilliseconds}ms");
                        }
                        catch (Exception cmdEx)
                        {
                            Debug.WriteLine($"[EXTRA] {commandPair.Value.Key} failed: {cmdEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CENTRALIZED] Error reading axes data: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Continuous Limit Monitor - Checks position limits EVERY polling cycle
        /// If motor is moving beyond limit, immediately sends reduced velocity or stops
        /// 
        /// [SOFT LIMIT ZONE] 15� before hard limit - velocity limited to 10�/s
        /// [HARD LIMIT] At limit boundary - velocity forced to 0
        /// </summary>
        private void CheckAndEnforceLimits()
        {
            const double SOFT_LIMIT_ZONE_DEG = 15.0;  // Distance from hard limit to start slowing down
            const double SOFT_LIMIT_MAX_VEL = 10.0;   // Max velocity in soft limit zone (deg/s)
            
            try
            {
                // ============ AZIMUTH LIMIT CHECK ============
                double azCurrentPos = (_azimuthInfo.Raw_Position / _azimuthConfig.Encoder_Resolution) * 360.0;
                double azCommandVel = _azimuthInfo.Command_Velocity_Deg_Sec;
                
                // Calculate soft limit boundaries
                double azSoftLimitCCW = _azimuthConfig.CCW_Limit_Deg + SOFT_LIMIT_ZONE_DEG; // e.g., -300 + 15 = -285
                double azSoftLimitCW = _azimuthConfig.CW_Limit_Deg - SOFT_LIMIT_ZONE_DEG;   // e.g., +300 - 15 = +285
                
                // Check hard limits (absolute stop)
                // ONLY block motion if moving FURTHER into limit, not away from it!
                bool azBeyondHardCCW = azCurrentPos <= _azimuthConfig.CCW_Limit_Deg;
                bool azBeyondHardCW = azCurrentPos >= _azimuthConfig.CW_Limit_Deg;
                bool azMovingIntoHardCCW = azBeyondHardCCW && azCommandVel < 0; // At/beyond CCW limit, trying to go CCW more
                bool azMovingIntoHardCW = azBeyondHardCW && azCommandVel > 0;   // At/beyond CW limit, trying to go CW more
                
                if (azMovingIntoHardCCW || azMovingIntoHardCW)
                {
                    // HARD LIMIT VIOLATED - STOP IMMEDIATELY!
                    EnqueueExtraCommand("LIMIT_STOP[1]", async () =>
                    {
                        string resp;
                        bool jpSuccess = _platinumDrive.SendRawCommand("AX1.JP=0", out resp, 500);
                        bool bgSuccess = _platinumDrive.SendRawCommand("AX1.BG", out resp, 500);
                        
                        if (jpSuccess && bgSuccess)
                        {
                            lock (_azimuthLock)
                            {
                                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                            }
                            Debug.WriteLine($"[HARD LIMIT!] Azimuth STOPPED at {azCurrentPos:F2}�");
                        }
                        return jpSuccess && bgSuccess;
                    });
                }
                // Check soft limits (velocity reduction)
                else
                {
                    bool azInSoftCCW = azCurrentPos < azSoftLimitCCW && azCurrentPos >= _azimuthConfig.CCW_Limit_Deg;
                    bool azInSoftCW = azCurrentPos > azSoftLimitCW && azCurrentPos <= _azimuthConfig.CW_Limit_Deg;
                    bool azMovingIntoSoftCCW = azInSoftCCW && azCommandVel < 0 && Math.Abs(azCommandVel) > SOFT_LIMIT_MAX_VEL;
                    bool azMovingIntoSoftCW = azInSoftCW && azCommandVel > 0 && azCommandVel > SOFT_LIMIT_MAX_VEL;
                    
                    if (azMovingIntoSoftCCW)
                    {
                        // In soft limit zone CCW - reduce velocity to max 10�/s
                        double reducedVel = -SOFT_LIMIT_MAX_VEL;
                        Debug.WriteLine($"[SOFT LIMIT] Azimuth CCW: Pos={azCurrentPos:F2}� | Vel={azCommandVel:F2}�/s ? Reduced to {reducedVel:F2}�/s");
                        
                        EnqueueExtraCommand("SOFT_LIMIT[1]", async () =>
                        {
                            double velocityCounts = (reducedVel / 360.0) * _azimuthConfig.Encoder_Resolution;
                            string resp;
                            bool jpSuccess = _platinumDrive.SendRawCommand($"AX1.JP={((long)velocityCounts)}", out resp, 500);
                            bool bgSuccess = _platinumDrive.SendRawCommand("AX1.BG", out resp, 500);
                            
                            if (jpSuccess && bgSuccess)
                            {
                                lock (_azimuthLock)
                                {
                                    _azimuthInfo.Command_Velocity_Deg_Sec = reducedVel;
                                }
                            }
                            return jpSuccess && bgSuccess;
                        });
                    }
                    else if (azMovingIntoSoftCW)
                    {
                        // In soft limit zone CW - reduce velocity to max 10�/s
                        double reducedVel = SOFT_LIMIT_MAX_VEL;
                        Debug.WriteLine($"[SOFT LIMIT] Azimuth CW: Pos={azCurrentPos:F2}� | Vel={azCommandVel:F2}�/s ? Reduced to {reducedVel:F2}�/s");
                        
                        EnqueueExtraCommand("SOFT_LIMIT[1]", async () =>
                        {
                            double velocityCounts = (reducedVel / 360.0) * _azimuthConfig.Encoder_Resolution;
                            string resp;
                            bool jpSuccess = _platinumDrive.SendRawCommand($"AX1.JP={((long)velocityCounts)}", out resp, 500);
                            bool bgSuccess = _platinumDrive.SendRawCommand("AX1.BG", out resp, 500);
                            
                            if (jpSuccess && bgSuccess)
                            {
                                lock (_azimuthLock)
                                {
                                    _azimuthInfo.Command_Velocity_Deg_Sec = reducedVel;
                                }
                            }
                            return jpSuccess && bgSuccess;
                        });
                    }
                }
                
                // ============ ALTITUDE LIMIT CHECK ============
                double altCurrentPos = (_altitudeInfo.Raw_Position / _altitudeConfig.Encoder_Resolution) * 360.0;
                double altCommandVel = _altitudeInfo.Command_Velocity_Deg_Sec;
                
                // Determine which drive and axis number to use for Altitude
                var altLimitDrive = _altitudePlatinumDrive ?? _platinumDrive;
                int altLimitAxisNumber = _altitudePlatinumDrive != null ? 1 : 2;
                
                // Calculate soft limit boundaries
                double altSoftLimitDown = _altitudeConfig.Down_Limit_Deg + SOFT_LIMIT_ZONE_DEG;
                double altSoftLimitUp = _altitudeConfig.Up_Limit_Deg - SOFT_LIMIT_ZONE_DEG;
                
                // Check hard limits (absolute stop)
                bool altBeyondHardDown = altCurrentPos <= _altitudeConfig.Down_Limit_Deg;
                bool altBeyondHardUp = altCurrentPos >= _altitudeConfig.Up_Limit_Deg;
                bool altMovingIntoHardDown = altBeyondHardDown && altCommandVel < 0;
                bool altMovingIntoHardUp = altBeyondHardUp && altCommandVel > 0;
                
                if (altMovingIntoHardDown || altMovingIntoHardUp)
                {
                    Debug.WriteLine($"[HARD LIMIT!] Altitude EMERGENCY STOP: Pos={altCurrentPos:F2}deg | Vel={altCommandVel:F2}deg/s | Hard Limits=[{_altitudeConfig.Down_Limit_Deg}, {_altitudeConfig.Up_Limit_Deg}]");
                    
                    EnqueueExtraCommand("LIMIT_STOP[2]", async () =>
                    {
                        string resp;
                        bool jvSuccess = altLimitDrive.SendRawCommand($"AX{altLimitAxisNumber}.JP=0", out resp, 500);
                        bool bgSuccess = altLimitDrive.SendRawCommand($"AX{altLimitAxisNumber}.BG", out resp, 500);
                        
                        if (jvSuccess && bgSuccess)
                        {
                            lock (_altitudeLock)
                            {
                                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                            }
                            Debug.WriteLine($"[HARD LIMIT!] Altitude STOPPED at {altCurrentPos:F2}deg");
                        }
                        return jvSuccess && bgSuccess;
                    });
                }
                // Check soft limits (velocity reduction)
                else
                {
                    bool altInSoftDown = altCurrentPos < altSoftLimitDown && altCurrentPos >= _altitudeConfig.Down_Limit_Deg;
                    bool altInSoftUp = altCurrentPos > altSoftLimitUp && altCurrentPos <= _altitudeConfig.Up_Limit_Deg;
                    bool altMovingIntoSoftDown = altInSoftDown && altCommandVel < 0 && Math.Abs(altCommandVel) > SOFT_LIMIT_MAX_VEL;
                    bool altMovingIntoSoftUp = altInSoftUp && altCommandVel > 0 && altCommandVel > SOFT_LIMIT_MAX_VEL;
                    
                    if (altMovingIntoSoftDown)
                    {
                        double reducedVel = -SOFT_LIMIT_MAX_VEL;
                        Debug.WriteLine($"[SOFT LIMIT] Altitude DOWN: Pos={altCurrentPos:F2}deg | Vel={altCommandVel:F2}deg/s -> Reduced to {reducedVel:F2}deg/s");
                        
                        EnqueueExtraCommand("SOFT_LIMIT[2]", async () =>
                        {
                            double velocityCounts = (reducedVel / 360.0) * _altitudeConfig.Encoder_Resolution;
                            string resp;
                            bool jvSuccess = altLimitDrive.SendRawCommand($"AX{altLimitAxisNumber}.JP={(long)velocityCounts}", out resp, 500);
                            bool bgSuccess = altLimitDrive.SendRawCommand($"AX{altLimitAxisNumber}.BG", out resp, 500);
                            
                            if (jvSuccess && bgSuccess)
                            {
                                lock (_altitudeLock)
                                {
                                    _altitudeInfo.Command_Velocity_Deg_Sec = reducedVel;
                                }
                            }
                            return jvSuccess && bgSuccess;
                        });
                    }
                    else if (altMovingIntoSoftUp)
                    {
                        double reducedVel = SOFT_LIMIT_MAX_VEL;
                        Debug.WriteLine($"[SOFT LIMIT] Altitude UP: Pos={altCurrentPos:F2}deg | Vel={altCommandVel:F2}deg/s -> Reduced to {reducedVel:F2}deg/s");
                        
                        EnqueueExtraCommand("SOFT_LIMIT[2]", async () =>
                        {
                            double velocityCounts = (reducedVel / 360.0) * _altitudeConfig.Encoder_Resolution;
                            string resp;
                            bool jvSuccess = altLimitDrive.SendRawCommand($"AX{altLimitAxisNumber}.JP={(long)velocityCounts}", out resp, 500);
                            bool bgSuccess = altLimitDrive.SendRawCommand($"AX{altLimitAxisNumber}.BG", out resp, 500);
                            
                            if (jvSuccess && bgSuccess)
                            {
                                lock (_altitudeLock)
                                {
                                    _altitudeInfo.Command_Velocity_Deg_Sec = reducedVel;
                                }
                            }
                            return jvSuccess && bgSuccess;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LIMIT] Error in limit checking: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send Command Velocities - Sends JV/BG commands for both axes
        /// Reads Command_Velocity_Deg_Sec from AzimuthInfo and AltitudeInfo
        /// [NEW] Centralized command sender to replace individual Drive Command Sender Loops
        /// [OPTIMIZATION] Only sends commands when velocity changes or Drive is enabled
        /// </summary>
        private double _lastAzimuthVelocity = double.NaN;
        private double _lastAltitudeVelocity = double.NaN;
        
        private async Task SendCommandVelocities()
        {
            try
            {
                // Determine which drive and axis number to use for Altitude
                var altDrive = _altitudePlatinumDrive ?? _platinumDrive;
                int altAxisNumber = _altitudePlatinumDrive != null ? 1 : 2;
                
                // === AZIMUTH COMMAND VELOCITY ===
                double azCommandVel;
                bool azDriveEnabled;
                lock (_azimuthLock)
                {
                    azCommandVel = _azimuthInfo.Command_Velocity_Deg_Sec;
                    azDriveEnabled = _azimuthInfo.Is_Drive_Enabled;
                }
                
                // Only send if Drive is enabled AND (velocity changed OR not zero)
                if (azDriveEnabled && (azCommandVel != _lastAzimuthVelocity || azCommandVel != 0))
                {
                    // Skip if velocity is 0 AND last velocity was also 0 (optimization)
                    if (!(azCommandVel == 0 && _lastAzimuthVelocity == 0))
                    {
                        double azVelCounts = (azCommandVel / 360.0) * _azimuthConfig.Encoder_Resolution;
                        string response;
                        
                        bool jpSuccess = _platinumDrive.SendRawCommand($"AX1.JP={(long)azVelCounts}", out response, 100);
                        bool bgSuccess = _platinumDrive.SendRawCommand("AX1.BG", out response, 100);
                        
                        if (jpSuccess && bgSuccess)
                        {
                            _lastAzimuthVelocity = azCommandVel;
                        }
                    }
                }
                
                // === ALTITUDE COMMAND VELOCITY ===
                double altCommandVel;
                bool altDriveEnabled;
                lock (_altitudeLock)
                {
                    altCommandVel = _altitudeInfo.Command_Velocity_Deg_Sec;
                    altDriveEnabled = _altitudeInfo.Is_Drive_Enabled;
                }
                
                // Only send if Drive is enabled AND (velocity changed OR not zero)
                if (altDriveEnabled && (altCommandVel != _lastAltitudeVelocity || altCommandVel != 0))
                {
                    // Skip if velocity is 0 AND last velocity was also 0 (optimization)
                    if (!(altCommandVel == 0 && _lastAltitudeVelocity == 0))
                    {
                        double altVelCounts = (altCommandVel / 360.0) * _altitudeConfig.Encoder_Resolution;
                        
                        string jpResponse = "";
                        string bgResponse = "";
                        
                        string jpCommand = $"AX{altAxisNumber}.JP={(long)altVelCounts}";
                        
                        bool jpSuccess = altDrive.SendRawCommand(jpCommand, out jpResponse, 100);
                        bool bgSuccess = altDrive.SendRawCommand($"AX{altAxisNumber}.BG", out bgResponse, 100);
                        
                        if (jpSuccess && bgSuccess)
                        {
                            _lastAltitudeVelocity = altCommandVel;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CMD] Error sending command velocities: {ex.Message}");
            }
        }
        
        #endregion
    }
}
