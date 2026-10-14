using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Control;
using PWLib.Astro;

namespace ForkMount_WPF.UI
{
    public partial class ManualControlWindow : Window
    {
        private enum DirectionControlMode
        {
            MovementVelocity,
            DirectionOffset
        }

        private enum OffsetCoordinateType
        {
            AzimuthAltitude,
            RaDec
        }

        private Azimuth_Controller _azimuthController;
        private AzimuthManager _azimuthDrive;
        private AltitudeManager _altitudeDrive;
        private AzimuthInfo _azimuthInfo;
        private AltitudeInfo _altitudeInfo;
        
        // [CRITICAL] Reference to CentralizedPollingManager for sending velocity commands
        private CentralizedPollingManager _pollingManager;

        // [M3] Axis 4 encoder resolution: 18-bit = 2^18 = 262,144 counts/rev
        private long _m3EncoderResolution = 262144L; // loaded from AppSettings.M3_AxisConfig
        private int  _m3AxisNumber        = 4;        // loaded from AppSettings.M3_AxisConfig

        // [NEW] Sidereal Tracking Engine for quick tracking toggle
        private SiderealTracking _siderealTracking;
        private bool _isSiderealTrackingActive = false;
        
        private bool _isAzimuthConnected = false;
        private bool _isAltitudeConnected = false;
        private int _lastOffsetAzimuthDirection = 1;
        private int _lastOffsetAltitudeDirection = 0;
        private string _lastOffsetDirectionLabel = "Right";
        private readonly List<(double AzimuthDeltaDeg, double AltitudeDeltaDeg)> _executedOffsetPath = new();
        private double _cumulativeAzimuthOffsetDeg = 0;
        private double _cumulativeAltitudeOffsetDeg = 0;

        /// <summary>
        /// Public access to live AzimuthInfo from CentralizedPollingManager
        /// [CRITICAL FIX] Always return fresh reference from _pollingManager to avoid stale data
        /// </summary>
        public AzimuthInfo AzimuthInfo => _pollingManager?.AzimuthInfo ?? _azimuthInfo;
        
        /// <summary>
        /// Public access to live AltitudeInfo from CentralizedPollingManager
        /// [CRITICAL FIX] Always return fresh reference from _pollingManager to avoid stale data
        /// </summary>
        public AltitudeInfo AltitudeInfo => _pollingManager?.AltitudeInfo ?? _altitudeInfo;

        // Goto Position Collection
        private ObservableCollection<GotoPosition> _gotoPositions;
        private int _nextPositionId = 1;

        public ManualControlWindow(Azimuth_Controller azimuthController,
                                   AzimuthManager azimuthDrive, 
                                   AltitudeManager altitudeDrive, 
                                   AzimuthInfo azimuthInfo, 
                                   AltitudeInfo altitudeInfo)
        {
            InitializeComponent();
            
            
            _azimuthController = azimuthController;
            _azimuthDrive = azimuthDrive;
            _altitudeDrive = altitudeDrive;
            _azimuthInfo = azimuthInfo;
            _altitudeInfo = altitudeInfo;
            
            // [CRITICAL] Get CentralizedPollingManager from Azimuth_Controller
            _pollingManager = azimuthController?.CentralizedPollingManager;
            
            // [DEBUG] Verify references
            SimpleLogger.Info($"[INIT] _azimuthInfo reference: {_azimuthInfo?.GetHashCode() ?? 0}", "ManualCtrl");
            SimpleLogger.Info($"[INIT] _altitudeInfo reference: {_altitudeInfo?.GetHashCode() ?? 0}", "ManualCtrl");
            if (_pollingManager != null)
            {
                SimpleLogger.Info($"[INIT] _pollingManager.AzimuthInfo reference: {_pollingManager.AzimuthInfo?.GetHashCode() ?? 0}", "ManualCtrl");
                SimpleLogger.Info($"[INIT] _pollingManager.AltitudeInfo reference: {_pollingManager.AltitudeInfo?.GetHashCode() ?? 0}", "ManualCtrl");
                SimpleLogger.Info($"[INIT] References match: Az={_azimuthInfo == _pollingManager.AzimuthInfo}, Alt={_altitudeInfo == _pollingManager.AltitudeInfo}", "ManualCtrl");
            }
            
            _isAzimuthConnected = azimuthDrive != null && azimuthDrive.IsConnected;
            _isAltitudeConnected = altitudeDrive != null;
            
            // Load saved Goto Positions from storage
            _gotoPositions = GotoPositionStorage.LoadPositions(ref _nextPositionId);
            dgGotoPositions.ItemsSource = _gotoPositions;
            btnGotoPosition.IsEnabled = false;
            btnDeletePosition.IsEnabled = false;
            
            SimpleLogger.Info($"[LOAD] Loaded {_gotoPositions.Count} saved positions", "ManualCtrl");
            
            // Start update timer for velocity display
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += UpdateVelocityDisplay;
            timer.Start();
            
            // [NEW] Initialize Satellite Tracking Engine
            InitializeSatelliteTrackingEngine();
            
            // [NEW] Load TLE data from MongoDB when window opens
            LoadTLEDataFromMongoDB();

            // [M3] Load port positions from MongoDB
            LoadM3PortPositionsAsync();

            // [Instrument] Initialize Axis 3 control
            if (_pollingManager != null)
                instrumentControl.Initialize(_pollingManager);

            // Load M3 axis config from settings
            var m3Cfg = ForkMount_WPF.Engine.Utility.AppSettings.GetM3AxisConfig();
            _m3EncoderResolution = m3Cfg.Encoder_Resolution;
            _m3AxisNumber        = m3Cfg.Axis_Number;

            SimpleLogger.Info($"Manual Control initialized - Azimuth: {(_isAzimuthConnected ? "Connected" : "Not Connected")}, Altitude: {(_isAltitudeConnected ? "Connected" : "Not Connected")}", "ManualCtrl");

            UpdateDirectionControlModeUi();
            UpdateDirectionOffsetPreview();
        }
        
        /// <summary>
        /// Initialize Satellite Tracking Engine with axis references
        /// [FIX] Use CentralizedPollingManager's AltitudeInfo for correct reference
        /// [CRITICAL FIX] Pass CentralizedPollingManager instead of AltitudeDrive because CommandSenderLoop runs there!
        /// </summary>
        private void InitializeSatelliteTrackingEngine()
        {
            try
            {
                // [CRITICAL FIX] Use Info references from CentralizedPollingManager (not constructor parameters!)
                // This ensures we use the LIVE references that are being updated every 15ms
                AzimuthInfo liveAzimuthInfo = _pollingManager?.AzimuthInfo ?? _azimuthInfo;
                AltitudeInfo liveAltitudeInfo = _pollingManager?.AltitudeInfo ?? _altitudeInfo;
                
                if (satTrackingControl != null && liveAzimuthInfo != null && liveAltitudeInfo != null && _pollingManager != null)
                {
                    // [CRITICAL FIX] Pass LIVE Info references from CentralizedPollingManager
                    satTrackingControl.InitializeTrackingEngine(liveAzimuthInfo, liveAltitudeInfo, _pollingManager);
                    SimpleLogger.Info("[MANUAL CONTROL] Satellite tracking initialized with LIVE Info references from CentralizedPollingManager", "ManualCtrl");
                    
                    // [NEW] Subscribe to SatelliteSelected event for Sky Chart display
                    satTrackingControl.SatelliteSelected += SatTrackingControl_SatelliteSelected;
                    SimpleLogger.Info("[MANUAL CONTROL] Subscribed to SatelliteSelected event for Sky Chart", "ManualCtrl");
                }
                else
                {
                    SimpleLogger.Warning("[MANUAL CONTROL] Cannot initialize tracking - missing components", "ManualCtrl");
                    SimpleLogger.Warning($"  satTrackingControl: {(satTrackingControl != null ? "OK" : "NULL")}", "ManualCtrl");
                    SimpleLogger.Warning($"  liveAzimuthInfo: {(liveAzimuthInfo != null ? "OK" : "NULL")}", "ManualCtrl");
                    SimpleLogger.Warning($"  liveAltitudeInfo: {(liveAltitudeInfo != null ? "OK" : "NULL")}", "ManualCtrl");
                    SimpleLogger.Warning($"  _pollingManager: {(_pollingManager != null ? "OK" : "NULL")}", "ManualCtrl");
                }
                
                // [NEW] Initialize Sidereal Tracking Control
                if (siderealTrackingControl != null && _pollingManager != null)
                {
                    // [BEST FIX] Pass CentralizedPollingManager reference directly
                    // SiderealTrackingControl will access .AzimuthInfo and .AltitudeInfo dynamically
                    siderealTrackingControl.SetPollingManager(_pollingManager);
                    SimpleLogger.Info("[MANUAL CONTROL] Sidereal tracking initialized with CentralizedPollingManager", "ManualCtrl");
                }
                else
                {
                    SimpleLogger.Warning("[MANUAL CONTROL] Cannot initialize sidereal tracking - missing CentralizedPollingManager", "ManualCtrl");
                }
                
                // [NEW] Initialize Planet Tracking Control
                if (planetTrackingControl != null && _pollingManager != null)
                {
                    planetTrackingControl.SetPollingManager(_pollingManager);
                    SimpleLogger.Info("[MANUAL CONTROL] Planet tracking initialized with CentralizedPollingManager", "ManualCtrl");
                }
                else
                {
                    SimpleLogger.Warning("[MANUAL CONTROL] Cannot initialize planet tracking - missing CentralizedPollingManager", "ManualCtrl");
                }
                
                // [NEW] Initialize Deep Sky Tracking Control
                if (deepSkyTrackingControl != null && _pollingManager != null)
                {
                    deepSkyTrackingControl.SetPollingManager(_pollingManager);
                    SimpleLogger.Info("[MANUAL CONTROL] Deep Sky tracking initialized with CentralizedPollingManager", "ManualCtrl");
                }
                else
                {
                    SimpleLogger.Warning("[MANUAL CONTROL] Cannot initialize deep sky tracking - missing CentralizedPollingManager", "ManualCtrl");
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MANUAL CONTROL] Failed to initialize satellite tracking: {ex.Message}", "ManualCtrl");
                if (offsetTypePanel != null)
                {
                    offsetTypePanel.Visibility = Visibility.Collapsed;
                }
                if (borderDirectionOffsetPreview != null)
                {
                    borderDirectionOffsetPreview.Visibility = Visibility.Collapsed;
                    borderDirectionOffsetPreview.Height = double.NaN;
                }
            }
        }
        
        /// <summary>
        /// Handle SatelliteSelected event - update Sky Chart to show selected satellite's path
        /// Priority: Selected > Current Tracking (for Satellite tab only)
        /// </summary>
        private void SatTrackingControl_SatelliteSelected(object sender, Engine.Tracking.SatelliteTracker selectedSatellite)
        {
            try
            {
                // Only update sky chart if we're on Satellite Tracking tab (index 3)
                if (mainTabControl?.SelectedIndex != 3)
                    return;

                if (skyPositionMap != null)
                {
                    if (satTrackingControl?.ObserverLocation != null)
                    {
                        skyPositionMap.ObserverLocation = satTrackingControl.ObserverLocation;

                        // Priority: user selection → current tracking satellite
                        Engine.Tracking.SatelliteTracker trackerToDisplay = selectedSatellite
                            ?? (satTrackingControl.IsTracking ? satTrackingControl.CurrentSatelliteTracker : null);

                        skyPositionMap.SatelliteTracker = trackerToDisplay;

                        if (trackerToDisplay != null)
                            SimpleLogger.Info($"[SKY CHART] Displaying satellite: {trackerToDisplay.Name}", "ManualCtrl");
                        else
                            SimpleLogger.Info("[SKY CHART] Satellite path cleared", "ManualCtrl");
                    }
                    else
                    {
                        SimpleLogger.Warning("[SKY CHART] ObserverLocation not set — cannot display satellite path", "ManualCtrl");
                        skyPositionMap.SatelliteTracker = selectedSatellite;
                    }
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SKY CHART] Failed to update selected satellite: {ex.Message}", "ManualCtrl");
            }
        }

