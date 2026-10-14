using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Utility;
using PXPLib;  // PlaneWave pointing model library
using PWLib.Astro;

namespace ForkMount_WPF.Engine.PointingModel
{
    /// <summary>
    /// Manages pointing model operations at runtime
    /// - Loads model on startup
    /// - Applies corrections to mount positions using PXPLib
    /// - Updates MountInfo with corrected positions
    /// [NEW] Uses PlaneWave PointXP (following NaritMountLib pattern)
    /// [CRITICAL] Uses SINGLE PointXP instance for entire application!
    /// </summary>
    public class PointingModelManager
    {
        // [CRITICAL] SINGLE PointXP instance for entire application!
        // Following NaritMountLib pattern: App.MountManager.PointXP is used everywhere
        private PointXP _pointXP;
        private bool _isPxpModelLoaded;
        private string _modelFilePath;
        private string _legacyModelFilePath;
        private System.Reflection.MethodInfo _encToAltAzMethod; // Cached reflection for Core.EncToAltAz
        private ExperimentalPointingModelMetrics _experimentalMetrics;
        private PointingModelBackendMode _activeBackendMode = PointingModelBackendMode.LegacyPxpReload;
        
        // [NEW] FileSystemWatcher to auto-detect new model files in CalibrationData folder
        private FileSystemWatcher _modelFileWatcher;
        
        // [FIX] Debounce FileWatcher to prevent redundant model reloads
        // FileSystemWatcher fires 2-3 events per file write, each triggering a full
        // model reload (Calibrate() 5-20ms) that stalls sidereal tracking
        private DateTime _lastAutoLoadTime = DateTime.MinValue;
        private string _lastAutoLoadFile = "";
        private const int FILE_WATCHER_DEBOUNCE_MS = 2000;
        private const int MIN_RUNTIME_CAL_STARS = 9;
        public static string GetDefaultModelFilePath() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData", "Default_Model.pxp");
        
        /// <summary>
        /// [CRITICAL] Get the SINGLE PointXP instance used by entire application
        /// This allows external code to access the same PointXP object
        /// Following NaritMountLib pattern: App.MountManager.PointXP
        /// </summary>
        public PointXP PointXP => _pointXP;
        
        /// <summary>
        /// Is pointing model currently loaded and active?
        /// </summary>
        public bool IsModelLoaded => _isPxpModelLoaded;

        public bool IsExperimentalModelActive => _activeBackendMode == PointingModelBackendMode.ExperimentalCoreDirect && _isPxpModelLoaded;

        public PointingModelBackendMode ActiveBackendMode => _activeBackendMode;

        public ExperimentalPointingModelMetrics ExperimentalMetrics => _experimentalMetrics;

        public bool TryGetLoadedModelMetrics(out double rmsArcsec, out double maxErrorArcsec, out int numPointsUsed, out int numCalPoints)
        {
            rmsArcsec = 0.0;
            maxErrorArcsec = 0.0;
            numPointsUsed = 0;
            numCalPoints = 0;

            if (!IsModelLoaded || _pointXP?.Core == null)
            {
                return false;
            }

            try
            {
                numCalPoints = _pointXP.Core.NumCalPoints;
                numPointsUsed = _pointXP.Core.NumPointsUsed;

                if (numCalPoints <= 0)
                {
                    return false;
                }

                const double arcsecPerRadian = 206264.80624709636;
                rmsArcsec = _pointXP.Core.RMSErrorRad * arcsecPerRadian;
                maxErrorArcsec = _pointXP.Core.MaxErrorRad * arcsecPerRadian;

                return !double.IsNaN(rmsArcsec) &&
                       !double.IsInfinity(rmsArcsec) &&
                       !double.IsNaN(maxErrorArcsec) &&
                       !double.IsInfinity(maxErrorArcsec);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] TryGetLoadedModelMetrics error: {ex.Message}");
                return false;
            }
        }

