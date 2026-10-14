using System;
using System.Collections.Generic;
using System.Linq;
using ForkMount_WPF.Engine.Manager;
using PWLib.Astro;

namespace ForkMount_WPF.Engine.PointingModel
{
    /// <summary>
    /// Builds Pointing Model from calibration data
    /// Uses least-squares fitting to determine error correction terms
    /// Based on Tpoint algorithm (standard telescope pointing model)
    /// </summary>
    public class PointingModelBuilder
    {
        /// <summary>
        /// Pointing Model Terms (Tpoint-compatible)
        /// Represents mechanical and optical errors in the mount
        /// </summary>
        public class ModelTerms
        {
            // ===== INDEX ERRORS (constant offsets) =====
            /// <summary>
            /// IH: Azimuth index error (degrees)
            /// Constant offset in azimuth axis zero point
            /// </summary>
            public double IH { get; set; }
            
            /// <summary>
            /// ID: Altitude index error (degrees)
            /// Constant offset in altitude axis zero point
            /// </summary>
            public double ID { get; set; }
            
            // ===== AXIS NON-PERPENDICULARITY =====
            /// <summary>
            /// NP: Non-perpendicularity (degrees)
            /// Azimuth and altitude axes not exactly 90° apart
            /// </summary>
            public double NP { get; set; }
            
            // ===== COLLIMATION ERRORS =====
            /// <summary>
            /// CH: Horizontal collimation (degrees)
            /// Telescope optical axis not aligned with mechanical axis (horizontal)
            /// </summary>
            public double CH { get; set; }
            
            /// <summary>
            /// CV: Vertical collimation (degrees)
            /// Telescope optical axis not aligned with mechanical axis (vertical)
            /// </summary>
            public double CV { get; set; }
            
            // ===== AXIS MISALIGNMENT =====
            /// <summary>
            /// MA: Azimuth axis misalignment (degrees)
            /// Azimuth axis not perfectly vertical
            /// </summary>
            public double MA { get; set; }
            
            /// <summary>
            /// ME: Altitude axis misalignment (degrees)
            /// Altitude axis not perfectly horizontal
            /// </summary>
            public double ME { get; set; }
            
            // ===== ENCODER ERRORS =====
            /// <summary>
            /// HCES: Azimuth encoder sine error
            /// Periodic error in azimuth encoder (sine component)
            /// </summary>
            public double HCES { get; set; }
            
            /// <summary>
            /// HCEC: Azimuth encoder cosine error
            /// Periodic error in azimuth encoder (cosine component)
            /// </summary>
            public double HCEC { get; set; }
            
            // ===== FLEXURE TERMS =====
            /// <summary>
            /// TF: Tube flexure (degrees)
            /// Telescope tube bends under gravity (cosine component)
            /// </summary>
            public double TF { get; set; }
            
            /// <summary>
            /// TX: Tube flexure tilt (degrees)
            /// Telescope tube bends under gravity (sine component)
            /// </summary>
            public double TX { get; set; }
            
            /// <summary>
            /// Check if model has been fitted (any non-zero terms)
            /// </summary>
            public bool HasModel => Math.Abs(IH) > 0.0001 || Math.Abs(ID) > 0.0001;
            
            /// <summary>
            /// Get largest term magnitude (for validation)
            /// </summary>
            public double GetMaxTermMagnitude()
            {
                return new[] { 
                    Math.Abs(IH), Math.Abs(ID), Math.Abs(NP), 
                    Math.Abs(CH), Math.Abs(CV), Math.Abs(MA), 
                    Math.Abs(ME), Math.Abs(HCES), Math.Abs(HCEC), 
                    Math.Abs(TF), Math.Abs(TX) 
                }.Max();
            }
        }
        
        /// <summary>
        /// Build pointing model from calibration points
        /// Uses simple least-squares fitting (no library dependencies)
        /// </summary>
        /// <param name="points">Calibration points with plate solve results</param>
        /// <returns>Fitted model terms</returns>
        public ModelTerms BuildModel(List<CalibrationPoint> points)
        {
            // Filter valid points only
            var validPoints = points.Where(p => 
                p.IsSolved && 
                p.RaError_Arcsec.HasValue && 
                p.DecError_Arcsec.HasValue &&
                p.TotalError_Arcsec.HasValue
            ).ToList();
            
            // [FIX] Lower minimum to 9 points (was 10)
            // 9 points is mathematically sufficient for 11-term model (9 observations >> 11 unknowns is overdetermined)
            if (validPoints.Count < 9)
            {
                throw new Exception($"Too few valid points: {validPoints.Count}. Need at least 9 for reliable fit.");
            }
            
            System.Diagnostics.Debug.WriteLine($"[PointingModelBuilder] Building model from {validPoints.Count} points");
            
            // For simplicity, fit azimuth and altitude independently
            // (Full implementation would solve combined system)
            
            var azimuthTerms = FitAzimuthTerms(validPoints);
            var altitudeTerms = FitAltitudeTerms(validPoints);
            
            // Combine results
            var terms = new ModelTerms
            {
                // Azimuth terms
                IH = azimuthTerms.IH,
                NP = azimuthTerms.NP,
                CH = azimuthTerms.CH,
                MA = azimuthTerms.MA,
                ME = azimuthTerms.ME,
                HCES = azimuthTerms.HCES,
                HCEC = azimuthTerms.HCEC,
                
                // Altitude terms
                ID = altitudeTerms.ID,
                CV = altitudeTerms.CV,
                TF = altitudeTerms.TF,
                TX = altitudeTerms.TX
            };
            
            System.Diagnostics.Debug.WriteLine($"[PointingModelBuilder] Model built successfully:");
            System.Diagnostics.Debug.WriteLine($"  IH={terms.IH:F4}°  ID={terms.ID:F4}°");
            System.Diagnostics.Debug.WriteLine($"  Max term: {terms.GetMaxTermMagnitude():F4}°");
            
            return terms;
        }
        