        /// <summary>
        /// [NEW] Load TLE data from MongoDB database when ManualControlWindow opens
        /// </summary>
        private async void LoadTLEDataFromMongoDB()
        {
            try
            {
                if (satTrackingControl != null)
                {
                    SimpleLogger.Info("[MANUAL CONTROL] Fetching TLE data (NARIT API / MongoDB)...", "ManualCtrl");
                    await satTrackingControl.FetchAndLoadTLEAsync();
                    SimpleLogger.Info("[MANUAL CONTROL] TLE data loaded successfully", "ManualCtrl");
                }
                else
                {
                    SimpleLogger.Warning("[MANUAL CONTROL] Satellite tracking control not initialized", "ManualCtrl");
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MANUAL CONTROL] Failed to load TLE: {ex.Message}", "ManualCtrl");
            }
        }

        private void UpdateVelocityDisplay(object sender, EventArgs e)
        {
            // [CRITICAL FIX] Use _pollingManager references directly to avoid stale object references
            // _azimuthInfo/_altitudeInfo passed at construction may become stale if CentralizedPollingManager recreates them
            var actualAzimuthInfo = _pollingManager?.AzimuthInfo ?? _azimuthInfo;
            var actualAltitudeInfo = _pollingManager?.AltitudeInfo ?? _altitudeInfo;
            
            // Update Azimuth velocity display and position
            if (actualAzimuthInfo != null)
            {
                txtAzimuthVelocity.Text = $"{actualAzimuthInfo.Command_Velocity_Deg_Sec:F2} deg/s";
                
                try
                {
                    // [NEW] Use corrected position from MountInfo (with pointing model applied)
                    double currentAzimuth = 0;
                    if (_pollingManager?.MountInfo != null)
                    {
                        currentAzimuth = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                    }
                    else
                    {
                        // Fallback: calculate manually if MountInfo not available
                        var azimuthConfig = AppSettings.GetAzimuthAxisConfig();
                        if (azimuthConfig != null)
                        {
                            double azCountsPerDeg = azimuthConfig.Encoder_Resolution / 360.0;
                            currentAzimuth = actualAzimuthInfo.Raw_Position / azCountsPerDeg;
                        }
                    }
                    
                    // Pass corrected azimuth position to SkyPositionMap
                    // SkyPositionMap will normalize internally for display on map
                    if (skyPositionMap != null)
                    {
                        skyPositionMap.Azimuth = currentAzimuth; // Show actual sky position (corrected)
                        
                        // [REMOVED] Don't set AzimuthVelocity here - will be set later with all Mount Status properties
                        
                        // Update RMS Error from AzimuthInfo
                        skyPositionMap.AzimuthRmsError = actualAzimuthInfo.TrackingRmsError_Arcsec;
                        
                        // Update SatelliteTracker and ObserverLocation ONLY if on Satellite Tracking tab
                        // [FIX] Use SelectedSatelliteTracker if available, otherwise CurrentSatelliteTracker
                        if (mainTabControl?.SelectedIndex == 3 && satTrackingControl != null)
                        {
                            // Priority: Selected > Current Tracking
                            var trackerToShow = satTrackingControl.SelectedSatelliteTracker 
                                ?? satTrackingControl.CurrentSatelliteTracker;
                            
                            skyPositionMap.SatelliteTracker = trackerToShow;
                            skyPositionMap.ObserverLocation = satTrackingControl.ObserverLocation;
                            
                            // Update tracking status (target position will be read from SkyPositionMap directly)
                            skyPositionMap.IsTracking = satTrackingControl.IsTracking;
                        }
                        else
                        {
                            skyPositionMap.IsTracking = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SKY MAP ERROR] Azimuth: {ex.Message}");
                }
            }

            // Update Altitude velocity display and position
            if (actualAltitudeInfo != null)
            {
                txtAltitudeVelocity.Text = $"{actualAltitudeInfo.Command_Velocity_Deg_Sec:F2} deg/s";
                
                try
                {
                    // [NEW] Use corrected position from MountInfo (with pointing model applied)
                    double currentAltitude = 0;
                    if (_pollingManager?.MountInfo != null)
                    {
                        currentAltitude = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;
                    }
                    else
                    {
                        // Fallback: calculate manually if MountInfo not available
                        var altitudeConfig = AppSettings.GetAltitudeAxisConfig();
                        if (altitudeConfig != null)
                        {
                            double altCountsPerDeg = altitudeConfig.Encoder_Resolution / 360.0;
                            currentAltitude = actualAltitudeInfo.Raw_Position / altCountsPerDeg;
                        }
                    }
                    
                    // Clamp altitude to valid range (0-90 degrees)
                    currentAltitude = Math.Max(0, Math.Min(90, currentAltitude));
                    
                    if (skyPositionMap != null)
                    {
                        skyPositionMap.Altitude = currentAltitude; // Show actual sky position (corrected)
                        
                        // [REMOVED] Don't set AltitudeVelocity here - will be set later with all Mount Status properties
                        
                        // Update RMS Error from AltitudeInfo
                        skyPositionMap.AltitudeRmsError = actualAltitudeInfo.TrackingRmsError_Arcsec;
                        
                        // TargetAltitude already set in Azimuth section above to avoid double calculation

                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SKY MAP ERROR] Altitude: {ex.Message}");
                }
            }

            // [NEW] Update Mount Status properties for SkyPositionMap
            if (skyPositionMap != null && actualAzimuthInfo != null && actualAltitudeInfo != null)
            {
                try
                {
                    var azConfig = AppSettings.GetAzimuthAxisConfig();
                    var altConfig = AppSettings.GetAltitudeAxisConfig();

                    // [NEW] Pass MountInfo object (Single Source of Truth)
                    skyPositionMap.MountInfo = _pollingManager?.MountInfo;

                    // Update Command Velocities (for Enabled/Slewing detection)
                    skyPositionMap.AzimuthCommandVelocity = actualAzimuthInfo.Command_Velocity_Deg_Sec;
                    skyPositionMap.AltitudeCommandVelocity = actualAltitudeInfo.Command_Velocity_Deg_Sec;

                    // Update Actual Velocities (convert from counts/s to deg/s for display)
                    double azVelDegSec = (actualAzimuthInfo.Raw_Velocity / azConfig.Encoder_Resolution) * 360.0;
                    double altVelDegSec = (actualAltitudeInfo.Raw_Velocity / altConfig.Encoder_Resolution) * 360.0;
                    skyPositionMap.AzimuthVelocity = azVelDegSec;
                    skyPositionMap.AltitudeVelocity = altVelDegSec;

                    // [CRITICAL] Call UpdateMountStatusFromInfo() from UI Timer (not callback!)
                    // This avoids cross-thread UI access issues
                    skyPositionMap.UpdateMountStatusFromInfo();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MOUNT STATUS ERROR] {ex.Message}");
                }
            }

            // [M3] Update M3 position display when on M3 tab (Tab index 1)
            if (mainTabControl?.SelectedIndex == 1)
            {
                UpdateM3Display();
            }
        }

        /// <summary>
        /// Handle Tab selection change to show/hide trajectory path
        /// [NEW] Also stop tracking when switching between modes
        /// </summary>
        private void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                // [NEW] Stop all tracking when switching tabs to prevent conflicts
                // Only handle if this is the main tab control (not nested tabs)
                if (e.Source == mainTabControl)
                {
                    // Get the newly selected tab
                    if (mainTabControl.SelectedItem is TabItem selectedTab)
                    {
                        string tabHeader = selectedTab.Header?.ToString() ?? "Unknown";
                        SimpleLogger.Info($"[TAB SWITCH] Switching to: {tabHeader}", "ManualCtrl");
                        
                        
                        SimpleLogger.Info($"[TAB SWITCH] ? Switched to {tabHeader}", "ManualCtrl");
                    }
                }
                
                // Handle trajectory path visibility based on tab
                if (skyPositionMap != null && satTrackingControl != null)
                {
                    if (mainTabControl?.SelectedIndex == 3)
                    {
                        // Tab index 3 = Satellite Object - show satellite path
                        // Priority: Selected satellite > Current tracking satellite
                        skyPositionMap.ObserverLocation = satTrackingControl.ObserverLocation;

                        Engine.Tracking.SatelliteTracker trackerToDisplay = satTrackingControl.SelectedSatelliteTracker;

                        if (trackerToDisplay == null)
                        {
                            trackerToDisplay = satTrackingControl.CurrentSatelliteTracker;
                        }

                        skyPositionMap.SatelliteTracker = trackerToDisplay;
                        System.Diagnostics.Debug.WriteLine($"[TAB SWITCH] Satellite tab - showing path for: {trackerToDisplay?.Name ?? "NONE"}");
                    }
                    else if (mainTabControl?.SelectedIndex == 4)
                    {
                        // Tab index 4 = Bright Star Object (sidereal) - no path
                        skyPositionMap.SatelliteTracker = null;
                        skyPositionMap.ObserverLocation = null;
                        System.Diagnostics.Debug.WriteLine("[TAB SWITCH] Bright Star tab - path cleared");
                    }
                    else if (mainTabControl?.SelectedIndex == 5)
                    {
                        // Tab index 5 = Planet Object - no path
                        skyPositionMap.SatelliteTracker = null;
                        skyPositionMap.ObserverLocation = null;
                        System.Diagnostics.Debug.WriteLine("[TAB SWITCH] Planet tab - path cleared");
                    }
                    else
                    {
                        // Other tabs (Direction Control, etc.) - clear trajectory path
                        skyPositionMap.SatelliteTracker = null;
                        skyPositionMap.ObserverLocation = null;
                        System.Diagnostics.Debug.WriteLine("[TAB SWITCH] Other tab - path cleared");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TAB CHANGE ERROR] {ex.Message}");
                SimpleLogger.Error($"[TAB SWITCH] Error: {ex.Message}", "ManualCtrl");
            }
        }

        #region Title Bar

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            // Stop all motion before closing
            StopAllMotion();
            
            // Save positions one final time
            GotoPositionStorage.SavePositions(_gotoPositions);
            
            SimpleLogger.Info("Manual Control window closed", "ManualCtrl");
            this.Close();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            SimpleLogger.Warning("[EMERGENCY STOP] Emergency stop activated from Manual Control!", "ManualCtrl");
            
