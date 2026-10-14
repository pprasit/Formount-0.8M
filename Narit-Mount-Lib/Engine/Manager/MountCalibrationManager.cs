using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Tracking;
using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.PointingModel;
using ForkMount_WPF.Engine.Utility;
using ForkMount_WPF.Engine.PlateSolving;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Calibration state enum
    /// </summary>
    public enum CalibrationState
    {
        Idle,
        Slewing,
        CollectingSamples,
        WaitingForStability,
        Stabilizing,
        Capturing,
        Tracking,
        Processing,
        Completed,
        Paused
    }

    /// <summary>
    /// Manages mount calibration process (separate from UI)
    /// - Point management
    /// - Tracking control
    /// - Image capture queue
    /// - RMS calculation
    /// 
    /// UI polls this manager via timer instead of using Invoke
    /// </summary>
    public class MountCalibrationManager
    {
        // Dependencies
        private CentralizedPollingManager _pollingManager;
        private SiderealTracking _trackingController;
        private ObserverLocation _observerLocation;
        private ICameraProvider _camera;
        private AzimuthAxisConfig _azimuthConfig;
        private AltitudeAxisConfig _altitudeConfig;
        
        // Plate solving
        private PlateSolverManager _plateSolver;
        private string _fitsDirectory;
        private readonly SemaphoreSlim _solveSemaphore = new SemaphoreSlim(1, 1); // Serialize plate solves!
        
        // [NEW] Progressive pointing model file
        private string _progressiveModelPath;
        private string _sessionFolder;
        private readonly object _fileWriteLock = new object();
        
        // Calibration data
        private ObservableCollection<CalibrationPoint> _calibrationPoints;
        private int _currentPointIndex = 0;
        private bool _isRunning = false;

        // State tracking
        private CalibrationState _state = CalibrationState.Idle;
        private DateTime _trackingStartTime;
        private DateTime? _rmsStabilityStartTime = null;
        private bool _imageAddedForPoint = false;
        private volatile bool _exposureCompleted = false;
        private volatile bool _isCapturingImage = false;
        
        // [FIX] Track previous point that is still being solved (pipeline: slew while solving)
        private int _pendingSolvePointIndex = -1;
        private DateTime _pendingSolveStartTime;
        
        // [NEW] Manual point addition flag
        private bool _applyFirstPointOffset = false;  // Apply offset if this is first manual point
        private bool _isManualAddMode = false;       // Skip completion dialog for manual add
        private bool _completionDialogRequested = false;
        private bool _completionDialogShown = false;
        
        // [NEW] Cached target positions (to prevent recalculation during slew)
        private double _cachedTargetAzimuth = 0.0;   // Unwrapped target azimuth
        private double _cachedTargetAltitude = 0.0;  // Target altitude
        private double _cachedTargetRA = 0.0;        // RA (hours) — computed ONCE from Alt/Az
        private double _cachedTargetDec = 0.0;       // Dec (degrees) — computed ONCE from Alt/Az
        private bool _targetCached = false;          // Is target cached for current point?

        // Configuration (loaded from AppSettings)
        private int _trackingDurationSeconds;
        private double _arrivalThresholdDeg;
        private double _rmsStabilityThresholdArcsec;
        private int _rmsStabilityDurationMs;
        private double _plateSolverSearchRadiusDeg;
        private int _minimumTrackingSamples;
        private int _extraSettleTimeMs;
        private int _postExposureBufferMs;
        private double _rmsTimeoutSeconds;
        private PointingModelBackendMode _pointingModelBackendMode = PointingModelBackendMode.LegacyPxpReload;
        private ExperimentalPointingModelMetrics _experimentalMetrics;
        private DateTime? _runStartedAtUtc;
        private DateTime _lastCheckpointSaveUtc = DateTime.MinValue;
        private string _modelPathBeforeCalibration;
        private const int CHECKPOINT_INTERVAL_SECONDS = 30;

        // Image processing queue
        private System.Collections.Concurrent.ConcurrentQueue<CalibrationImageData> _imageQueue = new();
        
        // [NEW] Event for plate solve completion (UI can subscribe to update RMS display)
        public event Action<CalibrationPoint, PlateSolveResult, double> OnSolveCompleted;
        public event Action CalibrationRestartRequested;

        // Public properties for UI polling
        public CalibrationState State => _state;
        public int CurrentPointIndex => _currentPointIndex;
        public int PendingSolvePointIndex => _pendingSolvePointIndex;
        public int TotalPoints => _calibrationPoints?.Count ?? 0;
        public int CompletedPoints => _calibrationPoints?.Count(p => p.IsCompleted) ?? 0;
        public int SolvedPoints => _calibrationPoints?.Count(p => p.IsSolved) ?? 0;
        public int FailedPoints => _calibrationPoints?.Count(p => p.SolveFailed) ?? 0;
        public int ProcessedPoints => (_calibrationPoints?.Count(p => p.IsSolved || p.SolveFailed)) ?? 0;
        public double ProgressPercentage => TotalPoints > 0 ? (CompletedPoints / (double)TotalPoints) * 100.0 : 0.0;
        public bool IsRunning => _isRunning;
        public ObservableCollection<CalibrationPoint> CalibrationPoints => _calibrationPoints;  // [NEW] For deferred star rendering
        public PointingModelBackendMode PointingModelBackendMode => _pointingModelBackendMode;
        public ExperimentalPointingModelMetrics ExperimentalMetrics => _experimentalMetrics;
        public string ModelPathBeforeCalibration => _modelPathBeforeCalibration;
        public string AutosaveModelPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData", "PointingModel.autosave.pxp");
        public string RuntimeReadySnapshotPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData", "PointingModel.latest-ready.pxp");
        public string DefaultModelPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData", "Default_Model.pxp");

        public CalibrationPoint CurrentPoint => (_currentPointIndex >= 0 && _currentPointIndex < TotalPoints) 
            ? _calibrationPoints[_currentPointIndex] 
            : null;

        public DateTime? EstimatedCompletionLocalTime
        {
            get
            {
                if (!_runStartedAtUtc.HasValue || !_isRunning || TotalPoints <= 0 || ProcessedPoints <= 0)
                {
                    return null;
                }

                int remainingPoints = Math.Max(0, TotalPoints - ProcessedPoints);
                if (remainingPoints == 0)
                {
                    return DateTime.Now;
                }

                double elapsedSeconds = (DateTime.UtcNow - _runStartedAtUtc.Value).TotalSeconds;
                if (elapsedSeconds <= 0)
                {
                    return null;
                }

                double secondsPerPoint = elapsedSeconds / ProcessedPoints;
                return DateTime.Now.AddSeconds(secondsPerPoint * remainingPoints);
            }
        }

        public MountCalibrationManager(
            CentralizedPollingManager pollingManager,
            ObserverLocation observerLocation,
            AzimuthAxisConfig azimuthConfig,
            AltitudeAxisConfig altitudeConfig)
        {
            _pollingManager = pollingManager ?? throw new ArgumentNullException(nameof(pollingManager));
            _observerLocation = observerLocation ?? throw new ArgumentNullException(nameof(observerLocation));
            _azimuthConfig = azimuthConfig ?? throw new ArgumentNullException(nameof(azimuthConfig));
            _altitudeConfig = altitudeConfig ?? throw new ArgumentNullException(nameof(altitudeConfig));

            _calibrationPoints = new ObservableCollection<CalibrationPoint>();
            _trackingController = new SiderealTracking(_pollingManager);

            LoadCalibrationSettings();
            _plateSolver = new PlateSolverManager();
            EnsurePlateSolverInitialized();
        }

        /// <summary>
        /// Initialize plate solver once during manager construction.
        /// Without this, every solve attempt returns "Plate solver not initialized".
        /// </summary>
        private void EnsurePlateSolverInitialized()
        {
            try
            {
                bool initialized = _plateSolver.Initialize(string.Empty);
                if (!initialized)
                {
                    System.Diagnostics.Debug.WriteLine("[CALIBRATION] ⚠️ Plate solver initialization failed");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Plate solver initialization exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Load calibration settings from AppSettings.
        /// </summary>
        private void LoadCalibrationSettings()
        {
            try
            {
                ApplyCalibrationSettings(AppSettings.GetMountCalibrationSettings());
                CapSearchRadiusToFOV();
            }
            catch
            {
                ApplyCalibrationSettings(new MountCalibrationSettings());
            }
        }

        private void ApplyCalibrationSettings(MountCalibrationSettings settings)
        {
            _trackingDurationSeconds = settings.TrackingDuration_Seconds;
            _arrivalThresholdDeg = settings.ArrivalThreshold_Deg;
            _rmsStabilityThresholdArcsec = settings.RmsStabilityThreshold_Arcsec;
            _rmsStabilityDurationMs = settings.RmsStabilityDuration_Ms;
            _plateSolverSearchRadiusDeg = settings.PlateSolverSearchRadius_Deg;
            _minimumTrackingSamples = settings.MinimumTrackingSamples > 0 ? settings.MinimumTrackingSamples : 6;
            _extraSettleTimeMs = settings.ExtraSettleTime_Ms >= 0 ? settings.ExtraSettleTime_Ms : 750;
            _postExposureBufferMs = settings.PostExposureBuffer_Ms >= 0 ? settings.PostExposureBuffer_Ms : 500;
            _rmsTimeoutSeconds = settings.RmsTimeout_Seconds > 0 ? settings.RmsTimeout_Seconds : 20.0;
            _pointingModelBackendMode = ParseBackendMode(settings.PointingModelBackendMode);
        }

        private static PointingModelBackendMode ParseBackendMode(string rawMode)
        {
            if (Enum.TryParse(rawMode, true, out PointingModelBackendMode parsedMode))
            {
                return parsedMode;
            }

            return PointingModelBackendMode.LegacyPxpReload;
        }

        public void SetPointingModelBackendMode(PointingModelBackendMode backendMode)
        {
            _pointingModelBackendMode = backendMode;

            if (backendMode == PointingModelBackendMode.ExperimentalCoreDirect)
            {
                RebuildExperimentalPointingModel();
            }
            else
            {
                _experimentalMetrics = null;
                App.PointingModelManager?.RestoreLegacyModel();
            }
        }

        /// <summary>
        /// Cap plate solver search radius to a practical value based on camera FOV.
        /// </summary>
        private void CapSearchRadiusToFOV()
        {
            try
            {
                var cameraSettings = AppSettings.GetCameraSettings();
                double plateScale = cameraSettings.PlateScale_ArcsecPerPixel;
                double focalLength = cameraSettings.FocalLength_mm;

                if (plateScale <= 0 && focalLength > 0)
                {
                    plateScale = (3.76 / focalLength) * 206.265;
                }

                if (plateScale <= 0)
                {
                    return;
                }

                int sensorWidth = cameraSettings.Width > 0 ? cameraSettings.Width : 3096;
                int sensorHeight = cameraSettings.Height > 0 ? cameraSettings.Height : 2080;
                int binning = cameraSettings.Binning > 0 ? cameraSettings.Binning : 1;

                double fovWidthDeg = (sensorWidth * plateScale * binning) / 3600.0;
                double fovHeightDeg = (sensorHeight * plateScale * binning) / 3600.0;
                double maxFovDeg = Math.Max(fovWidthDeg, fovHeightDeg);
                double maxSearchRadius = Math.Max(10.0, maxFovDeg * 3.0);

                if (_plateSolverSearchRadiusDeg > maxSearchRadius && maxSearchRadius > 0.5)
                {
                    _plateSolverSearchRadiusDeg = maxSearchRadius;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] CapSearchRadiusToFOV error: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate a session folder name in DDMMYYYY-NN format.
        /// </summary>
        private string GenerateSessionFolderName(string baseFitsDir)
        {
            DateTime now = DateTime.Now;
            string datePrefix = now.ToString("ddMMyyyy");

            var existingFolders = Directory.GetDirectories(baseFitsDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) && name.StartsWith(datePrefix + "-"))
                .ToList();

            int nextIndex = 1;
            foreach (var folder in existingFolders)
            {
                var parts = folder.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[1], out int index))
                {
                    nextIndex = Math.Max(nextIndex, index + 1);
                }
            }

            return $"{datePrefix}-{nextIndex:D2}";
        }

        public void SetGains(PDControllerGains azGains, PDControllerGains altGains)
        {
            _trackingController?.SetGains(azGains, altGains);
        }

        public void InitializeCamera(ICameraProvider camera)
        {
            _camera = camera;
        }

        private void LogInfo(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[CALIBRATION] {message}");
        }

        private void LogError(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[CALIBRATION ERROR] {message}");
        }

        /// <summary>
        /// Create calibration grid
        /// [FIXED] Preserve existing completed points and only add new pending points
        /// </summary>
        public void CreateCalibrationGrid(int azimuthPoints, int altitudePoints, double minAltitude = 30.0, double maxAltitude = 85.0)
        {
            // [CRITICAL FIX] Preserve completed points before creating new grid
            var completedPoints = _calibrationPoints
                .Where(p => p.IsCompleted || p.IsSolved)
                .ToList();
            
            // Create new grid
            var newGridPoints = new List<CalibrationPoint>();
            
            double azStep = 360.0 / azimuthPoints;
            double altRange = Math.Max(0.0, maxAltitude - minAltitude);

            int index = 1;
            for (int azIdx = 0; azIdx < azimuthPoints; azIdx++)
            {
                double azimuth = azIdx * azStep;
                for (int altIdx = 0; altIdx < altitudePoints; altIdx++)
                {
                    double altitude;
                    if (altitudePoints <= 1)
                    {
                        altitude = minAltitude;
                    }
                    else
                    {
                        double normalizedAltIndex = altIdx / (double)(altitudePoints - 1);
                        altitude = minAltitude + (normalizedAltIndex * altRange);
                    }
                    
                    // Check if this position is already completed (within 5 degree tolerance)
                    bool isAlreadyCompleted = completedPoints.Any(cp => 
                        Math.Abs(cp.TargetAzimuth - azimuth) < 5.0 && 
                        Math.Abs(cp.TargetAltitude - altitude) < 5.0);
                    
                    if (!isAlreadyCompleted)
                    {
                        newGridPoints.Add(new CalibrationPoint
                        {
                            Index = index++,
                            TargetAzimuth = azimuth,
                            TargetAltitude = altitude,
                            IsCompleted = false
                        });
                    }
                }
            }
            
            // Merge: Keep completed points + add new pending points
            _calibrationPoints.Clear();
            
            // Re-add completed points first (preserving their solved data)
            int finalIndex = 1;
            foreach (var completed in completedPoints)
            {
                completed.Index = finalIndex++;
                _calibrationPoints.Add(completed);
            }
            
            // Add new pending points
            foreach (var newPoint in newGridPoints)
            {
                newPoint.Index = finalIndex++;
                _calibrationPoints.Add(newPoint);
            }
            
            // [CRITICAL] Set current index to first pending (non-completed) point
            _currentPointIndex = 0;
            for (int i = 0; i < _calibrationPoints.Count; i++)
            {
                if (!_calibrationPoints[i].IsCompleted && !_calibrationPoints[i].IsSolved)
                {
                    _currentPointIndex = i;
                    break;
                }
            }
            
            LogInfo($"[CreateGrid] Created grid: {completedPoints.Count} completed + {newGridPoints.Count} new = {_calibrationPoints.Count} total points");
            LogInfo($"[CreateGrid] Will start from point index {_currentPointIndex} (#{_calibrationPoints[_currentPointIndex].Index})");
        }

        private int FindFirstPendingPointIndex()
        {
            for (int i = 0; i < _calibrationPoints.Count; i++)
            {
                if (!_calibrationPoints[i].IsSolved)
                {
                    return i;
                }
            }

            return _calibrationPoints.Count > 0 ? _calibrationPoints.Count - 1 : 0;
        }
        
        /// <summary>
        /// [NEW] Add single calibration point (for manual point addition)
        /// </summary>
        public void AddCalibrationPoint(double azimuth, double altitude)
        {
            int newIndex = _calibrationPoints.Count > 0 
                ? _calibrationPoints.Max(p => p.Index) + 1 
                : 1;
            
            var newPoint = new CalibrationPoint
            {
                Index = newIndex,
                TargetAzimuth = azimuth,
                TargetAltitude = altitude,
                IsCompleted = false
            };
            
            _calibrationPoints.Add(newPoint);
            if (!_isRunning)
            {
                _currentPointIndex = FindFirstPendingPointIndex();
            }
            LogInfo($"Added calibration point #{newIndex}: Az={azimuth:F1}°, Alt={altitude:F1}°");
        }
        
        /// <summary>
        /// [NEW] Delete calibration point by index
        /// </summary>
        public bool DeleteCalibrationPoint(int pointIndex)
        {
            var point = _calibrationPoints.FirstOrDefault(p => p.Index == pointIndex);
            if (point == null)
            {
                LogError($"Point #{pointIndex} not found");
                return false;
            }
            
            // Don't allow deletion during active calibration run
            if (_isRunning)
            {
                LogError($"Cannot delete point #{pointIndex} - calibration is running. Stop first.");
                return false;
            }
            
            _calibrationPoints.Remove(point);
            
            // Update indexes of points after deleted one
            UpdatePointIndexes();
            
            // Reset current point index if needed
            if (_currentPointIndex >= _calibrationPoints.Count)
            {
                _currentPointIndex = Math.Max(0, _calibrationPoints.Count - 1);
            }
            
            // Reset state flags to prevent stuck state
            _targetCached = false;
            _imageAddedForPoint = false;
            _exposureCompleted = false;
            
            LogInfo($"Deleted calibration point #{pointIndex}, {_calibrationPoints.Count} points remaining");
            return true;
        }
        
        /// <summary>
        /// [NEW] Clear all calibration points
        /// [FIX] Also reset all state flags for proper restart
        /// </summary>
        public void ClearCalibrationPoints()
        {
            if (_isRunning)
            {
                LogError("Cannot clear points during calibration");
                return;
            }
            
            _calibrationPoints.Clear();
            _currentPointIndex = 0;
            _completionDialogRequested = false;
            _completionDialogShown = false;
            
            // [CRITICAL FIX] Reset all state flags when clearing points
            // This ensures next Start() will work correctly
            _targetCached = false;
            _imageAddedForPoint = false;
            _exposureCompleted = false;
            _rmsStabilityStartTime = null;
            _isCapturingImage = false;
            _pendingSolvePointIndex = -1;
            _state = CalibrationState.Idle;
            
            LogInfo("Cleared all calibration points and reset state flags");
        }
        
        /// <summary>
        /// [NEW] Clear all FITS files in the current session folder
        /// Call this when restarting calibration to clean up old files
        /// </summary>
        public void ClearSessionFitsFiles()
        {
            try
            {
                if (string.IsNullOrEmpty(_fitsDirectory))
                {
                    LogInfo("No FITS directory set - nothing to clear");
                    return;
                }
                
                if (!Directory.Exists(_fitsDirectory))
                {
                    LogInfo($"FITS directory does not exist: {_fitsDirectory}");
                    return;
                }
                
                // Get all FITS files in the session folder
                var fitsFiles = Directory.GetFiles(_fitsDirectory, "*.fits");
                
                if (fitsFiles.Length == 0)
                {
                    LogInfo("No FITS files to delete");
                    return;
                }
                
                int deletedCount = 0;
                int errorCount = 0;
                
                foreach (var file in fitsFiles)
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to delete {Path.GetFileName(file)}: {ex.Message}");
                        errorCount++;
                    }
                }
                
                LogInfo($"✅ Deleted {deletedCount} FITS file(s) from {_sessionFolder}");
                if (errorCount > 0)
                {
                    LogError($"⚠️ Failed to delete {errorCount} file(s)");
                }
                
                // Also clear the progressive model file for this session
                if (!string.IsNullOrEmpty(_progressiveModelPath) && File.Exists(_progressiveModelPath))
                {
                    try
                    {
                        File.Delete(_progressiveModelPath);
                        LogInfo($"✅ Deleted session model file: {Path.GetFileName(_progressiveModelPath)}");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to delete model file: {ex.Message}");
                    }
                }
                
                // Reset session folder to force new folder on next Start()
                _fitsDirectory = null;
                _sessionFolder = null;
                _progressiveModelPath = null;
            }
            catch (Exception ex)
            {
                LogError($"Error clearing FITS files: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset calibration state completely and create a brand-new blank progressive pointing model file.
        /// Returns the path of the new empty session file when successful.
        /// </summary>
        public string PrepareFreshPointingModelSession(bool deleteExistingSessionFiles = true)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Cannot prepare a new pointing model while calibration is running.");
            }

            if (deleteExistingSessionFiles)
            {
                ClearSessionFitsFiles();
            }
            else
            {
                _fitsDirectory = null;
                _sessionFolder = null;
                _progressiveModelPath = null;
            }

            ClearCalibrationPoints();

            _completionDialogRequested = false;
            _completionDialogShown = false;
            _runStartedAtUtc = null;
            _currentPointIndex = 0;

            InitializeFolders();
            InitializeProgressiveModelFile();

            LogInfo($"✅ Fresh pointing model session prepared: {Path.GetFileName(_progressiveModelPath)}");
            return _progressiveModelPath;
        }
        
        /// <summary>
        /// Get current session FITS directory path (for UI display)
        /// </summary>
        public string GetSessionFitsDirectory()
        {
            return _fitsDirectory;
        }
        
        /// <summary>
        /// [NEW] Update point indexes after deletion
        /// </summary>
        private void UpdatePointIndexes()
        {
            int newIndex = 1;
            foreach (var point in _calibrationPoints)
            {
                point.Index = newIndex++;
            }
        }

        /// <summary>
        /// Get all calibration points (for UI display)
        /// </summary>
        public IEnumerable<CalibrationPoint> GetCalibrationPoints()
        {
            return _calibrationPoints.ToList();
        }
        
        /// <summary>
        /// Load existing calibration points from a .pxp model file as base for new calibration.
        /// Points are imported as already-solved so they contribute to the new model
        /// without needing re-capture. New points can then be added on top.
        /// </summary>
        /// <param name="pxpFilePath">Path to existing .pxp file, or null to auto-detect latest</param>
        /// <returns>Number of points loaded, 0 if none found</returns>
        public int LoadExistingModelPoints(string pxpFilePath = null)
        {
            if (_isRunning)
            {
                LogError("Cannot load points while calibration is running");
                return 0;
            }
            
            try
            {
                // Auto-detect latest model file if not specified
                if (string.IsNullOrEmpty(pxpFilePath))
                {
                    string calibrationDataDir = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "CalibrationData");
                    
                    if (!Directory.Exists(calibrationDataDir))
                    {
                        LogError("CalibrationData directory not found");
                        return 0;
                    }
                    
                    // Search for .pxp files (newest first)
                    var pxpFiles = Directory.GetFiles(calibrationDataDir, "PointingModel*.pxp")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .ToList();
                    
                    if (pxpFiles.Count == 0)
                    {
                        LogInfo("No existing .pxp model files found");
                        return 0;
                    }
                    
                    pxpFilePath = pxpFiles[0];
                    LogInfo($"Auto-detected model: {System.IO.Path.GetFileName(pxpFilePath)}");
                }
                
                if (!File.Exists(pxpFilePath))
                {
                    LogError($"Model file not found: {pxpFilePath}");
                    return 0;
                }
                
                // Parse the pxp file
                var existingPoints = PointingModel.PlaneWaveModelFile.LoadPlaneWaveFormat(pxpFilePath);
                
                if (existingPoints.Count == 0)
                {
                    LogInfo("Model file contains 0 calibration points");
                    return 0;
                }
                
                // Convert PlaneWaveCalibrationPoint → CalibrationPoint (as already-solved)
                int startIndex = _calibrationPoints.Count > 0 
                    ? _calibrationPoints.Max(p => p.Index) + 1 
                    : 1;
                
                int loadedCount = 0;
                var diagnostics = PointingModelManager.GetCalibrationDiagnosticsFromFile(pxpFilePath)
                    .OrderBy(d => d.Index)
                    .ToList();
                
                foreach (var existingPt in existingPoints)
                {
                    var diagnostic = diagnostics != null && loadedCount < diagnostics.Count
                        ? diagnostics[loadedCount]
                        : null;

                    var calPoint = new CalibrationPoint
                    {
                        Index = startIndex++,
                        TargetAzimuth = existingPt.Azimuth_Deg,
                        TargetAltitude = existingPt.Altitude_Deg,
                        CurrentAzimuth = existingPt.Azimuth_Deg,
                        CurrentAltitude = existingPt.Altitude_Deg,
                        
                        // Mark as already solved
                        IsCompleted = true,
                        IsSolved = true,
                        SolveFailed = false,
                        IsIncludedInModel = true,
                        
                        // Store coordinates (J2000 radians from pxp)
                        ExpectedRa_J2000_Rads = existingPt.RA_Rads,
                        ExpectedDec_J2000_Rads = existingPt.Dec_Rads,
                        SolvedRa_J2000_Rads = existingPt.RA_Rads,
                        SolvedDec_J2000_Rads = existingPt.Dec_Rads,
                        
                        // Store apparent coordinates (same as J2000 for error calc)
                        ExpectedRa_App_Rads = existingPt.RA_Rads,
                        ExpectedDec_App_Rads = existingPt.Dec_Rads,
                        
                        // Encoder data (from pxp ThetaEnc/PhiEnc)
                        // Convert radians back to raw counts for model building
                        CurrentAzimuthRawCounts = (existingPt.PhiEnc_Rads * 180.0 / Math.PI - _azimuthConfig.Position_Offset_Deg) 
                            / 360.0 * _azimuthConfig.Encoder_Resolution,
                        CurrentAltitudeRawCounts = (existingPt.ThetaEnc_Rads * 180.0 / Math.PI - _altitudeConfig.Position_Offset_Deg) 
                            / 360.0 * _altitudeConfig.Encoder_Resolution,
                        
                        // Time data
                        CaptureTime = existingPt.CaptureTime,
                        JulianDate = existingPt.JulianDate,
                        
                        // Recompute the residuals from the loaded PointXP model.
                        RaError_Arcsec = diagnostic?.RASkyErrorArcsec,
                        DecError_Arcsec = diagnostic?.DecErrorArcsec,
                        // .pxp does not store matched-star counts from the original plate solve.
                        // Keep this as unknown/0 instead of showing a fake repeated value.
                        NumMatchedStars = 0,
                    };
                    
                    _calibrationPoints.Add(calPoint);
                    loadedCount++;
                }
                
                // Set current index to first unsolved point (skip imported points)
                _currentPointIndex = 0;
                for (int i = 0; i < _calibrationPoints.Count; i++)
                {
                    if (!_calibrationPoints[i].IsSolved)
                    {
                        _currentPointIndex = i;
                        break;
                    }
                }
                
                LogInfo($"✅ Loaded {loadedCount} existing points from {System.IO.Path.GetFileName(pxpFilePath)}");
                LogInfo($"   Total points: {_calibrationPoints.Count} ({_calibrationPoints.Count(p => p.IsSolved)} solved + {_calibrationPoints.Count(p => !p.IsSolved)} pending)");
                
                return loadedCount;
            }
            catch (Exception ex)
            {
                LogError($"Failed to load existing model: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// [NEW] Take single test image at current mount position (from Narit)
        /// Useful for testing plate solving and camera settings
        /// </summary>
        public async Task TakeSingleImageAtCurrentPosition()
        {
            if (!_pollingManager.IsPolling)
            {
                LogError("Mount not connected - cannot capture");
                return;
            }
            
            if (_isRunning)
            {
                LogError("Calibration already running - cannot take test image");
                return;
            }
            
            // Get current mount position
            var azInfo = _pollingManager.AzimuthInfo;
            var altInfo = _pollingManager.AltitudeInfo;
            
            if (azInfo == null || altInfo == null)
            {
                LogError("Cannot read mount position");
                return;
            }
            
            // [P1 FIX] Use CORRECTED position (sky coordinates) instead of Raw
            // Corrected position includes pointing model correction = true sky position
            double currentAz = _pollingManager.MountInfo?.Act_Azimuth_Pos_Correct
                ?? ((azInfo.Raw_Position / (double)_azimuthConfig.Encoder_Resolution) * 360.0 + _azimuthConfig.Position_Offset_Deg);
            double currentAlt = _pollingManager.MountInfo?.Act_Altitude_Pos_Correct
                ?? ((altInfo.Raw_Position / (double)_altitudeConfig.Encoder_Resolution) * 360.0 + _altitudeConfig.Position_Offset_Deg);
            
            LogInfo($"📸 Taking test image at current position: Az={currentAz:F2}°, Alt={currentAlt:F2}°");
            
            // Clear existing points and add current position
            ClearCalibrationPoints();
            AddCalibrationPoint(currentAz, currentAlt);
            
            // Initialize folders if needed
            if (string.IsNullOrEmpty(_fitsDirectory) || string.IsNullOrEmpty(_sessionFolder))
            {
                InitializeFolders();
            }

            CaptureModelPathBeforeCalibration();
            
            // Initialize progressive model file
            InitializeProgressiveModelFile();
            
            // Start calibration (will capture 1 point and stop)
            Start();
            
            LogInfo("Test capture started - will stop after 1 point");
        }

        /// <summary>
        /// [SIMPLIFIED] Add manual calibration point at current mount position
        /// - Read current Az/Alt (passed from UI)
        /// - Add point to calibration list  
        /// - Start capture & solve process
        /// - Does NOT touch tracking - leaves tracking as-is for other systems to manage
        /// </summary>
        public async Task AddManualCalibrationPoint(double currentAz, double currentAlt)
        {
            if (!_pollingManager.IsPolling)
            {
                LogError("Mount not connected");
                return;
            }
            
            if (_isRunning)
            {
                LogError("Calibration already running - cannot add manual point");
                return;
            }
            
            LogInfo($"[MANUAL ADD] Adding point at Az={currentAz:F2}°, Alt={currentAlt:F2}°");
            
            // [SIMPLIFIED] Don't touch tracking at all!
            // User is responsible for ensuring tracking is active before clicking Add Point.
            // This prevents race conditions and mount oscillation.
            _isManualAddMode = true;
            
            // Add point to calibration list
            AddCalibrationPoint(currentAz, currentAlt);
            
            // Initialize folders if needed
            if (string.IsNullOrEmpty(_fitsDirectory) || string.IsNullOrEmpty(_sessionFolder))
            {
                InitializeFolders();
            }
            
            // Initialize or load existing model file
            bool isFirstPoint = _calibrationPoints.Count == 1;
            
            if (isFirstPoint)
            {
                LogInfo($"[MANUAL ADD] First point - will create new model file");
                InitializeProgressiveModelFile();
            }
            else
            {
                LogInfo($"[MANUAL ADD] Adding to existing model ({_calibrationPoints.Count} points)");
            }
            
            // Start calibration (will capture this point)
            // Note: Start() will NOT stop external tracking anymore for manual mode
            StartManualCapture();
            
            LogInfo($"[MANUAL ADD] Point addition started - will capture and solve");
            
            // If first point, apply offset after solve completes
            _applyFirstPointOffset = isFirstPoint;
        }
        
        /// <summary>
        /// [NEW] Start manual capture mode - does NOT stop external tracking
        /// </summary>
        private void StartManualCapture()
        {
            if (_calibrationPoints.Count == 0)
            {
                return;
            }

            // Initialize folders if not yet done
            if (string.IsNullOrEmpty(_fitsDirectory) || string.IsNullOrEmpty(_sessionFolder))
            {
                InitializeFolders();
            }

            // Reset state flags
            _targetCached = false;
            _imageAddedForPoint = false;
            _exposureCompleted = false;
            _rmsStabilityStartTime = null;
            _isCapturingImage = false;
            
            // Find the last point (the one we just added)
            _currentPointIndex = _calibrationPoints.Count - 1;
            
            var currentPoint = _calibrationPoints[_currentPointIndex];
            currentPoint.IsSlewing = false;
            currentPoint.IsTracking = true;  // Assume tracking is already active
            currentPoint.IsCapturing = false;
            currentPoint.IsSolving = false;

            // [HYBRID] Use CORRECTED when model loaded (same as HandleArrivedAtTarget)
            bool hasModel = App.PointingModelManager?.IsModelLoaded == true;
            if (hasModel && _pollingManager?.MountInfo != null)
            {
                _trackingController.UseRawPosition = false;
            }
            else
            {
                _trackingController.UseRawPosition = true;
            }
            
            _isRunning = true;
            _runStartedAtUtc ??= DateTime.UtcNow;
            _completionDialogRequested = false;
            _completionDialogShown = false;
            _state = CalibrationState.Tracking;  // Skip slewing - go straight to tracking/stability check
            
            LogInfo($"[MANUAL CAPTURE] Started - waiting for stability at point #{currentPoint.Index}");
            
            // Initialize progressive pointing model file
            InitializeProgressiveModelFile();
        }
        
        /// <summary>
        /// Get current calibration point count
        /// </summary>
        public async Task<int> GetCalibrationPointCount()
        {
            return await Task.FromResult(_calibrationPoints.Count);
        }

        /// <summary>
        /// Start calibration process
        /// [NEW] Initialize folders on first Start() call only
        /// [FIX] Reset all state flags to ensure proper operation after Restart
        /// [FIX] Stop external tracking before starting calibration to prevent race conditions
        /// </summary>
        public void Start()
        {
            _completionDialogRequested = false;
            _completionDialogShown = false;

            if (_calibrationPoints.Count == 0)
            {
                return;
            }

            _currentPointIndex = FindFirstPendingPointIndex();
            if (_calibrationPoints[_currentPointIndex].IsSolved)
            {
                LogInfo("[CALIBRATION] All calibration points are already solved. Add pending points before starting.");
                return;
            }

            // [CRITICAL FIX] Stop any external tracking before starting calibration!
            // This prevents race conditions where external tracking fights with calibration slewing
            var trackingManager = TrackingManager.Instance;
            if (trackingManager.IsTracking)
            {
                LogInfo($"[CALIBRATION] ⚠️ External tracking detected: {trackingManager.CurrentObjectType}");
                LogInfo($"[CALIBRATION] 🛑 Stopping external tracking before calibration...");
                trackingManager.ForceStopAllTracking();
                
                // Also stop our local tracking controller if running
                _trackingController?.StopTracking();
                
                // Brief pause to allow tracking to fully stop
                System.Threading.Thread.Sleep(100);
                LogInfo($"[CALIBRATION] ✅ External tracking stopped");
            }

            // [NEW] Initialize folders if not yet done
            if (string.IsNullOrEmpty(_fitsDirectory) || string.IsNullOrEmpty(_sessionFolder))
            {
                InitializeFolders();
            }

            // [CRITICAL FIX] Reset all state flags to ensure proper slewing/tracking
            // These must be reset when starting fresh (after Restart) or resuming
            _targetCached = false;           // Force recalculation of target position
            _imageAddedForPoint = false;     // Allow image capture for first point
            _exposureCompleted = false;      // Allow exposure for first point
            _rmsStabilityStartTime = null;   // Reset stability timer
            _isCapturingImage = false;       // Reset capture flag
            _pendingSolvePointIndex = -1;    // No pending solve from previous point
            
            // [FIX] Also reset current point's state flags if it exists
            if (_currentPointIndex >= 0 && _currentPointIndex < _calibrationPoints.Count)
            {
                var currentPoint = _calibrationPoints[_currentPointIndex];
                currentPoint.IsSlewing = false;
                currentPoint.IsTracking = false;
                currentPoint.IsCapturing = false;
                currentPoint.IsSolving = false;
            }

            // [HYBRID FIX] Start with RAW for slewing, switch to CORRECTED on arrival
            // When switching: recalculate target from corrected position → no position jump
            // Slew: RAW (stable, no model reload impact)
            // Track: CORRECTED (accurate sidereal rate, rounder stars) with smooth transition
            _trackingController.UseRawPosition = true;
            
            _isRunning = true;
            _runStartedAtUtc ??= DateTime.UtcNow;
            _state = CalibrationState.Slewing;
            
            System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ▶️ Start() called - starting from point {_currentPointIndex + 1}/{_calibrationPoints.Count}");
            System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    HYBRID mode: RAW for slew → CORRECTED for tracking (target recalculated on switch)");
            System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    State flags reset: _targetCached=false, _imageAddedForPoint=false");
            
            // [NEW] Initialize progressive pointing model file
            InitializeProgressiveModelFile();
            
        }
        
        /// <summary>
        /// Initialize FITS folder and pointing model file path
        /// Called only once on first Start()
        /// </summary>
        private void InitializeFolders()
        {
            // Setup FITS directory with session-based folder naming
            // Format: CalibrationData/FITS/DDMMYYYY-NN/ where NN increments per session on same day
            string baseFitsDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "CalibrationData",
                "FITS"
            );
            
            // Create base directory if not exists
            if (!Directory.Exists(baseFitsDir))
            {
                Directory.CreateDirectory(baseFitsDir);
            }
            
            // Generate session folder name: DDMMYYYY-01, DDMMYYYY-02, etc.
            string sessionFolder = GenerateSessionFolderName(baseFitsDir);
            _fitsDirectory = Path.Combine(baseFitsDir, sessionFolder);
            _sessionFolder = sessionFolder;
            
            // Create session directory only if not exists
            if (!Directory.Exists(_fitsDirectory))
            {
                Directory.CreateDirectory(_fitsDirectory);
            }
            
            // Setup pointing model file path
            // Format: PointingModelDDMMYYYY-XX.pxp
            _progressiveModelPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "CalibrationData",
                $"PointingModel{sessionFolder}.pxp"
            );

            InitializePlateSolver();
        }

        private void InitializePlateSolver()
        {
            // Use PlateSolverManager auto-detection instead of legacy synthetic catalog settings.
            _plateSolver?.Initialize(string.Empty);
        }
        
        /// <summary>
        /// Initialize progressive pointing model file (PlaneWave .pxp format)
        /// File will be updated incrementally as points are solved
        /// [CRITICAL] Must match Port1.pxp format EXACTLY for PXPLib compatibility!
        /// </summary>
        private void InitializeProgressiveModelFile()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                
                // Header: Number of calibration stars
                sb.AppendLine("Number of Cal Stars:");
                sb.AppendLine("0");  // Will be updated as points are solved
                sb.AppendLine();     // Blank line after count (before first point)
                
                // [CRITICAL] Footer sections - MUST match Port1.pxp format EXACTLY!
                
                // Latitude, Longitude, TimeZone, DST
                // [FIX] TimeZone and DST must have values, not empty!
                sb.AppendLine("Latitude, Longitude, TimeZone, DST:");
                sb.AppendLine($"{_observerLocation.Latitude}");
                sb.AppendLine($"{_observerLocation.Longitude}");
                sb.AppendLine("7");   // TimeZone: +7 (Thailand = UTC+7)
                sb.AppendLine("0");   // DST: 0 (Thailand doesn't use DST)
                sb.AppendLine();      // Empty separator
                
                // CW Azimuth, MaxAzCounts
                // [CRITICAL] PlaneWave uses NORMALIZED coordinates!
                // MaxAzCounts = 2π (one full revolution in radians)
                // Encoder CPR is used separately for counts→radians conversion
                sb.AppendLine("CW Azimuth, MaxAzCounts:");
                sb.AppendLine("True");
                sb.AppendLine("6.283185308");  // 2π radians (normalized system)
                sb.AppendLine();
                
                // Altitude Up, MaxAltCounts
                sb.AppendLine("Altitude Up, MaxAltCounts:");
                sb.AppendLine("True");
                sb.AppendLine("6.283185308");  // 2π radians (normalized system)
                sb.AppendLine();
                
                // IsAltAz, IsGEM
                sb.AppendLine("IsAltAz, IsGEM:");
                sb.AppendLine("True");
                sb.AppendLine("False");
                sb.AppendLine();
                
                // Temperature, Pressure
                sb.AppendLine("Temperature, Pressure:");
                sb.AppendLine("16");
                sb.AppendLine("1018");
                sb.AppendLine();
                
                // Terms Enabled (15 terms)
                sb.AppendLine("Terms Enabled:");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine();
                
                // [NEW] Model Terms (will be updated after each solve)
                sb.AppendLine("Model Terms:");
                sb.AppendLine("0.0");  // IH - Azimuth index error
                sb.AppendLine("0.0");  // ID - Altitude index error  
                sb.AppendLine("0.0");  // NP - Non-perpendicularity
                sb.AppendLine("0.0");  // CH - Horizontal collimation
                sb.AppendLine("0.0");  // CV - Vertical collimation
                sb.AppendLine("0.0");  // MA - Azimuth axis misalignment
                sb.AppendLine("0.0");  // ME - Altitude axis misalignment
                sb.AppendLine("0.0");  // TF - Tube flexure
                sb.AppendLine("0.0");  // TX - Tube flexure tilt
                sb.AppendLine("0.0");  // HCES - Azimuth encoder sin
                sb.AppendLine("0.0");  // HCEC - Azimuth encoder cos
                sb.AppendLine("0.0");  // DCES - Altitude encoder sin
                sb.AppendLine("0.0");  // DCEC - Altitude encoder cos
                sb.AppendLine("0.0");  // TF2 - Second order flexure
                sb.AppendLine("0.0");  // TX2 - Second order tilt
                sb.AppendLine();
                
                // Cal Points Used (will be updated when points are added)
                sb.AppendLine("Cal Points Used:");
                sb.AppendLine();
                
                // Scale Factors, Theta, Phi
                sb.AppendLine("Scale Factors, Theta, Phi:");
                sb.AppendLine("1");
                sb.AppendLine("1");
                sb.AppendLine();
                
                // Clock Rate Error
                sb.AppendLine("Clock Rate Error:");
                sb.AppendLine("0.0");
                sb.AppendLine();
                
                // Terms Enabled (second set - 7 terms)
                sb.AppendLine("Terms Enabled:");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine("0");
                sb.AppendLine();
                
                // Enable Refraction
                sb.AppendLine("Enable Refraction:");
                sb.AppendLine("1");
                sb.AppendLine();
                
                // Max Sensitivity
                sb.AppendLine("Max Sensitivity:");
                sb.AppendLine("300");
                sb.AppendLine();
                
                // Write initial file
                _progressiveModelPath = _progressiveModelPath.Replace(".pmd", ".pxp");
                File.WriteAllText(_progressiveModelPath, sb.ToString());
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✓ Progressive model file initialized: {Path.GetFileName(_progressiveModelPath)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Failed to initialize progressive file: {ex.Message}");
            }
        }

        /// <summary>
        /// Pause calibration
        /// </summary>
        public void Pause()
        {
            _isRunning = false;
            _state = CalibrationState.Paused;
            
            // Stop mount
            _pollingManager?.SetAzimuthCommandVelocity(0);
            _pollingManager?.SetAltitudeCommandVelocity(0);
            
            // Stop tracking controller
            _trackingController?.StopTracking();
            
            // Disable RMS tracking
            if (_pollingManager?.RmsTrackingManager != null)
            {
                _pollingManager.RmsTrackingManager.SetAzimuthTarget(null);
                _pollingManager.RmsTrackingManager.SetAltitudeTarget(null);
            }

        }

        /// <summary>
        /// Resume calibration
        /// [FIX] Reset target cache to recalculate after pause
        /// </summary>
        public void Resume()
        {
            _completionDialogRequested = false;

            _currentPointIndex = FindFirstPendingPointIndex();
            if (_calibrationPoints.Count == 0 || _calibrationPoints[_currentPointIndex].IsSolved)
            {
                LogInfo("[CALIBRATION] No pending calibration points to resume.");
                return;
            }

            // [CRITICAL FIX] Stop any external tracking before resuming calibration!
            // Same as Start() - prevents race conditions
            var trackingManager = TrackingManager.Instance;
            if (trackingManager.IsTracking)
            {
                LogInfo($"[CALIBRATION] ⚠️ External tracking detected during Resume: {trackingManager.CurrentObjectType}");
                LogInfo($"[CALIBRATION] 🛑 Stopping external tracking before resume...");
                trackingManager.ForceStopAllTracking();
                
                // Also stop our local tracking controller if running
                _trackingController?.StopTracking();
                
                // Brief pause to allow tracking to fully stop
                System.Threading.Thread.Sleep(100);
                LogInfo($"[CALIBRATION] ✅ External tracking stopped");
            }
            
            _isRunning = true;
            _runStartedAtUtc ??= DateTime.UtcNow;
            _state = CalibrationState.Slewing;
            
            // [CRITICAL FIX] Reset ALL state flags — same as Start()!
            // Stop() sets UseRawPosition=false; must reset to true so arrival check (RAW) works.
            _targetCached = false;
            _imageAddedForPoint = false;
            _exposureCompleted = false;
            _rmsStabilityStartTime = null;
            _isCapturingImage = false;
            _pendingSolvePointIndex = -1;
            _trackingController.UseRawPosition = true;
            
            // Reset tracking flag for current point so it will re-initialize tracking
            if (_currentPointIndex >= 0 && _currentPointIndex < _calibrationPoints.Count)
            {
                _calibrationPoints[_currentPointIndex].IsTracking = false;
                _calibrationPoints[_currentPointIndex].IsSlewing = false;
                _calibrationPoints[_currentPointIndex].IsCapturing = false;
                _calibrationPoints[_currentPointIndex].IsSolving = false;
            }
            
            System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ▶️ Resume() called - resuming from point {_currentPointIndex + 1}");
        }
        
        /// <summary>
        /// [NEW] Resume calibration from failed/pending points (from Narit)
        /// Resets all non-solved points to Pending state
        /// </summary>
        public void ResumeFromFailures()
        {
            if (_isRunning)
            {
                LogError("Calibration is already running");
                return;
            }
            
            // Reset all non-solved points to pending
            // [FIX] Use !IsSolved only — MoveToNextPoint sets IsCompleted=true before
            // solve finishes, so solve-failed points have IsCompleted=true but IsSolved=false.
            // Old condition (!IsSolved && !IsCompleted) missed these failed points.
            int resetCount = 0;
            foreach (var point in _calibrationPoints)
            {
                if (!point.IsSolved)
                {
                    point.IsCompleted = false;
                    point.IsSolving = false;
                    point.IsCapturing = false;
                    point.IsSlewing = false;
                    point.IsTracking = false;
                    point.SolveFailed = false;
                    
                    // Clear error data
                    point.RaError_Arcsec = null;
                    point.DecError_Arcsec = null;
                    point.AzimuthRmsError_Arcsec = null;
                    point.AltitudeRmsError_Arcsec = null;
                    
                    resetCount++;
                }
            }
            
            if (resetCount == 0)
            {
                LogInfo("No failed points to resume - all points already solved");
                return;
            }
            
            // Find first unsolved point
            _currentPointIndex = 0;
            for (int i = 0; i < _calibrationPoints.Count; i++)
            {
                if (!_calibrationPoints[i].IsSolved)
                {
                    _currentPointIndex = i;
                    break;
                }
            }
            
            LogInfo($"Resuming calibration from {resetCount} unsolved point(s), starting at point #{_currentPointIndex + 1}");
            CaptureModelPathBeforeCalibration();
            
            // [CRITICAL FIX] Stop any external tracking before resuming calibration!
            var trackingManager = TrackingManager.Instance;
            if (trackingManager.IsTracking)
            {
                LogInfo($"[CALIBRATION] ⚠️ External tracking detected during ResumeFromFailures: {trackingManager.CurrentObjectType}");
                LogInfo($"[CALIBRATION] 🛑 Stopping external tracking...");
                trackingManager.ForceStopAllTracking();
                _trackingController?.StopTracking();
                System.Threading.Thread.Sleep(100);
                LogInfo($"[CALIBRATION] ✅ External tracking stopped");
            }
            
            // Start calibration
            // [CRITICAL FIX] Reset ALL state flags — same as Start()!
            // Missing resets caused deadlock: UseRawPosition stayed false after Stop(),
            // so arrival check (RAW) never matched while tracking (CORRECTED) was happy.
            _isRunning = true;
            _state = CalibrationState.Slewing;
            _targetCached = false;
            _imageAddedForPoint = false;
            _exposureCompleted = false;
            _rmsStabilityStartTime = null;
            _isCapturingImage = false;
            _pendingSolvePointIndex = -1;
            
            // [CRITICAL FIX] Start with RAW position for slewing!
            // Stop() sets UseRawPosition=false, must reset to true for arrival check.
            // Without this, tracking uses CORRECTED coords but arrival checks RAW → deadlock.
            _trackingController.UseRawPosition = true;
            
            // Reset current point's state flags
            if (_currentPointIndex >= 0 && _currentPointIndex < _calibrationPoints.Count)
            {
                var currentPt = _calibrationPoints[_currentPointIndex];
                currentPt.IsSlewing = false;
                currentPt.IsTracking = false;
                currentPt.IsCapturing = false;
                currentPt.IsSolving = false;
            }
        }

        /// <summary>
        /// Stop calibration
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _state = CalibrationState.Idle;
            _completionDialogRequested = false;
            SaveSessionCheckpoint("stop");
            
            // Stop mount
            _pollingManager?.SetAzimuthCommandVelocity(0);
            _pollingManager?.SetAltitudeCommandVelocity(0);
            
            // Stop tracking and restore Corrected position mode
            _trackingController?.StopTracking();
            if (_trackingController != null)
                _trackingController.UseRawPosition = false;
        }

        private void CaptureModelPathBeforeCalibration()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_modelPathBeforeCalibration))
                {
                    return;
                }

                var pointingModelManager = App.PointingModelManager;
                if (pointingModelManager?.IsModelLoaded == true &&
                    pointingModelManager.ActiveBackendMode == PointingModelBackendMode.LegacyPxpReload &&
                    !string.IsNullOrWhiteSpace(pointingModelManager.ModelFilePath) &&
                    File.Exists(pointingModelManager.ModelFilePath))
                {
                    _modelPathBeforeCalibration = pointingModelManager.ModelFilePath;
                    LogInfo($"Stored pre-calibration model: {Path.GetFileName(_modelPathBeforeCalibration)}");
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to capture pre-calibration model path: {ex.Message}");
            }
        }

        public bool TryCreateRuntimeReadyPointingModel(out string modelPath, out string statusMessage)
        {
            return TryBuildRuntimeReadyPointingModel(
                PointingModel.PointingModelFile.GetDefaultModelPath(),
                overwriteAutosave: true,
                out modelPath,
                out statusMessage);
        }

        public bool TryRefreshRuntimeReadySnapshot(out string modelPath, out string statusMessage)
        {
            return TryBuildRuntimeReadyPointingModel(
                RuntimeReadySnapshotPath,
                overwriteAutosave: false,
                out modelPath,
                out statusMessage);
        }

        public bool TryActivatePointingModelAsDefault(string sourceModelPath, out string defaultModelPath, out string statusMessage)
        {
            defaultModelPath = null;
            statusMessage = "No pointing model was activated.";

            try
            {
                if (string.IsNullOrWhiteSpace(sourceModelPath) || !File.Exists(sourceModelPath))
                {
                    statusMessage = "The source pointing model file does not exist.";
                    return false;
                }

                defaultModelPath = DefaultModelPath;
                Directory.CreateDirectory(Path.GetDirectoryName(defaultModelPath) ?? AppDomain.CurrentDomain.BaseDirectory);
                File.Copy(sourceModelPath, defaultModelPath, overwrite: true);

                bool loaded = App.PointingModelManager?.LoadModel(defaultModelPath) == true;
                if (!loaded)
                {
                    statusMessage = $"Default model file was created but could not be loaded: {Path.GetFileName(defaultModelPath)}";
                    return false;
                }

                AppSettings.SavePointingModelFilePath(defaultModelPath);
                statusMessage = $"Default pointing model activated: {Path.GetFileName(defaultModelPath)}";
                LogInfo(statusMessage);
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = $"Failed to activate default pointing model: {ex.Message}";
                LogError(statusMessage);
                return false;
            }
        }

        public bool TryCreateAndActivateDefaultPointingModel(out string defaultModelPath, out string statusMessage)
        {
            defaultModelPath = null;

            if (!TryCreateRuntimeReadyPointingModel(out string runtimeModelPath, out string buildStatus) ||
                string.IsNullOrWhiteSpace(runtimeModelPath) ||
                !File.Exists(runtimeModelPath))
            {
                statusMessage = buildStatus;
                return false;
            }

            if (!TryActivatePointingModelAsDefault(runtimeModelPath, out defaultModelPath, out string activationStatus))
            {
                statusMessage = activationStatus;
                return false;
            }

            statusMessage = $"{buildStatus}\nActivated as {Path.GetFileName(defaultModelPath)}";
            return true;
        }

        private bool TryBuildRuntimeReadyPointingModel(
            string targetPath,
            bool overwriteAutosave,
            out string modelPath,
            out string statusMessage)
        {
            modelPath = null;
            statusMessage = "No runtime-ready pointing model could be created.";

            try
            {
                var solvedPoints = _calibrationPoints
                    .Where(p => p.IsSolved &&
                                p.IsIncludedInModel &&
                                p.SolvedRa_J2000_Rads.HasValue &&
                                p.SolvedDec_J2000_Rads.HasValue)
                    .ToList();

                if (solvedPoints.Count == 0)
                {
                    statusMessage = "No included solved calibration points are available.";
                    return false;
                }

                string sourcePath = null;
                if (File.Exists(AutosaveModelPath))
                {
                    sourcePath = AutosaveModelPath;
                }
                else if (!string.IsNullOrWhiteSpace(_progressiveModelPath) && File.Exists(_progressiveModelPath))
                {
                    sourcePath = _progressiveModelPath;
                }

                string finalPath = targetPath;
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? AppDomain.CurrentDomain.BaseDirectory);

                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    var observerSettings = new Utility.ObserverLocationSettings
                    {
                        Latitude_Deg = _observerLocation.Latitude,
                        Longitude_Deg = _observerLocation.Longitude,
                        Altitude_Meters = _observerLocation.Altitude
                    };

                    PointingModel.PlaneWaveModelFile.SavePlaneWaveFormat(finalPath, solvedPoints, observerSettings);
                    sourcePath = finalPath;
                }

                var pointXp = PointingModel.PointingModelManager.CreateConfiguredRuntimePointXP();
                pointXp.LoadFile(sourcePath);

                if (pointXp.Core == null || pointXp.Core.NumCalPoints <= 0)
                {
                    statusMessage = $"The source model file does not contain usable calibration points: {Path.GetFileName(sourcePath)}";
                    return false;
                }

                pointXp.Core.Calibrate();
                pointXp.SaveFile(finalPath);

                if (overwriteAutosave && !string.Equals(finalPath, AutosaveModelPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(finalPath, AutosaveModelPath, overwrite: true);
                }

                modelPath = finalPath;
                double rmsArcsec = pointXp.Core.RMSErrorRad * 206264.80624709636;
                statusMessage = $"Runtime model ready: {Path.GetFileName(finalPath)} ({pointXp.Core.NumCalPoints} points, RMS {rmsArcsec:F2}\")";
                LogInfo(statusMessage);
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = $"Failed to create runtime-ready pointing model: {ex.Message}";
                LogError(statusMessage);
                return false;
            }
        }
        
        /// <summary>
        /// [DEPRECATED - DO NOT USE] 
        /// This method was causing race conditions by starting tracking without TrackingManager registration.
        /// Use AddManualCalibrationPoint() instead which handles tracking properly.
        /// Kept for backward compatibility but does nothing.
        /// </summary>
        [Obsolete("Use AddManualCalibrationPoint() instead - this method caused race conditions")]
        public void StartSiderealTracking()
        {
            // [CRITICAL FIX] This method was removed because it caused race conditions:
            // 1. It started tracking without registering with TrackingManager
            // 2. AddManualCalibrationPoint() would then start ANOTHER tracking instance
            // 3. Two tracking controllers would fight each other → mount oscillation
            //
            // AddManualCalibrationPoint() now handles tracking startup correctly with
            // proper TrackingManager registration.
            
            LogInfo("[TRACKING] StartSiderealTracking() is deprecated - tracking will be started by AddManualCalibrationPoint()");
        }

        /// <summary>
        /// Reset calibration (go back to first point)
        /// </summary>
        public void Reset()
        {
            Stop();
            _currentPointIndex = 0;
            _imageAddedForPoint = false;
            _exposureCompleted = false;
            _rmsStabilityStartTime = null;

            foreach (var point in _calibrationPoints)
            {
                point.IsCompleted = false;
                point.AzimuthRmsError_Arcsec = null;
                point.AltitudeRmsError_Arcsec = null;
            }

        }



        /// <summary>
        /// Update calibration process (called from control loop timer - 50ms)
        /// This replaces OrchestrationTimer_Tick logic
        /// </summary>
        public void Update()
        {
            if (!_isRunning || _currentPointIndex >= _calibrationPoints.Count)
                return;

            TryAutoSaveCheckpoint();

            var currentPoint = _calibrationPoints[_currentPointIndex];
            
            // [CRITICAL FIX] Skip completed points - move to next pending point
            if (currentPoint.IsCompleted || currentPoint.IsSolved)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⏭️ Skipping completed point #{currentPoint.Index}, moving to next");
                
                // Simply increment index without calling MoveToNextPoint (which would stop tracking, etc.)
                _currentPointIndex++;
                _targetCached = false;  // Reset for next point
                
                // Check if all points are done
                if (_currentPointIndex >= _calibrationPoints.Count)
                {
                    // Check if there are any remaining pending points
                    bool anyPending = _calibrationPoints.Any(p => !p.IsCompleted && !p.IsSolved);
                    if (!anyPending)
                    {
                        Stop();
                        _state = CalibrationState.Completed;
                        LogInfo("[CALIBRATION] All points completed!");

                        if (!_isManualAddMode)
                        {
                            _ = WaitForAllSolvesAndShowDialog();
                        }
                        else
                        {
                            LogInfo("[MANUAL ADD] Point completed from skip path - skipping completion dialog");
                            _isManualAddMode = false;
                        }
                    }
                }
                return;
            }

            // Get current mount position
            var azInfo = _pollingManager.AzimuthInfo;
            var altInfo = _pollingManager.AltitudeInfo;

            if (azInfo == null || altInfo == null)
                return;

            // [CRITICAL FIX] During calibration, ALWAYS use RAW encoder position!
            // Corrected position changes discontinuously when progressive model reloads,
            // causing arrival check mismatch and SiderealTracking instability.
            // RAW position is stable — same coordinate frame for all points.
            double rawAz = (azInfo.Raw_Position / (double)_azimuthConfig.Encoder_Resolution) * 360.0 + _azimuthConfig.Position_Offset_Deg;
            double rawAlt = (altInfo.Raw_Position / (double)_altitudeConfig.Encoder_Resolution) * 360.0 + _altitudeConfig.Position_Offset_Deg;
            
            double currentAz = rawAz;
            double currentAlt = rawAlt;

            // [FIX] Use cached target (calculated once in HandleSlewing)
            // If not cached yet, calculate and cache now
            if (!_targetCached)
            {
                _cachedTargetAzimuth = MountCalibrationHelper.UnwrapAzimuthToMountRange(
                    currentPoint.TargetAzimuth, 
                    currentAz, 
                    _azimuthConfig.CCW_Limit_Deg, 
                    _azimuthConfig.CW_Limit_Deg);
                _cachedTargetAltitude = currentPoint.TargetAltitude;
                
                // Also cache RA/Dec (same as HandleSlewing)
                var (ra, dec) = MountCalibrationHelper.ConvertAltAzToRaDec(
                    _cachedTargetAltitude, _cachedTargetAzimuth,
                    _observerLocation.Latitude, _observerLocation.Longitude,
                    NtpTimeService.Instance.GetCurrentUtcTime());
                _cachedTargetRA = ra;
                _cachedTargetDec = dec;
                _targetCached = true;
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 🎯 Target cached (fallback): Az={_cachedTargetAzimuth:F2}° Alt={_cachedTargetAltitude:F2}° → RA={_cachedTargetRA:F4}h Dec={_cachedTargetDec:F4}°");
            }

            // [PIPELINE] When exposure is done → MoveToNextPoint immediately (start slewing)
            // Plate solve runs in background. Track pending solve index.
            // When arriving at next point → must wait for previous solve to finish before tracking.
            if (_exposureCompleted)
            {
                var currentPt = _calibrationPoints[_currentPointIndex];
                currentPt.IsTracking = false;
                currentPt.IsCapturing = false;
                
                // Remember this point needs solve completion tracking
                _pendingSolvePointIndex = _currentPointIndex;
                _pendingSolveStartTime = DateTime.Now;
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✅ Exposure done for point {_currentPointIndex + 1} → MoveToNextPoint (slew while solving)");
                MoveToNextPoint();
                return;
            }

            if (_imageAddedForPoint)
            {
                // Image capture started but not yet complete — waiting
                if (DateTime.Now.Second % 5 == 0 && DateTime.Now.Millisecond < 100)
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⏳ Waiting for exposure to complete...");
                }
                return;
            }

            // Calculate errors using cached target
            double azError = _cachedTargetAzimuth - currentAz;
            double altError = _cachedTargetAltitude - currentAlt;

            // Update current position in point
            currentPoint.CurrentAzimuth = currentAz;
            currentPoint.CurrentAltitude = currentAlt;

            // Check if arrived (use setting value instead of constant)
            bool hasArrived = Math.Abs(azError) < _arrivalThresholdDeg && 
                             Math.Abs(altError) < _arrivalThresholdDeg;

            // [PIPELINE] Block tracking/capture at new point if previous solve is still pending
            // Allow slewing to continue, but don't start tracking until previous solve is done
            if (_pendingSolvePointIndex >= 0 && _pendingSolvePointIndex < _calibrationPoints.Count)
            {
                var pendingPoint = _calibrationPoints[_pendingSolvePointIndex];
                
                // Timeout: if solve takes too long (background processing stuck), force proceed
                const double SOLVE_START_GRACE_SECONDS = 5.0;
                const double SOLVE_TIMEOUT_SECONDS = 120.0;
                double pendingElapsed = (DateTime.Now - _pendingSolveStartTime).TotalSeconds;
                
                if (pendingPoint.IsSolved || pendingPoint.SolveFailed)
                {
                    // Previous solve done → clear pending
                    string solveStatus = pendingPoint.IsSolved ? "SOLVED" : "SOLVE FAILED";
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✅ Previous point {_pendingSolvePointIndex + 1} {solveStatus} → proceed with current point");
                    _pendingSolvePointIndex = -1;
                }
                else if (pendingElapsed >= SOLVE_TIMEOUT_SECONDS)
                {
                    // Timeout! Force proceed
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ TIMEOUT: Point {_pendingSolvePointIndex + 1} solve not completed after {pendingElapsed:F0}s — forcing proceed");
                    pendingPoint.SolveFailed = true;
                    pendingPoint.IsSolving = false;
                    _pendingSolvePointIndex = -1;
                }
                else if (!pendingPoint.IsSolving && pendingElapsed >= SOLVE_START_GRACE_SECONDS)
                {
                    // This state is inconsistent: the point was marked completed and the
                    // pipeline is waiting on it, but plate solving never actually started.
                    // Do not let the calibration freeze forever in [PROCESSING].
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Point {_pendingSolvePointIndex + 1} never entered SOLVING state within {SOLVE_START_GRACE_SECONDS:F0}s — marking as failed");
                    pendingPoint.SolveFailed = true;
                    pendingPoint.IsSolved = false;
                    pendingPoint.IsSolving = false;
                    _pendingSolvePointIndex = -1;
                }
                else if (hasArrived || currentPoint.IsTracking)
                {
                    // Arrived at new point but previous solve not done → wait
                    _state = CalibrationState.Processing;
                    if (DateTime.Now.Second % 3 == 0 && DateTime.Now.Millisecond < 100)
                    {
                        string detail = pendingPoint.IsSolving ? "plate solving..." : "waiting for solve to start...";
                        System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⏳ Arrived at point {_currentPointIndex + 1}, waiting for point {_pendingSolvePointIndex + 1} solve ({detail}) [{pendingElapsed:F0}s elapsed]");
                    }
                    return;
                }
                // Still slewing → let slewing continue (fall through)
            }

            // Once tracking starts, LOCK into tracking mode!
            // This lets SiderealTracking handle precise tracking.
            if (hasArrived || currentPoint.IsTracking)
            {
                // Pass Raw Az/Alt for plate solver (real sky error), Corrected for tracking
                HandleArrivedAtTarget(currentPoint, _cachedTargetAzimuth, currentAz, currentAlt, rawAz, rawAlt, azInfo, altInfo);
            }
            else
            {
                HandleSlewing(azError, altError);
            }
        }

        /// <summary>
        /// Handle slewing to target
        /// [NEW] Use SiderealTracking controller (same as normal tracking) instead of simple P-control
        /// [CRITICAL FIX] Cache target calculation to prevent recalculation during slew!
        /// </summary>
        private void HandleSlewing(double azError, double altError)
        {
            _state = CalibrationState.Slewing;
            
            // [NEW] Update current point status
            if (_currentPointIndex >= 0 && _currentPointIndex < _calibrationPoints.Count)
            {
                _calibrationPoints[_currentPointIndex].IsSlewing = true;
                _calibrationPoints[_currentPointIndex].IsTracking = false;
                _calibrationPoints[_currentPointIndex].IsCapturing = false;
                _calibrationPoints[_currentPointIndex].IsSolving = false;
            }

            var point = _calibrationPoints[_currentPointIndex];
            
            // [CRITICAL FIX] Calculate target ONCE and cache it!
            // Prevents recalculation every iteration which can cause wrong path selection
            if (!_targetCached)
            {
                // [CRITICAL FIX] During calibration, always use RAW position for path selection
                // Corrected position jumps when progressive model reloads → wrong path!
                var azInfoSlew = _pollingManager.AzimuthInfo;
                double currentAz = azInfoSlew != null
                    ? (azInfoSlew.Raw_Position / (double)_azimuthConfig.Encoder_Resolution) * 360.0 + _azimuthConfig.Position_Offset_Deg
                    : 0;
                
                // Calculate unwrapped target azimuth (choose shortest path)
                _cachedTargetAzimuth = MountCalibrationHelper.UnwrapAzimuthToMountRange(
                    point.TargetAzimuth, 
                    currentAz, 
                    _azimuthConfig.CCW_Limit_Deg, 
                    _azimuthConfig.CW_Limit_Deg);
                
                _cachedTargetAltitude = point.TargetAltitude;
                
                // [CRITICAL FIX] Convert Alt/Az → RA/Dec ONCE and cache!
                // Before: re-converted every 50ms with new timestamp → RA/Dec drifted
                //         → SiderealTracking got a new target every cycle → D-term jitter → oscillation
                // After:  convert once → SiderealTracking tracks a FIXED star (RA/Dec)
                //         → smooth approach, no jitter, proper sidereal tracking for plate solve
                var (ra, dec) = MountCalibrationHelper.ConvertAltAzToRaDec(
                    _cachedTargetAltitude, 
                    _cachedTargetAzimuth, 
                    _observerLocation.Latitude,
                    _observerLocation.Longitude,
                    NtpTimeService.Instance.GetCurrentUtcTime());
                _cachedTargetRA = ra;
                _cachedTargetDec = dec;
                _targetCached = true;
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 🎯 Target cached: Az={_cachedTargetAzimuth:F2}° Alt={_cachedTargetAltitude:F2}° → RA={_cachedTargetRA:F4}h Dec={_cachedTargetDec:F4}° (fixed star)");
            }
            
            // Use cached RA/Dec — same star every cycle, no jitter!
            // ConvertAltAzToRaDec returns Apparent coordinates (current epoch), not J2000
            _trackingController.SetTarget(_cachedTargetRA, _cachedTargetDec, _observerLocation, isApparent: true);
            
            // Start tracking if not already started
            if (!_trackingController.IsTracking)
            {
                _trackingController.StartTracking();
            }
            
            // Note: SiderealTracking will handle velocity commands internally with PDF control
            // No need to send velocity commands manually here
        }

        /// <summary>
        /// Handle arrived at target (tracking phase)
        /// [NEW] SiderealTracking already started from HandleSlewing - just update RMS targets
        /// </summary>
        private void HandleArrivedAtTarget(CalibrationPoint point, double targetAz, double currentAz, double currentAlt,
                                           double rawAz, double rawAlt, AzimuthInfo azInfo, AltitudeInfo altInfo)
        {
            // [CRITICAL FIX] Use cached RA/Dec — track a FIXED star, not a fixed Alt/Az!
            // Before: re-computed RA/Dec every 50ms from same Alt/Az → target jittered
            //         → D-term oscillation → mount couldn't settle
            // After:  use RA/Dec computed once in HandleSlewing → stable target
            //         → SiderealTracking handles Earth rotation via VFF
            //         → stars are round in plate solve images
            //
            // Note: The mount will drift ~15"/sec from the original Alt/Az grid position
            // due to Earth rotation, but that's fine — plate solver finds actual sky position.
            _trackingController.SetTarget(_cachedTargetRA, _cachedTargetDec, _observerLocation, isApparent: true);
            
            if (!_trackingController.IsTracking)
            {
                _trackingController.StartTracking();
            }

            // Initialize tracking on first arrival
            if (!point.IsTracking)
            {
                point.IsTracking = true;
                point.IsSlewing = false;  // Stop slewing state
                _trackingStartTime = DateTime.Now;
                _state = CalibrationState.Tracking;

                // [HYBRID FIX] Switch to CORRECTED position for precise sidereal tracking,
                // but recalculate the RA/Dec target from the corrected position to avoid
                // the position jump that previously caused 5-6 second reconvergence delays.
                //
                // Why CORRECTED is better for tracking:
                //   - VFF velocity matches actual sky position → accurate sidereal rate
                //   - P-term drives corrected → target → telescope tracks true sky position
                //   - Result: rounder stars → better plate solve → more accurate model
                //
                // Why recalculating target prevents the jump:
                //   - Old approach: target was set from RAW Alt/Az, then switched to CORRECTED
                //     → corrected position ≠ raw position → error jumped 81"+ → slow reconvergence
                //   - New approach: recalculate target from CORRECTED position at switch moment
                //     → target matches corrected position → error ≈ 0" → instant convergence
                bool hasModel = App.PointingModelManager?.IsModelLoaded == true;
                
                if (hasModel && _pollingManager?.MountInfo != null)
                {
                    // Get corrected position from pointing model
                    double correctedAz = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                    double correctedAlt = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;
                    
                    // Recalculate RA/Dec target from corrected position BEFORE switching
                    // This ensures the target matches the new position reference frame
                    var (newRA, newDec) = MountCalibrationHelper.ConvertAltAzToRaDec(
                        correctedAlt, correctedAz,
                        _observerLocation.Latitude, _observerLocation.Longitude,
                        NtpTimeService.Instance.GetCurrentUtcTime());
                    
                    _cachedTargetRA = newRA;
                    _cachedTargetDec = newDec;
                    
                    // Now switch to CORRECTED mode — target already matches → no jump!
                    _trackingController.UseRawPosition = false;
                    _trackingController.SetTarget(_cachedTargetRA, _cachedTargetDec, _observerLocation, isApparent: true);
                    
                    System.Diagnostics.Debug.WriteLine($"[TRACKING] Position source: CORRECTED (model loaded, target recalculated → no jump)");
                    System.Diagnostics.Debug.WriteLine($"[TRACKING]   Corrected Az={correctedAz:F4}° Alt={correctedAlt:F4}° → RA={newRA:F4}h Dec={newDec:F4}°");
                }
                else
                {
                    // No model → use RAW (no corrections available)
                    _trackingController.UseRawPosition = true;
                    System.Diagnostics.Debug.WriteLine($"[TRACKING] Position source: RAW (no model yet)");
                }




                var cameraSettings = AppSettings.GetCameraSettings();
                double exposureSec = cameraSettings.ExposureTime_ms / 1000.0;
                double expectedDriftArcsec = 15.0 * exposureSec * Math.Cos(_observerLocation.Latitude * Math.PI / 180.0);
                
                System.Diagnostics.Debug.WriteLine($"[TRACKING] Arrived at target Az={_cachedTargetAzimuth:F2}° Alt={_cachedTargetAltitude:F2}°");
                System.Diagnostics.Debug.WriteLine($"[TRACKING] RA/Dec target: RA={_cachedTargetRA:F4}h Dec={_cachedTargetDec:F4}°");
                System.Diagnostics.Debug.WriteLine($"[TRACKING] Expected sidereal drift for {exposureSec:F1}s exposure: ~{expectedDriftArcsec:F0}\"");
                
                // [P2] NTP Debug Logging — verify NTP vs System clock offset
                var ntpTime = NtpTimeService.Instance.GetCurrentUtcTime();
                var sysTime = DateTime.UtcNow;
                var ntpOffset = ntpTime - sysTime;
                System.Diagnostics.Debug.WriteLine($"[NTP-DEBUG] System={sysTime:HH:mm:ss.fff} NTP={ntpTime:HH:mm:ss.fff} Offset={ntpOffset.TotalSeconds:F3}s");

            }

            // Check RMS stability and capture image
            if (!_imageAddedForPoint && !_isCapturingImage)
            {
                point.TrackingTime = (DateTime.Now - _trackingStartTime).TotalSeconds;

                double rmsAz = azInfo.TrackingRmsError_Arcsec;
                double rmsAlt = altInfo.TrackingRmsError_Arcsec;
                double totalRms = Math.Sqrt(rmsAz * rmsAz + rmsAlt * rmsAlt);

                // Check if enough samples
                if (azInfo.TrackingSampleCount < _minimumTrackingSamples || altInfo.TrackingSampleCount < _minimumTrackingSamples)
                {
                    _state = CalibrationState.CollectingSamples;
                    point.SampleCount = Math.Min(azInfo.TrackingSampleCount, altInfo.TrackingSampleCount);
                    return;
                }

                // Check RMS stability (use setting value instead of constant)
                if (totalRms < _rmsStabilityThresholdArcsec)
                {
                    if (_rmsStabilityStartTime == null)
                    {
                        _rmsStabilityStartTime = DateTime.Now;
                        _state = CalibrationState.Stabilizing;
                    }

                    double stabilityDuration = (DateTime.Now - _rmsStabilityStartTime.Value).TotalMilliseconds;

                    if (stabilityDuration >= _rmsStabilityDurationMs)
                    {
                        if (stabilityDuration >= _rmsStabilityDurationMs + _extraSettleTimeMs)
                        {
                            // Start capture
                            _state = CalibrationState.Capturing;
                            System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✅ RMS stable for {stabilityDuration:F0}ms (threshold: {_rmsStabilityDurationMs}ms + {_extraSettleTimeMs}ms settle)");
                            System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 📸 Starting capture for point {_currentPointIndex + 1}");
                            _ = CaptureImageAsync(_currentPointIndex + 1, point, rawAz, rawAlt);
                            _imageAddedForPoint = true;
                            _rmsStabilityStartTime = null;
                        }
                        else
                        {
                            // Still in extra settling period
                            _state = CalibrationState.Stabilizing;
                        }
                    }
                }
                else
                {
                    _state = CalibrationState.WaitingForStability;
                    point.CurrentRmsError = totalRms;
                    _rmsStabilityStartTime = null;
                    
                    // [CRITICAL FIX] Timeout: If RMS never drops below threshold, capture anyway!
                    // Without this, calibration gets stuck forever at a point where RMS hovers
                    // just above the threshold (e.g., 4.44" vs threshold 3.0").
                    // Use _trackingStartTime (set when point.IsTracking becomes true) as anchor.
                    double waitingSeconds = (DateTime.Now - _trackingStartTime).TotalSeconds;
                    
                    if (waitingSeconds >= _rmsTimeoutSeconds)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ RMS TIMEOUT after {waitingSeconds:F0}s! RMS={totalRms:F2}\" (threshold={_rmsStabilityThresholdArcsec:F2}\") — capturing anyway");
                        System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 📸 Timeout capture for point {_currentPointIndex + 1}");
                        _state = CalibrationState.Capturing;
                        _ = CaptureImageAsync(_currentPointIndex + 1, point, rawAz, rawAlt);
                        _imageAddedForPoint = true;
                    }
                }
            }
        }

        /// <summary>
        /// Move to next calibration point
        /// </summary>
        private void MoveToNextPoint()
        {
            var currentPoint = _calibrationPoints[_currentPointIndex];
            
            // [FIX] Mark as completed and clear all active states
            currentPoint.IsCompleted = true;
            currentPoint.IsTracking = false;
            currentPoint.IsCapturing = false;
            currentPoint.IsSlewing = false;
            // Note: IsSolving may still be true if plate solving in background

            // Calculate RMS
            CalculateRmsErrors(currentPoint);

            // [FIX] Do NOT stop tracking between points!
            // Stopping tracking sets velocity=0 → mount decelerates → then re-accelerates → slow!
            // Instead, just clear RMS and let HandleSlewing() re-target on the next Update() cycle.
            // SiderealTracking will smoothly transition to the new target without stopping.
            
            // [HYBRID] Switch back to RAW for slewing to next point
            // When arriving at next point, HandleArrivedAtTarget will switch to CORRECTED
            // with target recalculated from corrected position → no position jump
            _trackingController.UseRawPosition = true;
            if (_pollingManager?.RmsTrackingManager != null)
            {
                _pollingManager.RmsTrackingManager.SetAzimuthTarget(null);
                _pollingManager.RmsTrackingManager.SetAltitudeTarget(null);
            }

            // Reset flags
            _currentPointIndex++;
            _imageAddedForPoint = false;
            _exposureCompleted = false;
            _rmsStabilityStartTime = null;
            
            // [NEW] Reset target cache for new point
            _targetCached = false;

            if (_currentPointIndex >= _calibrationPoints.Count)
            {
                Stop();
                _state = CalibrationState.Completed;
                
                // [FIX] Wait for all pending plate solves to complete before showing dialog
                // Skip dialog if this is manual add point mode
                if (!_isManualAddMode)
                {
                    _ = WaitForAllSolvesAndShowDialog();
                }
                else
                {
                    LogInfo("[MANUAL ADD] Point completed - skipping completion dialog");
                    _isManualAddMode = false;  // Reset flag
                }
            }
            else
            {
                _state = CalibrationState.Slewing;
                
                // [CRITICAL FIX] Immediately calculate and set target for next point!
                // Without this, tracking controller keeps driving toward the OLD target
                // until the next Update() cycle (50ms) calls HandleSlewing().
                // This causes mount to briefly move in wrong direction after each point.
                var nextPoint = _calibrationPoints[_currentPointIndex];
                
                // Get current RAW position for path calculation
                var azInfoNext = _pollingManager?.AzimuthInfo;
                double currentAzForNext = azInfoNext != null
                    ? (azInfoNext.Raw_Position / (double)_azimuthConfig.Encoder_Resolution) * 360.0 + _azimuthConfig.Position_Offset_Deg
                    : 0;
                
                // Calculate unwrapped target azimuth (shortest path)
                _cachedTargetAzimuth = MountCalibrationHelper.UnwrapAzimuthToMountRange(
                    nextPoint.TargetAzimuth,
                    currentAzForNext,
                    _azimuthConfig.CCW_Limit_Deg,
                    _azimuthConfig.CW_Limit_Deg);
                _cachedTargetAltitude = nextPoint.TargetAltitude;
                
                // Convert Alt/Az → RA/Dec ONCE and cache
                var (raNext, decNext) = MountCalibrationHelper.ConvertAltAzToRaDec(
                    _cachedTargetAltitude,
                    _cachedTargetAzimuth,
                    _observerLocation.Latitude,
                    _observerLocation.Longitude,
                    NtpTimeService.Instance.GetCurrentUtcTime());
                _cachedTargetRA = raNext;
                _cachedTargetDec = decNext;
                _targetCached = true;
                
                // Set new target on tracking controller IMMEDIATELY
                // This ensures mount starts slewing toward new point without any gap
                _trackingController.SetTarget(_cachedTargetRA, _cachedTargetDec, _observerLocation, isApparent: true);
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 🎯 MoveToNextPoint: Immediate target set for point #{nextPoint.Index}");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    Az={_cachedTargetAzimuth:F2}° Alt={_cachedTargetAltitude:F2}° → RA={_cachedTargetRA:F4}h Dec={_cachedTargetDec:F4}°");
            }
        }
        
        /// <summary>
        /// Wait for all pending plate solves to complete, then show completion dialog
        /// </summary>
        private async Task WaitForAllSolvesAndShowDialog()
        {
            if (_completionDialogShown || _completionDialogRequested)
            {
                LogInfo("Completion dialog already requested; skipping duplicate request.");
                return;
            }

            _completionDialogRequested = true;

            // Plate solve now targets a fast-fail path, so do not hold the completion dialog for minutes.
            int maxWaitMs = 20000;
            int waitedMs = 0;
            int checkIntervalMs = 500;
            
            LogInfo($"Waiting for all plate solves to complete...");
            
            while (waitedMs < maxWaitMs)
            {
                // Check if any points are still solving OR pending solve
                bool anyStillSolving = _calibrationPoints.Any(p => p.IsSolving);
                bool anyPending = _calibrationPoints.Any(p => p.IsCompleted && !p.IsSolved && !p.SolveFailed);
                
                int stillSolvingCount = _calibrationPoints.Count(p => p.IsSolving);
                int pendingCount = _calibrationPoints.Count(p => p.IsCompleted && !p.IsSolved && !p.SolveFailed);
                int solvedCount = _calibrationPoints.Count(p => p.IsSolved);
                int failedCount = _calibrationPoints.Count(p => p.SolveFailed);
                
                LogInfo($"Status - Solving: {stillSolvingCount}, Pending: {pendingCount}, Solved: {solvedCount}, Failed: {failedCount}");
                
                if (!anyStillSolving && !anyPending)
                {
                    // All solves complete!
                    LogInfo($"All plate solves complete! Showing dialog...");
                    break;
                }
                
                // Wait a bit and check again
                await Task.Delay(checkIntervalMs);
                waitedMs += checkIntervalMs;
            }
            
            if (waitedMs >= maxWaitMs)
            {
                LogError($"Timeout waiting for plate solves! Showing dialog anyway...");
                ForceFinalizeLingeringSolveStates("completion-timeout");
            }
            
            // Show completion dialog
            SaveSessionCheckpoint("before-completion-dialog");
            _completionDialogShown = true;
            await ShowCompletionDialogAsync();
        }

        private void ForceFinalizeLingeringSolveStates(string reason)
        {
            foreach (var point in _calibrationPoints.Where(p => p.IsSolving || (p.IsCompleted && !p.IsSolved && !p.SolveFailed)))
            {
                point.IsSolving = false;
                point.IsCapturing = false;
                point.IsTracking = false;
                point.IsCompleted = true;
                point.IsSolved = false;
                point.SolveFailed = true;
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Forced {reason} failure for point #{point.Index}");
            }
        }

        /// <summary>
        /// Capture calibration image
        /// ? PIPELINE OPTIMIZATION: Sets _exposureCompleted immediately after camera readout,
        /// allowing mount to slew while image processing continues in background!
        /// </summary>
        private async Task CaptureImageAsync(int pointNumber, CalibrationPoint point, double mountAz, double mountAlt)
        {
            if (_isCapturingImage) return;
            _isCapturingImage = true;
            
            // [NEW] Set capturing state
            point.IsCapturing = true;
            // [CRITICAL FIX] Do NOT set point.IsTracking = false here!
            // IsTracking is used as a LOCK in Update() to keep calling HandleArrivedAtTarget().
            // Setting it to false causes Update() to fall through to HandleSlewing(),
            // which re-slews to the same point and never reaches _exposureCompleted check.

            try
            {

                // Calculate RA/Dec for current position at capture start time
                var captureStartTime = DateTime.UtcNow;
                
                // [IMPROVEMENT] Use CORRECTED position for RA/Dec hint when model is loaded
                // Corrected position ≈ true sky position → more accurate plate solver hint
                // Raw position is still stored separately for model building (line below)
                bool hasModelForHint = App.PointingModelManager?.IsModelLoaded == true;
                double hintAz = mountAz;
                double hintAlt = mountAlt;
                if (hasModelForHint && _pollingManager?.MountInfo != null)
                {
                    hintAz = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                    hintAlt = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;
                }
                
                var (raHours, decDeg) = ConvertAltAzToRaDec(hintAlt, hintAz, captureStartTime);
                double raDeg = raHours * 15.0;
                
                // [CRITICAL FIX] Store BOTH raw counts AND degrees with offset
                // Raw counts needed for model, degrees needed for display/calculation
                if (_pollingManager?.AzimuthInfo != null && _pollingManager?.AltitudeInfo != null)
                {
                    // Store RAW encoder counts (no offset) for model
                    point.CurrentAzimuthRawCounts = _pollingManager.AzimuthInfo.Raw_Position;
                    point.CurrentAltitudeRawCounts = _pollingManager.AltitudeInfo.Raw_Position;
                    
                    // Store degrees with offset for display
                    point.CurrentAzimuth = mountAz;
                    point.CurrentAltitude = mountAlt;
                }
                else if (_pollingManager?.MountInfo != null)
                {
                    // [P1 FIX] Fallback: Use MountInfo CORRECTED position (not Raw!)
                    // CurrentAzimuth/CurrentAltitude are for display/calculation = sky coordinates
                    point.CurrentAzimuth = _pollingManager.MountInfo.Act_Azimuth_Pos_Correct;
                    point.CurrentAltitude = _pollingManager.MountInfo.Act_Altitude_Pos_Correct;
                    
                    // Cannot get raw counts - will cause model error!
                    System.Diagnostics.Debug.WriteLine("[CALIBRATION] ⚠️ Cannot get raw encoder counts!");
                }
                else
                {
                    // Ultimate fallback
                    point.CurrentAzimuth = mountAz;
                    point.CurrentAltitude = mountAlt;
                }
                
                // ========================================================================
                // [CRITICAL FIX] Following Narit's pattern EXACTLY:
                // 
                // Narit stores APPARENT coordinates (from Alt/Az conversion)
                // and compares Solved J2000 → Apparent vs Expected Apparent
                // 
                // From Narit's BuildPointingModel.cs:
                //   currentSample.EstimatedRaAppRads = raDecApp.Ra.Rads;   // APPARENT!
                //   currentSample.EstimatedDecAppRads = raDecApp.Dec.Rads; // APPARENT!
                //   
                // And in PlateSolvePoints():
                //   RaDec solvedRaDecApp = RaDec.FromRads(...).J2000ToNow(); // Convert to Apparent
                //   RaError = solvedRaDecApp.Ra.Rads - sample.EstimatedRaAppRads; // Apparent vs Apparent
                //
                // We MUST use this same pattern!
                // ========================================================================
                
                double raApp_Rads = raHours * Math.PI / 12.0;   // Apparent RA (hours → radians)
                double decApp_Rads = decDeg * Math.PI / 180.0;  // Apparent Dec (degrees → radians)
                double julianDate = PrecessionCalculator.DateTimeToJulianDate(captureStartTime);
                
                // [NARIT PATTERN] Store APPARENT coordinates (NOT J2000!)
                // Error calculation will be: Apparent vs Apparent
                point.ExpectedRa_App_Rads = raApp_Rads;     // NEW: Store Apparent RA
                point.ExpectedDec_App_Rads = decApp_Rads;   // NEW: Store Apparent Dec
                
                // Also convert to J2000 for plate solver hint (PWLib needs J2000 guess)
                // This is ONLY used as a hint for plate solver, NOT for error calculation!
                var (raJ2000_Rads, decJ2000_Rads) = PrecessionCalculator.ApparentToJ2000(
                    raApp_Rads, 
                    decApp_Rads, 
                    julianDate);
                
                // Store J2000 for plate solver hint only
                point.ExpectedRa_J2000_Rads = raJ2000_Rads;
                point.ExpectedDec_J2000_Rads = decJ2000_Rads;
                point.CaptureTime = captureStartTime;
                point.JulianDate = julianDate;  // Store JD for J2000ToNow conversion
                
                // [DEBUG] ====== COMPREHENSIVE COORDINATE DEBUG ======
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG] ========== Point #{pointNumber} Coordinate Calculation ==========");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG] Input:");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   Mount Raw Az/Alt: {mountAz:F4}° / {mountAlt:F4}°");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   Hint Az/Alt ({(hasModelForHint ? "CORRECTED" : "RAW")}): {hintAz:F4}° / {hintAlt:F4}°");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   Time (UTC): {captureStartTime:yyyy-MM-dd HH:mm:ss.fff}");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   Location: Lat={_observerLocation.Latitude:F6}°, Lon={_observerLocation.Longitude:F6}°");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   Julian Date: {julianDate:F6}");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG] Conversion Steps:");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   1. Alt/Az → RA/Dec (Apparent): RA={raHours:F6}h ({raDeg:F4}°), Dec={decDeg:F6}°");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   [STORED] Expected APPARENT: RA={raApp_Rads:F8} rad, Dec={decApp_Rads:F8} rad");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG]   [HINT] J2000 for solver: RA={raJ2000_Rads * 12.0 / Math.PI:F6}h, Dec={decJ2000_Rads * 180.0 / Math.PI:F6}°");
                System.Diagnostics.Debug.WriteLine($"[COORD-DEBUG] ====================================================");
                System.Diagnostics.Debug.WriteLine($"");

                

                // [CRITICAL FIX] Wait for camera exposure to complete before setting _exposureCompleted!
                // This ensures mount doesn't move while camera is still exposing!
                var cameraSettings = AppSettings.GetCameraSettings();
                int exposureTimeMs = cameraSettings.ExposureTime_ms;
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 📷 Starting exposure: {exposureTimeMs}ms");
                
                bool captureQueued = await CaptureCameraImageAsync(pointNumber, point);
                if (!captureQueued)
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point #{pointNumber}: Capture did not produce a solve job");
                    point.SolveFailed = true;
                    point.IsSolving = false;
                    _exposureCompleted = true; // Let pipeline advance and clear pending state.
                    point.IsCapturing = false;
                    return;
                }
                
                // Add a short post-readout buffer before advancing to the next point.
                if (_postExposureBufferMs > 0)
                {
                    await Task.Delay(_postExposureBufferMs);
                }
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✅ Exposure complete + {_postExposureBufferMs}ms buffer");

                // ✅ Set exposure complete ONLY AFTER camera readout finishes!
                _exposureCompleted = true;
                
                // [FIX] Pre-set IsSolving so pipeline check sees it immediately
                // ProcessAndQueueCameraImage (background) takes time before actual solve starts
                point.IsSolving = true;
                
                // [NEW] Clear capturing state (exposure done, now waiting for processing)
                point.IsCapturing = false;
                
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point #{pointNumber}: CaptureImageAsync exception: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]     Stack: {ex.StackTrace}");
                point.SolveFailed = true;
                point.IsSolved = false;
                point.IsSolving = false;
                point.IsCapturing = false;  // Clear state on error
                _exposureCompleted = true;  // Let pipeline advance and mark this point failed.
            }
            finally
            {
                _isCapturingImage = false;
            }
        }

        /// <summary>
        /// Poll for new images (called by UI timer)
        /// </summary>
        public CalibrationImageData PollNextImage()
        {
            if (_imageQueue.TryDequeue(out var imageData))
            {
                return imageData;
            }
            return null;
        }

        /// <summary>
        /// Capture image from camera only (ASYNC - Returns immediately after readout)
        /// ? PIPELINE OPTIMIZATION: Image processing happens in background!
        /// </summary>
        private async Task<bool> CaptureCameraImageAsync(int pointNumber, CalibrationPoint point)
        {
            if (_camera == null)
            {
                point.SolveFailed = true;
                point.IsSolving = false;
                return false;
            }

            
            // IMPORTANT: Apply saved camera settings before capture
            ApplySavedCameraSettings();
            
            // ? Camera capture (waits for exposure + readout)
            var cameraImage = await _camera.CaptureImageAsync();
            
            
            if (cameraImage == null)
            {
                point.SolveFailed = true;
                point.IsSolving = false;
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point #{pointNumber}: camera returned null image");
                return false;
            }

            // ? PIPELINE OPTIMIZATION: Process image in background (fire-and-forget)
            // Don't await this! Let it run while mount moves to next point
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessAndQueueCameraImage(pointNumber, point, cameraImage);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ ProcessAndQueueCameraImage FAILED for point {pointNumber}: {ex.Message}");
                    // Ensure solve status is set even if processing crashes
                    if (!point.IsSolved && !point.SolveFailed)
                    {
                        point.SolveFailed = true;
                        point.IsSolving = false;
                    }
                }
            });
            
            // Method returns immediately - mount can slew now!
            return true;
        }

        /// <summary>
        /// Process camera image in background (star detection + preview creation + queue)
        /// This runs in parallel with mount slewing to next point!
        /// [FIX] FITS+Solve runs FIRST (critical path), StarDetect+Preview runs after (non-critical)
        /// </summary>
        private async Task ProcessAndQueueCameraImage(int pointNumber, CalibrationPoint point, CameraImageData cameraImage)
        {
            // [PRIORITY] Show the newest captured image in the UI first so the operator
            // can clearly see which frame belongs to the current point before solve
            // results begin updating the grid.
            BitmapImage previewImage = null;

            try
            {
                previewImage = await cameraImage.GetPreviewImageAsync(maxWidth: 800, maxHeight: 600);

                if (previewImage != null)
                {
                    if (previewImage.CanFreeze && !previewImage.IsFrozen)
                        previewImage.Freeze();
                }
                else
                {
                    previewImage = cameraImage.GetBitmapImage();

                    if (previewImage != null && previewImage.CanFreeze && !previewImage.IsFrozen)
                        previewImage.Freeze();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] Preview creation failed: {ex.Message}");
                try
                {
                    previewImage = cameraImage.GetBitmapImage();
                    if (previewImage != null && previewImage.CanFreeze && !previewImage.IsFrozen)
                        previewImage.Freeze();
                }
                catch { /* Preview is non-critical */ }
            }

            // [QUEUE] Add to display queue before solve finishes.
            if (previewImage != null)
            {
                _imageQueue.Enqueue(new CalibrationImageData
                {
                    PointNumber = pointNumber,
                    Image = previewImage,
                    Azimuth = point.TargetAzimuth,
                    Altitude = point.TargetAltitude,
                    RawImageData = cameraImage
                });
            }

            // [CRITICAL PATH] Save FITS and start plate solving after the preview has
            // already been queued for UI display. This method is ALREADY running inside
            // a background fire-and-forget task, so we must await here to keep failures
            // on the same async chain instead of losing them in a nested fire-and-forget.
            try
            {
                await SaveFITSAndSolveAsync(point, cameraImage, point.CurrentAzimuth, point.CurrentAltitude);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ SaveFITSAndSolveAsync failed for point {pointNumber}: {ex.Message}");
                point.SolveFailed = true;
                point.IsSolving = false;
            }
        }

        /// <summary>
        /// Calculate RMS errors
        /// </summary>
        private void CalculateRmsErrors(CalibrationPoint point)
        {
            // This would calculate from collected samples
            // For now, use current RMS
            point.AzimuthRmsError_Arcsec = point.CurrentRmsError;
            point.AltitudeRmsError_Arcsec = point.CurrentRmsError;
        }

        /// <summary>
        /// Unwrap azimuth to mount range
        /// </summary>
        private double UnwrapAzimuthToMountRange(double targetAz_0to360, double currentAz)
        {
            double ccwLimit = _azimuthConfig.CCW_Limit_Deg;
            double cwLimit = _azimuthConfig.CW_Limit_Deg;

            double option1 = targetAz_0to360;
            double option2 = targetAz_0to360 - 360.0;
            double option3 = targetAz_0to360 + 360.0;

            var validOptions = new List<(double angle, double distance)>();

            if (option1 >= ccwLimit && option1 <= cwLimit)
                validOptions.Add((option1, Math.Abs(option1 - currentAz)));

            if (option2 >= ccwLimit && option2 <= cwLimit)
                validOptions.Add((option2, Math.Abs(option2 - currentAz)));

            if (option3 >= ccwLimit && option3 <= cwLimit)
                validOptions.Add((option3, Math.Abs(option3 - currentAz)));

            if (validOptions.Count == 0)
                return option2;

            return validOptions.OrderBy(x => x.distance).First().angle;
        }

        /// <summary>
        /// Convert Alt/Az to RA/Dec
        /// </summary>
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

            double hourAngleDeg = hourAngleRad * 180.0 / Math.PI;
            double hourAngleHours = hourAngleDeg / 15.0;

            double lst = CalculateLocalSiderealTime(utc);
            double raHours = lst - hourAngleHours;

            raHours = raHours % 24.0;
            if (raHours < 0) raHours += 24.0;

            return (raHours, decDegrees);
        }

        private double CalculateLocalSiderealTime(DateTime utc)
        {
            double jd = ToJulianDate(utc);
            double t = (jd - 2451545.0) / 36525.0;

            double gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0) +
                         0.000387933 * t * t - t * t * t / 38710000.0;

            gmst = gmst % 360.0;
            if (gmst < 0) gmst += 360.0;
            gmst = gmst / 15.0;

            double lst = gmst + _observerLocation.Longitude / 15.0;
            lst = lst % 24.0;
            if (lst < 0) lst += 24.0;

            return lst;
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
            jd += dayFraction;

            return jd;
        }
        
        /// <summary>
        /// Format decimal hours to HMS string (HH:MM:SS.SS)
        /// </summary>
        private string FormatHoursToHMS(double hours)
        {
            // Ensure positive
            if (hours < 0) hours += 24.0;
            if (hours >= 24.0) hours -= 24.0;
            
            int h = (int)hours;
            double remainder = (hours - h) * 60.0;
            int m = (int)remainder;
            double s = (remainder - m) * 60.0;
            
            return $"{h:D2}:{m:D2}:{s:F2}";
        }
        
        /// <summary>
        /// Format decimal degrees to DMS string (+DD:MM:SS.S)
        /// </summary>
        private string FormatDegreesToDMS(double degrees)
        {
            bool isNegative = degrees < 0;
            degrees = Math.Abs(degrees);
            
            int d = (int)degrees;
            double remainder = (degrees - d) * 60.0;
            int m = (int)remainder;
            double s = (remainder - m) * 60.0;
            
            return $"{(isNegative ? "-" : "+")}{d:D2}:{m:D2}:{s:F1}";
        }
        
        /// <summary>
        /// Calculate airmass from altitude
        /// Uses Hardie (1962) formula for zenith angle < 80°
        /// </summary>
        private double CalculateAirmass(double altitudeDeg)
        {
            if (altitudeDeg <= 0) return 10.0; // Below horizon
            
            double zenithAngleDeg = 90.0 - altitudeDeg;
            double zenithRad = zenithAngleDeg * Math.PI / 180.0;
            
            if (zenithAngleDeg > 80.0)
            {
                // Very low altitude - use simple sec(z)
                return 1.0 / Math.Cos(zenithRad);
            }
            
            // Hardie (1962) formula: X = sec(z) - 0.0018167(sec(z) - 1) - 0.002875(sec(z) - 1)^2 - 0.0008083(sec(z) - 1)^3
            double secZ = 1.0 / Math.Cos(zenithRad);
            double secZminus1 = secZ - 1.0;
            
            double airmass = secZ - 0.0018167 * secZminus1 - 0.002875 * Math.Pow(secZminus1, 2) - 0.0008083 * Math.Pow(secZminus1, 3);
            
            return airmass;
        }

        /// <summary>
        /// Apply saved camera settings from AppSettings before capture
        /// This ensures calibration uses the same settings configured in Camera Settings Window
        /// </summary>
        private void ApplySavedCameraSettings()
        {
            if (_camera == null || !_camera.IsConnected)
                return;

            try
            {
                var settings = AppSettings.GetCameraSettings();
                if (settings == null)
                    return;

                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] Applying saved camera settings: Exposure={settings.ExposureTime_ms}ms, Bin={settings.Binning}x{settings.Binning}");

                // Apply exposure, gain, offset
                _camera.SetExposure(settings.ExposureTime_ms);
                _camera.SetBinning(settings.Binning);

                bool success = true;

                if (_camera is ZWOCameraProvider zwoProvider)
                {
                    zwoProvider.SetGain(settings.Gain);
                    zwoProvider.SetOffset(settings.Offset);

                    int width = settings.Width > 0 ? settings.Width : zwoProvider.MaxWidth;
                    int height = settings.Height > 0 ? settings.Height : zwoProvider.MaxHeight;
                    var imageType = (ZWOASICameraSDK.ASI_IMG_TYPE)settings.ImageType;
                    success = zwoProvider.SetROIAndImageType(
                        width,
                        height,
                        settings.Binning,
                        imageType,
                        out var errorCode);
                }
            }
            catch (Exception)
            {
            }
        }

        // ========================================================================
        // FITS EXPORT & PLATE SOLVING (NEW)
        // ========================================================================
        
        /// <summary>
        /// Save captured image as FITS file and trigger plate solving
        /// Called automatically after image capture
        /// </summary>
        private async Task SaveFITSAndSolveAsync(
            CalibrationPoint point, 
            CameraImageData imageData, 
            double mountAz, 
            double mountAlt)
        {
            if (imageData == null || imageData.ImageData == null)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point #{point?.Index}: camera image data is null - cannot save FITS");
                if (point != null)
                {
                    point.SolveFailed = true;
                    point.IsSolving = false;
                }
                return;
            }
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 💾 Point #{point.Index}: SaveFITSAndSolveAsync starting");
                
                // [CRITICAL FIX] Use pre-stored RA/Dec from point object (calculated at capture start)
                // This ensures synthetic and plate solve use SAME coordinates (no time drift!)
                if (!point.ExpectedRa_J2000_Rads.HasValue || !point.ExpectedDec_J2000_Rads.HasValue)
                {
                    
                    // Fallback: Calculate now (but this defeats the synchronization!)
                    var captureTime = imageData.CaptureTime;
                    var (raHours_fallback, decDeg_fallback) = ConvertAltAzToRaDec(mountAlt, mountAz, captureTime.ToUniversalTime());
                    point.ExpectedRa_J2000_Rads = raHours_fallback * Math.PI / 12.0;
                    point.ExpectedDec_J2000_Rads = decDeg_fallback * Math.PI / 180.0;
                    
                }
                
                // Convert from radians to degrees for display
                double raHours = point.ExpectedRa_J2000_Rads.Value * 12.0 / Math.PI;
                double decDeg = point.ExpectedDec_J2000_Rads.Value * 180.0 / Math.PI;
                double raDeg = raHours * 15.0;
                
                
                // Prepare FITS metadata (extended format matching Narit's SetFITSKey)
                var cameraSettings = AppSettings.GetCameraSettings();
                
                // [CRITICAL] Calculate binned plate scale for FITS metadata
                // FITS must reflect actual binned pixels, not unbinned!
                int binning = imageData.Binning;
                double fitsPlateScale = cameraSettings.PlateScale_ArcsecPerPixel * binning;
                
                // Calculate Julian Date
                double julianDate = PrecessionCalculator.DateTimeToJulianDate(point.CaptureTime ?? NtpTimeService.Instance.GetCurrentUtcTime());
                
                // Calculate Local Sidereal Time (HMS format)
                double lst = CalculateLocalSiderealTime(point.CaptureTime ?? NtpTimeService.Instance.GetCurrentUtcTime());
                string lstHMS = FormatHoursToHMS(lst);
                
                // Format RA/Dec as HMS/DMS (for OBJCTRA/OBJCTDEC keywords)
                string raHMS = FormatHoursToHMS(raHours);
                string decDMS = FormatDegreesToDMS(decDeg);
                
                // Calculate airmass
                double airmass = CalculateAirmass(mountAlt);
                
                var metadata = new FITSMetadata
                {
                    // Mount position
                    Azimuth_Deg = mountAz,
                    Altitude_Deg = mountAlt,
                    
                    // Expected sky position (J2000 for plate solving)
                    ExpectedRA_Deg = raDeg,
                    ExpectedDec_Deg = decDeg,
                    
                    // Observer location
                    ObserverLatitude_Deg = _observerLocation.Latitude,
                    ObserverLongitude_Deg = _observerLocation.Longitude,
                    ObserverAltitude_m = _observerLocation.Altitude,
                    
                    // Optical system
                    FocalLength_mm = cameraSettings.FocalLength_mm,
                    PlateScale_ArcsecPerPixel = fitsPlateScale,  // Binned plate scale
                    
                    // Calibration point
                    PointIndex = point.Index,
                    
                    // [NEW] Extended metadata (matching Narit's format)
                    Airmass = airmass,
                    JulianDate = julianDate,
                    LocalSiderealTime = lstHMS,
                    TelescopeName = "ForkMount",
                    ObjectRA_HMS = raHMS,
                    ObjectDec_DMS = decDMS,
                    FocuserPosition = 0,      // Not used yet
                    RotatorAngle = 0.0,       // Not used yet
                    ApertureDiameter_mm = 0.0 // Not configured yet
                };
                
                // Generate FITS filename
                string timestamp = point.CaptureTime.HasValue 
                    ? point.CaptureTime.Value.ToString("yyyyMMdd_HHmmss")
                    : NtpTimeService.Instance.GetCurrentTime().ToString("yyyyMMdd_HHmmss");
                string fitsFilename = $"point_{point.Index:D3}_{timestamp}.fits";
                string fitsPath = Path.Combine(_fitsDirectory, fitsFilename);
                
                // Save FITS file
                bool fitsSaved = FITSWriter.WriteFITS(imageData, fitsPath, metadata);
                
                if (!fitsSaved)
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point #{point.Index}: FITSWriter.WriteFITS returned false");
                    point.SolveFailed = true;
                    point.IsSolved = false;
                    point.IsSolving = false;
                    return;
                }
                
                point.FitsFilePath = fitsPath;
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✅ Point #{point.Index}: FITS saved to {fitsPath}");
                
                // [CRITICAL] Wait for OS to flush file to disk!
                // PWLib needs file to be fully written before opening
                await Task.Delay(200);  // 200ms delay
                
                
                // Start plate solving (async, queued - one at a time!)
                // [CRITICAL FIX] Use SemaphoreSlim to prevent concurrent plate solves!
                // PWLib BackgroundPlateSolver has shared state and CANNOT run concurrently!
                // Still fire-and-forget so mount can move to next point while solving
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 🔎 Point #{point.Index}: Queueing serialized plate solve");
                _ = SolvePointSerializedAsync(point, fitsPlateScale, imageData.Width, imageData.Height);
                
                
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point #{point?.Index}: SaveFITSAndSolveAsync exception: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]     Stack: {ex.StackTrace}");
                if (point != null)
                {
                    point.SolveFailed = true;
                    point.IsSolved = false;
                    point.IsSolving = false;
                }
            }
        }
        
        /// <summary>
        /// [CRITICAL FIX] Serialized wrapper - ensures only ONE plate solve runs at a time!
        /// PWLib BackgroundPlateSolver has shared internal state and crashes or gives wrong
        /// results when multiple instances run concurrently.
        /// [MEMORY FIX] Added timeout to prevent permanent semaphore hold if solver hangs.
        /// </summary>
        private async Task SolvePointSerializedAsync(CalibrationPoint point, double plateScale, int imageWidth, int imageHeight)
        {
            const int SEMAPHORE_TIMEOUT_MS = 180_000; // 3 minutes max wait for semaphore
            
            bool acquired = await _solveSemaphore.WaitAsync(SEMAPHORE_TIMEOUT_MS);
            if (!acquired)
            {
                System.Diagnostics.Debug.WriteLine($"[SOLVE-QUEUE] ⚠️ Point #{point.Index}: Semaphore timeout after {SEMAPHORE_TIMEOUT_MS / 1000}s — skipping solve");
                point.SolveFailed = true;
                point.IsSolving = false;
                return;
            }
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"[SOLVE-QUEUE] Point #{point.Index}: Acquired semaphore, starting solve...");
                
                // Add a CancellationTokenSource with timeout for the solve itself
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
                var solveTask = SolvePointAsync(point, plateScale, imageWidth, imageHeight);
                var completedTask = await Task.WhenAny(solveTask, Task.Delay(Timeout.Infinite, cts.Token));
                
                if (completedTask != solveTask)
                {
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-QUEUE] ⚠️ Point #{point.Index}: Solve timeout — marking as failed");
                    point.SolveFailed = true;
                    point.IsSolving = false;
                }
                else
                {
                    await solveTask; // Propagate any exceptions
                }
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[SOLVE-QUEUE] Point #{point.Index}: Solve cancelled (timeout)");
                point.SolveFailed = true;
                point.IsSolving = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SOLVE-QUEUE] Point #{point.Index}: Solve error: {ex.Message}");
                point.SolveFailed = true;
                point.IsSolving = false;
            }
            finally
            {
                _solveSemaphore.Release();
                System.Diagnostics.Debug.WriteLine($"[SOLVE-QUEUE] Point #{point.Index}: Released semaphore");
            }
        }
        
        /// <summary>
        /// Solve point using plate solver (async, runs in background)
        /// [NEW] Includes adaptive pixel scale update (from Narit)
        /// </summary>
        private async Task SolvePointAsync(CalibrationPoint point, double plateScale, int imageWidth, int imageHeight)
        {
            if (point.FitsFilePath == null || _plateSolver == null)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point #{point?.Index}: SolvePointAsync aborted - FITS path or plate solver missing");
                if (point != null)
                {
                    point.SolveFailed = true;
                    point.IsSolved = false;
                    point.IsSolving = false;
                }
                return;
            }
            
            try
            {
                
                // Update status
                point.IsSolving = true;
                
                // Solve image (pass actual CCD dimensions and settings!)
                var result = await _plateSolver.SolveImageAsync(
                    point.FitsFilePath,
                    point.ExpectedRa_J2000_Rads.Value,
                    point.ExpectedDec_J2000_Rads.Value,
                    plateScale,
                    searchRadius_Deg: _plateSolverSearchRadiusDeg,  // Use setting value instead of hardcode
                    imageWidth: imageWidth,   // From CCD!
                    imageHeight: imageHeight  // From CCD!
                );
                
                if (result.Success)
                {
                    // Store solved coordinates as J2000 for PointXP/.pxp compatibility.
                    // For plate-solve error reporting we convert them to apparent coordinates
                    // at the capture time and compare apparent-vs-apparent.
                    double solvedRa_J2000_Rads = result.CenterRa_J2000_Rads;
                    double solvedDec_J2000_Rads = result.CenterDec_J2000_Rads;

                    point.SolvedRa_J2000_Rads = solvedRa_J2000_Rads;
                    point.SolvedDec_J2000_Rads = solvedDec_J2000_Rads;
                    point.NumMatchedStars = result.NumMatchedStars;
                    point.NumExtractedStars = result.NumExtractedStars;
                    point.RotationAngle_Deg = result.RotationAngle_Deg;
                    point.MeasuredPlateScale_ArcsecPerPixel = result.PlateScale_ArcsecPerPixel;

                    double solvedRa_App_Rads = solvedRa_J2000_Rads;
                    double solvedDec_App_Rads = solvedDec_J2000_Rads;
                    if (point.JulianDate.HasValue)
                    {
                        (solvedRa_App_Rads, solvedDec_App_Rads) = PrecessionCalculator.J2000ToApparent(
                            solvedRa_J2000_Rads,
                            solvedDec_J2000_Rads,
                            point.JulianDate.Value);
                    }
                    
                    // Calculate errors: APPARENT vs APPARENT
                    var (raErr, decErr, totalErr) = PlateSolverManager.CalculatePointingError(
                        point.ExpectedRa_App_Rads.Value,    // Expected APPARENT
                        point.ExpectedDec_App_Rads.Value,   // Expected APPARENT
                        solvedRa_App_Rads,                  // Solved (PWLib = APPARENT!)
                        solvedDec_App_Rads                  // Solved (PWLib = APPARENT!)
                    );
                    
                    point.RaError_Arcsec = raErr;
                    point.DecError_Arcsec = decErr;
                    point.IsSolved = true;
                    point.SolveFailed = false;
                    
                    // [DEBUG] ====== PLATE SOLVE RESULT DEBUG ======
                    System.Diagnostics.Debug.WriteLine($"");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] ========== Point #{point.Index} Plate Solve Result ==========");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] [FIX] PWLib solve stored as J2000, converted to apparent for error calculation");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] Expected APPARENT (from Alt/Az):");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   RA:  {point.ExpectedRa_App_Rads.Value * 12.0 / Math.PI:F6}h");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   Dec: {point.ExpectedDec_App_Rads.Value * 180.0 / Math.PI:F6}°");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] Solved J2000 (from PWLib):");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   RA:  {solvedRa_J2000_Rads * 12.0 / Math.PI:F6}h");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   Dec: {solvedDec_J2000_Rads * 180.0 / Math.PI:F6}°");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] Solved APPARENT (converted from J2000 at capture JD):");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   RA:  {solvedRa_App_Rads * 12.0 / Math.PI:F6}h");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   Dec: {solvedDec_App_Rads * 180.0 / Math.PI:F6}°");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] Pointing Error (Apparent vs Apparent - NO J2000 CONVERSION!):");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   RA Error:    {raErr:F2}\" ({raErr / 60.0:F2}')");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   Dec Error:   {decErr:F2}\" ({decErr / 60.0:F2}')");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG]   Total Error: {totalErr:F2}\" ({totalErr / 60.0:F2}')");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] Stars: {result.NumMatchedStars} matched / {result.NumExtractedStars} extracted");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] Rotation: {result.RotationAngle_Deg:F2}°");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] Plate Scale: {result.PlateScale_ArcsecPerPixel:F3}\"/pixel");
                    System.Diagnostics.Debug.WriteLine($"[SOLVE-DEBUG] ====================================================");
                    System.Diagnostics.Debug.WriteLine($"");
                    
                    // [NEW] Adaptive pixel scale update (from Narit's UpdatePixelScaleIfGoodSolution)
                    // If solve is high-quality, update plate scale for next captures
                    UpdatePixelScaleIfGoodSolution(result, _camera?.CurrentBinning ?? 1);

                    int currentSolvedCount = _calibrationPoints.Count(p => p.IsSolved);

                    if (_pointingModelBackendMode == PointingModelBackendMode.ExperimentalCoreDirect)
                    {
                        RebuildExperimentalPointingModel();
                    }
                    else
                    {
                        // [NEW] Append point to progressive model file (thread-safe)
                        AppendPointToProgressiveFile(point);

                        // [NEW] Update partial model if enough points (3+)
                        if (currentSolvedCount >= 3)
                        {
                            System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✓ {currentSolvedCount} points solved - updating model...");
                            UpdatePartialModel();
                        }
                    }

                    SaveSessionCheckpoint($"solve-success-point-{point.Index}");
                    
                    // [NEW] Apply first point offset if this is first manual point
                    if (_applyFirstPointOffset && currentSolvedCount == 1)
                    {
                        ApplyFirstPointOffset(point);
                        _applyFirstPointOffset = false;  // Clear flag
                    }
                    
                    if (_pointingModelBackendMode == PointingModelBackendMode.LegacyPxpReload)
                    {
                        // [PROGRESSIVE RELOAD] Reload progressive model into App.PointingModelManager
                        // after EVERY successful solve — applies corrections immediately!
                        // Works for both fresh start (new model) and additive (existing base model).
                        try
                        {
                            var pointingModelManager = App.PointingModelManager;
                            if (pointingModelManager != null && !string.IsNullOrEmpty(_progressiveModelPath)
                                && File.Exists(_progressiveModelPath))
                            {
                                bool reloaded = pointingModelManager.LoadModel(_progressiveModelPath);
                                if (reloaded)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✅ PointXP reloaded with {currentSolvedCount} solved point(s): {Path.GetFileName(_progressiveModelPath)}");
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ PointXP reload failed for {Path.GetFileName(_progressiveModelPath)}");
                                }
                            }
                        }
                        catch (Exception reloadEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ PointXP reload error: {reloadEx.Message}");
                        }
                    }
                    
                    // [UI UPDATE] Notify that plate solve is complete - update live image display with RMS error
                    OnSolveCompleted?.Invoke(point, result, totalErr);
                }
                else
                {
                    
                    point.SolveFailed = true;
                    point.IsSolved = false;
                    SaveSessionCheckpoint($"solve-failed-point-{point.Index}");
                }
            }
            catch (Exception)
            {
                point.SolveFailed = true;
                point.IsSolved = false;
                SaveSessionCheckpoint($"solve-exception-point-{point.Index}");
            }
            finally
            {
                point.IsSolving = false;
            }
        }

        private void TryAutoSaveCheckpoint()
        {
            if (!_isRunning)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastCheckpointSaveUtc).TotalSeconds < CHECKPOINT_INTERVAL_SECONDS)
            {
                return;
            }

            SaveSessionCheckpoint("periodic");
        }

        private void SaveSessionCheckpoint(string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_fitsDirectory))
                {
                    return;
                }

                Directory.CreateDirectory(_fitsDirectory);

                string checkpointPath = Path.Combine(_fitsDirectory, "calibration-checkpoint.json");
                var snapshot = new
                {
                    Reason = reason,
                    SavedAtLocal = DateTime.Now,
                    SavedAtUtc = DateTime.UtcNow,
                    SessionFolder = _sessionFolder,
                    TotalPoints,
                    CompletedPoints,
                    SolvedPoints,
                    FailedPoints,
                    ProcessedPoints,
                    CurrentPointIndex = _currentPointIndex,
                    State = _state.ToString(),
                    IsRunning = _isRunning,
                    ProgressiveModelPath = _progressiveModelPath,
                    EstimatedCompletionLocalTime,
                    Points = _calibrationPoints.Select(p => new
                    {
                        p.Index,
                        p.TargetAzimuth,
                        p.TargetAltitude,
                        p.IsCompleted,
                        p.IsSolved,
                        p.SolveFailed,
                        p.IsSolving,
                        p.TotalError_Arcsec,
                        p.NumMatchedStars,
                        p.FitsFilePath
                    }).ToList()
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                File.WriteAllText(checkpointPath, JsonSerializer.Serialize(snapshot, options));
                SaveAutosaveModelFile(reason);
                SaveRuntimeReadySnapshot(reason);
                _lastCheckpointSaveUtc = DateTime.UtcNow;
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 💾 Checkpoint saved ({reason}): {checkpointPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Checkpoint save failed ({reason}): {ex.Message}");
            }
        }

        private void SaveAutosaveModelFile(string reason)
        {
            try
            {
                string calibrationDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CalibrationData");
                Directory.CreateDirectory(calibrationDataDir);

                string autosaveModelPath = Path.Combine(calibrationDataDir, "PointingModel.autosave.pxp");

                if (_pointingModelBackendMode == PointingModelBackendMode.LegacyPxpReload &&
                    !string.IsNullOrWhiteSpace(_progressiveModelPath) &&
                    File.Exists(_progressiveModelPath))
                {
                    lock (_fileWriteLock)
                    {
                        File.Copy(_progressiveModelPath, autosaveModelPath, overwrite: true);
                    }

                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 💾 Autosave model updated ({reason}): {autosaveModelPath}");
                    return;
                }

                var solvedPointsList = _calibrationPoints
                    .Where(p => p.IsSolved && p.SolvedRa_J2000_Rads.HasValue && p.SolvedDec_J2000_Rads.HasValue)
                    .ToList();

                if (solvedPointsList.Count == 0)
                {
                    return;
                }

                var observerSettings = new Utility.ObserverLocationSettings
                {
                    Latitude_Deg = _observerLocation.Latitude,
                    Longitude_Deg = _observerLocation.Longitude,
                    Altitude_Meters = _observerLocation.Altitude
                };

                PointingModel.PlaneWaveModelFile.SavePlaneWaveFormat(autosaveModelPath, solvedPointsList, observerSettings);
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 💾 Autosave model regenerated ({reason}): {autosaveModelPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Autosave model save failed ({reason}): {ex.Message}");
            }
        }

        private void SaveRuntimeReadySnapshot(string reason)
        {
            try
            {
                int includedSolvedCount = _calibrationPoints.Count(p => p.IsSolved && p.IsIncludedInModel);
                if (includedSolvedCount < 3)
                {
                    return;
                }

                if (TryRefreshRuntimeReadySnapshot(out string snapshotPath, out string statusMessage) &&
                    !string.IsNullOrWhiteSpace(snapshotPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 💾 Runtime-ready snapshot updated ({reason}): {snapshotPath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Runtime-ready snapshot skipped ({reason}): {statusMessage}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Runtime-ready snapshot save failed ({reason}): {ex.Message}");
            }
        }
        
        /// <summary>
        /// [NEW] Update pixel scale if plate solve is high-quality
        /// Adapted from Narit's UpdatePixelScaleIfGoodSolution
        /// </summary>
        private void UpdatePixelScaleIfGoodSolution(PlateSolveResult result, int binning)
        {
            // Criteria for high-quality solve (from Narit):
            // - More than 20 matched stars, OR
            // - More than 8 matched stars AND matched > 50% of extracted
            bool isHighQuality = (result.NumMatchedStars > 20) ||
                                 (result.NumMatchedStars > 8 && result.NumMatchedStars > result.NumExtractedStars / 2);
            
            if (!isHighQuality)
                return;
            
            // Get current unbinned plate scale from settings
            var cameraSettings = AppSettings.GetCameraSettings();
            double currentUnbinnedScale = cameraSettings.PlateScale_ArcsecPerPixel;
            
            // Calculate measured unbinned plate scale
            double measuredUnbinnedScale = result.PlateScale_ArcsecPerPixel / binning;
            
            // Update only if difference > 10%
            double percentDiff = Math.Abs((currentUnbinnedScale - measuredUnbinnedScale) / currentUnbinnedScale);
            
            if (percentDiff > 0.1)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] 🔄 Adaptive Plate Scale Update:");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    High-confidence solve: {result.NumMatchedStars} matched / {result.NumExtractedStars} extracted");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    Old scale: {currentUnbinnedScale:F3} arcsec/pixel (unbinned)");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    New scale: {measuredUnbinnedScale:F3} arcsec/pixel (unbinned)");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    Difference: {percentDiff * 100.0:F1}%");
                
                // Update AppSettings (will be used for next captures)
                cameraSettings.PlateScale_ArcsecPerPixel = measuredUnbinnedScale;
                AppSettings.SaveCameraSettings(cameraSettings);
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION]    ✅ Plate scale updated for next captures");
            }
        }

        private void RebuildExperimentalPointingModel()
        {
            try
            {
                var pointingModelManager = App.PointingModelManager;
                if (pointingModelManager == null)
                {
                    _experimentalMetrics = ExperimentalPointingModelMetrics.CreatePending(0, 0, "PointingModelManager not available");
                    return;
                }

                var samples = BuildExperimentalCalibrationSamples();
                int includedCount = samples.Count(s => s.IsIncludedInModel);

                if (includedCount < 3)
                {
                    _experimentalMetrics = ExperimentalPointingModelMetrics.CreatePending(
                        samples.Count,
                        includedCount,
                        "Need at least 3 included solved points");
                    return;
                }

                bool activated = pointingModelManager.ActivateExperimentalModel(samples, out var metrics);
                _experimentalMetrics = metrics;

                if (activated)
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✅ Experimental PointXPCore model updated: {metrics.Summary}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Experimental PointXPCore model not active yet: {metrics.StatusMessage}");
                }
            }
            catch (Exception ex)
            {
                _experimentalMetrics = ExperimentalPointingModelMetrics.CreatePending(0, 0, $"Experimental rebuild failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Experimental PointXPCore rebuild failed: {ex.Message}");
            }
        }

        private List<ExperimentalCalibrationSample> BuildExperimentalCalibrationSamples()
        {
            var samples = new List<ExperimentalCalibrationSample>();

            foreach (var point in _calibrationPoints.Where(p => p.IsSolved))
            {
                if (!TryCreateExperimentalCalibrationSample(point, out var sample))
                {
                    continue;
                }

                samples.Add(sample);
            }

            return samples;
        }

        private bool TryCreateExperimentalCalibrationSample(
            CalibrationPoint point,
            out ExperimentalCalibrationSample sample)
        {
            sample = null;

            if (!point.CurrentAzimuthRawCounts.HasValue || !point.CurrentAltitudeRawCounts.HasValue)
            {
                return false;
            }

            if (!point.SolvedRa_J2000_Rads.HasValue || !point.SolvedDec_J2000_Rads.HasValue)
            {
                return false;
            }

            DateTime captureTime = point.CaptureTime ?? NtpTimeService.Instance.GetCurrentUtcTime();
            double lstHours = PointingModel.PlaneWaveModelFile.CalculateLocalSiderealTime(captureTime, _observerLocation.Longitude);
            double lstRads = lstHours * 15.0 * Math.PI / 180.0;
            double julianDate = PointingModel.PlaneWaveModelFile.CalculateJulianDate(captureTime);

            double azimuthDeg = (point.CurrentAzimuthRawCounts.Value / (double)_azimuthConfig.Encoder_Resolution) * 360.0 + _azimuthConfig.Position_Offset_Deg;
            double altitudeDeg = (point.CurrentAltitudeRawCounts.Value / (double)_altitudeConfig.Encoder_Resolution) * 360.0 + _altitudeConfig.Position_Offset_Deg;

            sample = new ExperimentalCalibrationSample
            {
                Index = point.Index,
                Name = $"Point {point.Index}",
                RaJ2000_Rads = point.SolvedRa_J2000_Rads.Value,
                DecJ2000_Rads = point.SolvedDec_J2000_Rads.Value,
                ThetaEnc_Rads = altitudeDeg * Math.PI / 180.0,
                PhiEnc_Rads = azimuthDeg * Math.PI / 180.0,
                Lst_Rads = lstRads,
                JulianDate = julianDate,
                CaptureTimeUtc = captureTime,
                IsIncludedInModel = point.IsIncludedInModel
            };

            return true;
        }
        
        /// <summary>
        /// [NEW] Apply first point offset to corrected position
        /// When first calibration point is solved, calculate the error and
        /// immediately offset the mount's corrected position by this amount.
        /// This provides instant improvement for pointing accuracy.
        /// </summary>
        private void ApplyFirstPointOffset(CalibrationPoint point)
        {
            if (!point.IsSolved)
            {
                LogError("[FIRST POINT] Cannot apply offset - point not solved");
                return;
            }
            
            // Calculate pointing error in arcseconds
            double raErrorArcsec = point.RaError_Arcsec ?? 0.0;
            double decErrorArcsec = point.DecError_Arcsec ?? 0.0;
            
            // Convert to degrees
            double raErrorDeg = raErrorArcsec / 3600.0;
            double decErrorDeg = decErrorArcsec / 3600.0;
            
            LogInfo($"");
            LogInfo($"[FIRST POINT OFFSET] ========================================");
            LogInfo($"[FIRST POINT OFFSET] Applying offset from first calibration point");
            LogInfo($"[FIRST POINT OFFSET] Point: Az={point.TargetAzimuth:F2}°, Alt={point.TargetAltitude:F2}°");
            LogInfo($"[FIRST POINT OFFSET] Pointing Error:");
            LogInfo($"[FIRST POINT OFFSET]   RA Error:  {raErrorArcsec:F2}\" ({raErrorDeg:F4}°)");
            LogInfo($"[FIRST POINT OFFSET]   Dec Error: {decErrorArcsec:F2}\" ({decErrorDeg:F4}°)");
            LogInfo($"[FIRST POINT OFFSET]   Total:     {point.TotalError_Arcsec:F2}\"");
            
            // TODO: Apply offset to pointing model or mount position
            // This depends on how PointingModelManager handles single-point offsets
            // For now, we'll log the values that should be applied
            
            LogInfo($"[FIRST POINT OFFSET] ⚠️ Offset calculated but not yet applied to mount");
            LogInfo($"[FIRST POINT OFFSET]    Implement: PointingModelManager.ApplyOffset(raErr, decErr)");
            LogInfo($"[FIRST POINT OFFSET] ========================================");
            LogInfo($"");
            
            // TODO: Implement actual offset application
            // Option 1: Apply to PointingModelManager (if it supports single-point correction)
            // Option 2: Apply to encoder offsets temporarily
            // Option 3: Create a 1-point model and activate it
        }
        
        /// <summary>
        /// Show calibration completion dialog
        /// [NEW] Builds final model BEFORE showing dialog
        /// </summary>
        private async Task ShowCompletionDialogAsync()
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Calculate overall statistics
                    int totalPoints = _calibrationPoints.Count;
                    int completedPoints = _calibrationPoints.Count(p => p.IsCompleted);
                    
                    // [FIX] Count all points by state
                    int solvedPoints = _calibrationPoints.Count(p => p.IsSolved);
                    int failedPoints = _calibrationPoints.Count(p => p.SolveFailed);
                    int pendingPoints = _calibrationPoints.Count(p => !p.IsSolved && !p.SolveFailed);
                    
                    
                    // Log pending points details for debugging
                    if (pendingPoints > 0)
                    {
                        var pendingPointsList = _calibrationPoints.Where(p => !p.IsSolved && !p.SolveFailed).ToList();
                        foreach (var point in pendingPointsList)
                        {
                        }
                    }
                    
                    var solvedPointsList = _calibrationPoints.Where(p => p.IsSolved && p.TotalError_Arcsec.HasValue).ToList();
                    
                    double? overallRMS = null;
                    double? avgError = null;
                    double? maxError = null;
                    double? minError = null;
                    string latestModelPath = null;
                    string latestModelStatus = "No new pointing model file is ready yet.";
                    
                    if (TryCreateRuntimeReadyPointingModel(out string runtimeModelPath, out string runtimeModelStatus))
                    {
                        latestModelPath = runtimeModelPath;
                        latestModelStatus = runtimeModelStatus;
                    }

                    if (string.IsNullOrWhiteSpace(latestModelPath) && File.Exists(AutosaveModelPath))
                    {
                        latestModelPath = AutosaveModelPath;
                        latestModelStatus = solvedPointsList.Count >= 9
                            ? $"Autosave model ready: {Path.GetFileName(AutosaveModelPath)}"
                            : $"Autosave only: {Path.GetFileName(AutosaveModelPath)} ({solvedPointsList.Count} solved points, use for resume/testing)";
                    }

                    if (_pointingModelBackendMode == PointingModelBackendMode.ExperimentalCoreDirect && _experimentalMetrics != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CALIBRATION] Experimental completion metrics: {_experimentalMetrics.Summary}");
                        latestModelStatus = _experimentalMetrics.Summary;
                    }
                    
                    if (solvedPointsList.Count > 0)
                    {
                        var errors = solvedPointsList.Select(p => p.TotalError_Arcsec.Value).ToList();
                        avgError = errors.Average();
                        maxError = errors.Max();
                        minError = errors.Min();
                        
                        // RMS = sqrt(mean(errors^2))
                        overallRMS = Math.Sqrt(errors.Average(e => e * e));
                    }
                    
                    // Create and show completion dialog
                    var completionDialog = new UI.CalibrationCompleteDialog
                    {
                        Owner = System.Windows.Application.Current.MainWindow,
                        TotalPoints = totalPoints,
                        CompletedPoints = completedPoints,
                        SolvedPoints = solvedPoints,
                        FailedPoints = failedPoints,
                        PendingPoints = pendingPoints,  // [NEW] Pass pending count
                        OverallRMS = overallRMS,
                        AverageError = avgError,
                        MaxError = maxError,
                        MinError = minError,
                        CalibrationPoints = _calibrationPoints.ToList(),  // Convert ObservableCollection to List
                        DialogTitle = "Pointing Model Summary",
                        DialogSubtitle = "Calibration finished. Choose what to do with the latest model.",
                        LatestModelPath = latestModelPath,
                        LatestModelStatus = latestModelStatus,
                        CanUseLatestModel = !string.IsNullOrWhiteSpace(latestModelPath) && File.Exists(latestModelPath),
                        UseLatestModelButtonText = "Use This Model",
                        ShowRestartButton = true
                    };
                    
                    if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.UpdatePointingModelStatus();
                    }

                    completionDialog.ShowDialog();

                    switch (completionDialog.SelectedAction)
                    {
                        case UI.CalibrationDialogAction.UseLatestModel:
                            if (TryCreateAndActivateDefaultPointingModel(out string activatedModelPath, out string activatedModelStatus) &&
                                !string.IsNullOrWhiteSpace(activatedModelPath) && File.Exists(activatedModelPath))
                            {
                                LogInfo($"Activated completion model as default: {Path.GetFileName(activatedModelPath)}");
                            }
                            else
                            {
                                LogError(activatedModelStatus);
                            }
                            break;

                        case UI.CalibrationDialogAction.KeepCurrentModel:
                            if (!string.IsNullOrWhiteSpace(_modelPathBeforeCalibration) && File.Exists(_modelPathBeforeCalibration))
                            {
                                bool restored = App.PointingModelManager?.LoadModel(_modelPathBeforeCalibration) == true;
                                LogInfo(restored
                                    ? $"Restored pre-calibration model: {Path.GetFileName(_modelPathBeforeCalibration)}"
                                    : $"Failed to restore pre-calibration model: {Path.GetFileName(_modelPathBeforeCalibration)}");
                            }
                            break;

                        case UI.CalibrationDialogAction.RestartCalibration:
                            CalibrationRestartRequested?.Invoke();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    LogError($"ShowCompletionDialogAsync failed: {ex.Message}");
                }
            });
        }
        
        /// <summary>
        /// Append solved point data to progressive model file in PlaneWave .pxp format
        /// This is called after each point is successfully plate solved
        /// </summary>
        private void AppendPointToProgressiveFile(CalibrationPoint point)
        {
            if (!point.IsSolved || !point.ExpectedRa_J2000_Rads.HasValue || !point.ExpectedDec_J2000_Rads.HasValue)
                return;
            
            // [CRITICAL] Verify we have raw encoder counts
            if (!point.CurrentAzimuthRawCounts.HasValue || !point.CurrentAltitudeRawCounts.HasValue)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Point {point.Index}: Missing raw encoder counts! Cannot add to model.");
                return;
            }
            
            try
            {
                // Calculate LST and JD (use _observerLocation from AppSettings, not hardcoded!)
                // [CRITICAL FIX] Use UTC time for astronomical calculations!
                DateTime captureTime = point.CaptureTime ?? NtpTimeService.Instance.GetCurrentUtcTime();  // Must be UTC!
                double lstHours = PointingModel.PlaneWaveModelFile.CalculateLocalSiderealTime(captureTime, _observerLocation.Longitude);
                // [CRITICAL FIX] NaritMountLib stores LST in RADIANS, not hours!
                // NaritMountLib: currentSample.LstRads = Angle.FromHms(MountInfo.Lmst).Rads;
                // Angle.FromHms("18:52:16").Rads → 18.871h × 15 = 283° → 4.943 rad
                // Convert: hours → degrees → radians
                double lst = lstHours * 15.0 * Math.PI / 180.0;  // hours → radians
                double jd = PointingModel.PlaneWaveModelFile.CalculateJulianDate(captureTime);
                
                
                
                
                
                
                // [FINAL FIX] Based on NaritMountLib documentation:
                //   PhiEnc = Azimuth encoder reading (radians)
                //   ThetaEnc = Altitude encoder reading (radians)
                // Use ACTUAL degrees (with offset) converted to radians
                
                // Calculate degrees with offset (this is what mount thinks it's pointing at)
                double azimuthDeg = (point.CurrentAzimuthRawCounts.Value / (double)_azimuthConfig.Encoder_Resolution) * 360.0 + _azimuthConfig.Position_Offset_Deg;
                double altitudeDeg = (point.CurrentAltitudeRawCounts.Value / (double)_altitudeConfig.Encoder_Resolution) * 360.0 + _altitudeConfig.Position_Offset_Deg;
                
                // Convert to radians for PlaneWave format
                // [CRITICAL FIX] Do NOT normalize to 0-2π!
                // NaritMountLib: PhiEncRads = Angle.FromDegs(MountInfo.AzimuthDegs).Rads;
                // NaritMountLib does NOT normalize - raw degrees→radians directly.
                // PointXP handles normalization internally using CountsPerRev.
                double phiEnc = azimuthDeg * Math.PI / 180.0;   // Azimuth in radians
                double thetaEnc = altitudeDeg * Math.PI / 180.0; // Altitude in radians
                
                // [DEBUG] Log encoder values for verification
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] Point {point.Index} Encoder:");
                System.Diagnostics.Debug.WriteLine($"  Target: Az={point.TargetAzimuth:F3}° Alt={point.TargetAltitude:F3}°");
                System.Diagnostics.Debug.WriteLine($"  Actual (with offset): Az={azimuthDeg:F3}°, Alt={altitudeDeg:F3}°");
                System.Diagnostics.Debug.WriteLine($"  Az Raw Counts: {point.CurrentAzimuthRawCounts.Value:F0}");
                System.Diagnostics.Debug.WriteLine($"  Alt Raw Counts: {point.CurrentAltitudeRawCounts.Value:F0}");
                System.Diagnostics.Debug.WriteLine($"  Expected J2000: RA={point.ExpectedRa_J2000_Rads.Value:F6} rad ({point.ExpectedRa_J2000_Rads.Value * 12.0 / Math.PI:F4}h), Dec={point.ExpectedDec_J2000_Rads.Value:F6} rad ({point.ExpectedDec_J2000_Rads.Value * 180.0 / Math.PI:F2}°)");
                System.Diagnostics.Debug.WriteLine($"  LST={lst:F6} rad, JD={jd:F2}");
                System.Diagnostics.Debug.WriteLine($"  PhiEnc (Az): {phiEnc:F6} rad ({phiEnc * 180.0 / Math.PI:F3}°)");
                System.Diagnostics.Debug.WriteLine($"  ThetaEnc (Alt): {thetaEnc:F6} rad ({thetaEnc * 180.0 / Math.PI:F3}°)");
                
                // Use Solved J2000 coordinates (from plate solve)
                // [CRITICAL FIX] PWLib plate solver returns J2000 coordinates
                // DO NOT apply any additional precession conversion!
                // The PointXP pxp format expects J2000 coordinates directly.
                double raJ2000_rad, decJ2000_rad;
                if (point.SolvedRa_J2000_Rads.HasValue && point.SolvedDec_J2000_Rads.HasValue)
                {
                    // Use plate solve result directly - no conversion needed!
                    raJ2000_rad = point.SolvedRa_J2000_Rads.Value;
                    decJ2000_rad = point.SolvedDec_J2000_Rads.Value;
                    
                    System.Diagnostics.Debug.WriteLine($"  Using Plate Solved J2000 (direct, no conversion):");
                    System.Diagnostics.Debug.WriteLine($"    RA={raJ2000_rad:F6} rad ({raJ2000_rad * 12.0 / Math.PI:F4}h), Dec={decJ2000_rad:F6} rad ({decJ2000_rad * 180.0 / Math.PI:F2}°)");
                }
                else
                {
                    raJ2000_rad = point.ExpectedRa_J2000_Rads.Value;
                    decJ2000_rad = point.ExpectedDec_J2000_Rads.Value;
                    System.Diagnostics.Debug.WriteLine($"  [WARNING] Using Expected J2000 (plate solve missing!)");
                }
                
                // Format PlaneWave point data
                var sb = new System.Text.StringBuilder();
                int solvedCount = GetExistingCalStarCountFromFile() + 1;
                
                sb.AppendLine($"Cal Star {solvedCount}:");
                sb.AppendLine();
                sb.AppendLine("RA, Dec:");
                sb.AppendLine($"{raJ2000_rad}");        // J2000 RA (radians)
                sb.AppendLine($"{decJ2000_rad}");       // J2000 Dec (radians)
                sb.AppendLine("ThetaEnc, PhiEnc:");
                sb.AppendLine($"{thetaEnc}");           // Raw encoder Alt (0-2π radians)
                sb.AppendLine($"{phiEnc}");             // Raw encoder Az (0-2π radians)
                sb.AppendLine("LST, JD:");
                sb.AppendLine($"{lst}");
                sb.AppendLine($"{jd}");
                sb.AppendLine("Time, Date:");
                sb.AppendLine(captureTime.ToString("HH:mm:ss"));
                sb.AppendLine(captureTime.ToString("yyyy-MM-dd"));
                sb.AppendLine();  // End current line
                sb.AppendLine();  // Create blank line for point separation
                
                // Thread-safe: Insert before footer, update count
                lock (_fileWriteLock)
                {
                    InsertPointBeforeFooter(sb.ToString(), solvedCount);
                }
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Insert point data before footer section and update count
        /// [CRITICAL] Footer now starts with "Latitude, Longitude:" (not "Terms Enabled:")
        /// </summary>
        private void InsertPointBeforeFooter(string pointData, int solvedCount)
        {
            var lines = File.ReadAllLines(_progressiveModelPath).ToList();
            
            // Update count at line 2 (line index 1)
            if (lines.Count > 1)
                lines[1] = solvedCount.ToString();
            
            // [CRITICAL FIX] Footer now starts with "Latitude, Longitude:" (not "Terms Enabled:")
            int footerIndex = lines.FindIndex(l => l.StartsWith("Latitude, Longitude"));
            
            if (footerIndex > 0)
            {
                // [FIX] Split point data into lines
                var pointLines = pointData.Split('\n')
                    .Select(l => l.TrimEnd())
                    .ToList();
                
                // Remove EXTRA trailing blank lines (keep only 1 for point separation)
                while (pointLines.Count > 1 && 
                       string.IsNullOrWhiteSpace(pointLines[pointLines.Count - 1]) && 
                       string.IsNullOrWhiteSpace(pointLines[pointLines.Count - 2]))
                {
                    pointLines.RemoveAt(pointLines.Count - 1);
                }
                
                // Insert point data before footer (before "Latitude, Longitude:")
                lines.InsertRange(footerIndex, pointLines);
            }
            else
            {
                // Fallback: Append before Terms Enabled if Latitude not found
                footerIndex = lines.FindIndex(l => l.StartsWith("Terms Enabled:"));
                if (footerIndex > 0)
                {
                    lines.InsertRange(footerIndex, pointData.Split('\n').Select(l => l.TrimEnd()));
                }
            }
            
            // [NEW] Update "Cal Points Used" section
            int calPointsUsedIndex = lines.FindIndex(l => l.StartsWith("Cal Points Used:"));
            if (calPointsUsedIndex > 0)
            {
                // Count existing "1" lines after "Cal Points Used:"
                int existingCount = 0;
                for (int i = calPointsUsedIndex + 1; i < lines.Count; i++)
                {
                    if (lines[i].Trim() == "1")
                        existingCount++;
                    else if (!string.IsNullOrWhiteSpace(lines[i]))
                        break;
                }
                
                // Add "1" for each new point
                if (existingCount < solvedCount)
                {
                    // Find insert position (after last "1")
                    int insertPos = calPointsUsedIndex + 1 + existingCount;
                    for (int i = existingCount; i < solvedCount; i++)
                    {
                        lines.Insert(insertPos++, "1");
                    }
                }
            }
            
            File.WriteAllLines(_progressiveModelPath, lines);
        }
        
        /// <summary>
        /// Update progressive file with current partial model
        /// Rebuilds model from all solved points and updates statistics
        /// [NEW] Also reloads model in PointingModelManager for immediate application!
        /// </summary>
        private void UpdatePartialModel()
        {
            try
            {
                // [NEW] Get all solved points that are INCLUDED in model (checkbox = true)
                var solvedPoints = _calibrationPoints
                    .Where(p => p.IsSolved && p.IsIncludedInModel)
                    .ToList();
                
                int excludedCount = _calibrationPoints.Count(p => p.IsSolved && !p.IsIncludedInModel);
                
                if (solvedPoints.Count < 3)
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Only {solvedPoints.Count} included points - need 3+ for model");
                    return;
                }
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] Building partial model from {solvedPoints.Count} points (excluded: {excludedCount})...");
                
                // Build partial model
                var builder = new PointingModel.PointingModelBuilder();
                var terms = builder.BuildModel(solvedPoints);
                var residualRMS = builder.CalculateResidualRMS(terms, solvedPoints);
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✓ Model built: IH={terms.IH:F3}°, ID={terms.ID:F3}°, RMS={residualRMS:F2}\"");
                
                // Update file with partial model (thread-safe)
                lock (_fileWriteLock)
                {
                    UpdateModelTermsInFile(terms, residualRMS, solvedPoints.Count);
                }
                
                // Note: Model reload into App.PointingModelManager happens in SolvePointAsync()
                // after this method returns — every solved point triggers an immediate reload.
                
                
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] Model file updated (NOT applied - user must load manually after calibration)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Error updating partial model: {ex.Message}");
            }
        }

        /// <summary>
        /// Read the current number of written "Cal Star" blocks from the progressive .pxp file.
        /// This is safer than counting in-memory solved points because the first solved point can be
        /// committed to file before all runtime flags settle, which previously caused Cal Star numbering
        /// to start at 2 and broke PointXP loading on restart.
        /// </summary>
        private int GetExistingCalStarCountFromFile()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_progressiveModelPath) || !File.Exists(_progressiveModelPath))
                    return 0;

                return File.ReadLines(_progressiveModelPath)
                    .Count(line => line.StartsWith("Cal Star ", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Update model terms section in progressive file
        /// [CRITICAL] Updates Model Terms section with fitted values
        /// </summary>
        private void UpdateModelTermsInFile(
            PointingModel.PointingModelBuilder.ModelTerms terms,
            double residualRMS,
            int solvedCount)
        {
            try
            {
                var lines = File.ReadAllLines(_progressiveModelPath).ToList();
                
                // [CRITICAL] Find and update "Model Terms:" section
                int modelTermsIndex = lines.FindIndex(l => l.StartsWith("Model Terms:"));
                
                if (modelTermsIndex >= 0 && modelTermsIndex + 15 < lines.Count)
                {
                    // Update all 15 model terms
                    lines[modelTermsIndex + 1] = $"{terms.IH}";    // IH
                    lines[modelTermsIndex + 2] = $"{terms.ID}";    // ID
                    lines[modelTermsIndex + 3] = $"{terms.NP}";    // NP
                    lines[modelTermsIndex + 4] = $"{terms.CH}";    // CH
                    lines[modelTermsIndex + 5] = $"{terms.CV}";    // CV
                    lines[modelTermsIndex + 6] = $"{terms.MA}";    // MA
                    lines[modelTermsIndex + 7] = $"{terms.ME}";    // ME
                    lines[modelTermsIndex + 8] = $"{terms.TF}";    // TF
                    lines[modelTermsIndex + 9] = $"{terms.TX}";    // TX
                    lines[modelTermsIndex + 10] = $"{terms.HCES}"; // HCES
                    lines[modelTermsIndex + 11] = $"{terms.HCEC}"; // HCEC
                    lines[modelTermsIndex + 12] = "0.0";           // DCES (disabled)
                    lines[modelTermsIndex + 13] = "0.0";           // DCEC (disabled)
                    lines[modelTermsIndex + 14] = "0.0";           // TF2 (disabled)
                    lines[modelTermsIndex + 15] = "0.0";           // TX2 (disabled)
                    
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ✓ Model terms updated: IH={terms.IH:F3}°, ID={terms.ID:F3}°, RMS={residualRMS:F2}\"");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ⚠️ Model Terms section not found in file!");
                }
                
                File.WriteAllLines(_progressiveModelPath, lines);
                
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALIBRATION] ❌ Error updating model terms: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Finalize progressive model file with fitted model and final statistics
        /// Called when calibration completes successfully
        /// </summary>
        private void FinalizeProgressiveModelFile(
            PointingModel.PointingModelBuilder.ModelTerms terms,
            double residualRMS)
        {
            try
            {
                
                // Read existing file
                var existingLines = File.ReadAllLines(_progressiveModelPath).ToList();
                
                // Update status header
                for (int i = 0; i < existingLines.Count; i++)
                {
                    if (existingLines[i].StartsWith("# Status:"))
                    {
                        existingLines[i] = "# Status: COMPLETED";
                    }
                    else if (existingLines[i].StartsWith("# Started:"))
                    {
                        existingLines.Insert(i + 1, $"# Completed: {NtpTimeService.Instance.GetCurrentTime():yyyy-MM-dd HH:mm:ss}");
                        break;
                    }
                }
                
                // Update statistics section
                int solvedCount = _calibrationPoints.Count(p => p.IsSolved);
                int failedCount = _calibrationPoints.Count(p => p.SolveFailed);
                int pendingCount = _calibrationPoints.Count - solvedCount - failedCount;
                
                for (int i = 0; i < existingLines.Count; i++)
                {
                    if (existingLines[i].StartsWith("SolvedPoints"))
                        existingLines[i] = $"SolvedPoints = {solvedCount}";
                    else if (existingLines[i].StartsWith("FailedPoints"))
                        existingLines[i] = $"FailedPoints = {failedCount}";
                    else if (existingLines[i].StartsWith("PendingPoints"))
                        existingLines[i] = $"PendingPoints = {pendingCount}";
                    else if (existingLines[i].StartsWith("ResidualRMS_Arcsec"))
                        existingLines[i] = $"ResidualRMS_Arcsec = {residualRMS:F3}";
                }
                
                // Update model terms section
                for (int i = 0; i < existingLines.Count; i++)
                {
                    if (existingLines[i].Contains("# Current status: PENDING"))
                    {
                        existingLines[i] = $"# Model fitted successfully from {solvedCount} points";
                    }
                    else if (existingLines[i].StartsWith("IH   ="))
                        existingLines[i] = $"IH   = {terms.IH:F8}   # Azimuth index error (deg)";
                    else if (existingLines[i].StartsWith("ID   ="))
                        existingLines[i] = $"ID   = {terms.ID:F8}   # Altitude index error (deg)";
                    else if (existingLines[i].StartsWith("NP   ="))
                        existingLines[i] = $"NP   = {terms.NP:F8}   # Non-perpendicularity (deg)";
                    else if (existingLines[i].StartsWith("CH   ="))
                        existingLines[i] = $"CH   = {terms.CH:F8}   # Horizontal collimation (deg)";
                    else if (existingLines[i].StartsWith("CV   ="))
                        existingLines[i] = $"CV   = {terms.CV:F8}   # Vertical collimation (deg)";
                    else if (existingLines[i].StartsWith("MA   ="))
                        existingLines[i] = $"MA   = {terms.MA:F8}   # Azimuth axis misalignment (deg)";
                    else if (existingLines[i].StartsWith("ME   ="))
                        existingLines[i] = $"ME   = {terms.ME:F8}   # Altitude axis misalignment (deg)";
                    else if (existingLines[i].StartsWith("HCES ="))
                        existingLines[i] = $"HCES = {terms.HCES:F8}   # Azimuth encoder sin error";
                    else if (existingLines[i].StartsWith("HCEC ="))
                        existingLines[i] = $"HCEC = {terms.HCEC:F8}   # Azimuth encoder cos error";
                    else if (existingLines[i].StartsWith("TF   ="))
                        existingLines[i] = $"TF   = {terms.TF:F8}   # Tube flexure (deg)";
                    else if (existingLines[i].StartsWith("TX   ="))
                        existingLines[i] = $"TX   = {terms.TX:F8}   # Tube flexure tilt (deg)";
                }
                
                // Write updated file
                File.WriteAllLines(_progressiveModelPath, existingLines);
                
                // Rename to final name (remove _INPROGRESS suffix)
                string finalPath = PointingModel.PointingModelFile.GetDefaultModelPath();
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);  // Delete old model
                }
                File.Move(_progressiveModelPath, finalPath);
                
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Calibration point data model
    /// </summary>
    public class CalibrationPoint : System.ComponentModel.INotifyPropertyChanged
    {
        public int Index { get; set; }
        public double TargetAzimuth { get; set; }
        public double TargetAltitude { get; set; }
        public double CurrentAzimuth { get; set; }
        public double CurrentAltitude { get; set; }
        
        /// <summary>
        /// [NEW] Whether this point is included in model calculation
        /// User can uncheck to exclude failed/outlier points
        /// </summary>
        private bool _isIncludedInModel = true;  // Default: included
        public bool IsIncludedInModel
        {
            get => _isIncludedInModel;
            set
            {
                if (_isIncludedInModel != value)
                {
                    _isIncludedInModel = value;
                    OnPropertyChanged(nameof(IsIncludedInModel));
                    // Trigger model rebuild when checkbox changes
                    System.Diagnostics.Debug.WriteLine($"[CALIBRATION] Point #{Index} IsIncludedInModel = {value}");
                }
            }
        }
        
        private bool _isTracking;
        public bool IsTracking
        {
            get => _isTracking;
            set
            {
                if (_isTracking != value)
                {
                    _isTracking = value;
                    OnPropertyChanged(nameof(IsTracking));
                    OnPropertyChanged(nameof(Status));  // Update Status when state changes
                }
            }
        }
        
        private bool _isCompleted;
        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (_isCompleted != value)
                {
                    _isCompleted = value;
                    OnPropertyChanged(nameof(IsCompleted));
                    OnPropertyChanged(nameof(Status));
                }
            }
        }
        
        private bool _isSlewing;
        public bool IsSlewing
        {
            get => _isSlewing;
            set
            {
                if (_isSlewing != value)
                {
                    _isSlewing = value;
                    OnPropertyChanged(nameof(IsSlewing));
                    OnPropertyChanged(nameof(Status));
                }
            }
        }
        
        private bool _isCapturing;
        public bool IsCapturing
        {
            get => _isCapturing;
            set
            {
                if (_isCapturing != value)
                {
                    _isCapturing = value;
                    OnPropertyChanged(nameof(IsCapturing));
                    OnPropertyChanged(nameof(Status));
                }
            }
        }
        
        private bool _isSolving;
        public bool IsSolving
        {
            get => _isSolving;
            set
            {
                if (_isSolving != value)
                {
                    _isSolving = value;
                    OnPropertyChanged(nameof(IsSolving));
                    OnPropertyChanged(nameof(Status));
                }
            }
        }
        
        private bool _isSolved;
        public bool IsSolved
        {
            get => _isSolved;
            set
            {
                if (_isSolved != value)
                {
                    _isSolved = value;
                    OnPropertyChanged(nameof(IsSolved));
                    OnPropertyChanged(nameof(Status));
                }
            }
        }
        
        private bool _solveFailed;
        public bool SolveFailed
        {
            get => _solveFailed;
            set
            {
                if (_solveFailed != value)
                {
                    _solveFailed = value;
                    OnPropertyChanged(nameof(SolveFailed));
                    OnPropertyChanged(nameof(Status));
                }
            }
        }
        
        public double? AzimuthRmsError_Arcsec { get; set; }
        public double? AltitudeRmsError_Arcsec { get; set; }
        public double CurrentRmsError { get; set; }
        
        public int SampleCount { get; set; }
        public double TrackingTime { get; set; }
        
        // ========================================================================
        // Plate Solving Results (NEW)
        // ========================================================================
        
        /// <summary>
        /// RA error from plate solving (arcseconds)
        /// Positive = solved RA > expected RA
        /// </summary>
        public double? RaError_Arcsec { get; set; }
        
        /// <summary>
        /// Dec error from plate solving (arcseconds)
        /// Positive = solved Dec > expected Dec
        /// </summary>
        public double? DecError_Arcsec { get; set; }
        
        /// <summary>
        /// Total pointing error (RMS of RA + Dec errors)
        /// </summary>
        public double? TotalError_Arcsec
        {
            get
            {
                if (RaError_Arcsec.HasValue && DecError_Arcsec.HasValue)
                {
                    double raErr = RaError_Arcsec.Value;
                    double decErr = DecError_Arcsec.Value;
                    return Math.Sqrt(raErr * raErr + decErr * decErr);
                }
                return null;
            }
        }
        
        /// <summary>
        /// Number of stars matched by plate solver
        /// Higher = more reliable solve
        /// </summary>
        public int NumMatchedStars { get; set; } = 0;
        
        /// <summary>
        /// Image rotation angle from plate solving (degrees)
        /// Indicates camera orientation vs sky
        /// </summary>
        public double? RotationAngle_Deg { get; set; }
        
        /// <summary>
        /// Plate scale measured from solve (arcsec/pixel)
        /// Can be used to validate camera settings
        /// </summary>
        public double? MeasuredPlateScale_ArcsecPerPixel { get; set; }
        
        /// <summary>
        /// Number of stars extracted from image before matching
        /// </summary>
        public int NumExtractedStars { get; set; } = 0;
        
        /// <summary>
        /// Is this a good solve? (Total error < 10 arcsec)
        /// </summary>
        public bool IsGoodSolve => TotalError_Arcsec.HasValue && TotalError_Arcsec.Value < 10.0;
        
        /// <summary>
        /// Status string for UI display
        /// </summary>
        public string Status
        {
            get
            {
                // [PRIORITY ORDER] More specific states first!
                if (IsSolving)
                    return "[SOLVING]";
                else if (SolveFailed)
                    return "[SOLVE FAILED]";
                else if (IsSolved && IsCompleted)
                    return "[COMPLETED]";
                else if (IsCapturing)
                    return $"[EXPOSING]";
                else if (IsTracking)
                    return "[TRACKING]";
                else if (IsSlewing)
                    return "[SLEWING]";
                else if (!IsSlewing && !IsTracking && !IsCapturing && !IsSolving && !IsCompleted)
                    return "[SETTLING]";  // Mount stopped but RMS too high
                else if (IsCompleted)
                    return "[COMPLETED]";
                else
                    return "[PENDING]";
            }
        }
        
        /// <summary>
        /// Exposure time remaining (for countdown display)
        /// </summary>
        public double? ExposureTimeRemaining_ms { get; set; } = null;
        
        /// <summary>
        /// FITS file path (for plate solving)
        /// </summary>
        public string FitsFilePath { get; set; }
        
        /// <summary>
        /// Expected RA at time of capture (J2000, radians)
        /// [NOTE] This is used as HINT for plate solver only!
        /// </summary>
        public double? ExpectedRa_J2000_Rads { get; set; }
        
        /// <summary>
        /// Expected Dec at time of capture (J2000, radians)
        /// [NOTE] This is used as HINT for plate solver only!
        /// </summary>
        public double? ExpectedDec_J2000_Rads { get; set; }
        
        /// <summary>
        /// [NARIT PATTERN] Expected RA at time of capture (APPARENT, radians)
        /// This is used for ERROR CALCULATION (Apparent vs Apparent)
        /// Matches Narit's sample.EstimatedRaAppRads
        /// </summary>
        public double? ExpectedRa_App_Rads { get; set; }
        
        /// <summary>
        /// [NARIT PATTERN] Expected Dec at time of capture (APPARENT, radians)
        /// This is used for ERROR CALCULATION (Apparent vs Apparent)
        /// Matches Narit's sample.EstimatedDecAppRads
        /// </summary>
        public double? ExpectedDec_App_Rads { get; set; }
        
        /// <summary>
        /// [NARIT PATTERN] Julian Date at capture time
        /// Needed for J2000ToApparent conversion of solved coordinates
        /// </summary>
        public double? JulianDate { get; set; }
        
        /// <summary>
        /// Solved RA from plate solving (J2000, radians)
        /// </summary>
        public double? SolvedRa_J2000_Rads { get; set; }
        
        /// <summary>
        /// Solved Dec from plate solving (J2000, radians)
        /// </summary>
        public double? SolvedDec_J2000_Rads { get; set; }
        
        /// <summary>
        /// Capture timestamp
        /// </summary>
        public DateTime? CaptureTime { get; set; }
        
        /// <summary>
        /// Raw encoder counts at capture time (Azimuth)
        /// [CRITICAL] Needed for PlaneWave model - must be RAW counts without offset!
        /// </summary>
        public double? CurrentAzimuthRawCounts { get; set; }
        
        /// <summary>
        /// Raw encoder counts at capture time (Altitude)
        /// [CRITICAL] Needed for PlaneWave model - must be RAW counts without offset!
        /// </summary>
        public double? CurrentAltitudeRawCounts { get; set; }
        
        // INotifyPropertyChanged implementation
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
    
    /// <summary>
    /// Helper methods for MountCalibrationManager
    /// </summary>
    public static class MountCalibrationHelper
    {
        /// <summary>
        /// Convert Alt/Az to RA/Dec for SiderealTracking using PWLib.Astro
        /// </summary>
        public static (double raHours, double decDeg) ConvertAltAzToRaDec(
            double altitudeDeg, 
            double azimuthDeg, 
            double observerLatitude, 
            double observerLongitude, 
            DateTime utc)
        {
            // Normalize azimuth to 0-360
            double azimuth0to360 = azimuthDeg;
            while (azimuth0to360 < 0) azimuth0to360 += 360.0;
            while (azimuth0to360 >= 360) azimuth0to360 -= 360.0;
            
            // Use PWLib.Astro for conversion
            var site = new PWLib.Astro.LatLon(
                PWLib.Astro.Angle.FromDegs(observerLatitude), 
                PWLib.Astro.Angle.FromDegs(observerLongitude));
            double jd = PWLib.Astro.AstroTime.JulianDay(utc);
            var altAz = new PWLib.Astro.AltAz(
                PWLib.Astro.Angle.FromDegs(altitudeDeg), 
                PWLib.Astro.Angle.FromDegs(azimuth0to360));
            var raDec = altAz.ToRaDec(site, jd);
            
            double raHours = raDec.Ra.Hours;  // Already in hours
            double decDeg = raDec.Dec.Degs;
            return (raHours, decDeg);
        }
        
        /// <summary>
        /// Unwrap azimuth target to mount's coordinate range
        /// Use simple linear distance (no wrapping calculation)
        /// </summary>
        public static double UnwrapAzimuthToMountRange(
            double targetAz_0to360, 
            double currentAz, 
            double ccwLimit, 
            double cwLimit)
        {
            // Find shortest path to target
            double[] candidates = new[]
            {
                targetAz_0to360,
                targetAz_0to360 - 360.0,
                targetAz_0to360 + 360.0
            };
            
            double bestTarget = targetAz_0to360;
            double smallestMove = double.MaxValue;
            
            foreach (double candidate in candidates)
            {
                // Check if within limits
                if (candidate >= ccwLimit && candidate <= cwLimit)
                {
                    // Use simple linear distance (no wrapping)
                    double moveDistance = Math.Abs(candidate - currentAz);
                    
                    if (moveDistance < smallestMove)
                    {
                        smallestMove = moveDistance;
                        bestTarget = candidate;
                    }
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"[UNWRAP] Target {targetAz_0to360:F2}° → {bestTarget:F2}° (from {currentAz:F2}°, distance: {smallestMove:F2}°)");
            return bestTarget;
        }
    }

    /// <summary>
    /// Image data for UI polling
    /// </summary>
    public class CalibrationImageData
    {
        public int PointNumber { get; set; }
        public BitmapSource Image { get; set; }  // [NEW] BitmapSource (supports 16-bit!)
        public double Azimuth { get; set; }
        public double Altitude { get; set; }
        public CameraImageData RawImageData { get; set; }  // [NEW] For histogram stretching
    }
}
