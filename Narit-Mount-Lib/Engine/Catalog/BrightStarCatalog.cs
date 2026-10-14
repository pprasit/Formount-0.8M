using System;
using System.Collections.Generic;
using System.IO;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Tracking;
using System.Text.Json;
using System.Linq;
using PWLib.Astro;

namespace ForkMount_WPF.Engine.Catalog
{
    /// <summary>
    /// Bright Star Catalog Entry
    /// Based on Yale Bright Star Catalog (BSC5)
    /// </summary>
    public class BrightStar
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Bayer { get; set; }
        public int HD { get; set; }  // Henry Draper Catalog number
        public double RA { get; set; }  // Right Ascension (hours, J2000)
        public double Dec { get; set; }  // Declination (degrees, J2000)
        public double ProperMotionRA_ArcsecPerYear { get; set; }
        public double ProperMotionDec_ArcsecPerYear { get; set; }
        public double Magnitude { get; set; }
        public string SpectralType { get; set; }
        public string Constellation { get; set; }
        
        // Computed properties
        public string RAString
        {
            get
            {
                int h = (int)RA;
                int m = (int)((RA - h) * 60);
                double s = ((RA - h) * 60 - m) * 60;
                return $"{h:00}:{m:00}:{s:00.0}";
            }
        }
        
        public string DecString
        {
            get
            {
                string sign = Dec >= 0 ? "+" : "-";
                double decAbs = Math.Abs(Dec);
                int d = (int)decAbs;
                int m = (int)((decAbs - d) * 60);
                double s = ((decAbs - d) * 60 - m) * 60;
                return $"{sign}{d:00}:{m:00}:{s:00.0}";
            }
        }
    }
    
    /// <summary>
    /// Bright Star Catalog Manager
    /// Loads and manages the bright star catalog for calibration
    /// </summary>
    public static class BrightStarCatalog
    {
        private static List<BrightStar> _catalog;
        private static readonly object _lockObject = new object();
        
        /// <summary>
        /// Get all bright stars from catalog
        /// </summary>
        public static List<BrightStar> GetCatalog()
        {
            if (_catalog == null)
            {
                lock (_lockObject)
                {
                    if (_catalog == null)
                    {
                        _catalog = LoadCatalog();
                    }
                }
            }
            return _catalog;
        }
        
        /// <summary>
        /// Get stars brighter than specified magnitude
        /// </summary>
        public static List<BrightStar> GetStarsBrighterThan(double magnitude)
        {
            return GetCatalog().Where(s => s.Magnitude <= magnitude).ToList();
        }
        
        /// <summary>
        /// Get stars visible from observer location at current time
        /// </summary>
        public static List<BrightStar> GetVisibleStars(ObserverLocation location, DateTime utc, double minAltitude = 10.0)
        {
            var catalog = GetCatalog();
            var visibleStars = new List<BrightStar>();

            // Calculate Local Sidereal Time using PWLib.Astro
            var site = new LatLon(
                Angle.FromDegs(location.Latitude), 
                Angle.FromDegs(location.Longitude));
            double jd = AstroTime.JulianDay(utc);
            double lst = site.Lmst(jd).Hours;
            
            foreach (var star in catalog)
            {
                var (raHours, decDeg) = GetAdjustedJ2000Coordinates(star, utc);

                // Calculate Hour Angle
                double ha = lst - raHours;
                
                // Calculate Altitude
                var (altitude, azimuth) = CalculateHorizontalCoordinates(ha, decDeg, location.Latitude);
                
                if (altitude >= minAltitude)
                {
                    visibleStars.Add(star);
                }
            }
            
            return visibleStars;
        }
        
        /// <summary>
        /// Find star by name (case-insensitive)
        /// </summary>
        public static BrightStar FindByName(string name)
        {
            return GetCatalog().FirstOrDefault(s => 
                s.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true ||
                s.Bayer?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        }
        
        /// <summary>
        /// Get stars in specific constellation
        /// </summary>
        public static List<BrightStar> GetStarsInConstellation(string constellation)
        {
            return GetCatalog().Where(s => 
                s.Constellation?.Equals(constellation, StringComparison.OrdinalIgnoreCase) == true).ToList();
        }
        
        private static List<BrightStar> LoadCatalog()
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "BrightStars.json");

            try
            {
                if (!File.Exists(jsonPath))
                {
                    throw new FileNotFoundException("BrightStars.json not found.", jsonPath);
                }

                string jsonContent = File.ReadAllText(jsonPath);
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var catalogData = JsonSerializer.Deserialize<BrightStarCatalogData>(jsonContent, jsonOptions);
                
                int starCount = catalogData?.Stars?.Count ?? 0;
                System.Diagnostics.Debug.WriteLine($"[CATALOG] Loaded {starCount} stars from BrightStars.json");

                if (catalogData?.Stars == null || starCount == 0)
                {
                    throw new InvalidDataException("BrightStars.json does not contain any stars.");
                }

                // Prefer the real catalog file as-is.
                // Do not replace it with procedurally generated stars just because the
                // record count is lower than an arbitrary synthetic target.
                SimpleLogger.Info($"[CATALOG] Successfully loaded {starCount} stars from catalog file", "Catalog");
                return catalogData.Stars;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CATALOG ERROR] Failed to load: {ex.Message}");
                SimpleLogger.Error($"[CATALOG] Real bright-star catalog unavailable: {ex.Message}", "Catalog");
                return new List<BrightStar>();
            }
        }

        public static (double raHours, double decDeg) GetAdjustedJ2000Coordinates(BrightStar star, DateTime utc)
        {
            if (star == null)
            {
                throw new ArgumentNullException(nameof(star));
            }

            double yearsSinceJ2000 = (utc.ToUniversalTime() - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays / 365.25;
            double adjustedDecDeg = star.Dec + (star.ProperMotionDec_ArcsecPerYear * yearsSinceJ2000 / 3600.0);

            double cosDec = Math.Cos(adjustedDecDeg * Math.PI / 180.0);
            double adjustedRaHours = star.RA;
            if (Math.Abs(cosDec) > 1e-9)
            {
                adjustedRaHours += (star.ProperMotionRA_ArcsecPerYear * yearsSinceJ2000) / (54000.0 * cosDec);
            }

            while (adjustedRaHours < 0.0) adjustedRaHours += 24.0;
            while (adjustedRaHours >= 24.0) adjustedRaHours -= 24.0;

            return (adjustedRaHours, adjustedDecDeg);
        }
        
        /// <summary>
        /// Calculate horizontal coordinates (Alt/Az) from hour angle and declination using PWLib.Astro
        /// </summary>
        private static (double altitude, double azimuth) CalculateHorizontalCoordinates(
            double hourAngle, double declination, double latitude)
        {
            // Use PWLib.Astro for coordinate conversion
            var ha = Angle.FromHours(hourAngle);
            var dec = Angle.FromDegs(declination);
            var lat = Angle.FromDegs(latitude);
            
            var haDec = new HaDec(ha, dec);
            var site = new LatLon(lat, Angle.Zero); // Longitude not needed for HA->AltAz
            var altAz = haDec.ToAltAz(site);
            
            return (altAz.Alt.Degs, altAz.Az.Degs);
        }
    }
    
    // JSON Deserialization classes
    internal class BrightStarCatalogData
    {
        public string Catalog { get; set; }
        public string Epoch { get; set; }
        public string Source { get; set; }
        public string Description { get; set; }
        public int Star_Count { get; set; }
        public List<BrightStar> Stars { get; set; }
    }
}
