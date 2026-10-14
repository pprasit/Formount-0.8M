using System;
using System.IO;
using System.Text.Json;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Class for managing application settings stored in a local JSON file.
    /// For Elmo Platinum Drive Series.
    /// </summary>
    public static class AppSettings
    {
        private const string LegacySettingsFileName = "AppSetting.json";
        private const string ForkMountSettingsFileName = "AppSetting.Fork-Mount.json";
        private const string LMountSettingsFileName = "AppSetting.L-Mount.json";
        private const string LMountDriveSerialNumber = "25382282";
        private const string SharedSettingsVendorDirectory = "NARIT";
        private const string SharedSettingsProductDirectory = "MountController";

        private static readonly string SharedSettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SharedSettingsVendorDirectory,
            SharedSettingsProductDirectory
        );
        private static readonly string LegacySettingsFilePath = Path.Combine(
            SharedSettingsDirectory,
            LegacySettingsFileName
        );
        private static readonly string ForkMountSettingsFilePath = Path.Combine(
            SharedSettingsDirectory,
            ForkMountSettingsFileName
        );
        private static readonly string LMountSettingsFilePath = Path.Combine(
            SharedSettingsDirectory,
            LMountSettingsFileName
        );
        private static readonly string LegacySettingsFilePathInBaseDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            LegacySettingsFileName
        );
        private static readonly string ForkMountSettingsFilePathInBaseDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            ForkMountSettingsFileName
        );
        private static readonly string LMountSettingsFilePathInBaseDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            LMountSettingsFileName
        );

        private static AppSettingsModel _currentSettings;
        private static string _activeSettingsFilePath;
        private static readonly object _lockObject = new object();

        private sealed class SettingsBootstrap
        {
            public string Drive_SN { get; set; }
        }
        
        /// <summary>
        /// MongoDB-backed settings storage has been retired.
        /// This property is kept for compatibility and always returns false.
        /// </summary>
        public static bool IsUsingMongoDB => false;

        private static string NormalizeSerialNumber(string serialNumber)
        {
            return string.IsNullOrWhiteSpace(serialNumber) ? string.Empty : serialNumber.Trim();
        }

        private static bool IsLMountDriveSerialNumber(string serialNumber)
        {
            return string.Equals(
                NormalizeSerialNumber(serialNumber),
                LMountDriveSerialNumber,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureSharedSettingsDirectoryExists()
        {
            if (!Directory.Exists(SharedSettingsDirectory))
                Directory.CreateDirectory(SharedSettingsDirectory);
        }

        private static string GetLegacySettingsFilePathForSharedPath(string sharedSettingsFilePath)
        {
            string fileName = Path.GetFileName(sharedSettingsFilePath);
            return fileName switch
            {
                LegacySettingsFileName => LegacySettingsFilePathInBaseDirectory,
                ForkMountSettingsFileName => ForkMountSettingsFilePathInBaseDirectory,
                LMountSettingsFileName => LMountSettingsFilePathInBaseDirectory,
                _ => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName)
            };
        }

        private static AppSettingsModel TryLoadSettingsSnapshot(string settingsFilePath)
        {
            try
            {
                if (!File.Exists(settingsFilePath))
                    return null;

                string json = File.ReadAllText(settingsFilePath);
                return JsonSerializer.Deserialize<AppSettingsModel>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Settings snapshot probe skipped for {settingsFilePath}: {ex.Message}");
                return null;
            }
        }

        private static AppSettingsModel TryMigrateExistingSettings(string targetSettingsFilePath)
        {
            string legacyProfilePath = GetLegacySettingsFilePathForSharedPath(targetSettingsFilePath);
            string[] candidatePaths =
            {
                legacyProfilePath,
                LegacySettingsFilePathInBaseDirectory,
                LegacySettingsFilePath
            };

            foreach (string candidatePath in candidatePaths)
            {
                if (string.IsNullOrWhiteSpace(candidatePath))
                    continue;

                var migratedSettings = TryLoadSettingsSnapshot(candidatePath);
                if (migratedSettings != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] Migrating settings from {candidatePath} to {targetSettingsFilePath}");
                    return migratedSettings;
                }
            }

            return null;
        }

        private static string GetSettingsFilePathForSerialNumber(string serialNumber)
        {
            return IsLMountDriveSerialNumber(serialNumber)
                ? LMountSettingsFilePath
                : ForkMountSettingsFilePath;
        }

        private static string TryReadDriveSerialNumberFromFile(string settingsFilePath)
        {
            try
            {
                if (!File.Exists(settingsFilePath))
                    return string.Empty;

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsFilePath));
                if (document.RootElement.TryGetProperty("Drive_SN", out JsonElement driveSnElement))
                    return NormalizeSerialNumber(driveSnElement.GetString());

                if (document.RootElement.TryGetProperty("Azimuth_SN", out JsonElement azimuthSnElement))
                    return NormalizeSerialNumber(azimuthSnElement.GetString());
            }
            catch
            {
                // Ignore bootstrap/profile probe failures and fall back to default profile selection.
            }

            return string.Empty;
        }

        private static string ResolveSettingsFilePath(string serialNumber = null)
        {
            string normalizedSerialNumber = NormalizeSerialNumber(serialNumber);
            if (!string.IsNullOrEmpty(normalizedSerialNumber))
                return GetSettingsFilePathForSerialNumber(normalizedSerialNumber);

            if (!string.IsNullOrEmpty(_activeSettingsFilePath))
                return _activeSettingsFilePath;

            string bootstrapSerialNumber = TryReadDriveSerialNumberFromFile(LegacySettingsFilePath);
            if (!string.IsNullOrEmpty(bootstrapSerialNumber))
                return GetSettingsFilePathForSerialNumber(bootstrapSerialNumber);

            string legacyBootstrapSerialNumber = TryReadDriveSerialNumberFromFile(LegacySettingsFilePathInBaseDirectory);
            if (!string.IsNullOrEmpty(legacyBootstrapSerialNumber))
                return GetSettingsFilePathForSerialNumber(legacyBootstrapSerialNumber);

            string forkSerialNumber = TryReadDriveSerialNumberFromFile(ForkMountSettingsFilePath);
            if (!string.IsNullOrEmpty(forkSerialNumber))
                return GetSettingsFilePathForSerialNumber(forkSerialNumber);

            string legacyForkSerialNumber = TryReadDriveSerialNumberFromFile(ForkMountSettingsFilePathInBaseDirectory);
            if (!string.IsNullOrEmpty(legacyForkSerialNumber))
                return GetSettingsFilePathForSerialNumber(legacyForkSerialNumber);

            string lMountSerialNumber = TryReadDriveSerialNumberFromFile(LMountSettingsFilePath);
            if (!string.IsNullOrEmpty(lMountSerialNumber))
                return GetSettingsFilePathForSerialNumber(lMountSerialNumber);

            string legacyLMountSerialNumber = TryReadDriveSerialNumberFromFile(LMountSettingsFilePathInBaseDirectory);
            if (!string.IsNullOrEmpty(legacyLMountSerialNumber))
                return GetSettingsFilePathForSerialNumber(legacyLMountSerialNumber);

            return ForkMountSettingsFilePath;
        }

        private static string GetActiveSettingsFilePath()
        {
            if (string.IsNullOrEmpty(_activeSettingsFilePath))
                _activeSettingsFilePath = ResolveSettingsFilePath();

            return _activeSettingsFilePath;
        }

        private static void SaveBootstrapDriveSerialNumber(string serialNumber)
        {
            try
            {
                EnsureSharedSettingsDirectoryExists();

                var bootstrap = new SettingsBootstrap
                {
                    Drive_SN = NormalizeSerialNumber(serialNumber)
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = null
                };

                string json = JsonSerializer.Serialize(bootstrap, options);
                File.WriteAllText(LegacySettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Failed to save bootstrap serial number: {ex.Message}");
            }
        }

        private static AppSettingsModel CreateInitialSettingsForProfile(string activeSettingsFilePath)
        {
            try
            {
                var migratedSettings = TryMigrateExistingSettings(activeSettingsFilePath);
                if (migratedSettings != null)
                    return migratedSettings;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Legacy settings migration skipped: {ex.Message}");
            }

            return new AppSettingsModel();
        }

        /// <summary>
        /// Load settings from the selected local JSON file.
        /// </summary>
        private static AppSettingsModel LoadSettings(string settingsFilePath = null)
        {
            lock (_lockObject)
            {
                try
                {
                    EnsureSharedSettingsDirectoryExists();

                    string activeSettingsFilePath = settingsFilePath ?? GetActiveSettingsFilePath();
                    _activeSettingsFilePath = activeSettingsFilePath;
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] ========== LOAD SETTINGS ==========");
                    
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] Loading from JSON file: {activeSettingsFilePath}");
                    
                    if (File.Exists(activeSettingsFilePath))
                    {
                        string json = File.ReadAllText(activeSettingsFilePath);
                        var settings = JsonSerializer.Deserialize<AppSettingsModel>(json);
                        
                        if (settings == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Deserialization returned NULL - using defaults");
                            return new AppSettingsModel();
                        }
                        
                        // Only log if settings look suspicious (reduced spam)
                        bool looksValid = !IsDefaultSettings(settings);
                        if (!looksValid)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Settings loaded but look like DEFAULTS!");
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[AppSettings] ============================================");
                        return settings;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[AppSettings] ? File NOT FOUND - creating default settings");
                        
                        // Create profile settings from legacy file if available, otherwise defaults.
                        var defaultSettings = CreateInitialSettingsForProfile(activeSettingsFilePath);
                        SaveSettings(defaultSettings, activeSettingsFilePath);
                        
                        System.Diagnostics.Debug.WriteLine($"[AppSettings] ============================================");
                        return defaultSettings;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Load error: {ex.Message}");
                    throw new Exception($"Failed to load settings: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Save settings to the local JSON file.
        /// </summary>
        private static void SaveSettings(AppSettingsModel settings, string settingsFilePath = null)
        {
            lock (_lockObject)
            {
                try
                {
                    EnsureSharedSettingsDirectoryExists();

                    string activeSettingsFilePath = settingsFilePath ?? GetActiveSettingsFilePath();
                    _activeSettingsFilePath = activeSettingsFilePath;

                    // [DEBUG] Log who is calling SaveSettings
                    var stackTrace = new System.Diagnostics.StackTrace(true);
                    var callingMethod = stackTrace.GetFrame(2)?.GetMethod();
                    var callingClass = callingMethod?.DeclaringType?.Name ?? "Unknown";
                    var callingMethodName = callingMethod?.Name ?? "Unknown";
                    
                    // Only log saves when suspicious (reduce spam)
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Save: {callingClass}.{callingMethodName}()");
                    
                    // [DEBUG] Check if this is a reset (all settings = default)
                    bool looksLikeReset = IsDefaultSettings(settings);
                    if (looksLikeReset)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AppSettings] ??? WARNING: Saving DEFAULT settings - this will ERASE all user settings!");
                        System.Diagnostics.Debug.WriteLine($"[AppSettings] Stack trace:");
                        for (int i = 0; i < Math.Min(5, stackTrace.FrameCount); i++)
                        {
                            var frame = stackTrace.GetFrame(i);
                            var method = frame?.GetMethod();
                            System.Diagnostics.Debug.WriteLine($"[AppSettings]   {i}: {method?.DeclaringType?.Name}.{method?.Name}()");
                        }
                    }
                    
                    settings.LastModified = DateTime.Now;
                    
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true, // Format JSON for readability
                        PropertyNamingPolicy = null // Use property names as-is
                    };

                    string json = JsonSerializer.Serialize(settings, options);
                    File.WriteAllText(activeSettingsFilePath, json);
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Saved to JSON");

                    // Update cache
                    _currentSettings = settings;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Save error: {ex.Message}");
                    throw new Exception($"Failed to save settings: {ex.Message}", ex);
                }
            }
        }
        
        /// <summary>
        /// Check if settings appear to be default/empty (helper for debugging)
        /// </summary>
        private static bool IsDefaultSettings(AppSettingsModel settings)
        {
            // Simple heuristic: if all major sections are null or default, it's likely a reset
            int nullCount = 0;
            if (settings.Azimuth_AxisConfig == null) nullCount++;
            if (settings.Altitude_AxisConfig == null) nullCount++;
            if (settings.Observer_Location == null) nullCount++;
            if (settings.ZWO_Camera_Settings == null) nullCount++;
            if (settings.Sidereal_Tracking_PD_Gains == null) nullCount++;
            
            // Also check if observer location has default values (0,0 = invalid)
            bool locationIsDefault = settings.Observer_Location?.Latitude_Deg == 0.0 && 
                                    settings.Observer_Location?.Longitude_Deg == 0.0;
            
            // If 3+ sections are null OR location is 0,0, probably a reset
            return nullCount >= 3 || locationIsDefault;
        }
        
        #region Legacy MongoDB Compatibility
        
        /// <summary>
        /// MongoDB-backed settings storage has been retired.
        /// This method is kept as a no-op for compatibility.
        /// </summary>
        public static void SetMongoDbEnabled(bool enable)
        {
            System.Diagnostics.Debug.WriteLine("[AppSettings] MongoDB settings storage is retired; using JSON file storage.");
            SimpleLogger.Info("[AppSettings] MongoDB settings storage is retired; using JSON file storage.", "AppSettings");
        }
        
        /// <summary>
        /// MongoDB-backed settings storage has been retired.
        /// Reloads settings from the JSON file instead.
        /// </summary>
        public static void ForceReloadFromMongoDB()
        {
            ReloadSettings();
            SimpleLogger.Info("[AppSettings] Reloaded settings from JSON file.", "AppSettings");
        }
        
        /// <summary>
        /// Get storage status information
        /// </summary>
        public static string GetStorageStatus()
        {
            return $"Storage: JSON ({GetActiveSettingsFilePath()}), Cache: {(_currentSettings != null ? "LOADED" : "EMPTY")}";
        }
        
        #endregion

        /// <summary>
        /// Get Current Settings (from Cache)
        /// WARNING: Returns reference to cached object - do not modify directly!
        /// Use specific Save methods instead (e.g., SaveAzimuthAxisConfig)
        /// </summary>
        private static AppSettingsModel GetCurrentSettings()
        {
            lock (_lockObject)
            {
                if (_currentSettings == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] Cache MISS - loading from file...");
                    _currentSettings = LoadSettings();
                }
                else
                {
                    // [DEBUG] Periodically verify cache vs file (every 10000 calls = ~rare)
                    // Disabled by default to reduce log spam - enable for debugging only
                    if (false && _getCalls % 10000 == 0)
                    {
                        string activeSettingsFilePath = GetActiveSettingsFilePath();
                        var fileModTime = File.Exists(activeSettingsFilePath) ? File.GetLastWriteTime(activeSettingsFilePath) : DateTime.MinValue;
                        
                        // Use 1-second tolerance to avoid precision issues
                        var timeDiff = (fileModTime - _currentSettings.LastModified).TotalSeconds;
                        
                        if (timeDiff > 1.0)  // File is >1 second newer
                        {
                            System.Diagnostics.Debug.WriteLine($"[AppSettings] ?? File is {timeDiff:F1}s newer - reloading!");
                            _currentSettings = LoadSettings();
                        }
                    }
                    _getCalls++;
                }
                
                return _currentSettings;
            }
        }
        
        private static int _getCalls = 0;

        /// <summary>
        /// Reload Settings from file
        /// </summary>
        public static void ReloadSettings()
        {
            _currentSettings = LoadSettings(GetActiveSettingsFilePath());
        }

        #region Drive Settings (Platinum)

        /// <summary>
        /// Get Drive Serial Number (identifies which mount to connect to)
        /// </summary>
        public static string GetDriveSerialNumber()
        {
            try
            {
                var settings = GetCurrentSettings();
                return NormalizeSerialNumber(settings.Drive_SN);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Drive SN: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Switch the active settings profile based on drive serial number.
        /// SN 25382282 uses L-Mount settings, all others use Fork-Mount settings.
        /// </summary>
        public static void UseSettingsProfileForDriveSerialNumber(string serialNumber)
        {
            try
            {
                string normalizedSerialNumber = NormalizeSerialNumber(serialNumber);
                if (string.IsNullOrEmpty(normalizedSerialNumber))
                    return;

                lock (_lockObject)
                {
                    string targetSettingsFilePath = ResolveSettingsFilePath(normalizedSerialNumber);
                    bool profileChanged = !string.Equals(
                        _activeSettingsFilePath,
                        targetSettingsFilePath,
                        StringComparison.OrdinalIgnoreCase);

                    if (profileChanged || _currentSettings == null)
                    {
                        _activeSettingsFilePath = targetSettingsFilePath;
                        _currentSettings = File.Exists(targetSettingsFilePath)
                            ? LoadSettings(targetSettingsFilePath)
                            : new AppSettingsModel();
                    }

                    _currentSettings.Drive_SN = normalizedSerialNumber;
                    SaveSettings(_currentSettings, targetSettingsFilePath);
                    SaveBootstrapDriveSerialNumber(normalizedSerialNumber);

                    string mountProfileName = IsLMountDriveSerialNumber(normalizedSerialNumber)
                        ? "L-Mount"
                        : "Fork-Mount";
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] Active profile switched to {mountProfileName} for Drive SN {normalizedSerialNumber}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to switch settings profile for Drive SN: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Drive Serial Number
        /// </summary>
        public static void SaveDriveSerialNumber(string serialNumber)
        {
            try
            {
                UseSettingsProfileForDriveSerialNumber(serialNumber);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Drive SN: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// [Deprecated] Use GetDriveSerialNumber() instead
        /// </summary>
        [Obsolete("Use GetDriveSerialNumber() instead")]
        public static string GetAzimuthSerialNumber() => GetDriveSerialNumber();

        /// <summary>
        /// [Deprecated] Use SaveDriveSerialNumber() instead
        /// </summary>
        [Obsolete("Use SaveDriveSerialNumber() instead")]
        public static void SaveAzimuthSerialNumber(string serialNumber) => SaveDriveSerialNumber(serialNumber);

        /// <summary>
        /// Get Azimuth Baud Rate
        /// </summary>
        public static int GetAzimuthBaudRate()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Azimuth_BaudRate;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Azimuth Baud Rate: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Azimuth Baud Rate
        /// </summary>
        public static void SaveAzimuthBaudRate(int baudRate)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Azimuth_BaudRate = baudRate;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Azimuth Baud Rate: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get Azimuth Polling Interval
        /// </summary>
        public static int GetAzimuthPollingInterval()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Azimuth_PollingInterval;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Azimuth Polling Interval: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Azimuth Polling Interval
        /// </summary>
        public static void SaveAzimuthPollingInterval(int pollingInterval)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Azimuth_PollingInterval = pollingInterval;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Azimuth Polling Interval: {ex.Message}", ex);
            }
        }

        #endregion

        #region Altitude Drive Settings (Platinum)

        /// <summary>
        /// Get Altitude Serial Number
        /// </summary>
        public static string GetAltitudeSerialNumber()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Drive_SN ?? string.Empty;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Altitude SN: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Altitude Serial Number
        /// </summary>
        public static void SaveAltitudeSerialNumber(string serialNumber)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Drive_SN = serialNumber;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Altitude SN: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get Altitude Baud Rate
        /// </summary>
        public static int GetAltitudeBaudRate()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Altitude_BaudRate;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Altitude Baud Rate: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Altitude Baud Rate
        /// </summary>
        public static void SaveAltitudeBaudRate(int baudRate)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Altitude_BaudRate = baudRate;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Altitude Baud Rate: {ex.Message}", ex);
            }
        }

        #endregion

        #region Azimuth Axis Configuration

        /// <summary>
        /// Get Azimuth Axis Configuration
        /// </summary>
        public static AzimuthAxisConfig GetAzimuthAxisConfig()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Azimuth_AxisConfig ?? new AzimuthAxisConfig();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Azimuth Axis Config: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Azimuth Axis Configuration
        /// </summary>
        public static void SaveAzimuthAxisConfig(AzimuthAxisConfig config)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Azimuth_AxisConfig = config;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Azimuth Axis Config: {ex.Message}", ex);
            }
        }

        #endregion

        #region Altitude Axis Configuration

        /// <summary>
        /// Get Altitude Axis Configuration
        /// </summary>
        public static AltitudeAxisConfig GetAltitudeAxisConfig()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Altitude_AxisConfig ?? new AltitudeAxisConfig();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Altitude Axis Config: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Altitude Axis Configuration
        /// </summary>
        public static void SaveAltitudeAxisConfig(AltitudeAxisConfig config)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Altitude_AxisConfig = config;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Altitude Axis Config: {ex.Message}", ex);
            }
        }

        public static M3AxisConfig GetM3AxisConfig()
        {
            var settings = GetCurrentSettings();
            return settings.M3_AxisConfig ?? new M3AxisConfig();
        }

        public static FieldDerotatorAxisConfig GetFieldDerotatorAxisConfig()
        {
            var settings = GetCurrentSettings();
            return settings.FieldDerotator_AxisConfig ?? new FieldDerotatorAxisConfig();
        }

        #endregion

        #region Azimuth PD Controller and Tuning Settings

        /// <summary>
        /// Get Azimuth PD Controller Parameters
        /// </summary>
        public static PDControllerParameters GetAzimuthPDControllerParameters()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Azimuth_PD_Controller ?? new PDControllerParameters();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Azimuth PD Controller Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Azimuth PD Controller Parameters
        /// </summary>
        public static void SaveAzimuthPDControllerParameters(PDControllerParameters parameters)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Azimuth_PD_Controller = parameters;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Azimuth PD Controller Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get Azimuth Tuning Parameters
        /// </summary>
        public static TuningParameters GetAzimuthTuningParameters()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Azimuth_Tuning_Parameters ?? new TuningParameters();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Azimuth Tuning Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Azimuth Tuning Parameters
        /// </summary>
        public static void SaveAzimuthTuningParameters(TuningParameters parameters)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Azimuth_Tuning_Parameters = parameters;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Azimuth Tuning Parameters: {ex.Message}", ex);
            }
        }

        #endregion

        #region Altitude PD Controller and Tuning Settings

        /// <summary>
        /// Get Altitude PD Controller Parameters
        /// </summary>
        public static PDControllerParameters GetAltitudePDControllerParameters()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Altitude_PD_Controller ?? new PDControllerParameters();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Altitude PD Controller Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Altitude PD Controller Parameters
        /// </summary>
        public static void SaveAltitudePDControllerParameters(PDControllerParameters parameters)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Altitude_PD_Controller = parameters;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Altitude PD Controller Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get Altitude Tuning Parameters
        /// </summary>
        public static TuningParameters GetAltitudeTuningParameters()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Altitude_Tuning_Parameters ?? new TuningParameters();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Altitude Tuning Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Altitude Tuning Parameters
        /// </summary>
        public static void SaveAltitudeTuningParameters(TuningParameters parameters)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Altitude_Tuning_Parameters = parameters;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Altitude Tuning Parameters: {ex.Message}", ex);
            }
        }

        #endregion

        #region PD Controller Settings (Legacy - for backward compatibility)

        /// <summary>
        /// Get PD Controller Parameters (Legacy - for backward compatibility)
        /// </summary>
        public static PDControllerParameters GetPDControllerParameters()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.PD_Controller ?? new PDControllerParameters();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get PD Controller Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save PD Controller Parameters (Legacy - for backward compatibility)
        /// </summary>
        public static void SavePDControllerParameters(PDControllerParameters parameters)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.PD_Controller = parameters;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save PD Controller Parameters: {ex.Message}", ex);
            }
        }

        #endregion

        #region Tuning Parameters (Legacy - for backward compatibility)

        /// <summary>
        /// Get Tuning Parameters (Legacy - for backward compatibility)
        /// </summary>
        public static TuningParameters GetTuningParameters()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Tuning_Parameters ?? new TuningParameters();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Tuning Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Tuning Parameters (Legacy - for backward compatibility)
        /// </summary>
        public static void SaveTuningParameters(TuningParameters parameters)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Tuning_Parameters = parameters;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Tuning Parameters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get Tuning Steps
        /// </summary>
        public static TuningSteps GetTuningSteps()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Tuning_Steps ?? new TuningSteps();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Tuning Steps: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Tuning Steps
        /// </summary>
        public static void SaveTuningSteps(TuningSteps steps)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Tuning_Steps = steps;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Tuning Steps: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get Tuning Offsets
        /// </summary>
        public static TuningOffsets GetTuningOffsets()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Tuning_Offsets ?? new TuningOffsets();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Tuning Offsets: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Tuning Offsets
        /// </summary>
        public static void SaveTuningOffsets(TuningOffsets offsets)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Tuning_Offsets = offsets;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Tuning Offsets: {ex.Message}", ex);
            }
        }

        #endregion

        #region Observer Location Settings

        /// <summary>
        /// Get Observer Location
        /// </summary>
        public static ObserverLocationSettings GetObserverLocation()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Observer_Location ?? new ObserverLocationSettings();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Observer Location: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Observer Location
        /// </summary>
        public static void SaveObserverLocation(ObserverLocationSettings location)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Observer_Location = location;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Observer Location: {ex.Message}", ex);
            }
        }

        #endregion

        #region Pointing Model Settings

        /// <summary>
        /// Get Pointing Model file path
        /// </summary>
        public static string GetPointingModelFilePath()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.Mount_Calibration_Settings?.PointingModelFilePath ?? string.Empty;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Pointing Model file path: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save Pointing Model file path
        /// </summary>
        public static void SavePointingModelFilePath(string filePath)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.Mount_Calibration_Settings ??= new MountCalibrationSettings();
                settings.Mount_Calibration_Settings.PointingModelFilePath = filePath ?? string.Empty;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Pointing Model file path: {ex.Message}", ex);
            }
        }

        #endregion

        #region Generic Settings Methods

        /// <summary>
        /// Get All Settings
        /// </summary>
        public static AppSettingsModel GetAllSettings()
        {
            return GetCurrentSettings();
        }

        /// <summary>
        /// Save All Settings
        /// </summary>
        public static void SaveAllSettings(AppSettingsModel settings)
        {
            SaveSettings(settings);
        }

        /// <summary>
        /// Reset Settings to Default
        /// </summary>
        public static void ResetToDefault()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] ?????? RESET TO DEFAULT CALLED!");
                System.Diagnostics.Debug.WriteLine($"[AppSettings] This will ERASE all user settings!");
                
                // [SAFETY] Create backup before reset
                string activeSettingsFilePath = GetActiveSettingsFilePath();
                if (File.Exists(activeSettingsFilePath))
                {
                    string backupPath = activeSettingsFilePath + ".backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(activeSettingsFilePath, backupPath);
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Backup created: {backupPath}");
                }
                
                var defaultSettings = new AppSettingsModel();
                SaveSettings(defaultSettings);
                
                System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Settings reset to default");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Reset failed: {ex.Message}");
                throw new Exception($"Failed to reset settings: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if Settings file exists
        /// </summary>
        public static bool SettingsFileExists()
        {
            return File.Exists(GetActiveSettingsFilePath());
        }


        /// <summary>
        /// Get Settings file path
        /// </summary>
        public static string GetSettingsFilePath()
        {
            return GetActiveSettingsFilePath();
        }

        #endregion
        
        #region System Settings (NTP, etc.)
        
        /// <summary>
        /// Get System Settings
        /// </summary>
        public static SystemSettings GetSystemSettings()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.System_Settings ?? new SystemSettings();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get System Settings: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save System Settings
        /// </summary>
        public static void SaveSystemSettings(SystemSettings systemSettings)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.System_Settings = systemSettings;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save System Settings: {ex.Message}", ex);
            }
        }

        #endregion

        #region ZWO Camera Settings
        
        /// <summary>
        /// Get ZWO Camera Settings
        /// </summary>
        public static ZWOCameraSettings GetCameraSettings()
        {
            try
            {
                var settings = GetCurrentSettings();
                return settings.ZWO_Camera_Settings ?? new ZWOCameraSettings();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get Camera Settings: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Save ZWO Camera Settings
        /// </summary>
        public static void SaveCameraSettings(ZWOCameraSettings cameraSettings)
        {
            try
            {
                var settings = GetCurrentSettings();
                settings.ZWO_Camera_Settings = cameraSettings;
                SaveSettings(settings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save Camera Settings: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Reset Camera Settings to ASI 294MC Pro defaults
        /// - Pixel Size: 2.3?m
        /// - Focal Length: 1000mm
        /// - Binning: 2
        /// - Exposure: 3000ms
        /// - Gain: 150
        /// </summary>
        public static void ResetCameraSettingsToASI294MC()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Resetting Camera Settings to ASI 294MC Pro defaults...");
                
                var newCameraSettings = new ZWOCameraSettings
                {
                    ExposureTime_ms = 3000,      // 3 seconds for plate solving
                    Gain = 150,                   // Mid gain for sensitivity
                    Offset = 10,                  // Prevent black clipping
                    Width = 0,                    // Full frame (8288)
                    Height = 0,                   // Full frame (5644)
                    Binning = 2,                  // Bin2 for faster readout
                    ImageType = 1,                // RAW16
                    FocalLength_mm = 1000.0,      // 1000mm telescope
                    PlateScale_ArcsecPerPixel = 0.95,  // (2.3 * 2 / 1000) * 206.265
                    PixelSize_um = 2.3            // ASI294MC pixel size
                };
                
                SaveCameraSettings(newCameraSettings);
                
                System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Camera Settings reset to ASI 294MC Pro @ 1000mm");
                System.Diagnostics.Debug.WriteLine($"[AppSettings]    - Pixel Size: 2.3?m");
                System.Diagnostics.Debug.WriteLine($"[AppSettings]    - Focal Length: 1000mm");
                System.Diagnostics.Debug.WriteLine($"[AppSettings]    - Binning: 2");
                System.Diagnostics.Debug.WriteLine($"[AppSettings]    - Plate Scale: 0.95 arcsec/pixel");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Failed to reset Camera Settings: {ex.Message}");
                throw new Exception($"Failed to reset Camera Settings: {ex.Message}", ex);
            }
        }

        #endregion
        
        #region Mount Calibration Settings
        
        /// <summary>
        /// Get Mount Calibration Settings
        /// </summary>
        public static MountCalibrationSettings GetMountCalibrationSettings()
        {
            var settings = GetCurrentSettings();
            settings.Mount_Calibration_Settings ??= new MountCalibrationSettings();
            return settings.Mount_Calibration_Settings;
        }
        
        /// <summary>
        /// Save Mount Calibration Settings
        /// </summary>
        public static void SaveMountCalibrationSettings(MountCalibrationSettings calibrationSettings)
        {
            var settings = GetCurrentSettings();
            settings.Mount_Calibration_Settings = calibrationSettings ?? new MountCalibrationSettings();
            SaveSettings(settings);
        }

        #endregion
        
        #region Histogram Stretch Profile Settings
        
        /// <summary>
        /// Get Histogram Stretch Profile from Camera Settings
        /// Used for consistent image display across CameraSettings and LiveCalibrationImage
        /// </summary>
        public static HistogramStretchProfile GetHistogramStretchProfile()
        {
            try
            {
                var cameraSettings = GetCameraSettings();
                return cameraSettings.HistogramStretch ?? new HistogramStretchProfile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Failed to get Histogram Stretch Profile: {ex.Message}");
                return new HistogramStretchProfile();
            }
        }
        
        /// <summary>
        /// Save Histogram Stretch Profile to Camera Settings
        /// </summary>
        public static void SaveHistogramStretchProfile(HistogramStretchProfile profile)
        {
            try
            {
                var cameraSettings = GetCameraSettings();
                cameraSettings.HistogramStretch = profile;
                SaveCameraSettings(cameraSettings);
                
                System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Histogram Stretch Profile saved: Min={profile.MinValue}, Max={profile.MaxValue}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] ? Failed to save Histogram Stretch Profile: {ex.Message}");
                throw new Exception($"Failed to save Histogram Stretch Profile: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Quick save histogram stretch values (convenience method)
        /// </summary>
        public static void SaveHistogramStretchValues(int minValue, int maxValue)
        {
            var profile = GetHistogramStretchProfile();
            profile.MinValue = minValue;
            profile.MaxValue = maxValue;
            SaveHistogramStretchProfile(profile);
        }

        #endregion
        
        #region CameraOffset Histogram Stretch Profile
        
        /// <summary>
        /// Get CameraOffset Histogram Stretch Profile (separate from CameraSettings)
        /// </summary>
        public static HistogramStretchProfile GetCameraOffsetStretchProfile()
        {
            try
            {
                var appSettings = GetCurrentSettings();
                return appSettings.CameraOffset_HistogramStretch ?? new HistogramStretchProfile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Failed to get CameraOffset Stretch Profile: {ex.Message}");
                return new HistogramStretchProfile();
            }
        }
        
        /// <summary>
        /// Save CameraOffset Histogram Stretch Profile
        /// </summary>
        public static void SaveCameraOffsetStretchProfile(HistogramStretchProfile profile)
        {
            try
            {
                var appSettings = GetCurrentSettings();
                appSettings.CameraOffset_HistogramStretch = profile;
                SaveSettings(appSettings);
                System.Diagnostics.Debug.WriteLine($"[AppSettings] CameraOffset Stretch saved: Profile={profile.ProfileType}, Min={profile.MinValue}, Max={profile.MaxValue}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] Failed to save CameraOffset Stretch: {ex.Message}");
            }
        }

        #endregion
        
        #region Satellite Tracking PD Gains Settings
        
        /// <summary>
        /// Get Satellite Tracking PD Gains
        /// </summary>
        public static SatelliteTrackingPDGains GetSatelliteTrackingPDGains()
        {
            var settings = GetCurrentSettings();
            return settings.Satellite_Tracking_PD_Gains;
        }
        
        /// <summary>
        /// Save Satellite Tracking PD Gains
        /// </summary>
        public static void SaveSatelliteTrackingPDGains(SatelliteTrackingPDGains gains)
        {
            var settings = GetCurrentSettings();
            settings.Satellite_Tracking_PD_Gains = gains;
            SaveSettings(settings);
        }

        #endregion
        
        #region Sidereal Tracking PD Gains Settings
        
        /// <summary>
        /// Get Sidereal Tracking PD Gains
        /// </summary>
        public static SiderealTrackingPDGains GetSiderealTrackingPDGains()
        {
            var settings = GetCurrentSettings();
            return settings.Sidereal_Tracking_PD_Gains ?? new SiderealTrackingPDGains();
        }
        
        /// <summary>
        /// Save Sidereal Tracking PD Gains
        /// </summary>
        public static void SaveSiderealTrackingPDGains(SiderealTrackingPDGains gains)
        {
            var settings = GetCurrentSettings();
            settings.Sidereal_Tracking_PD_Gains = gains;
            SaveSettings(settings);
        }

        #endregion

        #region ZWO Camera Settings

        /// <summary>
        /// Get ZWO Camera Settings
        /// </summary>
        public static ZWOCameraSettings GetZWOCameraSettings()
        {
            var settings = GetCurrentSettings();
            return settings.ZWO_Camera_Settings;
        }

        /// <summary>
        /// Update ZWO Camera Settings
        /// </summary>
        public static void UpdateZWOCameraSettings(ZWOCameraSettings cameraSettings)
        {
            var settings = GetCurrentSettings();
            settings.ZWO_Camera_Settings = cameraSettings;
            SaveSettings(settings);

            System.Diagnostics.Debug.WriteLine("[AppSettings] ZWO Camera Settings saved:");
            System.Diagnostics.Debug.WriteLine($"  Exposure: {cameraSettings.ExposureTime_ms} ms");
            System.Diagnostics.Debug.WriteLine($"  Gain: {cameraSettings.Gain}");
            System.Diagnostics.Debug.WriteLine($"  Offset: {cameraSettings.Offset}");
            System.Diagnostics.Debug.WriteLine($"  Resolution: {cameraSettings.Width}x{cameraSettings.Height}");
            System.Diagnostics.Debug.WriteLine($"  Binning: {cameraSettings.Binning}x{cameraSettings.Binning}");
            System.Diagnostics.Debug.WriteLine($"  Image Type: {cameraSettings.ImageType}");
        }

        #endregion

        #region FITS Auto Save Settings

        /// <summary>
        /// Get FITS Auto Save directory
        /// </summary>
        public static string GetFitsAutoSaveDirectory()
        {
            var settings = GetCurrentSettings();
            return settings.FitsAutoSaveDirectory ?? "";
        }

        /// <summary>
        /// Get FITS Auto Save prefix
        /// </summary>
        public static string GetFitsAutoSavePrefix()
        {
            var settings = GetCurrentSettings();
            return string.IsNullOrWhiteSpace(settings.FitsAutoSavePrefix) ? "Light" : settings.FitsAutoSavePrefix;
        }

        /// <summary>
        /// Save FITS Auto Save settings
        /// </summary>
        public static void SaveFitsAutoSaveSettings(string directory, string prefix)
        {
            var settings = GetCurrentSettings();
            settings.FitsAutoSaveDirectory = directory ?? "";
            settings.FitsAutoSavePrefix = prefix ?? "Light";
            SaveSettings(settings);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Create Sample Settings with Default values
        /// </summary>
        public static void CreateSampleSettings()
        {
            try
            {
                var sampleSettings = new AppSettingsModel
                {
                    Drive_SN = "PLATINUM-001",
                    Azimuth_BaudRate = 115200,
                    Azimuth_PollingInterval = 100,
                    Version = "1.0-Platinum"
                };

                SaveSettings(sampleSettings);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create sample settings: {ex.Message}", ex);
            }
        }

        #endregion
    }
}
