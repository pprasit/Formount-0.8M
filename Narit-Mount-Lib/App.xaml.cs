using System.Configuration;
using System.Data;
using System.Windows;
using System.Diagnostics;

namespace ForkMount_WPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Global CentralizedPollingManager - SINGLE SOURCE OF TRUTH
        /// Access from anywhere: App.PollingManager.AzimuthInfo
        /// This prevents reference confusion and ensures all UI reads from same object
        /// </summary>
        public static Engine.Manager.CentralizedPollingManager PollingManager { get; set; }

        /// <summary>
        /// Quick access to AzimuthInfo (from PollingManager)
        /// Usage: App.AzimuthInfo instead of App.PollingManager.AzimuthInfo
        /// </summary>
        public static Engine.Info.AzimuthInfo AzimuthInfo => PollingManager?.AzimuthInfo;

        /// <summary>
        /// Quick access to AltitudeInfo (from PollingManager)
        /// </summary>
        public static Engine.Info.AltitudeInfo AltitudeInfo => PollingManager?.AltitudeInfo;

        /// <summary>
        /// Quick access to MountInfo (from PollingManager)
        /// </summary>
        public static Engine.Info.MountInfo MountInfo => PollingManager?.MountInfo;
        
        /// <summary>
        /// [NEW] Global Pointing Model Manager - Auto-load on startup
        /// </summary>
        public static Engine.PointingModel.PointingModelManager PointingModelManager { get; set; }
        
        /// <summary>
        /// Application startup - Initialize global services
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // [NEW] Initialize and load pointing model on startup
            InitializePointingModel();
        }
        
        /// <summary>
        /// Initialize pointing model manager and auto-load latest model
        /// </summary>
        private void InitializePointingModel()
        {
            try
            {
                Debug.WriteLine("[APP STARTUP] ========================================");
                Debug.WriteLine("[APP STARTUP] Initializing Pointing Model Manager...");
                Debug.WriteLine($"[APP STARTUP] Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
                
                PointingModelManager = new Engine.PointingModel.PointingModelManager();
                
                // Check if PointXP was initialized
                Debug.WriteLine($"[APP STARTUP] PointingModelManager created: {PointingModelManager != null}");
                
                // Auto-load latest model file
                Debug.WriteLine("[APP STARTUP] Attempting to load model...");
                bool modelLoaded = PointingModelManager.LoadModel();
                
                Debug.WriteLine($"[APP STARTUP] LoadModel() returned: {modelLoaded}");
                Debug.WriteLine($"[APP STARTUP] IsModelLoaded property: {PointingModelManager.IsModelLoaded}");
                
                if (modelLoaded)
                {
                    Debug.WriteLine("[APP STARTUP] ✅ Pointing Model loaded successfully");
                    Debug.WriteLine($"[APP STARTUP]    Model is active and ready for correction");
                }
                else
                {
                    Debug.WriteLine("[APP STARTUP] ❌ Pointing Model NOT loaded");
                    Debug.WriteLine("[APP STARTUP]    Check CalibrationData folder for PointingModel*.pxp files");
                    Debug.WriteLine("[APP STARTUP]    To create model: Use Mount Calibration tool");
                }
                Debug.WriteLine("[APP STARTUP] ========================================");
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[APP STARTUP] ⚠️ Error initializing Pointing Model: {ex.Message}");
                Debug.WriteLine($"[APP STARTUP]    Stack: {ex.StackTrace}");
                Debug.WriteLine($"[APP STARTUP]    Application will continue with raw positions");
            }
        }
    }

}
