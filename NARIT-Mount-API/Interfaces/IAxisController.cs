using NARIT.Mount.API.Events;
using NARIT.Mount.API.Models;

namespace NARIT.Mount.API.Interfaces
{
    /// <summary>
    /// Single-axis controller interface.
    /// Allows independent control of Azimuth or Altitude axis.
    /// </summary>
    public interface IAxisController
    {
        /// <summary>Which axis this controller manages</summary>
        AxisType Axis { get; }

        /// <summary>Get current axis state snapshot</summary>
        AxisState GetState();

        /// <summary>Enable the drive motor for this axis</summary>
        void Enable();

        /// <summary>Disable the drive motor for this axis</summary>
        void Disable();

        /// <summary>Whether the drive is currently enabled</summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Command a position move (goto) on this axis.
        /// </summary>
        /// <param name="positionDeg">Target position in degrees</param>
        /// <param name="velocityDegPerSec">Slew velocity in degrees/second</param>
        void GotoPosition(double positionDeg, double velocityDegPerSec);

        /// <summary>
        /// Command a continuous velocity move on this axis.
        /// The axis will move at the specified velocity until stopped.
        /// </summary>
        /// <param name="velocityDegPerSec">Velocity in degrees/second (positive = CW)</param>
        void MoveVelocity(double velocityDegPerSec);

        /// <summary>
        /// Jog the axis at a specified velocity (same as MoveVelocity but semantically for manual control).
        /// </summary>
        /// <param name="velocityDegPerSec">Jog velocity in degrees/second</param>
        void Jog(double velocityDegPerSec);

        /// <summary>
        /// Stop all motion on this axis.
        /// </summary>
        void Stop();

        /// <summary>Event raised when drive enable state changes</summary>
        event EventHandler<DriveStateChangedEventArgs> DriveStateChanged;
    }
}
