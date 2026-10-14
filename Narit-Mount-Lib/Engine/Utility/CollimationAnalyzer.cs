using System;
using System.Collections.Generic;
using System.Linq;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Collimation analysis result
    /// </summary>
    public class CollimationResult
    {
        public CollimationQuality Quality { get; set; }
        public double AverageFWHM { get; set; }
        public double AverageEccentricity { get; set; }
        public double AverageRoundness { get; set; }
        public int StarsAnalyzed { get; set; }
        public bool HasSystematicError { get; set; }
        public string DetailedMessage { get; set; }
        public List<StarAnalysis> StarResults { get; set; }

        /// <summary>
        /// Collimation score as percentage (0-100%).
        /// 100% = perfectly round stars, 0% = severely miscollimated.
        /// Weighted: 60% roundness + 25% FWHM quality + 15% consistency.
        /// </summary>
        public double CollimationScorePercent
        {
            get
            {
                // Roundness component (60%): 1.0 = 100%, 0.5 = 0%
                double roundnessScore = Math.Max(0, Math.Min(1, (AverageRoundness - 0.5) / 0.5)) * 100;

                // FWHM component (25%): < 1.5" = 100%, > 6" = 0%
                double fwhmScore = Math.Max(0, Math.Min(1, (6.0 - AverageFWHM) / 4.5)) * 100;

                // Eccentricity component (15%): 0 = 100%, 0.5 = 0%
                double eccentricityScore = Math.Max(0, Math.Min(1, (0.5 - AverageEccentricity) / 0.5)) * 100;

                return roundnessScore * 0.60 + fwhmScore * 0.25 + eccentricityScore * 0.15;
            }
        }

        /// <summary>
        /// Average elongation angle of stars (degrees) — indicates which direction
        /// the collimation is off. Only meaningful when eccentricity > 0.1.
        /// </summary>
        public double AverageElongationAngle
        {
            get
            {
                if (StarResults == null || StarResults.Count == 0) return 0;
                var elongated = StarResults.Where(s => s.Eccentricity > 0.1).ToList();
                if (elongated.Count == 0) return 0;
                return elongated.Average(s => s.ElongationAngle_Deg);
            }
        }
    }

    /// <summary>
    /// Quality level of collimation
    /// </summary>
    public enum CollimationQuality
    {
        Excellent,   // FWHM < 2.0", roundness > 0.95
        Good,        // FWHM < 3.0", roundness > 0.90
        Fair,        // FWHM < 4.0", roundness > 0.80
        Poor,        // FWHM >= 4.0" or roundness < 0.80
        NeedsAdjustment  // Systematic aberration detected
    }

    /// <summary>
    /// Individual star analysis result
    /// </summary>
    public class StarAnalysis
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double FWHM_Pixels { get; set; }
        public double FWHM_Arcsec { get; set; }
        public double Eccentricity { get; set; }
        public double Roundness { get; set; }
        public double PeakIntensity { get; set; }
        public double TotalFlux { get; set; }
        public double ElongationAngle_Deg { get; set; }
    }

    /// <summary>
    /// Analyzes star images to detect collimation quality
    /// Uses FWHM, eccentricity, and roundness metrics
    /// </summary>
    public class CollimationAnalyzer
    {
        private double _plateScale_ArcsecPerPixel;

        public CollimationAnalyzer(double plateScale_ArcsecPerPixel)
        {
            _plateScale_ArcsecPerPixel = plateScale_ArcsecPerPixel;
        }

        /// <summary>
        /// Analyze image for collimation quality
        /// </summary>
        public CollimationResult AnalyzeImage(byte[] rawData, int width, int height, bool isRAW16)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[CollimationAnalyzer] Starting analysis...");
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Image: {width}x{height}, RAW16={isRAW16}");

                // 1. Convert to 16-bit if needed
                ushort[] imageData = ConvertToUInt16(rawData, width, height, isRAW16);

                // 2. Detect stars
                var stars = DetectStars(imageData, width, height);
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Detected {stars.Count} stars");

                if (stars.Count == 0)
                {
                    return new CollimationResult
                    {
                        Quality = CollimationQuality.Poor,
                        DetailedMessage = "No stars detected - image may be out of focus or overexposed",
                        StarsAnalyzed = 0,
                        StarResults = new List<StarAnalysis>()
                    };
                }

                // 3. Analyze each star
                var starAnalyses = new List<StarAnalysis>();
                foreach (var star in stars.Take(20)) // Analyze top 20 brightest stars
                {
                    var analysis = AnalyzeStar(imageData, width, height, star.X, star.Y);
                    if (analysis != null)
                    {
                        starAnalyses.Add(analysis);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Successfully analyzed {starAnalyses.Count} stars");

                if (starAnalyses.Count == 0)
                {
                    return new CollimationResult
                    {
                        Quality = CollimationQuality.Poor,
                        DetailedMessage = "Failed to analyze any stars - check focus and exposure",
                        StarsAnalyzed = 0,
                        StarResults = new List<StarAnalysis>()
                    };
                }

                // 4. Calculate average metrics
                double avgFWHM = starAnalyses.Average(s => s.FWHM_Arcsec);
                double avgEccentricity = starAnalyses.Average(s => s.Eccentricity);
                double avgRoundness = starAnalyses.Average(s => s.Roundness);

                // 5. Check for systematic errors (all stars elongated in same direction)
                bool hasSystematicError = DetectSystematicAberration(starAnalyses);

                // 6. Determine quality
                var quality = DetermineQuality(avgFWHM, avgRoundness, avgEccentricity, hasSystematicError);
                string message = GenerateDetailedMessage(quality, avgFWHM, avgRoundness, avgEccentricity, hasSystematicError);

                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer] ? Analysis complete:");
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Quality: {quality}");
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Avg FWHM: {avgFWHM:F2}\"");
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Avg Roundness: {avgRoundness:F3}");
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Avg Eccentricity: {avgEccentricity:F3}");

                return new CollimationResult
                {
                    Quality = quality,
                    AverageFWHM = avgFWHM,
                    AverageEccentricity = avgEccentricity,
                    AverageRoundness = avgRoundness,
                    StarsAnalyzed = starAnalyses.Count,
                    HasSystematicError = hasSystematicError,
                    DetailedMessage = message,
                    StarResults = starAnalyses
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer] ? Error: {ex.Message}");
                return new CollimationResult
                {
                    Quality = CollimationQuality.Poor,
                    DetailedMessage = $"Analysis failed: {ex.Message}",
                    StarsAnalyzed = 0,
                    StarResults = new List<StarAnalysis>()
                };
            }
        }

        /// <summary>
        /// Convert raw data to UInt16 array
        /// </summary>
        private ushort[] ConvertToUInt16(byte[] rawData, int width, int height, bool isRAW16)
        {
            int pixelCount = width * height;
            ushort[] result = new ushort[pixelCount];

            if (isRAW16)
            {
                // RAW16: 2 bytes per pixel
                for (int i = 0; i < pixelCount; i++)
                {
                    result[i] = BitConverter.ToUInt16(rawData, i * 2);
                }
            }
            else
            {
                // RAW8: 1 byte per pixel, scale to 16-bit
                for (int i = 0; i < pixelCount; i++)
                {
                    result[i] = (ushort)(rawData[i] * 257); // 0-255 -> 0-65535
                }
            }

            return result;
        }

        /// <summary>
        /// Detect bright stars in image.
        /// Uses sampled statistics (median + robust sigma) instead of sorting the
        /// entire 25M-pixel array, which previously allocated a 50 MB sorted copy
        /// and was extremely slow.
        /// </summary>
        private List<(int X, int Y, double Intensity)> DetectStars(ushort[] imageData, int width, int height)
        {
            var stars = new List<(int X, int Y, double Intensity)>();

            // Calculate noise level using SAMPLED pixels (fast, no 50MB sort)
            // Sample every 37th pixel (~700K samples from 25M — more than enough for statistics)
            int step = 37;
            int sampleCount = (imageData.Length + step - 1) / step;
            var samples = new ushort[sampleCount];
            for (int i = 0, j = 0; i < imageData.Length && j < sampleCount; i += step, j++)
            {
                samples[j] = imageData[i];
            }
            Array.Sort(samples);
            ushort median = samples[sampleCount / 2];

            // Calculate MAD (Median Absolute Deviation) for robust noise estimate
            var absDeviations = new ushort[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int dev = Math.Abs(samples[i] - median);
                absDeviations[i] = (ushort)Math.Min(dev, 65535);
            }
            Array.Sort(absDeviations);
            double mad = absDeviations[sampleCount / 2];
            double sigma = mad * 1.4826; // MAD ? standard deviation estimate

            // Threshold = median + 5? (catches faint stars while rejecting noise)
            // Minimum 200 ADU above median to avoid hot pixels on very short exposures
            int dynamicOffset = (int)Math.Max(200, sigma * 5.0);
            int thresholdInt = Math.Min(median + dynamicOffset, 65535);
            ushort threshold = (ushort)thresholdInt;

            System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer]   Median: {median}, Sigma: {sigma:F1}, Threshold: {threshold} (median + {dynamicOffset})");

            // Find local maxima (simple peak detection)
            int searchRadius = 10; // pixels
            for (int y = searchRadius; y < height - searchRadius; y += searchRadius)
            {
                for (int x = searchRadius; x < width - searchRadius; x += searchRadius)
                {
                    int idx = y * width + x;
                    if (imageData[idx] > threshold)
                    {
                        // Check if it's a local maximum
                        bool isMax = true;
                        for (int dy = -searchRadius; dy <= searchRadius && isMax; dy++)
                        {
                            for (int dx = -searchRadius; dx <= searchRadius && isMax; dx++)
                            {
                                int neighborIdx = (y + dy) * width + (x + dx);
                                if (imageData[neighborIdx] > imageData[idx])
                                {
                                    isMax = false;
                                }
                            }
                        }

                        if (isMax)
                        {
                            stars.Add((x, y, imageData[idx]));
                        }
                    }
                }
            }

            // Sort by intensity (brightest first)
            stars = stars.OrderByDescending(s => s.Intensity).ToList();

            return stars;
        }

        /// <summary>
        /// Analyze individual star
        /// </summary>
        private StarAnalysis AnalyzeStar(ushort[] imageData, int width, int height, int centerX, int centerY)
        {
            try
            {
                int radius = 20; // Analysis window radius

                // Extract star region
                double[,] starRegion = new double[radius * 2 + 1, radius * 2 + 1];
                double maxValue = 0;

                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int x = centerX + dx;
                        int y = centerY + dy;

                        if (x >= 0 && x < width && y >= 0 && y < height)
                        {
                            double value = imageData[y * width + x];
                            starRegion[dy + radius, dx + radius] = value;
                            if (value > maxValue) maxValue = value;
                        }
                    }
                }

                // Calculate background (median of edge pixels)
                var edgePixels = new List<double>();
                for (int i = 0; i < radius * 2 + 1; i++)
                {
                    edgePixels.Add(starRegion[0, i]);
                    edgePixels.Add(starRegion[radius * 2, i]);
                    edgePixels.Add(starRegion[i, 0]);
                    edgePixels.Add(starRegion[i, radius * 2]);
                }
                double background = edgePixels.OrderBy(x => x).ElementAt(edgePixels.Count / 2);

                // Subtract background
                for (int y = 0; y <= radius * 2; y++)
                {
                    for (int x = 0; x <= radius * 2; x++)
                    {
                        starRegion[y, x] = Math.Max(0, starRegion[y, x] - background);
                    }
                }

                // Calculate FWHM (Full Width Half Maximum)
                double fwhm_pixels = CalculateFWHM(starRegion, radius);
                double fwhm_arcsec = fwhm_pixels * _plateScale_ArcsecPerPixel;

                // Calculate moments for shape analysis
                var moments = CalculateMoments(starRegion, radius);

                // Calculate eccentricity and roundness
                double eccentricity = CalculateEccentricity(moments);
                double roundness = 1.0 - eccentricity; // Simple approximation

                // Calculate elongation angle
                double elongationAngle = CalculateElongationAngle(moments);

                // Calculate total flux
                double totalFlux = 0;
                for (int y = 0; y <= radius * 2; y++)
                {
                    for (int x = 0; x <= radius * 2; x++)
                    {
                        totalFlux += starRegion[y, x];
                    }
                }

                return new StarAnalysis
                {
                    X = centerX,
                    Y = centerY,
                    FWHM_Pixels = fwhm_pixels,
                    FWHM_Arcsec = fwhm_arcsec,
                    Eccentricity = eccentricity,
                    Roundness = roundness,
                    PeakIntensity = maxValue - background,
                    TotalFlux = totalFlux,
                    ElongationAngle_Deg = elongationAngle
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CollimationAnalyzer] Error analyzing star at ({centerX},{centerY}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate FWHM using radial profile
        /// </summary>
        private double CalculateFWHM(double[,] starRegion, int radius)
        {
            // Find peak value
            double peakValue = 0;
            for (int y = 0; y <= radius * 2; y++)
            {
                for (int x = 0; x <= radius * 2; x++)
                {
                    if (starRegion[y, x] > peakValue)
                        peakValue = starRegion[y, x];
                }
            }

            double halfMax = peakValue / 2.0;

            // Measure width at half maximum (horizontal and vertical)
            double fwhmX = MeasureFWHM_1D(starRegion, radius, true, halfMax);
            double fwhmY = MeasureFWHM_1D(starRegion, radius, false, halfMax);

            // Average of both directions
            return (fwhmX + fwhmY) / 2.0;
        }

        private double MeasureFWHM_1D(double[,] starRegion, int radius, bool horizontal, double halfMax)
        {
            int center = radius;
            double leftPos = center, rightPos = center;

            if (horizontal)
            {
                // Scan left
                for (int x = center; x >= 0; x--)
                {
                    if (starRegion[center, x] < halfMax)
                    {
                        leftPos = x + 1;
                        break;
                    }
                }

                // Scan right
                for (int x = center; x <= radius * 2; x++)
                {
                    if (starRegion[center, x] < halfMax)
                    {
                        rightPos = x - 1;
                        break;
                    }
                }
            }
            else
            {
                // Scan up
                for (int y = center; y >= 0; y--)
                {
                    if (starRegion[y, center] < halfMax)
                    {
                        leftPos = y + 1;
                        break;
                    }
                }

                // Scan down
                for (int y = center; y <= radius * 2; y++)
                {
                    if (starRegion[y, center] < halfMax)
                    {
                        rightPos = y - 1;
                        break;
                    }
                }
            }

            return rightPos - leftPos;
        }

        /// <summary>
        /// Calculate image moments for shape analysis
        /// </summary>
        private (double M00, double M10, double M01, double M20, double M02, double M11) CalculateMoments(double[,] starRegion, int radius)
        {
            double M00 = 0, M10 = 0, M01 = 0, M20 = 0, M02 = 0, M11 = 0;

            for (int y = 0; y <= radius * 2; y++)
            {
                for (int x = 0; x <= radius * 2; x++)
                {
                    double value = starRegion[y, x];
                    M00 += value;
                    M10 += x * value;
                    M01 += y * value;
                    M20 += x * x * value;
                    M02 += y * y * value;
                    M11 += x * y * value;
                }
            }

            return (M00, M10, M01, M20, M02, M11);
        }

        /// <summary>
        /// Calculate eccentricity from moments
        /// </summary>
        private double CalculateEccentricity((double M00, double M10, double M01, double M20, double M02, double M11) moments)
        {
            if (moments.M00 == 0) return 0;

            // Calculate central moments
            double xc = moments.M10 / moments.M00;
            double yc = moments.M01 / moments.M00;

            double mu20 = moments.M20 / moments.M00 - xc * xc;
            double mu02 = moments.M02 / moments.M00 - yc * yc;
            double mu11 = moments.M11 / moments.M00 - xc * yc;

            // Calculate eigenvalues (semi-major and semi-minor axes)
            double a = (mu20 + mu02) / 2.0;
            double b = Math.Sqrt(4 * mu11 * mu11 + (mu20 - mu02) * (mu20 - mu02)) / 2.0;

            double lambda1 = a + b;
            double lambda2 = a - b;

            if (lambda1 <= 0) return 0;

            // Eccentricity = sqrt(1 - (b^2 / a^2))
            double eccentricity = Math.Sqrt(1.0 - Math.Max(0, lambda2 / lambda1));
            return Math.Min(1.0, eccentricity);
        }

        /// <summary>
        /// Calculate elongation angle (in degrees)
        /// </summary>
        private double CalculateElongationAngle((double M00, double M10, double M01, double M20, double M02, double M11) moments)
        {
            if (moments.M00 == 0) return 0;

            double xc = moments.M10 / moments.M00;
            double yc = moments.M01 / moments.M00;

            double mu20 = moments.M20 / moments.M00 - xc * xc;
            double mu02 = moments.M02 / moments.M00 - yc * yc;
            double mu11 = moments.M11 / moments.M00 - xc * yc;

            // Angle of major axis
            double angle = 0.5 * Math.Atan2(2 * mu11, mu20 - mu02);
            return angle * 180.0 / Math.PI; // Convert to degrees
        }

        /// <summary>
        /// Detect systematic aberration (all stars elongated in same direction)
        /// </summary>
        private bool DetectSystematicAberration(List<StarAnalysis> stars)
        {
            if (stars.Count < 3) return false;

            // Check if most stars have high eccentricity
            int highEccentricityCount = stars.Count(s => s.Eccentricity > 0.3);
            if (highEccentricityCount < stars.Count * 0.6) return false;

            // Check if elongation angles are similar (within 30 degrees)
            var angles = stars.Where(s => s.Eccentricity > 0.3).Select(s => s.ElongationAngle_Deg).ToList();
            if (angles.Count < 2) return false;

            double avgAngle = angles.Average();
            int similarAngleCount = angles.Count(a => Math.Abs(a - avgAngle) < 30);

            return similarAngleCount >= angles.Count * 0.7;
        }

        /// <summary>
        /// Determine collimation quality based on metrics
        /// </summary>
        private CollimationQuality DetermineQuality(double avgFWHM, double avgRoundness, double avgEccentricity, bool hasSystematicError)
        {
            if (hasSystematicError)
                return CollimationQuality.NeedsAdjustment;

            if (avgFWHM < 2.0 && avgRoundness > 0.95)
                return CollimationQuality.Excellent;

            if (avgFWHM < 3.0 && avgRoundness > 0.90)
                return CollimationQuality.Good;

            if (avgFWHM < 4.0 && avgRoundness > 0.80)
                return CollimationQuality.Fair;

            return CollimationQuality.Poor;
        }

        /// <summary>
        /// Generate detailed message based on analysis
        /// </summary>
        private string GenerateDetailedMessage(CollimationQuality quality, double avgFWHM, double avgRoundness, double avgEccentricity, bool hasSystematicError)
        {
            switch (quality)
            {
                case CollimationQuality.Excellent:
                    return $"? Excellent collimation! Stars are sharp and round.\nFWHM: {avgFWHM:F2}\", Roundness: {avgRoundness:F3}";

                case CollimationQuality.Good:
                    return $"? Good collimation. Minor improvements possible.\nFWHM: {avgFWHM:F2}\", Roundness: {avgRoundness:F3}";

                case CollimationQuality.Fair:
                    return $"? Fair collimation. Consider adjustment.\nFWHM: {avgFWHM:F2}\", Roundness: {avgRoundness:F3}";

                case CollimationQuality.NeedsAdjustment:
                    return $"? Systematic aberration detected!\nAll stars show similar elongation pattern.\nCollimation adjustment needed.\nFWHM: {avgFWHM:F2}\", Eccentricity: {avgEccentricity:F3}";

                case CollimationQuality.Poor:
                default:
                    return $"? Poor collimation or focus.\nStars are not round or sharp.\nFWHM: {avgFWHM:F2}\", Roundness: {avgRoundness:F3}";
            }
        }
    }
}
