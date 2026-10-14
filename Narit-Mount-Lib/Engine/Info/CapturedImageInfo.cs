using System;
using ForkMount_WPF.Engine.Camera;
using static ForkMount_WPF.Engine.Camera.ZWOASICameraSDK;

namespace ForkMount_WPF.Engine.Info
{
    /// <summary>
    /// Information about a captured astronomical image
    /// Used for Plate Solving and Mount Calibration
    /// </summary>
    public class CapturedImageInfo
    {
        #region Identification
        
        /// <summary>
        /// Unique identifier for this image
        /// </summary>
        public Guid ImageId { get; set; } = Guid.NewGuid();
        
        /// <summary>
        /// Capture timestamp (UTC)
        /// Used as primary search key
        /// </summary>
        public DateTime CaptureTime { get; set; }
        
        #endregion
        
        #region Mount Position (Search Keys)
        
        /// <summary>
        /// Telescope Azimuth at capture time (degrees)
        /// Used as search key for Plate Solving
        /// </summary>
        public double Azimuth_deg { get; set; }
        
        /// <summary>
        /// Telescope Altitude at capture time (degrees)
        /// Used as search key for Plate Solving
        /// </summary>
        public double Altitude_deg { get; set; }
        
        /// <summary>
        /// Right Ascension (if calculated from Az/Alt)
        /// </summary>
        public double? RA_deg { get; set; }
        
        /// <summary>
        /// Declination (if calculated from Az/Alt)
        /// </summary>
        public double? Dec_deg { get; set; }
        
        #endregion
        
        #region Image Data
        
        /// <summary>
        /// 16-bit image data in 2D array format [row, col]
        /// Ready for Plate Solving algorithms
        /// Format: ImageData[height, width]
        /// </summary>
        public ushort[,] ImageData { get; set; }
        
        /// <summary>
        /// Image width in pixels (after binning)
        /// </summary>
        public int Width { get; set; }
        
        /// <summary>
        /// Image height in pixels (after binning)
        /// </summary>
        public int Height { get; set; }
        
        #endregion
        
        #region Camera Settings
        
        /// <summary>
        /// Binning mode used for capture (1, 2, 3, or 4)
        /// </summary>
        public int Binning { get; set; }
        
        /// <summary>
        /// Exposure time in milliseconds
        /// </summary>
        public int ExposureTime_ms { get; set; }
        
        /// <summary>
        /// Camera gain setting
        /// </summary>
        public int Gain { get; set; }
        
        /// <summary>
        /// Camera offset setting
        /// </summary>
        public int Offset { get; set; }
        
        /// <summary>
        /// Image type (RAW8, RAW16, RGB24)
        /// </summary>
        public ASI_IMG_TYPE ImageType { get; set; }
        
        /// <summary>
        /// Camera temperature at capture time (Celsius)
        /// </summary>
        public double? Temperature_C { get; set; }
        
        #endregion
        
        #region Optical Configuration
        
        /// <summary>
        /// Focal length in millimeters
        /// </summary>
        public double FocalLength_mm { get; set; }
        
        /// <summary>
        /// Plate scale in arcseconds per pixel
        /// Critical for Plate Solving accuracy
        /// </summary>
        public double PlateScale_ArcsecPerPixel { get; set; }
        
        /// <summary>
        /// Field of View width (degrees)
        /// </summary>
        public double FOV_Width_deg { get; set; }
        
        /// <summary>
        /// Field of View height (degrees)
        /// </summary>
        public double FOV_Height_deg { get; set; }
        
        #endregion
        
        #region Plate Solve Results
        
        /// <summary>
        /// Plate solve status
        /// </summary>
        public PlateSolveStatus PlateSolveStatus { get; set; } = PlateSolveStatus.NotSolved;
        
        /// <summary>
        /// Solved RA (degrees) from Plate Solving
        /// </summary>
        public double? Solved_RA_deg { get; set; }
        
        /// <summary>
        /// Solved Dec (degrees) from Plate Solving
        /// </summary>
        public double? Solved_Dec_deg { get; set; }
        
