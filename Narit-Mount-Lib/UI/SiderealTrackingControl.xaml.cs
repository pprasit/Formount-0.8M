using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ForkMount_WPF.Engine;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Catalog;
using ForkMount_WPF.Engine.Utility;
using PWLib.Astro;

namespace ForkMount_WPF.UI
{
    public partial class SiderealTrackingControl : UserControl
    {
        private ObserverLocation _observerLocation;
        private ObservableCollection<BrightStarInfo> _brightStars;  // Visible stars only (displayed in DataGrid)
        private List<BrightStarInfo> _allStars;                     // All loaded stars from catalog
        
        // Sidereal tracking controller
        private SiderealTracking _trackingController;
        
        // [NEW] Star position update timer (1000ms = 1 Hz) - updates visible stars only
        private DispatcherTimer _starUpdateTimer;
        
        // [NEW] Visibility check timer (60000ms = 1/min) - filters visible stars
        private DispatcherTimer _visibilityCheckTimer;
        
        // Auto tracking state
        private bool _isAutoTracking = false;
        private bool _isSingleGoto = false;
        private int _currentStarIndex = 0;
        private const double ARRIVAL_THRESHOLD_DEG = 0.5; // Within 0.5 degrees = arrived
        private const int DWELL_TIME_SECONDS = 10; // Stay at star for 10 seconds
        
        // [NEW] Store CentralizedPollingManager reference instead of Info objects
        // This ensures we ALWAYS get the latest instance
        private CentralizedPollingManager _pollingManager;

        // [NEW] RMS Error Tracking
        private List<double> _azimuthErrorSamples = new List<double>();
        private List<double> _altitudeErrorSamples = new List<double>();
        private const int RMS_SAMPLE_INTERVAL_TICKS = 7; // Sample every 7 ticks (7 * 15ms = 105ms ~ 100ms)
        private const int MAX_RMS_SAMPLES = 100; // Keep last 100 samples (~10 seconds)
        private const int MIN_RMS_SAMPLES = 1; // Start calculating RMS immediately for testing

        public SiderealTrackingControl()
        {
            InitializeComponent();
            _brightStars = new ObservableCollection<BrightStarInfo>();
            _allStars = new List<BrightStarInfo>();
            dgBrightStars.ItemsSource = _brightStars;
            
            LoadObserverLocation();
            LoadBrightStarCatalog();
            // [REMOVED] InitializeTrackingController(); // Moved to SetPollingManager() to avoid null _pollingManager
            // [REMOVED] LoadGainsFromSettings(); // Gains are now managed through SiderealTuningWindow
            InitializeStarUpdateTimer();
            InitializeVisibilityCheckTimer();
        }

