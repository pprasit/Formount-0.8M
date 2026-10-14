using System;
using System.Collections.ObjectModel;
using System.Windows;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Core
{
    /// <summary>
    /// Simple Log System for ForkMount-WPF
    /// Includes Origin and Type for better UI display
    /// </summary>
    public static class SimpleLogger
    {
        private static ObservableCollection<string> _logs = new ObservableCollection<string>();
        private static readonly object _lockObject = new object();
        public const int MaxLogEntries = 500;

        /// <summary>
        /// Event fired when log is added (Format: timestamp|origin|type|message)
        /// </summary>
        public static event EventHandler<string> LogAdded;

        /// <summary>
        /// Collection of all log entries
        /// </summary>
        public static ObservableCollection<string> Logs
        {
            get
            {
                lock (_lockObject)
                {
                    return _logs;
                }
            }
        }

        /// <summary>
        /// Add log entry (Format: timestamp|origin|type|message with | as separator)
        /// </summary>
        public static void Log(string message, string type = "INFO", string origin = "System")
        {
            string timestamp = NtpTimeService.Instance.GetCurrentTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
            // Format: timestamp|origin|type|message (using | as separator)
            string logEntry = $"{timestamp}|{origin}|{type}|{message}";

            if (Application.Current != null)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    lock (_lockObject)
                    {
                        _logs.Add(logEntry);

                        // Remove old logs if exceeds limit
                        while (_logs.Count > MaxLogEntries)
                        {
                            _logs.RemoveAt(0);
                        }
                    }

                    LogAdded?.Invoke(null, logEntry);

                    // Output to Debug console as well
                    System.Diagnostics.Debug.WriteLine($"[{timestamp}] [{origin}] [{type}] {message}");
                }));
            }
        }

        /// <summary>
        /// Shorthand methods for common log types
        /// </summary>
        public static void Info(string message, string origin = "System") => Log(message, "INFO", origin);
        public static void Error(string message, string origin = "System") => Log(message, "ERROR", origin);
        public static void Success(string message, string origin = "System") => Log(message, "SUCCESS", origin);
        public static void Warning(string message, string origin = "System") => Log(message, "WARNING", origin);
        public static void Command(string message, string origin = "System") => Log(message, "CMD", origin);

        /// <summary>
        /// Clear all log entries
        /// </summary>
        public static void Clear()
        {
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    lock (_lockObject)
                    {
                        _logs.Clear();
                    }
                });
            }
        }
    }
}
