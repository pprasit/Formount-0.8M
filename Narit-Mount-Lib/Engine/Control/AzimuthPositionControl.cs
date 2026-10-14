using System;
using System.Threading;
using System.Diagnostics;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Control
{
    /// <summary>
    /// Azimuth Position Control Loop - PID Controller for Position Tracking
    /// Runs at 50Hz (20ms) on background thread
    /// Reads from AzimuthInfo (not Drive directly!) for non-blocking operation
    /// </summary>
    public class AzimuthPositionControl
    {
        private readonly AzimuthManager _drive;
        private PositionController _pidController;
        private Timer _controlTimer;
        private bool _isActive;
        private double _targetPositionDeg;
        
        // [CRITICAL] Reference to CentralizedPollingManager for sending velocity commands
        private CentralizedPollingManager _pollingManager;
        
        // Cache axis config to avoid file I/O in control loop
        private AzimuthAxisConfig _axisConfig;
        
        private const int CONTROL_LOOP_MS = 20; // 50Hz
        
        // Status message for UI (UI Timer will read these)
        public string StatusMessage { get; private set; }
        public bool HasStatusMessage { get; private set; }
        
        // [NEW] Track previous hold mode state to prevent repeated log messages
        private bool _wasInHoldMode = false;
        
        // Events
        public event EventHandler<string> ControlError;
        
        public bool IsActive => _isActive;
        public double TargetPosition => _targetPositionDeg;
        
        // [NEW] Expose Hold Mode status to UI
        public bool IsInHoldMode { get; private set; }
        
        public AzimuthPositionControl(AzimuthManager drive, AzimuthInfo info)
        {
            _drive = drive ?? throw new ArgumentNullException(nameof(drive));
            // Note: info parameter kept for backward compatibility but not used
        }
        
        /// <summary>
        /// Set CentralizedPollingManager reference (called from Azimuth_Controller)
        /// </summary>
        public void SetPollingManager(CentralizedPollingManager pollingManager)
        {
            _pollingManager = pollingManager;
            Debug.WriteLine($"[AzimuthPositionControl] CentralizedPollingManager reference set: {(_pollingManager != null ? "OK" : "NULL")}");
        }
        
        /// <summary>
        /// Start position control to target position
        /// </summary>
        public void Start(double targetPositionDeg, double maxVelocityDegSec, double kp, double ki, double kd, double deadband)
        {
            if (_isActive)
            {
                Stop();
            }
            
            _targetPositionDeg = targetPositionDeg;
            
            // Load axis config once at start (not in control loop!)
            _axisConfig = AppSettings.GetAzimuthAxisConfig();
            
            // Initialize PID Controller
            if (_pidController == null)
            {
                _pidController = new PositionController(kp, ki, kd, maxVelocityDegSec, deadband);
            }
            else
            {
                _pidController.Kp = kp;
                _pidController.Ki = ki;
                _pidController.Kd = kd;
                _pidController.MaxVelocity = maxVelocityDegSec;
                _pidController.Deadband = deadband;
                _pidController.Reset();
            }
            
            _isActive = true;
            StatusMessage = "";
            HasStatusMessage = false;
            IsInHoldMode = false; // Start in non-hold mode
            _wasInHoldMode = false; // Reset hold mode tracking
            
            // Start control loop timer
            _controlTimer = new Timer(ControlLoop, null, CONTROL_LOOP_MS, CONTROL_LOOP_MS);
            
            Debug.WriteLine($"[AzimuthPositionControl] Started - Target: {targetPositionDeg:F2}deg, MaxVel: {maxVelocityDegSec:F2}deg/s");
        }
        
        /// <summary>
        /// Stop position control
        /// </summary>
        public void Stop()
        {
            _isActive = false;
            IsInHoldMode = false; // Clear hold mode flag
            _wasInHoldMode = false; // Reset hold mode tracking
            
            _controlTimer?.Dispose();
            _controlTimer = null;
            
            // [CRITICAL] Send stop command via CentralizedPollingManager
            _drive.CurrentInfo.Command_Velocity_Deg_Sec = 0;
            _pollingManager?.SetAzimuthCommandVelocity(0);
            
            _pidController?.Reset();
            
            Debug.WriteLine("[AzimuthPositionControl] Stopped");
        }
        
        /// <summary>
        /// Update PID parameters in real-time (without stopping control)
        /// Useful for tuning parameters while control is active
        /// </summary>
        public void UpdateParameters(double kp, double ki, double kd, double maxVelocity, double deadband)
        {
            if (_pidController != null)
            {
                // Log OLD values first
                Debug.WriteLine($"[AzimuthPositionControl] Parameter Update Request:");
                Debug.WriteLine($"  OLD: Kp={_pidController.Kp:F4}, Ki={_pidController.Ki:F4}, Kd={_pidController.Kd:F4}, MaxVel={_pidController.MaxVelocity:F2}, Deadband={_pidController.Deadband:F6}");
                Debug.WriteLine($"  NEW: Kp={kp:F4}, Ki={ki:F4}, Kd={kd:F4}, MaxVel={maxVelocity:F2}, Deadband={deadband:F6}");
                
                _pidController.Kp = kp;
                _pidController.Ki = ki;
                _pidController.Kd = kd;
                _pidController.MaxVelocity = maxVelocity;
                _pidController.Deadband = deadband;
                
                // Verify values were set
                Debug.WriteLine($"  VERIFY: Kp={_pidController.Kp:F4}, Ki={_pidController.Ki:F4}, Kd={_pidController.Kd:F4}, MaxVel={_pidController.MaxVelocity:F2}, Deadband={_pidController.Deadband:F6}");
                Debug.WriteLine($"[AzimuthPositionControl] ? Parameters updated successfully!");
            }
            else
            {
                Debug.WriteLine($"[AzimuthPositionControl] ?? ERROR: _pidController is NULL - Cannot update parameters!");
            }
        }
        
        /// <summary>
        /// Update target position in real-time (re-target without stopping)
        /// </summary>
        public void UpdateTarget(double targetPositionDeg)
        {
            _targetPositionDeg = targetPositionDeg;
            IsInHoldMode = false; // Exit hold mode when retargeting
            _wasInHoldMode = false;
            
            Debug.WriteLine($"[AzimuthPositionControl] Target updated: {targetPositionDeg:F2}deg");
        }
        
        /// <summary>
        /// Control Loop - Runs at 50Hz
        /// Reads from AzimuthDrive.CurrentInfo (NOT from _info!)
        /// Writes Command Velocity to AzimuthDrive.CurrentInfo
        /// [POSITION HOLD MODE] - Never auto-stops, continues maintaining position
        /// Enables UI buttons when in hold mode to allow retargeting
        /// </summary>
        private void ControlLoop(object state)
        {
            if (!_isActive)
            {
                return;
            }
            
            try
            {
                // Use cached axis config (loaded at Start())
                
                // [CRITICAL] Read from CentralizedPollingManager's latest data
                // Don't read from _drive.CurrentInfo - it's not updated by CentralizedPollingManager!
                var azimuthInfo = _pollingManager?.AzimuthInfo;
                if (azimuthInfo == null)
                {
                    Debug.WriteLine("[AzimuthPositionControl] ERROR: AzimuthInfo is NULL from PollingManager!");
                    return;
                }
                
                double positionCounts = azimuthInfo.Raw_Position;
                
                // [SAFETY] Check if position data is valid (not zero on first run)
                if (positionCounts == 0 && azimuthInfo.Raw_Velocity == 0)
                {
                    // Data not ready yet, skip this cycle
                    Debug.WriteLine($"[PID@50Hz] Waiting for valid position data...");
                    return;
                }
                
                // [NEW] Use corrected position from MountInfo (with pointing model applied)
                double currentPositionDeg = 0;
                if (_pollingManager?.MountInfo != null)
                {
                    currentPositionDeg = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                }
                else
                {
                    // Fallback: calculate manually if MountInfo not available
                    currentPositionDeg = (positionCounts / _axisConfig.Encoder_Resolution) * 360.0;
                }
                
                // Safety check: Position limits with direction awareness
                // Allow movement if we're moving AWAY from the limit
                bool isBeyondCCWLimit = currentPositionDeg < _axisConfig.CCW_Limit_Deg;
                bool isBeyondCWLimit = currentPositionDeg > _axisConfig.CW_Limit_Deg;
                
                if (isBeyondCCWLimit || isBeyondCWLimit)
                {
                    // Check if we're trying to move away from the limit
                    double errorToTarget = _targetPositionDeg - currentPositionDeg;
                    
                    bool movingAwayCCW = isBeyondCCWLimit && errorToTarget > 0; // Moving towards positive (away from CCW limit)
                    bool movingAwayCW = isBeyondCWLimit && errorToTarget < 0;   // Moving towards negative (away from CW limit)
                    
                    if (!movingAwayCCW && !movingAwayCW)
                    {
                        // Still trying to move further into limit zone - STOP
                        Stop();
                        // Position limit exceeded - silently stop (Drive handles limits)
                        return;
                    }
                    // else: Moving away from limit - allow movement
                }
                
                // Calculate position error
                double positionError = _targetPositionDeg - currentPositionDeg;
                
                // Calculate velocity command from PID - Always runs, even within deadband
                double velocityCommand = _pidController.ComputeVelocityCommand(_targetPositionDeg, currentPositionDeg);
                
                // [CRITICAL] Send command velocity via CentralizedPollingManager ONLY
                // DO NOT write to _drive.CurrentInfo - it's not used by CentralizedPollingManager!
                if (_pollingManager != null)
                {
                    _pollingManager.SetAzimuthCommandVelocity(velocityCommand);
                }
                else
                {
                    Debug.WriteLine("[AzimuthPositionControl] ERROR: Cannot send velocity command - PollingManager is NULL!");
                }
                
                // Check if within deadband (Position Hold Mode)
                bool withinDeadband = _pidController.IsAtTarget(_targetPositionDeg, currentPositionDeg);
                
                // [NEW] Update Hold Mode status
                IsInHoldMode = withinDeadband;
                
                // [FIX] Only log when ENTERING Hold Mode (not every cycle)
                if (withinDeadband)
                {
                    // Only notify UI when first entering Hold Mode
                    if (!_wasInHoldMode)
                    {
                        SetStatusMessage($"[HOLD] Position Hold Mode - Target: {_targetPositionDeg:F2}deg, Maintaining position");
                        _wasInHoldMode = true;
                    }
                    
                    Debug.WriteLine($"[PID@50Hz] HOLD MODE - Target={_targetPositionDeg:F6}deg | Current={currentPositionDeg:F6}deg | Error={positionError:F6}deg | CmdVel={velocityCommand:F6}deg/s");
                }
                else
                {
                    // Reset flag when leaving Hold Mode
                    if (_wasInHoldMode)
                    {
                        _wasInHoldMode = false;
                    }
                    
                    Debug.WriteLine($"[PID@50Hz] MOVING - Target={_targetPositionDeg:F3}deg | Current={currentPositionDeg:F3}deg | Error={positionError:F3}deg | CmdVel={velocityCommand:F4}deg/s");
                }
                
                // Safety check: Max velocity
                double maxVelocity = _axisConfig.Max_Velocity_Deg_Sec;
                if (Math.Abs(velocityCommand) > maxVelocity)
                {
                    Stop();
                    // Velocity limit exceeded - silently stop (Drive handles limits)
                    return;
                }
                
                // [POSITION HOLD MODE] - No auto-stop when reaching target
                // Controller will continue sending small velocity commands to maintain position
                // User must manually stop via Stop Motion button OR issue new position command
                
                // Track hold mode state for logging
                _wasInHoldMode = withinDeadband;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AzimuthPositionControl] Error: {ex.Message}");
                SetStatusMessage($"[ERROR] Control loop error: {ex.Message}");
                ControlError?.Invoke(this, ex.Message);
                Stop();
            }
        }
        
        /// <summary>
        /// Set status message for UI Timer to display
        /// </summary>
        private void SetStatusMessage(string message)
        {
            StatusMessage = message;
            HasStatusMessage = true;
        }
        
        /// <summary>
        /// Clear status message (called by UI Timer after displaying)
        /// </summary>
        public void ClearStatusMessage()
        {
            HasStatusMessage = false;
        }
    }
}
