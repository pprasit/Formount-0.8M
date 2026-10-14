using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ForkMount_WPF.Engine.Catalog
{
    /// <summary>
    /// Reader for Tycho-2 Catalog ASCII files
    /// Based on Tycho-2 Catalogue (I/259) specification
    /// 2.5 million stars, magnitude 2-12
    /// </summary>
    public class Tycho2Reader : IDisposable
    {
        private readonly string _catalogPath;
        private const int NUM_FILES = 20; // tyc2.dat.00 to tyc2.dat.19
        
        public string CatalogPath => _catalogPath;
        
        /// <summary>
        /// Initialize Tycho-2 reader
        /// </summary>
        /// <param name="catalogPath">Path to Tycho-2 catalog directory (containing tyc2.dat.XX files)</param>
        public Tycho2Reader(string catalogPath)
        {
            _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
            
            if (!Directory.Exists(catalogPath))
            {
                throw new DirectoryNotFoundException($"Tycho-2 catalog directory not found: {catalogPath}");
            }
            
            System.Diagnostics.Debug.WriteLine($"[TYCHO2] Initialized reader for catalog: {catalogPath}");
        }
        
        /// <summary>
        /// Get stars in RA/Dec region
        /// </summary>
        /// <param name="raCenterDeg">RA center (degrees)</param>
        /// <param name="decCenterDeg">Dec center (degrees)</param>
        /// <param name="radiusDeg">Search radius (degrees)</param>
        /// <param name="maxMagnitude">Maximum magnitude (fainter limit)</param>
        /// <returns>List of stars</returns>
        public List<Tycho2Star> GetStarsInRegion(
            double raCenterDeg, 
            double decCenterDeg, 
            double radiusDeg,
            double maxMagnitude = 12.0)
        {
            // [CRITICAL FIX] Calculate bounding box with proper RA scaling at high declinations
            // At high Dec, RA lines converge, so we need wider RA range
            // Formula: RA_range = radius / cos(Dec)
            
            double decMin = decCenterDeg - radiusDeg;
            double decMax = decCenterDeg + radiusDeg;
            
            // Clamp declination first (before calculating RA range)
            decMin = Math.Max(decMin, -90.0);
            decMax = Math.Min(decMax, 90.0);
            
            // Calculate RA range with cos(Dec) correction
            // Use the most extreme declination (highest |Dec|) for conservative search
            double maxAbsDec = Math.Max(Math.Abs(decMin), Math.Abs(decMax));
            double cosDec = Math.Cos(maxAbsDec * Math.PI / 180.0);
            
            // Avoid division by zero at poles
            if (cosDec < 0.01) cosDec = 0.01; // Safety limit near poles
            
            double raRangeDeg = radiusDeg / cosDec;
            double raMin = raCenterDeg - raRangeDeg;
            double raMax = raCenterDeg + raRangeDeg;
            
            System.Diagnostics.Debug.WriteLine($"[TYCHO2] Search region calculation:");
            System.Diagnostics.Debug.WriteLine($"  Center: RA={raCenterDeg:F4}° Dec={decCenterDeg:F4}°");
            System.Diagnostics.Debug.WriteLine($"  Radius: {radiusDeg:F4}°");
            System.Diagnostics.Debug.WriteLine($"  cos(Dec={maxAbsDec:F2}°) = {cosDec:F4}");
            System.Diagnostics.Debug.WriteLine($"  RA range expanded: ±{raRangeDeg:F4}° (was ±{radiusDeg:F4}°)");
            System.Diagnostics.Debug.WriteLine($"  Final box: RA={raMin:F2}°-{raMax:F2}° Dec={decMin:F2}°-{decMax:F2}°");
            
            return GetStarsInRegion(raMin, raMax, decMin, decMax, maxMagnitude);
        }
        
        /// <summary>
        /// Get stars in RA/Dec box
        /// </summary>
        public List<Tycho2Star> GetStarsInRegion(
            double raMin, 
            double raMax, 
            double decMin, 
            double decMax,
            double maxMagnitude = 12.0)
        {
            var stars = new List<Tycho2Star>();
            
            // [CRITICAL FIX] Handle RA wrap-around FIRST (before normalization!)
            // Case 1: raMin > 360 (e.g., 362.61 from raCenter=359.73 + 2.88)
            // Case 2: raMin < 0 (e.g., -3.15 from raCenter=1.0 - 4.15)
            // Case 3: raMax > 360 (most common: raMax = 362.61 ? wraps to 2.61)
            
            bool raWrapAround = false;
            
            // Normalize raMin to 0-360
            if (raMin < 0) 
            {
                raMin += 360.0;
                raWrapAround = true;  // Search crosses 0°
            }
            
            // Normalize raMax to 0-360
            if (raMax > 360.0) 
            {
                raMax -= 360.0;
                raWrapAround = true;  // Search crosses 360°/0°
            }
            
            // After normalization, if raMax < raMin, we're still wrapping
            // e.g., raMin=356.85, raMax=2.61 (original raMax was 362.61)
            if (raMax < raMin)
            {
                raWrapAround = true;
            }
            
            // Clamp Dec
            decMin = Math.Max(decMin, -90.0);
            decMax = Math.Min(decMax, 90.0);
            
            System.Diagnostics.Debug.WriteLine($"[TYCHO2] Query region: RA={raMin:F2}-{raMax:F2}° Dec={decMin:F2}-{decMax:F2}° MaxMag={maxMagnitude:F1}");
            if (raWrapAround)
            {
                System.Diagnostics.Debug.WriteLine($"[TYCHO2] ? RA WRAP-AROUND detected! Will search RA>{raMin:F2}° OR RA<{raMax:F2}°");
            }
            
            int totalStarsRead = 0;
            int filesProcessed = 0;
            
            // Read all catalog files
            for (int fileNum = 0; fileNum < NUM_FILES; fileNum++)
            {
                string filePath = GetDataFilePath(fileNum);
                
                if (!File.Exists(filePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[TYCHO2] WARNING: File not found: {filePath}");
                    continue;
                }
                
                var fileStars = ReadDataFile(filePath, raMin, raMax, decMin, decMax, raWrapAround, maxMagnitude);
                stars.AddRange(fileStars);
                totalStarsRead += fileStars.Count;
                filesProcessed++;
                
                System.Diagnostics.Debug.WriteLine($"[TYCHO2] File {fileNum:D2}: Found {fileStars.Count} stars");
            }
            
            System.Diagnostics.Debug.WriteLine($"[TYCHO2] Processed {filesProcessed} files, found {stars.Count} stars");
            
            return stars;
        }
        
        /// <summary>
        /// Read single catalog file
        /// </summary>
        private List<Tycho2Star> ReadDataFile(
            string filePath, 
            double raMin, 
            double raMax, 
            double decMin, 
            double decMax,
            bool raWrapAround,
            double maxMagnitude)
        {
            var stars = new List<Tycho2Star>();
            
            try
            {
                using (var reader = new StreamReader(filePath))
                {
                    string line;
                    int lineNumber = 0;
                    
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNumber++;
                        
                        try
                        {
                            var star = ParseStarRecord(line);
                            
                            if (star == null)
                                continue;
                            
                            // Filter by declination
                            if (star.Dec_deg < decMin || star.Dec_deg > decMax)
                                continue;
                            
                            // Filter by RA
                            bool inRaRange;
                            if (raWrapAround)
                            {
                                inRaRange = (star.RA_deg >= raMin || star.RA_deg <= raMax);
                            }
                            else
                            {
                                inRaRange = (star.RA_deg >= raMin && star.RA_deg <= raMax);
                            }
                            
                            if (!inRaRange)
                                continue;
                            
                            // Filter by magnitude
                            if (!double.IsNaN(star.VT_mag) && star.VT_mag > maxMagnitude)
                                continue;
                            
                            stars.Add(star);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TYCHO2] Error parsing line {lineNumber}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TYCHO2] Error reading file {filePath}: {ex.Message}");
            }
            
            return stars;
        }
        
        /// <summary>
        /// Parse single star record from ASCII line
        /// Tycho-2 Format (fixed-width):
        /// Bytes 1-4: TYC1
        /// Bytes 6-10: TYC2
        /// Bytes 12: TYC3
        /// Bytes 16-27: RA (degrees)
        /// Bytes 29-40: Dec (degrees)
        /// Bytes 42-48: pmRA (mas/yr)
        /// Bytes 50-56: pmDec (mas/yr)
        /// Bytes 111-117: BTmag
        /// Bytes 124-130: VTmag
        /// </summary>
        private Tycho2Star ParseStarRecord(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length < 200)
                return null;
            
            try
            {
                var star = new Tycho2Star();
                
                // TYC1-TYC2-TYC3 identifier (bytes 1-12)
                star.TYC1 = ParseInt(line, 0, 4);
                star.TYC2 = ParseInt(line, 5, 5);
                star.TYC3 = ParseInt(line, 11, 1);
                
                // Position (J2000)
                star.RA_deg = ParseDouble(line, 15, 12);   // bytes 16-27
                star.Dec_deg = ParseDouble(line, 28, 12);  // bytes 29-40
                
                // Proper motion
                star.ProperMotionRA = ParseDouble(line, 41, 7);  // bytes 42-48
                star.ProperMotionDec = ParseDouble(line, 49, 7); // bytes 50-56
                
                // Position errors
                star.e_RA = ParseDouble(line, 57, 6);   // bytes 58-63
                star.e_Dec = ParseDouble(line, 64, 6);  // bytes 65-70
                
                // Epoch
                star.Epoch = ParseDouble(line, 83, 7);  // bytes 84-90
                
                // Magnitudes
                star.BT_mag = ParseDouble(line, 110, 6);  // bytes 111-116
                star.VT_mag = ParseDouble(line, 123, 6);  // bytes 124-129
                
                // Hipparcos number (if exists)
                star.HIP = ParseInt(line, 142, 6);  // bytes 143-148
                
                return star;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TYCHO2] Parse error: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Parse integer from fixed-width field
        /// </summary>
        private int ParseInt(string line, int start, int length)
        {
            if (start + length > line.Length)
                return 0;
            
            string value = line.Substring(start, length).Trim();
            
            if (string.IsNullOrEmpty(value))
                return 0;
            
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                return result;
            
            return 0;
        }
        
        /// <summary>
        /// Parse double from fixed-width field
        /// </summary>
        private double ParseDouble(string line, int start, int length)
        {
            if (start + length > line.Length)
                return double.NaN;
            
            string value = line.Substring(start, length).Trim();
            
            if (string.IsNullOrEmpty(value))
                return double.NaN;
            
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                return result;
            
            return double.NaN;
        }
        
        /// <summary>
        /// Get data file path
        /// </summary>
        private string GetDataFilePath(int fileNumber)
        {
            string fileName = $"tyc2.dat.{fileNumber:D2}";
            
            // Try multiple possible locations
            string[] possiblePaths = new[]
            {
                Path.Combine(_catalogPath, fileName),                    // Direct: C:\Tycho2\tyc2.dat.00
                Path.Combine(_catalogPath, "Tycho2", fileName),          // Nested: C:\Kepler\Tycho2\tyc2.dat.00
                Path.Combine(_catalogPath, "tycho2", fileName),          // Lowercase
                Path.Combine(_catalogPath, "data", fileName),            // In data folder
            };
            
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
            
            // Return first path as fallback
            return possiblePaths[0];
        }
        
        /// <summary>
        /// Get catalog statistics
        /// </summary>
        public CatalogInfo GetCatalogInfo()
        {
            var info = new CatalogInfo
            {
                CatalogName = "Tycho-2",
                CatalogPath = _catalogPath,
                TotalFiles = NUM_FILES
            };
            
            // Count available files
            for (int i = 0; i < NUM_FILES; i++)
            {
                string filePath = GetDataFilePath(i);
                if (File.Exists(filePath))
                {
                    info.AvailableFiles++;
                    
                    var fileInfo = new FileInfo(filePath);
                    info.TotalSizeBytes += fileInfo.Length;
                }
            }
            
            info.IsComplete = (info.AvailableFiles == NUM_FILES);
            
            return info;
        }
        
        public void Dispose()
        {
            System.Diagnostics.Debug.WriteLine("[TYCHO2] Reader disposed");
        }
    }
    
    /// <summary>
    /// Catalog information
    /// </summary>
    public class CatalogInfo
    {
        public string CatalogName { get; set; }
        public string CatalogPath { get; set; }
        public int TotalFiles { get; set; }
        public int AvailableFiles { get; set; }
        public long TotalSizeBytes { get; set; }
        public bool IsComplete { get; set; }
        
        public string TotalSizeMB => $"{TotalSizeBytes / 1024.0 / 1024.0:F1} MB";
        
        public override string ToString()
        {
            return $"{CatalogName}: {AvailableFiles}/{TotalFiles} files ({TotalSizeMB})";
        }
    }
}
