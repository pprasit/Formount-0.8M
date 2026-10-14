using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot.Plottables;
using ForkMount_WPF.Engine;
using ForkMount_WPF.Engine.Info;
using System.Collections.Generic;
using System.Linq;

namespace ForkMount_WPF.UI
{
    public partial class AzimuthChartPanel : UserControl
    {
        private readonly DispatcherTimer _chartUpdateTimer;
        private const int MAX_DATA_POINTS = 6000;
        private const double TIME_WINDOW_SECONDS = 60.0;
        
        private string _currentDataType = "Position (deg)";
        private string _currentDataType2 = "None";
        private Scatter _scatterPlot;
        private Scatter _scatterPlot2;
        private List<double> _timeData = new List<double>();
        private List<double> _valueData = new List<double>();
        private List<double> _valueData2 = new List<double>();
        private double _latestTimestamp = 0;
        
        // [NEW] MountInfo reference for corrected position
        private MountInfo _mountInfo;

        private readonly string[] _availableDataTypes = new[]
        {
            "Position (deg)",
            "Velocity (deg/s)",
            "Command Velocity (deg/s)",
            "Current (A)"
        };

        public AzimuthChartPanel()
        {
            InitializeComponent();
            
            // ? ??? initialize ????????? Design Mode
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                return;
            }
            
            _chartUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // 60 FPS
            };
            _chartUpdateTimer.Tick += ChartUpdateTimer_Tick;
            _chartUpdateTimer.Start();
            
