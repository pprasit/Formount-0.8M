using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// NTP Time Synchronization Service
    /// Provides accurate time synchronized from NTP servers for astronomical calculations.
    /// 
    /// IMPORTANT: This service does NOT modify Windows system time.
    /// It maintains an internal offset that is applied to DateTime.UtcNow
    /// to provide accurate time without requiring admin privileges.
    /// 
    /// Usage:
    ///   - Call StartAutoSync() to begin periodic synchronization
    ///   - Use GetCurrentTime() or GetCurrentUtcTime() instead of DateTime.Now/UtcNow
    ///   - All tracking and astronomical calculations should use this service
    /// </summary>
    public class NtpTimeService : IDisposable
    {
        #region Singleton Pattern
        
        private static readonly Lazy<NtpTimeService> _instance = 
            new Lazy<NtpTimeService>(() => new NtpTimeService());
        
        /// <summary>
        /// Singleton instance of NtpTimeService
        /// </summary>
        public static NtpTimeService Instance => _instance.Value;
        
        #endregion
        
        #region Fields
        
        private readonly object _lock = new object();
        private TimeSpan _ntpOffset = TimeSpan.Zero;
        private DateTime _lastSyncTime = DateTime.MinValue;
        private bool _isSynchronized = false;
        private string _ntpServer = "pool.ntp.org";
        private int _syncIntervalMinutes = 60;
        private bool _isAutoSyncEnabled = false;
        private CancellationTokenSource _autoSyncCts;
        private Task _autoSyncTask;
        
        // Stopwatch for high-precision time interpolation
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private DateTime _stopwatchBaseTime;
        
        // Statistics
        private int _syncSuccessCount = 0;
        private int _syncFailureCount = 0;
        private double _lastRoundTripDelay = 0;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Whether time has been successfully synchronized with NTP server
        /// </summary>
        public bool IsSynchronized
        {
            get { lock (_lock) return _isSynchronized; }
        }
        
        /// <summary>
        /// Time offset from local system clock to NTP time
        /// Positive = NTP is ahead of local, Negative = NTP is behind local
        /// </summary>
        public TimeSpan NtpOffset
        {
            get { lock (_lock) return _ntpOffset; }
        }
        
        /// <summary>
        /// Last successful synchronization time (local time)
        /// </summary>
        public DateTime LastSyncTime
        {
            get { lock (_lock) return _lastSyncTime; }
        }
        
        /// <summary>
        /// NTP server address
        /// </summary>
        public string NtpServer
        {
            get { lock (_lock) return _ntpServer; }
            set { lock (_lock) _ntpServer = value; }
        }
        
        /// <summary>
        /// Auto-sync interval in minutes
        /// </summary>
        public int SyncIntervalMinutes
        {
            get { lock (_lock) return _syncIntervalMinutes; }
            set { lock (_lock) _syncIntervalMinutes = Math.Max(1, value); }
        }
        
        /// <summary>
        /// Whether auto-sync is currently enabled and running
        /// </summary>
        public bool IsAutoSyncEnabled
        {
            get { lock (_lock) return _isAutoSyncEnabled; }
        }
        
        /// <summary>
        /// Number of successful sync operations
        /// </summary>
        public int SyncSuccessCount
        {
            get { lock (_lock) return _syncSuccessCount; }
        }
        
        /// <summary>
        /// Number of failed sync operations
        /// </summary>
        public int SyncFailureCount
        {
            get { lock (_lock) return _syncFailureCount; }
        }
        
        /// <summary>
        /// Last round-trip delay in milliseconds
        /// Lower values indicate more accurate sync
        /// </summary>
        public double LastRoundTripDelayMs
        {
            get { lock (_lock) return _lastRoundTripDelay; }
        }
        
        #endregion
        
        #region Constructor
        
        private NtpTimeService()
        {
            // Load settings from AppSettings
            LoadSettings();
        }
        
        #endregion
        
        #region Public Methods - Time Access
        
        /// <summary>
        /// Get current UTC time synchronized with NTP
        /// This is the PRIMARY method for getting accurate time
        /// </summary>
        /// <returns>UTC time adjusted by NTP offset</returns>
        public DateTime GetCurrentUtcTime()
        {
            lock (_lock)
            {
                if (_isSynchronized && _stopwatch.IsRunning)
                {
                    // Use high-precision interpolation from last sync
                    return _stopwatchBaseTime + _stopwatch.Elapsed;
                }
                else
                {
                    // Fallback to system time with offset
                    return DateTime.UtcNow + _ntpOffset;
                }
            }
        }
        
        /// <summary>
        /// Get current local time synchronized with NTP
        /// </summary>
        /// <returns>Local time adjusted by NTP offset</returns>
        public DateTime GetCurrentTime()
        {
            return GetCurrentUtcTime().ToLocalTime();
        }
        
        /// <summary>
        /// Get current time with specific UTC offset (for timezone display)
        /// </summary>
        /// <param name="utcOffsetHours">UTC offset in hours (e.g., +7 for Thailand)</param>
        /// <returns>Time at specified UTC offset</returns>
        public DateTime GetTimeAtOffset(double utcOffsetHours)
        {
            return GetCurrentUtcTime().AddHours(utcOffsetHours);
        }
        
        #endregion
        
        #region Public Methods - Synchronization
        
        /// <summary>
        /// Perform a single NTP synchronization
        /// </summary>
        /// <returns>True if sync was successful</returns>
        public async Task<bool> SyncNowAsync()
        {
            string server;
            lock (_lock)
            {
                server = _ntpServer;
            }
            
            try
            {
                var result = await GetNtpTimeAsync(server);
                
                if (result.HasValue)
                {
                    lock (_lock)
                    {
                        _ntpOffset = result.Value.Offset;
                        _lastSyncTime = DateTime.Now;
                        _isSynchronized = true;
                        _syncSuccessCount++;
                        _lastRoundTripDelay = result.Value.RoundTripDelay.TotalMilliseconds;
                        
                        // Reset stopwatch for high-precision interpolation
                        _stopwatchBaseTime = result.Value.NtpTime;
                        _stopwatch.Restart();
                    }
                    
                    Debug.WriteLine($"[NTP] Sync successful: Offset={_ntpOffset.TotalMilliseconds:F1}ms, RTT={_lastRoundTripDelay:F1}ms");
                    SimpleLogger.Info($"[NTP] Synchronized with {server}: Offset={_ntpOffset.TotalMilliseconds:F1}ms", "NTP");
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _syncFailureCount++;
                }
                
                Debug.WriteLine($"[NTP] Sync failed: {ex.Message}");
                SimpleLogger.Warning($"[NTP] Sync failed with {server}: {ex.Message}", "NTP");
            }
            
            return false;
        }
        
        /// <summary>
        /// Start automatic periodic synchronization
        /// </summary>
        public void StartAutoSync()
        {
            lock (_lock)
            {
                if (_isAutoSyncEnabled)
                    return;
                
                _isAutoSyncEnabled = true;
                _autoSyncCts = new CancellationTokenSource();
            }
            
            _autoSyncTask = Task.Run(async () =>
            {
                // Initial sync
                await SyncNowAsync();
                
                while (!_autoSyncCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        int intervalMinutes;
                        lock (_lock)
                        {
                            intervalMinutes = _syncIntervalMinutes;
                        }
                        
                        await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), _autoSyncCts.Token);
                        await SyncNowAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NTP] Auto-sync error: {ex.Message}");
                    }
                }
            }, _autoSyncCts.Token);
            
            Debug.WriteLine("[NTP] Auto-sync started");
            SimpleLogger.Info("[NTP] Auto-sync started", "NTP");
        }
        
        /// <summary>
        /// Stop automatic synchronization
        /// </summary>
        public void StopAutoSync()
        {
            lock (_lock)
            {
                if (!_isAutoSyncEnabled)
                    return;
                
                _isAutoSyncEnabled = false;
                _autoSyncCts?.Cancel();
            }
            
            Debug.WriteLine("[NTP] Auto-sync stopped");
            SimpleLogger.Info("[NTP] Auto-sync stopped", "NTP");
        }
        
        /// <summary>
        /// Load settings from AppSettings
        /// </summary>
        public void LoadSettings()
        {
            try
            {
                var settings = AppSettings.GetSystemSettings();
                lock (_lock)
                {
                    _ntpServer = settings.NtpServerAddress ?? "pool.ntp.org";
                    _syncIntervalMinutes = Math.Max(1, settings.NtpSyncIntervalMinutes);
                }
                
                Debug.WriteLine($"[NTP] Settings loaded: Server={_ntpServer}, Interval={_syncIntervalMinutes}min");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NTP] Failed to load settings: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Private Methods - NTP Protocol
        
        private struct NtpResult
        {
            public DateTime NtpTime;
            public TimeSpan Offset;
            public TimeSpan RoundTripDelay;
        }
        
        private async Task<NtpResult?> GetNtpTimeAsync(string server, int port = 123, int timeoutMs = 5000)
        {
            // NTP packet is 48 bytes
            byte[] ntpData = new byte[48];
            
            // Set the first byte: LI = 0 (no warning), VN = 4 (version 4), Mode = 3 (client)
            ntpData[0] = 0x23; // 00 100 011
            
            try
            {
                // Resolve hostname
                var addresses = await Dns.GetHostAddressesAsync(server);
                if (addresses.Length == 0)
                    return null;
                
                var ipEndPoint = new IPEndPoint(addresses[0], port);
                
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;
                
                // Record client transmit time (T1)
                DateTime t1 = DateTime.UtcNow;
                
                await socket.ConnectAsync(ipEndPoint);
                await socket.SendAsync(new ArraySegment<byte>(ntpData), SocketFlags.None);
                
                var receiveBuffer = new byte[48];
                int received = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), SocketFlags.None);
                
                // Record client receive time (T4)
                DateTime t4 = DateTime.UtcNow;
                
                if (received < 48)
                    return null;
                
                // Extract server receive time (T2) and transmit time (T3)
                // These are at bytes 32-39 (receive) and 40-47 (transmit)
                DateTime t2 = GetNtpTime(receiveBuffer, 32);
                DateTime t3 = GetNtpTime(receiveBuffer, 40);
                
                // Calculate offset and delay using NTP formula
                // Offset = ((T2 - T1) + (T3 - T4)) / 2
                // Delay = (T4 - T1) - (T3 - T2)
                TimeSpan offset = TimeSpan.FromTicks(((t2 - t1).Ticks + (t3 - t4).Ticks) / 2);
                TimeSpan delay = (t4 - t1) - (t3 - t2);
                
                // Calculate corrected NTP time
                DateTime ntpTime = DateTime.UtcNow + offset;
                
                return new NtpResult
                {
                    NtpTime = ntpTime,
                    Offset = offset,
                    RoundTripDelay = delay
                };
            }
            catch
            {
                return null;
            }
        }
        
        private DateTime GetNtpTime(byte[] ntpData, int offset)
        {
            // NTP timestamp is 8 bytes: 4 bytes for seconds since 1900, 4 bytes for fraction
            ulong intPart = ((ulong)ntpData[offset] << 24) |
                           ((ulong)ntpData[offset + 1] << 16) |
                           ((ulong)ntpData[offset + 2] << 8) |
                           ntpData[offset + 3];
            
            ulong fractPart = ((ulong)ntpData[offset + 4] << 24) |
                             ((ulong)ntpData[offset + 5] << 16) |
                             ((ulong)ntpData[offset + 6] << 8) |
                             ntpData[offset + 7];
            
            // Convert to DateTime
            // NTP epoch is January 1, 1900
            DateTime ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            
            // Add seconds
            DateTime result = ntpEpoch.AddSeconds(intPart);
            
            // Add fractional seconds (fraction is in units of 2^-32 seconds)
            double fraction = (double)fractPart / 0x100000000L;
            result = result.AddSeconds(fraction);
            
            return result;
        }
        
        #endregion
        
        #region IDisposable
        
        private bool _disposed = false;
        
        public void Dispose()
        {
            if (!_disposed)
            {
                StopAutoSync();
                _autoSyncCts?.Dispose();
                _stopwatch.Stop();
                _disposed = true;
            }
        }
        
        #endregion
    }
}
