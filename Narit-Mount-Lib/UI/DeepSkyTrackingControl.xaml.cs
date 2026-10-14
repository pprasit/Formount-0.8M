using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    public partial class DeepSkyTrackingControl : UserControl
    {
        private ObserverLocation _observerLocation;
        private ObservableCollection<NgcObjectInfo> _displayedObjects;
        private List<NgcObjectInfo> _allObjects;
        private List<string> _constellations;

        // Sidereal tracking controller (same as stars - deep sky objects are sidereal)
        private SiderealTracking _trackingController;

        // Position update timer (1 Hz)
        private DispatcherTimer _positionUpdateTimer;

        // Visibility check timer (1/min)
        private DispatcherTimer _visibilityCheckTimer;

        // Auto tracking state
        private bool _isAutoTracking = false;
        private int _currentObjectIndex = 0;
        private bool _hasArrivedAtObject = false;
        private const double ARRIVAL_THRESHOLD_DEG = 0.5;
        private const int DWELL_TIME_SECONDS = 30; // Longer dwell for deep sky

        // Store CentralizedPollingManager reference
        private CentralizedPollingManager _pollingManager;

        // Catalog loading state
        private bool _catalogLoaded = false;
        private bool _isLoadingCatalog = false;
        private bool _isUpdatingTrackingToggle = false;

        public DeepSkyTrackingControl()
        {
            InitializeComponent();
            _displayedObjects = new ObservableCollection<NgcObjectInfo>();
            _allObjects = new List<NgcObjectInfo>();
            _constellations = new List<string>();
            dgDeepSkyObjects.ItemsSource = _displayedObjects;

            LoadObserverLocation();
            InitializePositionUpdateTimer();
            InitializeVisibilityCheckTimer();
            
            // Auto-load catalog on startup
            Loaded += DeepSkyTrackingControl_Loaded;
        }
        
        private async void DeepSkyTrackingControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Load catalog automatically when control is loaded
            if (!_catalogLoaded && !_isLoadingCatalog)
            {
                await LoadCatalogAsync();
            }
        }

        private void InitializeTrackingController()
        {
            try
            {
                _trackingController = new SiderealTracking(_pollingManager);

                // Load PD gains from settings
                var gains = AppSettings.GetSiderealTrackingPDGains();
                _trackingController.SetGains(
                    new PDControllerGains { Kp = gains.Azimuth_Kp, Kd = gains.Azimuth_Kd },
                    new PDControllerGains { Kp = gains.Altitude_Kp, Kd = gains.Altitude_Kd }
                );

                System.Diagnostics.Debug.WriteLine("[DEEP SKY] Controller initialized");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY ERROR] Failed to initialize controller: {ex.Message}");
            }
        }

        private void InitializePositionUpdateTimer()
        {
            try
            {
                _positionUpdateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000)
                };
                _positionUpdateTimer.Tick += PositionUpdateTimer_Tick;
                _positionUpdateTimer.Start();

                System.Diagnostics.Debug.WriteLine("[DEEP SKY] Position update timer initialized (1 Hz)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY ERROR] Timer init failed: {ex.Message}");
            }
        }

        private void InitializeVisibilityCheckTimer()
        {
            try
            {
                _visibilityCheckTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(60000)
                };
                _visibilityCheckTimer.Tick += VisibilityCheckTimer_Tick;
                _visibilityCheckTimer.Start();

                System.Diagnostics.Debug.WriteLine("[DEEP SKY] Visibility check timer initialized (1/min)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY ERROR] Visibility timer init failed: {ex.Message}");
            }
        }

        private void PositionUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                foreach (var obj in _displayedObjects)
                {
                    UpdateObjectPosition(obj);
                }
                UpdateObjectCount();

                // Handle auto tracking advancement
                if (_isAutoTracking && _hasArrivedAtObject)
                {
                    MoveToNextObject();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY UPDATE ERROR] {ex.Message}");
            }
        }

        private void VisibilityCheckTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (!_catalogLoaded) return;

                foreach (var obj in _allObjects)
                {
                    UpdateObjectPosition(obj);
                }
                ApplyFilters();

                int totalVisible = _allObjects.Count(o => o.IsVisible);
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY VISIBILITY] Found {totalVisible} visible objects out of {_allObjects.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY VISIBILITY ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Set CentralizedPollingManager reference (called from parent window)
        /// </summary>
        public void SetPollingManager(CentralizedPollingManager pollingManager)
        {
            _pollingManager = pollingManager;

            if (_pollingManager != null)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY] CentralizedPollingManager linked");
                InitializeTrackingController();
            }
        }

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

                System.Diagnostics.Debug.WriteLine($"[DEEP SKY] Observer Location loaded: {location.LocationName ?? "Unknown"}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY ERROR] Failed to load location: {ex.Message}");
                var defaultLocation = new ObserverLocationSettings();
                _observerLocation = new ObserverLocation(
                    defaultLocation.Latitude_Deg,
                    defaultLocation.Longitude_Deg,
                    defaultLocation.Altitude_Meters
                );
            }
        }

        /// <summary>
        /// Load catalog automatically (called on control load and filter change)
        /// </summary>
        private async Task LoadCatalogAsync()
        {
            if (_isLoadingCatalog) return;
            
            try
            {
                _isLoadingCatalog = true;
                txtStatus.Text = "Loading NGC catalog...";

                var service = NgcCatalogService.Instance;
                if (service == null)
                {
                    txtStatus.Text = "MongoDB unavailable - loading OpenNGC catalog...";
                    System.Diagnostics.Debug.WriteLine("[DEEP SKY] MongoDB unavailable - using local OpenNGC catalog");
                    _allObjects = NgcCatalogService.LoadLocalCatalog();
                    _constellations = _allObjects.Select(o => o.Constellation).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).ToList();
                    PopulateConstellationFilter();
                    _catalogLoaded = _allObjects.Count > 0;
                    foreach (var obj in _allObjects)
                    {
                        UpdateObjectPosition(obj);
                    }
                    ApplyFilters();
                    txtStatus.Text = _catalogLoaded ? $"Loaded {_allObjects.Count} deep sky objects from OpenNGC" : "Deep sky catalog unavailable";
                    return;
                }

                // Load popular objects (Messier + bright NGC)
                _allObjects = await service.LoadPopularObjectsAsync(1000);
                
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY] Raw loaded: {_allObjects.Count} objects");

                // If no objects loaded from MongoDB, use fallback
                if (_allObjects == null || _allObjects.Count == 0)
                {
                    txtStatus.Text = "Deep sky catalog is empty";
                    System.Diagnostics.Debug.WriteLine("[DEEP SKY] No data from MongoDB - no fallback catalog will be used");
                    _allObjects = new List<NgcObjectInfo>();
                    _displayedObjects.Clear();
                    _catalogLoaded = false;
                    UpdateObjectCount();
                    return;
                }

                // Calculate initial positions
                foreach (var obj in _allObjects)
                {
                    UpdateObjectPosition(obj);
                }
                
                int visibleCount = _allObjects.Count(o => o.IsVisible);
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY] After position calc: {visibleCount} visible of {_allObjects.Count}");

                // Load constellations for filter
                _constellations = await service.GetDistinctConstellationsAsync();
                PopulateConstellationFilter();

                _catalogLoaded = true;
                ApplyFilters();

                txtStatus.Text = $"Loaded {_allObjects.Count} deep sky objects";
                SimpleLogger.Info($"[DEEP SKY] Loaded {_allObjects.Count} NGC objects from MongoDB", "DeepSky");
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Deep sky catalog error: {ex.Message}";
                SimpleLogger.Error($"[DEEP SKY] Load failed: {ex.Message}", "DeepSky");
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY ERROR] {ex.Message}\n{ex.StackTrace}");
                _allObjects = new List<NgcObjectInfo>();
                _displayedObjects.Clear();
                _catalogLoaded = false;
                UpdateObjectCount();
            }
            finally
            {
                _isLoadingCatalog = false;
            }
        }

        private string FormatRAString(double raHours)
        {
            int h = (int)raHours;
            int m = (int)((raHours - h) * 60);
            double s = ((raHours - h) * 60 - m) * 60;
            return $"{h:00}:{m:00}:{s:00.0}";
        }

        private string FormatDecString(double decDeg)
        {
            string sign = decDeg >= 0 ? "+" : "-";
            double absDeg = Math.Abs(decDeg);
            int d = (int)absDeg;
            int m = (int)((absDeg - d) * 60);
            double s = ((absDeg - d) * 60 - m) * 60;
            return $"{sign}{d:00}:{m:00}:{s:00.0}";
        }

        private void PopulateConstellationFilter()
        {
            cboConstellation.Items.Clear();
            cboConstellation.Items.Add(new ComboBoxItem { Content = "All", IsSelected = true });
            foreach (var constellation in _constellations)
            {
                cboConstellation.Items.Add(new ComboBoxItem { Content = constellation });
            }
        }

        private void ApplyFilters()
        {
            if (!_catalogLoaded) return;

            IEnumerable<NgcObjectInfo> filtered = _allObjects;

            // Category filter
            string category = (cboCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Objects";
            filtered = category switch
            {
                "Messier Only" => filtered.Where(o => !string.IsNullOrEmpty(o.Messier)),
                "Galaxies" => filtered.Where(o => o.Type == "G" || o.Type == "Gx"),
                "Open Clusters" => filtered.Where(o => o.Type == "OC"),
                "Globular Clusters" => filtered.Where(o => o.Type == "GC"),
                "Nebulae" => filtered.Where(o => o.Type == "Nb" || o.Type == "RfN" || o.Type == "EmN" || o.Type == "C+N"),
                "Planetary Nebulae" => filtered.Where(o => o.Type == "PN"),
                _ => filtered
            };

            // Constellation filter
            string constellation = (cboConstellation.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
            if (constellation != "All")
            {
                filtered = filtered.Where(o => o.Constellation == constellation);
            }

            // Visible only filter
            if (chkVisibleOnly.IsChecked == true)
            {
                filtered = filtered.Where(o => o.IsVisible);
            }

            // Sort by magnitude (brightest first)
            filtered = filtered.OrderBy(o => o.Magnitude);

            // Update displayed collection
            _displayedObjects.Clear();
            foreach (var obj in filtered.Take(500))
            {
                _displayedObjects.Add(obj);
            }

            UpdateObjectCount();
        }

        private void UpdateObjectPosition(NgcObjectInfo obj)
        {
            try
            {
                DateTime now = DateTime.UtcNow;

                var site = new LatLon(
                    Angle.FromDegs(_observerLocation.Latitude),
                    Angle.FromDegs(_observerLocation.Longitude));
                double jd = AstroTime.JulianDay(now);

                var raDecJ2000 = RaDec.FromHoursDegs(obj.RightAscension, obj.Declination);
                var raDecApparent = raDecJ2000.J2000ToNow();
                var altAz = raDecApparent.ToAltAz(site, jd);

                obj.Altitude = altAz.Alt.Degs;
                obj.Azimuth = altAz.Az.Degs;
                obj.IsVisible = obj.Altitude > 10.0; // Visible if above 10 degrees
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY CALC ERROR] {obj.Name}: {ex.Message}");
            }
        }

        private void UpdateObjectCount()
        {
            int totalLoaded = _allObjects.Count;
            int visible = _displayedObjects.Count(o => o.IsVisible);
            int displayed = _displayedObjects.Count;
            txtObjectCount.Text = $"Loaded: {totalLoaded} | Displayed: {displayed} | Visible: {visible}";
        }

        private void CboCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void CboConstellation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ChkVisibleOnly_Changed(object sender, RoutedEventArgs e)
        {
            ApplyFilters();
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
            
            // Enable Track button if both textboxes have values (can change target while tracking)
            bool hasRA = !string.IsNullOrWhiteSpace(txtRA.Text);
            bool hasDec = !string.IsNullOrWhiteSpace(txtDec.Text);
            
            btnStartTrack.IsEnabled = hasRA && hasDec;
        }

        private void SetTrackingToggleState(bool isTracking)
        {
            if (chkTrackingOn == null) return;

            _isUpdatingTrackingToggle = true;
            chkTrackingOn.IsChecked = isTracking;
            _isUpdatingTrackingToggle = false;
        }

        private void DgDeepSkyObjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgDeepSkyObjects.SelectedItem is NgcObjectInfo obj)
            {
                // Format RA
                double raHours = obj.RightAscension;
                int raH = (int)raHours;
                int raM = (int)((raHours - raH) * 60);
                double raS = ((raHours - raH) * 60 - raM) * 60;

                // Format Dec
                bool isNegative = obj.Declination < 0;
                double decDeg = Math.Abs(obj.Declination);
                int decD = (int)decDeg;
                int decM = (int)((decDeg - decD) * 60);
                double decS = ((decDeg - decD) * 60 - decM) * 60;

                txtRA.Text = $"{raH:00}:{raM:00}:{raS:00.0}";
                txtDec.Text = $"{(isNegative ? "-" : "+")}{decD:00}:{decM:00}:{decS:00.0}";

                txtStatus.Text = $"Selected: {obj.DisplayName} ({obj.TypeDescription}) - {obj.Constellation}";

                // Notify FITS auto-save of selected object name
                TrackingManager.Instance.NotifyObjectSelected(obj.DisplayName);
            }
        }

        /// <summary>
        /// Start Track button clicked
        /// </summary>
        private void BtnStartTrack_Click(object sender, RoutedEventArgs e)
        {
            StartTrackingFromCoordinates();
        }

        /// <summary>
        /// Stop Track button clicked
        /// </summary>
        private void BtnStopTrack_Click(object sender, RoutedEventArgs e)
        {
            StopTracking();
        }

        private void ChkTrackingOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingTrackingToggle) return;

            if (chkTrackingOn.IsChecked == true)
            {
                StartTrackingFromCurrentMountPosition();
            }
            else
            {
                StopTracking();
            }
        }

        private void StartTrackingFromCurrentMountPosition()
        {
            try
            {
                if (_pollingManager?.MountInfo == null)
                {
                    SimpleLogger.Warning("[DEEP SKY] Mount position is not available.", "DeepSky");
                    SetTrackingToggleState(false);
                    return;
                }

                double azimuth = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                double altitude = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;

                if (double.IsNaN(azimuth) || double.IsNaN(altitude))
                {
                    SimpleLogger.Warning("[DEEP SKY] Current mount Az/Alt is invalid.", "DeepSky");
                    SetTrackingToggleState(false);
                    return;
                }

                var (raHours, decDegrees) = ConvertAltAzToRaDec(altitude, azimuth, DateTime.UtcNow);

                string raText = FormatRAString(raHours);
                string decText = FormatDecString(decDegrees);

                txtRA.Text = raText;
                txtDec.Text = decText;
                txtStatus.Text = $"Tracking target locked from current mount position: Az {azimuth:F3} deg, Alt {altitude:F3} deg -> RA {raText}, Dec {decText}";

                StartTrackingFromCoordinates();
            }
            catch (Exception ex)
            {
                SetTrackingToggleState(false);
                MessageBox.Show($"Failed to start tracking from current mount position: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Start tracking from RA/Dec coordinates in textboxes
        /// </summary>
        private void StartTrackingFromCoordinates()
        {
            try
            {
                if (!TryParseRA(txtRA.Text.Trim(), out double ra))
                {
                    SimpleLogger.Warning($"[DEEP SKY] Invalid RA format: {txtRA.Text}", "DeepSky");
                    txtRA.Focus();
                    return;
                }

                if (!TryParseDec(txtDec.Text.Trim(), out double dec))
                {
                    SimpleLogger.Warning($"[DEEP SKY] Invalid Dec format: {txtDec.Text}", "DeepSky");
                    txtDec.Focus();
                    return;
                }

                if (ra < 0 || ra >= 24)
                {
                    SimpleLogger.Warning("[DEEP SKY] RA must be between 0 and 24 hours", "DeepSky");
                    return;
                }

                if (dec < -90 || dec > 90)
                {
                    SimpleLogger.Warning("[DEEP SKY] Dec must be between -90 and +90 degrees", "DeepSky");
                    return;
                }

                // Stop auto tracking if running
                if (_isAutoTracking)
                {
                    StopAutoTracking();
                }

                // Get selected object name (if any)
                string objectName = "Manual RA/Dec";
                if (dgDeepSkyObjects.SelectedItem is NgcObjectInfo selectedObj)
                {
                    objectName = selectedObj.DisplayName;
                }

                // Request tracking from TrackingManager (will stop other tracking types)
                var target = new TrackingTarget
                {
                    ObjectType = TrackingObjectType.DeepSky,
                    ObjectName = objectName,
                    RightAscension = ra,
                    Declination = dec,
                    SourceObject = dgDeepSkyObjects.SelectedItem
                };

                bool granted = TrackingManager.Instance.RequestStartTracking(
                    TrackingObjectType.DeepSky,
                    target,
                    () => Dispatcher.Invoke(() => StopTrackingInternal())
                );

                if (!granted)
                {
                    SimpleLogger.Warning("[DEEP SKY] Tracking request denied by TrackingManager", "DeepSky");
                    return;
                }

                _trackingController?.SetTarget(ra, dec, _observerLocation);
                _trackingController?.StartTracking();

                // Update button states
                btnStopTrack.IsEnabled = true;
                SetTrackingToggleState(true);

                txtStatus.Text = $"Tracking {objectName} - RA {ra:F4}h, Dec {dec:F4}�";
                SimpleLogger.Info($"[DEEP SKY] Started tracking: {objectName} RA={ra:F4}h, Dec={dec:F4}�", "DeepSky");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Start tracking failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartSingleTracking()
        {
            try
            {
                if (!TryParseRA(txtRA.Text.Trim(), out double ra) || !TryParseDec(txtDec.Text.Trim(), out double dec))
                {
                    MessageBox.Show("Please select an object or enter valid coordinates.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _trackingController?.SetTarget(ra, dec, _observerLocation);
                _trackingController?.StartTracking();

                // Update button states
                btnStartTrack.IsEnabled = false;
                btnStopTrack.IsEnabled = true;
                SetTrackingToggleState(true);

                txtStatus.Text = $"Tracking RA {ra:F4}h, Dec {dec:F4}�";
                SimpleLogger.Info($"[DEEP SKY] Started tracking", "DeepSky");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Start tracking failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAutoTrack_Click(object sender, RoutedEventArgs e)
        {
            if (_isAutoTracking)
            {
                StopAutoTracking();
            }
            else
            {
                StartAutoTracking();
            }
        }

        private void StartAutoTracking()
        {
            var visibleObjects = _displayedObjects.Where(o => o.IsVisible).OrderBy(o => o.Magnitude).ToList();

            if (visibleObjects.Count == 0)
            {
                MessageBox.Show("No visible objects to track. Load the catalog and make sure the 'Visible Only' filter is showing objects.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Request tracking from TrackingManager
            var firstObject = visibleObjects.FirstOrDefault();
            var target = new TrackingTarget
            {
                ObjectType = TrackingObjectType.DeepSky,
                ObjectName = $"Auto: {firstObject?.DisplayName ?? "Unknown"}",
                RightAscension = firstObject?.RightAscension ?? 0,
                Declination = firstObject?.Declination ?? 0,
                SourceObject = firstObject
            };

            bool granted = TrackingManager.Instance.RequestStartTracking(
                TrackingObjectType.DeepSky,
                target,
                () => Dispatcher.Invoke(() => StopTrackingInternal())
            );

            if (!granted)
            {
                SimpleLogger.Warning("[DEEP SKY] Auto tracking request denied by TrackingManager", "DeepSky");
                return;
            }

            _isAutoTracking = true;
            _currentObjectIndex = 0;
            _hasArrivedAtObject = false;

            btnAutoTrack.Content = "Stop Auto";
            btnAutoTrack.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(239, 68, 68));
            
            // Update button states
            btnStopTrack.IsEnabled = true;
            SetTrackingToggleState(true);

            MoveToNextObject();
            SimpleLogger.Info($"[DEEP SKY] Auto tracking started with {visibleObjects.Count} visible objects", "DeepSky");
        }


        private void StopAutoTracking()
        {
            // Notify TrackingManager
            TrackingManager.Instance.StopTracking(TrackingObjectType.DeepSky);
            
            _isAutoTracking = false;
            _trackingController?.StopTracking();

            // Clear highlight
            foreach (var obj in _displayedObjects)
            {
                obj.IsCurrentTarget = false;
            }

            btnAutoTrack.Content = "Auto Track";
            btnAutoTrack.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(99, 102, 241));

            // Update button states
            btnStopTrack.IsEnabled = false;
            SetTrackingToggleState(false);

            txtStatus.Text = "Auto tracking stopped";
            SimpleLogger.Info("[DEEP SKY] Auto tracking stopped", "DeepSky");
        }

        private void MoveToNextObject()
        {
            var visibleObjects = _displayedObjects.Where(o => o.IsVisible).OrderBy(o => o.Magnitude).ToList();

            if (visibleObjects.Count == 0)
            {
                StopAutoTracking();
                txtStatus.Text = "No visible objects available";
                return;
            }

            _currentObjectIndex = (_currentObjectIndex + 1) % visibleObjects.Count;
            var nextObject = visibleObjects[_currentObjectIndex];

            // Set new target
            _trackingController?.SetTarget(nextObject.RightAscension, nextObject.Declination, _observerLocation);
            _trackingController?.StartTracking();
            _hasArrivedAtObject = false;

            // Update highlight
            foreach (var obj in _displayedObjects)
            {
                obj.IsCurrentTarget = (obj == nextObject);
            }

            // Update UI
            double raHours = nextObject.RightAscension;
            int raH = (int)raHours;
            int raM = (int)((raHours - raH) * 60);
            double raS = ((raHours - raH) * 60 - raM) * 60;

            bool isNegative = nextObject.Declination < 0;
            double decDeg = Math.Abs(nextObject.Declination);
            int decD = (int)decDeg;
            int decM = (int)((decDeg - decD) * 60);
            double decS = ((decDeg - decD) * 60 - decM) * 60;

            txtRA.Text = $"{raH:00}:{raM:00}:{raS:00.0}";
            txtDec.Text = $"{(isNegative ? "-" : "+")}{decD:00}:{decM:00}:{decS:00.0}";

            txtStatus.Text = $"Auto: {_currentObjectIndex + 1}/{visibleObjects.Count} - {nextObject.DisplayName} ({nextObject.TypeDescription})";

            System.Diagnostics.Debug.WriteLine($"[DEEP SKY AUTO] Moving to: {nextObject.DisplayName}");
        }

        public void StopTracking()
        {
            try
            {
                if (_isAutoTracking)
                {
                    StopAutoTracking();
                    return;
                }

                // Notify TrackingManager
                TrackingManager.Instance.StopTracking(TrackingObjectType.DeepSky);

                StopTrackingInternal();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY ERROR] Stop tracking: {ex.Message}");
            }
        }

        /// <summary>
        /// Internal method to stop tracking (called by TrackingManager callback)
        /// </summary>
        private void StopTrackingInternal()
        {
            try
            {
                _isAutoTracking = false;
                _trackingController?.StopTracking();

                // Clear highlight
                foreach (var obj in _displayedObjects)
                {
                    obj.IsCurrentTarget = false;
                }

                // Reset auto track button
                btnAutoTrack.Content = "Auto Track";
                btnAutoTrack.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(99, 102, 241));

                // Update button states
                btnStopTrack.IsEnabled = false;
                SetTrackingToggleState(false);

                txtStatus.Text = "Tracking stopped";
                SimpleLogger.Info("[DEEP SKY] Tracking stopped", "DeepSky");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEEP SKY ERROR] StopTrackingInternal: {ex.Message}");
            }
        }

        private (double raHours, double decDegrees) ConvertAltAzToRaDec(double altitude, double azimuth, DateTime utc)
        {
            double altRad = altitude * Math.PI / 180.0;
            double azRad = azimuth * Math.PI / 180.0;
            double latRad = _observerLocation.Latitude * Math.PI / 180.0;

            double sinDec = Math.Sin(altRad) * Math.Sin(latRad) +
                            Math.Cos(altRad) * Math.Cos(latRad) * Math.Cos(azRad);
            double decRad = Math.Asin(sinDec);
            double decDegrees = decRad * 180.0 / Math.PI;

            double cosHA = (Math.Sin(altRad) - Math.Sin(latRad) * sinDec) /
                           (Math.Cos(latRad) * Math.Cos(decRad));
            cosHA = Math.Clamp(cosHA, -1.0, 1.0);
            double hourAngleRad = Math.Acos(cosHA);

            if (Math.Sin(azRad) > 0)
                hourAngleRad = -hourAngleRad;

            double hourAngleHours = (hourAngleRad * 180.0 / Math.PI) / 15.0;
            double lstHours = CalculateLocalSiderealTime(utc);
            double raHours = lstHours - hourAngleHours;

            raHours %= 24.0;
            if (raHours < 0)
                raHours += 24.0;

            return (raHours, decDegrees);
        }

        private double CalculateLocalSiderealTime(DateTime utc)
        {
            double jd = ToJulianDate(utc);
            double t = (jd - 2451545.0) / 36525.0;

            double gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0) +
                          0.000387933 * t * t - t * t * t / 38710000.0;

            gmst %= 360.0;
            if (gmst < 0)
                gmst += 360.0;

            double gmstHours = gmst / 15.0;
            double lstHours = gmstHours + _observerLocation.Longitude / 15.0;

            lstHours %= 24.0;
            if (lstHours < 0)
                lstHours += 24.0;

            return lstHours;
        }

        private double ToJulianDate(DateTime utc)
        {
            int year = utc.Year;
            int month = utc.Month;
            int day = utc.Day;

            if (month <= 2)
            {
                year -= 1;
                month += 12;
            }

            int a = year / 100;
            int b = 2 - a + a / 4;

            double jd = Math.Floor(365.25 * (year + 4716)) +
                        Math.Floor(30.6001 * (month + 1)) +
                        day + b - 1524.5;

            double dayFraction = (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0 + utc.Millisecond / 3600000.0) / 24.0;
            return jd + dayFraction;
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

                var tuningWindow = new SiderealTuningWindow(_trackingController)
                {
                    Owner = Window.GetWindow(this)
                };
                tuningWindow.Show();

                System.Diagnostics.Debug.WriteLine("[DEEP SKY] Tuning window opened");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open tuning window: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region Coordinate Parsing

        private bool TryParseRA(string input, out double raHours)
        {
            raHours = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            input = input.Trim();

            if (double.TryParse(input, out raHours)) return true;
            if (input.Contains(':')) return TryParseHMS(input, ':', out raHours);
            if (input.Contains(' ')) return TryParseHMS(input, ' ', out raHours);

            return false;
        }

        private bool TryParseDec(string input, out double decDegrees)
        {
            decDegrees = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            input = input.Trim();
            bool isNegative = input.StartsWith("-");
            string absInput = input.TrimStart('+', '-');

            if (double.TryParse(input, out decDegrees)) return true;

            if (absInput.Contains(':'))
            {
                if (TryParseHMS(absInput, ':', out double absDegrees))
                {
                    decDegrees = isNegative ? -absDegrees : absDegrees;
                    return true;
                }
            }

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

        private bool TryParseHMS(string input, char separator, out double result)
        {
            result = 0;
            string[] parts = input.Split(separator);

            if (parts.Length < 2 || parts.Length > 3) return false;
            if (!int.TryParse(parts[0], out int h) || h < 0) return false;
            if (!int.TryParse(parts[1], out int m) || m < 0 || m >= 60) return false;

            double s = 0;
            if (parts.Length == 3)
            {
                if (!double.TryParse(parts[2], out s) || s < 0 || s >= 60) return false;
            }

            result = h + m / 60.0 + s / 3600.0;
            return true;
        }

        #endregion
    }
}