            Loaded += AzimuthChartPanel_Loaded;
        }
        
        /// <summary>
        /// Set MountInfo reference for displaying corrected position
        /// Call this from Azimuth_Controller during initialization
        /// </summary>
        public void SetMountInfo(MountInfo mountInfo)
        {
            _mountInfo = mountInfo;
        }

        private void AzimuthChartPanel_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeChart();
        }

        private void InitializeChart()
        {
            if (wpfPlot == null) return;
            
            wpfPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
            wpfPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#252526");
            
            wpfPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2D2D30");
            wpfPlot.Plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#25262A");
            wpfPlot.Plot.Grid.MajorLineWidth = 0.8f;
            
            wpfPlot.Plot.Axes.Bottom.Label.Text = SafeLabel("Time (seconds ago)");
            wpfPlot.Plot.Axes.Bottom.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Bottom.Label.FontSize = 14;
            wpfPlot.Plot.Axes.Bottom.Label.Bold = false;
            wpfPlot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
            wpfPlot.Plot.Axes.Bottom.MajorTickStyle.Color = ScottPlot.Color.FromHex("#6B7280");
            wpfPlot.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
            
            // ????????????? X: 60 (????) ? 0 (???)
            wpfPlot.Plot.Axes.Bottom.Min = 60;
            wpfPlot.Plot.Axes.Bottom.Max = 0;
            
            // Left Y-axis
            wpfPlot.Plot.Axes.Left.Label.Text = SafeLabel(_currentDataType);
            wpfPlot.Plot.Axes.Left.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Left.Label.FontSize = 14;
            wpfPlot.Plot.Axes.Left.Label.Bold = false;
            wpfPlot.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Left.TickLabelStyle.FontSize = 11;
            wpfPlot.Plot.Axes.Left.MajorTickStyle.Color = ScottPlot.Color.FromHex("#6B7280");
            wpfPlot.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
            wpfPlot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic() { LabelFormatter = (d) => d.ToString("F3") };
            
            // Right Y-axis
            wpfPlot.Plot.Axes.Right.Label.Text = SafeLabel(_currentDataType2);
            wpfPlot.Plot.Axes.Right.Label.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Right.Label.FontSize = 14;
            wpfPlot.Plot.Axes.Right.Label.Bold = false;
            wpfPlot.Plot.Axes.Right.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#D1D5DB");
            wpfPlot.Plot.Axes.Right.TickLabelStyle.FontSize = 11;
            wpfPlot.Plot.Axes.Right.MajorTickStyle.Color = ScottPlot.Color.FromHex("#6B7280");
            wpfPlot.Plot.Axes.Right.FrameLineStyle.Color = ScottPlot.Color.FromHex("#4B5563");
            wpfPlot.Plot.Axes.Right.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic() { LabelFormatter = (d) => d.ToString("F3") };
            
            _scatterPlot = wpfPlot.Plot.Add.Scatter(new double[] { }, new double[] { });
            _scatterPlot.Axes.YAxis = wpfPlot.Plot.Axes.Left;
            UpdateLineColor();
            _scatterPlot.LineWidth = 2.5f;
            _scatterPlot.MarkerSize = 0;
            
            _scatterPlot2 = wpfPlot.Plot.Add.Scatter(new double[] { }, new double[] { });
            _scatterPlot2.Axes.YAxis = wpfPlot.Plot.Axes.Right;
            UpdateLineColor2();
            _scatterPlot2.LineWidth = 2.5f;
            _scatterPlot2.MarkerSize = 0;
            
            UpdateDropdownItems();
            
            // Set initial ComboBox colors
            cmbDataType.Foreground = GetBrushForDataType(_currentDataType);
            cmbDataType2.Foreground = GetBrushForDataType(_currentDataType2);
            
            SafeRefresh();
        }

        private void UpdateLineColor()
        {
            if (_scatterPlot == null) return;
            
            _scatterPlot.Color = _currentDataType switch
            {
                "Position (deg)" => ScottPlot.Color.FromHex("#60A5FA"),
                "Velocity (deg/s)" => ScottPlot.Color.FromHex("#34D399"),
                "Command Velocity (deg/s)" => ScottPlot.Color.FromHex("#F472B6"),
                "Current (A)" => ScottPlot.Color.FromHex("#FBBF24"),
                _ => ScottPlot.Color.FromHex("#60A5FA")
            };
        }

        private void UpdateLineColor2()
        {
            if (_scatterPlot2 == null) return;
            
            _scatterPlot2.Color = _currentDataType2 switch
            {
                "Position (deg)" => ScottPlot.Color.FromHex("#60A5FA"),
                "Velocity (deg/s)" => ScottPlot.Color.FromHex("#34D399"),
                "Command Velocity (deg/s)" => ScottPlot.Color.FromHex("#F472B6"),
                "Current (A)" => ScottPlot.Color.FromHex("#FBBF24"),
                _ => ScottPlot.Color.FromHex("#9CA3AF")
            };
        }

        private void UpdateDropdownItems()
        {
            if (cmbDataType == null || cmbDataType2 == null) return;

            var selectedType1 = _currentDataType;
            var selectedType2 = _currentDataType2;

            // Update dropdown 1
            cmbDataType.SelectionChanged -= CmbDataType_SelectionChanged;
            cmbDataType.Items.Clear();
            foreach (var type in _availableDataTypes)
            {
                if (type != selectedType2)
                {
                    var item = new ComboBoxItem { Content = type };
                    item.Foreground = GetBrushForDataType(type);
                    if (type == selectedType1)
                        item.IsSelected = true;
                    cmbDataType.Items.Add(item);
                }
            }
            cmbDataType.SelectionChanged += CmbDataType_SelectionChanged;

            // Update dropdown 2
            cmbDataType2.SelectionChanged -= CmbDataType2_SelectionChanged;
            cmbDataType2.Items.Clear();
            var noneItem = new ComboBoxItem { Content = "None", IsSelected = selectedType2 == "None" };
            noneItem.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175));
            cmbDataType2.Items.Add(noneItem);
            foreach (var type in _availableDataTypes)
            {
                if (type != selectedType1)
                {
                    var item = new ComboBoxItem { Content = type };
                    item.Foreground = GetBrushForDataType(type);
                    if (type == selectedType2)
                        item.IsSelected = true;
                    cmbDataType2.Items.Add(item);
                }
            }
            cmbDataType2.SelectionChanged += CmbDataType2_SelectionChanged;
        }

        /// <summary>
        /// ?????????????????????
        /// </summary>
        public void AddDataPoint(double positionDeg, double velocityDeg, double commandVelocityDeg, double currentA, double timestamp)
        {
            if (_scatterPlot == null || wpfPlot == null)
                return;
            
            double value = GetDataValue(positionDeg, velocityDeg, commandVelocityDeg, currentA, _currentDataType);
            double value2 = _currentDataType2 != "None" ? GetDataValue(positionDeg, velocityDeg, commandVelocityDeg, currentA, _currentDataType2) : 0;
            _latestTimestamp = timestamp;
            
            // Update Real-time Values Display
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // [NEW] Display Corrected Position from MountInfo (not calculated here!)
                if (_mountInfo != null)
                {
                    txtActPos.Text = _mountInfo.Act_Azimuth_Pos_Correct.ToString("F3");
                }
                else
                {
                    txtActPos.Text = positionDeg.ToString("F3"); // Fallback
                }
                
                txtActVel.Text = velocityDeg.ToString("F3");
                txtActCurrent.Text = currentA.ToString("F3");
            }));
            
            _timeData.Add(timestamp);
            _valueData.Add(value);
            _valueData2.Add(value2);
            
            // Remove data older than TIME_WINDOW_SECONDS
            while (_timeData.Count > 0 && (timestamp - _timeData[0]) > TIME_WINDOW_SECONDS)
            {
                _timeData.RemoveAt(0);
                _valueData.RemoveAt(0);
                _valueData2.RemoveAt(0);
            }
            
            // Limit total data points
            if (_timeData.Count > MAX_DATA_POINTS)
            {
                int excess = _timeData.Count - MAX_DATA_POINTS;
                _timeData.RemoveRange(0, excess);
                _valueData.RemoveRange(0, excess);
                _valueData2.RemoveRange(0, excess);
            }
            
            UpdatePlot();
        }

        private void UpdatePlot()
        {
            if (_timeData.Count == 0) return;
            
            // Convert to seconds ago: latest data (0, now), oldest data (60, past)
            double[] relativeTime = new double[_timeData.Count];
            for (int i = 0; i < _timeData.Count; i++)
            {
                relativeTime[i] = _latestTimestamp - _timeData[i];
            }
            
            wpfPlot.Plot.Remove(_scatterPlot);
            _scatterPlot = wpfPlot.Plot.Add.Scatter(relativeTime, _valueData.ToArray());
            _scatterPlot.Axes.YAxis = wpfPlot.Plot.Axes.Left;
            UpdateLineColor();
            _scatterPlot.LineWidth = 2.5f;
            _scatterPlot.MarkerSize = 0;
            
            if (_currentDataType2 != "None")
            {
                wpfPlot.Plot.Remove(_scatterPlot2);
                _scatterPlot2 = wpfPlot.Plot.Add.Scatter(relativeTime, _valueData2.ToArray());
                
                // Check if both data types have the same unit
                bool sameUnits = HaveSameUnits(_currentDataType, _currentDataType2);
                
                if (sameUnits)
                {
                    // Use LEFT axis for both (same scale)
                    _scatterPlot2.Axes.YAxis = wpfPlot.Plot.Axes.Left;
                    
                    // Hide right axis labels and ticks
                    wpfPlot.Plot.Axes.Right.Label.Text = SafeLabel(string.Empty);
                    wpfPlot.Plot.Axes.Right.TickLabelStyle.IsVisible = false;
                    wpfPlot.Plot.Axes.Right.MajorTickStyle.Width = 0;
                }
                else
                {
                    // Use RIGHT axis for data type 2 (different scale)
                    _scatterPlot2.Axes.YAxis = wpfPlot.Plot.Axes.Right;
                    
                    // Show right axis labels and ticks
                    wpfPlot.Plot.Axes.Right.Label.Text = SafeLabel(_currentDataType2);
                    wpfPlot.Plot.Axes.Right.TickLabelStyle.IsVisible = true;
                    wpfPlot.Plot.Axes.Right.MajorTickStyle.Width = 1;
                }
                
                UpdateLineColor2();
                _scatterPlot2.LineWidth = 2.5f;
                _scatterPlot2.MarkerSize = 0;
            }
            else
            {
                wpfPlot.Plot.Remove(_scatterPlot2);
                _scatterPlot2 = wpfPlot.Plot.Add.Scatter(new double[] { }, new double[] { });
                _scatterPlot2.Axes.YAxis = wpfPlot.Plot.Axes.Right;
                
                // Hide right axis when no second data type
                wpfPlot.Plot.Axes.Right.Label.Text = SafeLabel(string.Empty);
                wpfPlot.Plot.Axes.Right.TickLabelStyle.IsVisible = false;
                wpfPlot.Plot.Axes.Right.MajorTickStyle.Width = 0;
            }
            
            // AutoScale
            wpfPlot.Plot.Axes.AutoScale();
            
            // Force X-axis range to 60 (past) to 0 (now)
            wpfPlot.Plot.Axes.Bottom.Min = 60;
            wpfPlot.Plot.Axes.Bottom.Max = 0;
        }

        private double GetDataValue(double positionDeg, double velocityDeg, double commandVelocityDeg, double currentA, string dataType)
        {
            return dataType switch
            {
                "Position (deg)" => positionDeg,
                "Velocity (deg/s)" => velocityDeg,
                "Command Velocity (deg/s)" => commandVelocityDeg,
                "Current (A)" => currentA,
                _ => 0.0
            };
        }

        private string GetUnitFromDataType(string dataType)
        {
            return dataType switch
            {
                "Position (deg)" => "deg",
                "Velocity (deg/s)" => "deg/s",
                "Command Velocity (deg/s)" => "deg/s",
                "Current (A)" => "A",
                _ => ""
            };
        }

        private bool HaveSameUnits(string dataType1, string dataType2)
        {
            return GetUnitFromDataType(dataType1) == GetUnitFromDataType(dataType2);
        }

        private System.Windows.Media.Brush GetBrushForDataType(string dataType)
        {
            return dataType switch
            {
                "Position (deg)" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 165, 250)),
                "Velocity (deg/s)" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153)),
                "Command Velocity (deg/s)" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 114, 182)),
                "Current (A)" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36)),
                _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175))
            };
        }

        private void ChartUpdateTimer_Tick(object sender, EventArgs e)
        {
            if (wpfPlot != null && _timeData.Count > 0)
            {
                SafeRefresh();
            }
        }

        private void CmbDataType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbDataType.SelectedItem is ComboBoxItem selectedItem)
            {
                _currentDataType = selectedItem.Content.ToString();
                
                // Set ComboBox text color to match graph line color
                cmbDataType.Foreground = GetBrushForDataType(_currentDataType);
                
                if (wpfPlot != null && _scatterPlot != null)
                {
                    wpfPlot.Plot.Axes.Left.Label.Text = SafeLabel(_currentDataType);
                    UpdateLineColor();
                    UpdateDropdownItems();
                    SafeRefresh();
                }
            }
        }

        private void CmbDataType2_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbDataType2.SelectedItem is ComboBoxItem selectedItem)
            {
                _currentDataType2 = selectedItem.Content.ToString();
                
                // Set ComboBox text color to match graph line color
                cmbDataType2.Foreground = GetBrushForDataType(_currentDataType2);
                
                if (wpfPlot != null && _scatterPlot2 != null)
                {
                    wpfPlot.Plot.Axes.Right.Label.Text = SafeLabel(_currentDataType2);
                    UpdateLineColor2();
                    UpdateDropdownItems();
                    SafeRefresh();
                }
            }
        }

        /// <summary>
        /// Update sensor status indicators on chart panel
        /// </summary>
        public void UpdateSensorIndicators(bool ccwActive, bool cwActive)
        {
            if (ccwSensorIndicator != null)
            {
                ccwSensorIndicator.Background = ccwActive
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)) // Green
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128)); // Gray
            }

            if (cwSensorIndicator != null)
            {
                cwSensorIndicator.Background = cwActive
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)) // Green
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128)); // Gray
            }
        }

        /// <summary>
        /// Clear all plot data
        /// </summary>
        private void BtnClearPlot_Click(object sender, RoutedEventArgs e)
        {
            _timeData.Clear();
            _valueData.Clear();
            _valueData2.Clear();
            _latestTimestamp = 0;
            
            if (wpfPlot != null)
            {
                wpfPlot.Plot.Remove(_scatterPlot);
                wpfPlot.Plot.Remove(_scatterPlot2);
                
                _scatterPlot = wpfPlot.Plot.Add.Scatter(new double[] { }, new double[] { });
                _scatterPlot.Axes.YAxis = wpfPlot.Plot.Axes.Left;
                UpdateLineColor();
                _scatterPlot.LineWidth = 2.5f;
                _scatterPlot.MarkerSize = 0;
                
                _scatterPlot2 = wpfPlot.Plot.Add.Scatter(new double[] { }, new double[] { });
                _scatterPlot2.Axes.YAxis = wpfPlot.Plot.Axes.Right;
                UpdateLineColor2();
                _scatterPlot2.LineWidth = 2.5f;
                _scatterPlot2.MarkerSize = 0;
                
                SafeRefresh();
            }
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
