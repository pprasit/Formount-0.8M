using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ForkMount_WPF.UI
{
    public partial class PointingGraphWindow : Window
    {
        public PointingGraphWindow()
        {
            InitializeComponent();
            Loaded += PointingGraphWindow_Loaded;
        }

        private void PointingGraphWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigurePlot(wpfFramePlot);
            ConfigurePlot(wpfPositionPlot);
            RenderEmptyState();
        }

        public void UpdateData(
            IReadOnlyList<PointingMeasurement> measurements,
            IReadOnlyList<PositionStabilityResult> positionResults,
            double pixelScaleArcsecPerPixel)
        {
            if (!IsLoaded)
                return;

            ConfigurePlot(wpfFramePlot);
            ConfigurePlot(wpfPositionPlot);

            var validMeasurements = measurements?
                .Where(m => m.IsValid)
                .OrderBy(m => m.PositionIndex)
                .ThenBy(m => m.ImageIndex)
                .ToList() ?? new List<PointingMeasurement>();

            if (validMeasurements.Count == 0)
            {
                RenderEmptyState();
                return;
            }

            RenderFramePlot(validMeasurements, pixelScaleArcsecPerPixel);
            RenderPositionPlot(positionResults ?? Array.Empty<PositionStabilityResult>());

            txtValidFramesValue.Text = validMeasurements.Count.ToString();
            txtPositionsValue.Text = (positionResults?.Count ?? 0).ToString();

            var allJumps = (positionResults ?? Array.Empty<PositionStabilityResult>())
                .SelectMany(p => p.Jumps_arcsec)
                .ToList();
            var allOffsets = (positionResults ?? Array.Empty<PositionStabilityResult>())
                .SelectMany(p => p.Offsets_arcsec)
                .ToList();

            txtAvgJumpValue.Text = allJumps.Count > 0 ? $"{allJumps.Average():F2}\"" : "--";
            txtTotalRmsValue.Text = allOffsets.Count > 0
                ? $"{Math.Sqrt(allOffsets.Sum(offset => offset * offset) / allOffsets.Count):F2}\""
                : "--";
            txtGraphStatus.Text = "Top: Pink = radial offset, Blue = frame-to-frame jump. Bottom: Green = RMS, Orange = peak offset by position.";
        }

        private void RenderFramePlot(IReadOnlyList<PointingMeasurement> measurements, double pixelScaleArcsecPerPixel)
        {
            var ordered = measurements
                .OrderBy(m => m.PositionIndex)
                .ThenBy(m => m.ImageIndex)
                .ToList();

            var frameXs = new List<double>();
            var offsets = new List<double>();
            var jumpXs = new List<double>();
            var jumps = new List<double>();

            int globalIndex = 0;
            foreach (var positionGroup in ordered.GroupBy(m => m.PositionIndex).OrderBy(g => g.Key))
            {
                var positionMeasurements = positionGroup.OrderBy(m => m.ImageIndex).ToList();
                double centerX = CalculateMedian(positionMeasurements.Select(m => m.StarX));
                double centerY = CalculateMedian(positionMeasurements.Select(m => m.StarY));

                for (int i = 0; i < positionMeasurements.Count; i++)
                {
                    globalIndex++;
                    var measurement = positionMeasurements[i];
                    frameXs.Add(globalIndex);

                    double offsetPixels = Math.Sqrt(
                        Math.Pow(measurement.StarX - centerX, 2) +
                        Math.Pow(measurement.StarY - centerY, 2));
                    offsets.Add(offsetPixels * pixelScaleArcsecPerPixel);

                    if (i > 0)
                    {
                        var previous = positionMeasurements[i - 1];
                        double jumpPixels = Math.Sqrt(
                            Math.Pow(measurement.StarX - previous.StarX, 2) +
                            Math.Pow(measurement.StarY - previous.StarY, 2));
                        jumpXs.Add(globalIndex);
                        jumps.Add(jumpPixels * pixelScaleArcsecPerPixel);
                    }
                }
            }

            wpfFramePlot.Plot.Clear();
            ConfigurePlot(wpfFramePlot);

            var offsetScatter = wpfFramePlot.Plot.Add.Scatter(frameXs.ToArray(), offsets.ToArray());
            offsetScatter.Color = ScottPlot.Color.FromHex("#F472B6");
            offsetScatter.LineWidth = 2.2f;
            offsetScatter.MarkerSize = 4;

            if (jumpXs.Count > 0)
            {
                var jumpScatter = wpfFramePlot.Plot.Add.Scatter(jumpXs.ToArray(), jumps.ToArray());
                jumpScatter.Color = ScottPlot.Color.FromHex("#60A5FA");
                jumpScatter.LineWidth = 2.2f;
                jumpScatter.MarkerSize = 4;
            }

            wpfFramePlot.Plot.Title(SafeLabel("Pointing Stability: Frame Offset and Jump"));
            wpfFramePlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Valid Measurement Index");
            wpfFramePlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            wpfFramePlot.Plot.Axes.AutoScale();
            SafeRefresh(wpfFramePlot);
        }

        private void RenderPositionPlot(IReadOnlyList<PositionStabilityResult> positionResults)
        {
            wpfPositionPlot.Plot.Clear();
            ConfigurePlot(wpfPositionPlot);

            if (positionResults == null || positionResults.Count == 0)
            {
                wpfPositionPlot.Plot.Title(SafeLabel("Pointing Stability: No Position Summary Yet"));
                wpfPositionPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Position Index");
                wpfPositionPlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
                SafeRefresh(wpfPositionPlot);
                return;
            }

            var ordered = positionResults.OrderBy(result => result.PositionIndex).ToList();
            double[] positionXs = ordered.Select(result => (double)(result.PositionIndex + 1)).ToArray();
            double[] rmsValues = ordered.Select(result => result.RMS_arcsec).ToArray();
            double[] peakOffsets = ordered.Select(result => result.PeakOffset_arcsec).ToArray();

            var rmsScatter = wpfPositionPlot.Plot.Add.Scatter(positionXs, rmsValues);
            rmsScatter.Color = ScottPlot.Color.FromHex("#10B981");
            rmsScatter.LineWidth = 2.4f;
            rmsScatter.MarkerSize = 5;

            var peakScatter = wpfPositionPlot.Plot.Add.Scatter(positionXs, peakOffsets);
            peakScatter.Color = ScottPlot.Color.FromHex("#F59E0B");
            peakScatter.LineWidth = 2.4f;
            peakScatter.MarkerSize = 5;

            wpfPositionPlot.Plot.Title(SafeLabel("Per-Position RMS and Peak Offset"));
            wpfPositionPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Position Index");
            wpfPositionPlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            wpfPositionPlot.Plot.Axes.AutoScale();
            SafeRefresh(wpfPositionPlot);
        }

        private void ConfigurePlot(ScottPlot.WPF.WpfPlot plot)
        {
            if (plot == null)
                return;

            plot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
            plot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#252526");
            plot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2D2D30");
            plot.Plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#25262A");
            plot.Plot.Grid.MajorLineWidth = 0.8f;

            plot.Plot.Axes.Bottom.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            plot.Plot.Axes.Left.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            plot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            plot.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            plot.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
            plot.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
        }

        private void RenderEmptyState()
        {
            ConfigurePlot(wpfFramePlot);
            ConfigurePlot(wpfPositionPlot);

            wpfFramePlot.Plot.Clear();
            wpfFramePlot.Plot.Title(SafeLabel("Pointing Stability: No Data Yet"));
            wpfFramePlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Valid Measurement Index");
            wpfFramePlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            SafeRefresh(wpfFramePlot);

            wpfPositionPlot.Plot.Clear();
            wpfPositionPlot.Plot.Title(SafeLabel("Per-Position RMS and Peak Offset"));
            wpfPositionPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Position Index");
            wpfPositionPlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            SafeRefresh(wpfPositionPlot);

            txtValidFramesValue.Text = "--";
            txtPositionsValue.Text = "--";
            txtAvgJumpValue.Text = "--";
            txtTotalRmsValue.Text = "--";
            txtGraphStatus.Text = "Run a pointing stability test to display frame offsets, jumps, and per-position RMS.";
        }

        private static double CalculateMedian(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            if (sorted.Count == 0)
                return 0;

            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];
        }

        private static string SafeLabel(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
        }

        private void SafeRefresh(ScottPlot.WPF.WpfPlot plot)
        {
            if (plot == null || !IsLoaded)
                return;

            plot.Refresh();
        }
    }
}