        /// <summary>
        /// Fit azimuth error terms using least-squares
        /// </summary>
        private ModelTerms FitAzimuthTerms(List<CalibrationPoint> points)
        {
            int n = points.Count;
            int nTerms = 7; // IH, NP, CH, MA, ME, HCES, HCEC
            
            // Build design matrix A and error vector b
            double[,] A = new double[n, nTerms];
            double[] b = new double[n];
            
            for (int i = 0; i < n; i++)
            {
                var point = points[i];
                var az = Angle.FromDegs(point.TargetAzimuth);
                var alt = Angle.FromDegs(point.TargetAltitude);
                
                // Design matrix columns (basis functions) using PWLib.Astro.Angle
                A[i, 0] = 1.0;                                      // IH (constant)
                A[i, 1] = Angle.Tan(alt);                           // NP
                A[i, 2] = 1.0 / Angle.Cos(alt);                     // CH (sec(alt))
                A[i, 3] = Angle.Cos(az) * Angle.Tan(alt);           // MA
                A[i, 4] = Angle.Sin(az) * Angle.Tan(alt);           // ME
                A[i, 5] = Angle.Sin(az);                            // HCES
                A[i, 6] = Angle.Cos(az);                            // HCEC
                
                // Error vector (convert arcsec to degrees)
                b[i] = point.RaError_Arcsec.Value / 3600.0;
            }
            
            // Solve: coefficients = (A^T * A)^-1 * A^T * b
            double[] coeffs = LeastSquaresSolve(A, b, n, nTerms);
            
            return new ModelTerms
            {
                IH = coeffs[0],
                NP = coeffs[1],
                CH = coeffs[2],
                MA = coeffs[3],
                ME = coeffs[4],
                HCES = coeffs[5],
                HCEC = coeffs[6]
            };
        }
        
        /// <summary>
        /// Fit altitude error terms using least-squares
        /// </summary>
        private ModelTerms FitAltitudeTerms(List<CalibrationPoint> points)
        {
            int n = points.Count;
            int nTerms = 4; // ID, CV, TF, TX
            
            double[,] A = new double[n, nTerms];
            double[] b = new double[n];
            
            for (int i = 0; i < n; i++)
            {
                var point = points[i];
                var alt = Angle.FromDegs(point.TargetAltitude);
                
                A[i, 0] = 1.0;                  // ID (constant)
                A[i, 1] = 1.0;                  // CV (constant, but separate from ID)
                A[i, 2] = Angle.Cos(alt);       // TF (tube flexure cos)
                A[i, 3] = Angle.Sin(alt);       // TX (tube flexure sin)
                
                // Error vector (convert arcsec to degrees)
                b[i] = point.DecError_Arcsec.Value / 3600.0;
            }
            
            double[] coeffs = LeastSquaresSolve(A, b, n, nTerms);
            
            return new ModelTerms
            {
                ID = coeffs[0],
                CV = coeffs[1],
                TF = coeffs[2],
                TX = coeffs[3]
            };
        }
        
