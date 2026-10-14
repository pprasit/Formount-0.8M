using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ForkMount_WPF.UI
{
    public partial class CalibrationSkyMap : UserControl
    {
        private List<CalibrationPoint> _calibrationPoints;
        private CalibrationPoint _currentPoint;
        // _mountPositionMarker removed - using XAML mountMarker instead

        public CalibrationSkyMap()
        {
            InitializeComponent();
            _calibrationPoints = new List<CalibrationPoint>();
            // No need to create marker - it's in XAML now
        }

        /// <summary>
        /// Add a new calibration point to the map
        /// </summary>
        public void AddPoint(double azimuth, double altitude, PointStatus status = PointStatus.Pending)
        {
            var point = new CalibrationPoint
            {
                Azimuth = azimuth,
                Altitude = altitude,
                Status = status
            };

            _calibrationPoints.Add(point);
            DrawPoint(point);
            UpdateStats();
        }

        /// <summary>
        /// Update all points on the map
        /// </summary>
        public void SetPoints(IEnumerable<CalibrationPoint> points)
        {
            ClearPoints();
            
            foreach (var point in points)
            {
                _calibrationPoints.Add(point);
                DrawPoint(point);
            }

            UpdateStats();
        }

        /// <summary>
        /// Update the status of a specific point
        /// </summary>
        public void UpdatePointStatus(int index, PointStatus status)
        {
            if (index >= 0 && index < _calibrationPoints.Count)
            {
                _calibrationPoints[index].Status = status;
                RedrawAllPoints();
                UpdateStats();
            }
        }

        /// <summary>
        /// Highlight a specific point as current
        /// </summary>
        public void SetCurrentPoint(double azimuth, double altitude)
        {
            _currentPoint = new CalibrationPoint
            {
                Azimuth = azimuth,
                Altitude = altitude,
                Status = PointStatus.Current
            };

            UpdateCurrentMarker();
        }

        public void ClearPoints()
        {
            _calibrationPoints.Clear();
            
            // Clear all point ellipses from skyCanvas (keep grid/labels/marker)
            var toRemove = new List<UIElement>();
            foreach (var child in skyCanvas.Children)
            {
                if (child is Ellipse ellipse && Canvas.GetZIndex(ellipse) == 100)
                {
                    toRemove.Add(ellipse);
                }
            }
            foreach (var element in toRemove)
            {
                skyCanvas.Children.Remove(element);
            }
            
            UpdateStats();
        }

        private void DrawPoint(CalibrationPoint point)
        {
            // Calculate position on sky map
            var (x, y) = AzAltToCanvasPosition(point.Azimuth, point.Altitude);

            // Skip if point is above 90° elevation (invalid)
            if (point.Altitude > 90 || point.Altitude < 0)
                return;

            // Create ellipse for the point
            var ellipse = new Ellipse
            {
                Width = 8,
                Height = 8,
                StrokeThickness = 1
            };

            // Set color based on status
            switch (point.Status)
            {
                case PointStatus.Pending:
                    ellipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280"));
                    ellipse.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4B5563"));
                    break;

                case PointStatus.Slewing:
                    ellipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")); // Yellow
                    ellipse.Stroke = Brushes.White;
                    ellipse.StrokeThickness = 1.5;
                    break;

                case PointStatus.Tracking:
                    ellipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")); // Yellow
                    ellipse.Stroke = Brushes.White;
                    ellipse.StrokeThickness = 2;
                    break;

                case PointStatus.Completed:
                    ellipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")); // Green
                    ellipse.Stroke = Brushes.White;
                    break;

                case PointStatus.Measured:
                    ellipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")); // Green (legacy)
                    ellipse.Stroke = Brushes.White;
                    break;

                case PointStatus.Error:
                    ellipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                    ellipse.Stroke = Brushes.White;
                    ellipse.StrokeThickness = 1.5;
                    break;

                case PointStatus.Current:
                    ellipse.Width = 10;
                    ellipse.Height = 10;
                    ellipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));
                    ellipse.Stroke = Brushes.White;
                    ellipse.StrokeThickness = 2;
                    break;
            }

            // Position the ellipse (center it on the point)
            Canvas.SetLeft(ellipse, x - ellipse.Width / 2);
            Canvas.SetTop(ellipse, y - ellipse.Height / 2);
            
            // Add to skyCanvas directly (not pointsCanvas) with lower ZIndex
            Canvas.SetZIndex(ellipse, 100); // Below mount marker (9999)
            skyCanvas.Children.Add(ellipse);
        }

        private void RedrawAllPoints()
        {
            // Clear existing point ellipses
            var toRemove = new List<UIElement>();
            foreach (var child in skyCanvas.Children)
            {
                if (child is Ellipse ellipse && Canvas.GetZIndex(ellipse) == 100)
                {
                    toRemove.Add(ellipse);
                }
            }
            foreach (var element in toRemove)
            {
                skyCanvas.Children.Remove(element);
            }
            
            // Redraw all points
            foreach (var point in _calibrationPoints)
            {
                DrawPoint(point);
            }
        }

        private void UpdateCurrentMarker()
        {
            if (_currentPoint == null)
            {
                if (mountMarker != null)
                    mountMarker.Visibility = Visibility.Collapsed;
                return;
            }

            var (x, y) = AzAltToCanvasPosition(_currentPoint.Azimuth, _currentPoint.Altitude);

            if (mountMarker != null)
            {
                Canvas.SetLeft(mountMarker, x - 8.5);
                Canvas.SetTop(mountMarker, y - 8.5);
                mountMarker.Visibility = Visibility.Visible;
            }
        }

        private void UpdateStats()
        {
            int total = _calibrationPoints.Count;
            int measured = _calibrationPoints.FindAll(p => p.Status == PointStatus.Measured).Count;

            txtStats.Text = $"{measured}/{total} points";
        }

        /// <summary>
        /// Convert Azimuth/Altitude to Canvas X/Y coordinates
        /// Azimuth: 0° = North (top), 90° = East (right), 180° = South (bottom), 270° = West (left)
        /// Altitude: 0° = Horizon (outer circle), 90° = Zenith (center)
        /// Supports negative azimuth angles (e.g., -270° to +270°)
        /// </summary>
        private (double x, double y) AzAltToCanvasPosition(double azimuth, double altitude)
        {
            // Normalize azimuth to 0-360° range
            // This allows us to work with mount's -300 to +300 range
            azimuth = azimuth % 360.0;
            if (azimuth < 0)
                azimuth += 360.0;
            
            // Canvas center
            double centerX = 180;
            double centerY = 180;

            // Max radius for horizon (0° elevation)
            double maxRadius = 170;

            // Calculate radius based on altitude (linear mapping)
            // Altitude 0° ? radius = maxRadius
            // Altitude 90° ? radius = 0
            double radius = maxRadius * (1 - altitude / 90.0);

            // Convert azimuth to radians (0° = North = top = -90° in standard coordinates)
            double angleRadians = (azimuth - 90) * Math.PI / 180.0;

            // Calculate X, Y
            double x = centerX + radius * Math.Cos(angleRadians);
            double y = centerY + radius * Math.Sin(angleRadians);

            return (x, y);
        }
    }

    /// <summary>
    /// Represents a calibration point on the sky map
    /// </summary>
    public class CalibrationPoint
    {
        public double Azimuth { get; set; }
        public double Altitude { get; set; }
        public PointStatus Status { get; set; }
    }

    /// <summary>
    /// Status of a calibration point
    /// </summary>
    public enum PointStatus
    {
        Pending,    // Not yet measured
        Slewing,    // Moving to position
        Tracking,   // Currently tracking (yellow)
        Completed,  // Successfully completed (green)
        Measured,   // Successfully measured (legacy - same as Completed)
        Current,    // Currently being measured
        Error       // Measurement failed
    }
}
