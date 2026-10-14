# Pointing Model Implementation Guide

## ?? ?????????????? Plate Solve ????? 40 Points

### Phase 1: Data Collection (????????? ?)
```
[Complete] 40 calibration points
[Complete] Plate solve ????? point
[Complete] ???? errors: RA Error, Dec Error, Total RMS
[Complete] ?????????? FITS files + calibration data
```

---

## Phase 2: Build Pointing Model (?????????)

### 2.1 ????? Pointing Model Builder Class

**File:** `ForkMount-WPF\Engine\PointingModel\PointingModelBuilder.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearRegression;

namespace ForkMount_WPF.Engine.PointingModel
{
    /// <summary>
    /// ????? Pointing Model ????????? plate solving 40 points
    /// ??? least-squares fitting ??????? error terms
    /// </summary>
    public class PointingModelBuilder
    {
        /// <summary>
        /// Pointing Model Terms (??????? Tpoint algorithm)
        /// </summary>
        public class ModelTerms
        {
            // Index Errors (???????? offset)
            public double IH { get; set; }  // Azimuth index error (deg)
            public double ID { get; set; }  // Altitude index error (deg)
            
            // Non-perpendicularity (????????????????)
            public double NP { get; set; }  // Non-perpendicularity (deg)
            
            // Collimation Error (?????????????????)
            public double CH { get; set; }  // Horizontal collimation (deg)
            public double CV { get; set; }  // Vertical collimation (deg)
            
            // Mechanical Errors
            public double MA { get; set; }  // Azimuth axis misalignment (deg)
            public double ME { get; set; }  // Altitude axis misalignment (deg)
            
            // Encoder Errors
            public double HCES { get; set; } // Azimuth encoder sin error
            public double HCEC { get; set; } // Azimuth encoder cos error
            
            // Flexure Terms
            public double TF { get; set; }  // Tube flexure
            public double TX { get; set; }  // Tube flexure tilt
        }
        
        /// <summary>
        /// ????? Pointing Model ??? calibration points
        /// </summary>
        public ModelTerms BuildModel(List<CalibrationPoint> points)
        {
            // ???? points ??? solve ??????????????
            var validPoints = points.Where(p => 
                p.IsSolved && 
                p.RaError_Arcsec.HasValue && 
                p.DecError_Arcsec.HasValue
            ).ToList();
            
            if (validPoints.Count < 10)
            {
                throw new Exception($"Too few valid points: {validPoints.Count}. Need at least 10.");
            }
            
            // ????? design matrix ?????? least-squares fitting
            var designMatrix = BuildDesignMatrix(validPoints);
            var errorVector = BuildErrorVector(validPoints);
            
            // Solve: coefficients = (A^T * A)^-1 * A^T * b
            var coefficients = MultipleRegression.QR(designMatrix, errorVector);
            
            // ???? coefficients ???? ModelTerms
            return CoefficientsToTerms(coefficients);
        }
        
        /// <summary>
        /// ????? design matrix A ?????? least-squares
        /// ????? row = 1 observation point
        /// ????? column = 1 error term (IH, ID, NP, CH, CV, ...)
        /// </summary>
        private double[,] BuildDesignMatrix(List<CalibrationPoint> points)
        {
            int n = points.Count;
            int nTerms = 11; // IH, ID, NP, CH, CV, MA, ME, HCES, HCEC, TF, TX
            
            var matrix = new double[n, nTerms];
            
            for (int i = 0; i < n; i++)
            {
                var point = points[i];
                double az = point.TargetAzimuth * Math.PI / 180.0;  // radians
                double alt = point.TargetAltitude * Math.PI / 180.0; // radians
                
                // IH: Index error azimuth (constant offset)
                matrix[i, 0] = 1.0;
                
                // ID: Index error altitude (constant offset)
                matrix[i, 1] = 1.0;
                
                // NP: Non-perpendicularity (tan(alt))
                matrix[i, 2] = Math.Tan(alt);
                
                // CH: Horizontal collimation (sec(alt))
                matrix[i, 3] = 1.0 / Math.Cos(alt);
                
                // CV: Vertical collimation (constant)
                matrix[i, 4] = 1.0;
                
                // MA: Azimuth axis misalignment (cos(az)*tan(alt))
                matrix[i, 5] = Math.Cos(az) * Math.Tan(alt);
                
                // ME: Altitude axis misalignment (sin(az)*tan(alt))
                matrix[i, 6] = Math.Sin(az) * Math.Tan(alt);
                
                // HCES: Azimuth encoder sin error
                matrix[i, 7] = Math.Sin(az);
                
                // HCEC: Azimuth encoder cos error
                matrix[i, 8] = Math.Cos(az);
                
                // TF: Tube flexure (cos(alt))
                matrix[i, 9] = Math.Cos(alt);
                
                // TX: Tube flexure tilt (sin(alt))
                matrix[i, 10] = Math.Sin(alt);
            }
            
            return matrix;
        }
        
        /// <summary>
        /// ????? error vector b (observed errors)
        /// </summary>
        private double[] BuildErrorVector(List<CalibrationPoint> points)
        {
            int n = points.Count;
            var vector = new double[n];
            
            for (int i = 0; i < n; i++)
            {
                // ??? azimuth error ???? primary error
                // (?????? fit RA/Dec errors ???????)
                vector[i] = points[i].RaError_Arcsec.Value;
            }
            
            return vector;
        }
        
        /// <summary>
        /// ???? coefficients array ???? ModelTerms object
        /// </summary>
        private ModelTerms CoefficientsToTerms(double[] coeffs)
        {
            return new ModelTerms
            {
                IH = coeffs[0],
                ID = coeffs[1],
                NP = coeffs[2],
                CH = coeffs[3],
                CV = coeffs[4],
                MA = coeffs[5],
                ME = coeffs[6],
                HCES = coeffs[7],
                HCEC = coeffs[8],
                TF = coeffs[9],
                TX = coeffs[10]
            };
        }
        
        /// <summary>
        /// ????? RMS residual error ???? apply model
        /// </summary>
        public double CalculateResidualRMS(
            ModelTerms terms, 
            List<CalibrationPoint> points)
        {
            var validPoints = points.Where(p => p.IsSolved).ToList();
            
            double sumSquaredError = 0;
            foreach (var point in validPoints)
            {
                // Apply model correction
                var correctedError = ApplyModelCorrection(
                    point.TargetAzimuth, 
                    point.TargetAltitude, 
                    terms
                );
                
                // Calculate residual
                double residual = point.RaError_Arcsec.Value - correctedError;
                sumSquaredError += residual * residual;
            }
            
            return Math.Sqrt(sumSquaredError / validPoints.Count);
        }
        
        /// <summary>
        /// ????? correction value ??? model terms
        /// </summary>
        private double ApplyModelCorrection(
            double azimuthDeg, 
            double altitudeDeg, 
            ModelTerms terms)
        {
            double az = azimuthDeg * Math.PI / 180.0;
            double alt = altitudeDeg * Math.PI / 180.0;
            
            double correction = 0;
            correction += terms.IH;
            correction += terms.NP * Math.Tan(alt);
            correction += terms.CH / Math.Cos(alt);
            correction += terms.MA * Math.Cos(az) * Math.Tan(alt);
            correction += terms.ME * Math.Sin(az) * Math.Tan(alt);
            correction += terms.HCES * Math.Sin(az);
            correction += terms.HCEC * Math.Cos(az);
            correction += terms.TF * Math.Cos(alt);
            correction += terms.TX * Math.Sin(alt);
            
            return correction;
        }
    }
}
```

