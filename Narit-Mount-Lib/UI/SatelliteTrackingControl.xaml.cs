using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Control;  // [PHASE 1] For KalmanFilter1D
using Microsoft.Win32;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    public partial class SatelliteTrackingControl : UserControl
    {
        private ObservableCollection<SatelliteInfo> _satelliteInfoList;
        private List<SatelliteTracker> _displayedTrackers;
        private List<SatelliteTracker> _allTrackers;
        private DispatcherTimer _updateTimer;
        private DispatcherTimer _fastUpdateTimer;
        private DispatcherTimer _scanTimer;
        private ObserverLocation _observerLocation;
        
        private SatelliteTracking _trackingEngine;
        private AzimuthInfo _azimuthInfo;
        private AltitudeInfo _altitudeInfo;
        private CentralizedPollingManager _pollingManager; // [CRITICAL FIX] Use CentralizedPollingManager instead of AltitudeDrive
        
        private const int SCAN_POOL_SIZE = 5000;
        private const double MIN_ELEVATION = 10.0;
        private const double MAX_ELEVATION = 87.0;
        private const double MIN_VELOCITY_THRESHOLD = 0.3;
        private const int MAX_DISPLAYED_SATELLITES = 10;
        private const int SCAN_INTERVAL_SECONDS = 5;
        private bool _hasAutoScanned = false;
        private bool _isScanning = false;
        
        // [SCAN TRACKING] Track last scan time for logging purposes
        private DateTime _lastScanTime = DateTime.MinValue;
        
        // [MONGODB] Service for loading TLE from database
        private MongoDBService _mongoDBService;

        // Current satellite type for filtering
        private string _currentSatelliteType = "NARIT-ACTIVE";

        // Hourly refresh timer: re-fetch from NARIT API every 1 hour
        private DispatcherTimer _refreshTimer;
        
        // [NEW] Velocity tracking timer for satellite #1 (15ms = 66.67Hz)
        private DispatcherTimer _velocityTrackingTimer;
        private bool _isTracking = false;
        
        // [AUTO-RESUME] Flag to enable automatic satellite switching when current becomes invalid
        private bool _autoResumeEnabled => chkAutoResume?.IsChecked ?? false;
        
        // Flags to prevent duplicate warning logs
        private bool _lastCollisionPreventCW = false;
        private bool _lastCollisionPreventCCW = false;
        private bool _lastVelocityReversalCW = false;
        private bool _lastVelocityReversalCCW = false;
        
        // [NEW] Flag to prevent re-scanning while waiting for new satellite list
        private bool _pendingSatelliteSwitch = false;
        
        // [NEW] Debounce counter for satellite #1 validity (prevent flickering)
        private int _satellite1InvalidCounter = 0;
        private const int INVALID_COUNT_THRESHOLD = 10; // Must be invalid 10 times in a row (1000ms = 1 second) before marking as invalid
        
        private System.Diagnostics.Stopwatch _masterStopwatch = new();
        
        // [NEW] Selected satellite index from DataGrid (-1 = none selected)
        private int _selectedSatelliteIndex = -1;

        /// <summary>
        /// Event fired when a satellite is selected from the list (for Sky Chart display)
        /// </summary>
        public event EventHandler<SatelliteTracker> SatelliteSelected;
        
        /// <summary>
        /// Public property to get selected satellite (for Sky Chart path display)
        /// </summary>
        public SatelliteTracker SelectedSatelliteTracker =>
            (_selectedSatelliteIndex >= 0 && _selectedSatelliteIndex < _displayedTrackers.Count) 
                ? _displayedTrackers[_selectedSatelliteIndex] 
                : null;

        /// <summary>
        /// Public property to get current tracking satellite (satellite #1)
        /// </summary>
        public SatelliteTracker CurrentSatelliteTracker =>
            _displayedTrackers != null && _displayedTrackers.Count > 0 ? _displayedTrackers[0] : null;

        /// <summary>
        /// Public property to get observer location
        /// </summary>
        public ObserverLocation ObserverLocation => _observerLocation;

        /// <summary>
        /// Public property to check if satellite tracking is currently active
        /// </summary>
        public bool IsTracking => _isTracking;

        public SatelliteTrackingControl()
        {
            InitializeComponent();
            _satelliteInfoList = new ObservableCollection<SatelliteInfo>();
            _displayedTrackers = new List<SatelliteTracker>();
            _allTrackers = new List<SatelliteTracker>();
            dgSatellites.ItemsSource = _satelliteInfoList;
            
            // Load Observer Location from AppSettings
            LoadObserverLocation();

            // Fast update timer: Update satellite #1 every 100ms and check validity
            _fastUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _fastUpdateTimer.Tick += FastUpdateTimer_Tick;

            // Update timer: Update satellites #2-10 every 500ms
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _updateTimer.Tick += UpdateTimer_Tick;

            // Manual scan timer (disabled by default - only used for initial scan)
            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SCAN_INTERVAL_SECONDS) };
            _scanTimer.Tick += ScanTimer_Tick;
            
            // Velocity tracking timer: Send velocity commands every 15ms (66.67Hz)
            _velocityTrackingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            _velocityTrackingTimer.Tick += VelocityTrackingTimer_Tick;

            // Hourly TLE refresh timer
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _refreshTimer.Tick += async (s, e) => await FetchAndLoadTLEAsync();

            IsVisibleChanged += SatelliteTrackingControl_IsVisibleChanged;
            Loaded += SatelliteTrackingControl_Loaded;
            Unloaded += SatelliteTrackingControl_Unloaded;
        }

        /// <summary>
        /// Initialize tracking engine with axis references
        /// [CRITICAL FIX] Accept CentralizedPollingManager instead of AltitudeDrive
        /// </summary>
        public void InitializeTrackingEngine(AzimuthInfo azimuthInfo, AltitudeInfo altitudeInfo, CentralizedPollingManager pollingManager)
        {
            _azimuthInfo = azimuthInfo;
            _altitudeInfo = altitudeInfo;
            _pollingManager = pollingManager; // [CRITICAL FIX] Store CentralizedPollingManager reference
            _trackingEngine = new SatelliteTracking(azimuthInfo, altitudeInfo, pollingManager);
            
            // [NEW] Subscribe to satellite untrackable event
            _trackingEngine.OnSatelliteUntrackable += TrackingEngine_OnSatelliteUntrackable;
            
            SimpleLogger.Info("[TRACK INIT] Tracking engine initialized with CentralizedPollingManager", "SatTrack");
        }
        
        /// <summary>
        /// Handle satellite untrackable event (limit exceeded, below horizon, etc.)
        /// </summary>
        private void TrackingEngine_OnSatelliteUntrackable(object sender, string reason)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    SimpleLogger.Warning($"[AUTO-SWITCH] Current satellite untrackable: {reason}", "SatTrack");
                    
                    // If Auto-Resume is enabled, find and switch to next valid satellite
                    if (_autoResumeEnabled)
                    {
                        SimpleLogger.Info("[AUTO-SWITCH] Auto-Resume enabled - searching for new satellite...", "SatTrack");
                        
                        // Stop current tracking
                        _isTracking = false;
                        _velocityTrackingTimer?.Stop();
                        _trackingEngine?.StopTracking();
                        
                        // Stop drives
                        if (_pollingManager != null)
                        {
                            _pollingManager.SetAzimuthCommandVelocity(0.0);
                            _pollingManager.SetAltitudeCommandVelocity(0.0);
                        }
                        
                        // Trigger a scan to find new valid satellites (with auto-resume)
                        ScanForFastestSatellites(autoResumeTracking: true);
                        
                        // UI feedback
                        txtStatus.Text = $"Satellite untrackable ({reason}) - Auto-switching...";
                    }
                    else
                    {
                        // Just stop tracking
                        SimpleLogger.Info("[AUTO-SWITCH] Auto-Resume disabled - stopping tracking", "SatTrack");
                        
                        _isTracking = false;
                        _velocityTrackingTimer?.Stop();
                        _trackingEngine?.StopTracking();
                        
                        // Notify TrackingManager that tracking has stopped
                        TrackingManager.Instance.StopTracking(TrackingObjectType.Satellite);
                        
                        // Stop drives
                        if (_pollingManager != null)
                        {
                            _pollingManager.SetAzimuthCommandVelocity(0.0);
                            _pollingManager.SetAltitudeCommandVelocity(0.0);
                        }
                        
                        // Update UI
                        btnStartTrack.Content = "Start Track";
                        btnStartTrack.Background = System.Windows.Media.Brushes.Green;
                        txtStatus.Text = $"Tracking stopped: {reason}";
                    }
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[AUTO-SWITCH ERROR] {ex.Message}", "SatTrack");
                }
            });
        }

        private void SatelliteTrackingControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // [MONGODB] TLE loading is now handled by ManualControlWindow.xaml.cs
            // This event handler is kept for future tab visibility management
            // Do not auto-load here to avoid duplicate loading
        }

        private void SatelliteTrackingControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Resume timers when tab becomes visible (after Unload)
            if (_hasAutoScanned && _allTrackers.Count > 0)
            {
                if (_fastUpdateTimer != null && !_fastUpdateTimer.IsEnabled)
                {
                    _fastUpdateTimer.Start();
                    SimpleLogger.Info("[TAB] Fast update timer resumed", "SatTrack");
                }
                
                if (_updateTimer != null && !_updateTimer.IsEnabled)
                {
                    _updateTimer.Start();
                    SimpleLogger.Info("[TAB] Update timer resumed", "SatTrack");
                }
            }
        }

        private void SatelliteTrackingControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Pause timers to save CPU when tab is not visible
            _fastUpdateTimer?.Stop();
            _updateTimer?.Stop();
            _scanTimer?.Stop();
            _velocityTrackingTimer?.Stop();
            _refreshTimer?.Stop();
            SimpleLogger.Info("[TAB] Timers paused (tab unloaded)", "SatTrack");
        }

        

        

        /// <summary>
        /// Fetch TLE from NARIT API, save to MongoDB, and initialize trackers.
        /// Called on window open and every 1 hour by the refresh timer.
        /// </summary>
        public async Task FetchAndLoadTLEAsync()
        {
            var progress = new Progress<string>(msg =>
            {
                txtStatus.Text = msg;
                txtStatus.Foreground = System.Windows.Media.Brushes.Orange;
            });

            string tleContent = await NaritTLEService.FetchTLEAsync(_currentSatelliteType, progress);

            if (string.IsNullOrWhiteSpace(tleContent))
            {
                txtStatus.Text = "[NARIT TLE] Fetch failed – check internet connection";
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            if (_mongoDBService == null)
                _mongoDBService = new MongoDBService("mongodb://localhost:27017", "NEOs");

            await _mongoDBService.SaveTLEDataAsync(_currentSatelliteType, tleContent);
            await LoadTLEFromMongoDBAsync();

            if (!_refreshTimer.IsEnabled)
                _refreshTimer.Start();
        }

        /// <summary>
        /// Load TLE data from MongoDB database by selected satellite type.
        /// </summary>
        public async Task LoadTLEFromMongoDBAsync()
        {
            try
            {
                txtStatus.Text = $"[MongoDB] Connecting ({_currentSatelliteType})...";
                txtStatus.Foreground = System.Windows.Media.Brushes.Orange;

                // Initialize MongoDB service if not already initialized
                if (_mongoDBService == null)
                {
                    _mongoDBService = new MongoDBService("mongodb://localhost:27017", "NEOs");
                }

                // Test connection
                bool isConnected = await _mongoDBService.TestConnectionAsync();
                if (!isConnected)
                {
                    txtStatus.Text = "[MongoDB] Failed to connect to database";
                    txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                    SimpleLogger.Error("[MongoDB] Connection test failed", "SatTrack");
                    return;
                }

                txtStatus.Text = $"[MongoDB] Loading {_currentSatelliteType}...";
                txtStatus.Foreground = System.Windows.Media.Brushes.Orange;

                // Load TLE data from MongoDB by type
                var tleData = await _mongoDBService.LoadTLEDataByTypeAsync(_currentSatelliteType);
                
                if (tleData == null || tleData.Count == 0)
                {
                    txtStatus.Text = $"[MongoDB] No TLE data found (TYPE='{_currentSatelliteType}')";
                    txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                    SimpleLogger.Warning($"[MongoDB] No TLE data found for TYPE='{_currentSatelliteType}'", "SatTrack");
                    return;
                }

                SimpleLogger.Info($"[MongoDB] Loaded {tleData.Count} TLE entries for TYPE='{_currentSatelliteType}'", "SatTrack");

                // Stop timers and clear existing data
                _updateTimer.Stop();
                _fastUpdateTimer.Stop();
                _allTrackers.Clear();
                _satelliteInfoList.Clear();

                txtStatus.Text = $"[MongoDB] Initializing {tleData.Count} satellites...";
                int successCount = 0;
                int failCount = 0;
                int progressInterval = Math.Max(tleData.Count / 10, 10);

                SimpleLogger.Info($"[MongoDB] Starting initialization of {tleData.Count} satellites...", "SatTrack");

                await Task.Run(() =>
                {
                    for (int i = 0; i < tleData.Count; i++)
                    {
                        try
                        {
                            var tle = tleData[i];

                            var tracker = new SatelliteTracker(tle);
                            bool initSuccess = tracker.Initialize();
                            
                            if (initSuccess)
                            {
                                _allTrackers.Add(tracker);
                                successCount++;
                            }
                            else
                            {
                                failCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            if (failCount <= 3) // Log first 3 errors only
                            {
                                SimpleLogger.Error($"[MongoDB] Error initializing satellite #{i}: {ex.Message}", "SatTrack");
                            }
                        }

                        if ((i + 1) % progressInterval == 0)
                        {
                            int percent = (i + 1) * 100 / tleData.Count;
                            Dispatcher.Invoke(() => 
                            { 
                                txtStatus.Text = $"[MongoDB] Initializing... {percent}% ({i + 1}/{tleData.Count})"; 
                            });
                        }
                    }
                });

                SimpleLogger.Info($"[MongoDB] Initialization complete: Success={successCount}, Failed={failCount}", "SatTrack");

                txtStatus.Text = $"[MongoDB] Initialized {successCount} satellites. Starting search...";
                txtStatus.Foreground = System.Windows.Media.Brushes.Green;

                if (successCount > 0)
                {
                    _hasAutoScanned = true;
                    await Task.Delay(500);
                    UpdateObserverLocation();
                    InitializeDisplay();
                    _masterStopwatch.Restart();
                    _fastUpdateTimer.Start();
                    _updateTimer.Start();

                    SimpleLogger.Info($"[MongoDB] Successfully loaded and initialized {successCount} satellites", "SatTrack");
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"[MongoDB ERROR] {ex.Message}";
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                SimpleLogger.Error($"[MongoDB] Failed to load TLE data: {ex.Message}", "SatTrack");
            }
        }

        private void UpdateObserverLocation()
        {
            try
            {
                // Load from AppSettings instead of UI controls
                var location = AppSettings.GetObserverLocation();
                _observerLocation = new ObserverLocation(
                    location.Latitude_Deg,
                    location.Longitude_Deg,
                    location.Altitude_Meters
                );
                
                SimpleLogger.Info($"[UPDATE] Observer Location: Lat={location.Latitude_Deg:F4}, Lon={location.Longitude_Deg:F4}, Alt={location.Altitude_Meters:F0}m", "SatTrack");
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Invalid observer location!";
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                SimpleLogger.Error($"[UPDATE] Failed to load Observer Location: {ex.Message}", "SatTrack");
            }
        }

        private void InitializeDisplay()
        {
            dgSatellites.Items.SortDescriptions.Clear();
            _satelliteInfoList.Clear();
            _displayedTrackers.Clear();
            
            ScanForFastestSatellites();
        }

        private void ScanTimer_Tick(object sender, EventArgs e)
        {
            // No longer auto-scan every 5 seconds
            // Only used for manual refresh if needed
            if (_isScanning) return;
            ScanForFastestSatellites();
        }

        private async void ScanForFastestSatellites(bool autoResumeTracking = false)
        {
            if (_isScanning) return;
            
            // [SCAN PROTECTION] If current satellite #1 is still valid, don't scan
            // This prevents unnecessary scans when satellite is still trackable
            // IMPORTANT: Use REAL-TIME data, not cached list data!
            if (autoResumeTracking && _displayedTrackers.Count > 0)
            {
                DateTime now = DateTime.UtcNow;
                
                // Calculate real-time topocentric coordinates
                var topo0 = _displayedTrackers[0].GetTopocentric(_observerLocation, now);
                var topo1 = _displayedTrackers[0].GetTopocentric(_observerLocation, now.AddSeconds(0.5));
                
                // Calculate real-time velocity
                double azVel = (topo1.Azimuth - topo0.Azimuth) / 0.5;
                if (Math.Abs(azVel) > 180.0) azVel = azVel > 0 ? azVel - 360.0 : azVel + 360.0;
                double elVel = (topo1.Elevation - topo0.Elevation) / 0.5;
                double rawMaxVel = Math.Max(Math.Abs(azVel), Math.Abs(elVel));
                
                // Check validity using real-time data (same logic as FastUpdateTimer)
                bool isElevationOk = topo0.Elevation >= MIN_ELEVATION && topo0.Elevation <= MAX_ELEVATION;
                bool isVelocityOk = rawMaxVel >= MIN_VELOCITY_THRESHOLD && rawMaxVel <= 10.0;
                
                if (isElevationOk && isVelocityOk)
                {
                    SimpleLogger.Warning($"[SCAN BLOCKED] Current satellite {_displayedTrackers[0].Name} is still valid (El={topo0.Elevation:F1}°, Vel={rawMaxVel:F3} deg/s) - continuing tracking", "SatTrack");
                    return;
                }
                else
                {
                    SimpleLogger.Info($"[SCAN ALLOWED] Current satellite {_displayedTrackers[0].Name} is INVALID (El={topo0.Elevation:F1}° OK:{isElevationOk}, Vel={rawMaxVel:F3} OK:{isVelocityOk}) - scanning for new satellite", "SatTrack");
                }
            }
            
            try
            {
                _isScanning = true;
                _lastScanTime = DateTime.Now;
                
                // [REMOVED] Don't show "Scanning..." status - it causes flickering
                // txtStatus.Text = $"Scanning {_allTrackers.Count} satellites...";
                // txtStatus.Foreground = System.Windows.Media.Brushes.Orange;
                
                var result = await Task.Run(() => ScanSatellitesAsync(autoResumeTracking));
                UpdateDisplayWithScanResults(result, autoResumeTracking);
            }
            finally
            {
                _isScanning = false;
            }
        }

        private (List<(SatelliteTracker, double, double, double, TopocentricCoordinates, bool)>, int, int, int) ScanSatellitesAsync(bool autoResumeTracking = false)
        {
            DateTime now = NtpTimeService.Instance.GetCurrentUtcTime();
            var trackableCandidates = new List<(SatelliteTracker tracker, double azVel, double elVel, double maxVel, TopocentricCoordinates topo, bool isTrackable)>();
            var visibleOnlyCandidates = new List<(SatelliteTracker tracker, double azVel, double elVel, double maxVel, TopocentricCoordinates topo, bool isTrackable)>();
            int scanned = 0, visible = 0, fastEnough = 0;
            const double DELTA_TIME_SEC = 1.0;
            const double MIN_DURATION_MINUTES = 3.0;
            
            // [NEW] Different filtering based on satellite type
            // SpaceTrackLEO: Use velocity threshold + duration check (fast moving LEO satellites)
            // Other types: Just check if visible above horizon (slower moving satellites)
            bool useLeoFiltering = _currentSatelliteType == "NARIT-ACTIVE";

            foreach (var tracker in _allTrackers)
            {
                try
                {
                    var topo0 = tracker.GetTopocentric(_observerLocation, now);
                    scanned++;

                    // Check 1: Current position must be in range (10-87° for LEO, 5-90° for others)
                    double minElev = useLeoFiltering ? MIN_ELEVATION : 5.0;
                    double maxElev = useLeoFiltering ? MAX_ELEVATION : 90.0;
                    
                    if (topo0.Elevation < minElev || topo0.Elevation > maxElev)
                        continue;

                    visible++;

                    // Check 2: Calculate velocity
                    var topo1 = tracker.GetTopocentric(_observerLocation, now.AddSeconds(DELTA_TIME_SEC));
                    
                    double azVel = (topo1.Azimuth - topo0.Azimuth) / DELTA_TIME_SEC;
                    if (azVel > 180.0) azVel -= 360.0;
                    if (azVel < -180.0) azVel += 360.0;
                    
                    double elVel = (topo1.Elevation - topo0.Elevation) / DELTA_TIME_SEC;
                    double maxVel = Math.Max(Math.Abs(azVel), Math.Abs(elVel));

                    if (useLeoFiltering)
                    {
                        // SpaceTrackLEO: Apply velocity and duration filtering
                        
                        // Check 3: Velocity must be between 0.3-10 deg/s for trackable
                        bool velocityOk = (maxVel >= MIN_VELOCITY_THRESHOLD && maxVel <= 10.0);
                        
                        if (!velocityOk)
                        {
                            // Add to visible-only list (not trackable but visible)
                            visibleOnlyCandidates.Add((tracker, azVel, elVel, maxVel, topo0, false));
                            continue;
                        }

                        // Check 4: Duration check - must stay valid for at least 3 minutes
                        bool durationOk = true;
                        for (int minute = 1; minute <= MIN_DURATION_MINUTES; minute++)
                        {
                            DateTime futureTime = now.AddMinutes(minute);
                            var topoFuture = tracker.GetTopocentric(_observerLocation, futureTime);
                            
                            if (topoFuture.Elevation < MIN_ELEVATION || topoFuture.Elevation > MAX_ELEVATION)
                            {
                                durationOk = false;
                                break;
                            }
                            
                            var topoFuturePlus1s = tracker.GetTopocentric(_observerLocation, futureTime.AddSeconds(DELTA_TIME_SEC));
                            
                            double futureAzVel = (topoFuturePlus1s.Azimuth - topoFuture.Azimuth) / DELTA_TIME_SEC;
                            if (futureAzVel > 180.0) futureAzVel -= 360.0;
                            if (futureAzVel < -180.0) futureAzVel += 360.0;
                            
                            double futureElVel = (topoFuturePlus1s.Elevation - topoFuture.Elevation) / DELTA_TIME_SEC;
                            double futureMaxVel = Math.Max(Math.Abs(futureAzVel), Math.Abs(futureElVel));
                            
                            if (futureMaxVel < MIN_VELOCITY_THRESHOLD)
                            {
                                durationOk = false;
                                break;
                            }
                        }

                        if (!durationOk)
                        {
                            // Add to visible-only list (velocity ok but duration not ok)
                            visibleOnlyCandidates.Add((tracker, azVel, elVel, maxVel, topo0, false));
                            continue;
                        }

                        // Fully trackable satellite (LEO with good velocity and duration)
                        trackableCandidates.Add((tracker, azVel, elVel, maxVel, topo0, true));
                        fastEnough++;
                    }
                    else
                    {
                        // Other types (Geostationary, MEO, HEO, GPS, StarLink, SpaceStations):
                        // Just check if visible - all visible satellites are trackable
                        // These satellites are typically slower moving or geostationary
                        trackableCandidates.Add((tracker, azVel, elVel, maxVel, topo0, true));
                        fastEnough++;
                    }
                }
                catch { }
            }

            // Sort trackable by elevation (highest first - better visibility)
            var sortedTrackable = trackableCandidates.OrderByDescending(s => s.topo.Elevation).ToList();
            
            // Sort visible-only by elevation (highest first - more visible)
            var sortedVisibleOnly = visibleOnlyCandidates.OrderByDescending(s => s.topo.Elevation).ToList();
            
            // Combine: Trackable satellites first, then fill with visible-only to reach MAX_DISPLAYED_SATELLITES
            var result = new List<(SatelliteTracker, double, double, double, TopocentricCoordinates, bool)>();
            
            // Add trackable satellites first
            foreach (var sat in sortedTrackable.Take(MAX_DISPLAYED_SATELLITES))
            {
                result.Add(sat);
            }
            
            // Fill remaining slots with visible-only satellites
            int remaining = MAX_DISPLAYED_SATELLITES - result.Count;
            if (remaining > 0)
            {
                foreach (var sat in sortedVisibleOnly.Take(remaining))
                {
                    result.Add(sat);
                }
            }
            
            return (result, scanned, visible, fastEnough);
        }

        private void UpdateDisplayWithScanResults((List<(SatelliteTracker, double, double, double, TopocentricCoordinates, bool)>, int, int, int) result, bool autoResumeTracking = false)
        {
            var (topSats, scanned, visible, fastEnough) = result;
            _displayedTrackers.Clear();
            _satelliteInfoList.Clear();
            
            // Clear pending satellite switch flag - we have new list now
            _pendingSatelliteSwitch = false;
            
            // Reset invalid counter for new satellite
            _satellite1InvalidCounter = 0;

            int rank = 1;
            int trackableCount = 0;
            foreach (var (tracker, azVel, elVel, maxVel, topo, isTrackable) in topSats)
            {
                _displayedTrackers.Add(tracker);
                
                // Mark non-trackable satellites with (!) in name
                string displayName = isTrackable ? $"#{rank} - {tracker.Name}" : $"#{rank} - {tracker.Name} (!)";
                
                _satelliteInfoList.Add(new SatelliteInfo
                {
                    Name = displayName,
                    Azimuth = topo.Azimuth,
                    Altitude = topo.Elevation,
                    AzimuthVelocity = azVel,
                    AltitudeVelocity = elVel,
                    Range = topo.Range,
                    IsVisible = isTrackable  // Use IsVisible to indicate trackability
                });
                
                if (isTrackable) trackableCount++;
                rank++;
            }

            if (topSats.Count == 0)
            {
                txtStatus.Text = $"No satellites available";
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                
                // Cannot auto-resume if no satellites found
                if (autoResumeTracking)
                {
                    SimpleLogger.Warning("[AUTO-RESUME] No satellites available - tracking stopped", "SatTrack");
                }
            }
            else
            {
                // Show status with trackable count
                if (trackableCount > 0)
                {
                    txtStatus.Text = $"Ready - {trackableCount} trackable, {topSats.Count} visible";
                    txtStatus.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    txtStatus.Text = $"Ready - {topSats.Count} visible (none trackable)";
                    txtStatus.Foreground = System.Windows.Media.Brushes.Orange;
                }
                
                // [AUTO-RESUME] Automatically start tracking new satellite #1 (only if trackable)
                if (autoResumeTracking && topSats.Count > 0)
                {
                    var firstSat = topSats[0];
                    var newSat = _satelliteInfoList[0];
                    
                    // Only auto-resume if the first satellite is trackable
                    if (!firstSat.Item6) // isTrackable is the 6th element
                    {
                        SimpleLogger.Warning($"[AUTO-RESUME] First satellite {_displayedTrackers[0].Name} is NOT trackable - NOT restarting", "SatTrack");
                        return;
                    }
                    
                    // [CRITICAL FIX] Validate satellite BEFORE restarting timer!
                    // Different validation based on satellite type
                    bool useLeoFiltering = _currentSatelliteType == "NARIT-ACTIVE";
                    double minElev = useLeoFiltering ? MIN_ELEVATION : 5.0;
                    double maxElev = useLeoFiltering ? MAX_ELEVATION : 90.0;
                    
                    bool isAltitudeValid = (newSat.Altitude >= minElev && newSat.Altitude <= maxElev);
                    
                    // LEO satellites need velocity check, other types (MEO, GEO, GPS) don't
                    bool isVelocityValid = useLeoFiltering 
                        ? (Math.Abs(newSat.AzimuthVelocity) >= MIN_VELOCITY_THRESHOLD || 
                           Math.Abs(newSat.AltitudeVelocity) >= MIN_VELOCITY_THRESHOLD)
                        : true;  // Non-LEO satellites are always velocity-valid if visible
                    
                    if (!isAltitudeValid || !isVelocityValid)
                    {
                        SimpleLogger.Warning($"[AUTO-RESUME] New satellite {_displayedTrackers[0].Name} is INVALID! Alt={newSat.Altitude:F1}° (Valid:{isAltitudeValid}), Vel={Math.Max(Math.Abs(newSat.AzimuthVelocity), Math.Abs(newSat.AltitudeVelocity)):F3} (Valid:{isVelocityValid}) - NOT restarting timer", "SatTrack");
                        return;
                    }
                    
                    SimpleLogger.Info($"[AUTO-RESUME] Switching to {_displayedTrackers[0].Name} (Az: {newSat.Azimuth:F1}°, Alt: {newSat.Altitude:F1}°) - Satellite is VALID ✓", "SatTrack");
                    
                    // [CRITICAL FIX] Force restart tracking regardless of timer state
                    SimpleLogger.Info($"[AUTO-RESUME] Force restarting tracking engine with new satellite", "SatTrack");
                    
                    // Stop everything first
                    _isTracking = false;
                    _velocityTrackingTimer?.Stop();
                    _trackingEngine?.StopTracking();
                    
                    // Register new satellite with TrackingManager
                    var newTarget = new TrackingTarget
                    {
                        ObjectType = TrackingObjectType.Satellite,
                        ObjectName = _displayedTrackers[0].Name,
                        Azimuth = newSat.Azimuth,
                        Altitude = newSat.Altitude,
                        SourceObject = _displayedTrackers[0]
                    };

                    bool granted = TrackingManager.Instance.RequestStartTracking(
                        TrackingObjectType.Satellite,
                        newTarget,
                        () => Dispatcher.Invoke(() => StopTrackingInternal())
                    );

                    if (!granted)
                    {
                        SimpleLogger.Warning("[AUTO-RESUME] TrackingManager denied restart request", "SatTrack");
                        return;
                    }
                    
                    // Set new satellite to tracking engine
                    if (_trackingEngine != null && _displayedTrackers.Count > 0)
                    {
                        _trackingEngine.SetTarget(_displayedTrackers[0], _observerLocation);
                        _trackingEngine.StartTracking();
                        SimpleLogger.Info($"[TRACK ENGINE] ✓ Target set: {_displayedTrackers[0].Name}", "SatTrack");
                    }
                    
                    // Restart tracking
                    _isTracking = true;
                    _velocityTrackingTimer.Start();
                    btnStartTrack.Content = "Stop Track";
                    btnStartTrack.Background = System.Windows.Media.Brushes.Red;
                    SimpleLogger.Info($"[AUTO-RESUME] ✓ Tracking restarted - Now tracking {_displayedTrackers[0].Name}", "SatTrack");
                }
            }

            UpdateSatelliteCount();
        }

        /// <summary>
        /// Fast Update Timer (100 ms): Update satellite #1 DISPLAY and check validity
        /// NOTE: Satellite velocity is now calculated in VelocityTrackingTimer (15ms) for smoothness
        /// </summary>
        private void FastUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_displayedTrackers.Count == 0 || _satelliteInfoList.Count == 0) return;

                DateTime now = DateTime.UtcNow;
                const double DELTA_TIME_SEC = 0.5;

                var topo0 = _displayedTrackers[0].GetTopocentric(_observerLocation, now);
                var topo1 = _displayedTrackers[0].GetTopocentric(_observerLocation, now.AddSeconds(DELTA_TIME_SEC));

                // Calculate raw velocity for VALIDITY CHECK only (not used in tracking)
                double azVel = (topo1.Azimuth - topo0.Azimuth) / DELTA_TIME_SEC;
                
                if (azVel > 180.0)
                {
                    azVel -= 360.0;
                }
                else if (azVel < -180.0)
                {
                    azVel += 360.0;
                }
                
                double elVel = (topo1.Elevation - topo0.Elevation) / DELTA_TIME_SEC;

                // Update DISPLAY ONLY (satellite velocity for tracking is calculated in VelocityTrackingTimer at 15ms)
                _satelliteInfoList[0].Azimuth = topo0.Azimuth;
                _satelliteInfoList[0].Altitude = topo0.Elevation;
                _satelliteInfoList[0].AzimuthVelocity = azVel;  // For display only
                _satelliteInfoList[0].AltitudeVelocity = elVel;  // For display only
                _satelliteInfoList[0].Range = topo0.Range;
                
                // Check validity using RAW velocity
                // [CRITICAL FIX] Different validation based on satellite type
                bool useLeoFiltering = _currentSatelliteType == "NARIT-ACTIVE";
                double minElev = useLeoFiltering ? MIN_ELEVATION : 5.0;
                double maxElev = useLeoFiltering ? MAX_ELEVATION : 90.0;
                
                bool isElevationOk = topo0.Elevation >= minElev && topo0.Elevation <= maxElev;
                double rawMaxVel = Math.Max(Math.Abs(azVel), Math.Abs(elVel));
                
                // LEO satellites need velocity check, other types (MEO, GEO, GPS) don't
                bool isVelocityOk = useLeoFiltering 
                    ? (rawMaxVel >= MIN_VELOCITY_THRESHOLD && rawMaxVel <= 10.0)
                    : true;  // Non-LEO satellites are always velocity-valid if visible
                
                // [NEW] Check if approaching Azimuth limit (detect when AT or past limit)
                bool isAzimuthSafe = true;
                if (_isTracking && _pollingManager != null)
                {
                    var azConfig = AppSettings.GetAzimuthAxisConfig();
                    double currentAzRaw = (_pollingManager.AzimuthInfo.Raw_Position / azConfig.Encoder_Resolution) * 360.0;
                    double azMinLimit = azConfig.CCW_Limit_Deg;  // From AppSettings (e.g., -300°)
                    double azMaxLimit = azConfig.CW_Limit_Deg;   // From AppSettings (e.g., +300°)
                    
                    // Check if AT or beyond limits
                    bool atOrBeyondCWLimit = currentAzRaw >= azMaxLimit;     // >= limit (e.g., +300°)
                    bool atOrBeyondCCWLimit = currentAzRaw <= azMinLimit;    // <= limit (e.g., -300°)
                    
                    // Check if satellite is moving TOWARD limit
                    bool movingTowardCWLimit = atOrBeyondCWLimit && azVel > 0.1;   // At/beyond +limit and moving CW
                    bool movingTowardCCWLimit = atOrBeyondCCWLimit && azVel < -0.1; // At/beyond -limit and moving CCW
                    
                    if (movingTowardCWLimit || movingTowardCCWLimit)
                    {
                        isAzimuthSafe = false;
                        string direction = movingTowardCWLimit ? $"CW ({azMaxLimit:F0}°)" : $"CCW ({azMinLimit:F0}°)";
                        System.Diagnostics.Debug.WriteLine($"[LIMIT DANGER] Azimuth at/beyond {direction} limit!");
                        System.Diagnostics.Debug.WriteLine($"  Current Position: {currentAzRaw:F1}°");
                        System.Diagnostics.Debug.WriteLine($"  Satellite Velocity: {azVel:F3} deg/s");
                        SimpleLogger.Warning($"[LIMIT DANGER] Azimuth reached {direction} limit at {currentAzRaw:F1}° - Switching satellite", "SatTrack");
                    }
                }
                
                bool isCurrentlyValid = isElevationOk && isVelocityOk && isAzimuthSafe;
                
                // Debounce logic
                if (!isCurrentlyValid)
                {
                    _satellite1InvalidCounter++;
                }
                else
                {
                    _satellite1InvalidCounter = 0;
                }
                
                bool isValid = _satellite1InvalidCounter < INVALID_COUNT_THRESHOLD;
                _satelliteInfoList[0].IsVisible = true;

                // If invalid, trigger rescan
                if (!isValid && !_isScanning && !_pendingSatelliteSwitch)
                {
                    System.Diagnostics.Debug.WriteLine($"[SAT#1] {_displayedTrackers[0].Name} became INVALID - rescanning...");
                    System.Diagnostics.Debug.WriteLine($"  Elevation: {topo0.Elevation:F2}° (OK: {isElevationOk})");
                    System.Diagnostics.Debug.WriteLine($"  Velocity: {rawMaxVel:F3} deg/s (OK: {isVelocityOk})");
                    System.Diagnostics.Debug.WriteLine($"  Azimuth Limit Safe: {isAzimuthSafe}");
                    
                    _pendingSatelliteSwitch = true;
                    bool wasTracking = _isTracking;
                    
                    if (_isTracking && _autoResumeEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AUTO-RESUME] Satellite invalid - STOPPING tracking temporarily");
                        
                        // Stop tracking engine (will send stop commands and set target to null)
                        if (_trackingEngine != null)
                        {
                            _trackingEngine.StopTracking();
                        }
                        
                        SimpleLogger.Info($"[AUTO-RESUME] Satellite {_displayedTrackers[0].Name} invalid - Scanning for new satellite", "SatTrack");
                    }
                    else if (_isTracking && !_autoResumeEnabled)
                    {
                        _isTracking = false;
                        
                        // Stop tracking engine
                        if (_trackingEngine != null)
                        {
                            _trackingEngine.StopTracking();
                        }
                        
                        // Notify TrackingManager that tracking has stopped
                        TrackingManager.Instance.StopTracking(TrackingObjectType.Satellite);
                        
                        btnStartTrack.Content = "Start Track";
                        btnStartTrack.Background = System.Windows.Media.Brushes.Green;
                        SimpleLogger.Info($"[SAT#1] Satellite {_displayedTrackers[0].Name} invalid - Stopping tracking (Auto-Resume OFF)", "SatTrack");
                    }
                    
                    ScanForFastestSatellites(wasTracking && _autoResumeEnabled);
                    return;
                }
                else if (isValid && _pendingSatelliteSwitch)
                {
                    _pendingSatelliteSwitch = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] FastUpdateTimer: {ex.Message}");
            }
        }

        /// <summary>
        /// Update Timer (500 ms): Update satellites #2-10
        /// </summary>
        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                int visibleCount = 0;
                const double DELTA_TIME_SEC = 0.5;

                // Update satellites #2-10 (index 1-9)
                for (int i = 1; i < _displayedTrackers.Count && i < _satelliteInfoList.Count; i++)
                {
                    var topo0 = _displayedTrackers[i].GetTopocentric(_observerLocation, now);
                    var topo1 = _displayedTrackers[i].GetTopocentric(_observerLocation, now.AddSeconds(DELTA_TIME_SEC));

                    // Calculate velocity with proper angle unwrapping
                    double azVel = (topo1.Azimuth - topo0.Azimuth) / DELTA_TIME_SEC;
                    
                    // Unwrap to shortest path
                    if (azVel > 180.0)
                    {
                        azVel -= 360.0;
                    }
                    else if (azVel < -180.0)
                    {
                        azVel += 360.0;
                    }
                    
                    double elVel = (topo1.Elevation - topo0.Elevation) / DELTA_TIME_SEC;

                    _satelliteInfoList[i].Azimuth = topo0.Azimuth;
                    _satelliteInfoList[i].Altitude = topo0.Elevation;
                    _satelliteInfoList[i].AzimuthVelocity = azVel;
                    _satelliteInfoList[i].AltitudeVelocity = elVel;
                    _satelliteInfoList[i].Range = topo0.Range;
                    _satelliteInfoList[i].IsVisible = topo0.Elevation > MIN_ELEVATION && topo0.Elevation < MAX_ELEVATION;

                    if (_satelliteInfoList[i].IsVisible) visibleCount++;
                }

                // Add satellite #1 visible status
                if (_satelliteInfoList.Count > 0 && _satelliteInfoList[0].IsVisible)
                    visibleCount++;

                UpdateSatelliteCount(visibleCount);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] UpdateTimer: {ex.Message}");
            }
        }

        private void UpdateSatelliteCount(int? visibleCount = null)
        {
            // Removed satellite count display from UI
            // Information still tracked internally but not displayed
        }

        /// <summary>
        /// Button handler: Start/Stop tracking satellite #1
        /// [CRITICAL FIX] Now writes to CentralizedPollingManager's AltitudeInfo.Command_Velocity_Deg_Sec
        /// </summary>
        private async void BtnStartTrack_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[START TRACK] Button clicked - Current IsTracking: {_isTracking}");
            System.Diagnostics.Debug.WriteLine($"[START TRACK] PollingManager: {(_pollingManager != null ? "Valid" : "NULL")}");
            System.Diagnostics.Debug.WriteLine($"[START TRACK] TrackingEngine: {(_trackingEngine != null ? "Valid" : "NULL")}");
            System.Diagnostics.Debug.WriteLine($"[START TRACK] Satellite count: {_satelliteInfoList.Count}");
            
            if (_pollingManager == null)
            {
                SimpleLogger.Error("[TRACK] CentralizedPollingManager not initialized! Call InitializeTrackingEngine() first.", "SatTrack");
                MessageBox.Show("Tracking not initialized. Please restart the application.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (_trackingEngine == null)
            {
                SimpleLogger.Error("[TRACK] SatelliteTracking Engine is NULL! Call InitializeTrackingEngine() first.", "SatTrack");
                MessageBox.Show("Satellite Tracking Engine not initialized. Please restart the application.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_displayedTrackers.Count == 0 || _satelliteInfoList.Count == 0)
            {
                SimpleLogger.Warning("[TRACK] No satellite to track!", "SatTrack");
                MessageBox.Show("No satellites available to track. Please load TLE data first.", 
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_isTracking)
            {
                // Stop tracking - notify TrackingManager
                TrackingManager.Instance.StopTracking(TrackingObjectType.Satellite);
                
                _isTracking = false;
                _velocityTrackingTimer.Stop();
                
                System.Diagnostics.Debug.WriteLine($"[START TRACK] Stopping tracking - Timer stopped");
                
                // Stop tracking engine
                if (_trackingEngine != null)
                {
                    _trackingEngine.StopTracking();
                }
                
                // [FIXED] Stop altitude motion using CentralizedPollingManager
                if (_pollingManager != null)
                {
                    _pollingManager.SetAltitudeCommandVelocity(0.0);
                    _pollingManager.SetAzimuthCommandVelocity(0.0);
                    SimpleLogger.Info("[TRACK] Velocity commands set to 0 via CentralizedPollingManager", "SatTrack");
                    
                    // [NEW] Stop RMS tracking
                    _pollingManager.RmsTrackingManager?.SetAzimuthTarget(null);
                    _pollingManager.RmsTrackingManager?.SetAltitudeTarget(null);
                }
                
                // Hide adaptive prediction time display
                txtActivePrediction.Visibility = Visibility.Collapsed;
                
                btnStartTrack.Content = "Start Track";
                btnStartTrack.Background = System.Windows.Media.Brushes.Green;
                
                // Fire event to update Sky Chart path (show selected if any, otherwise clear)
                if (SelectedSatelliteTracker != null)
                {
                    SatelliteSelected?.Invoke(this, SelectedSatelliteTracker);
                }
                else
                {
                    SatelliteSelected?.Invoke(this, null);
                }
                
                SimpleLogger.Info("[PD TRACK] Tracking stopped by user", "SatTrack");
            }
            else
            {
                // Start tracking satellite #1
                
                // Request tracking from TrackingManager (will stop other tracking types)
                var sat = _displayedTrackers[0];
                var target = new TrackingTarget
                {
                    ObjectType = TrackingObjectType.Satellite,
                    ObjectName = sat.Name,
                    Azimuth = _satelliteInfoList.Count > 0 ? _satelliteInfoList[0].Azimuth : 0,
                    Altitude = _satelliteInfoList.Count > 0 ? _satelliteInfoList[0].Altitude : 0,
                    SourceObject = sat
                };

                bool granted = TrackingManager.Instance.RequestStartTracking(
                    TrackingObjectType.Satellite,
                    target,
                    () => Dispatcher.Invoke(() => StopTrackingInternal())
                );

                if (!granted)
                {
                    SimpleLogger.Warning("[TRACK] Satellite tracking request denied by TrackingManager", "SatTrack");
                    return;
                }
                
                _isTracking = true;
                
                // [CRITICAL] Check if drives are enabled before tracking
                System.Diagnostics.Debug.WriteLine($"[START TRACK] Checking drive enable status...");
                if (!_pollingManager.AzimuthInfo.Is_Drive_Enabled || !_pollingManager.AltitudeInfo.Is_Drive_Enabled)
                {
                    System.Diagnostics.Debug.WriteLine($"[START TRACK] ERROR - Drives not enabled!");
                    System.Diagnostics.Debug.WriteLine($"[START TRACK] Azimuth Enabled: {_pollingManager.AzimuthInfo.Is_Drive_Enabled}");
                    System.Diagnostics.Debug.WriteLine($"[START TRACK] Altitude Enabled: {_pollingManager.AltitudeInfo.Is_Drive_Enabled}");
                    
                    MessageBox.Show("Please enable both Azimuth and Altitude drives in Direction Control tab before starting tracking.", 
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _isTracking = false;
                    TrackingManager.Instance.StopTracking(TrackingObjectType.Satellite);
                    return;
                }
                
                System.Diagnostics.Debug.WriteLine($"[START TRACK] Both drives enabled - starting tracking");
                
                // Set target satellite and observer location to engine
                if (_trackingEngine != null && _displayedTrackers.Count > 0)
                {
                    _trackingEngine.SetTarget(_displayedTrackers[0], _observerLocation);
                    _trackingEngine.StartTracking();
                    SimpleLogger.Info($"[TRACK ENGINE] Started tracking {_displayedTrackers[0].Name}", "SatTrack");
                }
                
                _velocityTrackingTimer.Start();
                
                // Reset warning log flags
                _lastCollisionPreventCW = false;
                _lastCollisionPreventCCW = false;
                _lastVelocityReversalCW = false;
                _lastVelocityReversalCCW = false;
                
                btnStartTrack.Content = "Stop Track";
                btnStartTrack.Background = System.Windows.Media.Brushes.Red;
                
                // Fire event to update Sky Chart path to show current tracking satellite
                SatelliteSelected?.Invoke(this, _displayedTrackers[0]);
                
                System.Diagnostics.Debug.WriteLine($"[START TRACK] Target: {_displayedTrackers[0].Name}");
            }
        }
        
        /// <summary>
        /// Internal method to stop tracking (called by TrackingManager callback)
        /// </summary>
        private void StopTrackingInternal()
        {
            try
            {
                _isTracking = false;
                _velocityTrackingTimer?.Stop();
                
                // Stop tracking engine
                _trackingEngine?.StopTracking();
                
                // Stop drives
                if (_pollingManager != null)
                {
                    _pollingManager.SetAltitudeCommandVelocity(0.0);
                    _pollingManager.SetAzimuthCommandVelocity(0.0);
                    
                    // Stop RMS tracking
                    _pollingManager.RmsTrackingManager?.SetAzimuthTarget(null);
                    _pollingManager.RmsTrackingManager?.SetAltitudeTarget(null);
                }
                
                // Hide adaptive prediction time display
                txtActivePrediction.Visibility = Visibility.Collapsed;
                
                btnStartTrack.Content = "Start Track";
                btnStartTrack.Background = System.Windows.Media.Brushes.Green;
                
                SimpleLogger.Info("[SATELLITE] Tracking stopped via TrackingManager", "SatTrack");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SATELLITE ERROR] StopTrackingInternal: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Check if satellite is moving toward a limit and will cause tracking to stop prematurely.
        /// If detected, unwrap the target position to track from the opposite side for longer duration.
        /// This maximizes tracking time by choosing the path with more available travel range.
        /// </summary>
        /// <param name="currentPos">Current axis position (deg)</param>
        /// <param name="targetPos">Target satellite position (deg)</param>
        /// <param name="satelliteVelocity">Satellite velocity (deg/s) - indicates movement direction</param>
        /// <param name="minLimit">CCW limit (deg)</param>
        /// <param name="maxLimit">CW limit (deg)</param>
        /// <returns>Corrected target position (may be unwrapped to opposite side)</returns>
        private double PreventLimitCollision(double currentPos, double targetPos, double satelliteVelocity,
                                            double minLimit, double maxLimit)
        {
            const double COLLISION_PREDICTION_DEG = 50.0;  // Predict collision when satellite within 50° of limit
            const double MIN_SATELLITE_VEL = 0.1;          // Minimum satellite velocity to consider (deg/s)
            
            // Only check if satellite is actually moving
            if (Math.Abs(satelliteVelocity) < MIN_SATELLITE_VEL)
                return targetPos;
            
            bool satelliteMovingCW = satelliteVelocity > 0;   // Positive velocity = CW direction
            bool satelliteMovingCCW = satelliteVelocity < 0;  // Negative velocity = CCW direction
            
            bool targetNearCWLimit = targetPos >= (maxLimit - COLLISION_PREDICTION_DEG);   // Target within 50° of +300°
            bool targetNearCCWLimit = targetPos <= (minLimit + COLLISION_PREDICTION_DEG);  // Target within 50° of -300°
            
            // Case 1: Satellite moving CW and approaching CW limit
            // Example: Target=280° moving toward 300° ? Will hit limit soon!
            // Solution: Unwrap to CCW side ? Target becomes -80° (same sky position)
            if (satelliteMovingCW && targetNearCWLimit)
            {
                double unwrappedTarget = targetPos - 360.0;  // e.g., 280° ? -80°
                
                // Only unwrap if the unwrapped position is within CCW limit
                if (unwrappedTarget >= minLimit)
                {
                    // Calculate which path gives longer tracking duration
                    double distanceToCWLimit = maxLimit - targetPos;           // Distance before hitting CW limit
                    double distanceFromCCWLimit = unwrappedTarget - minLimit;   // Available range on CCW side
                    
                    // If CCW path provides significantly more tracking range, use it
                    if (distanceFromCCWLimit > distanceToCWLimit * 2)  // At least 2x more range
                    {
                        // Only log once when state changes
                        if (!_lastCollisionPreventCW)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LIMIT COLLISION PREVENTION] Satellite moving CW toward limit");
                            System.Diagnostics.Debug.WriteLine($"  Current={currentPos:F2}°, Target={targetPos:F2}°, SatVel={satelliteVelocity:F3}°/s");
                            System.Diagnostics.Debug.WriteLine($"  CW distance to limit: {distanceToCWLimit:F1}°");
                            System.Diagnostics.Debug.WriteLine($"  CCW available range: {distanceFromCCWLimit:F1}°");
                            System.Diagnostics.Debug.WriteLine($"  ? Unwrapping to CCW side: {targetPos:F2}° ? {unwrappedTarget:F2}°");
                            
                            SimpleLogger.Warning($"[COLLISION PREVENT] Target near CW limit - Unwrapping {targetPos:F1}° ? {unwrappedTarget:F1}° for longer tracking", "SatTrack");
                            _lastCollisionPreventCW = true;
                            _lastCollisionPreventCCW = false;  // Clear opposite flag
                        }
                        return unwrappedTarget;
                    }
                }
                else
                {
                    // Clear flag when no longer unwrapping
                    _lastCollisionPreventCW = false;
                }
            }
            // Case 2: Satellite moving CCW and approaching CCW limit
            // Example: Target=-280° moving toward -300° ? Will hit limit soon!
            // Solution: Unwrap to CW side ? Target becomes +80° (same sky position)
            else if (satelliteMovingCCW && targetNearCCWLimit)
            {
                double unwrappedTarget = targetPos + 360.0;  // e.g., -280° ? +80°
                
                // Only unwrap if the unwrapped position is within CW limit
                if (unwrappedTarget <= maxLimit)
                {
                    // Calculate which path gives longer tracking duration
                    double distanceToCCWLimit = targetPos - minLimit;           // Distance before hitting CCW limit
                    double distanceFromCWLimit = maxLimit - unwrappedTarget;    // Available range on CW side
                    
                    // If CW path provides significantly more tracking range, use it
                    if (distanceFromCWLimit > distanceToCCWLimit * 2)  // At least 2x more range
                    {
                        // Only log once when state changes
                        if (!_lastCollisionPreventCCW)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LIMIT COLLISION PREVENTION] Satellite moving CCW toward limit");
                            System.Diagnostics.Debug.WriteLine($"  Current={currentPos:F2}°, Target={targetPos:F2}°, SatVel={satelliteVelocity:F3}°/s");
                            System.Diagnostics.Debug.WriteLine($"  CCW distance to limit: {distanceToCCWLimit:F1}°");
                            System.Diagnostics.Debug.WriteLine($"  CW available range: {distanceFromCWLimit:F1}°");
                            System.Diagnostics.Debug.WriteLine($"  ? Unwrapping to CW side: {targetPos:F2}° ? {unwrappedTarget:F2}°");
                            
                            SimpleLogger.Warning($"[COLLISION PREVENT] Target near CCW limit - Unwrapping {targetPos:F1}° ? {unwrappedTarget:F1}° for longer tracking", "SatTrack");
                            _lastCollisionPreventCCW = true;
                            _lastCollisionPreventCW = false;  // Clear opposite flag
                        }
                        return unwrappedTarget;
                    }
                }
                else
                {
                    // Clear flag when no longer unwrapping
                    _lastCollisionPreventCCW = false;
                }
            }
            else
            {
                // Clear both flags when not near any limit
                _lastCollisionPreventCW = false;
                _lastCollisionPreventCCW = false;
            }
            
            // No unwrapping needed - target is safe
            return targetPos;
        }
        
        /// <summary>
        /// Validate velocity command and reverse direction if it would cause collision with limits.
        /// This function checks if the commanded velocity would push the axis beyond safe limits,
        /// and if so, reverses the direction to track from the opposite side.
        /// </summary>
        /// <param name="currentPos">Current axis position (deg)</param>
        /// <param name="targetPos">Target position (deg)</param>
        /// <param name="velocityCommand">Velocity from PD controller (deg/s)</param>
        /// <param name="minLimit">CCW limit (deg)</param>
        /// <param name="maxLimit">CW limit (deg)</param>
        /// <returns>Validated velocity command (may be reversed)</returns>
        private double ValidateAndReverseVelocity(double currentPos, double targetPos, double velocityCommand, 
                                                   double minLimit, double maxLimit)
        {
            const double DANGER_ZONE_DEG = 30.0;  // Start checking when within 30° of limit
            const double REVERSE_THRESHOLD = 0.1; // Minimum velocity to consider reversal (deg/s)
            
            // Only validate if velocity is significant
            if (Math.Abs(velocityCommand) < REVERSE_THRESHOLD)
                return velocityCommand;
            
            bool nearCWLimit = currentPos >= (maxLimit - DANGER_ZONE_DEG);   // Within 30° of CW limit
            bool nearCCWLimit = currentPos <= (minLimit + DANGER_ZONE_DEG);  // Within 30° of CCW limit
            
            // Case 1: Near CW limit and commanded velocity is CW (positive)
            if (nearCWLimit && velocityCommand > 0)
            {
                // Check if we can track from CCW side instead
                double reversedTarget = targetPos - 360.0;
                
                // Only reverse if reversed target is within CCW limit
                if (reversedTarget >= minLimit)
                {
                    // Only log once when state changes
                    if (!_lastVelocityReversalCW)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VELOCITY REVERSAL] Near CW limit: Current={currentPos:F2}°, CW Vel={velocityCommand:F3}°/s ? Reversed to CCW");
                        SimpleLogger.Warning($"[LIMIT PROTECTION] Reversing direction at CW limit: {velocityCommand:F3}°/s ? CCW", "SatTrack");
                        _lastVelocityReversalCW = true;
                        _lastVelocityReversalCCW = false;
                    }
                    
                    // Return reversed velocity (negative = CCW)
                    return -Math.Abs(velocityCommand);
                }
            }
            else
            {
                _lastVelocityReversalCW = false;
            }
            
            // Case 2: Near CCW limit and commanded velocity is CCW (negative)
            if (nearCCWLimit && velocityCommand < 0)
            {
                // Check if we can track from CW side instead
                double reversedTarget = targetPos + 360.0;
                
                // Only reverse if reversed target is within CW limit
                if (reversedTarget <= maxLimit)
                {
                    // Only log once when state changes
                    if (!_lastVelocityReversalCCW)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VELOCITY REVERSAL] Near CCW limit: Current={currentPos:F2}°, CCW Vel={velocityCommand:F3}°/s ? Reversed to CW");
                        SimpleLogger.Warning($"[LIMIT PROTECTION] Reversing direction at CCW limit: {velocityCommand:F3}°/s ? CW", "SatTrack");
                        _lastVelocityReversalCCW = true;
                        _lastVelocityReversalCW = false;
                    }
                    
                    // Return reversed velocity (positive = CW)
                    return Math.Abs(velocityCommand);
                }
            }
            else
            {
                _lastVelocityReversalCCW = false;
            }
            
            // No reversal needed
            return velocityCommand;
        }
        
        /// <summary>
        /// Velocity Tracking Timer (15ms): Calls SatelliteTracking Engine
        /// Engine computes satellite position internally via SGP4
        /// </summary>
        private void VelocityTrackingTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (!_isTracking || _trackingEngine == null)
                {
                    return;
                }
                
                // [CRITICAL FIX] Do NOT call ComputeAndSendCommands() here!
                // The Engine already has its own internal 15ms System.Timers.Timer that calls it.
                // Calling it again from UI DispatcherTimer causes:
                //   1. dt miscalculation (3-8ms instead of 15ms) → D-term spike
                //   2. Velocity filter double-update → output oscillation
                //   3. Two conflicting velocity commands within one 15ms cycle
                
                // Only update RMS Tracking Manager with target from engine
                if (_pollingManager?.RmsTrackingManager != null)
                {
                    _pollingManager.RmsTrackingManager.SetAzimuthTarget(_trackingEngine.TargetAzimuth);
                    _pollingManager.RmsTrackingManager.SetAltitudeTarget(_trackingEngine.TargetAltitude);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] VelocityTrackingTimer: {ex.Message}");
                SimpleLogger.Error($"[VEL TIMER] Exception: {ex.Message}", "SatTrack");
            }
        }


        #region Observer Location Event Handlers

        /// <summary>
        /// Load Observer Location from AppSettings
        /// </summary>
        private void LoadObserverLocation()
        {
            try
            {
                var location = AppSettings.GetObserverLocation();
                
                _observerLocation = new ObserverLocation(
                    location.Latitude_Deg,
                    location.Longitude_Deg,
                    location.Altitude_Meters
                );
                
                SimpleLogger.Info($"[CONFIG] Observer Location loaded: Lat={location.Latitude_Deg:F4}, Lon={location.Longitude_Deg:F4}, Alt={location.Altitude_Meters:F0}m", "SatTrack");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[CONFIG] Failed to load Observer Location: {ex.Message}", "SatTrack");
            }
        }
        
        /// <summary>
        /// Handle satellite type ComboBox selection changed
        /// </summary>
        private async void CmbSatelliteType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Skip if control not fully loaded yet (prevents null reference during initialization)
            if (!this.IsLoaded || txtStatus == null) return;
            
            if (cmbSatelliteType?.SelectedItem == null) return;
            
            var selectedItem = cmbSatelliteType.SelectedItem as ComboBoxItem;
            if (selectedItem == null) return;
            
            string newType = selectedItem.Content?.ToString() ?? "NARIT-ACTIVE";
            
            // Don't reload if same type
            if (newType == _currentSatelliteType && _hasAutoScanned) return;
            
            _currentSatelliteType = newType;
            SimpleLogger.Info($"[TYPE CHANGED] Satellite type changed to: {_currentSatelliteType}", "SatTrack");
            
            // Stop any active tracking
            if (_isTracking)
            {
                _isTracking = false;
                _velocityTrackingTimer?.Stop();
                _trackingEngine?.StopTracking();
                
                if (_pollingManager != null)
                {
                    _pollingManager.SetAzimuthCommandVelocity(0.0);
                    _pollingManager.SetAltitudeCommandVelocity(0.0);
                }
                
                btnStartTrack.Content = "Start Track";
                btnStartTrack.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // Green
            }
            
            // Reload TLE data for new type (fetch from NARIT API if applicable)
            await FetchAndLoadTLEAsync();
        }



        #endregion

        #region PD Gain Tuning Event Handlers

        /// <summary>
        /// Open Advanced Tuning Window
        /// </summary>
        private void BtnAdvancedTuning_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var tuningWindow = new AdvancedSatelliteTuningWindow
                {
                    Owner = Window.GetWindow(this)  // Set owner to parent window
                };
                
                // Show as non-modal window (not ShowDialog)
                tuningWindow.Show();
                
                // Subscribe to Closed event to reload settings when window closes
                tuningWindow.Closed += (s, args) =>
                {
                    var closedWindow = s as AdvancedSatelliteTuningWindow;
                    if (closedWindow?.WasApplied == true)
                    {
                        // Settings will be automatically reloaded by SatelliteTracking Engine on next update
                        SimpleLogger.Info("[ADVANCED TUNING] Settings saved - Engine will reload on next cycle", "SatTrack");
                    }
                };
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[ADVANCED TUNING] Failed to open window: {ex.Message}", "SatTrack");
                MessageBox.Show($"Failed to open Advanced Tuning window: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        #endregion
        
        #region Satellite Selection Event Handlers
        
        /// <summary>
        /// Handle DataGrid selection changed - show/hide Track Selected button and update Sky Chart
        /// Logic: If user selects a satellite → show that path
        ///        If user deselects but tracking is active → show current tracking satellite path
        ///        If no selection and not tracking → no path
        /// </summary>
        private void DgSatellites_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                int selectedIndex = dgSatellites.SelectedIndex;
                
                if (selectedIndex >= 0 && selectedIndex < _displayedTrackers.Count)
                {
                    _selectedSatelliteIndex = selectedIndex;
                    
                    // Show Track Selected button
                    btnTrackSelected.Visibility = Visibility.Visible;
                    
                    // Fire event for Sky Chart to display the selected satellite's path
                    var selectedTracker = _displayedTrackers[selectedIndex];
                    SatelliteSelected?.Invoke(this, selectedTracker);
                    
                    // Notify FITS auto-save of selected object name
                    TrackingManager.Instance.NotifyObjectSelected(selectedTracker.Name);
                    
                    SimpleLogger.Info($"[SELECTION] Satellite selected: {selectedTracker.Name} (Index: {selectedIndex})", "SatTrack");
                }
                else
                {
                    _selectedSatelliteIndex = -1;
                    btnTrackSelected.Visibility = Visibility.Collapsed;
                    
                    // No selection - fire event with CurrentSatelliteTracker if tracking, otherwise null
                    // This allows path to show current tracking satellite when user deselects
                    if (_isTracking && CurrentSatelliteTracker != null)
                    {
                        SatelliteSelected?.Invoke(this, CurrentSatelliteTracker);
                        SimpleLogger.Info($"[SELECTION] Deselected - showing current tracking: {CurrentSatelliteTracker.Name}", "SatTrack");
                    }
                    else
                    {
                        SatelliteSelected?.Invoke(this, null);
                        SimpleLogger.Info("[SELECTION] Deselected - no active tracking, path cleared", "SatTrack");
                    }
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SELECTION] Error handling selection: {ex.Message}", "SatTrack");
            }
        }
        
        /// <summary>
        /// Handle Track Selected button click - start tracking the selected satellite
        /// </summary>
        private void BtnTrackSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedSatelliteIndex < 0 || _selectedSatelliteIndex >= _displayedTrackers.Count)
                {
                    MessageBox.Show("Please select a satellite from the list first.", 
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                if (_pollingManager == null)
                {
                    SimpleLogger.Error("[TRACK SELECTED] CentralizedPollingManager not initialized!", "SatTrack");
                    MessageBox.Show("Tracking not initialized. Please restart the application.", 
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (_trackingEngine == null)
                {
                    SimpleLogger.Error("[TRACK SELECTED] SatelliteTracking Engine is NULL!", "SatTrack");
                    MessageBox.Show("Satellite Tracking Engine not initialized. Please restart the application.", 
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Check if drives are enabled
                if (!_pollingManager.AzimuthInfo.Is_Drive_Enabled || !_pollingManager.AltitudeInfo.Is_Drive_Enabled)
                {
                    MessageBox.Show("Please enable both Azimuth and Altitude drives in Direction Control tab before starting tracking.", 
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                // Stop current tracking if active
                if (_isTracking)
                {
                    _isTracking = false;
                    _velocityTrackingTimer?.Stop();
                    _trackingEngine?.StopTracking();
                    
                    if (_pollingManager != null)
                    {
                        _pollingManager.SetAzimuthCommandVelocity(0.0);
                        _pollingManager.SetAltitudeCommandVelocity(0.0);
                    }
                }
                
                // Get selected satellite
                var selectedSatellite = _displayedTrackers[_selectedSatelliteIndex];
                var satInfo = _satelliteInfoList[_selectedSatelliteIndex];
                
                SimpleLogger.Info($"[TRACK SELECTED] Starting tracking of selected satellite: {selectedSatellite.Name}", "SatTrack");
                SimpleLogger.Info($"[TRACK SELECTED] Position: Az={satInfo.Azimuth:F2}°, Alt={satInfo.Altitude:F2}°", "SatTrack");
                
                // Set target satellite and observer location to engine
                _trackingEngine.SetTarget(selectedSatellite, _observerLocation);
                _trackingEngine.StartTracking();
                
                // Start tracking
                _isTracking = true;
                _velocityTrackingTimer.Start();
                
                // Reset warning log flags
                _lastCollisionPreventCW = false;
                _lastCollisionPreventCCW = false;
                _lastVelocityReversalCW = false;
                _lastVelocityReversalCCW = false;
                
                // Update UI
                btnStartTrack.Content = "Stop Track";
                btnStartTrack.Background = System.Windows.Media.Brushes.Red;
                
                // Fire event to update Sky Chart path to show tracking satellite
                SatelliteSelected?.Invoke(this, selectedSatellite);
                
                SimpleLogger.Info($"[TRACK SELECTED] ✓ Now tracking: {selectedSatellite.Name}", "SatTrack");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[TRACK SELECTED] Failed to start tracking: {ex.Message}", "SatTrack");
                MessageBox.Show($"Failed to start tracking: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Public method to stop satellite tracking (called when switching to another tracking mode)
        /// </summary>
        public void StopTracking()
        {
            try
            {
                if (_isTracking)
                {
                    SimpleLogger.Info("[SATELLITE] Stopping tracking (mode switch)", "SatTrack");
                    
                    _isTracking = false;
                    _velocityTrackingTimer?.Stop();
                    _trackingEngine?.StopTracking();
                    
                    // Stop drives
                    if (_pollingManager != null)
                    {
                        _pollingManager.SetAzimuthCommandVelocity(0.0);
                        _pollingManager.SetAltitudeCommandVelocity(0.0);
                    }
                    
                    // Update UI
                    btnStartTrack.Content = "Start Track";
                    btnStartTrack.Background = System.Windows.Media.Brushes.Green;
                    txtStatus.Text = "Tracking stopped (mode switch)";
                    txtActivePrediction.Visibility = Visibility.Collapsed;
                    
                    // Fire event to clear Sky Chart path (or show selected if any)
                    if (SelectedSatelliteTracker != null)
                    {
                        SatelliteSelected?.Invoke(this, SelectedSatelliteTracker);
                    }
                    else
                    {
                        SatelliteSelected?.Invoke(this, null);
                    }
                    
                    SimpleLogger.Info("[SATELLITE] Tracking stopped successfully", "SatTrack");
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SATELLITE] Error stopping tracking: {ex.Message}", "SatTrack");
            }
        }
        
        #endregion
    }

    internal class SatelliteInfo : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name;
        private double _azimuth;
        private double _altitude;
        private double _azimuthVelocity;
        private double _altitudeVelocity;
        private double _range;
        private bool _isVisible;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public double Azimuth
        {
            get => _azimuth;
            set { _azimuth = value; OnPropertyChanged(nameof(Azimuth)); }
        }

        public double Altitude
        {
            get => _altitude;
            set { _altitude = value; OnPropertyChanged(nameof(Altitude)); }
        }

        public double AzimuthVelocity
        {
            get => _azimuthVelocity;
            set { _azimuthVelocity = value; OnPropertyChanged(nameof(AzimuthVelocity)); }
        }

        public double AltitudeVelocity
        {
            get => _altitudeVelocity;
            set { _altitudeVelocity = value; OnPropertyChanged(nameof(AltitudeVelocity)); }
        }

        public double Range
        {
            get => _range;
            set { _range = value; OnPropertyChanged(nameof(Range)); }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}

