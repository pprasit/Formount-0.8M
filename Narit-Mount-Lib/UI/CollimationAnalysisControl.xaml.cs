using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// User Control for automatic collimation analysis
    /// Analyzes captured images to detect collimation quality
    /// Supports series imaging and comparison with previous results
    /// </summary>
    public partial class CollimationAnalysisControl : UserControl
    {
        // Reference to parent window for capturing images
        private ImageVerificationAndOffset _parentWindow;
        
        private double _plateScale_ArcsecPerPixel;
        private bool _isAnalyzing = false;

        // Previous session results for comparison
        private CollimationSessionResult _previousSession;
        private CollimationSessionResult _currentSession;

        public CollimationAnalysisControl()
        {
            InitializeComponent();

            // Load plate scale from settings (default: 1.0 arcsec/pixel)
            LoadPlateScaleFromSettings();

            // Wire Loaded event to ensure parent is set after XAML initialization
            this.Loaded += OnControlLoaded;
        }

        /// <summary>
        /// Called when control is fully loaded and parent should be available
        /// </summary>
        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine($"[CollimationAnalysis] Control loaded, _parentWindow={(_parentWindow != null ? "SET" : "NULL")}");
        }

        /// <summary>
        /// Load plate scale from settings
        /// </summary>
        private void LoadPlateScaleFromSettings()
        {
            try
            {
                var cameraSettings = AppSettings.GetCameraSettings();
                _plateScale_ArcsecPerPixel = cameraSettings.PlateScale_ArcsecPerPixel;

                if (_plateScale_ArcsecPerPixel <= 0)
                {
                    _plateScale_ArcsecPerPixel = 1.0;
                }

                Debug.WriteLine($"[CollimationAnalysis] Loaded plate scale: {_plateScale_ArcsecPerPixel:F3} arcsec/pixel");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CollimationAnalysis] Error loading plate scale: {ex.Message}");
                _plateScale_ArcsecPerPixel = 1.0; // Fallback
            }
        }

        /// <summary>
        /// Set reference to parent window (called by ImageVerificationAndOffset)
        /// </summary>
        public void SetParentWindow(ImageVerificationAndOffset parent)
        {
            _parentWindow = parent;
            Debug.WriteLine($"[CollimationAnalysis] ? Parent window SET: {parent?.GetType().Name ?? "NULL"}");
        }

        /// <summary>
        /// Analyze button click - always capture new series
        /// Simple workflow: Capture ? Analyze ? Display
        /// </summary>
        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[CollimationAnalysis] ========================================");
            Debug.WriteLine("[CollimationAnalysis] ?? BtnAnalyze_Click triggered");
            Debug.WriteLine("[CollimationAnalysis] ========================================");

            // Validate parent window
            if (_parentWindow == null)
            {
                Debug.WriteLine("[CollimationAnalysis] ? ERROR: _parentWindow is NULL!");
                MessageBox.Show("Error: Parent window not set!\n\nPlease restart the application.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                txtInfo.Visibility = Visibility.Visible;
                txtInfo.Text = "? ERROR: Parent window not set. Please restart.";
                txtInfo.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                return;
            }

            Debug.WriteLine($"[CollimationAnalysis] ? _parentWindow is SET: {_parentWindow.GetType().Name}");

            // Validate sample count
            if (!int.TryParse(txtSampleCount.Text, out int sampleCount) || sampleCount < 1 || sampleCount > 20)
            {
                MessageBox.Show("Please enter a valid sample count (1-20).\n\nRecommended: 5-10 samples",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_isAnalyzing)
            {
                Debug.WriteLine("[CollimationAnalysis] Analysis already in progress");
                return;
            }

            try
            {
                _isAnalyzing = true;

                // Show loading indicator
                loadingPanel.Visibility = Visibility.Visible;
                resultsPanel.Visibility = Visibility.Collapsed;
                comparisonPanel.Visibility = Visibility.Collapsed;
                txtInfo.Visibility = Visibility.Collapsed;
                btnAnalyze.IsEnabled = false;
                txtSampleCount.IsEnabled = false;

                Debug.WriteLine($"[CollimationAnalysis] Starting NEW series capture: {sampleCount} samples");

                // Always capture fresh series
                var sessionResult = await CaptureAndAnalyzeSeries(sampleCount);

                if (sessionResult != null && sessionResult.Results.Count > 0)
                {
                    // Store current session
                    _currentSession = sessionResult;

                    // Display results with comparison to previous
                    DisplayResults(sessionResult, _previousSession);

                    // Update previous session for next comparison
                    _previousSession = sessionResult;

                    Debug.WriteLine($"[CollimationAnalysis] ? Analysis complete: Quality={sessionResult.AverageQuality}");

                    // Open Collimation Result Window with score and diagram
                    var combinedResult = sessionResult.Results.FirstOrDefault();
                    if (combinedResult != null)
                    {
                        var resultWindow = new CollimationResultWindow(combinedResult, sessionResult);
                        resultWindow.Owner = Window.GetWindow(this);
                        resultWindow.Show();
                    }
                }
                else
                {
                    Debug.WriteLine("[CollimationAnalysis] ? Analysis failed - no valid results");
                    
                    // Hide loading, show info message
                    loadingPanel.Visibility = Visibility.Collapsed;
                    txtInfo.Visibility = Visibility.Visible;
                    txtInfo.Text = "? Analysis failed - no stars detected.\n\n" +
                                   "Possible causes:\n" +
                                   "� Out of focus\n" +
                                   "� Exposure too short/long\n" +
                                   "� No bright stars in field\n\n" +
                                   "Adjust settings and try again.";
                    txtInfo.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CollimationAnalysis] ? Error during analysis: {ex.Message}");
                
                loadingPanel.Visibility = Visibility.Collapsed;
                txtInfo.Visibility = Visibility.Visible;
                txtInfo.Text = $"? Analysis error:\n{ex.Message}";
                txtInfo.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
            finally
            {
                _isAnalyzing = false;
                loadingPanel.Visibility = Visibility.Collapsed;
                btnAnalyze.IsEnabled = true;
                txtSampleCount.IsEnabled = true;
            }
        }

        /// <summary>
        /// Capture NEW series and analyze
        /// Captures N images, displays each one, creates a combined (stacked) image,
        /// and analyzes the combined image for collimation quality.
        /// </summary>
        private async Task<CollimationSessionResult> CaptureAndAnalyzeSeries(int sampleCount)
        {
            var seriesId = Guid.NewGuid();

            // Update progress
            Dispatcher.Invoke(() =>
            {
                progressBar.Maximum = sampleCount + 1;  // +1 for stacking step
                progressBar.Value = 0;
                txtProgress.Text = $"Starting series capture ({sampleCount} images)...";
            });

            // Capture series (this will display each image as captured)
            var images = await _parentWindow.CaptureImageSeriesForAnalysis(sampleCount, seriesId);

            Debug.WriteLine($"[CollimationAnalysis] Series captured: {images.Count}/{sampleCount} images");

            if (images.Count == 0)
            {
                Debug.WriteLine("[CollimationAnalysis] No images captured");
                return null;
            }

            // Update progress - stacking
            Dispatcher.Invoke(() =>
            {
                progressBar.Value = sampleCount;
                txtProgress.Text = "Creating combined image (stacking)...";
            });

            // Create stacked/combined image
            var combinedImage = await Task.Run(() => CreateStackedImage(images));

            if (combinedImage != null)
            {
                Debug.WriteLine($"[CollimationAnalysis] Combined image created: {combinedImage.Width}x{combinedImage.Height}");

                // Display and add combined image to thumbnail gallery
                BitmapSource stretchedImage = null;
                await _parentWindow.Dispatcher.InvokeAsync(async () =>
                {
                    // Store raw data for stretching
                    _parentWindow.SetLastRawData(combinedImage.ImageData, combinedImage.Width, combinedImage.Height,
                        combinedImage.ImageType == Engine.Camera.ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16);

                    // Apply current stretch profile and get stretched image
                    stretchedImage = await _parentWindow.RestretchAndDisplayPublic();

                    // Add combined image to thumbnail gallery (marked as Combined)
                    await _parentWindow.AddCombinedImageToThumbnail(combinedImage, seriesId, sampleCount, stretchedImage);
                });

                Debug.WriteLine("[CollimationAnalysis] Combined image displayed and added to gallery");
            }

            // Update progress - analyzing
            Dispatcher.Invoke(() =>
            {
                progressBar.Value = sampleCount + 1;
                txtProgress.Text = "Analyzing combined image...";
            });

            // Analyze ONLY the combined image
            var result = await Task.Run(() => combinedImage != null ? AnalyzeImage(combinedImage) : null);

            var results = new List<CollimationResult>();
            if (result != null && result.StarsAnalyzed > 0)
            {
                results.Add(result);
                Debug.WriteLine($"[CollimationAnalysis] Combined image analyzed: FWHM={result.AverageFWHM:F2}\", Quality={result.Quality}");

                // Draw star annotations on image
                await _parentWindow.Dispatcher.InvokeAsync(() =>
                {
                    _parentWindow.DrawStarAnnotations(result);
                });
            }
            else
            {
                Debug.WriteLine("[CollimationAnalysis] Combined image analysis failed");
            }

            // Update progress - complete
            Dispatcher.Invoke(() =>
            {
                txtProgress.Text = results.Count > 0
                    ? $"Complete! Analyzed combined image ({sampleCount} stacked)"
                    : "Analysis failed - no stars detected";
            });

            if (results.Count == 0)
            {
                Debug.WriteLine("[CollimationAnalysis] No valid results from combined image");
                return null;
            }

            // Return session with single result from combined image
            return new CollimationSessionResult
            {
                Results = results,
                AverageFWHM = result.AverageFWHM,
                AverageRoundness = result.AverageRoundness,
                AverageEccentricity = result.AverageEccentricity,
                StdDevFWHM = 0,  // Single image - no deviation
                StdDevRoundness = 0,
                TotalStars = result.StarsAnalyzed,
                SampleCount = sampleCount,  // Track how many images were stacked
                AverageQuality = result.Quality,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Create a stacked (averaged) image from multiple captures.
        /// Uses median stacking for better noise reduction and artifact rejection.
        /// </summary>
        private CameraImageData CreateStackedImage(List<CameraImageData> images)
        {
            if (images == null || images.Count == 0)
                return null;

            try
            {
                // Validate all images have same dimensions and type
                var first = images[0];
                bool allValid = images.All(img =>
                    img.Width == first.Width &&
                    img.Height == first.Height &&
                    img.ImageType == first.ImageType);

                if (!allValid)
                {
                    Debug.WriteLine("[CollimationAnalysis] Cannot stack - image dimensions/types don't match");
                    return null;
                }

                bool isRAW16 = first.ImageType == Engine.Camera.ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;
                int pixelCount = first.Width * first.Height;

                Debug.WriteLine($"[CollimationAnalysis] Stacking {images.Count} images ({first.Width}x{first.Height}, {(isRAW16 ? "RAW16" : "RAW8")})");

                byte[] stackedData;

                if (isRAW16)
                {
                    // RAW16: Use median stacking (better for outlier rejection)
                    stackedData = new byte[pixelCount * 2];
                    ushort[] medianBuffer = new ushort[images.Count];

                    for (int i = 0; i < pixelCount; i++)
                    {
                        // Collect pixel values from all images
                        for (int j = 0; j < images.Count; j++)
                        {
                            medianBuffer[j] = BitConverter.ToUInt16(images[j].ImageData, i * 2);
                        }

                        // Sort and take median
                        Array.Sort(medianBuffer);
                        ushort median = medianBuffer[images.Count / 2];

                        // Write to output
                        byte[] medianBytes = BitConverter.GetBytes(median);
                        stackedData[i * 2] = medianBytes[0];
                        stackedData[i * 2 + 1] = medianBytes[1];
                    }
                }
                else
                {
                    // RAW8: Use average stacking
                    stackedData = new byte[pixelCount];
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int sum = 0;
                        for (int j = 0; j < images.Count; j++)
                        {
                            sum += images[j].ImageData[i];
                        }
                        stackedData[i] = (byte)(sum / images.Count);
                    }
                }

                Debug.WriteLine("[CollimationAnalysis] Image stacking complete");

                // Create combined image data
                return new CameraImageData
                {
                    ImageData = stackedData,
                    Width = first.Width,
                    Height = first.Height,
                    ImageType = first.ImageType,
                    Binning = first.Binning,
                    ExposureTime_us = first.ExposureTime_us,
                    Gain = first.Gain,
                    Offset = first.Offset,
                    CaptureTime = DateTime.Now,
                    Temperature_C = images.Average(img => img.Temperature_C ?? 0)
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CollimationAnalysis] Image stacking error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate average statistics from multiple results
        /// </summary>
        private CollimationSessionResult CalculateSessionStatistics(List<CollimationResult> results)
        {
            if (results.Count == 0) return null;

            // Calculate averages
            double avgFWHM = results.Average(r => r.AverageFWHM);
            double avgRoundness = results.Average(r => r.AverageRoundness);
            double avgEccentricity = results.Average(r => r.AverageEccentricity);
            int totalStars = results.Sum(r => r.StarsAnalyzed);

            // Calculate standard deviations
            double stdDevFWHM = CalculateStdDev(results.Select(r => r.AverageFWHM).ToList());
            double stdDevRoundness = CalculateStdDev(results.Select(r => r.AverageRoundness).ToList());

            // Determine quality from average metrics
            bool hasSystematicError = results.Any(r => r.HasSystematicError);
            var quality = DetermineQuality(avgFWHM, avgRoundness, avgEccentricity, hasSystematicError);

            Debug.WriteLine($"[CollimationAnalysis] Session statistics:");
            Debug.WriteLine($"[CollimationAnalysis]   Samples: {results.Count}");
            Debug.WriteLine($"[CollimationAnalysis]   Avg FWHM: {avgFWHM:F2}\" � {stdDevFWHM:F2}\"");
            Debug.WriteLine($"[CollimationAnalysis]   Avg Roundness: {avgRoundness:F3} � {stdDevRoundness:F3}");
            Debug.WriteLine($"[CollimationAnalysis]   Quality: {quality}");

            return new CollimationSessionResult
            {
                Results = results,
                AverageFWHM = avgFWHM,
                AverageRoundness = avgRoundness,
                AverageEccentricity = avgEccentricity,
                StdDevFWHM = stdDevFWHM,
                StdDevRoundness = stdDevRoundness,
                TotalStars = totalStars,
                SampleCount = results.Count,
                AverageQuality = quality,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Calculate standard deviation
        /// </summary>
        private double CalculateStdDev(List<double> values)
        {
            if (values.Count <= 1) return 0;

            double avg = values.Average();
            double sumSquares = values.Sum(v => Math.Pow(v - avg, 2));
            return Math.Sqrt(sumSquares / (values.Count - 1));
        }

        /// <summary>
        /// Determine quality from metrics (reused from CollimationAnalyzer logic)
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
        /// Perform image analysis (single image)
        /// </summary>
        private CollimationResult AnalyzeImage(CameraImageData imageData)
        {
            Debug.WriteLine($"[CollimationAnalysis] Analyzing image: {imageData.Width}x{imageData.Height}");
            Debug.WriteLine($"[CollimationAnalysis]   Type: {imageData.ImageType}");
            Debug.WriteLine($"[CollimationAnalysis]   Binning: {imageData.Binning}");

            // Adjust plate scale for binning
            double effectivePlateScale = _plateScale_ArcsecPerPixel * imageData.Binning;
            Debug.WriteLine($"[CollimationAnalysis]   Effective plate scale: {effectivePlateScale:F3} arcsec/pixel (binning x{imageData.Binning})");

            // Create analyzer
            var analyzer = new CollimationAnalyzer(effectivePlateScale);

            // Determine if RAW16
            bool isRAW16 = imageData.ImageType == Engine.Camera.ZWOASICameraSDK.ASI_IMG_TYPE.ASI_IMG_RAW16;

            // Analyze
            var result = analyzer.AnalyzeImage(imageData.ImageData, imageData.Width, imageData.Height, isRAW16);

            return result;
        }

        /// <summary>
        /// Display session results with comparison to previous
        /// </summary>
        private void DisplayResults(CollimationSessionResult currentSession, CollimationSessionResult previousSession)
        {
            // Show results panel
            resultsPanel.Visibility = Visibility.Visible;
            txtInfo.Visibility = Visibility.Collapsed;

            // Update quality indicator
            txtQuality.Text = $"Quality: {currentSession.AverageQuality}";

            // Set color based on quality
            Color indicatorColor;
            Color textColor;

            switch (currentSession.AverageQuality)
            {
                case CollimationQuality.Excellent:
                    indicatorColor = Color.FromRgb(16, 185, 129);  // Green
                    textColor = Color.FromRgb(16, 185, 129);
                    break;

                case CollimationQuality.Good:
                    indicatorColor = Color.FromRgb(34, 197, 94);   // Light Green
                    textColor = Color.FromRgb(34, 197, 94);
                    break;

                case CollimationQuality.Fair:
                    indicatorColor = Color.FromRgb(251, 191, 36);  // Yellow
                    textColor = Color.FromRgb(251, 191, 36);
                    break;

                case CollimationQuality.NeedsAdjustment:
                    indicatorColor = Color.FromRgb(249, 115, 22);  // Orange
                    textColor = Color.FromRgb(249, 115, 22);
                    break;

                case CollimationQuality.Poor:
                default:
                    indicatorColor = Color.FromRgb(239, 68, 68);   // Red
                    textColor = Color.FromRgb(239, 68, 68);
                    break;
            }

            qualityIndicator.Fill = new SolidColorBrush(indicatorColor);
            txtQuality.Foreground = new SolidColorBrush(textColor);

            // Update metrics (with � StdDev)
            txtFWHM.Text = $"{currentSession.AverageFWHM:F2}\" � {currentSession.StdDevFWHM:F2}\"";
            txtRoundness.Text = $"{currentSession.AverageRoundness:F3} � {currentSession.StdDevRoundness:F3}";
            txtStarsCount.Text = $"{currentSession.TotalStars} ({currentSession.SampleCount} images)";
            txtStdDev.Text = $"{currentSession.StdDevFWHM:F2}\"";

            // Color code FWHM based on quality
            if (currentSession.AverageFWHM < 3.0)
                txtFWHM.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            else if (currentSession.AverageFWHM < 4.0)
                txtFWHM.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow
            else
                txtFWHM.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red

            // Color code Roundness
            if (currentSession.AverageRoundness > 0.90)
                txtRoundness.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            else if (currentSession.AverageRoundness > 0.80)
                txtRoundness.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow
            else
                txtRoundness.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red

            // Color code StdDev (lower is better)
            if (currentSession.StdDevFWHM < 0.3)
                txtStdDev.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green - excellent consistency
            else if (currentSession.StdDevFWHM < 0.5)
                txtStdDev.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Yellow - acceptable
            else
                txtStdDev.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red - poor consistency (seeing issues?)

            // Update detailed message
            txtDetailedMessage.Text = GenerateSessionMessage(currentSession);

            // Show comparison if previous session exists
            if (previousSession != null)
            {
                DisplayComparison(currentSession, previousSession);
            }
            else
            {
                comparisonPanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Display comparison with previous session
        /// </summary>
        private void DisplayComparison(CollimationSessionResult current, CollimationSessionResult previous)
        {
            comparisonPanel.Visibility = Visibility.Visible;

            // Calculate improvement percentage
            double fwhmChange = current.AverageFWHM - previous.AverageFWHM;
            double fwhmChangePercent = (fwhmChange / previous.AverageFWHM) * 100.0;

            double roundnessChange = current.AverageRoundness - previous.AverageRoundness;
            double roundnessChangePercent = (roundnessChange / previous.AverageRoundness) * 100.0;

            // Determine trend (FWHM: lower is better, Roundness: higher is better)
            bool fwhmImproved = fwhmChange < 0;
            bool roundnessImproved = roundnessChange > 0;

            string trendIcon;
            Color trendColor;
            string message;

            // Overall assessment (both metrics must improve for "Improved")
            if (fwhmImproved && roundnessImproved)
            {
                trendIcon = "?";
                trendColor = Color.FromRgb(16, 185, 129); // Green
                message = $"Improved! FWHM reduced by {Math.Abs(fwhmChangePercent):F1}%, Roundness +{roundnessChangePercent:F1}%";
            }
            else if (!fwhmImproved && !roundnessImproved)
            {
                trendIcon = "?";
                trendColor = Color.FromRgb(239, 68, 68); // Red
                message = $"Worse. FWHM increased by {Math.Abs(fwhmChangePercent):F1}%, Roundness {roundnessChangePercent:F1}%";
            }
            else
            {
                trendIcon = "?";
                trendColor = Color.FromRgb(251, 191, 36); // Yellow
                if (fwhmImproved)
                    message = $"Mixed. FWHM better ({fwhmChangePercent:F1}%), but Roundness worse ({roundnessChangePercent:F1}%)";
                else
                    message = $"Mixed. Roundness better (+{roundnessChangePercent:F1}%), but FWHM worse (+{Math.Abs(fwhmChangePercent):F1}%)";
            }

            txtTrendIcon.Text = trendIcon;
            txtTrendIcon.Foreground = new SolidColorBrush(trendColor);
            txtComparison.Text = message;
            txtComparison.Foreground = new SolidColorBrush(trendColor);

            Debug.WriteLine($"[CollimationAnalysis] Comparison:");
            Debug.WriteLine($"[CollimationAnalysis]   FWHM: {previous.AverageFWHM:F2}\" ? {current.AverageFWHM:F2}\" ({fwhmChangePercent:F1}%)");
            Debug.WriteLine($"[CollimationAnalysis]   Roundness: {previous.AverageRoundness:F3} ? {current.AverageRoundness:F3} ({roundnessChangePercent:F1}%)");
            Debug.WriteLine($"[CollimationAnalysis]   Trend: {(fwhmImproved && roundnessImproved ? "IMPROVED" : !fwhmImproved && !roundnessImproved ? "WORSE" : "MIXED")}");
        }

        /// <summary>
        /// Generate detailed message from session
        /// </summary>
        private string GenerateSessionMessage(CollimationSessionResult session)
        {
            string qualityMsg;
            switch (session.AverageQuality)
            {
                case CollimationQuality.Excellent:
                    qualityMsg = "? Excellent collimation! Stars are sharp and round.";
                    break;
                case CollimationQuality.Good:
                    qualityMsg = "? Good collimation. Minor improvements possible.";
                    break;
                case CollimationQuality.Fair:
                    qualityMsg = "? Fair collimation. Consider adjustment.";
                    break;
                case CollimationQuality.NeedsAdjustment:
                    qualityMsg = "? Systematic aberration detected! Collimation adjustment needed.";
                    break;
                case CollimationQuality.Poor:
                default:
                    qualityMsg = "? Poor collimation or focus. Adjustment required.";
                    break;
            }

            // Consistency assessment
            string consistencyMsg;
            if (session.StdDevFWHM < 0.3)
                consistencyMsg = "Excellent measurement consistency.";
            else if (session.StdDevFWHM < 0.5)
                consistencyMsg = "Good measurement consistency.";
            else
                consistencyMsg = "High variation - seeing may be affecting results.";

            return $"{qualityMsg}\n\n" +
                   $"Average FWHM: {session.AverageFWHM:F2}\" � {session.StdDevFWHM:F2}\"\n" +
                   $"Average Roundness: {session.AverageRoundness:F3} � {session.StdDevRoundness:F3}\n" +
                   $"{consistencyMsg}";
        }

        /// <summary>
        /// Clear results (called when session reset)
        /// </summary>
        public void ClearResults()
        {
            _previousSession = null;
            _currentSession = null;
            btnAnalyze.IsEnabled = true;
            resultsPanel.Visibility = Visibility.Collapsed;
            comparisonPanel.Visibility = Visibility.Collapsed;
            txtInfo.Visibility = Visibility.Visible;
            txtInfo.Text = "?? Enter number of samples (5-10 recommended), then click 'Take Sample Images' to start.";
        }
    }

    /// <summary>
    /// Results from a collimation analysis session (multiple images averaged)
    /// </summary>
    public class CollimationSessionResult
    {
        public List<CollimationResult> Results { get; set; }
        public double AverageFWHM { get; set; }
        public double AverageRoundness { get; set; }
        public double AverageEccentricity { get; set; }
        public double StdDevFWHM { get; set; }
        public double StdDevRoundness { get; set; }
        public int TotalStars { get; set; }
        public int SampleCount { get; set; }
        public CollimationQuality AverageQuality { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
