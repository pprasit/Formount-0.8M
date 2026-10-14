using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// Container for camera image data and metadata
    /// Used for calibration and plate solving
    /// </summary>
    public class CameraImageData
    {
        /// <summary>
        /// Raw image data (byte array)
        /// - RAW8: 8-bit per pixel
        /// - RAW16: 16-bit per pixel (2 bytes per pixel)
        /// - RGB24: 24-bit per pixel (3 bytes per pixel)
        /// </summary>
        public byte[] ImageData { get; set; }

        /// <summary>
        /// Image width in pixels
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Image height in pixels
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// Image type (RAW8, RAW16, RGB24, Y8)
        /// </summary>
        public ZWOASICameraSDK.ASI_IMG_TYPE ImageType { get; set; }

        /// <summary>
        /// Binning mode (1, 2, 4, etc.)
        /// </summary>
        public int Binning { get; set; }

        /// <summary>
        /// Exposure time in microseconds
        /// </summary>
        public long ExposureTime_us { get; set; }

        /// <summary>
        /// Camera gain setting
        /// </summary>
        public int Gain { get; set; }

        /// <summary>
        /// Camera offset setting
        /// </summary>
        public int Offset { get; set; }

        /// <summary>
        /// Timestamp when image was captured
        /// </summary>
        public DateTime CaptureTime { get; set; }

        /// <summary>
        /// Temperature at time of capture (degrees Celsius)
        /// </summary>
        public double? Temperature_C { get; set; }

        /// <summary>
        /// Target azimuth position (degrees)
        /// </summary>
        public double? Target_Azimuth { get; set; }

        /// <summary>
        /// Target altitude position (degrees)
        /// </summary>
        public double? Target_Altitude { get; set; }

        /// <summary>
        /// Actual mount azimuth position at capture (degrees)
        /// </summary>
        public double? Mount_Azimuth { get; set; }

        /// <summary>
        /// Actual mount altitude position at capture (degrees)
        /// </summary>
        public double? Mount_Altitude { get; set; }

        /// <summary>
        /// Observer latitude (degrees)
        /// </summary>
        public double? Observer_Latitude { get; set; }

        /// <summary>
        /// Observer longitude (degrees)
        /// </summary>
        public double? Observer_Longitude { get; set; }

        /// <summary>
        /// Observer altitude (meters)
        /// </summary>
        public double? Observer_Altitude_m { get; set; }
        
        // ========================================================================
        // HISTOGRAM STRETCH PARAMETERS (for consistent display across UI)
        // ========================================================================
        
        /// <summary>
        /// Histogram stretch minimum value (black point)
        /// For RAW16: 0-65535, For RAW8: 0-255
        /// </summary>
        public int StretchMin { get; set; } = 0;
        
        /// <summary>
        /// Histogram stretch maximum value (white point)
        /// For RAW16: 0-65535, For RAW8: 0-255
        /// </summary>
        public int StretchMax { get; set; } = 65535;
        
        /// <summary>
        /// Stretch profile index (0=Auto, 1=Low, 2=Medium, 3=High, 4=Manual, etc.)
        /// </summary>
        public int StretchProfileIndex { get; set; } = 0;
        
        /// <summary>
        /// Whether stretch parameters have been set by user
        /// </summary>
        public bool HasCustomStretch { get; set; } = false;


        /// <summary>
        /// WPF BitmapImage for display (lazy loaded)
        /// </summary>
        private BitmapImage _bitmapImage;
        
        /// <summary>
        /// Preview BitmapImage for UI display (lazy loaded, 800x600 max)
        /// </summary>
        private BitmapImage _previewImage;

        /// <summary>
        /// Convert raw image data to WPF BitmapImage for display
        /// ?? WARNING: This returns FULL SIZE image (may be 200+ MB!)
        /// For UI display, use GetPreviewImage() instead
        /// </summary>
        public BitmapImage GetBitmapImage()
        {
            if (_bitmapImage != null)
                return _bitmapImage;

            if (ImageData == null || Width == 0 || Height == 0)
                return null;

            try
            {
                _bitmapImage = ConvertToBitmapImage(ImageData, Width, Height, ImageType);
                return _bitmapImage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraImageData] Error converting to BitmapImage: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Get preview image for UI display (8-bit, scaled, stretched)
        /// ? Memory efficient: ~0.5 MB vs ~200 MB for full image
        /// </summary>
        /// <param name="maxWidth">Maximum preview width (default: 800px)</param>
        /// <param name="maxHeight">Maximum preview height (default: 600px)</param>
        /// <returns>Preview BitmapImage suitable for UI display</returns>
        public async Task<BitmapImage> GetPreviewImageAsync(int maxWidth = 800, int maxHeight = 600)
        {
            if (_previewImage != null)
                return _previewImage;

            if (ImageData == null || Width == 0 || Height == 0)
                return null;

            try
            {
                System.Diagnostics.Debug.WriteLine($"[CameraImageData] Creating preview image from {Width}x{Height} ({ImageType})...");
                
                // For 16-bit RAW: Use optimized stride sampling (no full conversion!)
                if (ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16)
                {
                    // Calculate preview dimensions
                    double aspectRatio = (double)Width / Height;
                    int previewWidth, previewHeight;
                    
                    if (aspectRatio > 1.0)
                    {
                        previewWidth = Math.Min(Width, maxWidth);
                        previewHeight = (int)(previewWidth / aspectRatio);
                    }
                    else
                    {
                        previewHeight = Math.Min(Height, maxHeight);
                        previewWidth = (int)(previewHeight * aspectRatio);
                    }
                    
                    // OPTIMIZATION: Sample directly to preview size (skip full conversion!)
                    var imageData2D = ConvertByteArrayTo2DArrayOptimized(ImageData, Width, Height, previewWidth, previewHeight);
                    
                    // Create preview using helper (already small!)
                    _previewImage = await ForkMount_WPF.Engine.Utility.ImagePreviewHelper.CreatePreviewImageAsync(
                        imageData2D,
                        maxWidth,
                        maxHeight,
                        autoStretch: true);
                    
                    System.Diagnostics.Debug.WriteLine($"[CameraImageData] ? Preview created: {previewWidth}x{previewHeight} (8-bit, stride sampled)");
                }
                else
                {
                    // For 8-bit: Simple downscale
                    _previewImage = await Task.Run(() => CreateSimplePreview(maxWidth, maxHeight));
                }
                
                return _previewImage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraImageData ERROR] Failed to create preview: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Convert byte array to 2D ushort array for 16-bit images (SUPER OPTIMIZED with stride sampling)
        /// Only samples pixels that will be used in downsampled image (much faster!)
        /// </summary>
        private unsafe ushort[,] ConvertByteArrayTo2DArrayOptimized(byte[] byteArray, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
        {
            var result = new ushort[dstHeight, dstWidth];
            
            double scaleX = (double)srcWidth / dstWidth;
            double scaleY = (double)srcHeight / dstHeight;
            
            // Use unsafe pointers for maximum speed
            fixed (byte* srcPtr = byteArray)
            {
                ushort* src = (ushort*)srcPtr;
                int srcStride = srcWidth;
                
                // Parallel sampling with AVERAGE POOLING (anti-aliasing!)
                System.Threading.Tasks.Parallel.For(0, dstHeight, y =>
                {
                    for (int x = 0; x < dstWidth; x++)
                    {
                        // Calculate source region bounds
                        double srcXStart = x * scaleX;
                        double srcYStart = y * scaleY;
                        double srcXEnd = srcXStart + scaleX;
                        double srcYEnd = srcYStart + scaleY;

                        int srcXMin = (int)srcXStart;
                        int srcYMin = (int)srcYStart;
                        int srcXMax = Math.Min((int)Math.Ceiling(srcXEnd), srcWidth);
                        int srcYMax = Math.Min((int)Math.Ceiling(srcYEnd), srcHeight);

                        // Average all pixels in the source region
                        int sum = 0;
                        int count = 0;
                        
                        for (int srcY = srcYMin; srcY < srcYMax; srcY++)
                        {
                            int srcRowOffset = srcY * srcStride;
                            for (int srcX = srcXMin; srcX < srcXMax; srcX++)
                            {
                                sum += src[srcRowOffset + srcX];
                                count++;
                            }
                        }

                        result[y, x] = count > 0 ? (ushort)(sum / count) : (ushort)0;
                    }
                });
            }
            
            return result;
        }
        
        /// <summary>
        /// Convert byte array to 2D ushort array for 16-bit images (OPTIMIZED with Parallel.For)
        /// </summary>
        private ushort[,] ConvertByteArrayTo2DArray(byte[] byteArray, int width, int height)
        {
            var result = new ushort[height, width];
            
            // Use parallel processing to speed up conversion
            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 2;
                    result[y, x] = BitConverter.ToUInt16(byteArray, index);
                }
            });
            
            return result;
        }
        
        /// <summary>
        /// Create simple preview for 8-bit images
        /// </summary>
        private BitmapImage CreateSimplePreview(int maxWidth, int maxHeight)
        {
            // Calculate preview dimensions
            double aspectRatio = (double)Width / Height;
            int previewWidth, previewHeight;
            
            if (aspectRatio > 1.0)
            {
                previewWidth = Math.Min(Width, maxWidth);
                previewHeight = (int)(previewWidth / aspectRatio);
            }
            else
            {
                previewHeight = Math.Min(Height, maxHeight);
                previewWidth = (int)(previewHeight * aspectRatio);
            }
            
            // Simple nearest-neighbor downsampling
            byte[] previewData = new byte[previewWidth * previewHeight];
            double scaleX = (double)Width / previewWidth;
            double scaleY = (double)Height / previewHeight;
            
            for (int y = 0; y < previewHeight; y++)
            {
                for (int x = 0; x < previewWidth; x++)
                {
                    int srcX = (int)(x * scaleX);
                    int srcY = (int)(y * scaleY);
                    int srcIdx = srcY * Width + srcX;
                    int dstIdx = y * previewWidth + x;
                    
                    previewData[dstIdx] = ImageData[srcIdx];
                }
            }
            
            // Create BitmapSource
            var bitmapSource = BitmapSource.Create(
                previewWidth, previewHeight,
                96, 96,
                System.Windows.Media.PixelFormats.Gray8,
                null,
                previewData,
                previewWidth);
            
            bitmapSource.Freeze();
            
            // Convert to BitmapImage
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            
            using (var stream = new System.IO.MemoryStream())
            {
                encoder.Save(stream);
                stream.Seek(0, System.IO.SeekOrigin.Begin);
                
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
                
                return bitmapImage;
            }
        }

        /// <summary>
        /// Convert raw byte array to WPF BitmapImage with aggressive histogram stretching
        /// </summary>
        private BitmapImage ConvertToBitmapImage(byte[] data, int width, int height, ZWOASICameraSDK.ASI_IMG_TYPE imgType)
        {
            BitmapSource bitmapSource = null;

            switch (imgType)
            {
                case ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW8:
                case ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_Y8:
                    // 8-bit grayscale - apply aggressive stretching
                    byte[] stretched8 = ApplyAggressiveStretch8(data);
                    bitmapSource = BitmapSource.Create(
                        width, height,
                        96, 96,
                        System.Windows.Media.PixelFormats.Gray8,
                        null,
                        stretched8,
                        width);
                    break;

                case ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16:
                    // 16-bit grayscale - apply aggressive stretching
                    byte[] stretched16 = ApplyAggressiveStretch16(data, width, height);
                    bitmapSource = BitmapSource.Create(
                        width, height,
                        96, 96,
                        System.Windows.Media.PixelFormats.Gray16,
                        null,
                        stretched16,
                        width * 2);
                    break;

                case ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RGB24:
                    // 24-bit RGB
                    bitmapSource = BitmapSource.Create(
                        width, height,
                        96, 96,
                        System.Windows.Media.PixelFormats.Bgr24,
                        null,
                        data,
                        width * 3);
                    break;
            }

            if (bitmapSource == null)
                return null;

            // Convert BitmapSource to BitmapImage
            var bitmapImage = new BitmapImage();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

            using (var stream = new System.IO.MemoryStream())
            {
                encoder.Save(stream);
                stream.Seek(0, System.IO.SeekOrigin.Begin);

                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
            }

            return bitmapImage;
        }

        /// <summary>
        /// Apply aggressive histogram stretch to 8-bit image data
        /// Uses 1.0% clip on both ends for maximum contrast and star visibility
        /// ENHANCED: Stronger clipping + lower gamma = darker background, brighter stars
        /// </summary>
        private byte[] ApplyAggressiveStretch8(byte[] data)
        {
            if (data == null || data.Length == 0)
                return data;

            byte[] result = new byte[data.Length];

            // Find histogram percentiles for aggressive clipping (1.0% on each end)
            int[] histogram = new int[256];
            foreach (byte pixel in data)
            {
                histogram[pixel]++;
            }

            int totalPixels = data.Length;
            int clipCount = (int)(totalPixels * 0.01); // 1.0% clip (more aggressive)

            int minValue = 0;
            int maxValue = 255;

            // Find min clip point
            int cumulative = 0;
            for (int i = 0; i < 256; i++)
            {
                cumulative += histogram[i];
                if (cumulative >= clipCount)
                {
                    minValue = i;
                    break;
                }
            }

            // Find max clip point
            cumulative = 0;
            for (int i = 255; i >= 0; i--)
            {
                cumulative += histogram[i];
                if (cumulative >= clipCount)
                {
                    maxValue = i;
                    break;
                }
            }

            // Prevent division by zero
            if (maxValue <= minValue)
            {
                maxValue = minValue + 1;
            }

            // Apply stretch with STRONGER gamma correction for extra brightness and darker background
            double gamma = 0.5; // Gamma < 1 brightens mid-tones (0.5 = more aggressive than 0.7)
            double range = maxValue - minValue;

            for (int i = 0; i < data.Length; i++)
            {
                double normalized = (data[i] - minValue) / range;
                normalized = Math.Max(0, Math.Min(1, normalized)); // Clamp to [0,1]
                normalized = Math.Pow(normalized, gamma); // Apply gamma
                result[i] = (byte)(normalized * 255);
            }

            System.Diagnostics.Debug.WriteLine($"[Stretch8] Clip range: {minValue}-{maxValue}, Gamma: {gamma} (Enhanced for star visibility)");
            return result;
        }

        /// <summary>
        /// Apply aggressive histogram stretch to 16-bit image data
        /// Uses 1.0% clip on both ends for maximum contrast and star visibility
        /// ENHANCED: Stronger clipping + lower gamma = darker background, brighter stars
        /// </summary>
        private byte[] ApplyAggressiveStretch16(byte[] data, int width, int height)
        {
            if (data == null || data.Length == 0)
                return data;

            byte[] result = new byte[data.Length];
            int pixelCount = width * height;

            // Convert byte array to ushort array for 16-bit processing
            ushort[] pixels = new ushort[pixelCount];
            Buffer.BlockCopy(data, 0, pixels, 0, data.Length);

            // Find histogram percentiles for aggressive clipping (1.0% on each end)
            Array.Sort(pixels);

            int clipIndex = (int)(pixelCount * 0.01); // 1.0% clip (more aggressive)
            ushort minValue = pixels[clipIndex];
            ushort maxValue = pixels[pixelCount - clipIndex - 1];

            // Prevent division by zero
            if (maxValue <= minValue)
            {
                maxValue = (ushort)(minValue + 1);
            }

            // Restore original order by reloading from source
            Buffer.BlockCopy(data, 0, pixels, 0, data.Length);

            // Apply stretch with STRONGER gamma correction for extra brightness and darker background
            double gamma = 0.5; // Gamma < 1 brightens mid-tones (0.5 = more aggressive than 0.7)
            double range = maxValue - minValue;
            ushort[] stretchedPixels = new ushort[pixelCount];

            for (int i = 0; i < pixelCount; i++)
            {
                double normalized = (pixels[i] - minValue) / range;
                normalized = Math.Max(0, Math.Min(1, normalized)); // Clamp to [0,1]
                normalized = Math.Pow(normalized, gamma); // Apply gamma
                stretchedPixels[i] = (ushort)(normalized * 65535);
            }

            // Convert back to byte array
            Buffer.BlockCopy(stretchedPixels, 0, result, 0, data.Length);

            System.Diagnostics.Debug.WriteLine($"[Stretch16] Clip range: {minValue}-{maxValue}, Gamma: {gamma} (Enhanced for star visibility)");
            return result;
        }

        /// <summary>
        /// Save image to FITS file with metadata (FITS Standard compliant)
        /// Writes standard FITS headers and big-endian pixel data.
        /// Supports RAW16 (16-bit), RAW8/Y8 (8-bit), and RGB24 (3-channel 8-bit) image types.
        /// </summary>
        public bool SaveToFITS(string filePath)
        {
            try
            {
                if (ImageData == null || ImageData.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[CameraImageData] Cannot save FITS - no image data");
                    return false;
                }

                int bitsPerPixel = GetBitsPerPixel();
                bool is16bit = (ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16);
                bool isRGB = (ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RGB24);

                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    // --- Build FITS Header ---
                    var headerCards = new List<string>();

                    headerCards.Add(FormatFitsCard("SIMPLE", "T", "FITS standard"));

                    if (isRGB)
                    {
                        // RGB24: 3-axis, 8-bit per channel
                        headerCards.Add(FormatFitsCard("BITPIX", "8", "Bits per pixel per channel"));
                        headerCards.Add(FormatFitsCard("NAXIS", "3", "Number of axes"));
                        headerCards.Add(FormatFitsCard("NAXIS1", Width.ToString(), "Width in pixels"));
                        headerCards.Add(FormatFitsCard("NAXIS2", Height.ToString(), "Height in pixels"));
                        headerCards.Add(FormatFitsCard("NAXIS3", "3", "Number of color channels (RGB)"));
                        headerCards.Add(FormatFitsCard("BZERO", "0", "Zero offset"));
                        headerCards.Add(FormatFitsCard("BSCALE", "1", "Scale factor"));
                    }
                    else
                    {
                        // Monochrome: RAW16 or RAW8
                        headerCards.Add(FormatFitsCard("BITPIX", is16bit ? "16" : "8", "Bits per pixel"));
                        headerCards.Add(FormatFitsCard("NAXIS", "2", "Number of axes"));
                        headerCards.Add(FormatFitsCard("NAXIS1", Width.ToString(), "Width in pixels"));
                        headerCards.Add(FormatFitsCard("NAXIS2", Height.ToString(), "Height in pixels"));
                        headerCards.Add(FormatFitsCard("BZERO", is16bit ? "32768" : "0", "Zero offset"));
                        headerCards.Add(FormatFitsCard("BSCALE", "1", "Scale factor"));
                    }

                    // Observation metadata
                    double exposureSec = ExposureTime_us / 1_000_000.0;
                    headerCards.Add(FormatFitsCard("EXPOSURE", exposureSec.ToString("F6"), "Exposure time (seconds)"));
                    headerCards.Add(FormatFitsCard("EXPTIME", exposureSec.ToString("F6"), "Exposure time (seconds)"));
                    headerCards.Add(FormatFitsCard("GAIN", Gain.ToString(), "Camera gain"));
                    headerCards.Add(FormatFitsCard("OFFSET", Offset.ToString(), "Camera offset"));
                    headerCards.Add(FormatFitsCard("XBINNING", Binning.ToString(), "X binning"));
                    headerCards.Add(FormatFitsCard("YBINNING", Binning.ToString(), "Y binning"));

                    // Timestamp
                    string dateObs = CaptureTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff");
                    headerCards.Add(FormatFitsCardString("DATE-OBS", dateObs, "UTC capture time"));

                    // Temperature
                    if (Temperature_C.HasValue)
                        headerCards.Add(FormatFitsCard("CCD-TEMP", Temperature_C.Value.ToString("F1"), "Sensor temperature (C)"));

                    // Mount position
                    if (Mount_Azimuth.HasValue)
                        headerCards.Add(FormatFitsCard("OBJCTAZ", Mount_Azimuth.Value.ToString("F6"), "Mount azimuth (deg)"));
                    if (Mount_Altitude.HasValue)
                        headerCards.Add(FormatFitsCard("OBJCTALT", Mount_Altitude.Value.ToString("F6"), "Mount altitude (deg)"));

                    // Observer location
                    if (Observer_Latitude.HasValue)
                        headerCards.Add(FormatFitsCard("SITELAT", Observer_Latitude.Value.ToString("F6"), "Observer latitude (deg)"));
                    if (Observer_Longitude.HasValue)
                        headerCards.Add(FormatFitsCard("SITELONG", Observer_Longitude.Value.ToString("F6"), "Observer longitude (deg)"));

                    // Tracking object name
                    string trackingObj = "";
                    try
                    {
                        var mountInfo = App.MountInfo;
                        if (mountInfo != null)
                            trackingObj = mountInfo.CurrentTrackingObjectName ?? "";
                        
                        // Fallback: read directly from TrackingManager (works even without PollingManager)
                        if (string.IsNullOrWhiteSpace(trackingObj))
                        {
                            var target = ForkMount_WPF.Engine.Manager.TrackingManager.Instance.CurrentTarget;
                            if (target != null)
                                trackingObj = target.ObjectName ?? "";
                        }
                    }
                    catch { }
                    if (!string.IsNullOrWhiteSpace(trackingObj))
                        headerCards.Add(FormatFitsCardString("OBJECT", trackingObj, "Tracking target name"));

                    headerCards.Add(FormatFitsCardString("IMAGETYP", "LIGHT", "Image type"));
                    headerCards.Add(FormatFitsCardString("INSTRUME", "ZWO ASI Camera", "Instrument"));
                    headerCards.Add(FormatFitsCardString("SOFTWARE", "ForkMount-WPF", "Software"));

                    headerCards.Add("END".PadRight(80));

                    // Pad header to multiple of 2880 bytes
                    int headerChars = headerCards.Count * 80;
                    int headerBlocks = (headerChars + 2879) / 2880;
                    int totalHeaderBytes = headerBlocks * 2880;

                    byte[] headerBytes = new byte[totalHeaderBytes];
                    for (int i = 0; i < headerBytes.Length; i++)
                        headerBytes[i] = 0x20; // Fill with ASCII space

                    for (int i = 0; i < headerCards.Count; i++)
                    {
                        string card = headerCards[i].PadRight(80).Substring(0, 80);
                        byte[] cardBytes = Encoding.ASCII.GetBytes(card);
                        Buffer.BlockCopy(cardBytes, 0, headerBytes, i * 80, 80);
                    }

                    fs.Write(headerBytes, 0, headerBytes.Length);

                    // --- Write Pixel Data (big-endian) ---
                    int pixelCount = Width * Height;

                    if (is16bit)
                    {
                        // RAW16: Convert unsigned 16-bit to FITS signed 16-bit (subtract BZERO=32768)
                        byte[] pixelBuffer = new byte[pixelCount * 2];
                        for (int i = 0; i < pixelCount; i++)
                        {
                            ushort raw = BitConverter.ToUInt16(ImageData, i * 2);
                            short fitsVal = (short)(raw - 32768);
                            pixelBuffer[i * 2] = (byte)(fitsVal >> 8);
                            pixelBuffer[i * 2 + 1] = (byte)(fitsVal & 0xFF);
                        }
                        fs.Write(pixelBuffer, 0, pixelBuffer.Length);

                        int dataBytes = pixelCount * 2;
                        int dataPadding = ((dataBytes + 2879) / 2880) * 2880 - dataBytes;
                        if (dataPadding > 0)
                            fs.Write(new byte[dataPadding], 0, dataPadding);
                    }
                    else if (isRGB)
                    {
                        // RGB24: ZWO stores as BGR interleaved (B,G,R,B,G,R,...)
                        // FITS expects plane-separated: all R, then all G, then all B
                        byte[] planeBuffer = new byte[pixelCount * 3];
                        int rOffset = 0;
                        int gOffset = pixelCount;
                        int bOffset = pixelCount * 2;

                        for (int i = 0; i < pixelCount; i++)
                        {
                            int srcIdx = i * 3;
                            planeBuffer[rOffset + i] = ImageData[srcIdx + 2]; // R
                            planeBuffer[gOffset + i] = ImageData[srcIdx + 1]; // G
                            planeBuffer[bOffset + i] = ImageData[srcIdx];     // B
                        }
                        fs.Write(planeBuffer, 0, planeBuffer.Length);

                        int dataBytes = pixelCount * 3;
                        int dataPadding = ((dataBytes + 2879) / 2880) * 2880 - dataBytes;
                        if (dataPadding > 0)
                            fs.Write(new byte[dataPadding], 0, dataPadding);
                    }
                    else
                    {
                        // RAW8/Y8: Write directly (8-bit, no endian swap needed)
                        fs.Write(ImageData, 0, pixelCount);

                        int dataPadding = ((pixelCount + 2879) / 2880) * 2880 - pixelCount;
                        if (dataPadding > 0)
                            fs.Write(new byte[dataPadding], 0, dataPadding);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[CameraImageData] FITS saved: {filePath} ({Width}x{Height} {bitsPerPixel}-bit{(isRGB ? " RGB" : "")})");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraImageData] Error saving FITS: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Format a FITS header card: KEYWORD = value / comment
        /// </summary>
        private static string FormatFitsCard(string keyword, string value, string comment)
        {
            string kw = keyword.PadRight(8).Substring(0, 8);
            string valPart = value.PadLeft(20);
            string card = $"{kw}= {valPart} / {comment}";
            return card.PadRight(80).Substring(0, 80);
        }

        /// <summary>
        /// Format a FITS header card with string value (single-quoted)
        /// </summary>
        private static string FormatFitsCardString(string keyword, string value, string comment)
        {
            string kw = keyword.PadRight(8).Substring(0, 8);
            string quoted = $"'{value}'".PadRight(20);
            string card = $"{kw}= {quoted} / {comment}";
            return card.PadRight(80).Substring(0, 80);
        }

        /// <summary>
        /// Get exposure time in seconds
        /// </summary>
        public double GetExposureTime_Seconds()
        {
            return ExposureTime_us / 1_000_000.0;
        }

        /// <summary>
        /// Get image size in megabytes
        /// </summary>
        public double GetImageSize_MB()
        {
            if (ImageData == null)
                return 0;

            return ImageData.Length / (1024.0 * 1024.0);
        }

        /// <summary>
        /// Get bits per pixel
        /// </summary>
        public int GetBitsPerPixel()
        {
            return ImageType switch
            {
                ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW8 => 8,
                ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_Y8 => 8,
                ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16 => 16,
                ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RGB24 => 24,
                _ => 0
            };
        }

        /// <summary>
        /// Get BitmapImage with detected stars overlaid
        /// Shows circles around detected stars for better visualization
        /// </summary>
        public BitmapImage GetBitmapImageWithStars(List<DetectedStar> stars, int markerSize = 30, System.Windows.Media.Color? markerColor = null)
        {
            if (ImageData == null || Width == 0 || Height == 0)
                return null;

            try
            {
                // Get base stretched image
                var baseImage = GetBitmapImage();
                if (baseImage == null)
                    return baseImage;

                // If no stars, return base image
                if (stars == null || stars.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[CameraImageData] No stars to overlay");
                    return baseImage;
                }

                // Create DrawingVisual to overlay stars
                var drawingVisual = new System.Windows.Media.DrawingVisual();
                using (var drawingContext = drawingVisual.RenderOpen())
                {
                    // Draw base image
                    drawingContext.DrawImage(baseImage, new System.Windows.Rect(0, 0, Width, Height));

                    // Set marker color (default: bright cyan)
                    var color = markerColor ?? System.Windows.Media.Color.FromArgb(255, 0, 255, 255); // Cyan
                    var pen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(color), 2);
                    pen.Freeze();

                    // Draw circle around each star
                    foreach (var star in stars)
                    {
                        var center = new System.Windows.Point(star.X, star.Y);
                        drawingContext.DrawEllipse(null, pen, center, markerSize / 2.0, markerSize / 2.0);

                        // Draw crosshair at center
                        var crosshairSize = markerSize / 4.0;
                        drawingContext.DrawLine(pen,
                            new System.Windows.Point(star.X - crosshairSize, star.Y),
                            new System.Windows.Point(star.X + crosshairSize, star.Y));
                        drawingContext.DrawLine(pen,
                            new System.Windows.Point(star.X, star.Y - crosshairSize),
                            new System.Windows.Point(star.X, star.Y + crosshairSize));
                    }
                }

                // Render to bitmap
                var renderBitmap = new RenderTargetBitmap(Width, Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                renderBitmap.Render(drawingVisual);

                // Convert to BitmapImage
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                var bitmapImage = new BitmapImage();
                using (var stream = new System.IO.MemoryStream())
                {
                    encoder.Save(stream);
                    stream.Seek(0, System.IO.SeekOrigin.Begin);

                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.StreamSource = stream;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                }

                System.Diagnostics.Debug.WriteLine($"[CameraImageData] Overlaid {stars.Count} stars on image (marker size={markerSize}px)");
                return bitmapImage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraImageData] Error overlaying stars: {ex.Message}");
                return GetBitmapImage(); // Fallback to base image
            }
        }
    }
}
