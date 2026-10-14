using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Control for measuring and analyzing pointing, tracking accuracy, and seeing estimation
    /// </summary>
    public partial class PointingAndTrackingControl : UserControl
    {
        #region Private Fields

        private CancellationTokenSource _pointingCts;
        private CancellationTokenSource _seeingCts;
        private bool _isPointingTestRunning;
        private bool _isTrackingTestRunning;
        private bool _isSeeingMeasurementRunning;

        // Pointing test results
        private readonly List<PointingMeasurement> _pointingMeasurements = new List<PointingMeasurement>();
        private PointingGraphWindow _pointingGraphWindow;
        private TrackingGraphWindow _trackingGraphWindow;
        private int _pointingSkippedMeasurements;

        // Tracking test results
        private readonly List<TrackingMeasurement> _trackingMeasurements = new List<TrackingMeasurement>();
        private StarCentroid _referenceStarPosition;
        private double _pixelScale_arcsecPerPixel = 0.16; // Default, should be set from camera/telescope

        // Seeing results
        private SeeingResult _lastSeeingResult;
        private readonly List<SeeingFrameMeasurement> _seeingMeasurements = new List<SeeingFrameMeasurement>();
        private TaskCompletionSource<SeeingFrameMeasurement> _pendingSeeingMeasurement;
        private TaskCompletionSource<PointingMeasurement> _pendingPointingMeasurement;
        private TaskCompletionSource<PointingSlewResult> _pendingPointingSlew;
        private SeeingGraphWindow _seeingGraphWindow;
        private const int DefaultSeeingSeriesFrameCount = 9;
        private const int DefaultSeeingFrameIntervalMs = 250;
        private const int SeeingCaptureTimeoutMs = 12000;
        private const double SeeingStarMatchRadius_pixels = 30.0;
        private const int PointingCaptureTimeoutMs = 12000;
        private const int PointingSlewTimeoutMs = 90000;
        private const double PointingStarMatchRadius_pixels = 30.0;
        private const double PointingStarFallbackMatchRadius_pixels = 80.0;

        #endregion

        #region Events

        /// <summary>
        /// Event raised when a capture is requested
        /// </summary>
        public event EventHandler<CaptureRequestEventArgs> CaptureRequested;

        /// <summary>
        /// Event raised when a slew/goto is requested for pointing test
        /// </summary>
        public event EventHandler<SlewRequestEventArgs> SlewRequested;

        #endregion

        #region Constructor

        public PointingAndTrackingControl()
        {
            InitializeComponent();
            txtSeeingFrames.TextChanged += (_, __) => UpdateSeeingTrackingSummary();
            txtSeeingInterval.TextChanged += (_, __) => UpdateSeeingTrackingSummary();
            UpdateSeeingTrackingSummary();
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Set the pixel scale for accurate arcsec calculations
        /// </summary>
        public double PixelScale_arcsecPerPixel
        {
            get => _pixelScale_arcsecPerPixel;
            set => _pixelScale_arcsecPerPixel = value;
        }

        #endregion

        #region Seeing Estimation

        private async void BtnMeasureSeeing_Click(object sender, RoutedEventArgs e)
        {
            if (_isSeeingMeasurementRunning)
            {
                _seeingCts?.Cancel();
                return;
            }

            if (!TryGetSeeingSeriesSettings(out int frameCount, out int frameIntervalMs))
            {
                return;
            }

            _isSeeingMeasurementRunning = true;
            _isTrackingTestRunning = true;
            _seeingCts = new CancellationTokenSource();
            _seeingMeasurements.Clear();
            _trackingMeasurements.Clear();
            _referenceStarPosition = null;
            _lastSeeingResult = null;
            ResetTrackingMetricsForSeries();

            double totalDurationSeconds = frameCount > 1
                ? ((frameCount - 1) * frameIntervalMs) / 1000.0
                : 0;

            btnMeasureSeeing.Content = "Stop Analysis";
            btnMeasureSeeing.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            txtSeeingStatus.Text = $"Starting combined seeing + tracking series ({frameCount} frames)...";
            txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
            txtTrackingStatus.Text = "Waiting for first valid star frame...";
            txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
            txtTrackingElapsed.Text = $"0 / {totalDurationSeconds:F1} s";
            UpdateSeeingTrackingSummary(frameCount, frameIntervalMs);

            SimpleLogger.Info(
                $"Combined seeing+tracking series started: frames={frameCount}, intervalMs={frameIntervalMs}, estimatedDuration={totalDurationSeconds:F1}s.",
                "SeeingTracking");

            try
            {
                await RunSeeingTrackingAnalysisAsync(frameCount, frameIntervalMs, _seeingCts.Token);
            }
            catch (OperationCanceledException)
            {
                txtSeeingStatus.Text = "Seeing + tracking analysis cancelled";
                txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                txtTrackingStatus.Text = "Combined analysis cancelled";
                txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                SimpleLogger.Warning("Combined seeing+tracking series cancelled by user.", "SeeingTracking");
            }
            catch (Exception ex)
            {
                txtSeeingStatus.Text = $"Error: {ex.Message}";
                txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                txtTrackingStatus.Text = $"Error: {ex.Message}";
                txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                SimpleLogger.Error($"Combined seeing+tracking series failed: {ex.Message}", "SeeingTracking");
            }
            finally
            {
                _pendingSeeingMeasurement = null;
                _isSeeingMeasurementRunning = false;
                _isTrackingTestRunning = false;
                btnMeasureSeeing.Content = "Run Seeing + Tracking Series";
                btnMeasureSeeing.Background = new SolidColorBrush(Color.FromRgb(0x14, 0xB8, 0xA6));
            }
        }

        private bool TryGetSeeingSeriesSettings(out int frameCount, out int frameIntervalMs)
        {
            frameCount = DefaultSeeingSeriesFrameCount;
            frameIntervalMs = DefaultSeeingFrameIntervalMs;

            if (!int.TryParse(txtSeeingFrames.Text, out frameCount) || frameCount < 3 || frameCount > 100)
            {
                txtSeeingStatus.Text = "Frames must be between 3 and 100";
                txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                _isSeeingMeasurementRunning = false;
                return false;
            }

            if (!int.TryParse(txtSeeingInterval.Text, out frameIntervalMs) || frameIntervalMs < 0 || frameIntervalMs > 10000)
            {
                txtSeeingStatus.Text = "Interval must be between 0 and 10000 ms";
                txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                _isSeeingMeasurementRunning = false;
                return false;
            }

            return true;
        }

        private async Task RunSeeingTrackingAnalysisAsync(int frameCount, int frameIntervalMs, CancellationToken ct)
        {
            double totalDurationSeconds = frameCount > 1
                ? ((frameCount - 1) * frameIntervalMs) / 1000.0
                : 0;

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                ct.ThrowIfCancellationRequested();

                txtSeeingStatus.Text = $"Capturing analysis frame {frameIndex + 1}/{frameCount}...";
                txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));

                var measurement = await RequestSeeingMeasurementFrameAsync(frameIndex, ct);
                if (measurement?.IsValid == true)
                {
                    _seeingMeasurements.Add(measurement);
                    txtSeeingStarCount.Text = _seeingMeasurements.Count.ToString();
                    txtSeeingStatus.Text = $"Captured frame {frameIndex + 1}/{frameCount} ({_seeingMeasurements.Count} usable)";

                    AppendTrackingMeasurementFromSeeingFrame(measurement);
                    UpdateSeeingGraphWindow();
                    UpdateTrackingGraphWindow();

                    if (_referenceStarPosition == null)
                    {
                        txtTrackingStatus.Text = "Tracking reference not established yet.";
                    }
                    else
                    {
                        txtTrackingStatus.Text = $"Tracking using {_trackingMeasurements.Count} valid measurements";
                        txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
                    }
                }
                else
                {
                    string reason = string.IsNullOrWhiteSpace(measurement?.FailureReason)
                        ? "No suitable star detected"
                        : measurement.FailureReason;
                    txtSeeingStatus.Text = $"Frame {frameIndex + 1}/{frameCount} skipped: {reason}";
                    txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    txtTrackingStatus.Text = $"Tracking frame {frameIndex + 1}/{frameCount} skipped: {reason}";
                    txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    SimpleLogger.Warning($"Combined series frame {frameIndex + 1}/{frameCount} skipped: {reason}.", "SeeingTracking");
                }

                double elapsedSeconds = frameIndex * frameIntervalMs / 1000.0;
                txtTrackingElapsed.Text = $"{elapsedSeconds:F1} / {totalDurationSeconds:F1} s";
                progressTracking.Value = frameCount > 1
                    ? ((double)frameIndex / (frameCount - 1)) * 100.0
                    : 100.0;
                UpdateSeeingTrackingSummary(frameCount, frameIntervalMs);

                if (frameIndex < frameCount - 1 && frameIntervalMs > 0)
                {
                    await Task.Delay(frameIntervalMs, ct);
                }
            }

            progressTracking.Value = 100;
            txtTrackingElapsed.Text = $"{totalDurationSeconds:F1} / {totalDurationSeconds:F1} s";

            FinalizeSeeingMeasurement();
            FinalizeTrackingMeasurement();
            UpdateSeeingTrackingSummary(frameCount, frameIntervalMs);

            SimpleLogger.Success(
                $"Combined seeing+tracking series complete: validSeeingFrames={_seeingMeasurements.Count}, validTrackingMeasurements={_trackingMeasurements.Count}, seeing={txtSeeingFWHM.Text}, trackingRms={txtTrackingRMS.Text}.",
                "SeeingTracking");
        }

        private async Task<SeeingFrameMeasurement> RequestSeeingMeasurementFrameAsync(int frameIndex, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<SeeingFrameMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSeeingMeasurement = tcs;

            CaptureRequested?.Invoke(this, new CaptureRequestEventArgs
            {
                Purpose = CapturePurpose.SeeingMeasurement,
                ExposureTime_ms = 1000,
                MeasurementIndex = frameIndex
            });

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(SeeingCaptureTimeoutMs, ct));
            if (completedTask != tcs.Task)
            {
                _pendingSeeingMeasurement = null;
                return new SeeingFrameMeasurement
                {
                    FrameIndex = frameIndex,
                    IsValid = false,
                    FailureReason = "Capture timeout"
                };
            }

            return await tcs.Task;
        }

        public void UpdateSeeingFromStars(List<DetectedStar> stars, int measurementIndex)
        {
            var measurement = SelectSeeingMeasurement(stars, measurementIndex);
            _pendingSeeingMeasurement?.TrySetResult(measurement);
            _pendingSeeingMeasurement = null;
        }

        private SeeingFrameMeasurement SelectSeeingMeasurement(List<DetectedStar> stars, int measurementIndex)
        {
            var validStars = (stars ?? new List<DetectedStar>())
                .Where(s => s.Brightness > 0 && s.FWHM > 0.8 && s.FWHM < 20.0)
                .OrderByDescending(s => s.Brightness)
                .ToList();

            if (validStars.Count == 0)
            {
                return new SeeingFrameMeasurement
                {
                    FrameIndex = measurementIndex,
                    IsValid = false,
                    FailureReason = "No valid stars"
                };
            }

            DetectedStar selectedStar;
            if (_seeingMeasurements.Count == 0)
            {
                selectedStar = validStars.First();
            }
            else
            {
                var previous = _seeingMeasurements.Last();
                selectedStar = validStars
                    .Select(star => new
                    {
                        Star = star,
                        Distance = Math.Sqrt(
                            Math.Pow(star.X - previous.StarX, 2) +
                            Math.Pow(star.Y - previous.StarY, 2))
                    })
                    .Where(x => x.Distance <= SeeingStarMatchRadius_pixels)
                    .OrderBy(x => x.Distance)
                    .ThenByDescending(x => x.Star.Brightness)
                    .Select(x => x.Star)
                    .FirstOrDefault() ?? validStars.First();
            }

            return new SeeingFrameMeasurement
            {
                FrameIndex = measurementIndex,
                Timestamp = DateTime.Now,
                StarX = selectedStar.X,
                StarY = selectedStar.Y,
                FWHM_pixels = selectedStar.FWHM,
                Brightness = selectedStar.Brightness,
                IsValid = true
            };
        }

        private void FinalizeSeeingMeasurement()
        {
            if (_seeingMeasurements.Count < 3)
            {
                txtSeeingFWHM.Text = "--";
                txtSeeingQuality.Text = "--";
                txtSeeingStarCount.Text = _seeingMeasurements.Count.ToString();
                txtSeeingStdDev.Text = "--";
                txtSeeingStatus.Text = "Need at least 3 valid frames for seeing estimation";
                txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                return;
            }

            try
            {
                var fwhmPixels = _seeingMeasurements.Select(m => m.FWHM_pixels).ToList();
                var jumpsPixels = new List<double>();

                for (int i = 1; i < _seeingMeasurements.Count; i++)
                {
                    double dx = _seeingMeasurements[i].StarX - _seeingMeasurements[i - 1].StarX;
                    double dy = _seeingMeasurements[i].StarY - _seeingMeasurements[i - 1].StarY;
                    jumpsPixels.Add(Math.Sqrt(dx * dx + dy * dy));
                }

                double avgFwhm_pixels = fwhmPixels.Average();
                double avgFwhm_arcsec = avgFwhm_pixels * _pixelScale_arcsecPerPixel;
                double jumpRms_pixels = jumpsPixels.Count > 0
                    ? Math.Sqrt(jumpsPixels.Sum(j => j * j) / jumpsPixels.Count)
                    : 0.0;
                double jumpRms_arcsec = jumpRms_pixels * _pixelScale_arcsecPerPixel;
                double maxJump_arcsec = jumpsPixels.Count > 0
                    ? jumpsPixels.Max() * _pixelScale_arcsecPerPixel
                    : 0.0;
                double fwhmStdDev_arcsec = CalculateStdDev(fwhmPixels) * _pixelScale_arcsecPerPixel;
                double combinedSeeing_arcsec = Math.Sqrt(
                    avgFwhm_arcsec * avgFwhm_arcsec +
                    jumpRms_arcsec * jumpRms_arcsec);

                string quality = ClassifySeeing(combinedSeeing_arcsec);
                Color qualityColor = GetSeeingQualityColor(combinedSeeing_arcsec);

                _lastSeeingResult = new SeeingResult
                {
                    Timestamp = DateTime.Now,
                    FWHM_arcsec = avgFwhm_arcsec,
                    FWHM_pixels = avgFwhm_pixels,
                    StdDev_arcsec = jumpRms_arcsec,
                    StarCount = _seeingMeasurements.Count,
                    Quality = quality,
                    FramesUsed = _seeingMeasurements.Count,
                    JumpRms_arcsec = jumpRms_arcsec,
                    MaxJump_arcsec = maxJump_arcsec,
                    FwhmStdDev_arcsec = fwhmStdDev_arcsec
                };

                Dispatcher.Invoke(() =>
                {
                    txtSeeingFWHM.Text = $"{avgFwhm_arcsec:F2}\"";
                    txtSeeingQuality.Text = quality;
                    txtSeeingQuality.Foreground = new SolidColorBrush(qualityColor);
                    txtSeeingStarCount.Text = _seeingMeasurements.Count.ToString();
                    txtSeeingStdDev.Text = $"{jumpRms_arcsec:F2}\"";
                    txtSeeingStatus.Text =
                        $"Seeing series complete: FWHM={avgFwhm_arcsec:F2}\", Jump RMS={jumpRms_arcsec:F2}\", Max Jump={maxJump_arcsec:F2}\"";
                    txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
                });

                UpdateSeeingGraphWindow();

                Debug.WriteLine(
                    $"[Seeing] Frames={_seeingMeasurements.Count}, FWHM={avgFwhm_arcsec:F2}\", JumpRMS={jumpRms_arcsec:F2}\", MaxJump={maxJump_arcsec:F2}\", Quality={quality}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    txtSeeingStatus.Text = $"Error: {ex.Message}";
                    txtSeeingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                });
            }
        }

        private void BtnOpenSeeingGraph_Click(object sender, RoutedEventArgs e)
        {
            OpenSeeingGraphWindow();
        }

        private void OpenSeeingGraphWindow()
        {
            if (_seeingGraphWindow == null || !_seeingGraphWindow.IsLoaded)
            {
                _seeingGraphWindow = new SeeingGraphWindow();
                _seeingGraphWindow.Owner = Window.GetWindow(this);
                _seeingGraphWindow.Closed += (_, __) => _seeingGraphWindow = null;
            }

            _seeingGraphWindow.Show();
            _seeingGraphWindow.Activate();
            UpdateSeeingGraphWindow();
        }

        private void UpdateSeeingGraphWindow()
        {
            if (_seeingGraphWindow == null || !_seeingGraphWindow.IsLoaded)
                return;

            _seeingGraphWindow.UpdateData(_seeingMeasurements, _lastSeeingResult, _pixelScale_arcsecPerPixel);
        }

        private string ClassifySeeing(double fwhm_arcsec)
        {
            if (fwhm_arcsec < 1.0) return "Excellent";
            if (fwhm_arcsec < 2.0) return "Good";
            if (fwhm_arcsec < 3.0) return "Fair";
            if (fwhm_arcsec < 4.0) return "Poor";
            return "Very Poor";
        }

        private Color GetSeeingQualityColor(double fwhm_arcsec)
        {
            if (fwhm_arcsec < 1.0) return Color.FromRgb(0x10, 0xB9, 0x81);
            if (fwhm_arcsec < 2.0) return Color.FromRgb(0x6E, 0xE7, 0xB7);
            if (fwhm_arcsec < 3.0) return Color.FromRgb(0xF5, 0x9E, 0x0B);
            if (fwhm_arcsec < 4.0) return Color.FromRgb(0xF9, 0x73, 0x16);
            return Color.FromRgb(0xEF, 0x44, 0x44);
        }

        #endregion

        #region Pointing Stability Test

        // Position-level results for pointing stability
        private readonly List<PositionStabilityResult> _positionResults = new List<PositionStabilityResult>();
        private StarCentroid _pointingReferencePosition;
        private int _currentPositionIndex;
        private int _currentImageIndex;

        private async void BtnStartPointingTest_Click(object sender, RoutedEventArgs e)
        {
            if (_isPointingTestRunning)
            {
                _pointingCts?.Cancel();
                return;
            }

            if (!int.TryParse(txtPointingPositions.Text, out int positionCount) || positionCount < 1)
            {
                txtPointingStatus.Text = "? Invalid position count";
                txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                return;
            }

            if (!int.TryParse(txtPointingImagesPerPos.Text, out int imagesPerPosition) || imagesPerPosition < 2)
            {
                txtPointingStatus.Text = "? Need at least 2 images per position";
                txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                return;
            }

            _isPointingTestRunning = true;
            _pointingCts = new CancellationTokenSource();
            _pointingMeasurements.Clear();
            _positionResults.Clear();
            _pointingSkippedMeasurements = 0;

            SimpleLogger.Info(
                $"Pointing stability test started: positions={positionCount}, imagesPerPosition={imagesPerPosition}, pixelScale={_pixelScale_arcsecPerPixel:F3}\"/px",
                "PointingTest");

            btnStartPointingTest.Content = "Stop Test";
            btnStartPointingTest.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

            txtPointingStatus.Text = "Starting pointing stability test...";
            txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));

            try
            {
                await RunPointingStabilityTestAsync(positionCount, imagesPerPosition, _pointingCts.Token);
            }
            catch (OperationCanceledException)
            {
                txtPointingStatus.Text = "Test cancelled";
                SimpleLogger.Warning("Pointing stability test cancelled by user.", "PointingTest");
            }
            catch (Exception ex)
            {
                txtPointingStatus.Text = $"Error: {ex.Message}";
                txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                SimpleLogger.Error($"Pointing stability test failed: {ex.Message}", "PointingTest");
            }
            finally
            {
                _isPointingTestRunning = false;
                _pendingPointingMeasurement = null;
                _pendingPointingSlew = null;
                btnStartPointingTest.Content = "Start Pointing Test";
                btnStartPointingTest.Background = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
            }
        }

        private async Task RunPointingStabilityTestAsync(int positionCount, int imagesPerPosition, CancellationToken ct)
        {
            var random = new Random();

            for (int posIndex = 0; posIndex < positionCount; posIndex++)
            {
                ct.ThrowIfCancellationRequested();
                _currentPositionIndex = posIndex;

                // Generate random sky position (safe altitude range)
                double targetAlt = 30 + random.NextDouble() * 50; // 30-80 degrees
                double targetAz = random.NextDouble() * 360; // 0-360 degrees

                txtPointingStatus.Text = $"Position {posIndex + 1}/{positionCount}: Slewing to Alt={targetAlt:F1}� Az={targetAz:F1}�...";
                txtPointingProgress.Text = $"{posIndex + 1} / {positionCount}";
                txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
                SimpleLogger.Info(
                    $"Position {posIndex + 1}/{positionCount}: slew requested to Alt={targetAlt:F2} deg, Az={targetAz:F2} deg.",
                    "PointingTest");

                await RequestPointingSlewAsync(targetAlt, targetAz, ct);

                // Take N images at this position
                _pointingReferencePosition = null;

                for (int imgIndex = 0; imgIndex < imagesPerPosition; imgIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    _currentImageIndex = imgIndex;

                    txtPointingStatus.Text = $"Position {posIndex + 1}/{positionCount}: Image {imgIndex + 1}/{imagesPerPosition}...";
                    txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));

                    var measurement = await RequestPointingMeasurementAsync(posIndex, imgIndex, ct);
                    if (!measurement.IsValid)
                    {
                        _pointingSkippedMeasurements++;
                        string failureReason = string.IsNullOrWhiteSpace(measurement.FailureReason)
                            ? "Unknown"
                            : measurement.FailureReason;
                        Dispatcher.Invoke(() =>
                        {
                            txtPointingStatus.Text =
                                $"Position {posIndex + 1}/{positionCount}: frame {imgIndex + 1}/{imagesPerPosition} skipped ({failureReason})";
                            txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                        });
                        SimpleLogger.Warning(
                            $"Position {posIndex + 1}/{positionCount}, frame {imgIndex + 1}/{imagesPerPosition} skipped: {failureReason}.",
                            "PointingTest");
                    }
                }

                // Calculate position RMS after all images at this position
                var validAtPosition = _pointingMeasurements
                    .Where(m => m.IsValid && m.PositionIndex == posIndex)
                    .ToList();

                if (validAtPosition.Count >= 2)
                {
                    var jumps = CalculateJumpsAtPosition(validAtPosition);
                    var offsets = CalculateOffsetsFromPositionCenter(validAtPosition);
                    var posResult = new PositionStabilityResult
                    {
                        PositionIndex = posIndex,
                        Altitude_deg = targetAlt,
                        Azimuth_deg = targetAz,
                        MeasurementCount = validAtPosition.Count,
                        Jumps_arcsec = jumps,
                        Offsets_arcsec = offsets,
                        AvgJump_arcsec = jumps.Count > 0 ? jumps.Average() : 0,
                        MaxJump_arcsec = jumps.Count > 0 ? jumps.Max() : 0,
                        RMS_arcsec = offsets.Count > 0 ? Math.Sqrt(offsets.Sum(o => o * o) / offsets.Count) : 0,
                        PeakOffset_arcsec = offsets.Count > 0 ? offsets.Max() : 0
                    };
                    _positionResults.Add(posResult);

                    Debug.WriteLine($"[Pointing] Position {posIndex + 1}: AvgJump={posResult.AvgJump_arcsec:F2}\", MaxJump={posResult.MaxJump_arcsec:F2}\", RMS={posResult.RMS_arcsec:F2}\", PeakOffset={posResult.PeakOffset_arcsec:F2}\"");
                    SimpleLogger.Info(
                        $"Position {posIndex + 1}/{positionCount} summary: validFrames={validAtPosition.Count}, avgJump={posResult.AvgJump_arcsec:F2}\", maxJump={posResult.MaxJump_arcsec:F2}\", rms={posResult.RMS_arcsec:F2}\", peakOffset={posResult.PeakOffset_arcsec:F2}\".",
                        "PointingTest");
                }
                else
                {
                    Debug.WriteLine($"[Pointing] Position {posIndex + 1}: insufficient valid measurements ({validAtPosition.Count})");
                    SimpleLogger.Warning(
                        $"Position {posIndex + 1}/{positionCount} has insufficient valid frames ({validAtPosition.Count}).",
                        "PointingTest");
                }

                UpdatePointingStabilityResults();
                UpdatePointingGraphWindow();
            }

            // Final summary
            txtPointingStatus.Text = $"Pointing test complete: {positionCount} positions, {_pointingMeasurements.Count} valid measurements";
            txtPointingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
            SimpleLogger.Success(
                $"Pointing stability test complete: positions={positionCount}, validMeasurements={_pointingMeasurements.Count}, skippedFrames={_pointingSkippedMeasurements}, avgJump={txtPointingAvgJump.Text}, totalRms={txtPointingTotalRMS.Text}.",
                "PointingTest");
        }

        private List<double> CalculateJumpsAtPosition(List<PointingMeasurement> measurements)
        {
            var jumps = new List<double>();
            var sorted = measurements.OrderBy(m => m.ImageIndex).ToList();

            for (int i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var curr = sorted[i];

                double dx = curr.StarX - prev.StarX;
                double dy = curr.StarY - prev.StarY;
                double jump_pixels = Math.Sqrt(dx * dx + dy * dy);
                double jump_arcsec = jump_pixels * _pixelScale_arcsecPerPixel;

                jumps.Add(jump_arcsec);
            }

            return jumps;
        }

        private List<double> CalculateOffsetsFromPositionCenter(List<PointingMeasurement> measurements)
        {
            var valid = measurements.Where(m => m.IsValid).OrderBy(m => m.ImageIndex).ToList();
            if (valid.Count == 0)
            {
                return new List<double>();
            }

            double centerX = CalculateMedian(valid.Select(m => m.StarX));
            double centerY = CalculateMedian(valid.Select(m => m.StarY));

            return valid
                .Select(m =>
                {
                    double dx = m.StarX - centerX;
                    double dy = m.StarY - centerY;
                    return Math.Sqrt(dx * dx + dy * dy) * _pixelScale_arcsecPerPixel;
                })
                .ToList();
        }

        private static double CalculateMedian(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0)
            {
                return 0;
            }

            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];
        }

        private async Task RequestPointingSlewAsync(double targetAlt, double targetAz, CancellationToken ct)
        {
            if (SlewRequested == null)
            {
                throw new InvalidOperationException("Pointing slew handler is not configured.");
            }

            var tcs = new TaskCompletionSource<PointingSlewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPointingSlew = tcs;

            SlewRequested.Invoke(this, new SlewRequestEventArgs
            {
                TargetAltitude_deg = targetAlt,
                TargetAzimuth_deg = targetAz
            });

            try
            {
                var result = await WaitForTaskAsync(tcs.Task, PointingSlewTimeoutMs, ct, "Pointing slew timed out.");
                if (!result.Success)
                {
                    SimpleLogger.Error($"Slew failed for Alt={targetAlt:F2} deg, Az={targetAz:F2} deg: {result.Message ?? "Unknown error"}", "PointingTest");
                    throw new InvalidOperationException(result.Message ?? "Pointing slew failed.");
                }

                SimpleLogger.Info($"Slew settled at Alt={targetAlt:F2} deg, Az={targetAz:F2} deg.", "PointingTest");
            }
            finally
            {
                if (ReferenceEquals(_pendingPointingSlew, tcs))
                {
                    _pendingPointingSlew = null;
                }
            }
        }

        private async Task<PointingMeasurement> RequestPointingMeasurementAsync(int positionIndex, int imageIndex, CancellationToken ct)
        {
            if (CaptureRequested == null)
            {
                throw new InvalidOperationException("Pointing capture handler is not configured.");
            }

            var tcs = new TaskCompletionSource<PointingMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPointingMeasurement = tcs;

            CaptureRequested.Invoke(this, new CaptureRequestEventArgs
            {
                Purpose = CapturePurpose.PointingMeasurement,
                ExposureTime_ms = 1000,
                PositionIndex = positionIndex,
                ImageIndex = imageIndex
            });

            try
            {
                return await WaitForTaskAsync(tcs.Task, PointingCaptureTimeoutMs, ct, "Pointing capture timed out.");
            }
            finally
            {
                if (ReferenceEquals(_pendingPointingMeasurement, tcs))
                {
                    _pendingPointingMeasurement = null;
                }
            }
        }

        private static async Task<T> WaitForTaskAsync<T>(Task<T> task, int timeoutMs, CancellationToken ct, string timeoutMessage)
        {
            var timeoutTask = Task.Delay(timeoutMs, ct);
            var completedTask = await Task.WhenAny(task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(timeoutMessage);
            }

            return await task;
        }

        /// <summary>
        /// Called when star position is detected in an image for pointing test
        /// </summary>
        public void UpdatePointingFromStars(List<DetectedStar> stars, int positionIndex, int imageIndex)
        {
            if (!_isPointingTestRunning) return;

            PointingMeasurement measurement;
            var selectedStar = SelectPointingStar(stars, imageIndex);

            if (selectedStar == null)
            {
                measurement = new PointingMeasurement
                {
                    Timestamp = DateTime.Now,
                    PositionIndex = positionIndex,
                    ImageIndex = imageIndex,
                    IsValid = false,
                    FailureReason = stars == null || stars.Count == 0 ? "No stars detected" : "Reference star lost"
                };
            }
            else
            {
                measurement = new PointingMeasurement
                {
                    Timestamp = DateTime.Now,
                    PositionIndex = positionIndex,
                    ImageIndex = imageIndex,
                    StarX = selectedStar.X,
                    StarY = selectedStar.Y,
                    IsValid = true
                };

                _pointingReferencePosition = new StarCentroid
                {
                    X = selectedStar.X,
                    Y = selectedStar.Y,
                    Brightness = selectedStar.Brightness,
                    Timestamp = DateTime.Now
                };
            }

            if (measurement.IsValid)
            {
                Dispatcher.Invoke(() =>
                {
                    _pointingMeasurements.Add(measurement);
                    txtPointingMeasurements.Text = _pointingMeasurements.Count.ToString();
                });
            }

            _pendingPointingMeasurement?.TrySetResult(measurement);
            UpdatePointingGraphWindow();
        }

        public void UpdatePointingStarPosition(double starX, double starY, int positionIndex, int imageIndex)
        {
            UpdatePointingFromStars(new List<DetectedStar>
            {
                new DetectedStar { X = starX, Y = starY, Brightness = 1.0 }
            }, positionIndex, imageIndex);
        }

        public void CompletePointingSlew(bool success, string message = null)
        {
            _pendingPointingSlew?.TrySetResult(new PointingSlewResult
            {
                Success = success,
                Message = message
            });
        }

        private DetectedStar SelectPointingStar(IReadOnlyList<DetectedStar> stars, int imageIndex)
        {
            if (stars == null || stars.Count == 0)
            {
                return null;
            }

            if (_pointingReferencePosition == null || imageIndex == 0)
            {
                return stars
                    .OrderByDescending(s => s.Brightness)
                    .ThenBy(s => s.FWHM > 0 ? s.FWHM : double.MaxValue)
                    .FirstOrDefault();
            }

            var matchedStar = stars
                .Select(star => new
                {
                    Star = star,
                    Distance = Math.Sqrt(Math.Pow(star.X - _pointingReferencePosition.X, 2) + Math.Pow(star.Y - _pointingReferencePosition.Y, 2))
                })
                .Where(candidate => candidate.Distance <= PointingStarMatchRadius_pixels)
                .OrderBy(candidate => candidate.Distance)
                .ThenByDescending(candidate => candidate.Star.Brightness)
                .Select(candidate => candidate.Star)
                .FirstOrDefault();

            if (matchedStar != null)
            {
                return matchedStar;
            }

            return stars
                .Select(star => new
                {
                    Star = star,
                    Distance = Math.Sqrt(Math.Pow(star.X - _pointingReferencePosition.X, 2) + Math.Pow(star.Y - _pointingReferencePosition.Y, 2))
                })
                .Where(candidate => candidate.Distance <= PointingStarFallbackMatchRadius_pixels)
                .OrderBy(candidate => candidate.Distance)
                .ThenByDescending(candidate => candidate.Star.Brightness)
                .Select(candidate => candidate.Star)
                .FirstOrDefault();
        }

        private void UpdatePointingStabilityResults()
        {
            if (_positionResults.Count == 0)
            {
                txtPointingAvgJump.Text = "--";
                txtPointingMaxJump.Text = "--";
                txtPointingTotalRMS.Text = "--";
                return;
            }

            // Calculate overall statistics
            var allJumps = _positionResults.SelectMany(p => p.Jumps_arcsec).ToList();
            if (allJumps.Count == 0)
            {
                txtPointingAvgJump.Text = "--";
                txtPointingMaxJump.Text = "--";
                txtPointingTotalRMS.Text = "--";
                return;
            }

            double avgJump = allJumps.Average();
            double maxJump = allJumps.Max();
            var allOffsets = _positionResults.SelectMany(p => p.Offsets_arcsec).ToList();
            double totalRMS = allOffsets.Count > 0
                ? Math.Sqrt(allOffsets.Sum(offset => offset * offset) / allOffsets.Count)
                : Math.Sqrt(allJumps.Sum(j => j * j) / allJumps.Count);

            txtPointingAvgJump.Text = $"{avgJump:F2}\"";
            txtPointingMaxJump.Text = $"{maxJump:F2}\"";
            txtPointingTotalRMS.Text = $"{totalRMS:F2}\"";
            txtPointingMeasurements.Text = _pointingMeasurements.Count.ToString();
        }

        private void BtnOpenPointingGraph_Click(object sender, RoutedEventArgs e)
        {
            OpenPointingGraphWindow();
        }

        private void OpenPointingGraphWindow()
        {
            if (_pointingGraphWindow == null || !_pointingGraphWindow.IsLoaded)
            {
                _pointingGraphWindow = new PointingGraphWindow();
                _pointingGraphWindow.Owner = Window.GetWindow(this);
                _pointingGraphWindow.Closed += (_, __) => _pointingGraphWindow = null;
            }

            _pointingGraphWindow.Show();
            _pointingGraphWindow.Activate();
            UpdatePointingGraphWindow();
        }

        private void UpdatePointingGraphWindow()
        {
            if (_pointingGraphWindow == null || !_pointingGraphWindow.IsLoaded)
                return;

            _pointingGraphWindow.UpdateData(_pointingMeasurements, _positionResults, _pixelScale_arcsecPerPixel);
        }

        private void BtnOpenTrackingGraph_Click(object sender, RoutedEventArgs e)
        {
            OpenTrackingGraphWindow();
        }

        private void OpenTrackingGraphWindow()
        {
            if (_trackingGraphWindow == null || !_trackingGraphWindow.IsLoaded)
            {
                _trackingGraphWindow = new TrackingGraphWindow();
                _trackingGraphWindow.Owner = Window.GetWindow(this);
                _trackingGraphWindow.Closed += (_, __) => _trackingGraphWindow = null;
            }

            _trackingGraphWindow.Show();
            _trackingGraphWindow.Activate();
            UpdateTrackingGraphWindow();
        }

        private void UpdateTrackingGraphWindow()
        {
            if (_trackingGraphWindow == null || !_trackingGraphWindow.IsLoaded)
                return;

            _trackingGraphWindow.UpdateData(_trackingMeasurements);
        }

        private void UpdateSeeingTrackingSummary(int? frameCount = null, int? frameIntervalMs = null)
        {
            int resolvedFrameCount = frameCount ?? TryParsePositiveInt(txtSeeingFrames.Text, DefaultSeeingSeriesFrameCount);
            int resolvedIntervalMs = frameIntervalMs ?? TryParsePositiveInt(txtSeeingInterval.Text, DefaultSeeingFrameIntervalMs);
            double estimatedDurationSeconds = resolvedFrameCount > 1
                ? ((resolvedFrameCount - 1) * resolvedIntervalMs) / 1000.0
                : 0;

            txtAnalysisEstimatedDuration.Text = $"{estimatedDurationSeconds:F1} s";
            txtAnalysisUsableFrames.Text = _seeingMeasurements.Count.ToString();
            txtAnalysisReferenceLocked.Text = _referenceStarPosition != null ? "Yes" : "No";
            txtAnalysisReferenceLocked.Foreground = _referenceStarPosition != null
                ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
                : new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        }

        private static int TryParsePositiveInt(string text, int fallback)
        {
            return int.TryParse(text, out int parsedValue) && parsedValue >= 0
                ? parsedValue
                : fallback;
        }

        #endregion

        #region Tracking Test

        private void BtnStartTrackingTest_Click(object sender, RoutedEventArgs e)
        {
            BtnMeasureSeeing_Click(sender, e);
        }

        private void ResetTrackingMetricsForSeries()
        {
            _trackingMeasurements.Clear();
            _referenceStarPosition = null;
            txtTrackingRADrift.Text = "--";
            txtTrackingDecDrift.Text = "--";
            txtTrackingRMS.Text = "--";
            txtTrackingPeakError.Text = "--";
            txtTrackingPE.Text = "--";
            txtTrackingElapsed.Text = "0 / 0 s";
            txtTrackingStatus.Text = "";
            txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
            progressTracking.Value = 0;
            UpdateTrackingGraphWindow();
            UpdateSeeingTrackingSummary();
        }

        private void AppendTrackingMeasurementFromSeeingFrame(SeeingFrameMeasurement measurement)
        {
            if (measurement == null || !measurement.IsValid)
            {
                return;
            }

            if (_referenceStarPosition == null)
            {
                _referenceStarPosition = new StarCentroid
                {
                    X = measurement.StarX,
                    Y = measurement.StarY,
                    Brightness = measurement.Brightness,
                    Timestamp = measurement.Timestamp
                };

                var referenceMeasurement = new TrackingMeasurement
                {
                    Timestamp = measurement.Timestamp,
                    ElapsedSeconds = 0,
                    DriftX_pixels = 0,
                    DriftY_pixels = 0,
                    DriftRA_arcsec = 0,
                    DriftDec_arcsec = 0,
                    MeasurementIndex = measurement.FrameIndex + 1,
                    IsValid = true
                };

                _trackingMeasurements.Add(referenceMeasurement);
                UpdateTrackingResults();
                UpdateTrackingGraphWindow();
                UpdateSeeingTrackingSummary();
                txtTrackingStatus.Text = $"Tracking reference locked at ({measurement.StarX:F1}, {measurement.StarY:F1})";
                txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
                SimpleLogger.Info(
                    $"Combined series reference star locked at ({measurement.StarX:F1}, {measurement.StarY:F1}).",
                    "SeeingTracking");
                return;
            }

            double driftX_pixels = measurement.StarX - _referenceStarPosition.X;
            double driftY_pixels = measurement.StarY - _referenceStarPosition.Y;

            var trackingMeasurement = new TrackingMeasurement
            {
                Timestamp = measurement.Timestamp,
                ElapsedSeconds = (measurement.Timestamp - _referenceStarPosition.Timestamp).TotalSeconds,
                DriftX_pixels = driftX_pixels,
                DriftY_pixels = driftY_pixels,
                DriftRA_arcsec = driftX_pixels * _pixelScale_arcsecPerPixel,
                DriftDec_arcsec = driftY_pixels * _pixelScale_arcsecPerPixel,
                MeasurementIndex = measurement.FrameIndex + 1,
                IsValid = true
            };

            _trackingMeasurements.Add(trackingMeasurement);
            UpdateTrackingResults();
            UpdateTrackingGraphWindow();
            UpdateSeeingTrackingSummary();
        }

        private void FinalizeTrackingMeasurement()
        {
            var validMeasurements = _trackingMeasurements.Where(m => m.IsValid).ToList();
            if (validMeasurements.Count < 2)
            {
                txtTrackingStatus.Text = "Need at least 2 valid frames for tracking drift estimation";
                txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                return;
            }

            UpdateTrackingResults();
            CalculatePeriodicError();

            txtTrackingStatus.Text = $"Tracking analysis complete - {validMeasurements.Count} valid measurements";
            txtTrackingStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
            UpdateTrackingGraphWindow();
            UpdateSeeingTrackingSummary();
        }

        /// <summary>
        /// Set the reference star position for tracking test
        /// </summary>
        public void SetReferenceStar(double x, double y, double brightness)
        {
            _referenceStarPosition = new StarCentroid
            {
                X = x,
                Y = y,
                Brightness = brightness,
                Timestamp = DateTime.Now
            };

            Debug.WriteLine($"[Tracking] Reference star set at ({x:F2}, {y:F2})");
        }

        /// <summary>
        /// Update tracking measurement with current star position
        /// </summary>
        public void UpdateTrackingMeasurement(double currentX, double currentY, int measurementIndex)
        {
            if (_referenceStarPosition == null || !_isTrackingTestRunning) return;

            double driftX_pixels = currentX - _referenceStarPosition.X;
            double driftY_pixels = currentY - _referenceStarPosition.Y;

            double driftX_arcsec = driftX_pixels * _pixelScale_arcsecPerPixel;
            double driftY_arcsec = driftY_pixels * _pixelScale_arcsecPerPixel;

            var measurement = new TrackingMeasurement
            {
                Timestamp = DateTime.Now,
                ElapsedSeconds = (DateTime.Now - _referenceStarPosition.Timestamp).TotalSeconds,
                DriftX_pixels = driftX_pixels,
                DriftY_pixels = driftY_pixels,
                DriftRA_arcsec = driftX_arcsec, // Assuming X ~= RA direction
                DriftDec_arcsec = driftY_arcsec, // Assuming Y ~= Dec direction
                MeasurementIndex = measurementIndex,
                IsValid = true
            };

            Dispatcher.Invoke(() =>
            {
                _trackingMeasurements.Add(measurement);
                UpdateTrackingResults();
            });
        }

        private void UpdateTrackingResults()
        {
            var validMeasurements = _trackingMeasurements.Where(m => m.IsValid).ToList();
            if (validMeasurements.Count < 2) return;

            // Calculate drift rate (arcsec/minute)
            var first = validMeasurements.First();
            var last = validMeasurements.Last();
            double elapsedMinutes = (last.ElapsedSeconds - first.ElapsedSeconds) / 60.0;

            if (elapsedMinutes > 0)
            {
                double raDriftRate = (last.DriftRA_arcsec - first.DriftRA_arcsec) / elapsedMinutes;
                double decDriftRate = (last.DriftDec_arcsec - first.DriftDec_arcsec) / elapsedMinutes;

                txtTrackingRADrift.Text = $"{raDriftRate:F2}\"/min";
                txtTrackingDecDrift.Text = $"{decDriftRate:F2}\"/min";
            }

            // Calculate RMS and peak error
            var totalErrors = validMeasurements.Select(m =>
                Math.Sqrt(m.DriftRA_arcsec * m.DriftRA_arcsec + m.DriftDec_arcsec * m.DriftDec_arcsec)).ToList();

            double rms = Math.Sqrt(totalErrors.Sum(e => e * e) / totalErrors.Count);
            double peak = totalErrors.Max();

            txtTrackingRMS.Text = $"{rms:F2}\"";
            txtTrackingPeakError.Text = $"{peak:F2}\"";
        }

        private void CalculatePeriodicError()
        {
            var validMeasurements = _trackingMeasurements.Where(m => m.IsValid).ToList();
            if (validMeasurements.Count < 10)
            {
                txtTrackingPE.Text = "N/A";
                return;
            }

            // Simple periodic error estimation: peak-to-peak / 2
            var raErrors = validMeasurements.Select(m => m.DriftRA_arcsec).ToList();
            double peakToPeak = raErrors.Max() - raErrors.Min();
            double periodicError = peakToPeak / 2;

            txtTrackingPE.Text = $"�{periodicError:F1}\"";

            Debug.WriteLine($"[Tracking] Estimated PE: �{periodicError:F1}\"");
        }

        #endregion

        #region Helper Methods

        private double CalculateStdDev(List<double> values)
        {
            if (values.Count < 2) return 0;
            double avg = values.Average();
            double sumSq = values.Sum(v => (v - avg) * (v - avg));
            return Math.Sqrt(sumSq / (values.Count - 1));
        }

        /// <summary>
        /// Reset all measurements and UI
        /// </summary>
        public void Reset()
        {
            _pointingMeasurements.Clear();
            _positionResults.Clear();
            _trackingMeasurements.Clear();
            _seeingMeasurements.Clear();
            _pointingSkippedMeasurements = 0;
            _pendingPointingMeasurement = null;
            _pendingPointingSlew = null;
            _pendingSeeingMeasurement = null;
            _referenceStarPosition = null;
            _pointingReferencePosition = null;
            _lastSeeingResult = null;

            // Reset Seeing UI
            txtSeeingFWHM.Text = "--";
            txtSeeingQuality.Text = "--";
            txtSeeingStarCount.Text = "--";
            txtSeeingStdDev.Text = "--";
            txtSeeingStatus.Text = "";
            UpdateSeeingGraphWindow();

            // Reset Pointing UI
            txtPointingAvgJump.Text = "--";
            txtPointingMaxJump.Text = "--";
            txtPointingTotalRMS.Text = "--";
            txtPointingMeasurements.Text = "0";
            txtPointingProgress.Text = "0 / 0";
            txtPointingStatus.Text = "";
            UpdatePointingGraphWindow();

            // Reset Tracking UI
            txtTrackingRADrift.Text = "--";
            txtTrackingDecDrift.Text = "--";
            txtTrackingRMS.Text = "--";
            txtTrackingPeakError.Text = "--";
            txtTrackingPE.Text = "--";
            txtTrackingElapsed.Text = "0 / 0 s";
            txtTrackingStatus.Text = "";
            progressTracking.Value = 0;
            UpdateTrackingGraphWindow();
            UpdateSeeingTrackingSummary();
        }

        #endregion
    }

    #region Data Classes

    public class PointingMeasurement
    {
        public DateTime Timestamp { get; set; }
        public int PositionIndex { get; set; }
        public int ImageIndex { get; set; }
        public double StarX { get; set; }
        public double StarY { get; set; }
        public bool IsValid { get; set; }
        public string FailureReason { get; set; }
    }

    public class PositionStabilityResult
    {
        public int PositionIndex { get; set; }
        public double Altitude_deg { get; set; }
        public double Azimuth_deg { get; set; }
        public int MeasurementCount { get; set; }
        public List<double> Jumps_arcsec { get; set; } = new List<double>();
        public List<double> Offsets_arcsec { get; set; } = new List<double>();
        public double AvgJump_arcsec { get; set; }
        public double MaxJump_arcsec { get; set; }
        public double RMS_arcsec { get; set; }
        public double PeakOffset_arcsec { get; set; }
    }

    public class PointingSlewResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class TrackingMeasurement
    {
        public DateTime Timestamp { get; set; }
        public double ElapsedSeconds { get; set; }
        public double DriftX_pixels { get; set; }
        public double DriftY_pixels { get; set; }
        public double DriftRA_arcsec { get; set; }
        public double DriftDec_arcsec { get; set; }
        public int MeasurementIndex { get; set; }
        public bool IsValid { get; set; }
    }

    public class StarCentroid
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Brightness { get; set; }
        public DateTime Timestamp { get; set; }
    }

        public class SeeingResult
    {
        public DateTime Timestamp { get; set; }
        public double FWHM_arcsec { get; set; }
        public double FWHM_pixels { get; set; }
        public double StdDev_arcsec { get; set; }
        public int StarCount { get; set; }
        public string Quality { get; set; }
        public int FramesUsed { get; set; }
        public double JumpRms_arcsec { get; set; }
        public double MaxJump_arcsec { get; set; }
        public double FwhmStdDev_arcsec { get; set; }
    }

    public class SeeingFrameMeasurement
    {
        public int FrameIndex { get; set; }
        public DateTime Timestamp { get; set; }
        public double StarX { get; set; }
        public double StarY { get; set; }
        public double FWHM_pixels { get; set; }
        public double Brightness { get; set; }
        public bool IsValid { get; set; }
        public string FailureReason { get; set; }
    }
    public class DetectedStar
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double FWHM { get; set; }
        public double Brightness { get; set; }
    }

    public enum CapturePurpose
    {
        General,
        SeeingMeasurement,
        PointingMeasurement,
        TrackingReference,
        TrackingMeasurement
    }

    public class CaptureRequestEventArgs : EventArgs
    {
        public CapturePurpose Purpose { get; set; } = CapturePurpose.General;
        public double ExposureTime_ms { get; set; } = 1000;
        public double CommandedAlt_deg { get; set; }
        public double CommandedAz_deg { get; set; }
        public int PositionIndex { get; set; }
        public int ImageIndex { get; set; }
        public int MeasurementIndex { get; set; }
    }

    public class SlewRequestEventArgs : EventArgs
    {
        public double TargetRA_hours { get; set; }
        public double TargetDec_deg { get; set; }
        public double TargetAltitude_deg { get; set; }
        public double TargetAzimuth_deg { get; set; }
    }

    public class PlateSolveRequestEventArgs : EventArgs
    {
        public double HintRA_hours { get; set; }
        public double HintDec_deg { get; set; }
        public double SearchRadius_deg { get; set; } = 5.0;
    }

    #endregion
}

