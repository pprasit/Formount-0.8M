using System;

namespace ForkMount_WPF.Engine.Catalog
{
    /// <summary>
    /// Represents a star from UCAC4 catalog
    /// </summary>
    public struct UCAC4Star
    {
        public double RA_deg { get; set; }              // Right Ascension (degrees)
        public double Dec_deg { get; set; }             // Declination (degrees)
        public double Magnitude { get; set; }           // UCAC magnitude (between V and R)
        public double MagnitudeAperture { get; set; }   // UCAC aperture magnitude
        public double MagnitudeError { get; set; }      // Error in magnitude
        
        public double ProperMotionRA { get; set; }      // mas/yr
        public double ProperMotionDec { get; set; }     // mas/yr
        public double ProperMotionRA_Error { get; set; }
        public double ProperMotionDec_Error { get; set; }
        
        public double Epoch { get; set; }               // Central epoch (years since 1900)
        
        public int ObjectType { get; set; }             // 0=good star, 1-9=various flags
        public int DoubleStarFlag { get; set; }         // Combined double star flag
        
        public int UniqueID { get; set; }               // Unique star identification
        public int ZoneNumber { get; set; }             // Zone number (1-900)
        public int RecordNumber { get; set; }           // Record number in zone
        
        // 2MASS data
        public double J_mag { get; set; }               // 2MASS J magnitude
        public double H_mag { get; set; }               // 2MASS H magnitude
        public double K_mag { get; set; }               // 2MASS K_s magnitude
        
        // APASS data
        public double B_mag { get; set; }               // APASS B magnitude
        public double V_mag { get; set; }               // APASS V magnitude
        public double g_mag { get; set; }               // APASS g magnitude
        public double r_mag { get; set; }               // APASS r magnitude
        public double i_mag { get; set; }               // APASS i magnitude
        
        /// <summary>
        /// Check if star has valid magnitude
        /// </summary>
        public bool HasValidMagnitude => Magnitude < 20.0 && Magnitude > 0;
        
        /// <summary>
        /// Check if star has valid proper motion
        /// </summary>
        public bool HasValidProperMotion => ProperMotionRA_Error < 50.0 && ProperMotionDec_Error < 50.0;
        
        /// <summary>
        /// Check if star is good quality (objt = 0)
        /// </summary>
        public bool IsGoodQuality => ObjectType == 0;
        
        /// <summary>
        /// Get UCAC4 star designation
        /// </summary>
        public string Designation => $"UCAC4-{ZoneNumber:D3}-{RecordNumber:D6}";
        
        public override string ToString()
        {
            return $"{Designation} RA={RA_deg:F4}° Dec={Dec_deg:F4}° Mag={Magnitude:F2}";
        }
    }
}
