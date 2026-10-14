using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ForkMount_WPF.UI
{
    public partial class TrackingGraphWindow : Window
    {
        public TrackingGraphWindow()
        {
            InitializeComponent();
            Loaded += TrackingGraphWindow_Loaded;
        }

        private void TrackingGraphWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigurePlot();
            RenderEmptyState();
        }

        public void UpdateData(IReadOnlyList<TrackingMeasurement> measurements)
        {
            if (!IsLoaded)
                return;

            ConfigurePlot();

            var validMeasurements = measurements?
                .Where(m => m.IsValid)
                .OrderBy(m => m.ElapsedSeconds)
                .ToList() ?? new List<TrackingMeasurement>();

            if (validMeasurements.Count == 0)
            {
                RenderEmptyState();
                return;
            }

            double[] xs = validMeasurements.Select(m => m.ElapsedSeconds).ToArray();
            double[] ra = validMeasurements.Select(m => m.DriftRA_arcsec).ToArray();
            double[] dec = validMeasurements.Select(m => m.DriftDec_arcsec).ToArray();
            double[] total = validMeasurements
                .Select(m => Math.Sqrt(m.DriftRA_arcsec * m.DriftRA_arcsec + m.DriftDec_arcsec * m.DriftDec_arcsec))
                .ToArray();

            wpfTrackingPlot.Plot.Clear();
            ConfigurePlot();

            var raScatter = wpfTrackingPlot.Plot.Add.Scatter(xs, ra);
            raScatter.Color = ScottPlot.Color.FromHex("#60A5FA");
            raScatter.LineWidth = 2.2f;
            raScatter.MarkerSize = 4;

            var decScatter = wpfTrackingPlot.Plot.Add.Scatter(xs, dec);
            decScatter.Color = ScottPlot.Color.FromHex("#F472B6");
            decScatter.LineWidth = 2.2f;
            decScatter.MarkerSize = 4;

            var totalScatter = wpfTrackingPlot.Plot.Add.Scatter(xs, total);
            totalScatter.Color = ScottPlot.Color.FromHex("#10B981");
            totalScatter.LineWidth = 2.4f;
            totalScatter.MarkerSize = 4;

            wpfTrackingPlot.Plot.Title(SafeLabel("Tracking Drift Over Time"));
            wpfTrackingPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Elapsed Seconds");
            wpfTrackingPlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            wpfTrackingPlot.Plot.Axes.AutoScale();
            SafeRefresh();

            txtMeasurementsValue.Text = validMeasurements.Count.ToString();
            txtRaDriftValue.Text = $"{CalculateDriftPerMinute(validMeasurements, isRa: true):F2}\"/min";
            txtRmsValue.Text = $"{Math.Sqrt(total.Sum(value => value * value) / total.Length):F2}\"";
            txtPeakValue.Text = $"{total.Max():F2}\"";
            txtGraphStatus.Text = "Blue = RA drift, Pink = Dec drift, Green = total tracking error";
        }

        private void ConfigurePlot()
        {
            if (wpfTrackingPlot == null)
                return;

            wpfTrackingPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
            wpfTrackingPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#252526");
            wpfTrackingPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2D2D30");
            wpfTrackingPlot.Plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#25262A");
            wpfTrackingPlot.Plot.Grid.MajorLineWidth = 0.8f;

            wpfTrackingPlot.Plot.Axes.Bottom.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfTrackingPlot.Plot.Axes.Left.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfTrackingPlot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfTrackingPlot.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfTrackingPlot.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
            wpfTrackingPlot.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
        }

        private void RenderEmptyState()
        {
            if (wpfTrackingPlot == null || !IsLoaded)
                return;

            wpfTrackingPlot.Plot.Clear();
            ConfigurePlot();
            wpfTrackingPlot.Plot.Title(SafeLabel("Tracking Drift Over Time"));
            wpfTrackingPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Elapsed Seconds");
            wpfTrackingPlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            SafeRefresh();

            txtMeasurementsValue.Text = "--";
            txtRaDriftValue.Text = "--";
            txtRmsValue.Text = "--";
            txtPeakValue.Text = "--";
            txtGraphStatus.Text = "Run a seeing + tracking series to display drift and total tracking error.";
        }

        private static double CalculateDriftPerMinute(IReadOnlyList<TrackingMeasurement> measurements, bool isRa)
        {
            if (measurements.Count < 2)
                return 0;

            var first = measurements.First();
            var last = measurements.Last();
            double elapsedMinutes = (last.ElapsedSeconds - first.ElapsedSeconds) / 60.0;
            if (elapsedMinutes <= 0)
                return 0;

            double firstValue = isRa ? first.DriftRA_arcsec : first.DriftDec_arcsec;
            double lastValue = isRa ? last.DriftRA_arcsec : last.DriftDec_arcsec;
            return (lastValue - firstValue) / elapsedMinutes;
        }

        private static string SafeLabel(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
        }

        private void SafeRefresh()
        {
            if (wpfTrackingPlot == null || !IsLoaded)
                return;

            wpfTrackingPlot.Refresh();
        }
    }
}
