using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ForkMount_WPF.Engine;
using ForkMount_WPF.Engine.Utility;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ForkMount_WPF.UI
{
    public partial class LocationTimeControl : UserControl
    {
        private WebView2 _webView;
        private bool _isMapReady = false;
        private bool _isUpdatingFromMap = false;
        private static readonly HttpClient _httpClient = new HttpClient();
        private DispatcherTimer _timeUpdateTimer;

        public LocationTimeControl()
        {
            InitializeComponent();
            
            // Set User-Agent for Nominatim API (required)
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "ForkMount-WPF/1.0 (Telescope Control System)");
            }
            
            // Initialize time update timer (1 second interval)
            _timeUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timeUpdateTimer.Tick += TimeUpdateTimer_Tick;
            _timeUpdateTimer.Start();
            
            Loaded += LocationTimeControl_Loaded;
            Unloaded += LocationTimeControl_Unloaded;
        }
        
        private void LocationTimeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Stop timer when control is unloaded
            _timeUpdateTimer?.Stop();
        }
        
        private void TimeUpdateTimer_Tick(object sender, EventArgs e)
        {
            UpdateTimeDisplay();
        }
        
        private void UpdateTimeDisplay()
        {
            try
            {
                // Get current UTC time from NTP-synced service
                DateTime utcNow = NtpTimeService.Instance.GetCurrentUtcTime();
                
                // Get timezone offset from ComboBox
                double utcOffset = GetTimezoneOffset();
                
                // Calculate local time based on selected timezone
                DateTime localTime = NtpTimeService.Instance.GetTimeAtOffset(utcOffset);
                
                // Show sync status
                bool isSynced = NtpTimeService.Instance.IsSynchronized;
                string syncIndicator = isSynced ? "?" : "?";
                string syncStatus = isSynced ? $"NTP Synced (offset: {NtpTimeService.Instance.NtpOffset.TotalMilliseconds:F0}ms)" : "Not synced (using system time)";
                
                // Update UI
                txtUtcTime.Text = $"{utcNow:yyyy-MM-dd HH:mm:ss} {syncIndicator}";
                txtUtcTime.ToolTip = syncStatus;
                txtLocalTime.Text = localTime.ToString("yyyy-MM-dd HH:mm:ss");
                
                // Update label to show current timezone
                string sign = utcOffset >= 0 ? "+" : "";
                txtLocalTimeLabel.Text = $"Local Time (UTC{sign}{utcOffset:F0}):";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationTimeControl] Error updating time: {ex.Message}");
            }
        }
        
        private void CmbTimezone_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Update time display immediately when timezone changes
            UpdateTimeDisplay();
        }

        private async void LocationTimeControl_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            await InitializeMapAsync();
        }

        public void LoadSettings()
        {
            try
            {
                // Initialize Timezone ComboBox
                InitializeTimezoneComboBox();
                
                // Load Observer Location
                var location = AppSettings.GetObserverLocation();
                txtLocationName.Text = location.LocationName ?? "Chiang Mai, Thailand";
                txtLatitude.Text = location.Latitude_Deg.ToString("F6");
                txtLongitude.Text = location.Longitude_Deg.ToString("F6");
                txtElevation.Text = location.Altitude_Meters.ToString("F0");
                
                // Set Timezone ComboBox
                SetTimezoneComboBox(location.UtcOffset_Hours);

                // Load System Settings (NTP)
                var systemSettings = AppSettings.GetSystemSettings();
                chkEnableNtp.IsChecked = systemSettings.EnableNtpSync;
                txtNtpServer.Text = systemSettings.NtpServerAddress;
                txtSyncInterval.Text = systemSettings.NtpSyncIntervalMinutes.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationTimeControl] Error loading settings: {ex.Message}");
            }
        }
        
        private void InitializeTimezoneComboBox()
        {
            cmbTimezone.Items.Clear();
            
            // Add timezone options from UTC-12 to UTC+14
            for (int i = -12; i <= 14; i++)
            {
                string sign = i >= 0 ? "+" : "";
                string displayText = $"UTC{sign}{i}";
                
                // Add common timezone names
                string tzName = i switch
                {
                    -12 => " (Baker Island)",
                    -11 => " (Samoa)",
                    -10 => " (Hawaii)",
                    -9 => " (Alaska)",
                    -8 => " (Pacific Time)",
                    -7 => " (Mountain Time)",
                    -6 => " (Central Time)",
                    -5 => " (Eastern Time)",
                    -4 => " (Atlantic Time)",
                    -3 => " (Brazil)",
                    0 => " (GMT/London)",
                    1 => " (Central Europe)",
                    2 => " (Eastern Europe)",
                    3 => " (Moscow)",
                    4 => " (Dubai)",
                    5 => " (Pakistan/India)",
                    6 => " (Bangladesh)",
                    7 => " (Thailand/Vietnam)",
                    8 => " (China/Singapore)",
                    9 => " (Japan/Korea)",
                    10 => " (Sydney)",
                    12 => " (New Zealand)",
                    _ => ""
                };
                
                cmbTimezone.Items.Add($"{displayText}{tzName}");
            }
        }
        
        private void SetTimezoneComboBox(double utcOffset)
        {
            int offsetInt = (int)Math.Round(utcOffset);
            int index = offsetInt + 12;  // UTC-12 is index 0
            
            if (index >= 0 && index < cmbTimezone.Items.Count)
            {
                cmbTimezone.SelectedIndex = index;
            }
            else
            {
                cmbTimezone.SelectedIndex = 19;  // UTC+7 (Thailand) as default
            }
        }
        
        private double GetTimezoneOffset()
        {
            if (cmbTimezone.SelectedIndex < 0)
                return 7.0;  // Default to Thailand
            
            return cmbTimezone.SelectedIndex - 12;  // Convert index back to UTC offset
        }

        public bool SaveSettings()
        {
            try
            {
                // Validate inputs
                if (!ValidateInputs())
                    return false;

                // Save Observer Location
                var location = new ObserverLocationSettings
                {
                    LocationName = txtLocationName.Text.Trim(),
                    Latitude_Deg = double.Parse(txtLatitude.Text),
                    Longitude_Deg = double.Parse(txtLongitude.Text),
                    Altitude_Meters = double.Parse(txtElevation.Text),
                    UtcOffset_Hours = GetTimezoneOffset()
                };
                AppSettings.SaveObserverLocation(location);

                // Save System Settings
                var systemSettings = new SystemSettings
                {
                    EnableNtpSync = chkEnableNtp.IsChecked ?? true,
                    NtpServerAddress = txtNtpServer.Text.Trim(),
                    NtpSyncIntervalMinutes = int.Parse(txtSyncInterval.Text)
                };
                AppSettings.SaveSystemSettings(systemSettings);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationTimeControl] Error saving settings: {ex.Message}");
                return false;
            }
        }

        private bool ValidateInputs()
        {
            // Validate Latitude
            if (!double.TryParse(txtLatitude.Text, out double lat) || lat < -90 || lat > 90)
            {
                MessageBox.Show("Invalid Latitude. Must be between -90 and +90 degrees.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtLatitude.Focus();
                return false;
            }

            // Validate Longitude
            if (!double.TryParse(txtLongitude.Text, out double lon) || lon < -180 || lon > 180)
            {
                MessageBox.Show("Invalid Longitude. Must be between -180 and +180 degrees.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtLongitude.Focus();
                return false;
            }

            // Validate Elevation
            if (!double.TryParse(txtElevation.Text, out double elev) || elev < -500 || elev > 9000)
            {
                MessageBox.Show("Invalid Elevation. Must be between -500 and 9000 meters.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtElevation.Focus();
                return false;
            }

            // Validate NTP Server
            if (chkEnableNtp.IsChecked == true && string.IsNullOrWhiteSpace(txtNtpServer.Text))
            {
                MessageBox.Show("NTP Server address cannot be empty when NTP is enabled.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtNtpServer.Focus();
                return false;
            }

            // Validate Sync Interval
            if (!int.TryParse(txtSyncInterval.Text, out int interval) || interval < 1 || interval > 1440)
            {
                MessageBox.Show("Invalid Sync Interval. Must be between 1 and 1440 minutes.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtSyncInterval.Focus();
                return false;
            }

            return true;
        }

        #region Map Implementation

        private async Task InitializeMapAsync()
        {
            try
            {
                // Create WebView2 control
                _webView = new WebView2();
                mapContainer.Child = _webView;

                // Initialize WebView2
                await _webView.EnsureCoreWebView2Async(null);

                // Handle messages from JavaScript
                _webView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;

                // Load the map HTML
                _webView.NavigateToString(GetMapHtml());

                // Wait for map to load
                await Task.Delay(1000);
                _isMapReady = true;

                // Update map with current location
                UpdateMapMarker();

                // Hide loading overlay
                mapLoadingOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationTimeControl] Error loading map: {ex.Message}");
            }
        }

        private void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.TryGetWebMessageAsString();
                var parts = message.Split(',');

                if (parts.Length == 2)
                {
                    _isUpdatingFromMap = true;

                    double lat = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    double lng = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

                    txtLatitude.Text = lat.ToString("F6");
                    txtLongitude.Text = lng.ToString("F6");

                    _isUpdatingFromMap = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationTimeControl] Error processing map click: {ex.Message}");
            }
        }

        private void LocationTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isMapReady || _isUpdatingFromMap)
                return;

            UpdateMapMarker();
        }

        private void UpdateMapMarker()
        {
            if (!_isMapReady || _webView == null)
                return;

            try
            {
                if (double.TryParse(txtLatitude.Text, out double lat) && 
                    double.TryParse(txtLongitude.Text, out double lng))
                {
                    var script = $"updateMarker({lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {lng.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
                    _webView.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationTimeControl] Error updating map: {ex.Message}");
            }
        }

        private string GetMapHtml()
        {
            // Get current location from settings or use defaults
            var location = AppSettings.GetObserverLocation();
            double lat = location.Latitude_Deg;
            double lng = location.Longitude_Deg;
            
            string latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string lngStr = lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
            
            return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Location Map</title>
    <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
    <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
    <style>
        body {{ margin: 0; padding: 0; }}
        #map {{ height: 100vh; width: 100%; background: #1F2937; }}
    </style>
</head>
<body>
    <div id='map'></div>
    <script>
        var map = L.map('map').setView([{latStr}, {lngStr}], 13);
        L.tileLayer('https://{{s}}.basemaps.cartocdn.com/rastertiles/voyager/{{z}}/{{x}}/{{y}}{{r}}.png', {{
            attribution: '\u00a9 OpenStreetMap contributors \u00a9 CARTO',
            subdomains: 'abcd',
            maxZoom: 20
        }}).addTo(map);
        var marker = L.marker([{latStr}, {lngStr}], {{
            draggable: true
        }}).addTo(map);
        marker.on('dragend', function(e) {{
            var position = marker.getLatLng();
            window.chrome.webview.postMessage(position.lat + ',' + position.lng);
        }});
        map.on('click', function(e) {{
            marker.setLatLng(e.latlng);
            window.chrome.webview.postMessage(e.latlng.lat + ',' + e.latlng.lng);
        }});
        function updateMarker(lat, lng) {{
            var newLatLng = L.latLng(lat, lng);
            marker.setLatLng(newLatLng);
            map.setView(newLatLng, map.getZoom());
        }}
    </script>
</body>
</html>";
        }

        #endregion

        #region Search Location

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            await SearchLocation();
        }

        private async void TxtSearchLocation_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await SearchLocation();
            }
        }

        private async Task SearchLocation()
        {
            var searchText = txtSearchLocation.Text.Trim();
            if (string.IsNullOrEmpty(searchText))
            {
                return;
            }

            try
            {
                btnSearch.IsEnabled = false;

                // Use Nominatim API (OpenStreetMap's geocoding service)
                var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(searchText)}&format=json&limit=1";
                var response = await _httpClient.GetStringAsync(url);

                var results = JsonDocument.Parse(response).RootElement;
                
                if (results.GetArrayLength() > 0)
                {
                    var firstResult = results[0];
                    var lat = firstResult.GetProperty("lat").GetString();
                    var lon = firstResult.GetProperty("lon").GetString();
                    var displayName = firstResult.GetProperty("display_name").GetString();

                    // Update UI
                    _isUpdatingFromMap = true;
                    txtLatitude.Text = double.Parse(lat).ToString("F6");
                    txtLongitude.Text = double.Parse(lon).ToString("F6");
                    txtLocationName.Text = displayName;
                    _isUpdatingFromMap = false;

                    // Update map
                    UpdateMapMarker();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationTimeControl] Search error: {ex.Message}");
            }
            finally
            {
                btnSearch.IsEnabled = true;
            }
        }

        #endregion
    }
}
