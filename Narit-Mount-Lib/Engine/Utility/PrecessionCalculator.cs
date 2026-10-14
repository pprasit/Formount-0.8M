using System;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Precession calculator using IAU 2006 precession model
    /// Converts between apparent coordinates (epoch of date) and J2000.0
    /// 
    /// References:
    /// - IAU 2006 precession model (Capitaine et al. 2003)
    /// - USNO Circular 179
    /// - Astronomical Algorithms (Jean Meeus)
    /// </summary>
    public static class PrecessionCalculator
    {
        /// <summary>
        /// Convert apparent coordinates (epoch of date) to J2000.0
        /// This matches PWLib's .NowToJ2000() method
        /// </summary>
        /// <param name="raApp_Rads">Apparent RA in radians</param>
        /// <param name="decApp_Rads">Apparent Dec in radians</param>
        /// <param name="julianDate">Julian date of observation</param>
        /// <returns>J2000 coordinates (RA, Dec) in radians</returns>
        public static (double raJ2000_Rads, double decJ2000_Rads) ApparentToJ2000(
            double raApp_Rads, 
            double decApp_Rads, 
            double julianDate)
        {
            // Calculate centuries from J2000.0
            double T = (julianDate - 2451545.0) / 36525.0;
            
            // Convert to Cartesian coordinates for rotation
            double cosRA = Math.Cos(raApp_Rads);
            double sinRA = Math.Sin(raApp_Rads);
            double cosDec = Math.Cos(decApp_Rads);
            double sinDec = Math.Sin(decApp_Rads);
            
            double x = cosDec * cosRA;
            double y = cosDec * sinRA;
            double z = sinDec;
            
            // Apply inverse precession matrix (Date ? J2000)
            // Using IAU 2006 precession angles
            double zeta, z_angle, theta;
            CalculatePrecessionAngles(T, out zeta, out z_angle, out theta);
            
            // Build rotation matrix (inverse: R^T)
            // R = Rz(-z) * Ry(theta) * Rz(-zeta)
            // R^T = Rz(zeta) * Ry(-theta) * Rz(z)
            
            double cosZeta = Math.Cos(zeta);
            double sinZeta = Math.Sin(zeta);
            double cosTheta = Math.Cos(-theta);  // Negative for inverse
            double sinTheta = Math.Sin(-theta);
            double cosZ = Math.Cos(z_angle);
            double sinZ = Math.Sin(z_angle);
            
            // First rotation: Rz(zeta)
            double x1 = x * cosZeta - y * sinZeta;
            double y1 = x * sinZeta + y * cosZeta;
            double z1 = z;
            
            // Second rotation: Ry(-theta)
            double x2 = x1 * cosTheta + z1 * sinTheta;
            double y2 = y1;
            double z2 = -x1 * sinTheta + z1 * cosTheta;
            
            // Third rotation: Rz(z)
            double x3 = x2 * cosZ - y2 * sinZ;
            double y3 = x2 * sinZ + y2 * cosZ;
            double z3 = z2;
            
            // Convert back to spherical coordinates
            double raJ2000 = Math.Atan2(y3, x3);
            double decJ2000 = Math.Asin(z3);
            
            // Normalize RA to [0, 2?)
            if (raJ2000 < 0) raJ2000 += 2.0 * Math.PI;
            
            return (raJ2000, decJ2000);
        }
        
        /// <summary>
        /// Convert J2000.0 to apparent coordinates (epoch of date)
        /// This matches PWLib's .J2000ToNow() method
        /// </summary>
        /// <param name="raJ2000_Rads">J2000 RA in radians</param>
        /// <param name="decJ2000_Rads">J2000 Dec in radians</param>
        /// <param name="julianDate">Julian date for apparent position</param>
        /// <returns>Apparent coordinates (RA, Dec) in radians</returns>
        public static (double raApp_Rads, double decApp_Rads) J2000ToApparent(
            double raJ2000_Rads, 
            double decJ2000_Rads, 
            double julianDate)
        {
            // Calculate centuries from J2000.0
            double T = (julianDate - 2451545.0) / 36525.0;
            
            // Convert to Cartesian coordinates
            double cosRA = Math.Cos(raJ2000_Rads);
            double sinRA = Math.Sin(raJ2000_Rads);
            double cosDec = Math.Cos(decJ2000_Rads);
            double sinDec = Math.Sin(decJ2000_Rads);
            
            double x = cosDec * cosRA;
            double y = cosDec * sinRA;
            double z = sinDec;
            
            // Apply precession matrix (J2000 ? Date)
            double zeta, z_angle, theta;
            CalculatePrecessionAngles(T, out zeta, out z_angle, out theta);
            
            // Build rotation matrix
            // R = Rz(-z) * Ry(theta) * Rz(-zeta)
            
            double cosZeta = Math.Cos(-zeta);  // Negative for forward
            double sinZeta = Math.Sin(-zeta);
            double cosTheta = Math.Cos(theta);
            double sinTheta = Math.Sin(theta);
            double cosZ = Math.Cos(-z_angle);  // Negative for forward
            double sinZ = Math.Sin(-z_angle);
            
            // First rotation: Rz(-zeta)
            double x1 = x * cosZeta - y * sinZeta;
            double y1 = x * sinZeta + y * cosZeta;
            double z1 = z;
            
            // Second rotation: Ry(theta)
            double x2 = x1 * cosTheta + z1 * sinTheta;
            double y2 = y1;
            double z2 = -x1 * sinTheta + z1 * cosTheta;
            
            // Third rotation: Rz(-z)
            double x3 = x2 * cosZ - y2 * sinZ;
            double y3 = x2 * sinZ + y2 * cosZ;
            double z3 = z2;
            
            // Convert back to spherical coordinates
            double raApp = Math.Atan2(y3, x3);
            double decApp = Math.Asin(z3);
            
            // Normalize RA to [0, 2?)
            if (raApp < 0) raApp += 2.0 * Math.PI;
            
            return (raApp, decApp);
        }
        
        /// <summary>
        /// Calculate IAU 2006 precession angles
        /// Based on Capitaine et al. (2003) A&A 412, 567-586
        /// </summary>
        private static void CalculatePrecessionAngles(double T, out double zeta, out double z, out double theta)
        {
            // Convert arcseconds to radians
            const double ARCSEC_TO_RAD = Math.PI / (180.0 * 3600.0);
            
            // IAU 2006 precession angles (in arcseconds)
            // Formulas from USNO Circular 179
            
            // ? (zeta) - precession in RA
            zeta = (2.5976176 + 2306.0809506 * T + 0.3019015 * T * T + 0.0179663 * T * T * T
                    - 0.0000327 * T * T * T * T - 0.0000002 * T * T * T * T * T) * ARCSEC_TO_RAD;
            
            // z - precession in RA (second component)
            z = (-2.5976176 + 2306.0803226 * T + 1.0947790 * T * T + 0.0182273 * T * T * T
                 + 0.0000470 * T * T * T * T - 0.0000003 * T * T * T * T * T) * ARCSEC_TO_RAD;
            
            // ? (theta) - precession in Dec
            theta = (2004.1917476 * T - 0.4269353 * T * T - 0.0418251 * T * T * T
                     - 0.0000601 * T * T * T * T - 0.0000001 * T * T * T * T * T) * ARCSEC_TO_RAD;
        }
        
        /// <summary>
        /// Calculate Julian Date from UTC DateTime
        /// </summary>
        public static double DateTimeToJulianDate(DateTime utc)
        {
            int year = utc.Year;
            int month = utc.Month;
            int day = utc.Day;
            
            // Adjust for January/February
            if (month <= 2)
            {
                year -= 1;
                month += 12;
            }
            
            // Gregorian calendar correction
            int a = year / 100;
            int b = 2 - a + a / 4;
            
            // Calculate integer part
            double jd = Math.Floor(365.25 * (year + 4716)) +
                       Math.Floor(30.6001 * (month + 1)) +
                       day + b - 1524.5;
            
            // Add fractional day
            double dayFraction = (utc.Hour + utc.Minute / 60.0 + 
                                  utc.Second / 3600.0 + 
                                  utc.Millisecond / 3600000.0) / 24.0;
            jd += dayFraction;
            
            return jd;
        }
        
        /// <summary>
        /// Quick approximate precession (Meeus algorithm)
        /// Faster but less accurate (±1 arcsec) for dates within ±50 years of J2000
        /// Use for real-time calculations where speed matters
        /// </summary>
        public static (double raJ2000_Rads, double decJ2000_Rads) ApparentToJ2000_Fast(
            double raApp_Rads, 
            double decApp_Rads, 
            double julianDate)
        {
            // Calculate centuries from J2000.0
            double T = (julianDate - 2451545.0) / 36525.0;
            
            // Convert arcseconds to radians
            const double ARCSEC_TO_RAD = Math.PI / (180.0 * 3600.0);
            
            // Meeus approximate precession (in seconds of time and arcseconds)
            double m = 3.07496 + 0.00186 * T;  // seconds of time
            double n = 20.0431 - 0.0085 * T;   // arcseconds
            
            // Convert m to radians (1 second of time = 15 arcseconds)
            double m_rad = m * 15.0 * ARCSEC_TO_RAD;
            double n_rad = n * ARCSEC_TO_RAD;
            
            // Apply correction (subtract to go from apparent ? J2000)
            double raJ2000 = raApp_Rads - (m_rad + n_rad * Math.Sin(raApp_Rads) * Math.Tan(decApp_Rads));
            double decJ2000 = decApp_Rads - (n_rad * Math.Cos(raApp_Rads));
            
            // Normalize RA
            if (raJ2000 < 0) raJ2000 += 2.0 * Math.PI;
            if (raJ2000 >= 2.0 * Math.PI) raJ2000 -= 2.0 * Math.PI;
            
            return (raJ2000, decJ2000);
        }
    }
}
