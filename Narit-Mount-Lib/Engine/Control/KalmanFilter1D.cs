using System;

namespace ForkMount_WPF.Engine.Control
{
    /// <summary>
    /// 1D Kalman Filter for Position/Velocity Estimation
    /// Reduces encoder noise and provides smooth position estimate
    /// 
    /// Use Case: Filter noisy encoder readings before feeding to PD controller
    /// Result: Smoother tracking, reduced jitter, better RMS error
    /// </summary>
    public class KalmanFilter1D
    {
        #region State Variables
        
        // State estimate
        private double _position;           // Filtered position
        private double _velocity;           // Estimated velocity
        
        // Covariance matrix (2x2)
        private double _p00, _p01, _p10, _p11;
        
        // Process noise covariance (Q matrix)
        private double _processNoisePosition = 0.0001;  // Small for position
        private double _processNoiseVelocity = 0.001;   // Larger for velocity
        
        // Measurement noise covariance (R)
        private double _measurementNoise = 0.1;  // Encoder noise level (degrees)
        
        // Time step
        private double _dt = 0.015; // 15ms default (67 Hz)
        
        #endregion
        
        #region Constructor
        
        public KalmanFilter1D(double initialPosition, double initialVelocity = 0)
        {
            _position = initialPosition;
            _velocity = initialVelocity;
            
            // Initialize covariance matrix (high uncertainty)
            _p00 = 1.0;
            _p01 = 0.0;
            _p10 = 0.0;
            _p11 = 1.0;
        }
        
        #endregion
        
        #region Configuration
        
        /// <summary>
        /// Set process noise (how much we trust the model)
        /// Lower = trust model more (smoother but slower response)
        /// Higher = trust model less (faster response but noisier)
        /// </summary>
        public void SetProcessNoise(double positionNoise, double velocityNoise)
        {
            _processNoisePosition = positionNoise;
            _processNoiseVelocity = velocityNoise;
        }
        
        /// <summary>
        /// Set measurement noise (how much we trust the sensor)
        /// Lower = trust sensor more
        /// Higher = trust sensor less (more filtering)
        /// </summary>
        public void SetMeasurementNoise(double noise)
        {
            _measurementNoise = noise;
        }
        
        /// <summary>
        /// Set time step for prediction
        /// </summary>
        public void SetTimeStep(double dt)
        {
            _dt = dt;
        }
        
        #endregion
        
        #region Update
        
        /// <summary>
        /// Update filter with new measurement
        /// Returns filtered position estimate
        /// </summary>
        public double Update(double measurement, double dt)
        {
            _dt = dt;
            
            // [1] PREDICTION STEP
            // Predict next state: x(k|k-1) = F * x(k-1)
            // State: [position, velocity]
            // F = [1, dt]
            //     [0, 1]
            
            double predictedPosition = _position + _velocity * _dt;
            double predictedVelocity = _velocity; // Constant velocity model
            
            // Predict covariance: P(k|k-1) = F * P(k-1) * F' + Q
            double p00_pred = _p00 + _dt * _p10 + _dt * _p01 + _dt * _dt * _p11 + _processNoisePosition;
            double p01_pred = _p01 + _dt * _p11;
            double p10_pred = _p10 + _dt * _p11;
            double p11_pred = _p11 + _processNoiseVelocity;
            
            // [2] UPDATE STEP
            // Calculate Kalman Gain: K = P * H' * (H * P * H' + R)^-1
            // H = [1, 0] (we only measure position)
            
            double innovation = measurement - predictedPosition;
            double innovationCovariance = p00_pred + _measurementNoise;
            
            double k0 = p00_pred / innovationCovariance;
            double k1 = p10_pred / innovationCovariance;
            
            // Update state: x(k|k) = x(k|k-1) + K * innovation
            _position = predictedPosition + k0 * innovation;
            _velocity = predictedVelocity + k1 * innovation;
            
            // Update covariance: P(k|k) = (I - K * H) * P(k|k-1)
            _p00 = (1 - k0) * p00_pred;
            _p01 = (1 - k0) * p01_pred;
            _p10 = p10_pred - k1 * p00_pred;
            _p11 = p11_pred - k1 * p01_pred;
            
            return _position;
        }
        
        #endregion
        
        #region Prediction
        
        /// <summary>
        /// Predict future position without updating state
        /// Useful for Model Predictive Control (MPC)
        /// </summary>
        /// <param name="lookaheadTime">Time to predict ahead (seconds)</param>
        /// <returns>Predicted position</returns>
        public double PredictPosition(double lookaheadTime)
        {
            // Use constant velocity model: x_pred = x + v * dt
            return _position + _velocity * lookaheadTime;
        }
        
        /// <summary>
        /// Predict future position uncertainty
        /// Returns standard deviation of prediction
        /// </summary>
        public double PredictPositionUncertainty(double lookaheadTime)
        {
            // Uncertainty grows with time
            // ?²_pred = ?²_pos + t² * ?²_vel + t * Q_pos
            double variance = _p00 + 
                             lookaheadTime * lookaheadTime * _p11 + 
                             lookaheadTime * _processNoisePosition;
            
            return Math.Sqrt(variance);
        }
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Get filtered position estimate
        /// </summary>
        public double Position => _position;
        
        /// <summary>
        /// Get estimated velocity
        /// </summary>
        public double Velocity => _velocity;
        
        /// <summary>
        /// Get position uncertainty (standard deviation)
        /// </summary>
        public double PositionUncertainty => Math.Sqrt(_p00);
        
        /// <summary>
        /// Get velocity uncertainty (standard deviation)
        /// </summary>
        public double VelocityUncertainty => Math.Sqrt(_p11);
        
        #endregion
        
        #region Reset
        
        /// <summary>
        /// Reset filter to new position
        /// </summary>
        public void Reset(double position, double velocity = 0)
        {
            _position = position;
            _velocity = velocity;
            
            // Reset covariance (high uncertainty)
            _p00 = 1.0;
            _p01 = 0.0;
            _p10 = 0.0;
            _p11 = 1.0;
        }
        
        #endregion
    }
}
