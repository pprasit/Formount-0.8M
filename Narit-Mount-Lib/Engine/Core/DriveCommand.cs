using System;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Core
{
    /// <summary>
    /// Represents a command to be sent to the Platinum Drive
    /// Used in Command Queue Architecture for non-blocking UI
    /// </summary>
    public class DriveCommand
    {
        /// <summary>
        /// Command type
        /// </summary>
        public DriveCommandType CommandType { get; set; }

        /// <summary>
        /// Target axis (1-4)
        /// </summary>
        public int AxisNumber { get; set; }

        /// <summary>
        /// Command string (e.g., "MO", "JV", "ST")
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// Command parameter (e.g., "1" for MO=1, velocity value for JV)
        /// </summary>
        public string Parameter { get; set; }

        /// <summary>
        /// Callback to execute when command completes (optional)
        /// </summary>
        public Action<bool, string> OnComplete { get; set; }

        /// <summary>
        /// Unique ID for tracking
        /// </summary>
        public Guid CommandId { get; set; }

        /// <summary>
        /// Timestamp when command was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Priority (0 = Normal, 1 = High, 2 = Emergency)
        /// </summary>
        public int Priority { get; set; }

        public DriveCommand()
        {
            CommandId = Guid.NewGuid();
            CreatedAt = NtpTimeService.Instance.GetCurrentTime();
            Priority = 0;
        }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Parameter))
            {
                return $"AX{AxisNumber}.{Command}";
            }
            else
            {
                return $"AX{AxisNumber}.{Command}={Parameter}";
            }
        }
    }

    /// <summary>
    /// Command types for categorization
    /// </summary>
    public enum DriveCommandType
    {
        /// <summary>
        /// Enable/Disable motor (MO)
        /// </summary>
        EnableDisable,

        /// <summary>
        /// Motion commands (JV, ST, PR, PA, BG)
        /// </summary>
        Motion,

        /// <summary>
        /// Read data (PX, VX, IQ, MO)
        /// </summary>
        Read,

        /// <summary>
        /// Write parameters (AC, DC, SD, SF, VH)
        /// </summary>
        WriteParameter,

        /// <summary>
        /// Emergency stop (highest priority)
        /// </summary>
        EmergencyStop,

        /// <summary>
        /// Generic command
        /// </summary>
        Generic
    }
}
