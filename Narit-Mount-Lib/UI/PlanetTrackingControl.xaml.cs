using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Utility;
using PWLib.Astro;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Planet Tracking Control - Track Solar System objects (Sun, Moon, Planets)
    /// Uses PWLib.Astro.Planets for accurate planetary ephemeris calculations
    /// </summary>
    public partial class PlanetTrackingControl : UserControl
    {
        private ObserverLocation _observerLocation;
        private ObservableCollection<PlanetInfo> _planets;
        
        // PWLib.Astro.Planets object for ephemeris calculations
        private Planets _planetsCalculator;
        
        // Planet tracking controller (same pattern as SiderealTracking)
        private SiderealTracking _trackingController;
        
        // Position update timer (1000ms = 1 Hz)
        private DispatcherTimer _updateTimer;
        
        // Tracking state
        private bool _isTracking = false;
        private int _selectedPlanetIndex = -1;
        
        // CentralizedPollingManager reference
        private CentralizedPollingManager _pollingManager;

        /// <summary>
        /// Is tracking currently active
        /// </summary>
        public bool IsTracking => _isTracking;

        public PlanetTrackingControl()
        {
            InitializeComponent();
            _planets = new ObservableCollection<PlanetInfo>();
            dgPlanets.ItemsSource = _planets;
            
            LoadObserverLocation();
            InitializePlanetsCalculator();
            InitializePlanetList();
            InitializeUpdateTimer();
        }

        /// <summary>
        /// Initialize PWLib.Astro.Planets calculator
        /// </summary>
        private void InitializePlanetsCalculator()
        {
            try
            {
                // Convert degrees to radians for PWLib.Astro.Planets
                double latRads = _observerLocation.Latitude * Math.PI / 180.0;
                double lonRads = _observerLocation.Longitude * Math.PI / 180.0;
                double elevation = _observerLocation.Altitude; // meters
                
                // Path to ephemeris data files (elp82.dat, ps_1996.dat)
                // These should be in the same directory as PWLib.dll or use "EMBEDDED_RESOURCE"
                string pathToDataFiles = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\";
                
                // Check if data files exist
                bool hasElp82 = System.IO.File.Exists(pathToDataFiles + "elp82.dat");
                bool hasPs1996 = System.IO.File.Exists(pathToDataFiles + "ps_1996.dat");
                
                if (!hasElp82 || !hasPs1996)
                {
                    // Try embedded resources
                    pathToDataFiles = "EMBEDDED_RESOURCE";
                    SimpleLogger.Info("[PLANET] Using embedded ephemeris data", "Planet");
                }
                else
                {
                    SimpleLogger.Info($"[PLANET] Using ephemeris files from: {pathToDataFiles}", "Planet");
                }
                
                _planetsCalculator = new Planets(latRads, lonRads, elevation, pathToDataFiles);
                
                SimpleLogger.Info("[PLANET] PWLib.Astro.Planets calculator initialized", "Planet");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Failed to initialize Planets calculator: {ex.Message}", "Planet");
                _planetsCalculator = null;
            }
        }

        /// <summary>
        /// Initialize tracking controller with CentralizedPollingManager
        /// </summary>
        private void InitializeTrackingController()
        {
            try
            {
                if (_pollingManager == null)
                {
                    System.Diagnostics.Debug.WriteLine("[PLANET] Cannot initialize controller - PollingManager is null");
                    return;
                }
                
                // Use SiderealTracking controller (same pattern - good for slow-moving objects)
                _trackingController = new SiderealTracking(_pollingManager);
                
                // Load PD gains from sidereal settings (planets move slower than satellites)
                var gains = AppSettings.GetSiderealTrackingPDGains();
                _trackingController.SetGains(
                    new PDControllerGains { Kp = gains.Azimuth_Kp, Kd = gains.Azimuth_Kd },
                    new PDControllerGains { Kp = gains.Altitude_Kp, Kd = gains.Altitude_Kd }
                );
                
                System.Diagnostics.Debug.WriteLine("[PLANET] Tracking controller initialized successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PLANET ERROR] Failed to initialize controller: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[PLANET] CentralizedPollingManager linked (Hash: {_pollingManager.GetHashCode()})");
                InitializeTrackingController();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[PLANET] CentralizedPollingManager is NULL!");
            }
        }

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
                
                SimpleLogger.Info($"[PLANET] Observer Location loaded: Lat={location.Latitude_Deg:F4}, Lon={location.Longitude_Deg:F4}", "Planet");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Failed to load Observer Location: {ex.Message}", "Planet");
                // Default to Chiang Mai
                _observerLocation = new ObserverLocation(18.7883, 98.9853, 350);
            }
        }

        /// <summary>
        /// Initialize the list of solar system objects to track
        /// Planet indices match PWLib.Astro.Planets:
        /// 0=Sun, 1=Mercury, 2=Venus, 3=Earth, 4=Mars, 5=Jupiter, 6=Saturn, 7=Uranus, 8=Neptune, 9=Pluto, 10=Moon
        /// </summary>
        private void InitializePlanetList()
        {
            try
            {
                _planets.Clear();
                
                // Add objects in order that makes sense for users
                _planets.Add(new PlanetInfo { PlanetIndex = 0, Name = "Sun" });
                _planets.Add(new PlanetInfo { PlanetIndex = 10, Name = "Moon" });
                _planets.Add(new PlanetInfo { PlanetIndex = 1, Name = "Mercury" });
                _planets.Add(new PlanetInfo { PlanetIndex = 2, Name = "Venus" });
                _planets.Add(new PlanetInfo { PlanetIndex = 4, Name = "Mars" });
                _planets.Add(new PlanetInfo { PlanetIndex = 5, Name = "Jupiter" });
                _planets.Add(new PlanetInfo { PlanetIndex = 6, Name = "Saturn" });
                _planets.Add(new PlanetInfo { PlanetIndex = 7, Name = "Uranus" });
                _planets.Add(new PlanetInfo { PlanetIndex = 8, Name = "Neptune" });
                _planets.Add(new PlanetInfo { PlanetIndex = 9, Name = "Pluto" });
                
                // Initial position update
                UpdateAllPlanetPositions();
                
                txtStatus.Text = $"Loaded {_planets.Count} solar system objects";
                SimpleLogger.Info($"[PLANET] Initialized {_planets.Count} solar system objects", "Planet");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Failed to initialize planet list: {ex.Message}", "Planet");
            }
        }

        /// <summary>
        /// Initialize position update timer
        /// </summary>
        private void InitializeUpdateTimer()
        {
            try
            {
                _updateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000) // Update every second
                };
                _updateTimer.Tick += UpdateTimer_Tick;
                _updateTimer.Start();
                
                System.Diagnostics.Debug.WriteLine("[PLANET] Update timer initialized (1 Hz)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PLANET ERROR] Failed to initialize update timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Update timer tick - update planet positions
        /// </summary>
        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                UpdateAllPlanetPositions();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PLANET UPDATE ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Update positions for all planets using PWLib.Astro.Planets
        /// </summary>
        private void UpdateAllPlanetPositions()
        {
            try
            {
                if (_planetsCalculator == null)
                {
                    System.Diagnostics.Debug.WriteLine("[PLANET] Calculator not initialized");
                    return;
                }
                
                double jd = AstroTime.JulianDay();
                
                // Calculate all planet positions at once
                _planetsCalculator.CalculateAllPositions(jd);
                
                // Update each planet info from calculator
                foreach (var planet in _planets)
                {
                    try
                    {
                        UpdatePlanetFromCalculator(planet, jd);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PLANET] Error updating {planet.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PLANET UPDATE ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Update a single planet's info from PWLib.Astro.Planets calculator
        /// </summary>
        private void UpdatePlanetFromCalculator(PlanetInfo planet, double jd)
        {
            try
            {
                // Get the Planetoid data from calculator
                var planetoid = _planetsCalculator.All.Plan[planet.PlanetIndex];
                
                // RA and Dec are in radians, convert to hours and degrees
                planet.RightAscension = planetoid.RA * 12.0 / Math.PI; // radians to hours
                planet.Declination = planetoid.Dec * 180.0 / Math.PI;  // radians to degrees
                planet.Distance = planetoid.Distance;
                planet.Phase = planetoid.Phase * 180.0 / Math.PI; // Convert to degrees for display
                
                // Calculate Az/Alt using PWLib.Astro coordinate conversion
                try
                {
                    double raRads = planetoid.RA;
                    double decRads = planetoid.Dec;
                    
                    var raDec = RaDec.FromRads(raRads, decRads);
                    var lmst = AstroTime.Lmst(jd, Angle.FromDegs(_observerLocation.Longitude));
                    var haDec = raDec.ToHaDec(lmst);
                    var latLon = new LatLon(Angle.FromDegs(_observerLocation.Latitude), Angle.FromDegs(_observerLocation.Longitude));
                    var altAz = haDec.ToAltAz(latLon);
                    
                    planet.Azimuth = altAz.Az.Degs;
                    planet.Altitude = altAz.Alt.Degs;
                    
                    // Normalize azimuth to 0-360
                    while (planet.Azimuth < 0) planet.Azimuth += 360.0;
                    while (planet.Azimuth >= 360) planet.Azimuth -= 360.0;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PLANET] Error converting coordinates for {planet.Name}: {ex.Message}");
                }
                
                // Update visibility (above horizon)
                planet.IsVisible = planet.Altitude > 0;
                
                // Format RA string (HH:MM:SS.S)
                double raHours = planet.RightAscension;
                if (raHours < 0) raHours += 24.0;
                int raH = (int)raHours;
                int raM = (int)((raHours - raH) * 60);
                double raS = ((raHours - raH) * 60 - raM) * 60;
                planet.RAString = $"{raH:00}:{raM:00}:{raS:00.0}";
                
                // Format Dec string (+DD:MM:SS.S)
                double decDeg = planet.Declination;
                string decSign = decDeg >= 0 ? "+" : "-";
                double absDec = Math.Abs(decDeg);
                int decD = (int)absDec;
                int decM = (int)((absDec - decD) * 60);
                double decS = ((absDec - decD) * 60 - decM) * 60;
                planet.DecString = $"{decSign}{decD:00}:{decM:00}:{decS:00.0}";
                
                // Format distance string
                if (planet.PlanetIndex == 10) // Moon
                {
                    // Moon distance is in km (from ELP82)
                    planet.DistanceString = $"{planet.Distance:F0} km";
                }
                else
                {
                    // Planets distance is in AU
                    planet.DistanceString = $"{planet.Distance:F4} AU";
                }
                
                // Format phase string (only meaningful for Moon and inner planets)
                if (planet.PlanetIndex == 10) // Moon
                {
                    double moonPhasePercent = (1.0 - Math.Cos(planetoid.Phase)) / 2.0 * 100.0;
                    planet.PhaseString = $"{moonPhasePercent:F0}%";
                    planet.Magnitude = -12.7 + 0.026 * Math.Abs(moonPhasePercent - 50);
                }
                else
                {
                    planet.PhaseString = "-";
                    // Approximate magnitudes
                    planet.Magnitude = GetApproximateMagnitude(planet.PlanetIndex);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PLANET] Error updating {planet.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get approximate visual magnitude for a planet
        /// </summary>
        private double GetApproximateMagnitude(int planetIndex)
        {
            return planetIndex switch
            {
                0 => -26.7,  // Sun
                1 => -0.4,   // Mercury
                2 => -4.4,   // Venus
                4 => -2.0,   // Mars
                5 => -2.7,   // Jupiter
                6 => 0.4,    // Saturn
                7 => 5.7,    // Uranus
                8 => 7.8,    // Neptune
                9 => 14.0,   // Pluto
                10 => -12.7, // Moon (full)
                _ => 0.0
            };
        }

        #region Event Handlers

        /// <summary>
        /// Handle DataGrid selection changed
        /// </summary>
        private void DgPlanets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                int selectedIndex = dgPlanets.SelectedIndex;
                
                if (selectedIndex >= 0 && selectedIndex < _planets.Count)
                {
                    _selectedPlanetIndex = selectedIndex;
                    btnTrackSelected.Visibility = Visibility.Visible;
                    
                    var selected = _planets[selectedIndex];
                    txtStatus.Text = $"Selected: {selected.Name} (Az: {selected.Azimuth:F2}, Alt: {selected.Altitude:F2})";
                    
                    // Notify FITS auto-save of selected object name
                    TrackingManager.Instance.NotifyObjectSelected(selected.Name);
                    
                    SimpleLogger.Info($"[PLANET] Selected: {selected.Name}", "Planet");
                }
                else
                {
                    _selectedPlanetIndex = -1;
                    btnTrackSelected.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Error handling selection: {ex.Message}", "Planet");
            }
        }

        /// <summary>
        /// Handle Start/Stop Track button click
        /// </summary>
        private void BtnStartTrack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isTracking)
                {
                    StopTracking();
                }
                else
                {
                    // If no planet selected, select the first visible one
                    if (_selectedPlanetIndex < 0)
                    {
                        var firstVisible = _planets.FirstOrDefault(p => p.IsVisible);
                        if (firstVisible != null)
                        {
                            _selectedPlanetIndex = _planets.IndexOf(firstVisible);
                            dgPlanets.SelectedIndex = _selectedPlanetIndex;
                        }
                        else
                        {
                            MessageBox.Show("No visible planets are available to track.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                    }
                    
                    StartTrackingSelected();
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Error in Start/Stop Track: {ex.Message}", "Planet");
            }
        }

        /// <summary>
        /// Handle Track Selected button click
        /// </summary>
        private void BtnTrackSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedPlanetIndex < 0 || _selectedPlanetIndex >= _planets.Count)
                {
                    MessageBox.Show("Please select a planet from the list first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                StartTrackingSelected();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Error tracking selected: {ex.Message}", "Planet");
            }
        }

        /// <summary>
        /// Start tracking the selected planet
        /// </summary>
        private void StartTrackingSelected()
        {
            try
            {
                if (_trackingController == null)
                {
                    MessageBox.Show("Tracking controller not initialized. Please restart the application.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (_pollingManager == null)
                {
                    MessageBox.Show("Polling manager not initialized.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Check if drives are enabled
                if (!_pollingManager.AzimuthInfo.Is_Drive_Enabled || !_pollingManager.AltitudeInfo.Is_Drive_Enabled)
                {
                    MessageBox.Show("Please enable both Azimuth and Altitude drives before starting tracking.", 
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                var planet = _planets[_selectedPlanetIndex];
                
                if (!planet.IsVisible)
                {
                    var result = MessageBox.Show($"{planet.Name} is below the horizon (Altitude: {planet.Altitude:F1}).\n\nDo you still want to track it?", 
                        "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes)
                        return;
                }
                
                // Request tracking from TrackingManager (will stop other tracking types)
                var target = new TrackingTarget
                {
                    ObjectType = TrackingObjectType.Planet,
                    ObjectName = planet.Name,
                    RightAscension = planet.RightAscension,
                    Declination = planet.Declination,
                    Azimuth = planet.Azimuth,
                    Altitude = planet.Altitude,
                    SourceObject = planet
                };

                bool granted = TrackingManager.Instance.RequestStartTracking(
                    TrackingObjectType.Planet,
                    target,
                    () => Dispatcher.Invoke(() => StopTrackingInternal())
                );

                if (!granted)
                {
                    SimpleLogger.Warning("[PLANET] Tracking request denied by TrackingManager", "Planet");
                    return;
                }
                
                // Set target RA/Dec
                _trackingController.SetTarget(planet.RightAscension, planet.Declination, _observerLocation);
                _trackingController.StartTracking();
                
                _isTracking = true;
                
                // Update highlight
                foreach (var p in _planets)
                {
                    p.IsCurrentTarget = (p == planet);
                }
                
                // Update UI
                btnStartTrack.Content = "Stop Track";
                btnStartTrack.Background = System.Windows.Media.Brushes.Red;
                txtStatus.Text = $"Tracking: {planet.Name}";
                
                SimpleLogger.Info($"[PLANET] Started tracking: {planet.Name} (RA: {planet.RightAscension:F4}h, Dec: {planet.Declination:F4})", "Planet");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Error starting tracking: {ex.Message}", "Planet");
                MessageBox.Show($"Failed to start tracking: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Stop tracking
        /// </summary>
        public void StopTracking()
        {
            // Notify TrackingManager
            TrackingManager.Instance.StopTracking(TrackingObjectType.Planet);
            
            StopTrackingInternal();
        }
        
        /// <summary>
        /// Internal method to stop tracking (called by TrackingManager callback)
        /// </summary>
        private void StopTrackingInternal()
        {
            try
            {
                if (_isTracking)
                {
                    _trackingController?.StopTracking();
                    _isTracking = false;
                    
                    // Stop drives
                    if (_pollingManager != null)
                    {
                        _pollingManager.SetAzimuthCommandVelocity(0.0);
                        _pollingManager.SetAltitudeCommandVelocity(0.0);
                    }
                    
                    // Clear highlight
                    foreach (var p in _planets)
                    {
                        p.IsCurrentTarget = false;
                    }
                    
                    // Update UI
                    btnStartTrack.Content = "Start Track";
                    btnStartTrack.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(16, 185, 129)); // Green
                    txtStatus.Text = "Tracking stopped";
                    
                    SimpleLogger.Info("[PLANET] Tracking stopped", "Planet");
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[PLANET] Error stopping tracking: {ex.Message}", "Planet");
            }
        }

        #endregion
    }

    /// <summary>
    /// Planet information model for DataGrid display
    /// </summary>
    internal class PlanetInfo : INotifyPropertyChanged
    {
        private int _planetIndex;
        private string _name;
        private double _rightAscension;
        private double _declination;
        private double _azimuth;
        private double _altitude;
        private double _distance;
        private double _magnitude;
        private double _phase;
        private bool _isVisible;
        private bool _isCurrentTarget;
        private string _raString;
        private string _decString;
        private string _distanceString;
        private string _phaseString;

        public int PlanetIndex
        {
            get => _planetIndex;
            set { _planetIndex = value; OnPropertyChanged(nameof(PlanetIndex)); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public double RightAscension
        {
            get => _rightAscension;
            set { _rightAscension = value; OnPropertyChanged(nameof(RightAscension)); }
        }

        public double Declination
        {
            get => _declination;
            set { _declination = value; OnPropertyChanged(nameof(Declination)); }
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

        public double Distance
        {
            get => _distance;
            set { _distance = value; OnPropertyChanged(nameof(Distance)); }
        }

        public double Magnitude
        {
            get => _magnitude;
            set { _magnitude = value; OnPropertyChanged(nameof(Magnitude)); }
        }

        public double Phase
        {
            get => _phase;
            set { _phase = value; OnPropertyChanged(nameof(Phase)); }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); }
        }

        public bool IsCurrentTarget
        {
            get => _isCurrentTarget;
            set { _isCurrentTarget = value; OnPropertyChanged(nameof(IsCurrentTarget)); }
        }

        public string RAString
        {
            get => _raString;
            set { _raString = value; OnPropertyChanged(nameof(RAString)); }
        }

        public string DecString
        {
            get => _decString;
            set { _decString = value; OnPropertyChanged(nameof(DecString)); }
        }

        public string DistanceString
        {
            get => _distanceString;
            set { _distanceString = value; OnPropertyChanged(nameof(DistanceString)); }
        }

        public string PhaseString
        {
            get => _phaseString;
            set { _phaseString = value; OnPropertyChanged(nameof(PhaseString)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