        private void InitializeTrackingController()
        {
            try
            {
                // [NEW PATTERN] Initialize tracking controller with PollingManager
                _trackingController = new SiderealTracking(_pollingManager);
                
                // Load PD gains from settings
                var gains = AppSettings.GetSiderealTrackingPDGains();
                _trackingController.SetGains(
                    new PDControllerGains { Kp = gains.Azimuth_Kp, Kd = gains.Azimuth_Kd },
                    new PDControllerGains { Kp = gains.Altitude_Kp, Kd = gains.Altitude_Kd }
                );
                
                // [REMOVED] No more UI timer needed - SiderealTracking Engine handles everything
                // Engine has internal 15ms timer for control + RMS target updates
                
                System.Diagnostics.Debug.WriteLine("[SIDEREAL] Controller initialized (Engine pattern - no UI timer)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ERROR] Failed to initialize controller: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize star position update timer (updates every 1 second)
        /// Updates ONLY visible stars in _brightStars collection
        /// </summary>
        private void InitializeStarUpdateTimer()
        {
            try
            {
                // Initialize star update timer (1000ms = 1 Hz)
                _starUpdateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000)
                };
                _starUpdateTimer.Tick += StarUpdateTimer_Tick;
                _starUpdateTimer.Start(); // Start immediately
                
                System.Diagnostics.Debug.WriteLine("[SIDEREAL] Star update timer initialized (1 Hz)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ERROR] Failed to initialize star update timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize visibility check timer (every 1 minute)
        /// Filters stars from _allStars with Altitude > 0� and populates _brightStars
        /// </summary>
        private void InitializeVisibilityCheckTimer()
        {
            try
            {
                // Initialize visibility check timer (60000ms = 1 minute)
                _visibilityCheckTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(60000)
                };
                _visibilityCheckTimer.Tick += VisibilityCheckTimer_Tick;
                _visibilityCheckTimer.Start(); // Start immediately
                
                // Perform initial visibility check
                VisibilityCheckTimer_Tick(null, null);
                
                System.Diagnostics.Debug.WriteLine("[SIDEREAL] Visibility check timer initialized (1/min)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ERROR] Failed to initialize visibility check timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Star position update timer tick (every 1 second)
        /// Updates Azimuth, Altitude, and Visible status for visible stars ONLY
        /// </summary>
        private void StarUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Update ONLY visible stars in _brightStars
                foreach (var star in _brightStars)
                {
                    UpdateStarPosition(star);
                }
                
                // Update star count display
                UpdateStarCount();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[STAR UPDATE ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Visibility check timer tick (every 1 minute)
        /// Filters stars from _allStars catalog where Altitude > 0� and updates _brightStars
        /// Selects TOP 500 BRIGHTEST visible stars
        /// </summary>
        private void VisibilityCheckTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Calculate positions for ALL stars (this may take 1-2 seconds for 10,000 stars)
                foreach (var star in _allStars)
                {
                    UpdateStarPosition(star);
                }
                
                // Filter visible stars (Altitude > 0�) and select TOP 500 BRIGHTEST
                var visibleStars = _allStars
                    .Where(s => s.IsVisible)           // Only above horizon
                    .OrderBy(s => s.Magnitude)          // Brightest first (lowest magnitude)
                    .Take(500)                          // Take only 500 brightest
                    .ToList();
                
                // Update _brightStars collection (displayed in DataGrid)
                _brightStars.Clear();
                foreach (var star in visibleStars)
                {
                    _brightStars.Add(star);
                }
                
                // Update star count
                UpdateStarCount();
                
                int totalVisible = _allStars.Count(s => s.IsVisible);
                System.Diagnostics.Debug.WriteLine($"[VISIBILITY CHECK] Found {totalVisible} visible stars out of {_allStars.Count} total, showing top 500 brightest");
                SimpleLogger.Info($"[SIDEREAL] Visible stars: {totalVisible}, displaying top {_brightStars.Count} brightest", "Sidereal");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VISIBILITY CHECK ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Set CentralizedPollingManager reference (called from parent window)
        /// This ensures we ALWAYS use the correct AzimuthInfo/AltitudeInfo instances
        /// </summary>
        public void SetPollingManager(CentralizedPollingManager pollingManager)
        {
            _pollingManager = pollingManager;
            
            if (_pollingManager != null)
            {
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ?] CentralizedPollingManager linked (Hash: {_pollingManager.GetHashCode()})");
                
                // [CRITICAL FIX] Initialize tracking controller AFTER PollingManager is set
                // This ensures _trackingController is created with valid _pollingManager reference
                InitializeTrackingController();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ?] CentralizedPollingManager is NULL!");
            }
        }

        // [REMOVED] ControlTimer_Tick - No longer needed!
        // SiderealTracking Engine handles everything internally:
        //   - 15ms timer for control loop
        //   - RMS target updates
        //   - Velocity commands
        // UI only needs to display status, not participate in control loop

        private void MoveToNextStar()
        {
            // Find next visible star
            var visibleStars = _brightStars.Where(s => s.IsVisible).OrderBy(s => s.Magnitude).ToList();
            
            if (visibleStars.Count == 0)
            {
                StopAutoTracking();
                txtStatus.Text = "No visible stars available";
                return;
            }

            _currentStarIndex = (_currentStarIndex + 1) % visibleStars.Count;
            var nextStar = visibleStars[_currentStarIndex];
            var (targetRaHours, targetDecDeg) = GetTrackingCoordinates(nextStar, DateTime.UtcNow);

            // Set new target
            _trackingController.SetTarget(targetRaHours, targetDecDeg, _observerLocation);
            
            // [NEW] Clear RMS samples when changing target to avoid spike
            ClearRmsSamples();
            
            // [NEW] Update highlight

            foreach (var star in _brightStars)
            {
                star.IsCurrentTarget = (star == nextStar);
            }

            // Update UI
            txtRA.Text = $"{(int)nextStar.RightAscension:00}:{(int)((nextStar.RightAscension - (int)nextStar.RightAscension) * 60):00}:00.0";
            txtDec.Text = $"{(nextStar.Declination >= 0 ? "+" : "-")}{Math.Abs((int)nextStar.Declination):00}:00:00.0";
            txtStatus.Text = $"Moving to star {_currentStarIndex + 1}/{visibleStars.Count}: {nextStar.Name}";

            System.Diagnostics.Debug.WriteLine($"[AUTO TRACK] Moving to next star: {nextStar.Name}");
        }

        /// <summary>
        /// Stop Track button clicked
        /// </summary>
        private void BtnStopTrack_Click(object sender, RoutedEventArgs e)
        {
            StopAutoTracking();
        }

        /// <summary>
        /// Check if RA/Dec textboxes have values and enable/disable Track button
        /// </summary>
        private void TxtCoordinate_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTrackButtonState();
        }

        /// <summary>
        /// Update Track button enabled state based on RA/Dec having values
        /// </summary>
        private void UpdateTrackButtonState()
        {
            if (btnStartTrack == null || txtRA == null || txtDec == null) return;
            
            // Enable Track button if both textboxes have values
            bool hasRA = !string.IsNullOrWhiteSpace(txtRA.Text);
            bool hasDec = !string.IsNullOrWhiteSpace(txtDec.Text);
            
            btnStartTrack.IsEnabled = hasRA && hasDec;
        }

        private void BtnStartTrack_Click(object sender, RoutedEventArgs e)
        {
            StartTrackingFromCoordinates();
        }
        
        /// <summary>
        /// Start tracking from RA/Dec coordinates in textboxes
        /// </summary>
        private void StartTrackingFromCoordinates()
        {
            try
            {
                // Parse RA
                if (!TryParseRA(txtRA.Text.Trim(), out double ra))
                {
                    SimpleLogger.Warning($"[SIDEREAL] Invalid RA format: {txtRA.Text}", "Sidereal");
                    txtRA.Focus();
                    return;
                }

                // Parse Dec
                if (!TryParseDec(txtDec.Text.Trim(), out double dec))
                {
                    SimpleLogger.Warning($"[SIDEREAL] Invalid Dec format: {txtDec.Text}", "Sidereal");
                    txtDec.Focus();
                    return;
                }

                // Validate ranges
                if (ra < 0 || ra >= 24)
                {
                    SimpleLogger.Warning("[SIDEREAL] RA must be between 0 and 24 hours", "Sidereal");
                    return;
                }

                if (dec < -90 || dec > 90)
                {
                    SimpleLogger.Warning("[SIDEREAL] Dec must be between -90 and +90 degrees", "Sidereal");
                    return;
                }

                // Check if drives are available
                if (_pollingManager == null)
                {
                    SimpleLogger.Error("[SIDEREAL] CentralizedPollingManager not available.", "Sidereal");
                    return;
                }

                // Get selected star name (if any)
                string objectName = "Manual RA/Dec";
                if (dgBrightStars.SelectedItem is BrightStarInfo selectedStar)
                {
                    objectName = selectedStar.Name;
                    if (IsSameCatalogCoordinates(selectedStar, ra, dec))
                    {
                        (ra, dec) = GetTrackingCoordinates(selectedStar, DateTime.UtcNow);
                    }
                }

                // Request tracking from TrackingManager (will stop other tracking types)
                var target = new TrackingTarget
                {
                    ObjectType = TrackingObjectType.BrightStar,
                    ObjectName = objectName,
                    RightAscension = ra,
                    Declination = dec,
                    SourceObject = dgBrightStars.SelectedItem
                };

                bool granted = TrackingManager.Instance.RequestStartTracking(
                    TrackingObjectType.BrightStar,
                    target,
                    () => Dispatcher.Invoke(() => StopTrackingInternal())
                );

                if (!granted)
                {
                    SimpleLogger.Warning("[SIDEREAL] Tracking request denied by TrackingManager", "Sidereal");
                    return;
                }

                // If already tracking, stop current tracking first
                if (_isAutoTracking || _isSingleGoto)
                {
                    // Clear current target highlight
                    foreach (var star in _brightStars)
                    {
                        star.IsCurrentTarget = false;
                    }
                    
                    // Stop current tracking (but don't reset UI completely)
                    _isAutoTracking = false;
                    _isSingleGoto = false;
                    _trackingController?.StopTracking();
                    
                    // Stop drives momentarily
                    if (_pollingManager != null)
                    {
                        if (_pollingManager.AzimuthInfo != null)
                            _pollingManager.AzimuthInfo.Command_Velocity_Deg_Sec = 0;
                        if (_pollingManager.AltitudeInfo != null)
                            _pollingManager.AltitudeInfo.Command_Velocity_Deg_Sec = 0;
                    }
                }

                // Set target and start tracking
                _trackingController.SetTarget(ra, dec, _observerLocation);
                
                // Clear RMS samples BEFORE starting tracking
                ClearRmsSamples();
                
                _trackingController.StartTracking();
                
                // Highlight the target star in table (if it's in the visible list)
                foreach (var star in _brightStars)
                {
                    bool isTarget = Math.Abs(star.RightAscension - ra) < 0.001 && Math.Abs(star.Declination - dec) < 0.1;
                    star.IsCurrentTarget = isTarget;
                }
                
                _isSingleGoto = true;
                _isAutoTracking = false;
                
                // Update button states
                btnStopTrack.IsEnabled = true;
                
                txtStatus.Text = $"Tracking: {objectName} - RA {FormatRA(ra)}, Dec {FormatDec(dec)}";
                SimpleLogger.Info($"[SIDEREAL] Started tracking: {objectName} RA={ra:F6}h, Dec={dec:F6}�", "Sidereal");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SIDEREAL] Start tracking error: {ex.Message}", "Sidereal");
            }
        }

        private void StartAutoTracking()
        {
            try
            {
                // Check if drives are available
                if (_pollingManager == null)
                {
                    SimpleLogger.Error("[SIDEREAL] CentralizedPollingManager not available. Ensure drives are connected and initialized.", "Sidereal");
                    return;
                }

                // Get visible stars sorted by brightness
                var visibleStars = _brightStars.Where(s => s.IsVisible).OrderBy(s => s.Magnitude).ToList();
                
                if (visibleStars.Count == 0)
                {
                    SimpleLogger.Warning("[SIDEREAL] No visible stars found. Check location and time settings.", "Sidereal");
                    return;
                }

                var firstStar = visibleStars[0];
                var (targetRaHours, targetDecDeg) = GetTrackingCoordinates(firstStar, DateTime.UtcNow);
                
                // Request tracking from TrackingManager (will stop other tracking types)
                var target = new TrackingTarget
                {
                    ObjectType = TrackingObjectType.BrightStar,
                    ObjectName = firstStar.Name,
                    RightAscension = targetRaHours,
                    Declination = targetDecDeg,
                    SourceObject = firstStar
                };

                bool granted = TrackingManager.Instance.RequestStartTracking(
                    TrackingObjectType.BrightStar,
                    target,
                    () => Dispatcher.Invoke(() => StopTrackingInternal())
                );

                if (!granted)
                {
                    SimpleLogger.Warning("[SIDEREAL] Auto tracking request denied by TrackingManager", "Sidereal");
                    return;
                }

                // Start with first star
                _currentStarIndex = 0;
                _isAutoTracking = true;
                _isSingleGoto = false;
                
                _trackingController.SetTarget(targetRaHours, targetDecDeg, _observerLocation);
                
                // [NEW] Clear RMS samples BEFORE starting tracking
                ClearRmsSamples();
                
                _trackingController.StartTracking();
                
                // [NEW] Highlight current target star


                foreach (var star in _brightStars)
                {
                    star.IsCurrentTarget = (star == firstStar);
                }
                
                // [REMOVED] No more _controlTimer - Engine handles everything!
                
                // Update button states
                btnStopTrack.IsEnabled = true;
                
                txtStatus.Text = $"Auto tracking started - Moving to {firstStar.Name} ({visibleStars.Count} stars)";
                SimpleLogger.Info($"[SIDEREAL] Auto tracking started: {visibleStars.Count} visible stars, first target: {firstStar.Name}", "Sidereal");
                System.Diagnostics.Debug.WriteLine($"[AUTO TRACK] Started with {visibleStars.Count} visible stars");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SIDEREAL] Auto tracking start error: {ex.Message}", "Sidereal");
                System.Diagnostics.Debug.WriteLine($"[AUTO TRACK ERROR] {ex.Message}");
            }
        }


        private void StopAutoTracking()
        {
            // Notify TrackingManager
            TrackingManager.Instance.StopTracking(TrackingObjectType.BrightStar);
            
            StopTrackingInternal();
        }
        
        /// <summary>
        /// Internal method to stop tracking (called by TrackingManager callback)
        /// </summary>
        private void StopTrackingInternal()
        {
            _isAutoTracking = false;
            _isSingleGoto = false;
            _trackingController?.StopTracking();
            // [REMOVED] _controlTimer?.Stop(); - No more UI timer!
            
            // [NEW] Clear RMS samples but keep last RMS values for reference
            _azimuthErrorSamples.Clear();
            _altitudeErrorSamples.Clear();
            
            if (_pollingManager != null)
            {
                // [CRITICAL FIX] Keep RMS values AND sample count to preserve tracking quality display
                // Do NOT reset TrackingSampleCount - this allows UI to show final RMS error
                // Only update tracking state flags
                _pollingManager.AzimuthInfo.IsTracking = false;
                _pollingManager.AzimuthInfo.TrackingMode = TrackingMode.None;
                
                _pollingManager.AltitudeInfo.IsTracking = false;
                _pollingManager.AltitudeInfo.TrackingMode = TrackingMode.None;
                
                SimpleLogger.Info($"[SIDEREAL] Tracking stopped - Final RMS: Az={_pollingManager.AzimuthInfo.TrackingRmsError_Arcsec:F2}\", Alt={_pollingManager.AltitudeInfo.TrackingRmsError_Arcsec:F2}\" (samples={_pollingManager.AzimuthInfo.TrackingSampleCount})", "Sidereal");
            }
            
            // [NEW] Clear target highlight


            foreach (var star in _brightStars)
            {
                star.IsCurrentTarget = false;
            }
            
            // Stop drives via CentralizedPollingManager
            if (_pollingManager != null)
            {
                if (_pollingManager.AzimuthInfo != null)
                    _pollingManager.AzimuthInfo.Command_Velocity_Deg_Sec = 0;
                if (_pollingManager.AltitudeInfo != null)
                    _pollingManager.AltitudeInfo.Command_Velocity_Deg_Sec = 0;
                
                // [CRITICAL] Clear RMS Manager targets to stop RMS calculation
                _pollingManager.RmsTrackingManager?.SetAzimuthTarget(null);
                _pollingManager.RmsTrackingManager?.SetAltitudeTarget(null);
            }
            
            // Update button states
            btnStopTrack.IsEnabled = false;
            
            txtStatus.Text = "Tracking stopped";
            SimpleLogger.Info("[SIDEREAL] Tracking stopped", "Sidereal");
            System.Diagnostics.Debug.WriteLine("[SIDEREAL] Tracking stopped");
        }
        
        private void BtnAdvancedTuning_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_trackingController == null)
                {
                    MessageBox.Show("Tracking controller not initialized!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                // Create tuning window with Owner (non-modal)
                var tuningWindow = new SiderealTuningWindow(_trackingController)
                {
                    Owner = Window.GetWindow(this)
                };
                
                // Show as non-modal window (can interact with both windows)
                tuningWindow.Show();
                
                System.Diagnostics.Debug.WriteLine("[SIDEREAL] Tuning window opened (non-modal)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open tuning window: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ERROR] Tuning window: {ex.Message}");
            }
        }

        // Keep all the existing helper methods (LoadObserverLocation, LoadBrightStarCatalog, TryParseRA, etc.)
        // ... (insert all remaining methods from original file)
        
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
                
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL] Observer Location loaded: {location.LocationName ?? "Unknown"} ({location.Latitude_Deg:F6}, {location.Longitude_Deg:F6}, {location.Altitude_Meters:F0}m)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ERROR] Failed to load location: {ex.Message}");
                
