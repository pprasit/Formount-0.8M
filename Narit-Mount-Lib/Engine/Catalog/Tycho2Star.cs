using System;

namespace ForkMount_WPF.Engine.Catalog
{
    /// <summary>
    /// Tycho-2 Catalog star data
    /// Based on Tycho-2 Catalogue (I/259) specification
    /// </summary>
    public class Tycho2Star
    {
        /// <summary>
        /// Tycho-2 identifier TYC1-TYC2-TYC3
        /// </summary>
        public int TYC1 { get; set; }
        public int TYC2 { get; set; }
        public int TYC3 { get; set; }
        
        /// <summary>
        /// Right Ascension J2000 (degrees)
        /// </summary>
        public double RA_deg { get; set; }
        
        /// <summary>
        /// Declination J2000 (degrees)
        /// </summary>
        public double Dec_deg { get; set; }
        
        /// <summary>
        /// Proper motion in RA*cos(Dec) (mas/yr)
        /// </summary>
        public double ProperMotionRA { get; set; }
        
        /// <summary>
        /// Proper motion in Dec (mas/yr)
        /// </summary>
        public double ProperMotionDec { get; set; }
        
        /// <summary>
        /// BT magnitude (Tycho B)
        /// </summary>
        public double BT_mag { get; set; }
        
        /// <summary>
        /// VT magnitude (Tycho V)
        /// </summary>
        public double VT_mag { get; set; }
        
        /// <summary>
        /// Johnson V magnitude (converted from BT/VT)
        /// </summary>
        public double V_mag
        {
            get
            {
                if (double.IsNaN(BT_mag) || double.IsNaN(VT_mag))
                    return double.NaN;
                
                // Conversion formula from Tycho-2 guide
                double bt_vt = BT_mag - VT_mag;
                return VT_mag - 0.090 * bt_vt;
            }
        }
        
        /// <summary>
        /// Position error in RA (mas)
        /// </summary>
        public double e_RA { get; set; }
        
        /// <summary>
        /// Position error in Dec (mas)
        /// </summary>
        public double e_Dec { get; set; }
        
        /// <summary>
        /// Mean epoch (years)
        /// </summary>
        public double Epoch { get; set; }
        
        /// <summary>
        /// Hipparcos catalog number (if exists)
        /// </summary>
        public int HIP { get; set; }
        
        /// <summary>
        /// Distance from query center (degrees) - calculated field
        /// </summary>
        public double DistanceFromCenter { get; set; }
        
        /// <summary>
        /// Returns Tycho-2 identifier string
        /// </summary>
        public string TychoID => $"TYC {TYC1}-{TYC2}-{TYC3}";
        
        /// <summary>
        /// Returns true if star is from Hipparcos catalog
        /// </summary>
        public bool IsHipparcos => HIP > 0;
        
        /// <summary>
        /// Returns magnitude for display (uses V if available, otherwise VT)
        /// </summary>
        public double DisplayMagnitude => !double.IsNaN(V_mag) ? V_mag : VT_mag;
        
        public override string ToString()
        {
            return $"{TychoID}: RA={RA_deg:F4}° Dec={Dec_deg:F4}° V={DisplayMagnitude:F2}";
        }
    }
}