            try
            {
                // Step 1: Stop Position Control (Hold Mode) for Azimuth
                if (_azimuthController != null)
                {
                    _azimuthController.StopPositionControlForManualMode();
                    SimpleLogger.Info("[EMERGENCY STOP] Azimuth Position Control stopped", "ManualCtrl");
                }
                
                // Step 2: Set Command Velocity to 0 for both axes
                StopAllMotion();
                
                // Step 3: Send ST (Stop) command to Azimuth Drive
                if (_azimuthDrive != null && _azimuthDrive.IsConnected)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            _azimuthDrive.StopMotion();
                            SimpleLogger.Info("[EMERGENCY STOP] ST command sent to Azimuth Drive", "ManualCtrl");
                        }
                        catch (Exception ex)
                        {
                            SimpleLogger.Error($"[EMERGENCY STOP] Failed to send ST to Azimuth: {ex.Message}", "ManualCtrl");
                        }
                    });
                }
                
                // Step 4: Send ST (Stop) command to Altitude Drive (if connected)
                if (_altitudeDrive != null)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            _altitudeDrive.StopMotion();
                            SimpleLogger.Info("[EMERGENCY STOP] ST command sent to Altitude Drive", "ManualCtrl");
                        }
                        catch (Exception ex)
                        {
                            SimpleLogger.Error($"[EMERGENCY STOP] Failed to send ST to Altitude: {ex.Message}", "ManualCtrl");
                        }
                    });
                }
                
                SimpleLogger.Info("[EMERGENCY STOP] All motion stopped - Motors will coast to stop", "ManualCtrl");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[EMERGENCY STOP] Error: {ex.Message}", "ManualCtrl");
            }
        }

        #endregion

        #region Direction Button Events

        private void CmbDirectionMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            StopAllMotion();
            UpdateDirectionControlModeUi();

            if (borderDirectionOffsetPreview != null && GetDirectionControlMode() != DirectionControlMode.DirectionOffset)
            {
                ResetDirectionOffsetState(resetMountOffsets: true);
                borderDirectionOffsetPreview.Visibility = Visibility.Collapsed;
                borderDirectionOffsetPreview.Height = double.NaN;
            }

            if (offsetTypePanel != null && GetDirectionControlMode() != DirectionControlMode.DirectionOffset)
            {
                offsetTypePanel.Visibility = Visibility.Collapsed;
            }

            UpdateDirectionOffsetPreview();
        }

        private void CmbOffsetType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ClearExecutedOffsetPreview();
            UpdateDirectionOffsetPreview();
        }

        private void BorderDirectionOffsetPreview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateDirectionOffsetPreview();
        }

        private void TxtVelocity_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                UpdateDirectionOffsetPreview();
            }
        }

        private void BtnResetOffset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ResetCurrentDirectionOffset();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[OFFSET-RESET] Failed: {ex.Message}", "ManualCtrl");
                MessageBox.Show($"Reset offset failed:{Environment.NewLine}{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ========== UP-LEFT (Diagonal: Altitude +, Azimuth -) ==========
        private void BtnUpLeft_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(-1, 1, "[UP-LEFT]"))
            {
                e.Handled = true;
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[UP-LEFT] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Azimuth: Move CCW (-) via CentralizedPollingManager
            if (_isAzimuthConnected)
            {
                _azimuthController?.StopPositionControlForManualMode();
                _azimuthInfo.Command_Velocity_Deg_Sec = -Math.Abs(velocity);
                _pollingManager?.SetAzimuthCommandVelocity(-Math.Abs(velocity));
                SimpleLogger.Info($"[UP-LEFT] Azimuth moving CCW: {-Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }

            // Altitude: Move Up (+) via CentralizedPollingManager
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = Math.Abs(velocity);
                _pollingManager?.SetAltitudeCommandVelocity(Math.Abs(velocity));
                SimpleLogger.Info($"[UP-LEFT] Altitude moving UP: {Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }
        }

        private void BtnUpLeft_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAzimuthConnected)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAzimuthCommandVelocity(0);
            }
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAltitudeCommandVelocity(0);
            }
            SimpleLogger.Info("[UP-LEFT] Button released - Stopped", "ManualCtrl");
        }

        // ========== UP-RIGHT (Diagonal: Altitude +, Azimuth +) ==========
        private void BtnUpRight_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(1, 1, "[UP-RIGHT]"))
            {
                e.Handled = true;
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[UP-RIGHT] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Azimuth: Move CW (+) via CentralizedPollingManager
            if (_isAzimuthConnected)
            {
                _azimuthController?.StopPositionControlForManualMode();
                _azimuthInfo.Command_Velocity_Deg_Sec = Math.Abs(velocity);
                _pollingManager?.SetAzimuthCommandVelocity(Math.Abs(velocity));
                SimpleLogger.Info($"[UP-RIGHT] Azimuth moving CW: {Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }

            // Altitude: Move Up (+) via CentralizedPollingManager
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = Math.Abs(velocity);
                _pollingManager?.SetAltitudeCommandVelocity(Math.Abs(velocity));
                SimpleLogger.Info($"[UP-RIGHT] Altitude moving UP: {Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }
        }

        private void BtnUpRight_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAzimuthConnected)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAzimuthCommandVelocity(0);
            }
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAltitudeCommandVelocity(0);
            }
            SimpleLogger.Info("[UP-RIGHT] Button released - Stopped", "ManualCtrl");
        }

        // ========== DOWN-LEFT (Diagonal: Altitude -, Azimuth -) ==========
        private void BtnDownLeft_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(-1, -1, "[DOWN-LEFT]"))
            {
                e.Handled = true;
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[DOWN-LEFT] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Azimuth: Move CCW (-) via CentralizedPollingManager
            if (_isAzimuthConnected)
            {
                _azimuthController?.StopPositionControlForManualMode();
                _azimuthInfo.Command_Velocity_Deg_Sec = -Math.Abs(velocity);
                _pollingManager?.SetAzimuthCommandVelocity(-Math.Abs(velocity));
                SimpleLogger.Info($"[DOWN-LEFT] Azimuth moving CCW: {-Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }

            // Altitude: Move Down (-) via CentralizedPollingManager
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = -Math.Abs(velocity);
                _pollingManager?.SetAltitudeCommandVelocity(-Math.Abs(velocity));
                SimpleLogger.Info($"[DOWN-LEFT] Altitude moving DOWN: {-Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }
        }

        private void BtnDownLeft_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAzimuthConnected)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAzimuthCommandVelocity(0);
            }
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAltitudeCommandVelocity(0);
            }
            SimpleLogger.Info("[DOWN-LEFT] Button released - Stopped", "ManualCtrl");
        }

        // ========== DOWN-RIGHT (Diagonal: Altitude -, Azimuth +) ==========
        private void BtnDownRight_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(1, -1, "[DOWN-RIGHT]"))
            {
                e.Handled = true;
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[DOWN-RIGHT] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Azimuth: Move CW (+) via CentralizedPollingManager
            if (_isAzimuthConnected)
            {
                _azimuthController?.StopPositionControlForManualMode();
                _azimuthInfo.Command_Velocity_Deg_Sec = Math.Abs(velocity);
                _pollingManager?.SetAzimuthCommandVelocity(Math.Abs(velocity));
                SimpleLogger.Info($"[DOWN-RIGHT] Azimuth moving CW: {Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }

            // Altitude: Move Down (-) via CentralizedPollingManager
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = -Math.Abs(velocity);
                _pollingManager?.SetAltitudeCommandVelocity(-Math.Abs(velocity));
                SimpleLogger.Info($"[DOWN-RIGHT] Altitude moving DOWN: {-Math.Abs(velocity):F2} deg/s", "ManualCtrl");
            }
        }

        private void BtnDownRight_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAzimuthConnected)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAzimuthCommandVelocity(0);
            }
            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAltitudeCommandVelocity(0);
            }
            SimpleLogger.Info("[DOWN-RIGHT] Button released - Stopped", "ManualCtrl");
        }

        // ========== UP (Altitude +) ==========
        private void BtnUp_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(0, 1, "[UP]"))
            {
                e.Handled = true;
                return;
            }

            if (!_isAltitudeConnected)
            {
                SimpleLogger.Error("[UP] Altitude Drive not connected", "ManualCtrl");
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[UP] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Send positive velocity to Altitude (UP) via CentralizedPollingManager
            _altitudeInfo.Command_Velocity_Deg_Sec = Math.Abs(velocity);
            _pollingManager?.SetAltitudeCommandVelocity(Math.Abs(velocity));
            SimpleLogger.Info($"[UP] Moving up with velocity: {Math.Abs(velocity):F2} deg/s", "ManualCtrl");
        }

        private void BtnUp_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAltitudeCommandVelocity(0);
                SimpleLogger.Info("[UP] Button released - Stopped", "ManualCtrl");
            }
        }

        // ========== DOWN (Altitude -) ==========
        private void BtnDown_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(0, -1, "[DOWN]"))
            {
                e.Handled = true;
                return;
            }

            if (!_isAltitudeConnected)
            {
                SimpleLogger.Error("[DOWN] Altitude Drive not connected", "ManualCtrl");
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[DOWN] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Send negative velocity to Altitude (DOWN) via CentralizedPollingManager
            _altitudeInfo.Command_Velocity_Deg_Sec = -Math.Abs(velocity);
            _pollingManager?.SetAltitudeCommandVelocity(-Math.Abs(velocity));
            SimpleLogger.Info($"[DOWN] Moving down with velocity: {-Math.Abs(velocity):F2} deg/s", "ManualCtrl");
        }

        private void BtnDown_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAltitudeConnected)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAltitudeCommandVelocity(0);
                SimpleLogger.Info("[DOWN] Button released - Stopped", "ManualCtrl");
            }
        }

        // ========== LEFT (Azimuth CCW / -) ==========
        private void BtnLeft_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(-1, 0, "[LEFT]"))
            {
                e.Handled = true;
                return;
            }

            if (!_isAzimuthConnected)
            {
                SimpleLogger.Error("[LEFT] Azimuth Drive not connected", "ManualCtrl");
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[LEFT] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Stop Position Control if active (disable Hold Mode)
            _azimuthController?.StopPositionControlForManualMode();

            // Send negative velocity to Azimuth (CCW) via CentralizedPollingManager
            _azimuthInfo.Command_Velocity_Deg_Sec = -Math.Abs(velocity);
            _pollingManager?.SetAzimuthCommandVelocity(-Math.Abs(velocity));
            SimpleLogger.Info($"[LEFT] Moving CCW with velocity: {-Math.Abs(velocity):F2} deg/s", "ManualCtrl");
        }

        private void BtnLeft_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAzimuthConnected)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAzimuthCommandVelocity(0);
                SimpleLogger.Info("[LEFT] Button released - Stopped", "ManualCtrl");
            }
        }

        // ========== RIGHT (Azimuth CW / +) ==========
        private void BtnRight_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TryHandleDirectionOffset(1, 0, "[RIGHT]"))
            {
                e.Handled = true;
                return;
            }

            if (!_isAzimuthConnected)
            {
                SimpleLogger.Error("[RIGHT] Azimuth Drive not connected", "ManualCtrl");
                return;
            }

            if (!double.TryParse(txtVelocity.Text, out double velocity))
            {
                SimpleLogger.Error("[RIGHT] Invalid velocity value", "ManualCtrl");
                return;
            }

            // Stop Position Control if active (disable Hold Mode)
            _azimuthController?.StopPositionControlForManualMode();

            // Send positive velocity to Azimuth (CW) via CentralizedPollingManager
            _azimuthInfo.Command_Velocity_Deg_Sec = Math.Abs(velocity);
            _pollingManager?.SetAzimuthCommandVelocity(Math.Abs(velocity));
            SimpleLogger.Info($"[RIGHT] Moving CW with velocity: {Math.Abs(velocity):F2} deg/s", "ManualCtrl");
        }

        private void BtnRight_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                return;
            }

            if (_isAzimuthConnected)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAzimuthCommandVelocity(0);
                SimpleLogger.Info("[RIGHT] Button released - Stopped", "ManualCtrl");
            }
        }

        #endregion

        #region Helper Methods

        private DirectionControlMode GetDirectionControlMode()
        {
            if (cmbDirectionMode?.SelectedItem is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), "Direction Offset", StringComparison.OrdinalIgnoreCase))
            {
                return DirectionControlMode.DirectionOffset;
            }

            return DirectionControlMode.MovementVelocity;
        }

        private void UpdateDirectionControlModeUi()
        {
            if (txtDirectionValueLabel == null || txtDirectionValueUnit == null || txtDirectionInstructions == null)
            {
                return;
            }

            if (GetDirectionControlMode() == DirectionControlMode.DirectionOffset)
            {
                txtDirectionValueLabel.Text = "Offset Size";
                txtDirectionValueUnit.Text = "deg";
                txtDirectionInstructions.Text =
                    "• Click an arrow button once to move by the selected offset distance" + Environment.NewLine +
                    "• The mount will move in that direction and stop automatically" + Environment.NewLine +
                    "• Diagonal buttons move both axes by the same offset amount" + Environment.NewLine +
                    "• Use STOP if you need to cancel motion immediately";

                if (string.Equals(txtVelocity.Text, "20.0", StringComparison.Ordinal))
                {
                    txtVelocity.Text = "1.0";
                }

                if (borderDirectionOffsetPreview != null)
                {
                    borderDirectionOffsetPreview.Visibility = Visibility.Visible;
                    ApplyDirectionOffsetPreviewHeight();
                }

                if (offsetTypePanel != null)
                {
                    offsetTypePanel.Visibility = Visibility.Visible;
                }
            }
            else
            {
                txtDirectionValueLabel.Text = "Movement Velocity";
                txtDirectionValueUnit.Text = "deg/s";
                txtDirectionInstructions.Text =
                    "• Press and hold arrow buttons to move" + Environment.NewLine +
                    "• Release button to stop" + Environment.NewLine +
                    "• Adjust velocity before moving" + Environment.NewLine +
                    "• Motor will stop when button is released";
            }
        }

        private bool TryParseDirectionValue(out double value, string logPrefix)
        {
            if (!double.TryParse(txtVelocity.Text, out value) || value <= 0)
            {
                SimpleLogger.Error($"{logPrefix} Invalid value", "ManualCtrl");
                MessageBox.Show("Please enter a value greater than 0.", "Invalid Value", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private bool TryHandleDirectionOffset(int azimuthDirection, int altitudeDirection, string logPrefix)
        {
            if (GetDirectionControlMode() != DirectionControlMode.DirectionOffset)
            {
                return false;
            }

            if (!TryParseDirectionValue(out double offsetDeg, logPrefix))
            {
                return true;
            }

            try
            {
                SetLastOffsetDirection(azimuthDirection, altitudeDirection);
                MoveByDirectionOffset(azimuthDirection, altitudeDirection, offsetDeg, logPrefix);
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"{logPrefix} Direction offset failed: {ex.Message}", "ManualCtrl");
                MessageBox.Show($"Direction offset failed:{Environment.NewLine}{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return true;
        }

        private void ApplyDirectionOffsetPreviewHeight()
        {
            if (borderDirectionOffsetPreview == null || directionControlRightPanel == null)
            {
                return;
            }

            double panelHeight = directionControlRightPanel.ActualHeight;
            if (panelHeight <= 0)
            {
                return;
            }

            borderDirectionOffsetPreview.Height = Math.Max(235, Math.Min(420, panelHeight * 0.62));
        }

        private void SetLastOffsetDirection(int azimuthDirection, int altitudeDirection)
        {
            _lastOffsetAzimuthDirection = azimuthDirection;
            _lastOffsetAltitudeDirection = altitudeDirection;
            _lastOffsetDirectionLabel = GetOffsetDirectionLabel(azimuthDirection, altitudeDirection);
        }

        private void RecordExecutedOffset(double azimuthDeltaDeg, double altitudeDeltaDeg)
        {
            _cumulativeAzimuthOffsetDeg += azimuthDeltaDeg;
            _cumulativeAltitudeOffsetDeg += altitudeDeltaDeg;
            _executedOffsetPath.Add((_cumulativeAzimuthOffsetDeg, _cumulativeAltitudeOffsetDeg));
            UpdateDirectionOffsetPreview();
        }

        private void ClearExecutedOffsetPreview()
        {
            _executedOffsetPath.Clear();
            _cumulativeAzimuthOffsetDeg = 0;
            _cumulativeAltitudeOffsetDeg = 0;
        }

        private void ResetDirectionOffsetState(bool resetMountOffsets)
        {
            ClearExecutedOffsetPreview();
            _lastOffsetAzimuthDirection = 1;
            _lastOffsetAltitudeDirection = 0;
            _lastOffsetDirectionLabel = "Right";

            if (cmbOffsetType != null)
            {
                cmbOffsetType.SelectedIndex = 0;
            }

            if (resetMountOffsets && App.MountInfo != null)
            {
                App.MountInfo.TelescopeOffset_Az_Arcsec = 0;
                App.MountInfo.TelescopeOffset_Alt_Arcsec = 0;
            }

            UpdateDirectionOffsetPreview();
        }

        private OffsetCoordinateType GetOffsetCoordinateType()
        {
            if (cmbOffsetType?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                string.Equals(tag, "RaDec", StringComparison.OrdinalIgnoreCase))
            {
                return OffsetCoordinateType.RaDec;
            }

            return OffsetCoordinateType.AzimuthAltitude;
        }

        private static string GetOffsetDirectionLabel(int azimuthDirection, int altitudeDirection)
        {
            if (azimuthDirection > 0 && altitudeDirection > 0) return "Up-Right";
            if (azimuthDirection < 0 && altitudeDirection > 0) return "Up-Left";
            if (azimuthDirection > 0 && altitudeDirection < 0) return "Down-Right";
            if (azimuthDirection < 0 && altitudeDirection < 0) return "Down-Left";
            if (azimuthDirection > 0) return "Right";
            if (azimuthDirection < 0) return "Left";
            if (altitudeDirection > 0) return "Up";
            if (altitudeDirection < 0) return "Down";
            return "Center";
        }

        private (double rawAzimuthDeg, double rawAltitudeDeg) GetCurrentMountCommandPosition()
        {
            if (_pollingManager?.MountInfo == null)
            {
                throw new InvalidOperationException("Mount status is not available.");
            }

            double rawAzimuth = _pollingManager.MountInfo.Act_Azimuth_Pos_Raw;
            double rawAltitude = _pollingManager.MountInfo.Act_Altitude_Pos_Raw;

            if (double.IsNaN(rawAzimuth) || double.IsNaN(rawAltitude))
            {
                throw new InvalidOperationException("Current raw mount position is invalid.");
            }

            return (rawAzimuth, rawAltitude);
        }

        private void ResetCurrentDirectionOffset()
        {
            if (_executedOffsetPath.Count == 0)
            {
                UpdateDirectionOffsetPreview();
                return;
            }

            bool isTrackingActive = TrackingManager.Instance.IsTracking;
            double resetAzimuthDeltaDeg = -_cumulativeAzimuthOffsetDeg;
            double resetAltitudeDeltaDeg = -_cumulativeAltitudeOffsetDeg;

            if (isTrackingActive)
            {
                if (App.MountInfo == null)
                {
                    throw new InvalidOperationException("MountInfo is not available for offset reset.");
                }

                App.MountInfo.TelescopeOffset_Az_Arcsec = 0;
                App.MountInfo.TelescopeOffset_Alt_Arcsec = 0;
                SimpleLogger.Info("[OFFSET-RESET] Cleared tracking telescope offsets back to zero.", "ManualCtrl");
            }
            else
            {
                var (currentAzimuth, currentAltitude) = GetCurrentMountCommandPosition();

                bool canMoveAzimuth = _isAzimuthConnected && _azimuthDrive != null && _azimuthDrive.IsConnected;
                bool canMoveAltitude = _isAltitudeConnected && _altitudeDrive != null;

                if (!canMoveAzimuth && !canMoveAltitude)
                {
                    throw new InvalidOperationException("No connected axis is available for offset reset.");
                }

                double azimuthVelocity = GetOffsetMoveVelocity(isAzimuth: true);
                double altitudeVelocity = GetOffsetMoveVelocity(isAzimuth: false);

                if (canMoveAzimuth && Math.Abs(resetAzimuthDeltaDeg) > 0.0001)
                {
                    _azimuthController?.StopPositionControlForManualMode();
                    double targetAzimuth = currentAzimuth + resetAzimuthDeltaDeg;
                    _azimuthDrive.MoveToPosition(targetAzimuth, azimuthVelocity);
                    SimpleLogger.Info($"[OFFSET-RESET] Azimuth reset move: {currentAzimuth:F3} -> {targetAzimuth:F3} deg", "ManualCtrl");
                }

                if (canMoveAltitude && Math.Abs(resetAltitudeDeltaDeg) > 0.0001)
                {
                    double targetAltitude = currentAltitude + resetAltitudeDeltaDeg;
                    _altitudeDrive.MoveToPosition(targetAltitude, altitudeVelocity);
                    SimpleLogger.Info($"[OFFSET-RESET] Altitude reset move: {currentAltitude:F3} -> {targetAltitude:F3} deg", "ManualCtrl");
                }
            }

            ClearExecutedOffsetPreview();
            _lastOffsetAzimuthDirection = 0;
            _lastOffsetAltitudeDirection = 0;
            _lastOffsetDirectionLabel = "Center";
            UpdateDirectionOffsetPreview();
        }

        private void UpdateDirectionOffsetPreview()
        {
            if (offsetPreviewCanvas == null || txtOffsetPreviewSummary == null || txtOffsetPreviewLegend == null)
            {
                return;
            }

            if (GetDirectionControlMode() != DirectionControlMode.DirectionOffset)
            {
                offsetPreviewCanvas.Children.Clear();
                return;
            }

            if (btnResetOffset != null)
            {
                btnResetOffset.IsEnabled = _executedOffsetPath.Count > 0;
            }

            ApplyDirectionOffsetPreviewHeight();

            double offsetDeg = 1.0;
            if (!double.TryParse(txtVelocity?.Text, out offsetDeg) || offsetDeg <= 0)
            {
                offsetDeg = 1.0;
            }

            double width = offsetPreviewCanvas.ActualWidth;
            double height = offsetPreviewCanvas.ActualHeight;

            if (width < 40 || height < 40)
            {
                width = Math.Max(width, (borderDirectionOffsetPreview?.ActualWidth ?? 340) - 30);
                height = Math.Max(height, (borderDirectionOffsetPreview?.ActualHeight ?? 220) - 90);
            }

            if (width < 40 || height < 40)
            {
                return;
            }

            double centerX = width * 0.50;
            double centerY = height * 0.52;

            offsetPreviewCanvas.Children.Clear();

            if (_executedOffsetPath.Count == 0)
            {
                txtOffsetPreviewSummary.Text = "No offset movement yet";
                txtOffsetPreviewLegend.Text = $"The preview will appear after the first real {(GetOffsetCoordinateType() == OffsetCoordinateType.RaDec ? "RA/Dec" : "Azm/Alt")} direction offset movement.";
                return;
            }

            AddOffsetAxis(centerX, centerY, width, height);

            double maxMagnitude = Math.Max(
                Math.Max(Math.Abs(_cumulativeAzimuthOffsetDeg), Math.Abs(_cumulativeAltitudeOffsetDeg)),
                _executedOffsetPath.Max(p => Math.Max(Math.Abs(p.AzimuthDeltaDeg), Math.Abs(p.AltitudeDeltaDeg))));
            maxMagnitude = Math.Max(maxMagnitude, offsetDeg);

            double scaleX = (width * 0.34) / Math.Max(maxMagnitude, 0.1);
            double scaleY = (height * 0.34) / Math.Max(maxMagnitude, 0.1);

            var pathPoints = new List<Point> { new(centerX, centerY) };
            foreach (var step in _executedOffsetPath)
            {
                pathPoints.Add(new Point(
                    centerX + (step.AzimuthDeltaDeg * scaleX),
                    centerY - (step.AltitudeDeltaDeg * scaleY)));
            }

            var executedPath = new Polyline
            {
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F87171")),
                StrokeThickness = 2.8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = new PointCollection(pathPoints)
            };
            offsetPreviewCanvas.Children.Add(executedPath);

            for (int i = 0; i < pathPoints.Count; i++)
            {
                bool isCenter = i == 0;
                bool isLatest = i == pathPoints.Count - 1 && !isCenter;
                double markerSize = isCenter ? 10 : (isLatest ? 15 : 11);
                var marker = new Ellipse
                {
                    Width = markerSize,
                    Height = markerSize,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        isCenter ? "#FFFFFF" : (isLatest ? "#FEF3C7" : "#FCA5A5"))),
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        isCenter ? "#DC2626" : (isLatest ? "#F59E0B" : "#7F1D1D"))),
                    StrokeThickness = isCenter || isLatest ? 2 : 1.5
                };
                Canvas.SetLeft(marker, pathPoints[i].X - markerSize / 2);
                Canvas.SetTop(marker, pathPoints[i].Y - markerSize / 2);
                offsetPreviewCanvas.Children.Add(marker);

                var label = new TextBlock
                {
                    Text = isCenter ? "Center" : i.ToString(),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        isCenter ? "#FECACA" : (isLatest ? "#FEF3C7" : "#FECACA"))),
                    FontSize = isCenter ? 11 : 10,
                    FontWeight = isCenter || isLatest ? FontWeights.Bold : FontWeights.SemiBold
                };
                Canvas.SetLeft(label, pathPoints[i].X + 8);
                Canvas.SetTop(label, pathPoints[i].Y - 18);
                offsetPreviewCanvas.Children.Add(label);
            }

            if (pathPoints.Count >= 2)
            {
                var previous = pathPoints[^2];
                var latest = pathPoints[^1];
                var latestVector = new Line
                {
                    X1 = previous.X,
                    Y1 = previous.Y,
                    X2 = latest.X,
                    Y2 = latest.Y,
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FDE68A")),
                    StrokeThickness = 3.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                offsetPreviewCanvas.Children.Add(latestVector);
            }

            txtOffsetPreviewSummary.Text =
                $"Last direction: {_lastOffsetDirectionLabel}   Step: {offsetDeg:F2} deg   Total mount move: Az {_cumulativeAzimuthOffsetDeg:F2}, Alt {_cumulativeAltitudeOffsetDeg:F2}";
            txtOffsetPreviewLegend.Text =
                $"Executed mount-axis offset path with {_executedOffsetPath.Count} step(s). The yellow segment shows the most recent movement. Reset Offset returns this path to zero.";
        }

        private void AddOffsetAxis(double centerX, double centerY, double width, double height)
        {
            var horizontalAxis = new Line
            {
                X1 = width * 0.12,
                Y1 = centerY,
                X2 = width * 0.88,
                Y2 = centerY,
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7F1D1D")),
                StrokeThickness = 1,
                Opacity = 0.55,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            offsetPreviewCanvas.Children.Add(horizontalAxis);

            var verticalAxis = new Line
            {
                X1 = centerX,
                Y1 = height * 0.14,
                X2 = centerX,
                Y2 = height * 0.90,
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7F1D1D")),
                StrokeThickness = 1,
                Opacity = 0.55,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            offsetPreviewCanvas.Children.Add(verticalAxis);

            bool isRaDec = GetOffsetCoordinateType() == OffsetCoordinateType.RaDec;
            AddAxisLabel(width * 0.89, centerY - 18, isRaDec ? "RA+" : "Az+");
            AddAxisLabel(width * 0.08, centerY - 18, isRaDec ? "RA-" : "Az-");
            AddAxisLabel(centerX + 8, height * 0.10, isRaDec ? "Dec+" : "Alt+");
            AddAxisLabel(centerX + 8, height * 0.90, isRaDec ? "Dec-" : "Alt-");
        }

        private void AddAxisLabel(double x, double y, string text)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5")),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            offsetPreviewCanvas.Children.Add(label);
        }

        private static int GetActiveOffsetPointIndex(int azimuthDirection, int altitudeDirection)
        {
            if (azimuthDirection > 0 && altitudeDirection == 0) return 0;
            if (azimuthDirection > 0 && altitudeDirection > 0) return 1;
            if (azimuthDirection == 0 && altitudeDirection > 0) return 2;
            if (azimuthDirection < 0 && altitudeDirection > 0) return 3;
            if (azimuthDirection < 0 && altitudeDirection == 0) return 4;
            if (azimuthDirection < 0 && altitudeDirection < 0) return 5;
            if (azimuthDirection == 0 && altitudeDirection < 0) return 6;
            if (azimuthDirection > 0 && altitudeDirection < 0) return 7;
            return 0;
        }

        private void MoveByDirectionOffset(int azimuthDirection, int altitudeDirection, double offsetDeg, string logPrefix)
        {
            if (_pollingManager?.MountInfo == null)
            {
                throw new InvalidOperationException("Mount status is not available.");
            }

            bool willMoveAzimuth = azimuthDirection != 0 && _isAzimuthConnected && _azimuthDrive != null && _azimuthDrive.IsConnected;
            bool willMoveAltitude = altitudeDirection != 0 && _isAltitudeConnected && _altitudeDrive != null;

            if (!willMoveAzimuth && !willMoveAltitude)
            {
                throw new InvalidOperationException("No connected axis is available for this direction.");
            }

            var mountInfo = _pollingManager.MountInfo;
            double currentAzimuth = mountInfo.Act_Azimuth_Pos_Correct;
            double currentAltitude = mountInfo.Act_Altitude_Pos_Correct;

            double azimuthVelocity = GetOffsetMoveVelocity(isAzimuth: true);
            double altitudeVelocity = GetOffsetMoveVelocity(isAzimuth: false);
            bool isTrackingActive = TrackingManager.Instance.IsTracking;

            double commandedAzimuthDelta = azimuthDirection * offsetDeg;
            double commandedAltitudeDelta = altitudeDirection * offsetDeg;

            if (GetOffsetCoordinateType() == OffsetCoordinateType.RaDec)
            {
                var (targetAzimuth, targetAltitude, appliedAzimuthDelta, appliedAltitudeDelta, targetRaHours, targetDecDeg) =
                    ConvertRaDecOffsetToAltAzTarget(currentAltitude, currentAzimuth, azimuthDirection, altitudeDirection, offsetDeg);

                commandedAzimuthDelta = appliedAzimuthDelta;
                commandedAltitudeDelta = appliedAltitudeDelta;
                bool shouldMoveAzimuthForRaDec = _isAzimuthConnected && _azimuthDrive != null && _azimuthDrive.IsConnected &&
                    Math.Abs(commandedAzimuthDelta) > 0.0001;
                bool shouldMoveAltitudeForRaDec = _isAltitudeConnected && _altitudeDrive != null &&
                    Math.Abs(commandedAltitudeDelta) > 0.0001;

                if (isTrackingActive)
                {
                    ApplyTrackingOffset(commandedAzimuthDelta, commandedAltitudeDelta, logPrefix, "RA/Dec");
                    RecordExecutedOffset(commandedAzimuthDelta, commandedAltitudeDelta);
                    return;
                }

                if (!shouldMoveAzimuthForRaDec && !shouldMoveAltitudeForRaDec)
                {
                    SimpleLogger.Info($"{logPrefix} RA/Dec offset resolved to negligible axis movement.", "ManualCtrl");
                    return;
                }

                var (currentRawAzimuth, currentRawAltitude) = GetCurrentMountCommandPosition();
                double commandTargetAzimuth = currentRawAzimuth + appliedAzimuthDelta;
                double commandTargetAltitude = currentRawAltitude + appliedAltitudeDelta;

                if (shouldMoveAzimuthForRaDec)
                {
                    _azimuthController?.StopPositionControlForManualMode();
                    _azimuthDrive.MoveToPosition(commandTargetAzimuth, azimuthVelocity);
                    SimpleLogger.Info($"{logPrefix} RA/Dec local offset -> Azimuth raw {currentRawAzimuth:F3} -> {commandTargetAzimuth:F3} deg (sky target {targetAzimuth:F3}, dAz {appliedAzimuthDelta:F3}) at {azimuthVelocity:F2} deg/s", "ManualCtrl");
                }

                if (shouldMoveAltitudeForRaDec)
                {
                    _altitudeDrive.MoveToPosition(commandTargetAltitude, altitudeVelocity);
                    SimpleLogger.Info($"{logPrefix} RA/Dec local offset -> Altitude raw {currentRawAltitude:F3} -> {commandTargetAltitude:F3} deg (sky target {targetAltitude:F3}, dAlt {appliedAltitudeDelta:F3}) at {altitudeVelocity:F2} deg/s", "ManualCtrl");
                }

                RecordExecutedOffset(
                    shouldMoveAzimuthForRaDec ? commandedAzimuthDelta : 0,
                    shouldMoveAltitudeForRaDec ? commandedAltitudeDelta : 0);
                return;
            }

            if (isTrackingActive)
            {
                ApplyTrackingOffset(commandedAzimuthDelta, commandedAltitudeDelta, logPrefix, "Azm/Alt");
                RecordExecutedOffset(commandedAzimuthDelta, commandedAltitudeDelta);
                return;
            }

            if (willMoveAzimuth)
            {
                var (commandAzimuth, _) = GetCurrentMountCommandPosition();
                _azimuthController?.StopPositionControlForManualMode();
                double targetAzimuth = commandAzimuth + commandedAzimuthDelta;
                _azimuthDrive.MoveToPosition(targetAzimuth, azimuthVelocity);
                SimpleLogger.Info($"{logPrefix} Azimuth offset move: raw {commandAzimuth:F3} -> {targetAzimuth:F3} deg (sky delta {commandedAzimuthDelta:F3} deg) at {azimuthVelocity:F2} deg/s", "ManualCtrl");
            }

            if (willMoveAltitude)
            {
                var (_, commandAltitude) = GetCurrentMountCommandPosition();
                double targetAltitude = commandAltitude + commandedAltitudeDelta;
                _altitudeDrive.MoveToPosition(targetAltitude, altitudeVelocity);
                SimpleLogger.Info($"{logPrefix} Altitude offset move: raw {commandAltitude:F3} -> {targetAltitude:F3} deg (sky delta {commandedAltitudeDelta:F3} deg) at {altitudeVelocity:F2} deg/s", "ManualCtrl");
            }

            RecordExecutedOffset(
                willMoveAzimuth ? commandedAzimuthDelta : 0,
                willMoveAltitude ? commandedAltitudeDelta : 0);
        }

        private double GetOffsetMoveVelocity(bool isAzimuth)
        {
            if (isAzimuth)
            {
                var config = AppSettings.GetAzimuthAxisConfig();
                return Math.Max(0.1, Math.Min(5.0, config?.Max_Velocity_Deg_Sec ?? 1.0));
            }

            var altitudeConfig = AppSettings.GetAltitudeAxisConfig();
            return Math.Max(0.1, Math.Min(5.0, altitudeConfig?.Max_Velocity_Deg_Sec ?? 1.0));
        }

        private (double targetAzimuthDeg, double targetAltitudeDeg, double azimuthDeltaDeg, double altitudeDeltaDeg, double targetRaHours, double targetDecDeg)
            ConvertRaDecOffsetToAltAzTarget(double currentAltitudeDeg, double currentAzimuthDeg, int raDirection, int decDirection, double offsetDeg)
        {
            var locationSettings = AppSettings.GetObserverLocation();
            DateTime utcNow = DateTime.UtcNow;

            var (raHours, decDeg) = MountCalibrationHelper.ConvertAltAzToRaDec(
                currentAltitudeDeg,
                currentAzimuthDeg,
                locationSettings.Latitude_Deg,
                locationSettings.Longitude_Deg,
                utcNow);

            double appliedRaSkyOffsetDeg = raDirection * offsetDeg;
            double appliedDecOffsetDeg = decDirection * offsetDeg;

            if (raDirection != 0 && decDirection != 0)
            {
                double diagonalComponent = offsetDeg / Math.Sqrt(2.0);
                appliedRaSkyOffsetDeg = raDirection * diagonalComponent;
                appliedDecOffsetDeg = decDirection * diagonalComponent;
            }

            double cosDec = Math.Cos(decDeg * Math.PI / 180.0);
            double safeCosDec = Math.Max(Math.Abs(cosDec), 0.1);
            double raCoordinateOffsetDeg = appliedRaSkyOffsetDeg / safeCosDec;

            double targetRaHours = NormalizeHours(raHours + (raCoordinateOffsetDeg / 15.0));
            double targetDecDeg = Math.Clamp(decDeg + appliedDecOffsetDeg, -89.5, 89.5);

            double jd = PrecessionCalculator.DateTimeToJulianDate(utcNow);
            var site = new LatLon(
                Angle.FromDegs(locationSettings.Latitude_Deg),
                Angle.FromDegs(locationSettings.Longitude_Deg));
            var raDec = RaDec.FromHoursDegs(targetRaHours, targetDecDeg);
            var altAz = raDec.ToAltAz(site, jd);

            double targetAltitude = altAz.Alt.Degs;
            double rawTargetAzimuth = altAz.Az.Degs;
            double targetAzimuth = NormalizeTargetDegreesNearCurrent(rawTargetAzimuth, currentAzimuthDeg);
            double azimuthDelta = targetAzimuth - currentAzimuthDeg;
            double altitudeDelta = targetAltitude - currentAltitudeDeg;

            SimpleLogger.Info(
                $"[OFFSET-RADEC] RA={raHours:F4}h Dec={decDeg:F3}° -> Target RA={targetRaHours:F4}h Dec={targetDecDeg:F3}° -> Az={targetAzimuth:F3}° Alt={targetAltitude:F3}°",
                "ManualCtrl");

            return (targetAzimuth, targetAltitude, azimuthDelta, altitudeDelta, targetRaHours, targetDecDeg);
        }

        private void ApplyTrackingOffset(double azimuthDeltaDeg, double altitudeDeltaDeg, string logPrefix, string offsetTypeLabel)
        {
            var mountInfo = App.MountInfo;
            if (mountInfo == null)
            {
                throw new InvalidOperationException("MountInfo is not available for tracking offset.");
            }

            mountInfo.TelescopeOffset_Az_Arcsec += azimuthDeltaDeg * 3600.0;
            mountInfo.TelescopeOffset_Alt_Arcsec += altitudeDeltaDeg * 3600.0;

            SimpleLogger.Info(
                $"{logPrefix} Tracking offset applied ({offsetTypeLabel}): dAz={azimuthDeltaDeg:F4}°, dAlt={altitudeDeltaDeg:F4}° | Total offsets = Az {mountInfo.TelescopeOffset_Az_Arcsec / 3600.0:F4}°, Alt {mountInfo.TelescopeOffset_Alt_Arcsec / 3600.0:F4}°",
                "ManualCtrl");
        }

        private static double NormalizeHours(double hours)
        {
            double normalized = hours % 24.0;
            if (normalized < 0)
            {
                normalized += 24.0;
            }

            return normalized;
        }

        private static double NormalizeDegrees(double degrees)
        {
            double normalized = degrees % 360.0;
            if (normalized < 0)
            {
                normalized += 360.0;
            }

            return normalized;
        }

        private static double NormalizeSignedDegrees(double degrees)
        {
            double normalized = NormalizeDegrees(degrees);
            if (normalized > 180.0)
            {
                normalized -= 360.0;
            }

            return normalized;
        }

        private static double NormalizeTargetDegreesNearCurrent(double targetDegrees, double currentDegrees)
        {
            double normalizedTarget = NormalizeDegrees(targetDegrees);
            double normalizedCurrent = NormalizeDegrees(currentDegrees);
            double shortestDelta = NormalizeSignedDegrees(normalizedTarget - normalizedCurrent);
            return currentDegrees + shortestDelta;
        }

        private void StopAllMotion()
        {
            if (_azimuthInfo != null)
            {
                _azimuthInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAzimuthCommandVelocity(0);
                SimpleLogger.Info("[STOP] Azimuth Command_Velocity set to 0", "ManualCtrl");
            }

            if (_altitudeInfo != null)
            {
                _altitudeInfo.Command_Velocity_Deg_Sec = 0;
                _pollingManager?.SetAltitudeCommandVelocity(0);
                SimpleLogger.Info("[STOP] Altitude Command_Velocity set to 0", "ManualCtrl");
            }
        }

        #endregion

        #region Goto Position Management

        private void BtnAddPosition_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!double.TryParse(txtNewAzimuth.Text, out double azimuth))
                {
                    SimpleLogger.Warning("[ADD POS] Invalid Azimuth value", "ManualCtrl");
                    MessageBox.Show("Invalid Azimuth value", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!double.TryParse(txtNewAltitude.Text, out double altitude))
                {
                    SimpleLogger.Warning("[ADD POS] Invalid Altitude value", "ManualCtrl");
                    MessageBox.Show("Invalid Altitude value", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!double.TryParse(txtNewVelocity.Text, out double velocity) || velocity <= 0)
                {
                    SimpleLogger.Warning("[ADD POS] Invalid Velocity value", "ManualCtrl");
                    MessageBox.Show("Invalid Velocity value (must be > 0)", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var newPosition = new GotoPosition
                {
                    Id = _nextPositionId++,
                    Azimuth = azimuth,
                    Altitude = altitude,
                    Velocity = velocity
                };

                _gotoPositions.Add(newPosition);
                SimpleLogger.Info($"[ADD POS] Position #{newPosition.Id} added: Az={azimuth:F2}�, Alt={altitude:F2}�, Vel={velocity:F1}�/s", "ManualCtrl");

                // Auto-save to storage
                GotoPositionStorage.SavePositions(_gotoPositions);

                // Clear input fields
                txtNewAzimuth.Text = "0.00";
                txtNewAltitude.Text = "0.00";
                txtNewVelocity.Text = "1.0";
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[ADD POS] Error: {ex.Message}", "ManualCtrl");
            }
        }

        private void DgGotoPositions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool hasSelection = dgGotoPositions.SelectedItem != null;
            btnGotoPosition.IsEnabled = hasSelection;
            btnDeletePosition.IsEnabled = hasSelection;
        }

        private void BtnGotoPosition_Click(object sender, RoutedEventArgs e)
        {
            if (dgGotoPositions.SelectedItem is GotoPosition selectedPosition)
            {
                if (!_isAzimuthConnected)
                {
                    SimpleLogger.Error("[GOTO] Azimuth Drive not connected", "ManualCtrl");
                    MessageBox.Show("Azimuth Drive not connected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    SimpleLogger.Info($"[GOTO] Moving to Position #{selectedPosition.Id}: Az={selectedPosition.Azimuth:F2}�, Alt={selectedPosition.Altitude:F2}�, Vel={selectedPosition.Velocity:F1}�/s", "ManualCtrl");

                    // Stop Position Control first (if running)
                    _azimuthController?.StopPositionControlForManualMode();
                    
                    // [FIX] Check if Azimuth Drive is ENABLED using fresh reference from CentralizedPollingManager
                    // Use AzimuthInfo property (line 27) which returns _pollingManager.AzimuthInfo
                    bool azimuthEnabled = AzimuthInfo?.Is_Drive_Enabled ?? false;
                    
                    SimpleLogger.Info($"[GOTO] Azimuth Drive Enabled check: {azimuthEnabled} (from CentralizedPollingManager)", "ManualCtrl");
                    
                    if (!azimuthEnabled)
                    {
                        SimpleLogger.Error("[GOTO] Azimuth Drive is not ENABLED", "ManualCtrl");
                        MessageBox.Show("Azimuth Drive is not ENABLED.\n\nPlease enable the drive before using Goto Position.", 
                                      "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    
                    // Send Azimuth Move Command using PA+SP+BG
                    if (_azimuthDrive != null && _azimuthDrive.IsConnected)
                    {
                        try
                        {
                            _azimuthDrive.MoveToPosition(selectedPosition.Azimuth, selectedPosition.Velocity);
                            SimpleLogger.Info($"[GOTO] Azimuth command sent - Target: {selectedPosition.Azimuth:F2}�, Speed: {selectedPosition.Velocity:F1}�/s", "ManualCtrl");
                        }
                        catch (Exception azEx)
                        {
                            SimpleLogger.Error($"[GOTO] Azimuth move failed: {azEx.Message}", "ManualCtrl");
                            
                            // Check if it's Error 58 (Drive not enabled)
                            if (azEx.Message.Contains("error 58") || azEx.Message.Contains("Error 58"))
                            {
                                MessageBox.Show("Azimuth Drive is not ENABLED.\n\nPlease enable Azimuth drive before using Goto Position.",
                                              "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                            else
                            {
                                MessageBox.Show($"Azimuth move error:\n{azEx.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                            return;
                        }
                    }
                    
                    // Send Altitude Move Command using PA+SP+BG (if connected AND enabled)
                    if (_isAltitudeConnected && _altitudeDrive != null)
                    {
                        // [FIX] Check if Altitude Drive is ENABLED using fresh reference from CentralizedPollingManager
                        // Use AltitudeInfo property (line 33) which returns _pollingManager.AltitudeInfo
                        bool altitudeEnabled = AltitudeInfo?.Is_Drive_Enabled ?? false;
                        
                        SimpleLogger.Info($"[GOTO] Altitude Drive Enabled check: {altitudeEnabled} (from CentralizedPollingManager)", "ManualCtrl");
                        
                        if (altitudeEnabled)
                        {
                            try
                            {
                                _altitudeDrive.MoveToPosition(selectedPosition.Altitude, selectedPosition.Velocity);
                                SimpleLogger.Info($"[GOTO] Altitude command sent - Target: {selectedPosition.Altitude:F2}�, Speed: {selectedPosition.Velocity:F1}�/s", "ManualCtrl");
                            }
                            catch (Exception altEx)
                            {
                                SimpleLogger.Error($"[GOTO] Altitude move failed: {altEx.Message}", "ManualCtrl");
                                
                                // Check if it's Error 58 (Drive not enabled)
                                if (altEx.Message.Contains("error 58") || altEx.Message.Contains("Error 58"))
                                {
                                    SimpleLogger.Warning("[GOTO] Altitude Drive was not enabled (Error 58) - Azimuth has moved to target", "ManualCtrl");
                                    // [NO MESSAGEBOX] Log warning only - user can see in System Log
                                }
                                else
                                {
                                    SimpleLogger.Error($"[GOTO] Altitude move error: {altEx.Message}", "ManualCtrl");
                                    // [NO MESSAGEBOX] Log error only - Azimuth already moved successfully
                                }
                                // Don't return - Azimuth already moved successfully
                            }
                        }
                        else
                        {
                            SimpleLogger.Warning($"[GOTO] Altitude Drive not ENABLED - skipping altitude move", "ManualCtrl");
                            // [NO MESSAGEBOX] Log warning only - Azimuth has moved to target successfully
                        }
                    }
                    else
                    {
                        SimpleLogger.Warning($"[GOTO] Altitude Drive not connected - skipping altitude move", "ManualCtrl");
                    }
                    
                    SimpleLogger.Info($"[GOTO] Position commands sent successfully - Drive will handle motion", "ManualCtrl");
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[GOTO] Error: {ex.Message}", "ManualCtrl");
                    MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnDeletePosition_Click(object sender, RoutedEventArgs e)
        {
            if (dgGotoPositions.SelectedItem is GotoPosition selectedPosition)
            {
                var result = MessageBox.Show($"Delete Position #{selectedPosition.Id}?", "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _gotoPositions.Remove(selectedPosition);
                    SimpleLogger.Info($"[DELETE] Position #{selectedPosition.Id} deleted", "ManualCtrl");
                    
                    // Auto-save to storage
                    GotoPositionStorage.SavePositions(_gotoPositions);
                }
            }
        }

        #endregion
        
        #region Sidereal Tracking Toggle

        private void SetSiderealTrackingToggleState(bool isTracking)
        {
            _isSiderealTrackingActive = isTracking;

            if (txtSiderealTrackingStatus != null)
            {
                txtSiderealTrackingStatus.Text = isTracking ? "Tracking Off" : "Tracking On";
            }

            if (btnSiderealTracking != null)
            {
                btnSiderealTracking.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    isTracking ? "#10B981" : "#8B5CF6"));
                btnSiderealTracking.Tag = isTracking ? "Tracking" : null;
            }
        }

        private void StopSiderealTrackingInternal()
        {
            try
            {
                _siderealTracking?.StopTracking();

                if (_pollingManager != null)
                {
                    _pollingManager.SetAzimuthCommandVelocity(0);
                    _pollingManager.SetAltitudeCommandVelocity(0);
                }

                SetSiderealTrackingToggleState(false);
                SimpleLogger.Info("[SIDEREAL] Tracking STOPPED", "ManualCtrl");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SIDEREAL] Error stopping tracking internally: {ex.Message}", "ManualCtrl");
            }
        }
        
        /// <summary>
        /// Toggle Sidereal Tracking at current mount position
        /// - Convert current Alt/Az to RA/Dec
        /// - Start/Stop sidereal tracking
        /// </summary>
        private void BtnSiderealTracking_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_pollingManager == null)
                {
                    MessageBox.Show("Mount not connected!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Initialize SiderealTracking if not yet created
                if (_siderealTracking == null)
                {
                    _siderealTracking = new SiderealTracking(_pollingManager);
                    SimpleLogger.Info("[SIDEREAL] Created new SiderealTracking engine", "ManualCtrl");
                }
                
                if (!_isSiderealTrackingActive)
                {
                    // === START TRACKING ===

                    if (_pollingManager.MountInfo == null)
                    {
                        MessageBox.Show("Cannot read mount position!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    double currentAz = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                    double currentAlt = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;

                    if (double.IsNaN(currentAz) || double.IsNaN(currentAlt))
                    {
                        MessageBox.Show("Current corrected mount position is invalid!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    
                    // Get observer location from settings
                    var locationSettings = AppSettings.GetObserverLocation();
                    var observerLocation = new ObserverLocation(
                        locationSettings.Latitude_Deg,
                        locationSettings.Longitude_Deg,
                        locationSettings.Altitude_Meters
                    );
                    
                    // Convert Alt/Az to RA/Dec
                    DateTime utcNow = DateTime.UtcNow;
                    var (raHours, decDeg) = MountCalibrationHelper.ConvertAltAzToRaDec(
                        currentAlt, currentAz, 
                        observerLocation.Latitude, observerLocation.Longitude, 
                        utcNow);

                    var target = new TrackingTarget
                    {
                        ObjectType = TrackingObjectType.BrightStar,
                        ObjectName = "Manual Current Position",
                        RightAscension = raHours,
                        Declination = decDeg,
                        Azimuth = currentAz,
                        Altitude = currentAlt
                    };

                    bool granted = TrackingManager.Instance.RequestStartTracking(
                        TrackingObjectType.BrightStar,
                        target,
                        () => Dispatcher.Invoke(() => StopSiderealTrackingInternal())
                    );

                    if (!granted)
                    {
                        SimpleLogger.Warning("[SIDEREAL] Tracking request denied by TrackingManager", "ManualCtrl");
                        return;
                    }
                    
                    // Set target and start tracking
                    _siderealTracking.SetTarget(raHours, decDeg, observerLocation);
                    _siderealTracking.StartTracking();
                    
                    SetSiderealTrackingToggleState(true);
                    
                    SimpleLogger.Info($"[SIDEREAL] Tracking STARTED at Az={currentAz:F2}�, Alt={currentAlt:F2}� ? RA={raHours:F4}h, Dec={decDeg:F2}�", "ManualCtrl");
                }
                else
                {
                    // === STOP TRACKING ===
                    TrackingManager.Instance.StopTracking(TrackingObjectType.BrightStar);
                    StopSiderealTrackingInternal();
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SIDEREAL] Error toggling tracking: {ex.Message}", "ManualCtrl");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        #endregion

        #region M3 Control (Axis 4)

        // [M3] Saved port positions and step size (deg)
        private double _m3Port1Deg = 0.0;
        private double _m3Port2Deg = 90.0;
        private double _m3StepDeg  = 0.01;

        // [M3] Motor enable state — synced from actual AX4.MO read
        private bool _isM3MotorEnabled = false;

        // Cache last-read M3 values so the DispatcherTimer can update UI without blocking
        private double _m3LastPositionDeg = double.NaN;
        private double _m3LastVelocityDeg = double.NaN;

        /// <summary>
        /// Called by the 100 ms DispatcherTimer - reads Axis 4 position/velocity in the background
        /// and updates the M3 tab UI labels.
        /// </summary>
        private void UpdateM3Display()
        {
            // Update labels from cached values (always safe on UI thread)
            if (!double.IsNaN(_m3LastPositionDeg))
                txtM3CurrentPosition.Text = _m3LastPositionDeg.ToString("F3");
            if (!double.IsNaN(_m3LastVelocityDeg))
                txtM3CurrentVelocity.Text = _m3LastVelocityDeg.ToString("F4");

            var m3Info = _pollingManager?.M3Info;
            if (m3Info != null && m3Info.Timestamp != DateTime.MinValue)
            {
                _m3LastPositionDeg = m3Info.Position_Deg;
                _m3LastVelocityDeg = m3Info.Velocity_Deg_Sec;
                bool enabled = m3Info.Is_Motor_Enabled;
                if (enabled != _isM3MotorEnabled)
                {
                    _isM3MotorEnabled = enabled;
                    UpdateM3EnableButton(_isM3MotorEnabled);
                }
            }
        }

        /// <summary>
        /// Update Enable/Disable button appearance to reflect actual M3 motor state
        /// </summary>
        private void UpdateM3EnableButton(bool isEnabled)
        {
            if (btnM3Enable == null) return;

            if (isEnabled)
            {
                btnM3Enable.Content    = "Disable";
                btnM3Enable.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
            else
            {
                btnM3Enable.Content    = "Enable";
                btnM3Enable.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
        }

        /// <summary>
        /// Goto target position on Axis 4 using PA + SP + BG commands
        /// </summary>
        private void BtnM3Goto_Click(object sender, RoutedEventArgs e)
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected)
            {
                MessageBox.Show("Azimuth drive not connected.\nAxis 4 shares the Azimuth drive.",
                                "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtM3TargetPosition.Text, out double targetDeg))
            {
                MessageBox.Show("Invalid target position value.", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtM3Velocity.Text, out double velocityDeg) || velocityDeg <= 0)
            {
                MessageBox.Show("Invalid velocity value (must be > 0).", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            txtM3Status.Text = $"Moving to {targetDeg:F3}° at {velocityDeg:F2} °/s …";
            txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    long targetCounts   = (long)(targetDeg   / 360.0 * _m3EncoderResolution);
                    long velocityCounts = (long)(velocityDeg / 360.0 * _m3EncoderResolution);
                    velocityCounts = Math.Abs(velocityCounts);

                    string resp;
                    bool ok = true;

                    ok &= drive.SendRawCommand($"AX{_m3AxisNumber}.SP={velocityCounts}", out resp, 1000);
                    ok &= drive.SendRawCommand($"AX{_m3AxisNumber}.PA={targetCounts}",   out resp, 1000);
                    ok &= drive.SendRawCommand($"AX{_m3AxisNumber}.BG",                  out resp, 1000);

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ok)
                        {
                            txtM3Status.Text = $"Goto sent → {targetDeg:F3}°";
                            txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                            SimpleLogger.Info($"[M3] Goto {targetDeg:F3}° at {velocityDeg:F2}°/s (AX{_m3AxisNumber})", "M3");
                        }
                        else
                        {
                            txtM3Status.Text = "Command failed — check drive";
                            txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                            SimpleLogger.Error("[M3] Goto command failed", "M3");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtM3Status.Text = $"Error: {ex.Message}";
                        txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                    });
                    SimpleLogger.Error($"[M3] Goto error: {ex.Message}", "M3");
                }
            });
        }

        /// <summary>
        /// Stop Axis 4 motion immediately
        /// </summary>
        private void BtnM3Stop_Click(object sender, RoutedEventArgs e)
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string resp;
                    drive.SendRawCommand($"AX{_m3AxisNumber}.ST", out resp, 500);
                    SimpleLogger.Info($"[M3] Stop command sent (AX{_m3AxisNumber})", "M3");

                    Dispatcher.InvokeAsync(() =>
                    {
                        txtM3Status.Text = "Stopped";
                        txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[M3] Stop error: {ex.Message}", "M3");
                }
            });
        }

        /// <summary>
        /// Toggle M3 motor Enable/Disable based on actual axis state
        /// </summary>
        private void BtnM3Enable_Click(object sender, RoutedEventArgs e)
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected) return;

            bool targetState = !_isM3MotorEnabled;   // toggle
            string cmd = targetState ? $"AX{_m3AxisNumber}.MO=1" : $"AX{_m3AxisNumber}.MO=0";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string resp;
                    bool ok = drive.SendRawCommand(cmd, out resp, 1000);
                    SimpleLogger.Info($"[M3] {(targetState ? "Enable" : "Disable")} motor ({cmd}) → {(ok ? "OK" : "FAIL")}", "M3");

                    if (ok)
                    {
                        _isM3MotorEnabled = targetState;
                        Dispatcher.InvokeAsync(() =>
                        {
                            UpdateM3EnableButton(_isM3MotorEnabled);
                            txtM3Status.Text = _isM3MotorEnabled ? "Motor enabled" : "Motor disabled";
                            txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                                _isM3MotorEnabled ? "#10B981" : "#F59E0B"));
                        });
                    }
                    else
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            txtM3Status.Text = "Command failed — check drive";
                            txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                        });
                    }
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[M3] Toggle motor error: {ex.Message}", "M3");
                }
            });
        }

        /// <summary>
        /// Save Port 1 and Port 2 positions to MongoDB (via AppSettingsModel)
        /// </summary>
        private async void BtnM3SavePorts_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3Port1Position.Text, out double p1) ||
                !double.TryParse(txtM3Port2Position.Text, out double p2))
            {
                MessageBox.Show("Invalid position value.", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtM3Step.Text, out double step) || step <= 0)
            {
                MessageBox.Show("Invalid step value (must be > 0).", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtM3Acceleration.Text, out double acc) || acc <= 0)
            {
                MessageBox.Show("Invalid acceleration value (must be > 0).", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtM3Deceleration.Text, out double dcc) || dcc <= 0)
            {
                MessageBox.Show("Invalid deceleration value (must be > 0).", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtM3PortVelocity.Text, out double portVel) || portVel <= 0)
            {
                MessageBox.Show("Invalid port velocity value (must be > 0).", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _m3Port1Deg = p1;
            _m3Port2Deg = p2;
            _m3StepDeg  = step;

            txtM3PortStatus.Text = "Saving…";
            txtM3PortStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));

            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Load current settings, patch all M3 fields, save back
                    var settings = AppSettings.GetAllSettings();
                    settings.M3_Port1_Deg            = _m3Port1Deg;
                    settings.M3_Port2_Deg            = _m3Port2Deg;
                    settings.M3_Step_Deg             = _m3StepDeg;
                    settings.M3_Acceleration_Deg_S2  = acc;
                    settings.M3_Deceleration_Deg_S2  = dcc;
                    settings.M3_Velocity_Deg         = portVel;
                    AppSettings.SaveAllSettings(settings);
                    bool ok = true;

                    SimpleLogger.Info(
                        $"[M3] Saved to AppSettings — Port1={_m3Port1Deg:F3}° Port2={_m3Port2Deg:F3}° Step={_m3StepDeg:F4}° " +
                        $"Acc={acc:F3}°/s² DCC={dcc:F3}°/s² Vel={portVel:F3}°/s → {(ok ? "OK" : "FAIL")}", "M3");

                    Dispatcher.InvokeAsync(() =>
                    {
                        txtM3PortStatus.Text = ok
                            ? $"Saved ✓  Port1: {_m3Port1Deg:F3}°  Port2: {_m3Port2Deg:F3}°  Step: {_m3StepDeg:F4}°  Acc: {acc:F2}°/s²  DCC: {dcc:F2}°/s²  Vel: {portVel:F3}°/s"
                            : "Save failed";
                        txtM3PortStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                            ok ? "#10B981" : "#EF4444"));
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[M3] Save ports error: {ex.Message}", "M3");
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtM3PortStatus.Text = $"Error: {ex.Message}";
                        txtM3PortStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                    });
                }
            });
        }

        /// <summary>
        /// Load all M3 settings from AppSetting.json on window open and populate all textboxes
        /// </summary>
        private async void LoadM3PortPositionsAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var settings = AppSettings.GetAllSettings();
                    if (settings == null) return;

                    _m3Port1Deg = settings.M3_Port1_Deg;
                    _m3Port2Deg = settings.M3_Port2_Deg;
                    _m3StepDeg  = settings.M3_Step_Deg > 0 ? settings.M3_Step_Deg : 0.01;

                    double targetDeg   = settings.M3_TargetPosition_Deg;
                    double velocityDeg = settings.M3_Velocity_Deg > 0 ? settings.M3_Velocity_Deg : 1.0;
                    double accDeg      = settings.M3_Acceleration_Deg_S2 > 0 ? settings.M3_Acceleration_Deg_S2 : 10.0;
                    double dccDeg      = settings.M3_Deceleration_Deg_S2 > 0 ? settings.M3_Deceleration_Deg_S2 : 10.0;

                    SimpleLogger.Info($"[M3] Loaded from AppSettings — Target={targetDeg:F3}° Vel={velocityDeg:F3}°/s Port1={_m3Port1Deg:F3}° Port2={_m3Port2Deg:F3}° Step={_m3StepDeg:F4}° Acc={accDeg:F3}°/s² DCC={dccDeg:F3}°/s²", "M3");

                    Dispatcher.InvokeAsync(() =>
                    {
                        txtM3TargetPosition.Text = targetDeg.ToString("F3");
                        txtM3Velocity.Text       = velocityDeg.ToString("F3");
                        txtM3Port1Position.Text  = _m3Port1Deg.ToString("F3");
                        txtM3Port2Position.Text  = _m3Port2Deg.ToString("F3");
                        txtM3Step.Text           = _m3StepDeg.ToString("F4");
                        txtM3PortVelocity.Text   = velocityDeg.ToString("F3");
                        txtM3Acceleration.Text   = accDeg.ToString("F3");
                        txtM3Deceleration.Text   = dccDeg.ToString("F3");
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"[M3] Load settings error: {ex.Message}", "M3");
                }
            });
        }

        /// <summary>
        /// Save a single M3 field to AppSettings — helper to avoid duplicating boilerplate
        /// </summary>
        private async void SaveM3FieldAsync(System.Action<AppSettingsModel> patch)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var settings = AppSettings.GetAllSettings();
                    patch(settings);
                    AppSettings.SaveAllSettings(settings);
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"[M3] Auto-save field error: {ex.Message}", "M3");
                }
            });
        }

        /// <summary>
        /// Auto-save Velocity when textbox loses focus
        /// </summary>
        private void TxtM3Velocity_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3Velocity.Text, out double vel) || vel <= 0) return;
            SaveM3FieldAsync(s => s.M3_Velocity_Deg = vel);
            SimpleLogger.Info($"[M3] Velocity auto-saved: {vel:F3} °/s", "M3");
        }

        /// <summary>
        /// Auto-save Target Position when textbox loses focus
        /// </summary>
        private void TxtM3TargetPosition_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3TargetPosition.Text, out double pos)) return;
            SaveM3FieldAsync(s => s.M3_TargetPosition_Deg = pos);
            SimpleLogger.Info($"[M3] Target position auto-saved: {pos:F3}°", "M3");
        }

        /// <summary>Auto-save Port Velocity when textbox loses focus</summary>
        private void TxtM3PortVelocity_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3PortVelocity.Text, out double vel) || vel <= 0) return;
            // Sync with left-panel velocity and save
            txtM3Velocity.Text = vel.ToString("F3");
            SaveM3FieldAsync(s => s.M3_Velocity_Deg = vel);
            SimpleLogger.Info($"[M3] Port velocity saved: {vel:F3} °/s", "M3");
        }

        /// <summary>Auto-save Acceleration when textbox loses focus</summary>
        private void TxtM3Acceleration_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3Acceleration.Text, out double acc) || acc <= 0) return;
            SaveM3FieldAsync(s => s.M3_Acceleration_Deg_S2 = acc);
            SimpleLogger.Info($"[M3] Acceleration saved: {acc:F3} °/s²", "M3");
        }

        /// <summary>Auto-save Deceleration when textbox loses focus</summary>
        private void TxtM3Deceleration_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3Deceleration.Text, out double dcc) || dcc <= 0) return;
            SaveM3FieldAsync(s => s.M3_Deceleration_Deg_S2 = dcc);
            SimpleLogger.Info($"[M3] Deceleration saved: {dcc:F3} °/s²", "M3");
        }

        /// <summary>
        /// Apply Acceleration and Deceleration to drive (AX4.ACC / AX4.DCC)
        /// </summary>
        /// <summary>
        /// Trigger LostFocus save when user presses Enter in any M3 textbox
        /// </summary>
        private void TxtM3Field_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                System.Windows.Input.Keyboard.ClearFocus();
        }

        /// <summary>
        /// Move M3 to saved Port 1 position
        /// </summary>
        private void BtnM3GotoPort1_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3Port1Position.Text, out double pos))
            {
                MessageBox.Show("Invalid Port 1 position value.", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _m3Port1Deg = pos; // sync field with textbox
            GotoM3Port(pos, 1);
        }

        /// <summary>
        /// Move M3 to saved Port 2 position
        /// </summary>
        private void BtnM3GotoPort2_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtM3Port2Position.Text, out double pos))
            {
                MessageBox.Show("Invalid Port 2 position value.", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _m3Port2Deg = pos; // sync field with textbox
            GotoM3Port(pos, 2);
        }

        /// <summary>
        /// Port 1 — increment position by step and move immediately
        /// </summary>
        private void BtnM3Port1Plus_Click(object sender, RoutedEventArgs e)
            => StepM3Port(txtM3Port1Position, +1);

        /// <summary>
        /// Port 1 — decrement position by step and move immediately
        /// </summary>
        private void BtnM3Port1Minus_Click(object sender, RoutedEventArgs e)
            => StepM3Port(txtM3Port1Position, -1);

        /// <summary>
        /// Port 2 — increment position by step and move immediately
        /// </summary>
        private void BtnM3Port2Plus_Click(object sender, RoutedEventArgs e)
            => StepM3Port(txtM3Port2Position, +1);

        /// <summary>
        /// Port 2 — decrement position by step and move immediately
        /// </summary>
        private void BtnM3Port2Minus_Click(object sender, RoutedEventArgs e)
            => StepM3Port(txtM3Port2Position, -1);

        /// <summary>
        /// Apply ±step to port textbox and immediately command M3 to move (relative move via PR+BG)
        /// </summary>
        private void StepM3Port(System.Windows.Controls.TextBox portTextBox, int direction)
        {
            // Read and validate step
            if (!double.TryParse(txtM3Step.Text, out double step) || step <= 0)
            {
                txtM3PortStatus.Text = "Invalid step value";
                txtM3PortStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                return;
            }
            _m3StepDeg = step;

            // Update position textbox to new absolute target
            double currentVal = double.TryParse(portTextBox.Text, out double v) ? v : 0.0;
            double newVal = currentVal + direction * step;
            portTextBox.Text = newVal.ToString("F3");

            // Move to new absolute position immediately (PA = absolute, not PR = relative)
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected) return;

            long targetCounts   = (long)(newVal / 360.0 * _m3EncoderResolution);

            if (!double.TryParse(txtM3Velocity.Text, out double velocityDeg) || velocityDeg <= 0)
                velocityDeg = 1.0;
            long velocityCounts = (long)(velocityDeg / 360.0 * _m3EncoderResolution);

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string resp;
                    bool ok = drive.SendRawCommand($"AX{_m3AxisNumber}.MO=1",               out resp, 500);
                    ok     &= drive.SendRawCommand($"AX{_m3AxisNumber}.SP={velocityCounts}", out resp, 500);
                    ok     &= drive.SendRawCommand($"AX{_m3AxisNumber}.PA={targetCounts}",   out resp, 500);
                    ok     &= drive.SendRawCommand($"AX{_m3AxisNumber}.BG",                  out resp, 500);

                    SimpleLogger.Info($"[M3] Step → {newVal:F3}° (PA={targetCounts}) {(ok ? "OK" : "FAIL")}", "M3");

                    Dispatcher.InvokeAsync(() =>
                    {
                        txtM3PortStatus.Text = ok
                            ? $"Moving to {newVal:F3}°"
                            : "Step command failed";
                        txtM3PortStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                            ok ? "#10B981" : "#EF4444"));
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[M3] Step error: {ex.Message}", "M3");
                }
            });
        }

        private void GotoM3Port(double targetDeg, int portNumber)
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected)
            {
                MessageBox.Show("M3 drive not connected.", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Read motion parameters from Motor Parameters panel (fallback to defaults)
            if (!double.TryParse(txtM3PortVelocity.Text, out double velocityDeg) || velocityDeg <= 0)
                velocityDeg = 1.0;
            if (!double.TryParse(txtM3Acceleration.Text, out double accDeg) || accDeg <= 0)
                accDeg = 10.0;
            if (!double.TryParse(txtM3Deceleration.Text, out double dccDeg) || dccDeg <= 0)
                dccDeg = 10.0;

            txtM3Status.Text       = $"Moving to Port {portNumber} ({targetDeg:F2}°) …";
            txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string resp;
                    long targetCounts   = (long)(targetDeg   / 360.0 * _m3EncoderResolution);
                    long velocityCounts = (long)(velocityDeg / 360.0 * _m3EncoderResolution);

                    // Set motion parameters (same pattern as Altitude axis)
                    bool ok = drive.SendRawCommand($"AC[{_m3AxisNumber}]={accDeg:F2}", out resp, 500);
                    ok     &= drive.SendRawCommand($"DC[{_m3AxisNumber}]={dccDeg:F2}", out resp, 500);

                    // Enable motor then execute absolute position move
                    ok &= drive.SendRawCommand($"AX{_m3AxisNumber}.MO=1",                 out resp, 1000);
                    ok &= drive.SendRawCommand($"AX{_m3AxisNumber}.SP={velocityCounts}",  out resp, 1000);
                    ok &= drive.SendRawCommand($"AX{_m3AxisNumber}.PA={targetCounts}",    out resp, 1000);
                    ok &= drive.SendRawCommand($"AX{_m3AxisNumber}.BG",                   out resp, 1000);

                    SimpleLogger.Info(
                        $"[M3] Goto Port{portNumber} {targetDeg:F2}°  " +
                        $"Acc={accDeg:F2}°/s²  DCC={dccDeg:F2}°/s²  Vel={velocityDeg:F2}°/s → {(ok ? "OK" : "FAIL")}", "M3");

                    Dispatcher.InvokeAsync(() =>
                    {
                        txtM3Status.Text = ok
                            ? $"Moving to Port {portNumber} ({targetDeg:F2}°)"
                            : "Command failed — check drive";
                        txtM3Status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                            ok ? "#10B981" : "#EF4444"));
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[M3] GotoPort{portNumber} error: {ex.Message}", "M3");
                }
            });
        }

        #endregion
    }

    /// <summary>
    /// Model for Goto Position data
    /// </summary>
    public class GotoPosition
    {
        public int Id { get; set; }
        public double Azimuth { get; set; }
        public double Altitude { get; set; }
        public double Velocity { get; set; }
    }
}