---

### 2.2 ????? Model File Format

**File:** `ForkMount-WPF\Engine\PointingModel\PointingModelFile.cs`

```csharp
using System;
using System.IO;
using System.Text;

namespace ForkMount_WPF.Engine.PointingModel
{
    /// <summary>
    /// ?????? save/load pointing model file
    /// Format: Text-based, human-readable
    /// </summary>
    public class PointingModelFile
    {
        /// <summary>
        /// ?????? pointing model ???? file
        /// </summary>
        public static void SaveModel(
            string filepath, 
            PointingModelBuilder.ModelTerms terms,
            List<CalibrationPoint> points,
            double residualRMS)
        {
            var sb = new StringBuilder();
            
            // Header
            sb.AppendLine("# Fork Mount Pointing Model");
            sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# Total Points: {points.Count}");
            sb.AppendLine($"# Solved Points: {points.Count(p => p.IsSolved)}");
            sb.AppendLine($"# Residual RMS: {residualRMS:F3} arcsec");
            sb.AppendLine();
            
            // Model Terms (Tpoint-compatible format)
            sb.AppendLine("[MODEL_TERMS]");
            sb.AppendLine($"IH  = {terms.IH:F6}   # Azimuth index error (deg)");
            sb.AppendLine($"ID  = {terms.ID:F6}   # Altitude index error (deg)");
            sb.AppendLine($"NP  = {terms.NP:F6}   # Non-perpendicularity (deg)");
            sb.AppendLine($"CH  = {terms.CH:F6}   # Horizontal collimation (deg)");
            sb.AppendLine($"CV  = {terms.CV:F6}   # Vertical collimation (deg)");
            sb.AppendLine($"MA  = {terms.MA:F6}   # Azimuth axis misalignment (deg)");
            sb.AppendLine($"ME  = {terms.ME:F6}   # Altitude axis misalignment (deg)");
            sb.AppendLine($"HCES = {terms.HCES:F6} # Azimuth encoder sin error");
            sb.AppendLine($"HCEC = {terms.HCEC:F6} # Azimuth encoder cos error");
            sb.AppendLine($"TF  = {terms.TF:F6}   # Tube flexure");
            sb.AppendLine($"TX  = {terms.TX:F6}   # Tube flexure tilt");
            sb.AppendLine();
            
            // Calibration Points Summary
            sb.AppendLine("[CALIBRATION_DATA]");
            sb.AppendLine("# Point  Az(deg)  Alt(deg)  RA_Err(\")  Dec_Err(\")  Total_Err(\")  Status");
            
            foreach (var point in points.Where(p => p.IsSolved))
            {
                sb.AppendLine($"{point.Index,5}  " +
                             $"{point.TargetAzimuth,7:F2}  " +
                             $"{point.TargetAltitude,7:F2}  " +
                             $"{point.RaError_Arcsec,10:F2}  " +
                             $"{point.DecError_Arcsec,10:F2}  " +
                             $"{point.TotalError_Arcsec,12:F2}  " +
                             $"SOLVED");
            }
            
            File.WriteAllText(filepath, sb.ToString());
        }
        
        /// <summary>
        /// ???? pointing model ??? file
        /// </summary>
        public static PointingModelBuilder.ModelTerms LoadModel(string filepath)
        {
            var terms = new PointingModelBuilder.ModelTerms();
            
            foreach (var line in File.ReadLines(filepath))
            {
                // Skip comments and empty lines
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                    continue;
                
                // Parse model terms
                if (line.Contains("="))
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
                                case "IH": terms.IH = value; break;
                                case "ID": terms.ID = value; break;
                                case "NP": terms.NP = value; break;
                                case "CH": terms.CH = value; break;
                                case "CV": terms.CV = value; break;
                                case "MA": terms.MA = value; break;
                                case "ME": terms.ME = value; break;
                                case "HCES": terms.HCES = value; break;
                                case "HCEC": terms.HCEC = value; break;
                                case "TF": terms.TF = value; break;
                                case "TX": terms.TX = value; break;
                            }
                        }
                    }
                }
            }
            
            return terms;
        }
    }
}
```

