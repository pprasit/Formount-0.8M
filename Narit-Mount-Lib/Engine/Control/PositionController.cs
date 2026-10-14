using ForkMount_WPF.Engine.Utility;
using System;

namespace ForkMount_WPF.Engine.Control
{
    /// <summary>
    /// PID Controller for Position to Velocity Command
    /// For ForkMount Platinum Drive System
    /// </summary>
    public class PositionController
    {
        #region Fields
        private double _kp = 1.0;  // Proportional Gain
        private double _ki = 0.0;  // Integral Gain
        private double _kd = 0.1;  // Derivative Gain 
        private double _previousError = 0;
        private double _integralError = 0;
        private DateTime _previousTime;
        private double _maxVelocity = 100.0; // deg/s - Default value
        private double _deadband = 0.1; // deg - Used for stopping velocity output and "at target" detection
        private double _previousVelCmd = 0;
        #endregion

        #region Properties
        /// <summary>
        /// Proportional Gain
        /// </summary>
        public double Kp
        {
            get => _kp;
            set => _kp = value;
        }

        /// <summary>
        /// Integral Gain
        /// </summary>
        public double Ki
        {
            get => _ki;
            set => _ki = value;
        }

        /// <summary>
        /// Derivative Gain
        /// </summary>
        public double Kd
        {
            get => _kd;
            set => _kd = value;
        }

        /// <summary>
        /// Maximum Velocity Command (deg/s)
        /// THIS is the actual max velocity used by the controller
        /// </summary>
        public double MaxVelocity
        {
            get => _maxVelocity;
            set => _maxVelocity = value;
        }

        /// <summary>
        /// Deadband - Distance for stopping velocity output and "at target" detection (deg)
        /// </summary>
        public double Deadband
        {
            get => _deadband;
            set => _deadband = value;
        }
        #endregion

        #region Constructor
        public PositionController()
        {
            _previousTime = NtpTimeService.Instance.GetCurrentTime();
            LoadParametersFromSettings();
        }

        public PositionController(double kp, double ki, double kd, double maxVelocity, double deadband)
        {
            _kp = kp;
            _ki = ki;
            _kd = kd;
            _maxVelocity = maxVelocity; // [CRITICAL] Use the velocity passed in, NOT from settings!
            _deadband = deadband; // Use deadband passed in from caller (from AppSettings)
            _previousTime = NtpTimeService.Instance.GetCurrentTime();
            
            System.Diagnostics.Debug.WriteLine($"[PositionController] Initialized with: Kp={_kp:F4}, Ki={_ki:F4}, Kd={_kd:F4}, MaxVel={_maxVelocity:F2}, Deadband={_deadband:F6}");
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Load Parameters from AppSettings
        /// MaxVelocity is loaded from AxisConfig (not from PD parameters)
        /// Position Window is loaded from AxisConfig for accurate target detection
        /// </summary>
        public void LoadParametersFromSettings()
        {
            try
            {
                var pdParams = AppSettings.GetAzimuthPDControllerParameters();
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                _kp = pdParams.Kp;
                _ki = pdParams.Ki;
                _kd = pdParams.Kd;
                _maxVelocity = axisConfig.Max_Velocity_Deg_Sec; // Load from AxisConfig
                _deadband = pdParams.Deadband;
                
                System.Diagnostics.Debug.WriteLine($"[PositionController] Loaded: Kp={_kp}, Ki={_ki}, Kd={_kd}, MaxVel={_maxVelocity}, Deadband={_deadband}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load PID parameters from settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Compute Velocity Command from Position Error
        /// Simple PID Controller with Position Hold Mode
        /// - Uses standard Kp, Ki, Kd gains (no adaptive zones)
        /// - Within Deadband: Continues to send small PID commands to hold position
        /// - Beyond Deadband: Sends normal PID commands to move to target
        /// </summary>
        /// <param name="targetPos">Target position (deg)</param>
        /// <param name="currentPos">Current position (deg)</param>
        /// <returns>Velocity Command (deg/s)</returns>
        public double ComputeVelocityCommand(double targetPos, double currentPos)
        {
            // 1. Calculate Position Error
            double error = targetPos - currentPos;
            double absError = Math.Abs(error);

            // 2. Calculate dt
            DateTime now = NtpTimeService.Instance.GetCurrentTime();
            double dt = (now - _previousTime).TotalSeconds;
            if (dt <= 0) dt = 0.001; // Avoid division by zero

            // 3. Calculate Derivative (rate of change of error)
            double derivative = (error - _previousError) / dt;

            // 4. Calculate Integral (accumulated error)
            _integralError += error * dt;

            // 5. Calculate Velocity Command (Standard PID)
            double velocityCmd = (_kp * error) + (_ki * _integralError) + (_kd * derivative);

            // 6. Limit Velocity to MaxVelocity
            velocityCmd = Math.Clamp(velocityCmd, -_maxVelocity, _maxVelocity);

            // 7. Anti-windup: Reset integral if velocity is saturated
            if (Math.Abs(velocityCmd) >= _maxVelocity && Math.Sign(error) == Math.Sign(_integralError))
            {
                _integralError -= error * dt; // Remove last integration
            }

            // 8. Check if in Position Hold Mode (within deadband)
            bool inHoldMode = absError <= _deadband;

            // 9. Debug Log
            if (inHoldMode)
            {
                System.Diagnostics.Debug.WriteLine($"[PD-HOLD] Kp={_kp:F4}, Ki={_ki:F4}, Kd={_kd:F4} | Err={error:F6}° (Deadband={_deadband:F6}°), P={_kp * error:F4}, I={_ki * _integralError:F4}, D={_kd * derivative:F4}, Cmd={velocityCmd:F4}°/s");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[PD-MOVE] Kp={_kp:F4}, Ki={_ki:F4}, Kd={_kd:F4} | Err={error:F6}°, P={_kp * error:F4}, I={_ki * _integralError:F4}, D={_kd * derivative:F4}, Cmd={velocityCmd:F4}°/s");
            }

            // 10. Update Previous Values
            _previousError = error;
            _previousVelCmd = velocityCmd;
            _previousTime = now;

            return velocityCmd;
        }

        /// <summary>
        /// Reset Controller State
        /// </summary>
        public void Reset()
        {
            _previousError = 0;
            _integralError = 0;
            _previousVelCmd = 0;
            _previousTime = NtpTimeService.Instance.GetCurrentTime();
            
            System.Diagnostics.Debug.WriteLine("[PID] Controller reset");
        }

        /// <summary>
        /// Check if at Target position
        /// Uses Deadband for "at target" detection
        /// Note: Even when at target, controller continues to send hold commands
        /// </summary>
        public bool IsAtTarget(double targetPos, double currentPos)
        {
            double error = Math.Abs(targetPos - currentPos);
            bool atTarget = error <= _deadband;
            
            
            if (atTarget)
            {
                System.Diagnostics.Debug.WriteLine($"[PID] At Target! Error={error:F6}° <= Deadband={_deadband:F6}°");
            }
            
            return atTarget;
        }
        #endregion
    }
}
