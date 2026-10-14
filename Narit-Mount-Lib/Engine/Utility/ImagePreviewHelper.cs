using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Helper class for creating optimized preview images (8-bit, scaled, stretched)
    /// Used for UI display only - saves RAM compared to full 16-bit images
    /// </summary>
    public static class ImagePreviewHelper
    {
        /// <summary>
        /// Create preview BitmapImage from 16-bit image data (ASYNC VERSION)
        /// - Downscales to target resolution
        /// - Applies histogram stretch
        /// - Converts to 8-bit
        /// Memory efficient for UI preview only
        /// </summary>
        public static async Task<BitmapImage> CreatePreviewImageAsync(
            ushort[,] imageData16bit,
            int maxPreviewWidth = 800,
            int maxPreviewHeight = 600,
            bool autoStretch = true)
        {
            if (imageData16bit == null)
                throw new ArgumentNullException(nameof(imageData16bit));

            // Run all processing on background thread
            return await Task.Run(() => CreatePreviewImage(imageData16bit, maxPreviewWidth, maxPreviewHeight, autoStretch));
        }
        
        /// <summary>
        /// Create preview BitmapImage from 16-bit image data (SYNCHRONOUS VERSION)
        /// - Downscales to target resolution
        /// - Applies histogram stretch
        /// - Converts to 8-bit
        /// Memory efficient for UI preview only
        /// </summary>
        /// <param name="imageData16bit">16-bit image data (ushort[,])</param>
        /// <param name="maxPreviewWidth">Maximum preview width (default: 800px)</param>
        /// <param name="maxPreviewHeight">Maximum preview height (default: 600px)</param>
        /// <param name="autoStretch">Apply auto histogram stretch (default: true)</param>
        /// <returns>8-bit BitmapImage suitable for WPF display</returns>
        public static BitmapImage CreatePreviewImage(
            ushort[,] imageData16bit,
            int maxPreviewWidth = 800,
            int maxPreviewHeight = 600,
            bool autoStretch = true)
        {
            if (imageData16bit == null)
                throw new ArgumentNullException(nameof(imageData16bit));

            try
            {
                int fullHeight = imageData16bit.GetLength(0);
                int fullWidth = imageData16bit.GetLength(1);

                Debug.WriteLine($"[ImagePreview] Creating preview from {fullWidth}x{fullHeight} (16-bit)");

                // Check if already at or below target size (from stride sampling)
                bool alreadySmall = (fullWidth <= maxPreviewWidth && fullHeight <= maxPreviewHeight);
                
                ushort[,] downsampledData;
                int previewWidth, previewHeight;
                
                if (alreadySmall)
                {
                    // Already small (from stride sampling) - skip downsampling!
                    downsampledData = imageData16bit;
                    previewWidth = fullWidth;
                    previewHeight = fullHeight;
                    Debug.WriteLine($"[ImagePreview] ? Already small ({fullWidth}x{fullHeight}) - skipping downsample");
                }
                else
                {
                    // Step 1: Calculate preview dimensions (maintain aspect ratio)
                    double aspectRatio = (double)fullWidth / fullHeight;

                    if (aspectRatio > 1.0)
                    {
                        // Landscape
                        previewWidth = Math.Min(fullWidth, maxPreviewWidth);
                        previewHeight = (int)(previewWidth / aspectRatio);
                    }
                    else
                    {
                        // Portrait
                        previewHeight = Math.Min(fullHeight, maxPreviewHeight);
                        previewWidth = (int)(previewHeight * aspectRatio);
                    }

                    // Step 2: Downsample image data
                    downsampledData = DownsampleImage(imageData16bit, fullWidth, fullHeight, previewWidth, previewHeight);
                    Debug.WriteLine($"[ImagePreview] Downsampled to {previewWidth}x{previewHeight}");
                }

                // Step 3: Apply histogram stretch if enabled
                byte[] previewData8bit;
                if (autoStretch)
                {
                    previewData8bit = ApplyAutoStretchTo8Bit(downsampledData, previewWidth, previewHeight);
                }
                else
                {
                    previewData8bit = ConvertTo8Bit(downsampledData);
                }

                // Step 4: Create BitmapSource (8-bit grayscale)
                var bitmapSource = BitmapSource.Create(
                    previewWidth, previewHeight,
                    96, 96,
                    PixelFormats.Gray8,
                    null,
                    previewData8bit,
                    previewWidth);

                bitmapSource.Freeze();

                // Step 5: Convert to BitmapImage (for WPF Image control)
                var bitmapImage = ConvertToBitmapImage(bitmapSource);

                Debug.WriteLine($"[ImagePreview] ? Preview created: {previewWidth}x{previewHeight} (8-bit)");
                Debug.WriteLine($"[ImagePreview] Memory saved: {CalculateMemorySaved(fullWidth, fullHeight, previewWidth, previewHeight):F1} MB");

                return bitmapImage;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImagePreview ERROR] Failed to create preview: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downsample 16-bit image using area averaging (ANTI-ALIASING)
        /// Averages multiple source pixels for each destination pixel
        /// Eliminates vertical lines/banding artifacts from point sampling
        /// </summary>
        private static ushort[,] DownsampleImage(ushort[,] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
        {
            var result = new ushort[dstHeight, dstWidth];

            double scaleX = (double)srcWidth / dstWidth;
            double scaleY = (double)srcHeight / dstHeight;

            // Use parallel processing for faster downsampling
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
                        for (int srcX = srcXMin; srcX < srcXMax; srcX++)
                        {
                            sum += source[srcY, srcX];
                            count++;
                        }
                    }

                    result[y, x] = count > 0 ? (ushort)(sum / count) : (ushort)0;
                }
            });

            return result;
        }

        /// <summary>
        /// Apply auto histogram stretch and convert to 8-bit (SUPER OPTIMIZED - Approximate Percentile)
        /// Uses histogram bins instead of sorting (much faster!)
        /// </summary>
        private static byte[] ApplyAutoStretchTo8Bit(ushort[,] data16bit, int width, int height)
        {
            int pixelCount = width * height;
            byte[] result = new byte[pixelCount];

            // [NEW] Find actual min/max values (no binning - full 16-bit precision!)
            ushort globalMin = ushort.MaxValue;
            ushort globalMax = ushort.MinValue;
            
            // Parallel scan for min/max
            object lockObj = new object();
            System.Threading.Tasks.Parallel.For(0, height, () => new { min = ushort.MaxValue, max = ushort.MinValue },
                (y, loop, local) =>
                {
                    ushort localMin = local.min;
                    ushort localMax = local.max;
                    
                    for (int x = 0; x < width; x++)
                    {
                        ushort value = data16bit[y, x];
                        if (value < localMin) localMin = value;
                        if (value > localMax) localMax = value;
                    }
                    
                    return new { min = localMin, max = localMax };
                },
                local =>
                {
                    lock (lockObj)
                    {
                        if (local.min < globalMin) globalMin = local.min;
                        if (local.max > globalMax) globalMax = local.max;
                    }
                });

            // Use direct min/max (no binning!)
            int blackPoint = globalMin;
            int whitePoint = globalMax;

            // Ensure valid range
            if (whitePoint <= blackPoint)
                whitePoint = blackPoint + 1000;

            double scale = 255.0 / (whitePoint - blackPoint);

            // Apply stretch in parallel (flatten + stretch in one pass)
            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    ushort value = data16bit[y, x];

                    if (value <= blackPoint)
                        result[index] = 0;
                    else if (value >= whitePoint)
                        result[index] = 255;
                    else
                        result[index] = (byte)((value - blackPoint) * scale);
                }
            });

            Debug.WriteLine($"[ImagePreview] Histogram stretch (DIRECT min/max): {blackPoint} - {whitePoint} ? 0 - 255");

            return result;
        }

        /// <summary>
        /// Simple 16-bit to 8-bit conversion (no stretch)
        /// Just takes high byte
        /// </summary>
        private static byte[] ConvertTo8Bit(ushort[,] data16bit)
        {
            int height = data16bit.GetLength(0);
            int width = data16bit.GetLength(1);
            byte[] result = new byte[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Scale 16-bit (0-65535) to 8-bit (0-255)
                    result[y * width + x] = (byte)(data16bit[y, x] >> 8);
                }
            }

            return result;
        }

        /// <summary>
        /// Convert BitmapSource to BitmapImage (for WPF)
        /// </summary>
        private static BitmapImage ConvertToBitmapImage(BitmapSource bitmapSource)
        {
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
        /// Calculate memory saved by using preview vs full image
        /// </summary>
        private static double CalculateMemorySaved(int fullWidth, int fullHeight, int previewWidth, int previewHeight)
        {
            // Full image: 16-bit (2 bytes per pixel)
            double fullSizeMB = (fullWidth * fullHeight * 2) / (1024.0 * 1024.0);

            // Preview image: 8-bit (1 byte per pixel)
            double previewSizeMB = (previewWidth * previewHeight * 1) / (1024.0 * 1024.0);

            return fullSizeMB - previewSizeMB;
        }
    }
}
