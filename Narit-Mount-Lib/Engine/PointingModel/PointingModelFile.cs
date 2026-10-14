using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ForkMount_WPF.Engine.Manager;

namespace ForkMount_WPF.Engine.PointingModel
{
    /// <summary>
    /// Manages saving and loading pointing model files
    /// File format: Text-based, human-readable, Tpoint-compatible
    /// Extension: .pmd (Pointing Model Data)
    /// </summary>
    public class PointingModelFile
    {
        /// <summary>
        /// Save pointing model to file
        /// </summary>
        /// <param name="filepath">Full path to .pmd file</param>
        /// <param name="terms">Model terms to save</param>
        /// <param name="points">Original calibration points (for reference)</param>
        /// <param name="residualRMS">Residual RMS after model fit (arcsec)</param>
        public static void SaveModel(
            string filepath,
            PointingModelBuilder.ModelTerms terms,
            List<CalibrationPoint> points,
            double residualRMS)
        {
            var sb = new StringBuilder();
            
            // ===== HEADER =====
            sb.AppendLine("# ForkMount-WPF Pointing Model");
            sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Format Version: 1.0");
            sb.AppendLine();
            
            // ===== SUMMARY STATISTICS =====
            sb.AppendLine("[STATISTICS]");
            sb.AppendLine($"TotalPoints = {points.Count}");
            sb.AppendLine($"SolvedPoints = {points.Count(p => p.IsSolved)}");
            sb.AppendLine($"FailedPoints = {points.Count(p => p.SolveFailed)}");
            sb.AppendLine($"ResidualRMS_Arcsec = {residualRMS:F3}");
            
            // Calculate statistics
            var solvedPoints = points.Where(p => p.IsSolved && p.TotalError_Arcsec.HasValue).ToList();
            if (solvedPoints.Any())
            {
                var errors = solvedPoints.Select(p => p.TotalError_Arcsec.Value).ToList();
                sb.AppendLine($"MeanError_Arcsec = {errors.Average():F3}");
                sb.AppendLine($"MaxError_Arcsec = {errors.Max():F3}");
                sb.AppendLine($"MinError_Arcsec = {errors.Min():F3}");
            }
            sb.AppendLine();
            
            // ===== MODEL TERMS (Tpoint-compatible format) =====
            sb.AppendLine("[MODEL_TERMS]");
            sb.AppendLine("# Index Errors (constant offsets)");
            sb.AppendLine($"IH   = {terms.IH:F8}   # Azimuth index error (deg)");
            sb.AppendLine($"ID   = {terms.ID:F8}   # Altitude index error (deg)");
            sb.AppendLine();
            
            sb.AppendLine("# Axis Non-Perpendicularity");
            sb.AppendLine($"NP   = {terms.NP:F8}   # Non-perpendicularity (deg)");
            sb.AppendLine();
            
            sb.AppendLine("# Collimation Errors");
            sb.AppendLine($"CH   = {terms.CH:F8}   # Horizontal collimation (deg)");
            sb.AppendLine($"CV   = {terms.CV:F8}   # Vertical collimation (deg)");
            sb.AppendLine();
            
            sb.AppendLine("# Axis Misalignment");
            sb.AppendLine($"MA   = {terms.MA:F8}   # Azimuth axis misalignment (deg)");
            sb.AppendLine($"ME   = {terms.ME:F8}   # Altitude axis misalignment (deg)");
            sb.AppendLine();
            
            sb.AppendLine("# Encoder Errors");
            sb.AppendLine($"HCES = {terms.HCES:F8}   # Azimuth encoder sin error");
            sb.AppendLine($"HCEC = {terms.HCEC:F8}   # Azimuth encoder cos error");
            sb.AppendLine();
            
            sb.AppendLine("# Flexure Terms");
            sb.AppendLine($"TF   = {terms.TF:F8}   # Tube flexure (deg)");
            sb.AppendLine($"TX   = {terms.TX:F8}   # Tube flexure tilt (deg)");
            sb.AppendLine();
            
            // ===== CALIBRATION DATA (for reference) =====
            sb.AppendLine("[CALIBRATION_DATA]");
            sb.AppendLine("# Point  Az(deg)   Alt(deg)  RA_Err(\")  Dec_Err(\")  Total_Err(\")  Stars  Status");
            
            foreach (var point in points.Where(p => p.IsSolved).OrderBy(p => p.Index))
            {
                sb.AppendLine($"{point.Index,6}  " +
                             $"{point.TargetAzimuth,8:F2}  " +
                             $"{point.TargetAltitude,8:F2}  " +
                             $"{point.RaError_Arcsec,10:F2}  " +
                             $"{point.DecError_Arcsec,10:F2}  " +
                             $"{point.TotalError_Arcsec,12:F2}  " +
                             $"{point.NumMatchedStars,6}  " +
                             $"SOLVED");
            }
            
            // Write to file
            File.WriteAllText(filepath, sb.ToString());
            
            System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Model saved to: {filepath}");
            System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Residual RMS: {residualRMS:F3} arcsec");
        }
        
