using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ForkMount_WPF.Engine.Core;
using System.Windows.Media;
using ForkMount_WPF.Engine;

namespace ForkMount_WPF.UI
{
    public partial class SimpleLogPanel : UserControl
    {
        private ObservableCollection<LogEntry> _logEntries;

        public SimpleLogPanel()
        {
            InitializeComponent();
            
            // Skip initialization in Design Mode
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                return;
            }
            
            _logEntries = new ObservableCollection<LogEntry>();
            LogListView.ItemsSource = _logEntries;
            
            // Subscribe to SimpleLogger
            SimpleLogger.LogAdded += OnLogAdded;
            
            Loaded += SimpleLogPanel_Loaded;
        }

        private void SimpleLogPanel_Loaded(object sender, RoutedEventArgs e)
        {
            // Auto scroll to top when loaded
            if (LogListView.Items.Count > 0)
            {
                LogListView.ScrollIntoView(LogListView.Items[0]);
            }
        }

        private void OnLogAdded(object sender, string logText)
        {
            Dispatcher.Invoke(() =>
            {
                // Parse log text format: timestamp|origin|type|message
                var parts = logText.Split('|');
                if (parts.Length >= 4)
                {
                    string timestamp = parts[0];
                    string origin = parts[1];
                    string type = parts[2];
                    string message = parts[3];

                    var logEntry = new LogEntry
                    {
                        Timestamp = timestamp,
                        Origin = origin,
                        OriginColor = GetOriginColor(origin),
                        Type = type,
                        TypeColor = GetLogTypeColor(type),
                        Message = message
                    };

                    _logEntries.Insert(0, logEntry);

                    // Remove old logs if exceeds limit
                    while (_logEntries.Count > SimpleLogger.MaxLogEntries)
                    {
                        _logEntries.RemoveAt(_logEntries.Count - 1);
                    }

                    // Auto scroll to top
                    if (LogListView.Items.Count > 0)
                    {
                        LogListView.ScrollIntoView(LogListView.Items[0]);
                    }
                }
            });
        }

        /// <summary>
        /// Add Log Entry manually (for backward compatibility)
        /// </summary>
        public void AddLog(string type, string message, string origin = "System")
        {
            Dispatcher.Invoke(() =>
            {
                var logEntry = new LogEntry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Origin = origin,
                    OriginColor = GetOriginColor(origin),
                    Type = type,
                    TypeColor = GetLogTypeColor(type),
                    Message = message
                };

                _logEntries.Insert(0, logEntry);

                while (_logEntries.Count > SimpleLogger.MaxLogEntries)
                {
                    _logEntries.RemoveAt(_logEntries.Count - 1);
                }

                if (LogListView.Items.Count > 0)
                {
                    LogListView.ScrollIntoView(LogListView.Items[0]);
                }
            });
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _logEntries.Clear();
            SimpleLogger.Info("Logs cleared");
        }

        private void CopyAllLogs_Click(object sender, RoutedEventArgs e)
        {
            var allLogs = new System.Text.StringBuilder();
            
            foreach (var log in _logEntries)
            {
                allLogs.AppendLine($"[{log.Timestamp}] [{log.Type}] {log.Message}");
            }
            
            if (allLogs.Length > 0)
            {
                Clipboard.SetText(allLogs.ToString());
                MessageBox.Show("Logs were copied to the clipboard successfully.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Get color for Origin
        /// </summary>
        private Brush GetOriginColor(string origin)
        {
            return origin switch
            {
                "Azimuth" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A78BFA")), // Purple
                "Altitude" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60A5FA")), // Blue
                "System" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF")), // Gray
                _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"))
            };
        }

        /// <summary>
        /// Get color for Log Type (compatible with IMount style)
        /// </summary>
        private Brush GetLogTypeColor(string type)
        {
            return type switch
            {
                "CMD" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),     // Blue
                "ERROR" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),   // Red
                "INFO" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),    // Green
                "SUCCESS" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")), // Green
                "WARNING" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD700")), // Yellow
                "Message" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")), // Green
                _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"))
            };
        }
    }

    /// <summary>
    /// Log Entry Model
    /// </summary>
    public class LogEntry
    {
        public string Timestamp { get; set; }
        public string Origin { get; set; }
        public Brush OriginColor { get; set; }
        public string Type { get; set; }
        public Brush TypeColor { get; set; }
        public string Message { get; set; }
    }
}
