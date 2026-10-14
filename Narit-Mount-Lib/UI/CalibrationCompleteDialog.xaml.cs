using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
// Use alias to avoid conflict with ForkMount_WPF.UI.CalibrationPoint
using CalibrationPointModel = ForkMount_WPF.Engine.Manager.CalibrationPoint;

namespace ForkMount_WPF.UI
{
    public partial class CalibrationCompleteDialog : Window
    {
        public CalibrationDialogAction SelectedAction { get; private set; } = CalibrationDialogAction.KeepCurrentModel;
        public int TotalPoints { get; set; }
        public int CompletedPoints { get; set; }
        public int SolvedPoints { get; set; }
        public int FailedPoints { get; set; }
        public int PendingPoints { get; set; }  // [NEW] Pending/Skipped points
        public double? OverallRMS { get; set; }
        public double? AverageError { get; set; }
        public double? MaxError { get; set; }
        public double? MinError { get; set; }
        public List<CalibrationPointModel> CalibrationPoints { get; set; }
        public string DialogTitle { get; set; }
        public string DialogSubtitle { get; set; }
        public string LatestModelPath { get; set; }
        public string LatestModelStatus { get; set; }
        public bool CanUseLatestModel { get; set; }
        public string UseLatestModelButtonText { get; set; } = "Use This Model";
        public bool ShowRestartButton { get; set; } = true;

        public CalibrationCompleteDialog()
        {
            InitializeComponent();
            Loaded += CalibrationCompleteDialog_Loaded;
        }

        private void CalibrationCompleteDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Update UI with statistics
            txtTotalPoints.Text = TotalPoints.ToString();
            txtCompletedPoints.Text = CompletedPoints.ToString();
            txtSolvedPoints.Text = SolvedPoints.ToString();
            txtFailedPoints.Text = FailedPoints.ToString();
            txtDialogTitle.Text = string.IsNullOrWhiteSpace(DialogTitle) ? "Mount Calibration Complete!" : DialogTitle;
            txtDialogSubtitle.Text = string.IsNullOrWhiteSpace(DialogSubtitle) ? "Review the latest pointing model before applying it." : DialogSubtitle;
            txtLatestModelStatus.Text = string.IsNullOrWhiteSpace(LatestModelStatus) ? "No new model file is ready yet." : LatestModelStatus;
            txtLatestModelPath.Text = string.IsNullOrWhiteSpace(LatestModelPath) ? string.Empty : LatestModelPath;
            btnUseLatestModel.Content = UseLatestModelButtonText;
            btnUseLatestModel.IsEnabled = CanUseLatestModel;
            btnRestartCalibration.Visibility = ShowRestartButton ? Visibility.Visible : Visibility.Collapsed;
            
            // [NEW] Show pending points if any
            if (PendingPoints > 0)
            {
                txtPendingPoints.Text = PendingPoints.ToString();
                rowPending.Visibility = Visibility.Visible;
            }
            else
            {
                rowPending.Visibility = Visibility.Collapsed;
            }
            
            // [DEBUG] Log statistics for verification
            System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] Statistics:");
            System.Diagnostics.Debug.WriteLine($"  Total: {TotalPoints}");
            System.Diagnostics.Debug.WriteLine($"  Completed: {CompletedPoints}");
            System.Diagnostics.Debug.WriteLine($"  Solved: {SolvedPoints}");
            System.Diagnostics.Debug.WriteLine($"  Failed: {FailedPoints}");
            System.Diagnostics.Debug.WriteLine($"  Pending/Skipped: {PendingPoints}");
            System.Diagnostics.Debug.WriteLine($"  Check: Solved({SolvedPoints}) + Failed({FailedPoints}) + Pending({PendingPoints}) = {SolvedPoints + FailedPoints + PendingPoints} (should be {TotalPoints})");
            
            txtOverallRMS.Text = OverallRMS.HasValue ? $"{OverallRMS.Value:F2}" : "--";
            txtAverageError.Text = AverageError.HasValue ? $"{AverageError.Value:F2}" : "--";
            txtMinError.Text = MinError.HasValue ? $"{MinError.Value:F2}" : "--";
            txtMaxError.Text = MaxError.HasValue ? $"{MaxError.Value:F2}" : "--";
            
