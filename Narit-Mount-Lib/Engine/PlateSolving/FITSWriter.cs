using System;
using System.IO;
using System.Text;
using ForkMount_WPF.Engine.Camera;
using static ForkMount_WPF.Engine.Camera.ZWOASICameraSDK;

namespace ForkMount_WPF.Engine.PlateSolving
{
    /// <summary>
    /// FITS file writer for astronomical images
    /// Supports RAW8 and RAW16 formats with proper FITS headers
    /// 
    /// FITS Format Specification:
    /// - Header: 2880 bytes blocks (80-char cards, 36 cards per block)
    /// - Each card: KEYWORD = VALUE / COMMENT
    /// - Data: Binary pixel data (big-endian for 16-bit)
    /// </summary>
    public class FITSWriter
    {
        /// <summary>
        /// Write camera image data to FITS file
        /// </summary>
        /// <param name="imageData">Camera image data</param>
        /// <param name="filePath">Output FITS file path</param>
        /// <param name="metadata">Optional metadata (Alt/Az, RA/Dec, etc.)</param>
        /// <returns>True if successful</returns>
        public static bool WriteFITS(CameraImageData imageData, string filePath, FITSMetadata metadata = null)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[FITSWriter] Writing FITS file: {filePath}");
                System.Diagnostics.Debug.WriteLine($"[FITSWriter] Image: {imageData.Width}x{imageData.Height}, Type: {imageData.ImageType}");
                
                // Create directory if needed
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    // Write FITS header
                    WriteFITSHeader(writer, imageData, metadata);
                    
                    // Write image data
                    WriteFITSImageData(writer, imageData);
                    
