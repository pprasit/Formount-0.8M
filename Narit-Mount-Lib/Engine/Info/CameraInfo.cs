using System;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// Centralized camera information (from hardware + settings + calculated)
    /// This class serves as the single source of truth for all camera-related data
    /// </summary>
    public class CameraInfo
    {
        #region Hardware Properties (from ZWO Camera)
        
        /// <summary>
        /// Camera name (e.g., "ZWO ASI461MM Pro")
        /// </summary>
        public string CameraName { get; set; } = "Not Connected";
        
        /// <summary>
        /// Maximum resolution width (full frame, no binning)
        /// </summary>
        public int MaxWidth { get; set; } = 0;
        
        /// <summary>
        /// Maximum resolution height (full frame, no binning)
        /// </summary>
        public int MaxHeight { get; set; } = 0;
        
        /// <summary>
        /// Pixel size in micrometers (µm)
        /// [CRITICAL] Must be read from camera hardware - no default!
        /// </summary>
        public double PixelSize_um { get; set; } = 0.0;  // ? No default - must get from hardware
        
        /// <summary>
        /// Is this a color camera?
        /// </summary>
        public bool IsColorCamera { get; set; } = false;
        
        /// <summary>
        /// Does camera have a cooler?
        /// </summary>
        public bool HasCooler { get; set; } = false;
        
        /// <summary>
        /// Is camera currently connected?
        /// </summary>
        public bool IsConnected { get; set; } = false;
        
        #endregion
        
        #region Settings Properties (from AppSettings)
        
        /// <summary>
        /// Focal length in millimeters (from telescope/lens)
        /// [CRITICAL] Must be configured by user in settings!
        /// </summary>
        public double FocalLength_mm { get; set; } = 0.0;  // ? No default - user must configure
        
        /// <summary>
        /// Current binning mode (1, 2, 3, or 4)
        /// </summary>
        public int Binning { get; set; } = 1;
        
        /// <summary>
        /// Current exposure time in milliseconds
        /// </summary>
        public int ExposureTime_ms { get; set; } = 1000;
        
        /// <summary>
        /// Current gain setting
        /// </summary>
        public int Gain { get; set; } = 0;
        
        /// <summary>
        /// Current offset setting
        /// </summary>
        public int Offset { get; set; } = 0;
        
        #endregion
        
        #region Current Image Properties
        
        /// <summary>
        /// Current image width (after binning)
        /// </summary>
        public int CurrentWidth { get; set; } = 0;
        
        /// <summary>
        /// Current image height (after binning)
        /// </summary>
        public int CurrentHeight { get; set; } = 0;
        
        #endregion
        
        #region Calculated Properties
        
        /// <summary>
        /// Plate scale in arcseconds per pixel (calculated)
        /// Formula: (PixelSize_um * Binning / FocalLength_mm) * 206.265
        /// </summary>
        public double PlateScale_ArcsecPerPixel { get; set; } = 0.0;
        
        /// <summary>
        /// Field of View width in degrees (calculated)
        /// Formula: (CurrentWidth * PlateScale) / 3600.0
        /// </summary>
        public double FOV_Width_deg { get; set; } = 0.0;
        
        /// <summary>
        /// Field of View height in degrees (calculated)
        /// Formula: (CurrentHeight * PlateScale) / 3600.0
        /// </summary>
        public double FOV_Height_deg { get; set; } = 0.0;
        
        #endregion
        
        #region Calculation Methods
        
        /// <summary>
        /// Recalculate Plate Scale from current settings
        /// Formula: (PixelSize_um * Binning / FocalLength_mm) * 206.265
        /// </summary>
        public void RecalculatePlateScale()
        {
            try
            {
                if (FocalLength_mm <= 0 || PixelSize_um <= 0)
                {
                    PlateScale_ArcsecPerPixel = 0.0;
                    System.Diagnostics.Debug.WriteLine("[CameraInfo] Cannot calculate PlateScale: Invalid FocalLength or PixelSize");
                    return;
                }
                
                // Validate PixelSize (detect corruption)
                double pixelSize = PixelSize_um;
                if (pixelSize <= 0 || pixelSize > 100 || double.IsNaN(pixelSize) || double.IsInfinity(pixelSize))
                {
                    System.Diagnostics.Debug.WriteLine($"[CameraInfo] Invalid PixelSize ({pixelSize}), using default 3.76µm");
                    pixelSize = 3.76;
                    PixelSize_um = pixelSize;
                }
                
                PlateScale_ArcsecPerPixel = (pixelSize * Binning / FocalLength_mm) * 206.265;
                
                // Validate result
                if (PlateScale_ArcsecPerPixel <= 0 || PlateScale_ArcsecPerPixel > 100 || 
                    double.IsNaN(PlateScale_ArcsecPerPixel) || double.IsInfinity(PlateScale_ArcsecPerPixel))
                {
                    System.Diagnostics.Debug.WriteLine($"[CameraInfo] Invalid PlateScale result ({PlateScale_ArcsecPerPixel}), setting to 0");
                    PlateScale_ArcsecPerPixel = 0.0;
                }
                
                System.Diagnostics.Debug.WriteLine($"[CameraInfo] PlateScale calculated: {PlateScale_ArcsecPerPixel:F3} arcsec/pixel");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraInfo] Error calculating PlateScale: {ex.Message}");
                PlateScale_ArcsecPerPixel = 0.0;
            }
        }
        
        /// <summary>
        /// Recalculate Field of View from current settings
        /// </summary>
        public void RecalculateFOV()
        {
            try
            {
                if (PlateScale_ArcsecPerPixel <= 0)
                {
                    RecalculatePlateScale(); // Try to calculate if not set
                }
                
                if (PlateScale_ArcsecPerPixel <= 0 || CurrentWidth <= 0 || CurrentHeight <= 0)
                {
                    FOV_Width_deg = 0.0;
                    FOV_Height_deg = 0.0;
                    System.Diagnostics.Debug.WriteLine("[CameraInfo] Cannot calculate FOV: Invalid PlateScale or dimensions");
                    return;
                }
                
                // FOV = (pixels * plateScale) / 3600 degrees
                FOV_Width_deg = (CurrentWidth * PlateScale_ArcsecPerPixel) / 3600.0;
                FOV_Height_deg = (CurrentHeight * PlateScale_ArcsecPerPixel) / 3600.0;
                
                System.Diagnostics.Debug.WriteLine($"[CameraInfo] FOV calculated: {FOV_Width_deg:F3}° × {FOV_Height_deg:F3}°");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraInfo] Error calculating FOV: {ex.Message}");
                FOV_Width_deg = 0.0;
                FOV_Height_deg = 0.0;
            }
        }
        
        /// <summary>
        /// Recalculate all derived properties
        /// </summary>
        public void RecalculateAll()
        {
            RecalculatePlateScale();
            RecalculateFOV();
        }
        
        #endregion
        
        #region Factory Methods
        
        /// <summary>
        /// Create CameraInfo from AppSettings (fallback when camera not connected)
        /// Returns minimal info - actual values should come from camera hardware
        /// </summary>
        public static CameraInfo CreateFromSettings()
        {
            try
            {
                var settings = AppSettings.GetCameraSettings();
                
                // Use saved settings if available, otherwise use minimal defaults
                // The actual camera specs should be detected when camera connects
                int maxWidth = settings.Width > 0 ? settings.Width : 0;
                int maxHeight = settings.Height > 0 ? settings.Height : 0;
                double pixelSize = settings.PixelSize_um > 0 ? settings.PixelSize_um : 0;
                
                var info = new CameraInfo
                {
                    CameraName = maxWidth > 0 ? "Not Connected (Using Settings)" : "Not Connected",
                    IsConnected = false,
                    
                    // Use saved settings (from last connected camera)
                    MaxWidth = maxWidth,
                    MaxHeight = maxHeight,
                    PixelSize_um = pixelSize,
                    FocalLength_mm = settings.FocalLength_mm,
                    Binning = settings.Binning > 0 ? settings.Binning : 1,
                    ExposureTime_ms = settings.ExposureTime_ms > 0 ? settings.ExposureTime_ms : 1000,
                    Gain = settings.Gain,
                    Offset = settings.Offset,
                    
                    // Calculate current size (after binning) - only if we have valid dimensions
                    CurrentWidth = maxWidth > 0 && settings.Binning > 0 ? maxWidth / settings.Binning : 0,
                    CurrentHeight = maxHeight > 0 && settings.Binning > 0 ? maxHeight / settings.Binning : 0,
                    
                    // Use saved PlateScale if available
                    PlateScale_ArcsecPerPixel = settings.PlateScale_ArcsecPerPixel
                };
                
                // If PlateScale not in settings but we have valid data, calculate it
                if (info.PlateScale_ArcsecPerPixel <= 0 && pixelSize > 0 && settings.FocalLength_mm > 0)
                {
                    info.RecalculatePlateScale();
                }
                
                // Calculate FOV if we have valid dimensions
                if (info.CurrentWidth > 0 && info.CurrentHeight > 0 && info.PlateScale_ArcsecPerPixel > 0)
                {
                    info.RecalculateFOV();
                }
                
                System.Diagnostics.Debug.WriteLine("[CameraInfo] Created from AppSettings:");
                System.Diagnostics.Debug.WriteLine($"  Resolution: {info.MaxWidth}x{info.MaxHeight}");
                System.Diagnostics.Debug.WriteLine($"  Pixel Size: {info.PixelSize_um} ?m");
                System.Diagnostics.Debug.WriteLine($"  Binning: {info.Binning}x{info.Binning}");
                System.Diagnostics.Debug.WriteLine($"  Focal Length: {info.FocalLength_mm} mm");
                System.Diagnostics.Debug.WriteLine($"  PlateScale: {info.PlateScale_ArcsecPerPixel:F3} arcsec/pixel");
                System.Diagnostics.Debug.WriteLine($"  FOV: {info.FOV_Width_deg:F3}° × {info.FOV_Height_deg:F3}°");
                
                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraInfo] Error creating from settings: {ex.Message}");
                return new CameraInfo(); // Return default instance
            }
        }
        
        /// <summary>
        /// Load settings from AppSettings and recalculate
        /// </summary>
        public void LoadFromAppSettings()
        {
            try
            {
                var settings = AppSettings.GetCameraSettings();
                
                FocalLength_mm = settings.FocalLength_mm;
                Binning = settings.Binning;
                ExposureTime_ms = settings.ExposureTime_ms;
                Gain = settings.Gain;
                Offset = settings.Offset;
                
                // Use saved PlateScale if available
                if (settings.PlateScale_ArcsecPerPixel > 0)
                {
                    PlateScale_ArcsecPerPixel = settings.PlateScale_ArcsecPerPixel;
                }
                
                // Recalculate all derived values
                RecalculateAll();
                
                System.Diagnostics.Debug.WriteLine("[CameraInfo] Loaded settings from AppSettings");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraInfo] Error loading from settings: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Display Methods
        
        public override string ToString()
        {
            return $"{CameraName} ({MaxWidth}x{MaxHeight}, {PixelSize_um:F2}µm, FOV: {FOV_Width_deg:F3}° × {FOV_Height_deg:F3}°)";
        }
        
        /// <summary>
        /// Get formatted FOV string
        /// </summary>
        public string GetFOVString()
        {
            return $"{FOV_Width_deg:F3}° × {FOV_Height_deg:F3}°";
        }
        
        /// <summary>
        /// Get formatted PlateScale string
        /// </summary>
        public string GetPlateScaleString()
        {
            return $"{PlateScale_ArcsecPerPixel:F3} arcsec/pixel";
        }
        
        #endregion
    }
}