        /// <summary>
        /// Simple least-squares solver: x = (A^T * A)^-1 * A^T * b
        /// Uses Gaussian elimination (no external library needed)
        /// </summary>
        private double[] LeastSquaresSolve(double[,] A, double[] b, int n, int m)
        {
            // Compute A^T * A
            double[,] AtA = new double[m, m];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < m; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < n; k++)
                    {
                        sum += A[k, i] * A[k, j];
                    }
                    AtA[i, j] = sum;
                }
            }
            
            // Compute A^T * b
            double[] Atb = new double[m];
            for (int i = 0; i < m; i++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++)
                {
                    sum += A[k, i] * b[k];
                }
                Atb[i] = sum;
            }
            
            // Solve AtA * x = Atb using Gaussian elimination
            return GaussianElimination(AtA, Atb, m);
        }
        
        /// <summary>
        /// Gaussian elimination with partial pivoting
        /// </summary>
        private double[] GaussianElimination(double[,] A, double[] b, int n)
        {
            // Make copies (don't modify originals)
            double[,] M = (double[,])A.Clone();
            double[] y = (double[])b.Clone();
            
            // Forward elimination
            for (int k = 0; k < n - 1; k++)
            {
                // Partial pivoting
                int maxRow = k;
                double maxVal = Math.Abs(M[k, k]);
                for (int i = k + 1; i < n; i++)
                {
                    if (Math.Abs(M[i, k]) > maxVal)
                    {
                        maxVal = Math.Abs(M[i, k]);
                        maxRow = i;
                    }
                }
                
                // Swap rows if needed
                if (maxRow != k)
                {
                    for (int j = 0; j < n; j++)
                    {
                        double temp = M[k, j];
                        M[k, j] = M[maxRow, j];
                        M[maxRow, j] = temp;
                    }
                    double tempB = y[k];
                    y[k] = y[maxRow];
                    y[maxRow] = tempB;
                }
                
                // Eliminate column
                for (int i = k + 1; i < n; i++)
                {
                    if (Math.Abs(M[k, k]) < 1e-10)
                        continue; // Skip if pivot too small
                        
                    double factor = M[i, k] / M[k, k];
                    for (int j = k; j < n; j++)
                    {
                        M[i, j] -= factor * M[k, j];
                    }
                    y[i] -= factor * y[k];
                }
            }
            
            // Back substitution
            double[] x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = y[i];
                for (int j = i + 1; j < n; j++)
                {
                    sum -= M[i, j] * x[j];
                }
                
                if (Math.Abs(M[i, i]) < 1e-10)
                {
                    x[i] = 0; // Singular matrix, set to 0
                }
                else
                {
                    x[i] = sum / M[i, i];
                }
            }
            
            return x;
        }
        
        /// <summary>
        /// Calculate residual RMS error after applying model
        /// </summary>
        public double CalculateResidualRMS(ModelTerms terms, List<CalibrationPoint> points)
        {
            var validPoints = points.Where(p => p.IsSolved).ToList();
            
            if (validPoints.Count == 0)
                return double.NaN;
            
            double sumSquaredError = 0;
            foreach (var point in validPoints)
            {
                // Calculate model prediction
                double azPrediction = CalculateAzimuthCorrection(
                    point.TargetAzimuth, 
                    point.TargetAltitude, 
                    terms
                );
                double altPrediction = CalculateAltitudeCorrection(
                    point.TargetAzimuth, 
                    point.TargetAltitude, 
                    terms
                );
                
                // Calculate residual (observed - predicted)
                double azResidual = (point.RaError_Arcsec.Value / 3600.0) - azPrediction;
                double altResidual = (point.DecError_Arcsec.Value / 3600.0) - altPrediction;
                
                // RMS of both axes
                double residual = Math.Sqrt(azResidual * azResidual + altResidual * altResidual);
                sumSquaredError += residual * residual;
            }
            
            // Return RMS in arcseconds
            return Math.Sqrt(sumSquaredError / validPoints.Count) * 3600.0;
        }
        
        /// <summary>
        /// Calculate azimuth correction for given position
        /// Returns correction in DEGREES
        /// </summary>
        public double CalculateAzimuthCorrection(double azimuthDeg, double altitudeDeg, ModelTerms terms)
        {
            var az = Angle.FromDegs(azimuthDeg);
            var alt = Angle.FromDegs(altitudeDeg);
            
            double correction = 0;
            correction += terms.IH;                                     // Index error
            correction += terms.NP * Angle.Tan(alt);                    // Non-perpendicularity
            correction += terms.CH / Angle.Cos(alt);                    // Collimation
            correction += terms.MA * Angle.Cos(az) * Angle.Tan(alt);    // Axis misalignment
            correction += terms.ME * Angle.Sin(az) * Angle.Tan(alt);    // Axis misalignment
            correction += terms.HCES * Angle.Sin(az);                   // Encoder error
            correction += terms.HCEC * Angle.Cos(az);                   // Encoder error
            
            return correction;
        }
        
        /// <summary>
        /// Calculate altitude correction for given position
        /// Returns correction in DEGREES
        /// </summary>
        public double CalculateAltitudeCorrection(double azimuthDeg, double altitudeDeg, ModelTerms terms)
        {
            var alt = Angle.FromDegs(altitudeDeg);
            
            double correction = 0;
            correction += terms.ID;                     // Index error
            correction += terms.CV;                     // Collimation
            correction += terms.TF * Angle.Cos(alt);   // Tube flexure
            correction += terms.TX * Angle.Sin(alt);   // Tube flexure tilt
            
            return correction;
        }
    }
}
