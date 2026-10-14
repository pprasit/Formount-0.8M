using System;

namespace ForkMount_WPF.Engine.PlateSolving
{
    /// <summary>
    /// Result from plate solving operation
    /// </summary>
    public class PlateSolveResult
    {
        /// <summary>
        /// Was the plate solve successful?
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Solved image center RA (J2000, radians)
        /// </summary>
        public double CenterRa_J2000_Rads { get; set; }
        
        /// <summary>
        /// Solved image center Dec (J2000, radians)
        /// </summary>
        public double CenterDec_J2000_Rads { get; set; }
        
        /// <summary>
        /// Image rotation angle (degrees)
        /// 0 = North up, 90 = East up
        /// </summary>
        public double RotationAngle_Deg { get; set; }
        
        /// <summary>
        /// Measured plate scale (arcsec/pixel)
        /// </summary>
        public double PlateScale_ArcsecPerPixel { get; set; }
        
        /// <summary>
        /// Number of stars extracted from image
        /// </summary>
        public int NumExtractedStars { get; set; }
        
        /// <summary>
        /// Number of stars matched to catalog
        /// </summary>
        public int NumMatchedStars { get; set; }
        
        /// <summary>
        /// Error message if solve failed
        /// </summary>
        public string ErrorMessage { get; set; }
        
        /// <summary>
        /// Time taken to solve (milliseconds)
        /// </summary>
        public double SolveTime_ms { get; set; }
        
        /// <summary>
        /// Plate solution object (from solver library)
        /// </summary>
        public object PlateSolutionObject { get; set; }
    }
}
