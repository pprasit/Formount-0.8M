using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForkMount_WPF.Engine.Catalog
{
    /// <summary>
    /// Reader for UCAC4 binary catalog files
    /// Based on UCAC4 readme_u4.txt specification
    /// </summary>
    public class UCAC4Reader : IDisposable
    {
        private const int BYTES_PER_STAR = 78;
        private const double MAS_TO_DEG = 1.0 / 3600000.0;
        private const int SPD_OFFSET = 324000000; // South Pole Distance offset
        
        private readonly string _catalogPath;
        private readonly Dictionary<int, string> _zoneFileCache = new();
        
        public string CatalogPath => _catalogPath;
        
        /// <summary>
        /// Initialize UCAC4 reader
        /// </summary>
        /// <param name="catalogPath">Path to UCAC4 catalog directory (containing u4b folder)</param>
        public UCAC4Reader(string catalogPath)
        {
            _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
            
            if (!Directory.Exists(catalogPath))
            {
                throw new DirectoryNotFoundException($"UCAC4 catalog directory not found: {catalogPath}");
            }
            
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Initialized reader for catalog: {catalogPath}");
        }
        
        /// <summary>
        /// Get zone number for given declination
        /// </summary>
        /// <param name="decDeg">Declination in degrees (-90 to +90)</param>
        /// <returns>Zone number (1 to 900)</returns>
        public static int GetZoneNumber(double decDeg)
        {
            return 1 + (int)Math.Floor((decDeg + 90.0) / 0.2);
        }
        
        /// <summary>
        /// Read all stars from a specific zone
        /// </summary>
        public List<UCAC4Star> ReadZone(int zoneNumber)
        {
            if (zoneNumber < 1 || zoneNumber > 900)
            {
                throw new ArgumentOutOfRangeException(nameof(zoneNumber), "Zone number must be 1-900");
            }
            
            string zoneFile = GetZoneFilePath(zoneNumber);
            
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Attempting to read zone {zoneNumber}");
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Zone file path: {zoneFile}");
            
            if (!File.Exists(zoneFile))
            {
                System.Diagnostics.Debug.WriteLine($"[UCAC4] ERROR: Zone file not found: {zoneFile}");
                return new List<UCAC4Star>();
            }
            
            // Check file size
            FileInfo fileInfo = new FileInfo(zoneFile);
            long fileSize = fileInfo.Length;
            long expectedRecords = fileSize / BYTES_PER_STAR;
            System.Diagnostics.Debug.WriteLine($"[UCAC4] File size: {fileSize} bytes ({expectedRecords} records expected)");
            
            var stars = new List<UCAC4Star>();
            
            try
            {
                using (var stream = new FileStream(zoneFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    int recordNumber = 0;
                    
                    while (stream.Position < stream.Length)
                    {
                        recordNumber++;
                        
                        try
                        {
                            var star = ReadStarRecord(reader, zoneNumber, recordNumber);
                            stars.Add(star);
                        }
                        catch (EndOfStreamException)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UCAC4] End of stream at record {recordNumber}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UCAC4] Error reading record {recordNumber}: {ex.Message}");
                            break;
                        }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[UCAC4] Successfully read {stars.Count} stars from zone {zoneNumber}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UCAC4] Error opening zone file: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[UCAC4] Stack trace: {ex.StackTrace}");
            }
            
            return stars;
        }
        
        /// <summary>
        /// Get stars in RA/Dec region
        /// </summary>
        public List<UCAC4Star> GetStarsInRegion(double raMin, double raMax, double decMin, double decMax)
        {
            var stars = new List<UCAC4Star>();
            
            // Calculate zone range
            int zoneMin = Math.Max(1, GetZoneNumber(decMin));
            int zoneMax = Math.Min(900, GetZoneNumber(decMax));
            
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Query region: RA={raMin:F2}-{raMax:F2}° Dec={decMin:F2}-{decMax:F2}°");
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Zone range: {zoneMin} to {zoneMax} ({zoneMax - zoneMin + 1} zones)");
            
            // Handle RA wrap-around
            bool raWrapAround = raMax < raMin;
            if (raWrapAround)
            {
                System.Diagnostics.Debug.WriteLine("[UCAC4] RA wrap-around detected");
            }
            
            int totalStarsRead = 0;
            int zonesProcessed = 0;
            
            // Read zones
            for (int zone = zoneMin; zone <= zoneMax; zone++)
            {
                var zoneStars = ReadZone(zone);
                totalStarsRead += zoneStars.Count;
                zonesProcessed++;
                
                if (zoneStars.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[UCAC4] WARNING: Zone {zone} returned 0 stars (file may be missing)");
                }
                
                foreach (var star in zoneStars)
                {
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
                    
                    if (inRaRange)
                    {
                        stars.Add(star);
                    }
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Processed {zonesProcessed} zones, read {totalStarsRead} total stars");
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Found {stars.Count} stars matching region criteria");
            
            return stars;
        }
        
        /// <summary>
        /// Read single star record from binary file
        /// </summary>
        private UCAC4Star ReadStarRecord(BinaryReader reader, int zoneNumber, int recordNumber)
        {
            var star = new UCAC4Star
            {
                ZoneNumber = zoneNumber,
                RecordNumber = recordNumber
            };
            
            // Bytes 1-4: RA in mas
            int ra_mas = reader.ReadInt32();
            star.RA_deg = ra_mas * MAS_TO_DEG;
            
            // Bytes 5-8: SPD in mas
            int spd_mas = reader.ReadInt32();
            star.Dec_deg = (spd_mas - SPD_OFFSET) * MAS_TO_DEG;
            
            // Bytes 9-10: Model magnitude
            short magm = reader.ReadInt16();
            star.Magnitude = (magm == 20000) ? double.NaN : magm / 1000.0;
            
            // Bytes 11-12: Aperture magnitude
            short maga = reader.ReadInt16();
            star.MagnitudeAperture = (maga == 20000) ? double.NaN : maga / 1000.0;
            
            // Byte 13: Magnitude error
            sbyte sigmag = reader.ReadSByte();
            star.MagnitudeError = (sigmag == 99) ? double.NaN : sigmag / 100.0;
            
            // Byte 14: Object type
            star.ObjectType = reader.ReadSByte();
            
            // Byte 15: Double star flag
            star.DoubleStarFlag = reader.ReadSByte();
            
            // Bytes 16-17: Position errors (skip)
            reader.ReadSByte(); // sigra
            reader.ReadSByte(); // sigdc
            
            // Bytes 18-20: Image counts (skip)
            reader.ReadSByte(); // na1
            reader.ReadSByte(); // nu1
            reader.ReadSByte(); // cu1
            
            // Bytes 21-22: Central epoch RA
            short cepra = reader.ReadInt16();
            star.Epoch = cepra / 100.0; // Years since 1900
            
            // Bytes 23-24: Central epoch Dec (skip)
            reader.ReadInt16();
            
            // Bytes 25-26: Proper motion RA*cos(Dec)
            short pmra = reader.ReadInt16();
            star.ProperMotionRA = (pmra == 0) ? 0 : pmra * 0.1; // mas/yr
            
            // Bytes 27-28: Proper motion Dec
            short pmdc = reader.ReadInt16();
            star.ProperMotionDec = (pmdc == 0) ? 0 : pmdc * 0.1; // mas/yr
            
            // Bytes 29-30: PM errors
            sbyte sigpmr = reader.ReadSByte();
            star.ProperMotionRA_Error = ConvertPMError(sigpmr);
            
            sbyte sigpmd = reader.ReadSByte();
            star.ProperMotionDec_Error = ConvertPMError(sigpmd);
            
            // Bytes 31-34: 2MASS key (skip)
            reader.ReadInt32();
            
            // Bytes 35-40: 2MASS magnitudes
            short j_m = reader.ReadInt16();
            star.J_mag = (j_m == 20000) ? double.NaN : j_m / 1000.0;
            
            short h_m = reader.ReadInt16();
            star.H_mag = (h_m == 20000) ? double.NaN : h_m / 1000.0;
            
            short k_m = reader.ReadInt16();
            star.K_mag = (k_m == 20000) ? double.NaN : k_m / 1000.0;
            
            // Bytes 41-46: 2MASS flags (skip)
            reader.ReadBytes(6);
            
            // Bytes 47-56: APASS magnitudes
            short b_mag = reader.ReadInt16();
            star.B_mag = (b_mag == 20000) ? double.NaN : b_mag / 1000.0;
            
            short v_mag = reader.ReadInt16();
            star.V_mag = (v_mag == 20000) ? double.NaN : v_mag / 1000.0;
            
            short g_mag = reader.ReadInt16();
            star.g_mag = (g_mag == 20000) ? double.NaN : g_mag / 1000.0;
            
            short r_mag = reader.ReadInt16();
            star.r_mag = (r_mag == 20000) ? double.NaN : r_mag / 1000.0;
            
            short i_mag = reader.ReadInt16();
            star.i_mag = (i_mag == 20000) ? double.NaN : i_mag / 1000.0;
            
            // Bytes 57-62: APASS errors and flags (skip)
            reader.ReadBytes(6);
            
            // Bytes 63-68: Catalog flags (skip)
            reader.ReadBytes(6);
            
            // Bytes 69-72: Unique star ID
            star.UniqueID = reader.ReadInt32();
            
            // Bytes 73-78: UCAC2 cross-reference (skip)
            reader.ReadBytes(6);
            
            return star;
        }
        
        /// <summary>
        /// Convert proper motion error from signed byte to mas/yr
        /// </summary>
        private double ConvertPMError(sbyte sigmaPM)
        {
            int value = sigmaPM + 128; // Convert to 0-255 range
            
            if (value >= 251)
            {
                return value switch
                {
                    251 => 27.5,
                    252 => 32.5,
                    253 => 37.5,
                    254 => 45.0,
                    _ => 50.0 // 255 = no data
                };
            }
            
            return value * 0.1; // mas/yr
        }
        
        /// <summary>
        /// Get zone file path
        /// </summary>
        private string GetZoneFilePath(int zoneNumber)
        {
            // Standard UCAC4 format
            string zoneFileName = $"z{zoneNumber:D3}";
            
            // UC4 format (uppercase Z with .UC4 extension)
            string uc4FileName = $"Z{zoneNumber:D3}.UC4";
            
            // Try multiple possible locations
            string[] possiblePaths = new[]
            {
                // Standard UCAC4 formats
                Path.Combine(_catalogPath, "u4b", zoneFileName),           // Standard: C:\UCAC4\u4b\z001
                Path.Combine(_catalogPath, zoneFileName),                  // Direct: C:\UCAC4\z001
                Path.Combine(_catalogPath, "UCAC4", "u4b", zoneFileName),  // Nested: C:\Kepler\UCAC4\u4b\z001
                Path.Combine(_catalogPath, "UCAC4", zoneFileName),         // Direct nested: C:\Kepler\UCAC4\z001
                
                // UC4 formats (with .UC4 extension)
                Path.Combine(_catalogPath, uc4FileName),                   // Direct UC4: C:\UC4\Z001.UC4
                Path.Combine(_catalogPath, "UC4", uc4FileName),            // Nested UC4: C:\Kepler\UC4\Z001.UC4
                Path.Combine(_catalogPath, "u4b", uc4FileName),            // UC4 in u4b: C:\UC4\u4b\Z001.UC4
            };
            
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    System.Diagnostics.Debug.WriteLine($"[UCAC4] Found zone file: {path}");
                    return path;
                }
            }
            
            // Return first path as fallback (will fail later with proper error)
            System.Diagnostics.Debug.WriteLine($"[UCAC4] Zone file not found. Tried paths:");
            foreach (var path in possiblePaths)
            {
                System.Diagnostics.Debug.WriteLine($"[UCAC4]   - {path}");
            }
            
            return possiblePaths[0];
        }
        
        public void Dispose()
        {
            _zoneFileCache.Clear();
            System.Diagnostics.Debug.WriteLine("[UCAC4] Reader disposed");
        }
    }
}
