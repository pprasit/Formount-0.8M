using System;
using System.Diagnostics;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.PointingModel;
using ForkMount_WPF.Engine.Tracking;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Shared runtime bootstrap for connected mount sessions.
    /// Both Lib UI and API should use this entry point so they build the
    /// same PointingModelManager and CentralizedPollingManager stack.
    /// </summary>
    public static class MountRuntimeBootstrap
    {
        public sealed class RuntimeContext
        {
            public RuntimeContext(CentralizedPollingManager pollingManager, PointingModelManager pointingModelManager)
            {
                PollingManager = pollingManager ?? throw new ArgumentNullException(nameof(pollingManager));
                PointingModelManager = pointingModelManager ?? throw new ArgumentNullException(nameof(pointingModelManager));
            }

            public CentralizedPollingManager PollingManager { get; }

            public PointingModelManager PointingModelManager { get; }
        }

        public static RuntimeContext InitializeConnectedRuntime(
            PlatinumDrive platinumDrive,
            ObserverLocation observerLocation = null,
            string pointingModelPath = null)
        {
            if (platinumDrive == null)
                throw new ArgumentNullException(nameof(platinumDrive));
            if (!platinumDrive.IsConnected)
                throw new InvalidOperationException("PlatinumDrive must be connected before runtime bootstrap.");

            var pointingModelManager = EnsurePointingModelManager(observerLocation, pointingModelPath);

            var pollingManager = new CentralizedPollingManager(platinumDrive);
            App.PollingManager = pollingManager;

            Debug.WriteLine("[RUNTIME BOOTSTRAP] CentralizedPollingManager registered to App.PollingManager");

            return new RuntimeContext(pollingManager, pointingModelManager);
        }

        private static PointingModelManager EnsurePointingModelManager(
            ObserverLocation observerLocation,
            string pointingModelPath)
        {
            var pointingModelManager = App.PointingModelManager ?? new PointingModelManager();
            App.PointingModelManager = pointingModelManager;

            if (observerLocation != null)
            {
                pointingModelManager.UpdateObserverLocation(observerLocation.Latitude, observerLocation.Longitude);
                Debug.WriteLine(
                    $"[RUNTIME BOOTSTRAP] Pointing model observer synced: " +
                    $"Lat={observerLocation.Latitude:F4} Lon={observerLocation.Longitude:F4}");
            }

            bool loaded;
            if (!string.IsNullOrWhiteSpace(pointingModelPath))
            {
                loaded = pointingModelManager.LoadModel(pointingModelPath);
                if (!loaded)
                    throw new InvalidOperationException($"Failed to load pointing model: {pointingModelPath}");

                Debug.WriteLine($"[RUNTIME BOOTSTRAP] Loaded pointing model from explicit path: {pointingModelPath}");
                return pointingModelManager;
            }

            if (!pointingModelManager.IsModelLoaded)
            {
                loaded = pointingModelManager.LoadModel();
                Debug.WriteLine($"[RUNTIME BOOTSTRAP] Auto-load latest pointing model result: {loaded}");
            }

            return pointingModelManager;
        }
    }
}