        /// <summary>
        /// Load pointing model from file
        /// </summary>
        /// <param name="filepath">Full path to .pmd file</param>
        /// <returns>Loaded model terms, or null if file doesn't exist</returns>
        public static PointingModelBuilder.ModelTerms LoadModel(string filepath)
        {
            if (!File.Exists(filepath))
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Model file not found: {filepath}");
                return null;
            }
            
            var terms = new PointingModelBuilder.ModelTerms();
            bool inModelSection = false;
            
            try
            {
                foreach (var line in File.ReadLines(filepath))
                {
                    // Skip comments and empty lines
                    if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                        continue;
                    
                    // Check for section headers
                    if (line.StartsWith("[MODEL_TERMS]"))
                    {
                        inModelSection = true;
                        continue;
                    }
                    else if (line.StartsWith("["))
                    {
                        inModelSection = false;
                        continue;
                    }
                    
                    // Parse model terms
                    if (inModelSection && line.Contains("="))
                    {
                        var parts = line.Split('=');
                        if (parts.Length >= 2)
                        {
                            string key = parts[0].Trim();
                            string valueStr = parts[1].Split('#')[0].Trim(); // Remove comment
                            
                            if (double.TryParse(valueStr, out double value))
                            {
                                switch (key)
                                {
                                    case "IH":   terms.IH = value; break;
                                    case "ID":   terms.ID = value; break;
                                    case "NP":   terms.NP = value; break;
                                    case "CH":   terms.CH = value; break;
                                    case "CV":   terms.CV = value; break;
                                    case "MA":   terms.MA = value; break;
                                    case "ME":   terms.ME = value; break;
                                    case "HCES": terms.HCES = value; break;
                                    case "HCEC": terms.HCEC = value; break;
                                    case "TF":   terms.TF = value; break;
                                    case "TX":   terms.TX = value; break;
                                }
                            }
                        }
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Model loaded from: {filepath}");
                System.Diagnostics.Debug.WriteLine($"[PointingModelFile] IH={terms.IH:F4}° ID={terms.ID:F4}°");
                System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Max term: {terms.GetMaxTermMagnitude():F4}°");
                
                return terms;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Error loading model: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Check if pointing model file exists
        /// </summary>
        public static bool ModelFileExists(string filepath)
        {
            return File.Exists(filepath);
        }
        
        /// <summary>
        /// Get default pointing model file path
        /// Saved in CalibrationData folder as .pxp (PlaneWave format)
        /// </summary>
        public static string GetDefaultModelPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "CalibrationData", "Default_Model.pxp");
        }
        
        /// <summary>
        /// Delete pointing model file
        /// </summary>
        public static bool DeleteModel(string filepath)
        {
            try
            {
                if (File.Exists(filepath))
                {
                    File.Delete(filepath);
                    System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Model deleted: {filepath}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PointingModelFile] Error deleting model: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Get model file info (statistics)
        /// </summary>
        public static ModelFileInfo GetModelInfo(string filepath)
        {
            if (!File.Exists(filepath))
                return null;
            
            var info = new ModelFileInfo
            {
                FilePath = filepath,
                LastModified = File.GetLastWriteTime(filepath)
            };
            
            try
            {
                foreach (var line in File.ReadLines(filepath))
                {
                    if (line.StartsWith("# Generated:"))
                    {
                        var dateStr = line.Replace("# Generated:", "").Trim();
                        if (DateTime.TryParse(dateStr, out DateTime generated))
                        {
                            info.GeneratedDate = generated;
                        }
                    }
                    else if (line.Contains("ResidualRMS_Arcsec"))
                    {
                        var parts = line.Split('=');
                        if (parts.Length >= 2 && double.TryParse(parts[1].Trim(), out double rms))
                        {
                            info.ResidualRMS = rms;
                        }
                    }
                    else if (line.Contains("SolvedPoints"))
                    {
                        var parts = line.Split('=');
                        if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int solved))
                        {
                            info.SolvedPoints = solved;
                        }
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            
            return info;
        }
        
        /// <summary>
        /// Model file information
        /// </summary>
        public class ModelFileInfo
        {
            public string FilePath { get; set; }
            public DateTime GeneratedDate { get; set; }
            public DateTime LastModified { get; set; }
            public double ResidualRMS { get; set; }
            public int SolvedPoints { get; set; }
        }
    }
}
