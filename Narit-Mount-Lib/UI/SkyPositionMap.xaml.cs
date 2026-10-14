using System;
using System.Windows;
using System.Windows.Controls;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    public partial class SkyPositionMap : UserControl
    {
        private const double CENTER_X = 180.0;
        private const double CENTER_Y = 180.0;
        private const double MAX_RADIUS = 170.0; // Radius to horizon (0° elevation)

        public static readonly DependencyProperty AzimuthProperty =
            DependencyProperty.Register(
                nameof(Azimuth),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnPositionChanged));

        public static readonly DependencyProperty AltitudeProperty =
            DependencyProperty.Register(
                nameof(Altitude),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnPositionChanged));

        public static readonly DependencyProperty AzimuthVelocityProperty =
            DependencyProperty.Register(
                nameof(AzimuthVelocity),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnVelocityChanged));

        public static readonly DependencyProperty AltitudeVelocityProperty =
            DependencyProperty.Register(
                nameof(AltitudeVelocity),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnVelocityChanged));

        public static readonly DependencyProperty SatelliteTrackerProperty =
            DependencyProperty.Register(
                nameof(SatelliteTracker),
                typeof(Engine.Tracking.SatelliteTracker),
                typeof(SkyPositionMap),
                new PropertyMetadata(null, OnTrackerChanged));

        public static readonly DependencyProperty ObserverLocationProperty =
            DependencyProperty.Register(
                nameof(ObserverLocation),
                typeof(Engine.Tracking.ObserverLocation),
                typeof(SkyPositionMap),
                new PropertyMetadata(null, OnTrackerChanged));

        public static readonly DependencyProperty TargetAzimuthProperty =
            DependencyProperty.Register(
                nameof(TargetAzimuth),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty TargetAltitudeProperty =
            DependencyProperty.Register(
                nameof(TargetAltitude),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty IsTrackingProperty =
            DependencyProperty.Register(
                nameof(IsTracking),
                typeof(bool),
                typeof(SkyPositionMap),
                new PropertyMetadata(false));

        public static readonly DependencyProperty AzimuthRmsErrorProperty =
            DependencyProperty.Register(
                nameof(AzimuthRmsError),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnRmsChanged));

        public static readonly DependencyProperty AltitudeRmsErrorProperty =
            DependencyProperty.Register(
                nameof(AltitudeRmsError),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnRmsChanged));

        // Mount Status Properties
        public static readonly DependencyProperty IsAzimuthEnabledProperty =
            DependencyProperty.Register(
                nameof(IsAzimuthEnabled),
                typeof(bool),
                typeof(SkyPositionMap),
                new PropertyMetadata(false, OnMountStatusChanged));

        public static readonly DependencyProperty IsAltitudeEnabledProperty =
            DependencyProperty.Register(
                nameof(IsAltitudeEnabled),
                typeof(bool),
                typeof(SkyPositionMap),
                new PropertyMetadata(false, OnMountStatusChanged));

        public static readonly DependencyProperty AzimuthCommandVelocityProperty =
            DependencyProperty.Register(
                nameof(AzimuthCommandVelocity),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnMountStatusChanged));

        public static readonly DependencyProperty AltitudeCommandVelocityProperty =
            DependencyProperty.Register(
                nameof(AltitudeCommandVelocity),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.0, OnMountStatusChanged));

        public static readonly DependencyProperty AzimuthTrackingSensitivityProperty =
            DependencyProperty.Register(
                nameof(AzimuthTrackingSensitivity),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.1, OnMountStatusChanged));

        public static readonly DependencyProperty AltitudeTrackingSensitivityProperty =
            DependencyProperty.Register(
                nameof(AltitudeTrackingSensitivity),
                typeof(double),
                typeof(SkyPositionMap),
                new PropertyMetadata(0.1, OnMountStatusChanged));

        // Mount Info Object (NEW - Single Source of Truth)
        public static readonly DependencyProperty MountInfoProperty =
            DependencyProperty.Register(
                nameof(MountInfo),
                typeof(Engine.Info.MountInfo),
                typeof(SkyPositionMap),
                new PropertyMetadata(null)); // No callback - updated from UI Timer

        /// <summary>
        /// Current Azimuth angle in degrees - actual position from encoder

        /// (0° = North, 90° = East, 180° = South, 270° = West)
        /// </summary>
        public double Azimuth
        {
            get => (double)GetValue(AzimuthProperty);
            set => SetValue(AzimuthProperty, value);
        }

        /// <summary>
        /// Target Azimuth position (satellite position when tracking)
        /// </summary>
        public double TargetAzimuth
        {
            get => (double)GetValue(TargetAzimuthProperty);
            set => SetValue(TargetAzimuthProperty, value);
        }

        /// <summary>
        /// Target Altitude position (satellite position when tracking)
        /// </summary>
        public double TargetAltitude
        {
            get => (double)GetValue(TargetAltitudeProperty);
            set => SetValue(TargetAltitudeProperty, value);
        }

        /// <summary>
        /// Indicates if satellite tracking is active
        /// </summary>
        public bool IsTracking
        {
            get => (bool)GetValue(IsTrackingProperty);
            set => SetValue(IsTrackingProperty, value);
        }

        /// <summary>
        /// Altitude angle in degrees (0° = Horizon, 90° = Zenith)
        /// </summary>
        public double Altitude
        {
            get => (double)GetValue(AltitudeProperty);
            set => SetValue(AltitudeProperty, value);
        }

        /// <summary>
        /// Azimuth velocity in degrees per second
        /// </summary>
        public double AzimuthVelocity
        {
            get => (double)GetValue(AzimuthVelocityProperty);
            set => SetValue(AzimuthVelocityProperty, value);
        }

        /// <summary>
        /// Altitude velocity in degrees per second
        /// </summary>
        public double AltitudeVelocity
        {
            get => (double)GetValue(AltitudeVelocityProperty);
            set => SetValue(AltitudeVelocityProperty, value);
        }

        /// <summary>
        /// Current satellite tracker (for SGP4 trajectory calculation)
        /// </summary>
        public Engine.Tracking.SatelliteTracker SatelliteTracker
        {
            get => (Engine.Tracking.SatelliteTracker)GetValue(SatelliteTrackerProperty);
            set => SetValue(SatelliteTrackerProperty, value);
        }

        /// <summary>
        /// Observer location (for SGP4 trajectory calculation)
        /// </summary>
        public Engine.Tracking.ObserverLocation ObserverLocation
        {
            get => (Engine.Tracking.ObserverLocation)GetValue(ObserverLocationProperty);
            set => SetValue(ObserverLocationProperty, value);
        }

        /// <summary>
        /// Azimuth RMS tracking error in arcseconds
        /// </summary>
        public double AzimuthRmsError
        {
            get => (double)GetValue(AzimuthRmsErrorProperty);
            set => SetValue(AzimuthRmsErrorProperty, value);
        }

        /// <summary>
        /// Altitude RMS tracking error in arcseconds
        /// </summary>
        public double AltitudeRmsError
        {
            get => (double)GetValue(AltitudeRmsErrorProperty);
            set => SetValue(AltitudeRmsErrorProperty, value);
        }

        /// <summary>
        /// Azimuth Drive Enable Status
        /// </summary>
        public bool IsAzimuthEnabled
        {
            get => (bool)GetValue(IsAzimuthEnabledProperty);
            set => SetValue(IsAzimuthEnabledProperty, value);
        }

        /// <summary>
        /// Altitude Drive Enable Status
        /// </summary>
        public bool IsAltitudeEnabled
        {
            get => (bool)GetValue(IsAltitudeEnabledProperty);
            set => SetValue(IsAltitudeEnabledProperty, value);
        }

        /// <summary>
        /// Azimuth Command Velocity (deg/s)
        /// </summary>
        public double AzimuthCommandVelocity
        {
            get => (double)GetValue(AzimuthCommandVelocityProperty);
            set => SetValue(AzimuthCommandVelocityProperty, value);
        }

        /// <summary>
        /// Altitude Command Velocity (deg/s)
        /// </summary>
        public double AltitudeCommandVelocity
        {
            get => (double)GetValue(AltitudeCommandVelocityProperty);
            set => SetValue(AltitudeCommandVelocityProperty, value);
        }

        /// <summary>
        /// Azimuth Tracking Sensitivity threshold (deg)
        /// </summary>
        public double AzimuthTrackingSensitivity
        {
            get => (double)GetValue(AzimuthTrackingSensitivityProperty);
            set => SetValue(AzimuthTrackingSensitivityProperty, value);
        }

        /// <summary>
        /// Altitude Tracking Sensitivity threshold (deg)
        /// </summary>
        public double AltitudeTrackingSensitivity
        {
            get => (double)GetValue(AltitudeTrackingSensitivityProperty);
            set => SetValue(AltitudeTrackingSensitivityProperty, value);
        }

        /// <summary>
        /// Mount Info - Centralized status hub (Single Source of Truth)
        /// Contains IsMountEnabled and IsTracking calculated from both axes
        /// </summary>
        public Engine.Info.MountInfo MountInfo
        {
            get => (Engine.Info.MountInfo)GetValue(MountInfoProperty);
            set => SetValue(MountInfoProperty, value);
        }

        public SkyPositionMap()
        {
            InitializeComponent();
            UpdatePosition();
            UpdateVelocity();
            UpdateRmsDisplay();  // Initialize RMS display
            UpdateMountStatus(); // Initialize Mount Status display
        }




        private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SkyPositionMap map)
            {
                map.UpdatePosition();
            }
        }

        private static void OnVelocityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SkyPositionMap map)
            {
                map.UpdateVelocity();
            }
        }

        private static void OnTrackerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SkyPositionMap map)
            {
                // [DEBUG] Log tracker change
                var newTracker = e.NewValue as Engine.Tracking.SatelliteTracker;
                System.Diagnostics.Debug.WriteLine($"[SKY MAP] OnTrackerChanged: {(newTracker != null ? newTracker.Name : "NULL")}");
                
                map.UpdateTrajectoryPath();
            }
        }

        private static void OnRmsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SkyPositionMap map)
            {
                map.UpdateRmsDisplay();
            }
        }

        private static void OnMountStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SkyPositionMap map)
            {
                map.UpdateMountStatus();
            }
        }

        private void UpdatePosition()

        {
            // [FIX] Display raw azimuth value (can be negative like -259.276°)
            // But normalize for map position calculation
            double azimuthDisplay = Azimuth; // Raw value for display
            double azimuthNormalized = Azimuth % 360.0; // Normalize for map position
            if (azimuthNormalized < 0) azimuthNormalized += 360.0;
            
            double altitude = Math.Max(0, Math.Min(90, Altitude));

            // Update text displays (3 decimal places) - Show raw azimuth value
            txtAzimuth.Text = $"{azimuthDisplay:F3}\u00B0";
            txtAltitude.Text = $"{altitude:F3}\u00B0";

            // Calculate radius from center based on altitude
            // At 0° altitude (horizon), radius = MAX_RADIUS
            // At 90° altitude (zenith), radius = 0
            double radius = (90.0 - altitude) / 90.0 * MAX_RADIUS;

            // Convert azimuth to radians (0° = North = top, clockwise)
            // Use normalized azimuth for map position
            double angleRadians = (azimuthNormalized) * Math.PI / 180.0;

            // Calculate X, Y position on canvas
            // Note: In WPF, Y increases downward, so we negate the Y component
            double x = CENTER_X + radius * Math.Sin(angleRadians);
            double y = CENTER_Y - radius * Math.Cos(angleRadians);

            // Update marker position (offset by half marker size to center it)
            Canvas.SetLeft(positionMarker, x - 6);
            Canvas.SetTop(positionMarker, y - 6);

            // Update position line
            positionLine.X2 = x;
            positionLine.Y2 = y;

            // Update Limit Lines (use raw azimuth for limit comparison)
            UpdateLimitLines(azimuthDisplay);

            // Update trajectory path
            UpdateTrajectoryPath();

            // Change marker color based on altitude
            if (altitude < 10)
            {
                // Below horizon or very low - Red
                positionMarker.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            }
            else if (altitude < 30)
            {
                // Low altitude - Orange
                positionMarker.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
            }
            else if (altitude < 60)
            {
                // Medium altitude - Yellow-Green
                positionMarker.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x84, 0xCC, 0x16));
            }
            else
            {
                // High altitude - Green
                positionMarker.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
            }
        }

        private void UpdateLimitLines(double currentAzimuth)
        {
            try
            {
                // Get azimuth axis config from AppSettings
                var azimuthConfig = AppSettings.GetAzimuthAxisConfig();
                if (azimuthConfig == null) return;

                // Keep original values for display
                double cwLimitOriginal = azimuthConfig.CW_Limit_Deg;
                double ccwLimitOriginal = azimuthConfig.CCW_Limit_Deg;

                // Normalize limits to 0-360 range for calculation
                double cwLimit = cwLimitOriginal % 360.0;
                if (cwLimit < 0) cwLimit += 360.0;
                
                double ccwLimit = ccwLimitOriginal % 360.0;
                if (ccwLimit < 0) ccwLimit += 360.0;

                // Get sensor status from AzimuthInfo
                bool showCWLimit = false;
                bool showCCWLimit = false;

                // Search for ManualControlWindow in opened windows
                foreach (Window window in System.Windows.Application.Current.Windows)
                {
                    if (window is UI.ManualControlWindow manualControl)
                    {
                        var azimuthInfo = manualControl.AzimuthInfo;
                        if (azimuthInfo != null)
                        {
                            // Use actual sensor status to determine which limit to show
                            showCWLimit = azimuthInfo.CW_Sensor_Active;
                            showCCWLimit = azimuthInfo.CCW_Sensor_Active;
                        }
                        break;
                    }
                }

                // Show limit lines based on sensor status
                if (showCWLimit)
                {
                    // CW Sensor is active - show CW Limit (pass original value for display)
                    ShowLimitLine(cwLimitLine, cwLimitLabel, cwLimit, cwLimitOriginal, "CW LIMIT");
                    HideLimitLine(ccwLimitLine, ccwLimitLabel);
                }
                else if (showCCWLimit)
                {
                    // CCW Sensor is active - show CCW Limit (pass original value for display)
                    ShowLimitLine(ccwLimitLine, ccwLimitLabel, ccwLimit, ccwLimitOriginal, "CCW LIMIT");
                    HideLimitLine(cwLimitLine, cwLimitLabel);
                }
                else
                {
                    // No sensor active - hide both limits
                    HideLimitLine(cwLimitLine, cwLimitLabel);
                    HideLimitLine(ccwLimitLine, ccwLimitLabel);
                }
            }
            catch
            {
                // Hide all limits on error
                HideLimitLine(cwLimitLine, cwLimitLabel);
                HideLimitLine(ccwLimitLine, ccwLimitLabel);
            }
        }

        private void UpdateVelocity()
        {
            // Update velocity displays (3 decimal places)
            txtAzimuthVelocity.Text = $"{AzimuthVelocity:F3} deg/s";
            txtAltitudeVelocity.Text = $"{AltitudeVelocity:F3} deg/s";

            // Update trajectory path based on velocity
            UpdateTrajectoryPath();
        }

        private void UpdateTrajectoryPath()
        {
            try
            {
                // Clear existing points and markers
                trajectoryPath.Points.Clear();
                trajectoryMarkers.Children.Clear();

                // Check if we have valid tracker and observer location for SGP4 calculation
                if (SatelliteTracker == null || ObserverLocation == null)
                {
                    // [REMOVED] Excessive debug logging - only log once when state changes
                    // System.Diagnostics.Debug.WriteLine($"[SKY MAP] UpdateTrajectoryPath: Missing data - Tracker={SatelliteTracker != null}, Observer={ObserverLocation != null}");
                    trajectoryPath.Visibility = Visibility.Collapsed;
                    return;
                }

                // [REMOVED] Excessive debug logging - was printing every update cycle
                // System.Diagnostics.Debug.WriteLine($"[SKY MAP] UpdateTrajectoryPath: Drawing path for {SatelliteTracker.Name}");
                trajectoryPath.Visibility = Visibility.Visible;

                // Use current UTC time as starting point
                DateTime startTime = DateTime.UtcNow;

                // Store trajectory points for marker calculation
                var trajectoryPoints = new System.Collections.Generic.List<(System.Windows.Point position, double azimuth, double altitude)>();

                // Calculate trajectory using SGP4 with 1-second intervals
                // Continue until altitude goes below 0° (horizon)
                for (int i = 0; i < 600; i++) // Max 600 seconds (10 minutes)
                {
                    DateTime futureTime = startTime.AddSeconds(i);

                    // Get satellite position from SGP4
                    var topo = SatelliteTracker.GetTopocentric(ObserverLocation, futureTime);

                    // Stop if satellite goes below horizon
                    if (topo.Elevation < 0)
                        break;

                    // Also stop if above 90° (should not happen but safety check)
                    if (topo.Elevation > 90)
                        break;

                    // Convert topocentric coordinates to canvas coordinates
                    double azimuthNormalized = topo.Azimuth % 360.0;
                    if (azimuthNormalized < 0) azimuthNormalized += 360.0;

                    double radius = (90.0 - topo.Elevation) / 90.0 * MAX_RADIUS;
                    double angleRadians = azimuthNormalized * Math.PI / 180.0;
                    double x = CENTER_X + radius * Math.Sin(angleRadians);
                    double y = CENTER_Y - radius * Math.Cos(angleRadians);

                    var point = new System.Windows.Point(x, y);
                    trajectoryPath.Points.Add(point);
                    trajectoryPoints.Add((point, azimuthNormalized, topo.Elevation));
                }

                // Add triangle markers along the trajectory path
                // Place markers every 60 seconds (every 60th point)
                for (int i = 60; i < trajectoryPoints.Count; i += 60)
                {
                    var current = trajectoryPoints[i];
                    var previous = trajectoryPoints[i - 1];

                    // Calculate direction of movement
                    double dx = current.position.X - previous.position.X;
                    double dy = current.position.Y - previous.position.Y;
                    double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

                    // Create triangle marker
                    AddTriangleMarker(current.position.X, current.position.Y, angle);
                }

                // Hide trajectory if we have fewer than 2 points
                if (trajectoryPath.Points.Count < 2)
                {
                    trajectoryPath.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                // Hide trajectory on error
                trajectoryPath.Visibility = Visibility.Collapsed;
            }
        }

        private void AddTriangleMarker(double x, double y, double angleDegrees)
        {
            // Create a triangle polygon
            var triangle = new System.Windows.Shapes.Polygon
            {
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF9, 0x73, 0x16)), // Orange color
                Stroke = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)), // White border
                StrokeThickness = 0.5,
                Opacity = 0.9
            };

            // Define triangle shape pointing right (will be rotated)
            // Triangle size: 8 pixels wide, 10 pixels tall
            triangle.Points = new System.Windows.Media.PointCollection
            {
                new System.Windows.Point(0, -4),   // Top point
                new System.Windows.Point(8, 0),    // Right point (tip)
                new System.Windows.Point(0, 4)     // Bottom point
            };

            // Add rotation transform to point in direction of movement
            var transformGroup = new System.Windows.Media.TransformGroup();
            transformGroup.Children.Add(new System.Windows.Media.RotateTransform(angleDegrees));
            transformGroup.Children.Add(new System.Windows.Media.TranslateTransform(x, y));
            triangle.RenderTransform = transformGroup;

            // Add glow effect
            triangle.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromRgb(0xF9, 0x73, 0x16),
                Opacity = 0.6,
                ShadowDepth = 0,
                BlurRadius = 4
            };

            trajectoryMarkers.Children.Add(triangle);
        }

        private void ShowLimitLine(System.Windows.Shapes.Line line, System.Windows.Controls.TextBlock label, double limitAngleNormalized, double limitAngleOriginal, string labelText)
        {
            // Calculate limit line position using normalized angle (0-360)
            double angleRadians = limitAngleNormalized * Math.PI / 180.0;
            double x = CENTER_X + MAX_RADIUS * Math.Sin(angleRadians);
            double y = CENTER_Y - MAX_RADIUS * Math.Cos(angleRadians);

            // Update line
            line.X2 = x;
            line.Y2 = y;
            line.Visibility = System.Windows.Visibility.Visible;

            // Update label position
            double labelX = CENTER_X + (MAX_RADIUS + 20) * Math.Sin(angleRadians);
            double labelY = CENTER_Y - (MAX_RADIUS + 20) * Math.Cos(angleRadians);
            
            Canvas.SetLeft(label, labelX - 25);
            Canvas.SetTop(label, labelY - 8);
            // Display original value from AppSettings (e.g., -300° or 300°)
            label.Text = $"{labelText}\n{limitAngleOriginal:F0}\u00B0";
            label.Visibility = System.Windows.Visibility.Visible;
        }

        private void HideLimitLine(System.Windows.Shapes.Line line, System.Windows.Controls.TextBlock label)
        {
            line.Visibility = System.Windows.Visibility.Collapsed;
            label.Visibility = System.Windows.Visibility.Collapsed;
        }

        /// <summary>
        /// Update RMS error display in both Azimuth and Altitude panels
        /// </summary>
        private void UpdateRmsDisplay()
        {
            // Update Azimuth RMS
            txtAzimuthRms.Text = $"{AzimuthRmsError:F4}";
            
            // Color coding based on quality
            if (AzimuthRmsError <= 3.0)
            {
                // Excellent: <= 3" (green)
                txtAzimuthRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
            }
            else if (AzimuthRmsError <= 5.0)
            {
                // Good: 3-5" (yellow-green)
                txtAzimuthRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x84, 0xCC, 0x16));
            }
            else if (AzimuthRmsError <= 10.0)
            {
                // Fair: 5-10" (orange)
                txtAzimuthRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
            }
            else
            {
                // Poor: > 10" (red)
                txtAzimuthRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            }

            // Update Altitude RMS
            txtAltitudeRms.Text = $"{AltitudeRmsError:F4}";
            
            // Color coding based on quality
            if (AltitudeRmsError <= 3.0)
            {
                // Excellent: <= 3" (green)
                txtAltitudeRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
            }
            else if (AltitudeRmsError <= 5.0)
            {
                // Good: 3-5" (yellow-green)
                txtAltitudeRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x84, 0xCC, 0x16));
            }
            else if (AltitudeRmsError <= 10.0)
            {
                // Fair: 5-10" (orange)
                txtAltitudeRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
            }
            else
            {
                // Poor: > 10" (red)
                txtAltitudeRms.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            }
        }

        /// <summary>
        /// Update Mount Status display from MountInfo object (Single Source of Truth)
        /// [SIMPLIFIED] Just read from MountInfo.IsMountEnabled and MountInfo.IsTracking
        /// No need to recalculate - MountInfo already does it in CentralizedPollingManager
        /// Called from UI Timer in ManualControlWindow (NOT from callback to avoid thread issues)
        /// </summary>
        public void UpdateMountStatusFromInfo()
        {
            // Check if MountInfo is available
            if (MountInfo == null)
            {
                // No MountInfo - show Disabled
                txtMountStatus.Text = "Disabled";
                mountStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80)); // Gray
                return;
            }

            // Status 1: Disabled - if mount is disabled (read from MountInfo)
            if (!MountInfo.IsMountEnabled)
            {
                txtMountStatus.Text = "Disabled";
                mountStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80)); // Gray
                return;
            }

            // Check if any command velocity is active
            bool hasCommandVelocity = Math.Abs(AzimuthCommandVelocity) > 0.001 || 
                                     Math.Abs(AltitudeCommandVelocity) > 0.001;

            if (!hasCommandVelocity)
            {
                // Status 2: Enabled - mount enabled but not moving
                txtMountStatus.Text = "Enabled";
                mountStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
                return;
            }

            // Status 3 or 4: Moving - check if tracking (read from MountInfo)
            if (MountInfo.IsTracking)
            {
                // Status 4: Tracking - both axes RMS within sensitivity threshold
                txtMountStatus.Text = "Tracking";
                mountStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)); // Blue
            }
            else
            {
                // Status 3: Slewing - at least one axis RMS exceeds sensitivity
                txtMountStatus.Text = "Slewing";
                mountStatusIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // Orange
            }
        }

        /// <summary>
        /// [DEPRECATED] Old UpdateMountStatus - kept for backward compatibility with old callbacks
        /// Use UpdateMountStatusFromInfo() instead
        /// </summary>
        private void UpdateMountStatus()
        {
            UpdateMountStatusFromInfo();
        }
    }
}