        public IReadOnlyList<LoadedModelCalibrationDiagnostic> GetLoadedCalibrationDiagnostics()
        {
            var diagnostics = new List<LoadedModelCalibrationDiagnostic>();

            if (!IsModelLoaded || _pointXP?.Core == null)
            {
                return diagnostics;
            }

            try
            {
                const double arcsecPerRadian = 206264.80624709636;

                for (int index = 1; index <= _pointXP.Core.NumCalPoints; index++)
                {
                    var calPoint = _pointXP.Core.GetCalPoint(index);
                    if (calPoint == null)
                    {
                        continue;
                    }

                    diagnostics.Add(new LoadedModelCalibrationDiagnostic
                    {
                        Index = index,
                        Used = calPoint.Used,
                        TotalErrorArcsec = calPoint.DistErrRad * arcsecPerRadian,
                        RASkyErrorArcsec = calPoint.RASkyErrRad * arcsecPerRadian,
                        DecErrorArcsec = calPoint.DecErrRad * arcsecPerRadian,
                        AzimuthDeg = calPoint.Azimuth * 180.0 / Math.PI,
                        AltitudeDeg = (Math.PI / 2.0 - calPoint.ZenithDist) * 180.0 / Math.PI,
                        ThetaErrorArcsec = calPoint.ThetaErrRad * arcsecPerRadian,
                        PhiSkyErrorArcsec = calPoint.PhiSkyErrRad * arcsecPerRadian,
                        PhiEncErrorArcsec = calPoint.PhiEncErrRad * arcsecPerRadian
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] GetLoadedCalibrationDiagnostics error: {ex.Message}");
            }

            return diagnostics;
        }

        internal static IReadOnlyList<LoadedModelCalibrationDiagnostic> GetCalibrationDiagnosticsFromFile(string filepath)
        {
            var diagnostics = new List<LoadedModelCalibrationDiagnostic>();

            if (string.IsNullOrWhiteSpace(filepath) || !File.Exists(filepath))
            {
                return diagnostics;
            }

            try
            {
                var pointXP = CreateConfiguredRuntimePointXP();
                pointXP.LoadFile(filepath);

                if (pointXP.Core == null || pointXP.Core.NumCalPoints <= 0)
                {
                    return diagnostics;
                }

                const double arcsecPerRadian = 206264.80624709636;

                for (int index = 1; index <= pointXP.Core.NumCalPoints; index++)
                {
                    var calPoint = pointXP.Core.GetCalPoint(index);
                    if (calPoint == null)
                    {
                        continue;
                    }

                    diagnostics.Add(new LoadedModelCalibrationDiagnostic
                    {
                        Index = index,
                        Used = calPoint.Used,
                        TotalErrorArcsec = calPoint.DistErrRad * arcsecPerRadian,
                        RASkyErrorArcsec = calPoint.RASkyErrRad * arcsecPerRadian,
                        DecErrorArcsec = calPoint.DecErrRad * arcsecPerRadian,
                        AzimuthDeg = calPoint.Azimuth * 180.0 / Math.PI,
                        AltitudeDeg = (Math.PI / 2.0 - calPoint.ZenithDist) * 180.0 / Math.PI,
                        ThetaErrorArcsec = calPoint.ThetaErrRad * arcsecPerRadian,
                        PhiSkyErrorArcsec = calPoint.PhiSkyErrRad * arcsecPerRadian,
                        PhiEncErrorArcsec = calPoint.PhiEncErrRad * arcsecPerRadian
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] GetCalibrationDiagnosticsFromFile error: {ex.Message}");
            }

            return diagnostics;
        }
        
        
        
        
        /// <summary>
        /// Get the currently loaded model file path
        /// </summary>
        public string ModelFilePath => _activeBackendMode == PointingModelBackendMode.ExperimentalCoreDirect
            ? "[Experimental Core Direct]"
            : _modelFilePath;
        
        /// <summary>
        /// Check if PointXP library was initialized successfully
        /// </summary>
        public bool IsPointXPInitialized => _pointXP != null;
        
        /// <summary>
        /// Get initialization error message (if any)
        /// </summary>
        public string InitializationError { get; private set; }
        
        public PointingModelManager()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[PointingModel] ========================================");
                System.Diagnostics.Debug.WriteLine("[PointingModel] Initializing PointXP (NaritMountLib pattern)...");
                
                // [FIX] Following NaritMountLib pattern - DON'T use PointXPCore separately!
                // Just create PointXP directly like in MountSystem.LoadDefaultPointingModel()
                System.Diagnostics.Debug.WriteLine("[PointingModel] Step 1: Creating PointXP...");
                _pointXP = CreateConfiguredPointXP();
                System.Diagnostics.Debug.WriteLine("[PointingModel] ? PointXP created");
                
                // Step 4: Load configuration from AppSettings (MongoDB)
                System.Diagnostics.Debug.WriteLine("[PointingModel] Step 4: Loading Observer Location from AppSettings (MongoDB)...");
                var observerLocation = AppSettings.GetObserverLocation();
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    AppSettings Latitude:  {observerLocation.Latitude_Deg:F6}�");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    AppSettings Longitude: {observerLocation.Longitude_Deg:F6}�");
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ? PointXP initialized successfully!");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    PointXP.Latitude:  {_pointXP.Latitude:F6}�");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    PointXP.Longitude: {_pointXP.Longitude:F6}�");
                System.Diagnostics.Debug.WriteLine("[PointingModel] ========================================");
                
                InitializationError = null;
                
                // [NEW] Start watching CalibrationData folder for new model files
                StartFileWatcher();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ? FAILED to initialize PointXP!");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    Type: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    Stack: {ex.StackTrace}");
                _pointXP = null;
                InitializationError = ex.Message;
            }
        }

        /// <summary>
        /// Update the observer site used by PointXP at runtime.
        /// This is required when API clients change site after the manager was created.
        /// </summary>
        public void UpdateObserverLocation(double latitudeDeg, double longitudeDeg)
        {
            if (_pointXP == null)
            {
                InitializationError ??= "PointXP is not initialized.";
                return;
            }

            _pointXP.Latitude = latitudeDeg;
            _pointXP.Longitude = longitudeDeg;

            System.Diagnostics.Debug.WriteLine($"[PointingModel] Observer location updated at runtime: Lat={latitudeDeg:F6}°, Lon={longitudeDeg:F6}°");
        }
        
        internal static PointXP CreateConfiguredRuntimePointXP()
        {
            var observerLocation = AppSettings.GetObserverLocation();
            var pointXP = new PointXP
            {
                MountType = (short)0,
                Latitude = observerLocation.Latitude_Deg,
                Longitude = observerLocation.Longitude_Deg,
                ThetaCountsPerRev = 2.0 * Math.PI,
                PhiCountsPerRev = 2.0 * Math.PI
            };

            pointXP.EnableTermsForAltAz();
            return pointXP;
        }

        private static PointXP CreateConfiguredPointXP()
        {
            return CreateConfiguredRuntimePointXP();
        }

        /// <summary>
        /// Start FileSystemWatcher on CalibrationData folder.
        /// Auto-loads new valid .pxp/.pmd files when they appear or are modified.
        /// </summary>
        private void StartFileWatcher()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                
                _modelFileWatcher = new FileSystemWatcher(dir)
                {
                    Filter = "PointingModel*.*",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                _modelFileWatcher.Created += OnModelFileChanged;
                _modelFileWatcher.Changed += OnModelFileChanged;
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] FileWatcher started on: {dir}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] FileWatcher failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Called when a new/modified model file is detected in CalibrationData folder.
        /// Checks if it's a valid file with calibration stars > 0, then loads it.
        /// </summary>
        private void OnModelFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (_activeBackendMode == PointingModelBackendMode.ExperimentalCoreDirect)
                {
                    System.Diagnostics.Debug.WriteLine("[PointingModel] FileWatcher ignored while experimental core model is active");
                    return;
                }

                // Brief delay to let the file finish writing
                System.Threading.Thread.Sleep(500);
                
                string filePath = e.FullPath;
                if (!File.Exists(filePath))
                    return;
                
                // Only process .pxp and .pmd files
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext != ".pxp" && ext != ".pmd")
                    return;
                
                // [FIX] Debounce: FileSystemWatcher fires 2-3 events per single file write.
                // Each redundant reload runs Calibrate() (5-20ms) and blocks during tracking,
                // causing sidereal loop gaps (97ms max dt) and mount overshoot.
                var now = DateTime.UtcNow;
                if (filePath == _lastAutoLoadFile && 
                    (now - _lastAutoLoadTime).TotalMilliseconds < FILE_WATCHER_DEBOUNCE_MS)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Debounced: {Path.GetFileName(filePath)} (already loaded {(now - _lastAutoLoadTime).TotalMilliseconds:F0}ms ago)");
                    return;
                }
                
                int starCount = GetCalStarCount(filePath);
                System.Diagnostics.Debug.WriteLine($"[PointingModel] File detected: {Path.GetFileName(filePath)} [{starCount} stars]");
                
                if (starCount <= 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Skipped (0 stars)");
                    return;
                }
                
                // Check if this file is newer and has more stars than current
                if (_isPxpModelLoaded && !string.IsNullOrEmpty(_modelFilePath))
                {
                    int currentStars = GetCalStarCount(_modelFilePath);
                    DateTime currentTime = File.GetLastWriteTime(_modelFilePath);
                    DateTime newTime = File.GetLastWriteTime(filePath);
                    
                    // Load if: newer file OR same file updated with more stars
                    if (newTime <= currentTime && filePath != _modelFilePath)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Skipped (not newer than current)");
                        return;
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ?? Auto-loading new model: {Path.GetFileName(filePath)} [{starCount} stars]");
                bool loaded = LoadModel(filePath);
                
                if (loaded)
                {
                    _lastAutoLoadFile = filePath;
                    _lastAutoLoadTime = DateTime.UtcNow;
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Auto-loaded successfully!");
                }
                else
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Auto-load failed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] OnModelFileChanged error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get latest pointing model file from CalibrationData folder
        /// Format: PointingModelDDMMYYYY-XX.pxp (PlaneWave format)
        /// Returns newest file WITH calibration data (skips empty progressive files)
        /// </summary>
        private string GetLatestPointingModelFile()
        {
            try
            {
                string calibrationDataDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "CalibrationData"
                );
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Searching for model files...");
                System.Diagnostics.Debug.WriteLine($"[PointingModel] CalibrationData path: {calibrationDataDir}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Directory exists: {Directory.Exists(calibrationDataDir)}");
                
                if (!Directory.Exists(calibrationDataDir))
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? CalibrationData directory not found!");
                    return null;
                }
                
                // Find all .pxp and .pmd files (PlaneWave format + legacy)
                var pxpFiles = Directory.GetFiles(calibrationDataDir, "PointingModel*.pxp")
                    .Concat(Directory.GetFiles(calibrationDataDir, "PointingModel*.pmd"))
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Found {pxpFiles.Count} model file(s)");
                
                if (pxpFiles.Count > 0)
                {
                    foreach (var file in pxpFiles)
                    {
                        int numStars = GetCalStarCount(file);
                        System.Diagnostics.Debug.WriteLine($"[PointingModel]   - {Path.GetFileName(file)} ({File.GetLastWriteTime(file)}) [{numStars} stars]");
                    }
                    
                    // [CRITICAL FIX] Skip files with 0 calibration stars!
                    // InitializeProgressiveModelFile() creates empty files (0 stars) at the start
                    // of each calibration session. Loading these breaks the pointing model!
                    var validFile = pxpFiles.FirstOrDefault(f =>
                    {
                        string fileName = Path.GetFileName(f);
                        int starCount = GetCalStarCount(f);
                        bool isAutosave = fileName.Contains(".autosave.", StringComparison.OrdinalIgnoreCase);
                        return starCount >= MIN_RUNTIME_CAL_STARS && !isAutosave;
                    });
                    
                    if (validFile != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Will load: {Path.GetFileName(validFile)} ({GetCalStarCount(validFile)} stars)");
                        return validFile;
                    }

                    var autosaveFallback = pxpFiles.FirstOrDefault(f =>
                    {
                        string fileName = Path.GetFileName(f);
                        int starCount = GetCalStarCount(f);
                        bool isAutosave = fileName.Contains(".autosave.", StringComparison.OrdinalIgnoreCase);
                        return isAutosave && starCount >= MIN_RUNTIME_CAL_STARS;
                    });

                    if (autosaveFallback != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Falling back to autosave runtime model: {Path.GetFileName(autosaveFallback)} ({GetCalStarCount(autosaveFallback)} stars)");
                        return autosaveFallback;
                    }

                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? No runtime-safe model found (need >= {MIN_RUNTIME_CAL_STARS} stars; autosave allowed as fallback)");
                    return null;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? No PointingModel*.pxp or *.pmd files found");
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Error searching for model files: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Read "Number of Cal Stars" from a .pxp file header
        /// Returns 0 if file is empty or cannot be parsed
        /// </summary>
        private static int GetCalStarCount(string filePath)
        {
            try
            {
                using var reader = new StreamReader(filePath);
                string line1 = reader.ReadLine(); // "Number of Cal Stars:"
                string line2 = reader.ReadLine(); // e.g., "8"
                if (line2 != null && int.TryParse(line2.Trim(), out int count))
                    return count;
            }
            catch { }
            return 0;
        }
        
        /// <summary>
        /// Load pointing model from default location
        /// Call this on application startup
        /// [NEW] Uses PointXP.LoadPointingFile() (PlaneWave method)
        /// </summary>
        public bool LoadModel()
        {
            string defaultModelPath = GetDefaultModelFilePath();
            if (File.Exists(defaultModelPath))
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Loading Default_Model first: {defaultModelPath}");
                AppSettings.SavePointingModelFilePath(defaultModelPath);
                return LoadModel(defaultModelPath);
            }

            string configuredPath = AppSettings.GetPointingModelFilePath();
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Loading configured model file: {configuredPath}");
                return LoadModel(configuredPath);
            }

            // Get latest model file
            string latestFile = GetLatestPointingModelFile();
            if (string.IsNullOrEmpty(latestFile))
            {
                return false;
            }
            
            return LoadModel(latestFile);
        }
        
        /// <summary>
        /// Load pointing model from specific file
        /// [NEW] Uses PointXP.LoadPointingFile() to load PlaneWave .pxp format
        /// </summary>
        public bool LoadModel(string filepath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ========================================");
                System.Diagnostics.Debug.WriteLine($"[PointingModel] LoadModel (NaritMountLib pattern)");
                System.Diagnostics.Debug.WriteLine($"[PointingModel] File: {filepath}");
                
                if (string.IsNullOrEmpty(filepath))
                {
                    System.Diagnostics.Debug.WriteLine("[PointingModel] ? File path is null or empty");
                    _isPxpModelLoaded = false;
                    return false;
                }
                
                if (!File.Exists(filepath))
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? File not found: {filepath}");
                    _isPxpModelLoaded = false;
                    return false;
                }

                filepath = NormalizePointingModelFileIfNeeded(filepath);
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ? File exists, size: {new FileInfo(filepath).Length} bytes");
                
                // [FIX] Following NaritMountLib.MountSystem.LoadDefaultPointingModel() pattern:
                // 1. Create a SEPARATE PointXP to load and validate
                // 2. Check MountType, Latitude, Longitude
                // 3. Only if all checks pass, load into main PointXP
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Step 1: Creating temporary PointXP to validate file...");
                PointXP tempPointXP = new PointXP();
                
                try
                {
                    tempPointXP.LoadFile(filepath);
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Temp PointXP loaded file successfully");
                }
                catch (Exception loadEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Temp PointXP.LoadFile() EXCEPTION: {loadEx.Message}");
                    _isPxpModelLoaded = false;
                    return false;
                }
                
                // Step 2: Validate MountType
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Step 2: Validating MountType...");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    File MountType: {tempPointXP.MountType}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    Our MountType: {_pointXP?.MountType}");
                
                if (_pointXP != null && tempPointXP.MountType != _pointXP.MountType)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? MountType mismatch! Model was built for different mount geometry");
                    _isPxpModelLoaded = false;
                    return false;
                }
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ? MountType matches");
                
                // Step 3: Validate Latitude (allow 0.001� difference)
                var observerLocation = AppSettings.GetObserverLocation();
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Step 3: Validating Latitude...");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    File Latitude: {tempPointXP.Latitude:F4}�");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    Our Latitude: {observerLocation.Latitude_Deg:F4}�");
                
                if (Math.Abs(tempPointXP.Latitude - observerLocation.Latitude_Deg) > 0.001)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ?? Latitude mismatch (diff > 0.001�) - loading anyway");
                    // NaritMountLib rejects this, but we'll warn and continue
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Latitude matches");
                }
                
                // Step 4: Validate Longitude (allow 0.001� difference)
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Step 4: Validating Longitude...");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    File Longitude: {tempPointXP.Longitude:F4}�");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    Our Longitude: {observerLocation.Longitude_Deg:F4}�");
                
                if (Math.Abs(tempPointXP.Longitude - observerLocation.Longitude_Deg) > 0.001)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ?? Longitude mismatch (diff > 0.001�) - loading anyway");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Longitude matches");
                }
                
                // Step 5: All validation passed - load into main PointXP
                if (_pointXP == null)
                {
                    System.Diagnostics.Debug.WriteLine("[PointingModel] ? Main PointXP not initialized (null)");
                    _isPxpModelLoaded = false;
                    return false;
                }
                
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Step 5: Loading into main PointXP...");
                try
                {
                    _pointXP.LoadFile(filepath);
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Main PointXP loaded file successfully");
                    
                    // [DEBUG] Check if model was actually loaded
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] === POST-LOAD VERIFICATION ===");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    _pointXP reference: {(_pointXP != null ? "exists" : "NULL!")}");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    _pointXP.Core: {(_pointXP?.Core != null ? "exists" : "NULL!")}");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    NumCalPoints: {_pointXP?.Core?.NumCalPoints ?? -1}");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    MountType: {_pointXP?.MountType}");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    Latitude: {_pointXP?.Latitude:F4}�");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    Longitude: {_pointXP?.Longitude:F4}�");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    ThetaCountsPerRev: {_pointXP?.ThetaCountsPerRev:F6}");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel]    PhiCountsPerRev: {_pointXP?.PhiCountsPerRev:F6}");
                }
                catch (Exception loadEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Main PointXP.LoadFile() EXCEPTION: {loadEx.Message}");
                    _isPxpModelLoaded = false;
                    return false;
                }
                
                _isPxpModelLoaded = true;
                _modelFilePath = filepath;
                _legacyModelFilePath = filepath;
                _activeBackendMode = PointingModelBackendMode.LegacyPxpReload;
                _experimentalMetrics = null;
                
                // [FIX] Record load time so FileWatcher debounce works even when
                // LoadModel() is called directly (e.g., by calibration manager)
                _lastAutoLoadFile = filepath;
                _lastAutoLoadTime = DateTime.UtcNow;
                
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ? MODEL LOADED SUCCESSFULLY!");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    File: {Path.GetFileName(filepath)}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ========================================");
                
                // [DEBUG] Read and display model terms to verify they're reasonable
                try
                {
                    var lines = File.ReadAllLines(filepath);
                    int numStars = 0;
                    if (lines.Length > 1 && int.TryParse(lines[1], out int count))
                    {
                        numStars = count;
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ========== MODEL INFO ==========");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Calibration Points: {numStars}");
                    
                    // [CRITICAL WARNING] PointXP needs multiple calibration points!
                    if (numStars < 9)
                    {
                        System.Diagnostics.Debug.WriteLine($"");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] ?????? WARNING: INSUFFICIENT CALIBRATION POINTS! ??????");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Current: {numStars} points");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Minimum recommended: 9 points");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Optimal: 20-40 points spread across sky");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] ");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] With only {numStars} point(s), PointXP cannot fit model terms correctly!");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] All error will be absorbed into encoder offsets (WRONG!)");
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] ?????? CORRECTIONS WILL BE INACCURATE! ??????");
                        System.Diagnostics.Debug.WriteLine($"");
                    }
                    
                    // Extract key terms from file
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("CW Azimuth"))
                        {
                            System.Diagnostics.Debug.WriteLine($"[PointingModel] CW Azimuth: {(i+1 < lines.Length ? lines[i+1] : "?")}");
                            System.Diagnostics.Debug.WriteLine($"[PointingModel] MaxAzCounts: {(i+2 < lines.Length ? lines[i+2] : "?")}");
                        }
                        else if (lines[i].StartsWith("Altitude Up"))
                        {
                            System.Diagnostics.Debug.WriteLine($"[PointingModel] Altitude Up: {(i+1 < lines.Length ? lines[i+1] : "?")}");
                            System.Diagnostics.Debug.WriteLine($"[PointingModel] MaxAltCounts: {(i+2 < lines.Length ? lines[i+2] : "?")}");
                        }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] =============================");
                }
                catch { /* Ignore parsing errors */ }
                
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ? Load failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    Stack: {ex.StackTrace}");
                _isPxpModelLoaded = false;
                return false;
            }
        }

        /// <summary>
        /// Repair common progressive .pxp formatting issues before sending the file to PointXP.
        /// Older runs could write the header count one step ahead of the actual file contents,
        /// producing files like "Number of Cal Stars = 10" while the first block starts at
        /// "Cal Star 2". PointXP is strict about this structure and may refuse to load.
        /// </summary>
        private string NormalizePointingModelFileIfNeeded(string filepath)
        {
            try
            {
                var lines = File.ReadAllLines(filepath).ToList();
                if (lines.Count < 2)
                    return filepath;

                bool hasModelTermsSection = lines.Any(line => line.StartsWith("Model Terms:", StringComparison.OrdinalIgnoreCase));

                int actualCount = 0;
                var calStarIndexes = new List<int>();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith("Cal Star ", StringComparison.OrdinalIgnoreCase))
                    {
                        calStarIndexes.Add(i);
                        actualCount++;
                    }
                }

                if (actualCount == 0)
                    return filepath;

                bool changed = false;
                if (!int.TryParse(lines[1].Trim(), out int headerCount) || headerCount != actualCount)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Repairing header count: {lines[1]} -> {actualCount}");
                    lines[1] = actualCount.ToString();
                    changed = true;
                }

                for (int i = 0; i < calStarIndexes.Count; i++)
                {
                    string expectedLine = $"Cal Star {i + 1}:";
                    int lineIndex = calStarIndexes[i];
                    if (!string.Equals(lines[lineIndex].Trim(), expectedLine, StringComparison.Ordinal))
                    {
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Repairing star label: {lines[lineIndex]} -> {expectedLine}");
                        lines[lineIndex] = expectedLine;
                        changed = true;
                    }
                }

                if (changed)
                {
                    File.WriteAllLines(filepath, lines);
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Repaired header/labels in model file: {filepath}");
                }

                if (!hasModelTermsSection)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Model Terms section missing in {Path.GetFileName(filepath)} - attempting PXPLib recalibration repair");
                    if (TryRebuildPointingModelFile(filepath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Rebuilt model terms successfully for {Path.GetFileName(filepath)}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Failed to rebuild model terms for {Path.GetFileName(filepath)}");
                    }
                }

                return filepath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Model normalization skipped: {ex.Message}");
                return filepath;
            }
        }

        private bool TryRebuildPointingModelFile(string filepath)
        {
            try
            {
                var repairPointXP = CreateConfiguredPointXP();
                repairPointXP.LoadFile(filepath);

                if (repairPointXP.Core == null || repairPointXP.Core.NumCalPoints <= 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Rebuild skipped - no calibration points available in {Path.GetFileName(filepath)}");
                    return false;
                }

                repairPointXP.Core.Calibrate();
                repairPointXP.SaveFile(filepath);

                var repairedLines = File.ReadAllLines(filepath);
                bool hasModelTerms = repairedLines.Any(line => line.StartsWith("Model Terms:", StringComparison.OrdinalIgnoreCase));
                System.Diagnostics.Debug.WriteLine($"[PointingModel] PXPLib rebuild complete: NumCalPoints={repairPointXP.Core.NumCalPoints}, NumPointsUsed={repairPointXP.Core.NumPointsUsed}, HasModelTerms={hasModelTerms}");
                return hasModelTerms;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] TryRebuildPointingModelFile failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Unload current pointing model
        /// </summary>
        public void UnloadModel()
        {
            _isPxpModelLoaded = false;
            _modelFilePath = null;
            _legacyModelFilePath = null;
            _experimentalMetrics = null;
            _activeBackendMode = PointingModelBackendMode.LegacyPxpReload;
        }

        public bool ActivateExperimentalModel(
            IEnumerable<ExperimentalCalibrationSample> samples,
            out ExperimentalPointingModelMetrics metrics)
        {
            metrics = ExperimentalPointingModelMetrics.CreatePending(0, 0, "No samples");

            try
            {
                var builder = new ExperimentalPointingModelBuilder();
                string errorMessage;
                PointXP experimentalPointXp = builder.BuildModel(samples, out metrics, out errorMessage);

                if (experimentalPointXp == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Experimental build failed: {errorMessage}");
                    return false;
                }

                _pointXP = experimentalPointXp;
                _encToAltAzMethod = null;
                _isPxpModelLoaded = metrics.IsCalibrated;
                _activeBackendMode = PointingModelBackendMode.ExperimentalCoreDirect;
                _experimentalMetrics = metrics;
                _modelFilePath = "[Experimental Core Direct]";

                System.Diagnostics.Debug.WriteLine($"[PointingModel] Experimental core model updated: {metrics.Summary}");
                return metrics.IsCalibrated;
            }
            catch (Exception ex)
            {
                metrics = ExperimentalPointingModelMetrics.CreatePending(0, 0, $"Experimental activation failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel] Experimental activation error: {ex.Message}");
                return false;
            }
        }

        public bool RestoreLegacyModel()
        {
            _experimentalMetrics = null;
            _activeBackendMode = PointingModelBackendMode.LegacyPxpReload;

            if (!string.IsNullOrWhiteSpace(_legacyModelFilePath) && File.Exists(_legacyModelFilePath))
            {
                return LoadModel(_legacyModelFilePath);
            }

            _pointXP = CreateConfiguredPointXP();
            _encToAltAzMethod = null;
            _isPxpModelLoaded = false;
            _modelFilePath = _legacyModelFilePath;
            return false;
        }
        
        /// <summary>
        /// Reload pointing model � always scans CalibrationData folder for the latest valid file.
        /// Does NOT lock to a single file path.
        /// </summary>
        public bool ReloadModel()
        {
            return LoadModel();  // Always scan for latest valid file
        }
        
        
        /// <summary>
        /// Apply pointing model correction to MountInfo
        /// Call this from CentralizedPollingManager after updating raw positions
        /// [NEW] Uses PointXP.EncoderToAltAz() for real-time correction (NaritMountLib pattern)
        /// PointXP internally applies all pointing model corrections!
        /// [CRITICAL FIX] Must pass NORMALIZED encoder positions (0-2? radians), NOT degrees with offset!
        /// </summary>
        /// <param name="mountInfo">MountInfo to update</param>
        /// <param name="azimuthInfo">AzimuthInfo with raw encoder counts</param>
        /// <param name="altitudeInfo">AltitudeInfo with raw encoder counts</param>
        public void ApplyCorrections(MountInfo mountInfo, AzimuthInfo azimuthInfo, AltitudeInfo altitudeInfo)
        {
            if (!IsModelLoaded || _pointXP == null)
            {
                // No model loaded - corrected = raw
                if (mountInfo != null)
                {
                    mountInfo.Act_Azimuth_Pos_Correct = mountInfo.Act_Azimuth_Pos_Raw;
                    mountInfo.Act_Altitude_Pos_Correct = mountInfo.Act_Altitude_Pos_Raw;
                    mountInfo.UpdateAstronomicalCoordinates();
                }
                if (_debugCallCount++ % 1000 == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] ApplyCorrections: No model ? corrected=raw");
                }
                return;
            }
            
            if (azimuthInfo == null || altitudeInfo == null)
            {
                return;
            }
            
            // [DEBUG] First call only - verify PointXP state
            if (_debugCallCount == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] === FIRST ApplyCorrections CALL ===");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    _pointXP: {(_pointXP != null ? "exists" : "NULL!")}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    NumCalPoints: {_pointXP?.Core?.NumCalPoints ?? -1}");
                System.Diagnostics.Debug.WriteLine($"[PointingModel]    MountType: {_pointXP?.MountType}");
            }
            
            try
            {
                // [CRITICAL FIX] Get RAW encoder counts (without offset!)
                double azRawCounts = azimuthInfo.Raw_Position;
                double altRawCounts = altitudeInfo.Raw_Position;
                
                // [CRITICAL] Get encoder resolutions
                var azConfig = AppSettings.GetAzimuthAxisConfig();
                var altConfig = AppSettings.GetAltitudeAxisConfig();
                
                // Also keep degrees with offset for display
                double azDeg_withOffset = mountInfo.Act_Azimuth_Pos_Raw;
                double altDeg_withOffset = mountInfo.Act_Altitude_Pos_Raw;
                
                // ============================================================
                // [CRITICAL FIX] Following NaritMountLib EXACTLY:
                //
                //   ActualPos = MotorTicksRaw * 360.0 / MotorTicksPerRev;  (DEGREES!)
                //   angle3 = Angle.FromDegs(this.MotionController.Axis[1].ActualPos);
                //   double rads39 = angle3.Rads;  
                //   pointXp10.EncoderToAltAz(rads39, rads40, ref radians9, ref radians10);
                //
                // NaritMountLib sends DEGREES-AS-RADIANS (not normalized!)
                // And ThetaCountsPerRev/PhiCountsPerRev are set BY THE PXP FILE
                // ============================================================
                
                // Convert degrees (with offset) to radians - EXACTLY like NaritMountLib
                // NaritMountLib: Angle.FromDegs(Axis.ActualPos).Rads ? NO normalization!
                var altAngle = Angle.FromDegs(altDeg_withOffset);
                var azAngle = Angle.FromDegs(azDeg_withOffset);
                double altEncRads = altAngle.Rads;
                double azEncRads = azAngle.Rads;
                
                // [CRITICAL FIX] Do NOT normalize to 0-2?!
                // NaritMountLib passes raw degrees?radians directly (can be negative).
                // PointXP internally handles the normalization using ThetaCountsPerRev/PhiCountsPerRev.
                // Normalizing here breaks the model because calibration points were also stored
                // as degrees-with-offset?radians (which can be negative for negative encoder positions).
                
                // Apply corrections using PointXPCore.EncToAltAz directly via Reflection
                // [CRITICAL] PointXP.EncoderToAltAz wrapper returns 0 - bypass it!
                double altCorrectedRad = 0.0;
                double azCorrectedRad = 0.0;
                
                try
                {
                    if (_encToAltAzMethod == null)
                    {
                        _encToAltAzMethod = _pointXP.Core.GetType().GetMethod("EncToAltAz");
                    }
                    
                    if (_encToAltAzMethod != null)
                    {
                        object[] args = new object[] { altEncRads, azEncRads, 0.0, 0.0 };
                        _encToAltAzMethod.Invoke(_pointXP.Core, args);
                        altCorrectedRad = (double)args[2];
                        azCorrectedRad = (double)args[3];
                    }
                }
                catch (Exception reflEx)
                {
                    if (_debugCallCount % 500 == 0)
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] Core.EncToAltAz error: {reflEx.Message}");
                }
                
                // [DEBUG] Log results � every 5000 calls (~100 seconds at 20ms poll rate)
                // Reduced from 500 to 5000 to minimize Debug Output spam
                if (_debugCallCount % 5000 == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] EncoderToAltAz: Az={azDeg_withOffset:F2}� ? {azCorrectedRad * 180.0 / Math.PI:F2}�, Alt={altDeg_withOffset:F2}� ? {altCorrectedRad * 180.0 / Math.PI:F2}�");
                }
                
                // Convert corrected values back to degrees
                var azCorrected = Angle.FromRads(azCorrectedRad);
                var altCorrected = Angle.FromRads(altCorrectedRad);
                
                // [CRITICAL FIX] Output from EncoderToAltAz is in radians.
                // Convert to degrees and rerange to match raw encoder coordinate system.
                // Following NaritMountLib: MathUtil.RerangeDegs(corrected, wrapMin)
                double azCorrectedDeg = azCorrected.Degs;
                double altCorrectedDeg = altCorrected.Degs;
                
                // Rerange corrected azimuth to be within 180� of the raw azimuth.
                // This ensures corrected position stays in the same "wrap" as the raw encoder.
                // Example: Raw=-10� ? Corrected should be near -10�, not near 350�.
                double wrapMin = azDeg_withOffset - 180.0;
                azCorrectedDeg = RerangeDegs(azCorrectedDeg, wrapMin);
                
                // [CRITICAL] Update MountInfo with corrected positions (TRUE sky coordinates)
                mountInfo.Act_Azimuth_Pos_Correct = azCorrectedDeg;
                mountInfo.Act_Altitude_Pos_Correct = altCorrectedDeg;
                mountInfo.UpdateAstronomicalCoordinates();
                
                // [DEBUG] Log every 5000 calls (~100s) to verify corrections
                // Reduced from 500 to minimize Debug Output spam
                if (_debugCallCount % 5000 == 0)
                {
                    double azDelta = azCorrected.Degs - mountInfo.Act_Azimuth_Pos_Raw;
                    double altDelta = altCorrected.Degs - mountInfo.Act_Altitude_Pos_Raw;
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Corrections: Az {mountInfo.Act_Azimuth_Pos_Raw:F2} +({azDelta:F2}) = {azCorrected.Degs:F2}, Alt {mountInfo.Act_Altitude_Pos_Raw:F2} +({altDelta:F2}) = {altCorrected.Degs:F2}");
                }
                
                
                // [DEBUG] Calculate difference for logging - DISABLED
                // double azRawDeg = mountInfo.Act_Azimuth_Pos_Raw;
                // double altRawDeg = mountInfo.Act_Altitude_Pos_Raw;
                // double azDelta = azCorrected.Degs - azRawDeg;
                // double altDelta = altCorrected.Degs - altRawDeg;
                
                // Debug output - DISABLED to reduce log spam
                // Uncomment for debugging pointing model corrections
                _debugCallCount++;
                /*
                if (_debugCallCount % 100 == 0)
                {
                    double azRawDeg = mountInfo.Act_Azimuth_Pos_Raw;
                    double altRawDeg = mountInfo.Act_Altitude_Pos_Raw;
                    double azDelta = azCorrected.Degs - azRawDeg;
                    double altDelta = altCorrected.Degs - altRawDeg;
                    
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Encoder: Az={azRawCounts:F0} cnts, Alt={altRawCounts:F0} cnts");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Raw Deg: Az={azRawDeg:F3}�, Alt={altRawDeg:F3}� (with offset)");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Corrected: Az={azCorrected.Degs:F3}�, Alt={altCorrected.Degs:F3}� (TRUE sky)");
                    System.Diagnostics.Debug.WriteLine($"[PointingModel] Delta: Az={azDelta:F3}�, Alt={altDelta:F3}�");
                    
                    // Check if correction is too large (> 10�)
                    if (Math.Abs(azDelta) > 10.0 || Math.Abs(altDelta) > 10.0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PointingModel] ?? CORRECTION TOO LARGE! ??");
                    }
                }
                */
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ApplyCorrections error: {ex.Message}");
            }
        }
        
        private int _debugCallCount = 0;
        
        /// <summary>
        /// Get the corrected (true sky) Az/Alt position for a given raw encoder Az/Alt.
        /// Uses the same reflection-based EncToAltAz as ApplyCorrections (which works correctly).
        /// Used by CalibrationSkyMap to display solved points at their actual sky position.
        /// </summary>
        /// <param name="rawAzDeg">Raw encoder azimuth (degrees, with offset)</param>
        /// <param name="rawAltDeg">Raw encoder altitude (degrees, with offset)</param>
        /// <returns>Corrected (Az, Alt) in degrees, or null if model not loaded</returns>
        public (double correctedAz, double correctedAlt)? GetCorrectedPosition(double rawAzDeg, double rawAltDeg)
        {
            if (!IsModelLoaded || _pointXP == null)
                return null;
            
            try
            {
                double altEncRads = rawAltDeg * Math.PI / 180.0;
                double azEncRads = rawAzDeg * Math.PI / 180.0;
                
                if (_encToAltAzMethod == null)
                {
                    _encToAltAzMethod = _pointXP.Core.GetType().GetMethod("EncToAltAz");
                }
                
                if (_encToAltAzMethod != null)
                {
                    object[] args = new object[] { altEncRads, azEncRads, 0.0, 0.0 };
                    _encToAltAzMethod.Invoke(_pointXP.Core, args);
                    double altCorrectedRad = (double)args[2];
                    double azCorrectedRad = (double)args[3];
                    
                    double correctedAz = azCorrectedRad * 180.0 / Math.PI;
                    double correctedAlt = altCorrectedRad * 180.0 / Math.PI;
                    
                    // Rerange azimuth to stay near the raw value
                    double wrapMin = rawAzDeg - 180.0;
                    correctedAz = RerangeDegs(correctedAz, wrapMin);
                    
                    return (correctedAz, correctedAlt);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] GetCorrectedPosition error: {ex.Message}");
            }
            
            return null;
        }
        
        
        
        /// <summary>
        /// Calculate azimuth correction for given position (without applying)
        /// Useful for preview or debugging
        /// [NEW] Uses PointXP.EncoderToAltAz() for calculation
        /// </summary>
        public double GetAzimuthCorrection(double azimuthDeg, double altitudeDeg)
        {
            if (!IsModelLoaded || _pointXP == null)
                return 0.0;
            
            try
            {
                var az = Angle.FromDegs(azimuthDeg);
                var alt = Angle.FromDegs(altitudeDeg);
                
                double altCorrected = 0.0;
                double azCorrected = 0.0;
                
                _pointXP.EncoderToAltAz(alt.Rads, az.Rads, ref altCorrected, ref azCorrected);
                
                return azimuthDeg - Angle.FromRads(azCorrected).Degs;
            }
            catch
            {
                return 0.0;
            }
        }
        
        /// <summary>
        /// Calculate altitude correction for given position (without applying)
        /// Useful for preview or debugging
        /// [NEW] Uses PointXP.EncoderToAltAz() for calculation
        /// </summary>
        public double GetAltitudeCorrection(double azimuthDeg, double altitudeDeg)
        {
            if (!IsModelLoaded || _pointXP == null)
                return 0.0;
            
            try
            {
                var az = Angle.FromDegs(azimuthDeg);
                var alt = Angle.FromDegs(altitudeDeg);
                
                double altCorrected = 0.0;
                double azCorrected = 0.0;
                
                _pointXP.EncoderToAltAz(alt.Rads, az.Rads, ref altCorrected, ref azCorrected);
                
                return altitudeDeg - Angle.FromRads(altCorrected).Degs;
            }
            catch
            {
                return 0.0;
            }
        }
        
        // ========================================================================
        // [NEW] TRACKING SUPPORT: Convert RA/Dec to Encoder Position
        // Following NaritMountLib.Mount.MountManager pattern
        // ========================================================================
        
        /// <summary>
        /// Convert RA/Dec (Apparent) to Encoder position with pointing model corrections
        /// This is CRITICAL for accurate tracking - the mount needs ENCODER positions, not SKY positions!
        /// 
        /// Following NaritMountLib pattern:
        ///   pointXp.ApparentToEncoder(lmst.Rads, ra.Rads, dec.Rads, Tsign, ref thetaEnc, ref phiEnc);
        ///   targetAlt = thetaEnc * 360.0 / ThetaCountsPerRev;
        ///   targetAz = phiEnc * 360.0 / PhiCountsPerRev;
        /// </summary>
        /// <param name="raApparent_Rads">Right Ascension (Apparent, radians)</param>
        /// <param name="decApparent_Rads">Declination (Apparent, radians)</param>
        /// <param name="lst_Rads">Local Sidereal Time (radians)</param>
        /// <param name="targetAltDeg">OUTPUT: Encoder altitude position (degrees)</param>
        /// <param name="targetAzDeg">OUTPUT: Encoder azimuth position (degrees)</param>
        /// <returns>True if successful (model loaded), false otherwise</returns>
        public bool ApparentToEncoder(
            double raApparent_Rads, 
            double decApparent_Rads, 
            double lst_Rads,
            out double targetAltDeg, 
            out double targetAzDeg)
        {
            targetAltDeg = 0.0;
            targetAzDeg = 0.0;
            
            if (!IsModelLoaded || _pointXP == null)
            {
                return false;
            }
            
            try
            {
                double thetaEnc = 0.0;  // Altitude encoder (radians/counts)
                double phiEnc = 0.0;    // Azimuth encoder (radians/counts)
                short Tsign = 1;        // Tube sign (1 = normal, -1 = flipped)
                
                // Call PointXP.ApparentToEncoder() - applies all model corrections!
                // This converts celestial coordinates to ENCODER positions
                _pointXP.ApparentToEncoder(
                    lst_Rads,           // Local Sidereal Time (radians)
                    raApparent_Rads,    // RA Apparent (radians)
                    decApparent_Rads,   // Dec Apparent (radians)
                    Tsign,              // Tube sign
                    ref thetaEnc,       // OUT: Altitude encoder
                    ref phiEnc          // OUT: Azimuth encoder
                );
                
                // Convert from encoder counts (radians) to degrees
                // PointXP uses normalized system: CountsPerRev = 2?
                targetAltDeg = thetaEnc * 360.0 / _pointXP.ThetaCountsPerRev;
                targetAzDeg = phiEnc * 360.0 / _pointXP.PhiCountsPerRev;
                
                // [CRITICAL FIX] Rerange azimuth to match mount encoder coordinate system!
                // NaritMountLib: degrees3 = MathUtil.RerangeDegs(degrees2, AzmWrapMinLimit.Degs);
                // Our mount uses raw degrees with offset (can be -300 to +300 or similar).
                // PointXP outputs 0-360 range, but mount encoder may be in a different range.
                // We need to rerange to the CLOSEST equivalent value to the mount's current raw position.
                // This will be done by the caller (SiderealTracking.UnwrapAzimuthToMountRange).
                // But we MUST ensure the output is in a standard 0-360 range first.
                targetAzDeg = RerangeDegs(targetAzDeg, 0.0);  // Normalize to [0, 360)
                
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModel] ApparentToEncoder error: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Convert RA/Dec (Apparent) to Encoder position - PWLib.Astro.RaDec overload
        /// </summary>
        public bool ApparentToEncoder(
            RaDec raDecApparent,
            Angle lst,
            out double targetAltDeg,
            out double targetAzDeg)
        {
            return ApparentToEncoder(
                raDecApparent.Ra.Rads,
                raDecApparent.Dec.Rads,
                lst.Rads,
                out targetAltDeg,
                out targetAzDeg);
        }
        
        /// <summary>
        /// Rerange degrees to [minValue, minValue+360) range
        /// Matching NaritMountLib MathUtil.RerangeDegs()
        /// </summary>
        private static double RerangeDegs(double value, double minValue)
        {
            double range = 360.0;
            while (value < minValue) value += range;
            while (value >= minValue + range) value -= range;
            return value;
        }
        
        /// <summary>
        /// Get Local Sidereal Time from PointXP (for convenience)
        /// </summary>
        public double GetLstRads()
        {
            if (_pointXP == null)
                return 0.0;
            
            try
            {
                _pointXP.LoadDateTimeUTCFromComputer();
                return _pointXP.GetLSTRad();
            }
            catch
            {
                return 0.0;
            }
        }
        
        /// <summary>
        /// Get Julian Day from PointXP (for convenience)
        /// </summary>
        public double GetJulianDay()
        {
            if (_pointXP == null)
                return 0.0;
            
            try
            {
                return _pointXP.GetJulianDay();
            }
            catch
            {
                return 0.0;
            }
        }
    }

    public sealed class LoadedModelCalibrationDiagnostic
    {
        public int Index { get; set; }
        public bool Used { get; set; }
        public double TotalErrorArcsec { get; set; }
        public double RASkyErrorArcsec { get; set; }
        public double DecErrorArcsec { get; set; }
        public double AzimuthDeg { get; set; }
        public double AltitudeDeg { get; set; }
        public double ThetaErrorArcsec { get; set; }
        public double PhiSkyErrorArcsec { get; set; }
        public double PhiEncErrorArcsec { get; set; }
    }
}