---

### 2.3 ????? Pointing Correction ?? Tracking Controller

**File:** `ForkMount-WPF\Engine\Tracking\SiderealTrackingController.cs`

????? method:

```csharp
/// <summary>
/// Apply pointing model correction to target coordinates
/// </summary>
public (double correctedAz, double correctedAlt) ApplyPointingCorrection(
    double targetAz, 
    double targetAlt)
{
    // Load pointing model if not already loaded
    if (_pointingModel == null)
    {
        string modelPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, 
            "PointingModel.pmd"
        );
        
        if (File.Exists(modelPath))
        {
            _pointingModel = PointingModelFile.LoadModel(modelPath);
        }
        else
        {
            // No model - return uncorrected
            return (targetAz, targetAlt);
        }
    }
    
    // Calculate correction
    double azCorrection = CalculateAzimuthCorrection(targetAz, targetAlt);
    double altCorrection = CalculateAltitudeCorrection(targetAz, targetAlt);
    
    // Apply correction
    double correctedAz = targetAz + azCorrection;
    double correctedAlt = targetAlt + altCorrection;
    
    return (correctedAz, correctedAlt);
}
```

---

## Phase 3: ????????? Pointing Model

### 3.1 ????? Model ???? Calibration ?????

?? `CalibrationCompleteDialog.xaml.cs` ?????:

