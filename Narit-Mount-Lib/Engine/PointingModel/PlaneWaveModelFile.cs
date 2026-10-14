using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ForkMount_WPF.Engine.PointingModel
{
    /// <summary>
    /// PlaneWave-compatible pointing model file writer (.pxp format)
    /// Compatible with PointXP class in PXPLib.dll
    /// </summary>
    public static class PlaneWaveModelFile
    {
        /// <summary>
        /// Save calibration data in PlaneWave .pxp format
        /// Compatible with PXPLib.dll PointXP class
        /// </summary>
        public static void SavePlaneWaveFormat(
            string filePath,
            List<Manager.CalibrationPoint> calibrationPoints,
            Utility.ObserverLocationSettings observerLocation)
        {
            try
            {
                var sb = new StringBuilder();

                // Get only solved points
                var solvedPoints = calibrationPoints.Where(p => p.IsSolved).ToList();

                // Header: Number of calibration stars
                sb.AppendLine($"Number of Cal Stars:");
                sb.AppendLine($"{solvedPoints.Count}");
                sb.AppendLine();  // [FIX] Blank line after count — required by PXPLib!

                // Write each calibration point
                for (int i = 0; i < solvedPoints.Count; i++)
                {
                    var point = solvedPoints[i];

                    sb.AppendLine($"Cal Star {i + 1}:");
                    sb.AppendLine();

                    // RA, Dec (in radians, J2000)
                    // Use the solved sky position, not the commanded/expected target.
                    double raJ2000 = point.SolvedRa_J2000_Rads ?? point.ExpectedRa_J2000_Rads ?? 0.0;
                    double decJ2000 = point.SolvedDec_J2000_Rads ?? point.ExpectedDec_J2000_Rads ?? 0.0;
                    sb.AppendLine("RA, Dec:");
                    sb.AppendLine($"{raJ2000}");
                    sb.AppendLine($"{decJ2000}");

                    // ThetaEnc, PhiEnc (ACTUAL encoder positions in radians)
                    // PlaneWave uses measured encoder positions, not commanded positions!
                    // Theta = Altitude (measured from encoder), Phi = Azimuth (measured from encoder)
                    double thetaEnc = point.CurrentAltitude * Math.PI / 180.0;  // Actual Alt (measured)
                    double phiEnc = point.CurrentAzimuth * Math.PI / 180.0;     // Actual Az (measured)

                    sb.AppendLine("ThetaEnc, PhiEnc:");
                    sb.AppendLine($"{thetaEnc}");
                    sb.AppendLine($"{phiEnc}");

                    // LST, JD (Local Sidereal Time in RADIANS, Julian Date)
                    // [CRITICAL FIX] NaritMountLib stores LST in RADIANS!
                    // NaritMountLib: currentSample.LstRads = Angle.FromHms(MountInfo.Lmst).Rads;
                    // Convert: hours → degrees → radians
                    DateTime captureTime = point.CaptureTime ?? DateTime.Now;
                    double lstHours = CalculateLocalSiderealTime(captureTime, observerLocation.Longitude_Deg);
                    double lst = lstHours * 15.0 * Math.PI / 180.0;  // hours → radians
                    double jd = CalculateJulianDate(captureTime);

                    sb.AppendLine("LST, JD:");
                    sb.AppendLine($"{lst}");
                    sb.AppendLine($"{jd}");

                    // Time, Date (HH:mm:ss, YYYY-MM-DD)
                    sb.AppendLine("Time, Date:");
                    sb.AppendLine(captureTime.ToString("HH:mm:ss"));
                    sb.AppendLine(captureTime.ToString("yyyy-MM-dd"));
                    sb.AppendLine();  // Blank line separator between points — required by PXPLib!
                }

                // [CRITICAL] Add required footer sections (exact Port1.pxp format!)

                // Latitude, Longitude, TimeZone, DST
                sb.AppendLine("Latitude, Longitude, TimeZone, DST:");
                sb.AppendLine($"{observerLocation.Latitude_Deg}");
                sb.AppendLine($"{observerLocation.Longitude_Deg}");
                sb.AppendLine("7");   // TimeZone: UTC+7 (Thailand)
                sb.AppendLine("0");   // DST: 0 (Thailand doesn't use DST)
                sb.AppendLine();      // Blank separator

                // CW Azimuth, MaxAzCounts
                sb.AppendLine("CW Azimuth, MaxAzCounts:");
                sb.AppendLine("True");
                sb.AppendLine("6.283185308");
                sb.AppendLine();

                // Altitude Up, MaxAltCounts
                sb.AppendLine("Altitude Up, MaxAltCounts:");
                sb.AppendLine("True");
                sb.AppendLine("6.283185308");
                sb.AppendLine();

                // IsAltAz, IsGEM
                sb.AppendLine("IsAltAz, IsGEM:");
                sb.AppendLine("True");
                sb.AppendLine("False");
                sb.AppendLine();

                // Temperature, Pressure
                sb.AppendLine("Temperature, Pressure:");
                sb.AppendLine("16");     // Match Port1
                sb.AppendLine("1018");   // Match Port1
                sb.AppendLine();

                // Terms Enabled (15 terms - exact Port1 pattern!)
                sb.AppendLine("Terms Enabled:");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine();

                // Cal Points Used (one per point)
                sb.AppendLine("Cal Points Used:");
                for (int i = 0; i < solvedPoints.Count; i++)
                {
                    sb.AppendLine("1");
                }
                sb.AppendLine();

                // Scale Factors, Theta, Phi
                sb.AppendLine("Scale Factors, Theta, Phi:");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine();

                // Clock Rate Error
                sb.AppendLine("Clock Rate Error:");
                sb.AppendLine("0.0");
                sb.AppendLine();

                // Terms Enabled (second set - 7 terms)
                sb.AppendLine("Terms Enabled:");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine();

                // Enable Refraction
                sb.AppendLine("Enable Refraction:");
                sb.AppendLine("1");
                sb.AppendLine();

                // Max Sensitivity
                sb.AppendLine("Max Sensitivity:");
                sb.AppendLine("300");
                sb.AppendLine();  // Only ONE trailing blank line!

                // Write to file
                File.WriteAllText(filePath, sb.ToString());

                System.Diagnostics.Debug.WriteLine($"[PlaneWave] ✓ Saved {solvedPoints.Count} points to {Path.GetFileName(filePath)}");
                System.Diagnostics.Debug.WriteLine($"[PlaneWave]    Full path: {filePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlaneWave] ❌ Failed to save: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Calculate Local Sidereal Time in hours (public for use by other classes)
        /// </summary>
        public static double CalculateLocalSiderealTime(DateTime utcTime, double longitudeDeg)
        {
            // Julian Date
            double jd = CalculateJulianDate(utcTime);

            // Days since J2000.0
            double d = jd - 2451545.0;

            // Greenwich Mean Sidereal Time (GMST) in degrees
            double gmst = 280.46061837 + 360.98564736629 * d;
            gmst = gmst % 360.0;
            if (gmst < 0) gmst += 360.0;

            // Local Sidereal Time = GMST + longitude
            double lst = gmst + longitudeDeg;
            lst = lst % 360.0;
            if (lst < 0) lst += 360.0;

            // Convert to hours
            return lst / 15.0;
        }

        /// <summary>
        /// Calculate Julian Date from DateTime (public for use by other classes)
        /// </summary>
        public static double CalculateJulianDate(DateTime dateTime)
        {
            // Ensure UTC
            DateTime utc = dateTime.ToUniversalTime();

            int year = utc.Year;
            int month = utc.Month;
            double day = utc.Day + (utc.Hour / 24.0) + (utc.Minute / 1440.0) + (utc.Second / 86400.0);

            if (month <= 2)
            {
                year -= 1;
                month += 12;
            }

            int a = year / 100;
            int b = 2 - a + (a / 4);

            double jd = Math.Floor(365.25 * (year + 4716)) +
                       Math.Floor(30.6001 * (month + 1)) +
                       day + b - 1524.5;

            return jd;
        }

        /// <summary>
        /// Load PlaneWave pointing model file (.pxp format)
        /// Returns list of calibration points
        /// </summary>
        public static List<PlaneWaveCalibrationPoint> LoadPlaneWaveFormat(string filePath)
        {
            var points = new List<PlaneWaveCalibrationPoint>();

            try
            {
                var lines = File.ReadAllLines(filePath);
                int lineIndex = 0;

                // Read number of calibration stars
                while (lineIndex < lines.Length && !lines[lineIndex].StartsWith("Number of Cal Stars:"))
                    lineIndex++;

                lineIndex++; // Skip "Number of Cal Stars:"
                int numStars = int.Parse(lines[lineIndex].Trim(), CultureInfo.InvariantCulture);
                lineIndex++;

                System.Diagnostics.Debug.WriteLine($"[PlaneWave] Loading {numStars} calibration points...");

                // Read each calibration point
                for (int i = 0; i < numStars; i++)
                {
                    var point = new PlaneWaveCalibrationPoint();

                    // Skip to "Cal Star N:"
                    while (lineIndex < lines.Length && !lines[lineIndex].StartsWith($"Cal Star {i + 1}:"))
                        lineIndex++;
                    lineIndex++; // Skip empty line
                    lineIndex++; // Skip "Cal Star N:"

                    // Read RA, Dec
                    lineIndex++; // Skip "RA, Dec:"
                    point.RA_Rads = double.Parse(lines[lineIndex++].Trim(), CultureInfo.InvariantCulture);
                    point.Dec_Rads = double.Parse(lines[lineIndex++].Trim(), CultureInfo.InvariantCulture);

                    // Read ThetaEnc, PhiEnc
                    lineIndex++; // Skip "ThetaEnc, PhiEnc:"
                    point.ThetaEnc_Rads = double.Parse(lines[lineIndex++].Trim(), CultureInfo.InvariantCulture);
                    point.PhiEnc_Rads = double.Parse(lines[lineIndex++].Trim(), CultureInfo.InvariantCulture);

                    // Read LST, JD
                    lineIndex++; // Skip "LST, JD:"
                    point.LST_Hours = double.Parse(lines[lineIndex++].Trim(), CultureInfo.InvariantCulture);
                    point.JulianDate = double.Parse(lines[lineIndex++].Trim(), CultureInfo.InvariantCulture);

                    // Read Time, Date
                    lineIndex++; // Skip "Time, Date:"
                    string timeStr = lines[lineIndex++].Trim();
                    string dateStr = lines[lineIndex++].Trim();
                    point.CaptureTime = DateTime.Parse($"{dateStr} {timeStr}");

                    lineIndex++; // Skip empty line

                    points.Add(point);
                }

                System.Diagnostics.Debug.WriteLine($"[PlaneWave] ✓ Loaded {points.Count} points");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlaneWave] ❌ Failed to load: {ex.Message}");
                throw;
            }

            return points;
        }
    }

    /// <summary>
    /// PlaneWave calibration point data structure
    /// </summary>
    public class PlaneWaveCalibrationPoint
    {
        public double RA_Rads { get; set; }           // Right Ascension (radians, J2000)
        public double Dec_Rads { get; set; }          // Declination (radians, J2000)
        public double ThetaEnc_Rads { get; set; }     // Encoder Theta (Altitude in radians)
        public double PhiEnc_Rads { get; set; }       // Encoder Phi (Azimuth in radians)
        public double LST_Hours { get; set; }         // Local Sidereal Time (hours)
        public double JulianDate { get; set; }        // Julian Date
        public DateTime CaptureTime { get; set; }     // Capture timestamp

        // Convenience properties (degrees)
        public double Altitude_Deg => ThetaEnc_Rads * 180.0 / Math.PI;
        public double Azimuth_Deg => PhiEnc_Rads * 180.0 / Math.PI;
        public double RA_Deg => RA_Rads * 180.0 / Math.PI;
        public double Dec_Deg => Dec_Rads * 180.0 / Math.PI;
    }
}