        /// <summary>
        /// Plate solve error (arcseconds)
        /// Difference between commanded position and solved position
        /// </summary>
        public double? PositionError_arcsec { get; set; }
        
        /// <summary>
        /// Field rotation angle (degrees)
        /// </summary>
        public double? FieldRotation_deg { get; set; }
        
        /// <summary>
        /// Number of stars detected in image
        /// </summary>
        public int? StarsDetected { get; set; }
        
        /// <summary>
        /// Time taken to solve (seconds)
        /// </summary>
        public double? SolveTime_sec { get; set; }
        
        #endregion
        
        #region Quality Metrics
        
        /// <summary>
        /// Image quality score (0-100)
        /// Based on star FWHM, SNR, etc.
        /// </summary>
        public double? QualityScore { get; set; }
        
        /// <summary>
        /// Minimum pixel value in image
        /// </summary>
        public ushort MinPixelValue { get; set; }
        
        /// <summary>
        /// Maximum pixel value in image
        /// </summary>
        public ushort MaxPixelValue { get; set; }
        
        /// <summary>
        /// Mean pixel value
        /// </summary>
        public double MeanPixelValue { get; set; }
        
        /// <summary>
        /// Standard deviation of pixel values
        /// </summary>
        public double StdDevPixelValue { get; set; }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Calculate image statistics (min, max, mean, stddev)
        /// </summary>
        public void CalculateStatistics()
        {
            if (ImageData == null || ImageData.Length == 0)
                return;
            
            ushort min = ushort.MaxValue;
            ushort max = ushort.MinValue;
            long sum = 0;
            long count = Width * Height;
            
            // Calculate min, max, mean
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    ushort value = ImageData[y, x];
                    if (value < min) min = value;
                    if (value > max) max = value;
                    sum += value;
                }
            }
            
            MinPixelValue = min;
            MaxPixelValue = max;
            MeanPixelValue = (double)sum / count;
            