```csharp
private void SaveButton_Click(object sender, RoutedEventArgs e)
{
    // ... existing save code ...
    
    // [NEW] Build pointing model
    try
    {
        var builder = new PointingModelBuilder();
        var terms = builder.BuildModel(CalibrationPoints);
        var residualRMS = builder.CalculateResidualRMS(terms, CalibrationPoints);
        
        // Save pointing model
        string modelPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "PointingModel.pmd"
        );
        
        PointingModelFile.SaveModel(
            modelPath, 
            terms, 
            CalibrationPoints, 
            residualRMS
        );
        
        MessageBox.Show(
            $"Pointing Model created successfully!\n\n" +
            $"Residual RMS: {residualRMS:F3} arcsec\n" +
            $"File: {Path.GetFileName(modelPath)}",
            "Success",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }
    catch (Exception ex)
    {
        MessageBox.Show(
            $"Error creating pointing model:\n{ex.Message}",
            "Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }
}
```

### 3.2 ??? Model ??? Slew

?? `SiderealTrackingController.cs`:

```csharp
public void SetTarget(double raHours, double decDegrees, ObserverLocation location)
{
    // Convert RA/Dec -> Alt/Az
    var (az, alt) = ConvertRaDecToAltAz(raHours, decDegrees, location);
    
    // [NEW] Apply pointing model correction
    var (correctedAz, correctedAlt) = ApplyPointingCorrection(az, alt);
    
    // Use corrected coordinates
    _targetAzimuth = correctedAz;
    _targetAltitude = correctedAlt;
    
    // ... rest of tracking logic ...
}
```

---

## ?? ?????????????????

### ??????? Pointing Model:
- RMS Error: **~10-30 arcsec** (mechanical errors)
- Peak Error: **~60+ arcsec**

### ??????? Pointing Model:
- RMS Error: **< 5 arcsec** (residual only)
- Peak Error: **< 10 arcsec**

---

## ?? ????????????????

1. ? **PointingModelBuilder.cs** - ????? model terms
2. ? **PointingModelFile.cs** - Save/Load model file
3. ? **PointingModel.pmd** - Model file (auto-generated)
4. ? **Apply corrections** - ?? SiderealTrackingController

---

## ?? Dependencies

??????????? NuGet package:
```xml
<PackageReference Include="MathNet.Numerics" Version="5.0.0" />
```

?????? least-squares fitting ??? matrix operations.

---

## ?? ???????

- **Tpoint Algorithm**: Standard pointing model terms
- **PWI4**: PlaneWave pointing model implementation
- **ASCOM**: Telescope pointing model specification