            // [NEW] Update Failed color: Green if 0, Red if > 0
            if (FailedPoints == 0)
            {
                txtFailedPoints.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981"));  // Green
            }
            else
            {
                txtFailedPoints.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));  // Red
            }
            
            // Update colors based on quality
            if (OverallRMS.HasValue)
            {
                if (OverallRMS.Value < 2.0)
                {
                    txtOverallRMS.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981"));  // Green
                }
                else if (OverallRMS.Value < 5.0)
                {
                    txtOverallRMS.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));  // Orange
                }
                else
                {
                    txtOverallRMS.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));  // Red
                }
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // Double-click to maximize (disabled for dialog)
            }
            else
            {
                this.DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = CalibrationDialogAction.KeepCurrentModel;
            this.Close();
        }

        private void UseLatestModelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = CalibrationDialogAction.UseLatestModel;
            DialogResult = true;
            Close();
        }

        private void KeepCurrentModelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = CalibrationDialogAction.KeepCurrentModel;
            DialogResult = true;
            Close();
        }

        private void RestartCalibrationButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = CalibrationDialogAction.RestartCalibration;
            DialogResult = true;
            Close();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Title = "Save Calibration Data",
                    Filter = "PlaneWave Format (*.pxp)|*.pxp|Calibration Files (*.cal)|*.cal|CSV Files (*.csv)|*.csv|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    FilterIndex = 1,
                    DefaultExt = ".pxp",
                    FileName = $"MountCalibration_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    string extension = Path.GetExtension(saveFileDialog.FileName).ToLowerInvariant();
                    
                    switch (extension)
                    {
                        case ".pxp":
                            SaveAsPlaneWaveFormat(saveFileDialog.FileName);
                            break;
                        case ".cal":
                            SaveAsCalibrationFormat(saveFileDialog.FileName);
                            break;
                        case ".csv":
                            SaveAsCSV(saveFileDialog.FileName);
                            break;
                        case ".json":
                            SaveAsJSON(saveFileDialog.FileName);
                            break;
                        default:
                            SaveAsCalibrationFormat(saveFileDialog.FileName);
                            break;
                    }
                    
                    MessageBox.Show(
                        $"Calibration data saved successfully!\n\nFile: {Path.GetFileName(saveFileDialog.FileName)}",
                        "Information",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    
                    // [NEW] AUTO-BUILD POINTING MODEL after saving calibration data
                    BuildPointingModel();
                    
                    this.DialogResult = true;
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error saving calibration data:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        
        
        /// <summary>
        /// Build and save pointing model from calibration data
        /// NOTE: Model may have been already built before showing dialog
        /// This method checks if final file exists, or rebuilds if needed
        /// </summary>
        private void BuildPointingModel()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[CalibrationCompleteDialog] Checking pointing model...");
                
                // Check if we have enough solved points
                int solvedCount = CalibrationPoints.Count(p => p.IsSolved);
                if (solvedCount < 10)
                {
                    MessageBox.Show(
                        $"Cannot build pointing model: Only {solvedCount} solved points.\n" +
                        $"Need at least 10 points for reliable model.",
                        "Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                
                string finalPath = Engine.PointingModel.PointingModelFile.GetDefaultModelPath();
                
                // Check if final model file already exists (built before dialog)
                if (File.Exists(finalPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] ? Model file already exists: {finalPath}");
                    
                    // Load model info to show in message
                    var modelInfo = Engine.PointingModel.PointingModelFile.GetModelInfo(finalPath);
                    
                    MessageBox.Show(
                        $"? Pointing Model Already Saved!\n\n" +
                        $"Solved Points: {modelInfo.SolvedPoints}\n" +
                        $"Residual RMS: {modelInfo.ResidualRMS:F3} arcsec\n\n" +
                        $"Model file:\n{Path.GetFileName(finalPath)}\n\n" +
                        $"The model will be automatically loaded on next startup.",
                        "Information",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                
                // Model doesn't exist yet - check for progressive file
                string progressivePath = FindProgressiveModelFile();
                
                if (progressivePath != null && File.Exists(progressivePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] Found progressive file: {progressivePath}");
                    System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] Building final model...");
                    
                    // Build model from calibration data
                    var builder = new Engine.PointingModel.PointingModelBuilder();
                    var terms = builder.BuildModel(CalibrationPoints);
                    var residualRMS = builder.CalculateResidualRMS(terms, CalibrationPoints);
                    
                    System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] Model built: RMS = {residualRMS:F3} arcsec");
                    
                    // Update progressive file with model terms
                    UpdateProgressiveFileWithModel(progressivePath, terms, residualRMS);
                    
                    // Rename to final path
                    File.Move(progressivePath, finalPath);
                    
                    System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] ? Finalized: {finalPath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] No progressive file - creating from scratch");
                    
                    // Build model from scratch
                    var builder = new Engine.PointingModel.PointingModelBuilder();
                    var terms = builder.BuildModel(CalibrationPoints);
                    var residualRMS = builder.CalculateResidualRMS(terms, CalibrationPoints);
                    
                    // Save model
                    Engine.PointingModel.PointingModelFile.SaveModel(
                        finalPath,
                        terms,
                        CalibrationPoints,
                        residualRMS
                    );
                }
                
                // Load final model info
                var finalModelInfo = Engine.PointingModel.PointingModelFile.GetModelInfo(finalPath);
                
                // Show success message
                MessageBox.Show(
                    $"? Pointing Model Created Successfully!\n\n" +
                    $"Solved Points: {finalModelInfo.SolvedPoints}\n" +
                    $"Residual RMS: {finalModelInfo.ResidualRMS:F3} arcsec\n\n" +
                    $"Model saved to:\n{Path.GetFileName(finalPath)}\n\n" +
                    $"The model will be automatically loaded on next startup.",
                    "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] Error building model: {ex.Message}");
                
                MessageBox.Show(
                    $"Error building pointing model:\n\n{ex.Message}\n\n" +
                    $"Calibration data was saved, but model creation failed.\n" +
                    $"You can try building the model again later.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Find progressive model file (search for *_INPROGRESS.pmd)
        /// </summary>
        private string FindProgressiveModelFile()
        {
            try
            {
                string calibrationDataDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "CalibrationData"
                );
                
                if (!Directory.Exists(calibrationDataDir))
                    return null;
                
                // Find any file matching pattern: PointingModel_*_INPROGRESS.pmd
                var progressiveFiles = Directory.GetFiles(calibrationDataDir, "PointingModel_*_INPROGRESS.pmd");
                
                if (progressiveFiles.Length > 0)
                {
                    // Return most recent if multiple exist
                    return progressiveFiles
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .First();
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Update progressive file with fitted model terms and final statistics
        /// </summary>
        private void UpdateProgressiveFileWithModel(
            string filePath,
            Engine.PointingModel.PointingModelBuilder.ModelTerms terms,
            double residualRMS)
        {
            var lines = File.ReadAllLines(filePath).ToList();
            
            // Update status
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("# Status:"))
                    lines[i] = "# Status: COMPLETED";
                else if (lines[i].StartsWith("# Started:"))
                {
                    lines.Insert(i + 1, $"# Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    break;
                }
            }
            
            // Update statistics
            int solvedCount = CalibrationPoints.Count(p => p.IsSolved);
            int failedCount = CalibrationPoints.Count(p => p.SolveFailed);
            int pendingCount = CalibrationPoints.Count - solvedCount - failedCount;
            
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("SolvedPoints"))
                    lines[i] = $"SolvedPoints = {solvedCount}";
                else if (lines[i].StartsWith("FailedPoints"))
                    lines[i] = $"FailedPoints = {failedCount}";
                else if (lines[i].StartsWith("PendingPoints"))
                    lines[i] = $"PendingPoints = {pendingCount}";
                else if (lines[i].StartsWith("ResidualRMS_Arcsec"))
                    lines[i] = $"ResidualRMS_Arcsec = {residualRMS:F3}";
            }
            
            // Update model terms
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains("# Current status: PENDING"))
                    lines[i] = $"# Model fitted from {solvedCount} points";
                else if (lines[i].StartsWith("IH   ="))
                    lines[i] = $"IH   = {terms.IH:F8}   # Azimuth index error (deg)";
                else if (lines[i].StartsWith("ID   ="))
                    lines[i] = $"ID   = {terms.ID:F8}   # Altitude index error (deg)";
                else if (lines[i].StartsWith("NP   ="))
                    lines[i] = $"NP   = {terms.NP:F8}   # Non-perpendicularity (deg)";
                else if (lines[i].StartsWith("CH   ="))
                    lines[i] = $"CH   = {terms.CH:F8}   # Horizontal collimation (deg)";
                else if (lines[i].StartsWith("CV   ="))
                    lines[i] = $"CV   = {terms.CV:F8}   # Vertical collimation (deg)";
                else if (lines[i].StartsWith("MA   ="))
                    lines[i] = $"MA   = {terms.MA:F8}   # Azimuth axis misalignment (deg)";
                else if (lines[i].StartsWith("ME   ="))
                    lines[i] = $"ME   = {terms.ME:F8}   # Altitude axis misalignment (deg)";
                else if (lines[i].StartsWith("HCES ="))
                    lines[i] = $"HCES = {terms.HCES:F8}   # Azimuth encoder sin error";
                else if (lines[i].StartsWith("HCEC ="))
                    lines[i] = $"HCEC = {terms.HCEC:F8}   # Azimuth encoder cos error";
                else if (lines[i].StartsWith("TF   ="))
                    lines[i] = $"TF   = {terms.TF:F8}   # Tube flexure (deg)";
                else if (lines[i].StartsWith("TX   ="))
                    lines[i] = $"TX   = {terms.TX:F8}   # Tube flexure tilt (deg)";
            }
            
            File.WriteAllLines(filePath, lines);
        }
        
        private void SaveAsPlaneWaveFormat(string filePath)
        {
            try
            {
                // Create observer location (use default Bangkok coordinates)
                var observerLocation = new Engine.Utility.ObserverLocationSettings
                {
                    Latitude_Deg = 13.7563,   // Bangkok latitude
                    Longitude_Deg = 100.5018  // Bangkok longitude
                };
                
                // Save in PlaneWave .pxp format
                Engine.PointingModel.PlaneWaveModelFile.SavePlaneWaveFormat(
                    filePath,
                    CalibrationPoints,
                    observerLocation
                );
                
                System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] ? Saved PlaneWave format: {filePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CalibrationCompleteDialog] ? Failed to save PlaneWave format: {ex.Message}");
                throw;
            }
        }

        private void SaveAsCalibrationFormat(string filePath)
        {
            var sb = new StringBuilder();
            
            // Header
            sb.AppendLine("# Mount Calibration Data");
            sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Total Points: {TotalPoints}");
            sb.AppendLine($"# Solved Points: {SolvedPoints}");
            sb.AppendLine($"# Overall RMS: {OverallRMS:F3} arcsec");
            sb.AppendLine();
            
            // Column headers
            sb.AppendLine("# Point\tAz(deg)\tAlt(deg)\tRA(deg)\tDec(deg)\tRA_Err(\")\tDec_Err(\")\tTotal_Err(\")\tStars\tStatus");
            
            // Data
            foreach (var point in CalibrationPoints.Where(p => p.IsSolved))
            {
                double raDeg = point.SolvedRa_J2000_Rads.HasValue ? point.SolvedRa_J2000_Rads.Value * 180.0 / Math.PI : 0.0;
                double decDeg = point.SolvedDec_J2000_Rads.HasValue ? point.SolvedDec_J2000_Rads.Value * 180.0 / Math.PI : 0.0;
                
                sb.AppendLine($"{point.Index}\t" +
                             $"{point.TargetAzimuth:F2}\t" +
                             $"{point.TargetAltitude:F2}\t" +
                             $"{raDeg:F4}\t" +
                             $"{decDeg:F4}\t" +
                             $"{point.RaError_Arcsec:F2}\t" +
                             $"{point.DecError_Arcsec:F2}\t" +
                             $"{point.TotalError_Arcsec:F2}\t" +
                             $"{point.NumMatchedStars}\t" +
                             $"SOLVED");
            }
            
            File.WriteAllText(filePath, sb.ToString());
        }

        private void SaveAsCSV(string filePath)
        {
            var sb = new StringBuilder();
            
            // CSV Header
            sb.AppendLine("Point,Azimuth(deg),Altitude(deg),RA(deg),Dec(deg),RA_Error(arcsec),Dec_Error(arcsec),Total_Error(arcsec),Stars,Status");
            
            // Data
            foreach (var point in CalibrationPoints.Where(p => p.IsSolved))
            {
                double raDeg = point.SolvedRa_J2000_Rads.HasValue ? point.SolvedRa_J2000_Rads.Value * 180.0 / Math.PI : 0.0;
                double decDeg = point.SolvedDec_J2000_Rads.HasValue ? point.SolvedDec_J2000_Rads.Value * 180.0 / Math.PI : 0.0;
                
                sb.AppendLine($"{point.Index}," +
                             $"{point.TargetAzimuth:F2}," +
                             $"{point.TargetAltitude:F2}," +
                             $"{raDeg:F4}," +
                             $"{decDeg:F4}," +
                             $"{point.RaError_Arcsec:F2}," +
                             $"{point.DecError_Arcsec:F2}," +
                             $"{point.TotalError_Arcsec:F2}," +
                             $"{point.NumMatchedStars}," +
                             $"SOLVED");
            }
            
            File.WriteAllText(filePath, sb.ToString());
        }

        private void SaveAsJSON(string filePath)
        {
            // Simple JSON export (can be enhanced with JSON serializer)
            var sb = new StringBuilder();
            
            sb.AppendLine("{");
            sb.AppendLine($"  \"generatedDate\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",");
            sb.AppendLine($"  \"totalPoints\": {TotalPoints},");
            sb.AppendLine($"  \"solvedPoints\": {SolvedPoints},");
            sb.AppendLine($"  \"overallRMS\": {OverallRMS:F3},");
            sb.AppendLine($"  \"averageError\": {AverageError:F3},");
            sb.AppendLine($"  \"minError\": {MinError:F3},");
            sb.AppendLine($"  \"maxError\": {MaxError:F3},");
            sb.AppendLine("  \"points\": [");
            
            var solvedPoints = CalibrationPoints.Where(p => p.IsSolved).ToList();
            for (int i = 0; i < solvedPoints.Count; i++)
            {
                var point = solvedPoints[i];
                double raDeg = point.SolvedRa_J2000_Rads.HasValue ? point.SolvedRa_J2000_Rads.Value * 180.0 / Math.PI : 0.0;
                double decDeg = point.SolvedDec_J2000_Rads.HasValue ? point.SolvedDec_J2000_Rads.Value * 180.0 / Math.PI : 0.0;
                
                sb.AppendLine("    {");
                sb.AppendLine($"      \"index\": {point.Index},");
                sb.AppendLine($"      \"azimuth\": {point.TargetAzimuth:F2},");
                sb.AppendLine($"      \"altitude\": {point.TargetAltitude:F2},");
                sb.AppendLine($"      \"ra\": {raDeg:F4},");
                sb.AppendLine($"      \"dec\": {decDeg:F4},");
                sb.AppendLine($"      \"raError\": {point.RaError_Arcsec:F2},");
                sb.AppendLine($"      \"decError\": {point.DecError_Arcsec:F2},");
                sb.AppendLine($"      \"totalError\": {point.TotalError_Arcsec:F2},");
                sb.AppendLine($"      \"stars\": {point.NumMatchedStars}");
                sb.AppendLine(i < solvedPoints.Count - 1 ? "    }," : "    }");
            }
            
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            
            File.WriteAllText(filePath, sb.ToString());
        }
    }

    public enum CalibrationDialogAction
    {
        UseLatestModel,
        KeepCurrentModel,
        RestartCalibration
    }
}
