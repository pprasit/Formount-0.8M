using System;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Core
{
    /// <summary>
    /// Represents a single data point with timestamp
    /// Used for buffering high-speed data from Drive
    /// </summary>
    public class DataPoint
    {
        /// <summary>
        /// Timestamp when data was received from Drive
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Position in counts
        /// </summary>
        public double Position { get; set; }

        /// <summary>
        /// Velocity in counts/sec
        /// </summary>
        public double Velocity { get; set; }

        /// <summary>
        /// Current IQ in Amperes
        /// </summary>
        public double Current { get; set; }

        /// <summary>
        /// Command Velocity in degrees/sec (for tracking commands)
        /// </summary>
        public double CommandVelocity { get; set; }

        /// <summary>
        /// Relative timestamp in seconds (from start of session)
        /// Used for chart plotting
        /// </summary>
        public double RelativeTime { get; set; }

        /// <summary>
        /// Constructor with all parameters
        /// </summary>
        public DataPoint(double position, double velocity, double current, double commandVelocity, DateTime timestamp, double relativeTime)
        {
            Position = position;
            Velocity = velocity;
            Current = current;
            CommandVelocity = commandVelocity;
            Timestamp = timestamp;
            RelativeTime = relativeTime;
        }

        /// <summary>
        /// Default constructor
        /// </summary>
        public DataPoint()
        {
            Timestamp = NtpTimeService.Instance.GetCurrentTime();
            RelativeTime = 0;
        }
    }
}
