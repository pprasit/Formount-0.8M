using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Record of a single collimation adjustment step
    /// </summary>
    public class CollimationAdjustmentRecord
    {
        public DateTime Timestamp { get; set; }
        public double ScorePercent { get; set; }
        public double FWHM { get; set; }
        public double Roundness { get; set; }
        public double Eccentricity { get; set; }
        public double ScrewAAngle { get; set; }
        public string AdjustedScrew { get; set; }   // "A", "B", "C"
        public string AdjustDirection { get; set; }  // "Tighten", "Loosen"
        public bool IsFinished { get; set; }
    }

    /// <summary>
    /// Collimation Result Window — shows collimation score as %
    /// and a mirror adjustment screw diagram (A, B, C at 120° apart).
    /// User can set the angle of screw A; B and C auto-calculate.
    /// Supports iterative adjustment workflow with history tracking.
    /// </summary>
    public partial class CollimationResultWindow : Window
    {
        private readonly CollimationResult _result;
        private readonly CollimationSessionResult _session;
        private double _screwAAngleDeg = 0;
        private bool _isUpdatingUI = false;

        /// <summary>
        /// History of all adjustment steps in this collimation session
        /// </summary>
        public static List<CollimationAdjustmentRecord> AdjustmentHistory { get; set; } = new List<CollimationAdjustmentRecord>();

        /// <summary>
        /// Last saved screw A angle (persists between window opens)
        /// </summary>
        public static double LastScrewAAngle { get; set; } = 0;

        public CollimationResultWindow(CollimationResult result, CollimationSessionResult session = null)
        {
            InitializeComponent();
            _result = result;
            _session = session;

            // Restore last screw A angle
            _screwAAngleDeg = LastScrewAAngle;

            this.Loaded += (s, e) =>
            {
                _isUpdatingUI = true;
                sliderScrewA.Value = _screwAAngleDeg;
                txtScrewAAngle.Text = $"{_screwAAngleDeg:F0}";
                _isUpdatingUI = false;

                PopulateResults();
                DrawDiagram();
                GenerateSmartHint();
            };
        }

        private void PopulateResults()
        {
            if (_result == null) return;

            // Score
            double score = _result.CollimationScorePercent;
            txtScoreValue.Text = $"{score:F0}%";

            // Score ring color
            Color ringColor = ScoreToColor(score);
            scoreRing.Stroke = new SolidColorBrush(ringColor);

            // Quality badge
            txtQualityBadge.Text = _result.Quality.ToString();
            qualityBadge.Background = new SolidColorBrush(QualityToColor(_result.Quality));

            // Metrics
            txtFWHM.Text = $"{_result.AverageFWHM:F2}\"";
            txtFWHM.Foreground = new SolidColorBrush(
                _result.AverageFWHM < 3.0 ? Color.FromRgb(16, 185, 129) :
                _result.AverageFWHM < 4.0 ? Color.FromRgb(251, 191, 36) :
                Color.FromRgb(239, 68, 68));

            txtRoundness.Text = $"{_result.AverageRoundness:F3}";
            txtRoundness.Foreground = new SolidColorBrush(
                _result.AverageRoundness > 0.90 ? Color.FromRgb(16, 185, 129) :
                _result.AverageRoundness > 0.80 ? Color.FromRgb(251, 191, 36) :
                Color.FromRgb(239, 68, 68));

            txtEccentricity.Text = $"{_result.AverageEccentricity:F3}";
            txtEccentricity.Foreground = new SolidColorBrush(
                _result.AverageEccentricity < 0.2 ? Color.FromRgb(16, 185, 129) :
                _result.AverageEccentricity < 0.4 ? Color.FromRgb(251, 191, 36) :
                Color.FromRgb(239, 68, 68));

            txtStars.Text = _session != null
                ? $"{_session.TotalStars} ({_session.SampleCount} stacked)"
                : $"{_result.StarsAnalyzed}";
        }

        /// <summary>
        /// Generate smart hint based on current result + history.
        /// Uses rich Inlines: Bold for screw names, directions, and key values.
        /// </summary>
        private void GenerateSmartHint()
        {
            if (_result == null) return;

            hintPanel.Visibility = Visibility.Visible;
            txtHint.Inlines.Clear();

            var yellowBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            var greenBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            var redBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            var textBrush = new SolidColorBrush(Color.FromRgb(204, 204, 204));
            bool hasContent = false;

            // Check history for improvement/degradation trend
            if (AdjustmentHistory.Count > 0)
            {
                var lastRecord = AdjustmentHistory.Last();
                double scoreDelta = _result.CollimationScorePercent - lastRecord.ScorePercent;

                string lastScrew = lastRecord.AdjustedScrew ?? "?";
                string lastDir = lastRecord.AdjustDirection ?? "?";

                if (scoreDelta > 2)
                {
                    AddHintLine(txtHint, "Score improved by ", textBrush);
                    AddBold(txtHint, $"{scoreDelta:+0.0}%", greenBrush);
                    AddHintLine(txtHint, " after adjusting screw ", textBrush);
                    AddBold(txtHint, lastScrew, yellowBrush);
                    AddHintLine(txtHint, $" ({lastDir}).\n", textBrush);

                    AddHintLine(txtHint, "Continue adjusting screw ", textBrush);
                    AddBold(txtHint, lastScrew, yellowBrush);
                    AddHintLine(txtHint, " in the same direction (", textBrush);
                    AddBold(txtHint, lastDir, greenBrush);
                    AddHintLine(txtHint, ").\n", textBrush);
                }
                else if (scoreDelta < -2)
                {
                    string reverseDir = lastDir == "Tighten" ? "Loosen" : "Tighten";

                    AddHintLine(txtHint, "Score decreased by ", textBrush);
                    AddBold(txtHint, $"{scoreDelta:0.0}%", redBrush);
                    AddHintLine(txtHint, " after adjusting screw ", textBrush);
                    AddBold(txtHint, lastScrew, yellowBrush);
                    AddHintLine(txtHint, $" ({lastDir}).\n", textBrush);

                    AddHintLine(txtHint, "Try ", textBrush);
                    AddBold(txtHint, "REVERSING", redBrush);
                    AddHintLine(txtHint, ": ", textBrush);
                    AddBold(txtHint, reverseDir, greenBrush);
                    AddHintLine(txtHint, " screw ", textBrush);
                    AddBold(txtHint, lastScrew, yellowBrush);
                    AddHintLine(txtHint, ", or try a different screw.\n", textBrush);

                    cmbNextDirection.SelectedIndex = lastDir == "Tighten" ? 1 : 0;
                }
                else
                {
                    AddHintLine(txtHint, "Score is stable (", textBrush);
                    AddBold(txtHint, $"{scoreDelta:+0.0}%", yellowBrush);
                    AddHintLine(txtHint, "). The last adjustment had little effect.\n", textBrush);

                    AddHintLine(txtHint, "Try adjusting a ", textBrush);
                    AddBold(txtHint, "different screw", yellowBrush);
                    AddHintLine(txtHint, ".\n", textBrush);
                }

                AddHintLine(txtHint, "Session: ", textBrush);
                AddBold(txtHint, $"{AdjustmentHistory.Count}", yellowBrush);
                AddHintLine(txtHint, " adjustment(s) so far.\n", textBrush);
                hasContent = true;
            }

            // Elongation-based hint
            if (_result.HasSystematicError && _result.AverageEccentricity > 0.15)
            {
                double elongAngle = _result.AverageElongationAngle;
                AddHintLine(txtHint, "Stars elongated at ~", textBrush);
                AddBold(txtHint, $"{elongAngle:F0}\u00B0", yellowBrush);
                AddHintLine(txtHint, ". Adjust screw closest to ", textBrush);
                AddBold(txtHint, $"{(elongAngle + 90) % 360:F0}\u00B0", greenBrush);
                AddHintLine(txtHint, ".\n", textBrush);
                hasContent = true;
            }
            else if (_result.AverageEccentricity > 0.1)
            {
                AddHintLine(txtHint, "Minor elongation detected. Fine-tune for rounder stars.\n", textBrush);
                hasContent = true;
            }

            // Score-based hint
            double score = _result.CollimationScorePercent;
            if (score >= 90)
            {
                AddHintLine(txtHint, "Collimation is ", textBrush);
                AddBold(txtHint, "excellent", greenBrush);
                AddHintLine(txtHint, "! Only fine adjustments needed.", textBrush);
            }
            else if (score >= 70)
            {
                AddHintLine(txtHint, "Collimation is ", textBrush);
                AddBold(txtHint, "good", greenBrush);
                AddHintLine(txtHint, ". Small adjustments can improve star shapes.", textBrush);
            }
            else if (score >= 50)
            {
                AddHintLine(txtHint, "Collimation ", textBrush);
                AddBold(txtHint, "needs improvement", yellowBrush);
                AddHintLine(txtHint, ". Adjust screws carefully.", textBrush);
            }
            else
            {
                AddHintLine(txtHint, "Collimation is ", textBrush);
                AddBold(txtHint, "poor", redBrush);
                AddHintLine(txtHint, ". Significant adjustment required.", textBrush);
            }
            hasContent = true;

            if (!hasContent)
            {
                hintPanel.Visibility = Visibility.Collapsed;
            }
        }

        private static void AddHintLine(TextBlock tb, string text, Brush foreground)
        {
            tb.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = foreground });
        }

        private static void AddBold(TextBlock tb, string text, Brush foreground)
        {
            tb.Inlines.Add(new System.Windows.Documents.Bold(
                new System.Windows.Documents.Run(text) { Foreground = foreground }));
        }

        #region Screw A angle controls

        private void SliderScrewA_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUI) return;
            _screwAAngleDeg = sliderScrewA.Value;
            _isUpdatingUI = true;
            if (txtScrewAAngle != null)
                txtScrewAAngle.Text = $"{_screwAAngleDeg:F0}";
            _isUpdatingUI = false;
            DrawDiagram();
        }

        private void TxtScrewAAngle_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (double.TryParse(txtScrewAAngle.Text, out double val))
            {
                val = ((val % 360) + 360) % 360; // Normalize to 0-359
                _screwAAngleDeg = val;
                _isUpdatingUI = true;
                sliderScrewA.Value = val;
                txtScrewAAngle.Text = $"{val:F0}";
                _isUpdatingUI = false;
                DrawDiagram();
            }
            else
            {
                txtScrewAAngle.Text = $"{_screwAAngleDeg:F0}";
            }
        }

        #endregion

        private void CmbNextScrew_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DrawDiagram();
        }

        #region Action Buttons

        /// <summary>
        /// Further Adjustment: save result + screw config, close so user can adjust, then re-test
        /// </summary>
        private void BtnFurtherAdjustment_Click(object sender, RoutedEventArgs e)
        {
            SaveAdjustmentRecord(isFinished: false);
            LastScrewAAngle = _screwAAngleDeg;

            string screw = GetSelectedScrew();
            string direction = GetSelectedDirection();

            Debug.WriteLine($"[CollimationResult] Further Adjustment: Screw {screw} {direction}, A angle={_screwAAngleDeg:F0}°");
            Debug.WriteLine($"[CollimationResult] History: {AdjustmentHistory.Count} records");

            Close();
        }

        /// <summary>
        /// Finish: save final result + screw config, end collimation session
        /// </summary>
        private void BtnFinish_Click(object sender, RoutedEventArgs e)
        {
            SaveAdjustmentRecord(isFinished: true);
            LastScrewAAngle = _screwAAngleDeg;

            int totalSteps = AdjustmentHistory.Count;
            double finalScore = _result?.CollimationScorePercent ?? 0;

            Debug.WriteLine($"[CollimationResult] Finished! Final score: {finalScore:F0}%, Total steps: {totalSteps}");

            if (totalSteps > 1)
            {
                double startScore = AdjustmentHistory.First().ScorePercent;
                Debug.WriteLine($"[CollimationResult] Improvement: {startScore:F0}% -> {finalScore:F0}% ({finalScore - startScore:+0.0}%)");
            }

            // Clear history for next session
            AdjustmentHistory.Clear();

            MessageBox.Show(
                $"Collimation session complete!\n\n" +
                $"Final Score: {finalScore:F0}%\n" +
                $"Quality: {_result?.Quality}\n" +
                $"Total Adjustments: {totalSteps}\n\n" +
                $"Screw A Position: {_screwAAngleDeg:F0}°",
                "Collimation Finished",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SaveAdjustmentRecord(bool isFinished)
        {
            if (_result == null) return;

            var record = new CollimationAdjustmentRecord
            {
                Timestamp = DateTime.Now,
                ScorePercent = _result.CollimationScorePercent,
                FWHM = _result.AverageFWHM,
                Roundness = _result.AverageRoundness,
                Eccentricity = _result.AverageEccentricity,
                ScrewAAngle = _screwAAngleDeg,
                AdjustedScrew = GetSelectedScrew(),
                AdjustDirection = GetSelectedDirection(),
                IsFinished = isFinished
            };

            AdjustmentHistory.Add(record);
            Debug.WriteLine($"[CollimationResult] Saved record #{AdjustmentHistory.Count}: " +
                $"Score={record.ScorePercent:F0}%, Screw={record.AdjustedScrew} {record.AdjustDirection}, " +
                $"A={record.ScrewAAngle:F0}°, Finished={record.IsFinished}");
        }

        private string GetSelectedScrew()
        {
            if (cmbNextScrew?.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString() ?? "A";
            return "A";
        }

        private string GetSelectedDirection()
        {
            if (cmbNextDirection?.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString() ?? "Tighten";
            return "Tighten";
        }

        #endregion

        #region Diagram Drawing

        /// <summary>
        /// Draw the mirror diagram with 3 adjustment screws at 120° apart.
        /// </summary>
        private void DrawDiagram()
        {
            if (diagramCanvas == null) return;
            diagramCanvas.Children.Clear();

            double w = diagramCanvas.ActualWidth;
            double h = diagramCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double cx = w / 2;
            double cy = h / 2;
            double mirrorRadius = Math.Min(w, h) * 0.40;
            double screwOrbitRadius = mirrorRadius * 0.70;
            double screwDotRadius = 14;

            // Draw mirror outline (circle)
            var mirrorCircle = new Ellipse
            {
                Width = mirrorRadius * 2,
                Height = mirrorRadius * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(mirrorCircle, cx - mirrorRadius);
            Canvas.SetTop(mirrorCircle, cy - mirrorRadius);
            diagramCanvas.Children.Add(mirrorCircle);

            // Draw mirror cell (square)
            double cellSize = mirrorRadius * 2.15;
            var cellRect = new Rectangle
            {
                Width = cellSize,
                Height = cellSize,
                Stroke = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(cellRect, cx - cellSize / 2);
            Canvas.SetTop(cellRect, cy - cellSize / 2);
            diagramCanvas.Children.Add(cellRect);

            // Draw center mark
            var centerDot = new Ellipse
            {
                Width = 6, Height = 6,
                Fill = new SolidColorBrush(Color.FromRgb(107, 114, 128))
            };
            Canvas.SetLeft(centerDot, cx - 3);
            Canvas.SetTop(centerDot, cy - 3);
            diagramCanvas.Children.Add(centerDot);

            // Determine which screw is selected for next adjustment
            string selectedScrew = GetSelectedScrew();

            // Draw screws A, B, C
            var screws = new[]
            {
                ("A", _screwAAngleDeg, Color.FromRgb(239, 68, 68)),         // Red
                ("B", _screwAAngleDeg + 120, Color.FromRgb(59, 130, 246)),   // Blue
                ("C", _screwAAngleDeg + 240, Color.FromRgb(16, 185, 129))    // Green
            };

            foreach (var (name, angleDeg, color) in screws)
            {
                double angleRad = (angleDeg - 90) * Math.PI / 180.0; // -90 so 0° = top
                double sx = cx + screwOrbitRadius * Math.Cos(angleRad);
                double sy = cy + screwOrbitRadius * Math.Sin(angleRad);

                bool isSelected = name == selectedScrew;
                double dotRadius = isSelected ? screwDotRadius + 3 : screwDotRadius;

                // Highlight ring for selected screw
                if (isSelected)
                {
                    var highlight = new Ellipse
                    {
                        Width = (dotRadius + 4) * 2,
                        Height = (dotRadius + 4) * 2,
                        Stroke = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent
                    };
                    Canvas.SetLeft(highlight, sx - dotRadius - 4);
                    Canvas.SetTop(highlight, sy - dotRadius - 4);
                    diagramCanvas.Children.Add(highlight);
                }

                // Screw dot
                var screwDot = new Ellipse
                {
                    Width = dotRadius * 2,
                    Height = dotRadius * 2,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = isSelected ? 3.0 : 2.5,
                    Fill = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)60 : (byte)40, color.R, color.G, color.B))
                };
                Canvas.SetLeft(screwDot, sx - dotRadius);
                Canvas.SetTop(screwDot, sy - dotRadius);
                diagramCanvas.Children.Add(screwDot);

                // Screw label
                var label = new TextBlock
                {
                    Text = name,
                    Foreground = new SolidColorBrush(color),
                    FontSize = isSelected ? 14 : 13,
                    FontWeight = FontWeights.Bold
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, sx - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, sy - label.DesiredSize.Height / 2);
                diagramCanvas.Children.Add(label);

                // Draw line from center to screw (dashed)
                var line = new Line
                {
                    X1 = cx, Y1 = cy,
                    X2 = sx, Y2 = sy,
                    Stroke = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                diagramCanvas.Children.Add(line);
            }

            // Draw elongation direction arrow (if applicable)
            if (_result != null && _result.AverageEccentricity > 0.1)
            {
                double elongAngleRad = (_result.AverageElongationAngle - 90) * Math.PI / 180.0;
                double arrowLen = mirrorRadius * 0.55;

                var arrow = new Line
                {
                    X1 = cx - arrowLen * Math.Cos(elongAngleRad),
                    Y1 = cy - arrowLen * Math.Sin(elongAngleRad),
                    X2 = cx + arrowLen * Math.Cos(elongAngleRad),
                    Y2 = cy + arrowLen * Math.Sin(elongAngleRad),
                    Stroke = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 6, 3 }
                };
                diagramCanvas.Children.Add(arrow);

                var arrowLabel = new TextBlock
                {
                    Text = $"Elongation {_result.AverageElongationAngle:F0}\u00B0",
                    Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                    FontSize = 9,
                    FontStyle = FontStyles.Italic
                };
                Canvas.SetLeft(arrowLabel, cx + arrowLen * Math.Cos(elongAngleRad) + 4);
                Canvas.SetTop(arrowLabel, cy + arrowLen * Math.Sin(elongAngleRad) - 6);
                diagramCanvas.Children.Add(arrowLabel);
            }
        }

        #endregion

        #region Helpers

        private static Color ScoreToColor(double score)
        {
            if (score >= 85) return Color.FromRgb(16, 185, 129);
            if (score >= 65) return Color.FromRgb(34, 197, 94);
            if (score >= 45) return Color.FromRgb(251, 191, 36);
            if (score >= 25) return Color.FromRgb(249, 115, 22);
            return Color.FromRgb(239, 68, 68);
        }

        private static Color QualityToColor(CollimationQuality quality)
        {
            return quality switch
            {
                CollimationQuality.Excellent => Color.FromRgb(16, 185, 129),
                CollimationQuality.Good => Color.FromRgb(34, 197, 94),
                CollimationQuality.Fair => Color.FromRgb(251, 191, 36),
                CollimationQuality.NeedsAdjustment => Color.FromRgb(249, 115, 22),
                _ => Color.FromRgb(239, 68, 68)
            };
        }

        #endregion
    }
}