                    // Pad to 2880-byte boundary
                    long currentSize = stream.Position;
                    long padding = (2880 - (currentSize % 2880)) % 2880;
                    for (int i = 0; i < padding; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[FITSWriter] ? FITS file written successfully");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FITSWriter] ? Error writing FITS: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Write FITS header (2880-byte blocks)
        /// </summary>
        private static void WriteFITSHeader(BinaryWriter writer, CameraImageData imageData, FITSMetadata metadata)
        {
            var headerLines = new System.Collections.Generic.List<string>();
            
            // Required keywords (must be first)
            headerLines.Add(FormatCard("SIMPLE", true, "Standard FITS format"));
            
            // BITPIX: Bits per pixel (MUST be 8 or 16, NOT -16!)
            // BITPIX = 16 means signed 16-bit integer
            // BZERO = 32768 converts to unsigned (standard method)
            int bitpix = (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16) ? 16 : 8;
            headerLines.Add(FormatCard("BITPIX", bitpix, "Bits per pixel"));
            
            // NAXIS: Number of axes
            headerLines.Add(FormatCard("NAXIS", 2, "Number of axes"));
            headerLines.Add(FormatCard("NAXIS1", imageData.Width, "Width in pixels"));
            headerLines.Add(FormatCard("NAXIS2", imageData.Height, "Height in pixels"));
            
            // [FIX] BZERO and BSCALE as INTEGER (not float!)
            // MaxIm DL uses integers, PWLib may expect this format
            if (bitpix == 16)
            {
                headerLines.Add(FormatCard("BZERO", 32768, "Offset for unsigned integers"));
                headerLines.Add(FormatCard("BSCALE", 1, "Scaling factor"));
            }
            
            // Camera information (optional - can be added later if needed)
            // Note: CameraImageData doesn't have CameraName property yet
            
            // [NEW] Add common FITS keywords (compatibility with MaxIm DL)
            headerLines.Add(FormatCard("IMAGETYP", "Light Frame", "Image type"));
            headerLines.Add(FormatCard("OBJECT", "Calibration", "Object name"));
            
            // Exposure data (minimal set)
            headerLines.Add(FormatCard("EXPTIME", imageData.ExposureTime_us / 1000000.0, "Exposure time (seconds)"));
            
            // Binning
            headerLines.Add(FormatCard("XBINNING", imageData.Binning, "X-axis binning"));
            headerLines.Add(FormatCard("YBINNING", imageData.Binning, "Y-axis binning"));
            
            // Timestamp
            string dateObs = imageData.CaptureTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");
            headerLines.Add(FormatCard("DATE-OBS", dateObs, "UTC date/time of observation"));
            
            // [SIMPLIFIED] Remove excessive metadata - PWLib only needs basic info
            // Add metadata if provided (extended set matching Narit's format)
            if (metadata != null)
            {
                // Focal length (for plate scale calculation)
                if (metadata.FocalLength_mm.HasValue)
                {
                    headerLines.Add(FormatCard("FOCALLEN", metadata.FocalLength_mm.Value, "Focal length (mm)"));
                }
                
                // [NEW] Extended metadata (from Narit's SetFITSKey)
                if (metadata.Airmass.HasValue)
                {
                    headerLines.Add(FormatCard("AIRMASS", metadata.Airmass.Value, "Airmass"));
                }
                
                if (metadata.Altitude_Deg.HasValue)
                {
                    headerLines.Add(FormatCard("ALT", metadata.Altitude_Deg.Value, "Altitude (degrees)"));
                }
                
                if (metadata.Azimuth_Deg.HasValue)
                {
                    headerLines.Add(FormatCard("AZI", metadata.Azimuth_Deg.Value, "Azimuth (degrees)"));
                }
                
                if (metadata.FocuserPosition.HasValue)
                {
                    headerLines.Add(FormatCard("FOCUSPOS", metadata.FocuserPosition.Value, "Focuser position"));
                }
                
                if (metadata.JulianDate.HasValue)
                {
                    headerLines.Add(FormatCard("JD", metadata.JulianDate.Value, "Julian Date"));
                }
                
                if (metadata.ObserverLatitude_Deg.HasValue)
                {
                    headerLines.Add(FormatCard("LAT", metadata.ObserverLatitude_Deg.Value, "Latitude (degrees)"));
                }
                
                if (metadata.ObserverLongitude_Deg.HasValue)
                {
                    headerLines.Add(FormatCard("LONG", metadata.ObserverLongitude_Deg.Value, "Longitude (degrees)"));
                }
                
                if (!string.IsNullOrEmpty(metadata.LocalSiderealTime))
                {
                    headerLines.Add(FormatCard("LST", metadata.LocalSiderealTime, "Local Sidereal Time"));
                }
                
                if (!string.IsNullOrEmpty(metadata.ObjectDec_DMS))
                {
                    headerLines.Add(FormatCard("OBJCTDEC", metadata.ObjectDec_DMS, "Object Dec (apparent)"));
                }
                
                if (!string.IsNullOrEmpty(metadata.ObjectRA_HMS))
                {
                    headerLines.Add(FormatCard("OBJCTRA", metadata.ObjectRA_HMS, "Object RA (apparent)"));
                }
                
                if (metadata.RotatorAngle.HasValue)
                {
                    headerLines.Add(FormatCard("ROTATOR", metadata.RotatorAngle.Value, "Rotator angle (degrees)"));
                }
                
                if (metadata.ApertureDiameter_mm.HasValue)
                {
                    headerLines.Add(FormatCard("APTDIA", metadata.ApertureDiameter_mm.Value, "Aperture diameter (mm)"));
                }
                
                if (!string.IsNullOrEmpty(metadata.TelescopeName))
                {
                    headerLines.Add(FormatCard("TELESCOP", metadata.TelescopeName, "Telescope name"));
                }
                
                // Point index (for debugging/tracking only)
                if (metadata.PointIndex.HasValue)
                {
                    headerLines.Add(FormatCard("POINTIDX", metadata.PointIndex.Value, "Calibration point index"));
                }
            }
            
            // END keyword (required, marks end of header)
            headerLines.Add("END" + new string(' ', 77));
            
            // Write header blocks (each block = 2880 bytes = 36 cards x 80 chars)
            int totalCards = headerLines.Count;
            int cardsPerBlock = 36;
            int blocks = (int)Math.Ceiling(totalCards / (double)cardsPerBlock);
            int totalCardsNeeded = blocks * cardsPerBlock;
            
            // Pad with blank cards
            for (int i = totalCards; i < totalCardsNeeded; i++)
            {
                headerLines.Add(new string(' ', 80));
            }
            
            // [DEBUG] Log first 10 header cards for validation
            System.Diagnostics.Debug.WriteLine($"[FITSWriter] ===== FITS HEADER VALIDATION =====");
            for (int i = 0; i < Math.Min(10, headerLines.Count); i++)
            {
                string card = headerLines[i];
                System.Diagnostics.Debug.WriteLine($"[FITSWriter] Card {i}: '{card}' (len={card.Length})");
            }
            System.Diagnostics.Debug.WriteLine($"[FITSWriter] ====================================");
            
            // Write all cards
            foreach (var line in headerLines)
            {
                byte[] cardBytes = Encoding.ASCII.GetBytes(line);
                if (cardBytes.Length != 80)
                {
                    System.Diagnostics.Debug.WriteLine($"[FITSWriter] ? WARNING: Card length != 80: '{line}' (len={cardBytes.Length})");
                }
                writer.Write(cardBytes);
            }
            
            System.Diagnostics.Debug.WriteLine($"[FITSWriter] Header: {totalCards} cards, {blocks} blocks ({blocks * 2880} bytes)");
        }
        
        /// <summary>
        /// Write FITS image data
        /// </summary>
        private static void WriteFITSImageData(BinaryWriter writer, CameraImageData imageData)
        {
            byte[] data = imageData.ImageData;
            bool isRAW16 = (imageData.ImageType == ASI_IMG_TYPE.ASI_IMG_RAW16);
            
            if (isRAW16)
            {
                // [STANDARD] BITPIX=16 with BZERO=32768 (signed -> unsigned conversion)
                // This is the standard FITS method for unsigned 16-bit data
                // 
                // FITS File Format:
                //   - File stores: signed 16-bit (-32768 to +32767)
                //   - Reader adds BZERO: actual = file_value + 32768
                //   - Result: unsigned (0 to 65535)
                // 
                // Our encoding:
                //   file_value = actual_value - 32768
                //   Example: actual=40000 ? file=40000-32768=7232
                //   Reader: 7232 + 32768 = 40000 ?
                int pixelCount = imageData.Width * imageData.Height;
                
                for (int i = 0; i < pixelCount; i++)
                {
                    int byteIndex = i * 2;
                    
                    // Read unsigned value from camera data (little-endian)
                    ushort unsignedValue = (ushort)(data[byteIndex] | (data[byteIndex + 1] << 8));
                    
                    // Convert to signed: subtract 32768
                    // Use unchecked to allow wrap-around (e.g., 0-32768 = -32768)
                    short signedValue = unchecked((short)(unsignedValue - 32768));
                    
                    // Convert signed to unsigned bit pattern (for big-endian write)
                    ushort signedBits = unchecked((ushort)signedValue);
                    
                    // Write big-endian (FITS standard)
                    writer.Write((byte)(signedBits >> 8));     // High byte first
                    writer.Write((byte)(signedBits & 0xFF));   // Low byte
                }
                
                System.Diagnostics.Debug.WriteLine($"[FITSWriter] Image data: {pixelCount} pixels x 2 bytes (BITPIX=16, BZERO=32768, big-endian)");
            }
            else
            {
                // 8-bit data - write as-is
                writer.Write(data);
                
                System.Diagnostics.Debug.WriteLine($"[FITSWriter] Image data: {data.Length} bytes (8-bit)");
            }
        }
        
        /// <summary>
        /// Format FITS header card (80 characters)
        /// </summary>
        private static string FormatCard(string keyword, object value, string comment = "")
        {
            keyword = keyword.ToUpper().PadRight(8);
            
            string valueStr;
            if (value is bool boolValue)
            {
                valueStr = boolValue ? "T" : "F";
                valueStr = valueStr.PadLeft(20);
            }
            else if (value is int || value is long)
            {
                valueStr = value.ToString().PadLeft(20);
            }
            else if (value is double || value is float)
            {
                valueStr = string.Format("{0,20:F6}", value);
            }
            else if (value is string strValue)
            {
                valueStr = $"'{strValue}'".PadRight(20);
            }
            else
            {
                valueStr = value.ToString().PadLeft(20);
            }
            
            string card = $"{keyword}= {valueStr}";
            
            if (!string.IsNullOrEmpty(comment))
            {
                card += $" / {comment}";
            }
            
            // Pad or truncate to exactly 80 characters
            if (card.Length > 80)
                card = card.Substring(0, 80);
            else
                card = card.PadRight(80);
            
            return card;
        }
    }
    
