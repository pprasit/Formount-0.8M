using System;
using System.Threading;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Tracking object types supported by the system
    /// </summary>
    public enum TrackingObjectType
    {
        None,
        Satellite,
        BrightStar,    // Sidereal/Star tracking
        Planet,
        DeepSky        // NGC/IC objects
    }

    /// <summary>
    /// Information about the current tracking target
    /// </summary>
    public class TrackingTarget
    {
        public TrackingObjectType ObjectType { get; set; }
        public string ObjectName { get; set; }
        public double RightAscension { get; set; }  // Hours (for celestial objects)
        public double Declination { get; set; }      // Degrees
        public double Azimuth { get; set; }          // Degrees
        public double Altitude { get; set; }         // Degrees
        public DateTime StartTime { get; set; }
        public object SourceObject { get; set; }     // Original object reference
    }

    /// <summary>
    /// Event args for tracking state changes
    /// </summary>
    public class TrackingStateChangedEventArgs : EventArgs
    {
        public bool IsTracking { get; set; }
        public TrackingObjectType ObjectType { get; set; }
        public TrackingTarget Target { get; set; }
        public TrackingObjectType PreviousObjectType { get; set; }
    }

    /// <summary>
    /// Centralized manager for all tracking operations.
    /// Ensures only ONE object can be tracked at a time, preventing race conditions.
    /// Thread-safe singleton pattern.
    /// </summary>
    public sealed class TrackingManager
    {
        private static readonly Lazy<TrackingManager> _instance = 
            new Lazy<TrackingManager>(() => new TrackingManager(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object _trackingLock = new object();
        private bool _isTracking;
        private TrackingObjectType _currentObjectType;
        private TrackingTarget _currentTarget;
        private Action _stopCurrentTrackingCallback;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static TrackingManager Instance => _instance.Value;

        /// <summary>
        /// Event fired when tracking state changes
        /// </summary>
        public event EventHandler<TrackingStateChangedEventArgs> TrackingStateChanged;

        /// <summary>
        /// Event fired when user selects/clicks any object in any tracking table.
        /// Used by AutoSaveFitsControl to update the FITS object name.
        /// </summary>
        public event EventHandler<string> ObjectSelectionChanged;

        /// <summary>
        /// Last object name selected by user in any tracking table (Satellite, Star, Planet, DeepSky).
        /// Separate from CurrentTarget � this updates on click, not on tracking start.
        /// </summary>
        public string SelectedObjectName { get; private set; } = "";

        /// <summary>
        /// Called by any tracking control when user clicks/selects an object in the table.
        /// </summary>
        public void NotifyObjectSelected(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return;
            SelectedObjectName = objectName;
            System.Diagnostics.Debug.WriteLine($"[TRACKING MANAGER] Object selected: '{objectName}'");
            ObjectSelectionChanged?.Invoke(this, objectName);
        }

        /// <summary>
        /// Is tracking currently active
        /// </summary>
        public bool IsTracking
        {
            get { lock (_trackingLock) { return _isTracking; } }
        }

        /// <summary>
        /// Current tracking object type
        /// </summary>
        public TrackingObjectType CurrentObjectType
        {
            get { lock (_trackingLock) { return _currentObjectType; } }
        }

        /// <summary>
        /// Current tracking target info
        /// </summary>
        public TrackingTarget CurrentTarget
        {
            get { lock (_trackingLock) { return _currentTarget; } }
        }

        private TrackingManager()
        {
            _isTracking = false;
            _currentObjectType = TrackingObjectType.None;
            _currentTarget = null;
            _stopCurrentTrackingCallback = null;

            SimpleLogger.Info("[TRACKING MANAGER] Initialized", "TrackingManager");
        }

        /// <summary>
        /// Request to start tracking a new object.
        /// If another object is being tracked, it will be stopped first.
        /// </summary>
        /// <param name="objectType">Type of object to track</param>
        /// <param name="target">Target information</param>
        /// <param name="stopCallback">Callback to stop this tracking when another takes over</param>
        /// <returns>True if tracking started successfully</returns>
        public bool RequestStartTracking(TrackingObjectType objectType, TrackingTarget target, Action stopCallback)
        {
            if (objectType == TrackingObjectType.None || target == null)
            {
                SimpleLogger.Warning("[TRACKING MANAGER] Invalid tracking request - null target or None type", "TrackingManager");
                return false;
            }

            lock (_trackingLock)
            {
                var previousType = _currentObjectType;

                // If already tracking something else, stop it first
                if (_isTracking && _currentObjectType != TrackingObjectType.None)
                {
                    SimpleLogger.Info($"[TRACKING MANAGER] Stopping current {_currentObjectType} tracking to start {objectType}", "TrackingManager");
                    
                    // Call the stop callback for the previous tracking
                    try
                    {
                        _stopCurrentTrackingCallback?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        SimpleLogger.Error($"[TRACKING MANAGER] Error stopping previous tracking: {ex.Message}", "TrackingManager");
                    }
                }

                // Start new tracking
                _isTracking = true;
                _currentObjectType = objectType;
                _currentTarget = target;
                _currentTarget.StartTime = DateTime.UtcNow;
                _stopCurrentTrackingCallback = stopCallback;

                // Update MountInfo with current tracking object name
                var mountInfo = App.MountInfo;
                if (mountInfo != null)
                    mountInfo.CurrentTrackingObjectName = target.ObjectName ?? "";

                SimpleLogger.Info($"[TRACKING MANAGER] Started tracking {objectType}: {target.ObjectName}", "TrackingManager");
                System.Diagnostics.Debug.WriteLine($"[TRACKING MANAGER] Started tracking {objectType}: {target.ObjectName}");

                // Fire event
                OnTrackingStateChanged(new TrackingStateChangedEventArgs
                {
                    IsTracking = true,
                    ObjectType = objectType,
                    Target = target,
                    PreviousObjectType = previousType
                });

                return true;
            }
        }

        /// <summary>
        /// Stop current tracking
        /// </summary>
        /// <param name="requestingType">The type requesting the stop (for validation)</param>
        public void StopTracking(TrackingObjectType requestingType)
        {
            lock (_trackingLock)
            {
                if (!_isTracking)
                {
                    return;
                }

                // Only the current tracking type can stop itself, or None can stop any
                if (requestingType != TrackingObjectType.None && requestingType != _currentObjectType)
                {
                    SimpleLogger.Warning($"[TRACKING MANAGER] Stop request from {requestingType} ignored - current is {_currentObjectType}", "TrackingManager");
                    return;
                }

                var previousType = _currentObjectType;
                var previousTarget = _currentTarget;

                _isTracking = false;
                _currentObjectType = TrackingObjectType.None;
                _currentTarget = null;
                _stopCurrentTrackingCallback = null;

                // Clear tracking object name in MountInfo
                var mountInfo = App.MountInfo;
                if (mountInfo != null)
                    mountInfo.CurrentTrackingObjectName = "";

                SimpleLogger.Info($"[TRACKING MANAGER] Stopped tracking {previousType}: {previousTarget?.ObjectName}", "TrackingManager");
                System.Diagnostics.Debug.WriteLine($"[TRACKING MANAGER] Stopped tracking {previousType}");

                // Fire event
                OnTrackingStateChanged(new TrackingStateChangedEventArgs
                {
                    IsTracking = false,
                    ObjectType = TrackingObjectType.None,
                    Target = null,
                    PreviousObjectType = previousType
                });
            }
        }

        /// <summary>
        /// Force stop all tracking (emergency stop)
        /// </summary>
        public void ForceStopAllTracking()
        {
            lock (_trackingLock)
            {
                if (!_isTracking)
                {
                    return;
                }

                var previousType = _currentObjectType;

                // Call the stop callback
                try
                {
                    _stopCurrentTrackingCallback?.Invoke();
                }
                catch (Exception ex)
                {
                    SimpleLogger.Error($"[TRACKING MANAGER] Error in force stop: {ex.Message}", "TrackingManager");
                }

                _isTracking = false;
                _currentObjectType = TrackingObjectType.None;
                _currentTarget = null;
                _stopCurrentTrackingCallback = null;

                // Clear tracking object name in MountInfo
                var mountInfo = App.MountInfo;
                if (mountInfo != null)
                    mountInfo.CurrentTrackingObjectName = "";

                SimpleLogger.Warning($"[TRACKING MANAGER] FORCE STOPPED all tracking (was {previousType})", "TrackingManager");

                OnTrackingStateChanged(new TrackingStateChangedEventArgs
                {
                    IsTracking = false,
                    ObjectType = TrackingObjectType.None,
                    Target = null,
                    PreviousObjectType = previousType
                });
            }
        }

        /// <summary>
        /// Check if a specific type is currently tracking
        /// </summary>
        public bool IsTypeTracking(TrackingObjectType objectType)
        {
            lock (_trackingLock)
            {
                return _isTracking && _currentObjectType == objectType;
            }
        }

        /// <summary>
        /// Update current target information (position, etc.)
        /// </summary>
        public void UpdateTargetPosition(double azimuth, double altitude)
        {
            lock (_trackingLock)
            {
                if (_currentTarget != null)
                {
                    _currentTarget.Azimuth = azimuth;
                    _currentTarget.Altitude = altitude;
                }
            }
        }

        /// <summary>
        /// Get tracking duration
        /// </summary>
        public TimeSpan GetTrackingDuration()
        {
            lock (_trackingLock)
            {
                if (_isTracking && _currentTarget != null)
                {
                    return DateTime.UtcNow - _currentTarget.StartTime;
                }
                return TimeSpan.Zero;
            }
        }

        private void OnTrackingStateChanged(TrackingStateChangedEventArgs e)
        {
            try
            {
                TrackingStateChanged?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRACKING MANAGER] Error in event handler: {ex.Message}");
            }
        }
    }
}
