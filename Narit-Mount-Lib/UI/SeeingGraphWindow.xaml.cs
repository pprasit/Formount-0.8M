using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ForkMount_WPF.UI
{
    public partial class SeeingGraphWindow : Window
    {
        public SeeingGraphWindow()
        {
            InitializeComponent();
            Loaded += SeeingGraphWindow_Loaded;
        }

        private void SeeingGraphWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigurePlot();
            RenderEmptyState();
        }

        public void UpdateData(
            IReadOnlyList<SeeingFrameMeasurement> measurements,
            SeeingResult result,
            double pixelScaleArcsecPerPixel)
        {
            if (!IsLoaded)
                return;

            ConfigurePlot();

            if (measurements == null || measurements.Count == 0)
            {
                RenderEmptyState();
                return;
            }

            double[] frameXs = measurements.Select(m => (double)(m.FrameIndex + 1)).ToArray();
            double[] fwhmArcsec = measurements
                .Select(m => m.FWHM_pixels * pixelScaleArcsecPerPixel)
                .ToArray();

            var jumpXs = new List<double>();
            var jumpArcsec = new List<double>();
            for (int i = 1; i < measurements.Count; i++)
            {
                double dx = measurements[i].StarX - measurements[i - 1].StarX;
                double dy = measurements[i].StarY - measurements[i - 1].StarY;
                jumpXs.Add(measurements[i].FrameIndex + 1);
                jumpArcsec.Add(Math.Sqrt(dx * dx + dy * dy) * pixelScaleArcsecPerPixel);
            }

            wpfPlot.Plot.Clear();
            ConfigurePlot();

            var fwhmScatter = wpfPlot.Plot.Add.Scatter(frameXs, fwhmArcsec);
            fwhmScatter.Color = ScottPlot.Color.FromHex("#F472B6");
            fwhmScatter.LineWidth = 2.5f;
            fwhmScatter.MarkerSize = 5;

            if (jumpXs.Count > 0)
            {
                var jumpScatter = wpfPlot.Plot.Add.Scatter(jumpXs.ToArray(), jumpArcsec.ToArray());
                jumpScatter.Color = ScottPlot.Color.FromHex("#60A5FA");
                jumpScatter.LineWidth = 2.5f;
                jumpScatter.MarkerSize = 5;
            }

            wpfPlot.Plot.Title(SafeLabel("Seeing Series: FWHM and Frame-to-Frame Jump"));
            wpfPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Frame Index");
            wpfPlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            wpfPlot.Plot.Axes.AutoScale();
            SafeRefresh();

            txtFramesValue.Text = measurements.Count.ToString();
            txtFwhmValue.Text = result != null ? $"{result.FWHM_arcsec:F2}\"" : "--";
            txtJumpRmsValue.Text = result != null ? $"{result.JumpRms_arcsec:F2}\"" : "--";
            txtQualityValue.Text = result?.Quality ?? "--";
            txtQualityValue.Foreground = result != null
                ? GetQualityBrush(result.Quality)
                : new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            txtGraphStatus.Text = "Pink = FWHM per frame, Blue = frame-to-frame jump";
        }

        private void ConfigurePlot()
        {
            if (wpfPlot == null)
                return;

            wpfPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
            wpfPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#252526");
            wpfPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2D2D30");
            wpfPlot.Plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#25262A");
            wpfPlot.Plot.Grid.MajorLineWidth = 0.8f;

            wpfPlot.Plot.Axes.Bottom.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Left.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
            wpfPlot.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
        }

        private void RenderEmptyState()
        {
            if (wpfPlot == null || !IsLoaded)
                return;

            wpfPlot.Plot.Clear();
            ConfigurePlot();
            wpfPlot.Plot.Title(SafeLabel("Seeing Series: No Data Yet"));
            wpfPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Frame Index");
            wpfPlot.Plot.Axes.Left.Label.Text = SafeLabel("Arcseconds");
            SafeRefresh();

            txtFramesValue.Text = "--";
            txtFwhmValue.Text = "--";
            txtJumpRmsValue.Text = "--";
            txtQualityValue.Text = "--";
            txtQualityValue.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            txtGraphStatus.Text = "Run a seeing series to display FWHM and frame-to-frame jump.";
        }

        private static Brush GetQualityBrush(string quality)
        {
            return quality switch
            {
                "Excellent" => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
                "Good" => new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7)),
                "Fair" => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                "Poor" => new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)),
                "Very Poor" => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                _ => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
            };
        }

        private static string SafeLabel(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
        }

        private void SafeRefresh()
        {
            if (wpfPlot == null || !IsLoaded)
                return;

            wpfPlot.Refresh();
        }
    }
}
