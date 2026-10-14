using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using AForge;
using AForge.Imaging;
using AForge.Imaging.Filters;
using AForge.Math;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// Star detector for astronomical images using AForge.NET
    /// Performs preprocessing, detection, and sub-pixel centroiding
    /// </summary>
    public class StarDetector
    {
        #region Configuration Parameters

        /// <summary>
        /// Threshold multiplier for star detection (sigma above mean)
        /// </summary>
        public double ThresholdSigma { get; set; } = 5.0;

        /// <summary>
        /// Minimum star size in pixels
        /// </summary>
        public int MinStarSize { get; set; } = 3;

        /// <summary>
        /// Maximum star size in pixels
        /// </summary>
        public int MaxStarSize { get; set; } = 50;

        /// <summary>
        /// Minimum circularity (0-1, 1 = perfect circle)
        /// </summary>
        public double MinCircularity { get; set; } = 0.5;

        /// <summary>
        /// Centroid window size (pixels around star center)
        /// </summary>
        public int CentroidWindowSize { get; set; } = 7;

        /// <summary>
        /// Enable preprocessing (background subtraction, noise reduction)
        /// </summary>
        public bool EnablePreprocessing { get; set; } = true;

        /// <summary>
        /// Enable contrast enhancement
        /// </summary>
        public bool EnableContrastEnhancement { get; set; } = true;

        /// <summary>
        /// Maximum number of stars to return (0 = unlimited)
        /// </summary>
        public int MaxStars { get; set; } = 200;

        #endregion

        #region Detection Methods

        /// <summary>
        /// Detect stars in camera image data
        /// </summary>
        public List<DetectedStar> DetectStars(CameraImageData imageData)
        {
            if (imageData == null || imageData.ImageData == null)
            {
                System.Diagnostics.Debug.WriteLine("[StarDetector] Error: Null image data");
                return new List<DetectedStar>();
            }

            try
            {
                // Convert to AForge Bitmap
                Bitmap bitmap = ConvertToBitmap(imageData);
                
                // Detect stars
                var stars = DetectStars(bitmap);
                
                bitmap.Dispose();
                
                return stars;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] Error: {ex.Message}");
                return new List<DetectedStar>();
            }
        }

        /// <summary>
        /// Detect stars in bitmap image
        /// </summary>
        public List<DetectedStar> DetectStars(Bitmap image)
        {
            if (image == null)
            {
                System.Diagnostics.Debug.WriteLine("[StarDetector] Error: Null bitmap");
                return new List<DetectedStar>();
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] Processing {image.Width}x{image.Height} image");

                // Step 1: Preprocess image
                Bitmap processed = EnablePreprocessing ? PreprocessImage(image) : (Bitmap)image.Clone();

                // Step 2: Enhance contrast
                if (EnableContrastEnhancement)
                {
                    processed = EnhanceContrast(processed);
                }

                // Step 3: Calculate threshold
                ImageStatistics stats = new ImageStatistics(processed);
                double mean = stats.Gray.Mean;
                double stdDev = stats.Gray.StdDev;
                int threshold = (int)Math.Min(255, mean + ThresholdSigma * stdDev);

                System.Diagnostics.Debug.WriteLine($"[StarDetector] Statistics: Mean={mean:F2}, StdDev={stdDev:F2}, Threshold={threshold}");

                // [FIX] If threshold >= 255, only saturated pixels pass ? BlobCounter will find
                // thousands of hot/saturated pixels scattered across 25MP image ? takes forever.
                // Skip blob detection entirely — no meaningful stars can be detected.
                if (threshold >= 255)
                {
                    System.Diagnostics.Debug.WriteLine($"[StarDetector] Threshold >= 255 ? image too bright/saturated, skipping blob detection");
                    processed.Dispose();
                    return new List<DetectedStar>();
                }

                // Step 4: Threshold image
                Threshold thresholdFilter = new Threshold(threshold);
                Bitmap binary = thresholdFilter.Apply(processed);

                // Step 5: Find blobs (connected components)
                BlobCounter blobCounter = new BlobCounter
                {
                    FilterBlobs = true,
                    MinWidth = MinStarSize,
                    MinHeight = MinStarSize,
                    MaxWidth = MaxStarSize,
                    MaxHeight = MaxStarSize
                };

                blobCounter.ProcessImage(binary);
                Blob[] blobs = blobCounter.GetObjectsInformation();

                System.Diagnostics.Debug.WriteLine($"[StarDetector] Found {blobs.Length} candidate blobs");

                // Step 6: Filter and calculate centroids
                List<DetectedStar> stars = new List<DetectedStar>();

                foreach (var blob in blobs)
                {
                    // Calculate circularity using area only (simplified)
                    double area = blob.Area;
                    double circularity = (area > 0) ? (4 * Math.PI * area) / Math.Pow(2 * Math.Sqrt(area / Math.PI) * 2 * Math.PI, 2) : 0;
                    
                    // Simplified: just check if blob is roughly circular based on fullness
                    double fullness = blob.Fullness; // Ratio of actual area to bounding rectangle
                    circularity = fullness;

                    // Filter by circularity
                    if (circularity < MinCircularity)
                        continue;

                    // Calculate sub-pixel centroid
                    var centroid = CalculateWeightedCentroid(processed, blob.CenterOfGravity);

                    // Calculate brightness (sum of intensities in blob)
                    double brightness = CalculateBrightness(processed, blob);

                    // Create star object
                    DetectedStar star = new DetectedStar
                    {
                        X = centroid.X,
                        Y = centroid.Y,
                        IntegerX = (int)Math.Round(centroid.X),
                        IntegerY = (int)Math.Round(centroid.Y),
                        Brightness = brightness,
                        Area = area,
                        Circularity = circularity,
                        FWHM = EstimateFWHM(processed, centroid, blob)
                    };

                    stars.Add(star);
                }

                // Step 7: Sort by brightness and limit count
                stars = stars.OrderByDescending(s => s.Brightness).ToList();
                
                if (MaxStars > 0 && stars.Count > MaxStars)
                {
                    stars = stars.Take(MaxStars).ToList();
                }

                System.Diagnostics.Debug.WriteLine($"[StarDetector] Detected {stars.Count} stars after filtering");

                // Cleanup
                processed.Dispose();
                binary.Dispose();

                return stars;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] Detection error: {ex.Message}");
                return new List<DetectedStar>();
            }
        }

        #endregion

        #region Image Preprocessing

        /// <summary>
        /// Preprocess image: background subtraction + noise reduction
        /// </summary>
        private Bitmap PreprocessImage(Bitmap image)
        {
            // Clone image
            Bitmap processed = (Bitmap)image.Clone();

            try
            {
                // Remove hot pixels with median filter
                Median medianFilter = new Median(3);
                processed = medianFilter.Apply(processed);

                // Subtract background (using morphological opening)
                Opening openingFilter = new Opening(new short[,] {
                    { 1, 1, 1, 1, 1 },
                    { 1, 1, 1, 1, 1 },
                    { 1, 1, 1, 1, 1 },
                    { 1, 1, 1, 1, 1 },
                    { 1, 1, 1, 1, 1 }
                });

                Bitmap background = openingFilter.Apply(processed);

                // Subtract background
                Subtract subtractFilter = new Subtract(background);
                processed = subtractFilter.Apply(processed);

                background.Dispose();

                System.Diagnostics.Debug.WriteLine("[StarDetector] Preprocessing complete");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] Preprocessing error: {ex.Message}");
            }

            return processed;
        }

        /// <summary>
        /// Enhance contrast using histogram stretching
        /// </summary>
        private Bitmap EnhanceContrast(Bitmap image)
        {
            try
            {
                // Linear histogram stretching
                ContrastStretch contrastStretch = new ContrastStretch();
                return contrastStretch.Apply(image);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] Contrast enhancement error: {ex.Message}");
                return (Bitmap)image.Clone();
            }
        }

        #endregion

        #region Centroid Calculation

        /// <summary>
        /// Calculate weighted centroid with sub-pixel accuracy
        /// </summary>
        private DoublePoint CalculateWeightedCentroid(Bitmap image, AForge.Point integerCenter)
        {
            try
            {
                int halfWindow = CentroidWindowSize / 2;
                double sumX = 0, sumY = 0, sumIntensity = 0;

                // Lock bitmap for fast pixel access
                BitmapData bitmapData = image.LockBits(
                    new Rectangle(0, 0, image.Width, image.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format8bppIndexed);

                unsafe
                {
                    byte* ptr = (byte*)bitmapData.Scan0;
                    int stride = bitmapData.Stride;

                    // Calculate weighted centroid in window around integer center
                    for (int dy = -halfWindow; dy <= halfWindow; dy++)
                    {
                        for (int dx = -halfWindow; dx <= halfWindow; dx++)
                        {
                            int x = (int)Math.Round(integerCenter.X) + dx;
                            int y = (int)Math.Round(integerCenter.Y) + dy;

                            // Check bounds
                            if (x < 0 || x >= image.Width || y < 0 || y >= image.Height)
                                continue;

                            // Get pixel intensity
                            double intensity = ptr[y * stride + x];

                            sumX += x * intensity;
                            sumY += y * intensity;
                            sumIntensity += intensity;
                        }
                    }
                }

                image.UnlockBits(bitmapData);

                if (sumIntensity > 0)
                {
                    return new DoublePoint(sumX / sumIntensity, sumY / sumIntensity);
                }
                else
                {
                    return new DoublePoint(integerCenter.X, integerCenter.Y);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] Centroid calculation error: {ex.Message}");
                return new DoublePoint(integerCenter.X, integerCenter.Y);
            }
        }

        /// <summary>
        /// Calculate total brightness of blob
        /// </summary>
        private double CalculateBrightness(Bitmap image, Blob blob)
        {
            try
            {
                double sum = 0;

                BitmapData bitmapData = image.LockBits(
                    new Rectangle(0, 0, image.Width, image.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format8bppIndexed);

                unsafe
                {
                    byte* ptr = (byte*)bitmapData.Scan0;
                    int stride = bitmapData.Stride;

                    for (int y = blob.Rectangle.Top; y < blob.Rectangle.Bottom; y++)
                    {
                        for (int x = blob.Rectangle.Left; x < blob.Rectangle.Right; x++)
                        {
                            if (x >= 0 && x < image.Width && y >= 0 && y < image.Height)
                            {
                                sum += ptr[y * stride + x];
                            }
                        }
                    }
                }

                image.UnlockBits(bitmapData);

                return sum;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] Brightness calculation error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Estimate Full Width at Half Maximum (FWHM) of star from second moments around centroid.
        /// </summary>
        private double EstimateFWHM(Bitmap image, DoublePoint centroid, Blob blob)
        {
            try
            {
                int halfWindow = Math.Max(
                    CentroidWindowSize,
                    Math.Max(blob.Rectangle.Width, blob.Rectangle.Height)) / 2 + 2;

                int left = Math.Max(0, (int)Math.Floor(centroid.X) - halfWindow);
                int right = Math.Min(image.Width - 1, (int)Math.Ceiling(centroid.X) + halfWindow);
                int top = Math.Max(0, (int)Math.Floor(centroid.Y) - halfWindow);
                int bottom = Math.Min(image.Height - 1, (int)Math.Ceiling(centroid.Y) + halfWindow);

                var borderSamples = new List<double>();

                BitmapData bitmapData = image.LockBits(
                    new Rectangle(0, 0, image.Width, image.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format8bppIndexed);

                double sumSignal = 0;
                double sumX2 = 0;
                double sumY2 = 0;

                unsafe
                {
                    byte* ptr = (byte*)bitmapData.Scan0;
                    int stride = bitmapData.Stride;

                    for (int y = top; y <= bottom; y++)
                    {
                        for (int x = left; x <= right; x++)
                        {
                            double intensity = ptr[y * stride + x];
                            bool isBorder = x == left || x == right || y == top || y == bottom;
                            if (isBorder)
                            {
                                borderSamples.Add(intensity);
                            }
                        }
                    }

                    double background = borderSamples.Count > 0
                        ? CalculateMedian(borderSamples)
                        : 0.0;

                    for (int y = top; y <= bottom; y++)
                    {
                        for (int x = left; x <= right; x++)
                        {
                            double signal = ptr[y * stride + x] - background;
                            if (signal <= 0)
                                continue;

                            double dx = x - centroid.X;
                            double dy = y - centroid.Y;

                            sumSignal += signal;
                            sumX2 += dx * dx * signal;
                            sumY2 += dy * dy * signal;
                        }
                    }
                }

                image.UnlockBits(bitmapData);

                if (sumSignal <= 0)
                {
                    return EstimateFwhmFromBlobArea(blob);
                }

                double sigmaX = Math.Sqrt(sumX2 / sumSignal);
                double sigmaY = Math.Sqrt(sumY2 / sumSignal);
                double sigma = Math.Sqrt(Math.Max(0.0, (sigmaX * sigmaX + sigmaY * sigmaY) / 2.0));

                if (double.IsNaN(sigma) || double.IsInfinity(sigma) || sigma <= 0)
                {
                    return EstimateFwhmFromBlobArea(blob);
                }

                return 2.355 * sigma;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StarDetector] FWHM estimation error: {ex.Message}");
                return EstimateFwhmFromBlobArea(blob);
            }
        }

        private static double EstimateFwhmFromBlobArea(Blob blob)
        {
            double sigma = Math.Sqrt(blob.Area / Math.PI);
            return 2.355 * sigma;
        }

        private static double CalculateMedian(List<double> values)
        {
            if (values == null || values.Count == 0)
                return 0;

            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;

            if (sorted.Count % 2 == 0)
            {
                return (sorted[mid - 1] + sorted[mid]) / 2.0;
            }

            return sorted[mid];
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Convert CameraImageData to Bitmap
        /// </summary>
        private Bitmap ConvertToBitmap(CameraImageData imageData)
        {
            int width = imageData.Width;
            int height = imageData.Height;

            // Create 8-bit grayscale bitmap
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed);

            // Set grayscale palette
            ColorPalette palette = bitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(i, i, i);
            }
            bitmap.Palette = palette;

            // Copy image data
            BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format8bppIndexed);

            unsafe
            {
                byte* ptr = (byte*)bitmapData.Scan0;
                int stride = bitmapData.Stride;

                if (imageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16)
                {
                    // 16-bit to 8-bit conversion
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int srcIndex = (y * width + x) * 2;
                            ushort value16 = BitConverter.ToUInt16(imageData.ImageData, srcIndex);
                            byte value8 = (byte)(value16 >> 8); // Take high byte
                            ptr[y * stride + x] = value8;
                        }
                    }
                }
                else if (imageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW8 ||
                         imageData.ImageType == ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_Y8)
                {
                    // 8-bit direct copy
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int index = y * width + x;
                            ptr[y * stride + x] = imageData.ImageData[index];
                        }
                    }
                }
            }

            bitmap.UnlockBits(bitmapData);

            return bitmap;
        }

        #endregion
    }

    #region Data Structures

    /// <summary>
    /// Represents a detected star with sub-pixel position
    /// </summary>
    public class DetectedStar
    {
        /// <summary>
        /// X position with sub-pixel accuracy
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// Y position with sub-pixel accuracy
        /// </summary>
        public double Y { get; set; }

        /// <summary>
        /// Integer X position (rounded)
        /// </summary>
        public int IntegerX { get; set; }

        /// <summary>
        /// Integer Y position (rounded)
        /// </summary>
        public int IntegerY { get; set; }

        /// <summary>
        /// Total brightness (sum of pixel intensities)
        /// </summary>
        public double Brightness { get; set; }

        /// <summary>
        /// Area in pixels
        /// </summary>
        public double Area { get; set; }

        /// <summary>
        /// Circularity (0-1, 1 = perfect circle)
        /// </summary>
        public double Circularity { get; set; }

        /// <summary>
        /// Full Width at Half Maximum (pixels)
        /// </summary>
        public double FWHM { get; set; }

        /// <summary>
        /// Signal-to-Noise Ratio (calculated later)
        /// </summary>
        public double SNR { get; set; }

        public override string ToString()
        {
            return $"Star at ({X:F2}, {Y:F2}), Brightness={Brightness:F0}, FWHM={FWHM:F2}";
        }
    }

    /// <summary>
    /// Double precision point
    /// </summary>
    public struct DoublePoint
    {
        public double X { get; set; }
        public double Y { get; set; }

        public DoublePoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    #endregion
}