            // Calculate standard deviation
            double sumSquaredDiff = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    double diff = ImageData[y, x] - MeanPixelValue;
                    sumSquaredDiff += diff * diff;
                }
            }
            
            StdDevPixelValue = Math.Sqrt(sumSquaredDiff / count);
        }
        
        /// <summary>
        /// Get memory size of image data (MB)
        /// </summary>
        public double GetImageSize_MB()
        {
            if (ImageData == null)
                return 0;
            
            // ushort = 2 bytes
            long bytes = Width * Height * 2;
            return bytes / (1024.0 * 1024.0);
        }
        
        /// <summary>
        /// Create preview image for UI display (8-bit, scaled, stretched) - ASYNC VERSION
        /// Much more memory efficient than displaying full 16-bit image
        /// Does not block UI thread!
        /// </summary>
        /// <param name="maxWidth">Maximum preview width (default: 800px)</param>
        /// <param name="maxHeight">Maximum preview height (default: 600px)</param>
        /// <param name="autoStretch">Apply auto histogram stretch (default: true)</param>
        /// <returns>8-bit BitmapImage suitable for WPF display</returns>
        public async System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapImage> CreatePreviewImageAsync(
            int maxWidth = 800, 
            int maxHeight = 600, 
            bool autoStretch = true)
        {
            if (ImageData == null)
            {
                System.Diagnostics.Debug.WriteLine("[CapturedImageInfo] Cannot create preview - no image data");
                return null;
            }
            
            return await ForkMount_WPF.Engine.Utility.ImagePreviewHelper.CreatePreviewImageAsync(
                ImageData, 
                maxWidth, 
                maxHeight, 
                autoStretch);
        }
        
        /// <summary>
        /// Create preview image for UI display (8-bit, scaled, stretched) - SYNCHRONOUS VERSION
        /// Much more memory efficient than displaying full 16-bit image
        /// </summary>
        /// <param name="maxWidth">Maximum preview width (default: 800px)</param>
        /// <param name="maxHeight">Maximum preview height (default: 600px)</param>
        /// <param name="autoStretch">Apply auto histogram stretch (default: true)</param>
        /// <returns>8-bit BitmapImage suitable for WPF display</returns>
        public System.Windows.Media.Imaging.BitmapImage CreatePreviewImage(
            int maxWidth = 800, 
            int maxHeight = 600, 
            bool autoStretch = true)
        {
            if (ImageData == null)
            {
                System.Diagnostics.Debug.WriteLine("[CapturedImageInfo] Cannot create preview - no image data");
                return null;
            }
            
            return ForkMount_WPF.Engine.Utility.ImagePreviewHelper.CreatePreviewImage(
                ImageData, 
                maxWidth, 
                maxHeight, 
                autoStretch);
        }
        
        /// <summary>
        /// Create from CameraImageData
        /// </summary>
        public static CapturedImageInfo FromCameraImageData(
            CameraImageData cameraData, 
            double azimuth, 
            double altitude,
            double focalLength_mm,
            double plateScale_arcsecPerPixel)
        {
            if (cameraData == null)
                throw new ArgumentNullException(nameof(cameraData));
            
            if (cameraData.ImageType != ASI_IMG_TYPE.ASI_IMG_RAW16)
                throw new ArgumentException("Only RAW16 images are supported for plate solving", nameof(cameraData));
            
            // Convert byte[] to ushort[,] 2D array
            var imageData2D = ConvertByteArrayTo2DArray(
                cameraData.ImageData, 
                cameraData.Width, 
                cameraData.Height);
            
            var imageInfo = new CapturedImageInfo
            {
                CaptureTime = cameraData.CaptureTime,
                Azimuth_deg = azimuth,
                Altitude_deg = altitude,
                ImageData = imageData2D,
                Width = cameraData.Width,
                Height = cameraData.Height,
                Binning = cameraData.Binning,
                ExposureTime_ms = (int)(cameraData.ExposureTime_us / 1000),
                Gain = cameraData.Gain,
                Offset = cameraData.Offset,
                ImageType = cameraData.ImageType,
                Temperature_C = cameraData.Temperature_C,
                FocalLength_mm = focalLength_mm,
                PlateScale_ArcsecPerPixel = plateScale_arcsecPerPixel
            };
            
            // Calculate FOV
            imageInfo.FOV_Width_deg = (cameraData.Width * plateScale_arcsecPerPixel) / 3600.0;
            imageInfo.FOV_Height_deg = (cameraData.Height * plateScale_arcsecPerPixel) / 3600.0;
            
            // Calculate statistics
            imageInfo.CalculateStatistics();
            
            return imageInfo;
        }
        
        /// <summary>
        /// Convert RAW16 byte array to 2D ushort array
        /// </summary>
        private static ushort[,] ConvertByteArrayTo2DArray(byte[] byteArray, int width, int height)
        {
            var result = new ushort[height, width];
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 2; // 2 bytes per pixel
                    result[y, x] = BitConverter.ToUInt16(byteArray, index);
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Convert 2D array back to byte array (for saving)
        /// </summary>
        public byte[] ConvertTo1DByteArray()
        {
            var result = new byte[Width * Height * 2];
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = (y * Width + x) * 2;
                    byte[] bytes = BitConverter.GetBytes(ImageData[y, x]);
                    result[index] = bytes[0];
                    result[index + 1] = bytes[1];
                }
            }
            
            return result;
        }
        
        #endregion
        
        public override string ToString()
        {
            return $"Image {ImageId:N} captured at {CaptureTime:yyyy-MM-dd HH:mm:ss} UTC " +
                   $"(Az={Azimuth_deg:F2}°, Alt={Altitude_deg:F2}°, {Width}x{Height}, " +
                   $"Bin={Binning}, Exp={ExposureTime_ms}ms, Status={PlateSolveStatus})";
        }
    }
    
    /// <summary>
    /// Plate solve status
    /// </summary>
    public enum PlateSolveStatus
    {
        /// <summary>
        /// Not yet attempted
        /// </summary>
        NotSolved,
        
        /// <summary>
        /// Currently solving
        /// </summary>
        Solving,
        
        /// <summary>
        /// Successfully solved
        /// </summary>
        Solved,
        
        /// <summary>
        /// Failed to solve
        /// </summary>
        Failed,
        
        /// <summary>
        /// Timeout during solving
        /// </summary>
        Timeout
    }
}