    /// <summary>
    /// Metadata for FITS file
    /// Extended to match Narit's FITS header format for full compatibility
    /// </summary>
    public class FITSMetadata
    {
        // Mount position at capture time
        public double? Azimuth_Deg { get; set; }
        public double? Altitude_Deg { get; set; }
        
        // Expected sky position (for plate solving hint)
        public double? ExpectedRA_Deg { get; set; }
        public double? ExpectedDec_Deg { get; set; }
        
        // Observer location
        public double? ObserverLatitude_Deg { get; set; }
        public double? ObserverLongitude_Deg { get; set; }
        public double? ObserverAltitude_m { get; set; }
        
        // Optical system
        public double? FocalLength_mm { get; set; }
        public double? PlateScale_ArcsecPerPixel { get; set; }
        
        // Calibration point index
        public int? PointIndex { get; set; }
        
        /// <summary>
        /// [NEW] Capture timestamp (UTC) for accurate plate solving
        /// CRITICAL: Required for RA/Dec precession calculations!
        /// </summary>
        public DateTime? CaptureTime_UTC { get; set; }
        
        // [NEW] Extended metadata (from Narit's GenerateFitHeader)
        public double? Airmass { get; set; }
        public double? JulianDate { get; set; }
        public string LocalSiderealTime { get; set; }  // HMS format (e.g., "12:34:56")
        public string TelescopeName { get; set; }
        public int? FocuserPosition { get; set; }
        public double? RotatorAngle { get; set; }
        public double? ApertureDiameter_mm { get; set; }
        public string ObjectRA_HMS { get; set; }   // Target RA (HMS format)
        public string ObjectDec_DMS { get; set; }  // Target Dec (DMS format)
    }
}