                // Use defaults from AppSettings model if load fails
                var defaultLocation = new ObserverLocationSettings();
                _observerLocation = new ObserverLocation(
                    defaultLocation.Latitude_Deg, 
                    defaultLocation.Longitude_Deg, 
                    defaultLocation.Altitude_Meters
                );
            }
        }

        private void LoadBrightStarCatalog()
        {
            try
            {
                _allStars.Clear();

                // Load catalog from BrightStarCatalog class
                var stars = BrightStarCatalog.GetCatalog();
                if (stars == null || stars.Count == 0)
                {
                    txtStatus.Text = "Bright star catalog unavailable";
                    SimpleLogger.Error("[SIDEREAL] Bright star catalog is unavailable.", "Sidereal");
                    return;
                }
                
                // Load MORE stars (~5000-10000 stars, magnitude <= 6.0)
                // This ensures we have enough visible stars above horizon
                var brightestStars = stars
                    .Where(s => s.Magnitude <= 6.0)  // Brighter threshold (was 6.5)
                    .OrderBy(s => s.Magnitude)        // Brightest first
                    .Take(10000)                      // Take up to 10,000 stars
                    .ToList();
                
                foreach (var star in brightestStars)
                {
                    var brightStar = new BrightStarInfo
                    {
                        Name = star.Name ?? star.Bayer ?? $"HD {star.HD}",
                        RightAscension = star.RA,
                        Declination = star.Dec,
                        ProperMotionRaArcsecPerYear = star.ProperMotionRA_ArcsecPerYear,
                        ProperMotionDecArcsecPerYear = star.ProperMotionDec_ArcsecPerYear,
                        Magnitude = star.Magnitude,
                        SpectralType = star.SpectralType ?? "Unknown"
                    };
                    
                    // Add to ALL stars list (not displayed yet)
                    _allStars.Add(brightStar);
                }

                System.Diagnostics.Debug.WriteLine($"[SIDEREAL] Loaded {_allStars.Count} stars from catalog (magnitude <= 6.0)");
                SimpleLogger.Info($"[SIDEREAL] Loaded {_allStars.Count} stars from catalog", "Sidereal");
                
                // Initial visibility check will populate _brightStars
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SIDEREAL ERROR] Failed to load catalog: {ex.Message}");
                txtStatus.Text = "Bright star catalog load failed";
            }
        }

        private void UpdateStarPosition(BrightStarInfo star)
        {
            try
            {
                // Use PWLib.Astro for coordinate conversion
                DateTime now = DateTime.UtcNow;
                
                var site = new LatLon(
                    Angle.FromDegs(_observerLocation.Latitude), 
                    Angle.FromDegs(_observerLocation.Longitude));
                double jd = AstroTime.JulianDay(now);
                
                var (trackingRaHours, trackingDecDeg) = GetTrackingCoordinates(star, now);
                var raDecJ2000 = RaDec.FromHoursDegs(trackingRaHours, trackingDecDeg);
                
                // Precess J2000 ? Apparent, then convert to Alt/Az
                var raDecApparent = raDecJ2000.J2000ToNow();
                var altAz = raDecApparent.ToAltAz(site, jd);
                
                star.Altitude = altAz.Alt.Degs;
                star.Azimuth = altAz.Az.Degs;
                star.IsVisible = star.Altitude > 0.0; // Visible if above horizon (0 degrees)
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[STAR CALC ERROR] {star.Name}: {ex.Message}");
            }
        }

        private (double raHours, double decDeg) GetTrackingCoordinates(BrightStarInfo star, DateTime utc)
        {
            if (star == null)
            {
                return (0.0, 0.0);
            }

            return BrightStarCatalog.GetAdjustedJ2000Coordinates(
                new BrightStar
                {
                    RA = star.RightAscension,
                    Dec = star.Declination,
                    ProperMotionRA_ArcsecPerYear = star.ProperMotionRaArcsecPerYear,
                    ProperMotionDec_ArcsecPerYear = star.ProperMotionDecArcsecPerYear
                },
                utc);
        }

        private bool IsSameCatalogCoordinates(BrightStarInfo star, double raHours, double decDeg)
        {
            if (star == null)
            {
                return false;
            }

            return Math.Abs(star.RightAscension - raHours) < 1e-4 &&
                   Math.Abs(star.Declination - decDeg) < 1e-3;
        }

        /// <summary>
        /// Calculate horizontal coordinates (Alt/Az) from hour angle and declination using PWLib.Astro
        /// </summary>
        private (double altitude, double azimuth) CalculateHorizontalCoordinates(
            double hourAngle, double declination, double latitude)
        {
            var ha = Angle.FromHours(hourAngle);
            var dec = Angle.FromDegs(declination);
            var lat = Angle.FromDegs(latitude);
            
            var haDec = new HaDec(ha, dec);
            var site = new LatLon(lat, Angle.Zero);
            var altAz = haDec.ToAltAz(site);
            
            return (altAz.Alt.Degs, altAz.Az.Degs);
        }

        private void UpdateStarCount()
        {
            int totalLoaded = _allStars.Count;
            int visible = _brightStars.Count;
            txtStarCount.Text = $"Stars: {totalLoaded} | Visible: {visible}";
        }

        /// <summary>
        /// Parse RA from multiple formats:
        /// - Decimal hours: "6.752481"
        /// - HMS colon: "06:45:08.9"
        /// - HMS space: "06 45 08.9"
        /// </summary>
        private bool TryParseRA(string input, out double raHours)
        {
            raHours = 0;
            
            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.Trim();

            // Try decimal format first (e.g., "6.752481")
            if (double.TryParse(input, out raHours))
            {
                return true;
            }

            // Try HMS format with colon separator (e.g., "06:45:08.9")
            if (input.Contains(':'))
            {
                return TryParseHMS(input, ':', out raHours);
            }

            // Try HMS format with space separator (e.g., "06 45 08.9")
            if (input.Contains(' '))
            {
                return TryParseHMS(input, ' ', out raHours);
            }

            return false;
        }

        /// <summary>
        /// Parse Dec from multiple formats:
        /// - Decimal degrees: "-16.716116"
        /// - DMS colon: "+19:10:56.7"
        /// - DMS space: "-52 41 44.4"
        /// </summary>
        private bool TryParseDec(string input, out double decDegrees)
        {
            decDegrees = 0;
            
            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.Trim();

            // Check for sign
            bool isNegative = input.StartsWith("-");
            string absInput = input.TrimStart('+', '-');

            // Try decimal format first (e.g., "-16.716116")
            if (double.TryParse(input, out decDegrees))
            {
                return true;
            }

            // Try DMS format with colon separator (e.g., "19:10:56.7")
            if (absInput.Contains(':'))
            {
                if (TryParseHMS(absInput, ':', out double absDegrees))
                {
                    decDegrees = isNegative ? -absDegrees : absDegrees;
                    return true;
                }
            }

            // Try DMS format with space separator (e.g., "52 41 44.4")
            if (absInput.Contains(' '))
            {
                if (TryParseHMS(absInput, ' ', out double absDegrees))
                {
                    decDegrees = isNegative ? -absDegrees : absDegrees;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Parse HMS/DMS format with specified separator
        /// </summary>
        private bool TryParseHMS(string input, char separator, out double result)
        {
            result = 0;
            
            string[] parts = input.Split(separator);
            
            if (parts.Length < 2 || parts.Length > 3)
                return false;

            if (!int.TryParse(parts[0], out int h) || h < 0)
                return false;

            if (!int.TryParse(parts[1], out int m) || m < 0 || m >= 60)
                return false;

            double s = 0;
            if (parts.Length == 3)
            {
                if (!double.TryParse(parts[2], out s) || s < 0 || s >= 60)
                    return false;
            }

            result = h + m / 60.0 + s / 3600.0;
            return true;
        }

        /// <summary>
        /// Format RA hours to HH:MM:SS.S
        /// </summary>
        private string FormatRA(double raHours)
        {
            int h = (int)raHours;
            int m = (int)((raHours - h) * 60);
            double s = ((raHours - h) * 60 - m) * 60;
            return $"{h:00}:{m:00}:{s:00.0}";
        }

        /// <summary>
        /// Format Dec degrees to �DD:MM:SS.S
        /// </summary>
        private string FormatDec(double decDegrees)
        {
            string sign = decDegrees >= 0 ? "+" : "-";
            double absDeg = Math.Abs(decDegrees);
            int d = (int)absDeg;
            int m = (int)((absDeg - d) * 60);
            double s = ((absDeg - d) * 60 - m) * 60;
            return $"{sign}{d:00}:{m:00}:{s:00.0}";
        }

        private void DgBrightStars_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgBrightStars.SelectedItem is BrightStarInfo star)
            {
                // Convert RA to HMS format
                double raHours = star.RightAscension;
                int raH = (int)raHours;
                int raM = (int)((raHours - raH) * 60);
                double raS = ((raHours - raH) * 60 - raM) * 60;

                // Convert Dec to DMS format
                bool isNegative = star.Declination < 0;
                double decDeg = Math.Abs(star.Declination);
                int decD = (int)decDeg;
                int decM = (int)((decDeg - decD) * 60);
                double decS = ((decDeg - decD) * 60 - decM) * 60;

                // Update textboxes with HMS/DMS format (colon-separated)
                txtRA.Text = $"{raH:00}:{raM:00}:{raS:00.0}";
                txtDec.Text = $"{(isNegative ? "-" : "+")}{decD:00}:{decM:00}:{decS:00.0}";

                txtStatus.Text = $"Selected: {star.Name} (Mag {star.Magnitude:F2})";

                // Notify FITS auto-save of selected object name
                TrackingManager.Instance.NotifyObjectSelected(star.Name);
            }
        }




        // [REMOVED] LoadGainsFromSettings, BtnApplyGains_Click, BtnResetGains_Click
        // These methods are no longer needed - gains are now managed through SiderealTuningWindow
        
        /// <summary>
        /// Calculate and update RMS errors in AzimuthInfo/AltitudeInfo
        /// Called every ~100ms when tracking is active
        /// </summary>
        private void UpdateRmsErrors()
        {
            try
            {
                if (_pollingManager == null) return;

                // Calculate Azimuth RMS Error
                if (_azimuthErrorSamples.Count >= MIN_RMS_SAMPLES)
                {
                    double sumSquaredAz = _azimuthErrorSamples.Sum(e => e * e);
                    double rmsAz_deg = Math.Sqrt(sumSquaredAz / _azimuthErrorSamples.Count);
                    double rmsAz_arcsec = rmsAz_deg * 3600.0; // Convert to arcseconds
                    
                    _pollingManager.AzimuthInfo.TrackingRmsError_Arcsec = rmsAz_arcsec;
                    _pollingManager.AzimuthInfo.TrackingSampleCount = _azimuthErrorSamples.Count;
                    _pollingManager.AzimuthInfo.TrackingMode = TrackingMode.Sidereal;
                    
                    // Set IsTracking based on RMS threshold
                    bool azTrackingGood = rmsAz_arcsec <= TrackingThresholds.SIDEREAL_RMS_ARCSEC;
                    _pollingManager.AzimuthInfo.IsTracking = azTrackingGood;
                }

                // Calculate Altitude RMS Error
                if (_altitudeErrorSamples.Count >= MIN_RMS_SAMPLES)
                {
                    double sumSquaredAlt = _altitudeErrorSamples.Sum(e => e * e);
                    double rmsAlt_deg = Math.Sqrt(sumSquaredAlt / _altitudeErrorSamples.Count);
                    double rmsAlt_arcsec = rmsAlt_deg * 3600.0; // Convert to arcseconds
                    
                    _pollingManager.AltitudeInfo.TrackingRmsError_Arcsec = rmsAlt_arcsec;
                    _pollingManager.AltitudeInfo.TrackingSampleCount = _altitudeErrorSamples.Count;
                    _pollingManager.AltitudeInfo.TrackingMode = TrackingMode.Sidereal;
                    
                    // Set IsTracking based on RMS threshold
                    bool altTrackingGood = rmsAlt_arcsec <= TrackingThresholds.SIDEREAL_RMS_ARCSEC;
                    _pollingManager.AltitudeInfo.IsTracking = altTrackingGood;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RMS ERROR] {ex.Message}");
            }
        }


        /// <summary>
        /// Clear RMS error samples (called when changing target star)
        /// </summary>
        private void ClearRmsSamples()
        {
            _azimuthErrorSamples.Clear();
            _altitudeErrorSamples.Clear();
            
            System.Diagnostics.Debug.WriteLine("[RMS] Samples cleared - target changed");
        }
        
        /// <summary>
        /// Unwrap azimuth from 0-360� to mount's -300 to +300� range
        /// Chooses the equivalent angle that is:
        /// 1. Within mount limits
        /// 2. Closest to current position
        /// </summary>
        private double UnwrapAzimuthToMountRange(double targetAz_0to360, double currentAz, double ccwLimit, double cwLimit)
        {
            // Generate 3 possible equivalent angles
            // Example: target=70� can be: 70�, -290� (70-360), or 430� (70+360)
            double option1 = targetAz_0to360;          // 0-360 range
            double option2 = targetAz_0to360 - 360.0;  // negative equivalent
            double option3 = targetAz_0to360 + 360.0;  // positive equivalent

            // Filter options that are within mount limits
            var validOptions = new List<(double angle, double distance)>();

            if (option1 >= ccwLimit && option1 <= cwLimit)
                validOptions.Add((option1, Math.Abs(option1 - currentAz)));

            if (option2 >= ccwLimit && option2 <= cwLimit)
                validOptions.Add((option2, Math.Abs(option2 - currentAz)));

            if (option3 >= ccwLimit && option3 <= cwLimit)
                validOptions.Add((option3, Math.Abs(option3 - currentAz)));

            // If no valid option (shouldn't happen if limits are correct), use option2
            if (validOptions.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[WARNING] No valid unwrap option for target={targetAz_0to360}�! Using option2={option2}�");
                SimpleLogger.Warning($"[UNWRAP] Target {targetAz_0to360:F2}� cannot fit in mount limits ({ccwLimit:F0}� to {cwLimit:F0}�)", "Sidereal");
                return option2;
            }

            // Choose the option closest to current position
            var best = validOptions.OrderBy(x => x.distance).First();
            System.Diagnostics.Debug.WriteLine($"[UNWRAP] Target {targetAz_0to360:F2}� ? {best.angle:F2}� (distance: {best.distance:F2}�)");
            return best.angle;
        }
    }



    public class BrightStarInfo : INotifyPropertyChanged

    {
        private string _name;
        private double _rightAscension;
        private double _declination;
        private double _properMotionRaArcsecPerYear;
        private double _properMotionDecArcsecPerYear;
        private double _magnitude;
        private string _spectralType;
        private double _azimuth;
        private double _altitude;
        private bool _isVisible;
        private bool _isCurrentTarget;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }
        
        public bool IsCurrentTarget
        {
            get => _isCurrentTarget;
            set { _isCurrentTarget = value; OnPropertyChanged(nameof(IsCurrentTarget)); }
        }

        public double RightAscension
        {
            get => _rightAscension;
            set { _rightAscension = value; OnPropertyChanged(nameof(RightAscension)); OnPropertyChanged(nameof(RAString)); }
        }

        public double Declination
        {
            get => _declination;
            set { _declination = value; OnPropertyChanged(nameof(Declination)); OnPropertyChanged(nameof(DecString)); }
        }

        public double ProperMotionRaArcsecPerYear
        {
            get => _properMotionRaArcsecPerYear;
            set { _properMotionRaArcsecPerYear = value; OnPropertyChanged(nameof(ProperMotionRaArcsecPerYear)); }
        }

        public double ProperMotionDecArcsecPerYear
        {
            get => _properMotionDecArcsecPerYear;
            set { _properMotionDecArcsecPerYear = value; OnPropertyChanged(nameof(ProperMotionDecArcsecPerYear)); }
        }

        public double Magnitude
        {
            get => _magnitude;
            set { _magnitude = value; OnPropertyChanged(nameof(Magnitude)); }
        }

        public string SpectralType
        {
            get => _spectralType;
            set { _spectralType = value; OnPropertyChanged(nameof(SpectralType)); }
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

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); }
        }

        public string RAString
        {
            get
            {
                int h = (int)RightAscension;
                int m = (int)((RightAscension - h) * 60);
                double s = ((RightAscension - h) * 60 - m) * 60;
                return $"{h:00}:{m:00}:{s:00.0}";
            }
        }

        public string DecString
        {
            get
            {
                string sign = Declination >= 0 ? "+" : "-";
                double absDeg = Math.Abs(Declination);
                int d = (int)absDeg;
                int m = (int)((absDeg - d) * 60);
                double s = ((absDeg - d) * 60 - m) * 60;
                return $"{sign}{d:00}:{m:00}:{s:00.0}";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    // [NEW] Extension methods for SiderealTrackingControl
    public partial class SiderealTrackingControl
    {
        /// <summary>
        /// Public method to stop sidereal tracking (called when switching to another tracking mode)
        /// </summary>
        public void StopTracking()
        {
            try
            {
                if (_isAutoTracking || _isSingleGoto)
                {
                    SimpleLogger.Info("[SIDEREAL] Stopping tracking (mode switch)", "Sidereal");
                    
                    _isAutoTracking = false;
                    _isSingleGoto = false;
                    _trackingController?.StopTracking();
                    
                    // Clear RMS samples
                    _azimuthErrorSamples.Clear();
                    _altitudeErrorSamples.Clear();
                    
                    if (_pollingManager != null)
                    {
                        // Update tracking state flags
                        _pollingManager.AzimuthInfo.IsTracking = false;
                        _pollingManager.AzimuthInfo.TrackingMode = TrackingMode.None;
                        
                        _pollingManager.AltitudeInfo.IsTracking = false;
                        _pollingManager.AltitudeInfo.TrackingMode = TrackingMode.None;
                        
                        // Stop drives
                        if (_pollingManager.AzimuthInfo != null)
                            _pollingManager.AzimuthInfo.Command_Velocity_Deg_Sec = 0;
                        if (_pollingManager.AltitudeInfo != null)
                            _pollingManager.AltitudeInfo.Command_Velocity_Deg_Sec = 0;
                        
                        // Clear RMS Manager targets
                        _pollingManager.RmsTrackingManager?.SetAzimuthTarget(null);
                        _pollingManager.RmsTrackingManager?.SetAltitudeTarget(null);
                    }
                    
                    // Clear target highlight
                    foreach (var star in _brightStars)
                    {
                        star.IsCurrentTarget = false;
                    }
                    
                    // Update UI
                    btnStartTrack.Content = "Start Track";
                    btnStartTrack.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                    txtStatus.Text = "Tracking stopped (mode switch)";
                    
                    SimpleLogger.Info("[SIDEREAL] Tracking stopped successfully", "Sidereal");
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SIDEREAL] Error stopping tracking: {ex.Message}", "Sidereal");
            }
        }
    }
}
