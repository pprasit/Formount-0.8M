using System;
using System.Diagnostics;
using System.Threading.Tasks;
using PWLib;  // ✓ PlaneWave Library

namespace ForkMount_WPF.Engine.PlateSolving
{
    /// <summary>
    /// Plate solver manager - wrapper for PlaneWave PWLib
    /// 
    /// Requirements:
    /// - PWLib.dll (from PlaneWave Instruments) ✓ INSTALLED
    /// - Star catalog (UCAC4 or Tycho-2) ✓ UCAC4 INSTALLED
    /// - Reference: PWLib.BackgroundPlateSolver class
    /// 
    /// Usage:
    ///   var solver = new PlateSolverManager();
    ///   solver.Initialize(catalogPath);
    ///   var result = await solver.SolveImageAsync(fitsPath, estimatedRA, estimatedDec, plateScale);
    /// 
    /// [NARROW FOV SUPPORT]
    /// For telescopes with FOV < 1° (e.g., 10m focal length):
    /// - Automatic retry with blind solve if hint fails
    /// - Plate scale tolerance adjustment
    /// - Enhanced star extraction threshold
    /// </summary>
    public class PlateSolverManager
    {
        private const int TotalSolveTimeoutMs = 15000;
        private const int SolvePollIntervalMs = 100;
        private string _catalogPath;
        private bool _isInitialized = false;

        private static string ResolveUcac4Path()
        {
            string processPath = Environment.GetEnvironmentVariable("UCAC4_PATH", EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return processPath;
            }

            string userPath = Environment.GetEnvironmentVariable("UCAC4_PATH", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(userPath))
            {
                Environment.SetEnvironmentVariable("UCAC4_PATH", userPath, EnvironmentVariableTarget.Process);
                return userPath;
            }

            string machinePath = Environment.GetEnvironmentVariable("UCAC4_PATH", EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(machinePath))
            {
                Environment.SetEnvironmentVariable("UCAC4_PATH", machinePath, EnvironmentVariableTarget.Process);
                return machinePath;
            }

            return string.Empty;
        }
        
        /// <summary>
        /// Enable retry with blind solve when hint-based solve fails
        /// Useful for narrow FOV telescopes where pointing error exceeds FOV
        /// </summary>
        public bool EnableBlindSolveRetry { get; set; } = true;
        
        /// <summary>
        /// Minimum extracted stars required for solve attempt
        /// If fewer stars extracted, will log warning
        /// </summary>
        public int MinimumStarsForSolve { get; set; } = 10;
        
        /// <summary>
        /// FOV threshold below which narrow FOV mode is enabled (degrees)
        /// </summary>
        public double NarrowFovThreshold_Deg { get; set; } = 1.0;
        
        /// <summary>
        /// Initialize plate solver with catalog path
        /// </summary>
        /// <param name="catalogPath">Path to star catalog (e.g., UCAC4 or Tycho-2). Leave empty for auto-detect.</param>
        /// <returns>True if initialization successful</returns>
        public bool Initialize(string catalogPath)
        {
            try
            {
                Debug.WriteLine($"[PlateSolver] ========== PWLib INITIALIZATION ==========");
                
                // Check if UCAC4_PATH environment variable is set
                string ucac4EnvPath = ResolveUcac4Path();
                if (!string.IsNullOrEmpty(ucac4EnvPath))
                {
                    Debug.WriteLine($"[PlateSolver] ? UCAC4_PATH env var found: {ucac4EnvPath}");
                    
                    // Verify catalog files exist
                    if (System.IO.Directory.Exists(ucac4EnvPath))
                    {
                        string indexFile = System.IO.Path.Combine(ucac4EnvPath, "Index.UC4");
                        bool indexExists = System.IO.File.Exists(indexFile);
                        Debug.WriteLine($"[PlateSolver] Index.UC4 accessible: {(indexExists ? "? YES" : "? NO")}");
                        
                        if (indexExists)
                        {
                            Debug.WriteLine($"[PlateSolver] ? PWLib should be able to access UCAC4 catalog!");
                        }
                        else
                        {
                            Debug.WriteLine($"[PlateSolver] ?? Index.UC4 not found - PWLib may fail!");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[PlateSolver] ? UCAC4_PATH directory does not exist!");
                    }
                }
                else
                {
                    Debug.WriteLine($"[PlateSolver] ?? UCAC4_PATH env var NOT set - PWLib will use default/fallback");
                }
                
                // If catalog path is empty, try to use PWLib's default catalog location
                if (string.IsNullOrEmpty(catalogPath))
                {
                    Debug.WriteLine($"[PlateSolver] No catalog path specified - PWLib will use UCAC4_PATH env var or default");
                    _catalogPath = "";
                    _isInitialized = true;
                    Debug.WriteLine($"[PlateSolver] ? Initialized (using env var or PWLib default catalog)");
                    Debug.WriteLine($"[PlateSolver] ================================================");
                    return true;
                }
                
                // Validate catalog path
                if (!System.IO.File.Exists(catalogPath) && !System.IO.Directory.Exists(catalogPath))
                {
                    Debug.WriteLine($"[PlateSolver] ?? Warning: Catalog not found at: {catalogPath}");
                    Debug.WriteLine($"[PlateSolver] Will try to use PWLib default catalog instead");
                    _catalogPath = "";
                }
                else
                {
                    Debug.WriteLine($"[PlateSolver] Catalog: {catalogPath}");
                    _catalogPath = catalogPath;
                }
                
                _isInitialized = true;
                Debug.WriteLine($"[PlateSolver] ? Initialized successfully");
                Debug.WriteLine($"[PlateSolver] ================================================");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlateSolver] ? Initialization failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Solve FITS image asynchronously
        /// </summary>
        /// <param name="fitsFilePath">Path to FITS file</param>
        /// <param name="estimatedRa_J2000_Rads">Estimated RA (J2000, radians) for hint</param>
        /// <param name="estimatedDec_J2000_Rads">Estimated Dec (J2000, radians) for hint</param>
        /// <param name="plateScale_ArcsecPerPixel">Expected plate scale (arcsec/pixel)</param>
        /// <param name="searchRadius_Deg">Search radius around estimate (degrees)</param>
        /// <param name="imageWidth">Actual CCD image width (pixels)</param>
        /// <param name="imageHeight">Actual CCD image height (pixels)</param>
        /// <returns>Plate solve result</returns>
        public async Task<PlateSolveResult> SolveImageAsync(
            string fitsFilePath,
            double estimatedRa_J2000_Rads,
            double estimatedDec_J2000_Rads,
            double plateScale_ArcsecPerPixel,
            double searchRadius_Deg = 5.0,
            int imageWidth = 3096,
            int imageHeight = 2080)
        {
            var solveTask = SolveImageCoreAsync(
                fitsFilePath,
                estimatedRa_J2000_Rads,
                estimatedDec_J2000_Rads,
                plateScale_ArcsecPerPixel,
                searchRadius_Deg,
                imageWidth,
                imageHeight);

            var completedTask = await Task.WhenAny(solveTask, Task.Delay(TotalSolveTimeoutMs));
            if (completedTask != solveTask)
            {
                Debug.WriteLine($"[PlateSolver] ⏱️ HARD TIMEOUT after {TotalSolveTimeoutMs / 1000.0:F0} seconds for {System.IO.Path.GetFileName(fitsFilePath)}");
                return new PlateSolveResult
                {
                    Success = false,
                    ErrorMessage = $"Plate solve timeout after {TotalSolveTimeoutMs / 1000.0:F0}s",
                    SolveTime_ms = TotalSolveTimeoutMs
                };
            }

            return await solveTask;
        }

        private async Task<PlateSolveResult> SolveImageCoreAsync(
            string fitsFilePath,
            double estimatedRa_J2000_Rads,
            double estimatedDec_J2000_Rads,
            double plateScale_ArcsecPerPixel,
            double searchRadius_Deg = 5.0,
            int imageWidth = 3096,
            int imageHeight = 2080)
        {
            if (!_isInitialized)
            {
                return new PlateSolveResult
                {
                    Success = false,
                    ErrorMessage = "Plate solver not initialized"
                };
            }
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                Debug.WriteLine($"[PlateSolver] ========== PLATE SOLVE START ==========");
                Debug.WriteLine($"[PlateSolver] FITS file: {fitsFilePath}");
                Debug.WriteLine($"[PlateSolver] Estimated RA:  {estimatedRa_J2000_Rads * 180.0 / Math.PI / 15.0:F4} hours");
                Debug.WriteLine($"[PlateSolver] Estimated Dec: {estimatedDec_J2000_Rads * 180.0 / Math.PI:F4} deg");
                Debug.WriteLine($"[PlateSolver] Plate scale:   {plateScale_ArcsecPerPixel:F3} arcsec/pixel");
                Debug.WriteLine($"[PlateSolver] Search radius: {searchRadius_Deg:F2} deg");
                Debug.WriteLine($"[PlateSolver] ?? Image dimensions from CCD: {imageWidth}x{imageHeight}");
                
                // Verify UCAC4 catalog access before solve
                string ucac4Path = ResolveUcac4Path();
                if (!string.IsNullOrEmpty(ucac4Path))
                {
                    Debug.WriteLine($"[PlateSolver] ?? UCAC4_PATH: {ucac4Path}");
                    
                    if (System.IO.Directory.Exists(ucac4Path))
                    {
                        string indexFile = System.IO.Path.Combine(ucac4Path, "Index.UC4");
                        if (System.IO.File.Exists(indexFile))
                        {
                            Debug.WriteLine($"[PlateSolver] ? UCAC4 catalog accessible (Index.UC4 found)");
                        }
                        else
                        {
                            Debug.WriteLine($"[PlateSolver] ?? WARNING: Index.UC4 not found in UCAC4_PATH!");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[PlateSolver] ? ERROR: UCAC4_PATH directory does not exist!");
                    }
                }
                else
                {
                    Debug.WriteLine($"[PlateSolver] ?? UCAC4_PATH env var not set - PWLib will use default");
                }
                
                // Verify FITS file exists
                if (!System.IO.File.Exists(fitsFilePath))
                {
                    Debug.WriteLine($"[PlateSolver] ? ERROR: FITS file does not exist!");
                    Debug.WriteLine($"[PlateSolver] =========================================");
                    return new PlateSolveResult
                    {
                        Success = false,
                        ErrorMessage = "FITS file not found"
                    };
                }
                
                
                // Check FITS file size
                var fileInfo = new System.IO.FileInfo(fitsFilePath);
                Debug.WriteLine($"[PlateSolver] FITS file size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0:F1} KB)");
                
                // Create PWLib plate solver
                Debug.WriteLine($"[PlateSolver] Creating BackgroundPlateSolver...");
                var solver = new PWLib.BackgroundPlateSolver(fitsFilePath);
                Debug.WriteLine($"[PlateSolver] BackgroundPlateSolver created");
                
                // [CRITICAL] Check if PWLib loaded the FITS file
                Debug.WriteLine($"[PlateSolver] Immediate status after creation:");
                Debug.WriteLine($"[PlateSolver]   IsLoaded: {solver.IsLoaded}");
                Debug.WriteLine($"[PlateSolver]   IsExtracted: {solver.IsExtracted}");
                Debug.WriteLine($"[PlateSolver]   IsFinished: {solver.IsFinished}");
                
                if (!solver.IsLoaded)
                {
                    Debug.WriteLine($"[PlateSolver] ? CRITICAL: PWLib failed to load FITS file!");
                    Debug.WriteLine($"[PlateSolver] Possible causes:");
                    Debug.WriteLine($"[PlateSolver]   1. FITS format incompatible with PWLib");
                    Debug.WriteLine($"[PlateSolver]   2. FITS header errors");
                    Debug.WriteLine($"[PlateSolver]   3. FITS data corruption");
                    Debug.WriteLine($"[PlateSolver]   4. PWLib internal error");
                }
                
                // [DEBUG] Log PWLib configuration
                Debug.WriteLine($"[PlateSolver] ========== PWLib CONFIGURATION ==========");
                Debug.WriteLine($"[PlateSolver] CCD dimensions: {imageWidth}x{imageHeight}");
                Debug.WriteLine($"[PlateSolver] Plate scale guess: {plateScale_ArcsecPerPixel:F3} arcsec/pixel");
                
                double fovWidth_Deg = imageWidth * plateScale_ArcsecPerPixel / 3600.0;
                double fovHeight_Deg = imageHeight * plateScale_ArcsecPerPixel / 3600.0;
                bool isNarrowFov = Math.Max(fovWidth_Deg, fovHeight_Deg) < NarrowFovThreshold_Deg;
                bool allowAggressiveRetry = searchRadius_Deg >= 8.0;
                bool startBlind = searchRadius_Deg >= 15.0;
                
                Debug.WriteLine($"[PlateSolver] FOV: {fovWidth_Deg:F2}° x {fovHeight_Deg:F2}°");
                Debug.WriteLine($"[PlateSolver] Narrow FOV mode: {(isNarrowFov ? "✓ ENABLED" : "OFF")}");
                Debug.WriteLine($"[PlateSolver] Aggressive retry mode: {(allowAggressiveRetry ? "✓ ENABLED" : "OFF")}");
                Debug.WriteLine($"[PlateSolver] Start blind mode: {(startBlind ? "✓ ENABLED" : "OFF")}");
                Debug.WriteLine($"[PlateSolver] =============================================");
                
                // Set solving parameters
                solver.CenterRa2000GuessRads = estimatedRa_J2000_Rads;
                solver.CenterDec2000GuessRads = estimatedDec_J2000_Rads;
                solver.ArcsecPerPixelGuess = plateScale_ArcsecPerPixel;
                solver.UseStartingCoordinates = !startBlind;
                // Note: SearchRadius property does not exist in PWLib API
                
                Debug.WriteLine($"[PlateSolver] Starting BeginSolve()...");
                
                // Start solving in background
                solver.BeginSolve();
                
                Debug.WriteLine($"[PlateSolver] BeginSolve() returned, waiting for completion...");
                
                // Wait for completion (poll status)
                await Task.Run(async () =>
                {
                    int iterationCount = 0;
                    while (!solver.IsFinished)
                    {
                        await Task.Delay(SolvePollIntervalMs);
                        iterationCount++;
                        
                        // Log progress every second
                        if (iterationCount % (1000 / SolvePollIntervalMs) == 1)  // Every 1 second
                        {
                            Debug.WriteLine($"[PlateSolver] === STATUS UPDATE (T={iterationCount * SolvePollIntervalMs}ms) ===");
                            Debug.WriteLine($"[PlateSolver]   IsLoaded: {solver.IsLoaded}");
                            Debug.WriteLine($"[PlateSolver]   IsExtracted: {solver.IsExtracted}");
                            Debug.WriteLine($"[PlateSolver]   NumExtractedStars: {solver.NumExtractedStars}");
                            Debug.WriteLine($"[PlateSolver]   IsFinished: {solver.IsFinished}");
                            Debug.WriteLine($"[PlateSolver]   FoundMatch: {solver.FoundMatch}");
                        }
                        
                        // Log progress (legacy messages - for compatibility)
                        if (solver.IsLoaded && !solver.IsExtracted)
                        {
                            if (iterationCount == 1)
                                Debug.WriteLine($"[PlateSolver] Status: Extracting stars...");
                        }
                        else if (solver.IsExtracted && !solver.IsFinished)
                        {
                            if (iterationCount == 1)
                            {
                                Debug.WriteLine($"[PlateSolver] Status: Matching stars to catalog...");
                                Debug.WriteLine($"[PlateSolver]   Extracted: {solver.NumExtractedStars} stars");
                            }
                        }
                        
                        // Safety timeout (30 seconds)
                        if (stopwatch.ElapsedMilliseconds >= TotalSolveTimeoutMs)
                        {
                            Debug.WriteLine($"[PlateSolver] ⏱️ TIMEOUT after {TotalSolveTimeoutMs / 1000.0:F0} seconds during hint solve");
                            break;
                        }
                    }
                    
                    Debug.WriteLine($"[PlateSolver] Solve loop finished after {iterationCount * SolvePollIntervalMs}ms");
                });
                
                stopwatch.Stop();
                
                // Check if solve succeeded
                if (solver.FoundMatch)
                {
                    var result = new PlateSolveResult
                    {
                        Success = true,
                        CenterRa_J2000_Rads = solver.CenterRaRad2000,
                        CenterDec_J2000_Rads = solver.CenterDecRad2000,
                        RotationAngle_Deg = solver.RotationAngleDegs,
                        PlateScale_ArcsecPerPixel = solver.MatchedArcsecPerPixel,
                        NumExtractedStars = solver.NumExtractedStars,
                        NumMatchedStars = solver.NumMatchedStars,
                        SolveTime_ms = stopwatch.Elapsed.TotalMilliseconds,
                        PlateSolutionObject = solver.Plate
                    };
                    
                    Debug.WriteLine($"[PlateSolver] ? SOLVE SUCCESS!");
                    Debug.WriteLine($"[PlateSolver] Solved RA:  {result.CenterRa_J2000_Rads * 180.0 / Math.PI / 15.0:F6} hours");
                    Debug.WriteLine($"[PlateSolver] Solved Dec: {result.CenterDec_J2000_Rads * 180.0 / Math.PI:F6} deg");
                    Debug.WriteLine($"[PlateSolver] Rotation:   {result.RotationAngle_Deg:F2} deg");
                    Debug.WriteLine($"[PlateSolver] Plate scale: {result.PlateScale_ArcsecPerPixel:F3} arcsec/pixel");
                    Debug.WriteLine($"[PlateSolver] Matched {result.NumMatchedStars}/{result.NumExtractedStars} stars");
                    Debug.WriteLine($"[PlateSolver] Solve time: {result.SolveTime_ms:F0} ms");
                    Debug.WriteLine($"[PlateSolver] =========================================");
                    
                    return result;
                }
                else
                {
                    Debug.WriteLine($"[PlateSolver] ❌ SOLVE FAILED (hint-based)");
                    Debug.WriteLine($"[PlateSolver] Extracted stars: {solver.NumExtractedStars}");
                    
                    // [NARROW FOV] Warn if too few stars extracted
                    if (solver.NumExtractedStars < MinimumStarsForSolve)
                    {
                        Debug.WriteLine($"[PlateSolver] ⚠️ WARNING: Only {solver.NumExtractedStars} stars extracted (minimum: {MinimumStarsForSolve})");
                        Debug.WriteLine($"[PlateSolver] Possible causes for narrow FOV:");
                        Debug.WriteLine($"[PlateSolver]   1. FOV too small ({fovWidth_Deg:F2}° x {fovHeight_Deg:F2}°) - fewer stars visible");
                        Debug.WriteLine($"[PlateSolver]   2. Exposure too short - faint stars not detected");
                        Debug.WriteLine($"[PlateSolver]   3. Poor seeing conditions");
                        Debug.WriteLine($"[PlateSolver]   4. PWLib star detection threshold too high");
                    }
                    
                    // [RETRY] Search radius setting controls how aggressively we stop trusting
                    // the starting hint. Larger radius means "the operator expects a larger
                    // pointing/focal-length uncertainty", so allow blind/scale retries.
                    bool hasRichStarField = solver.NumExtractedStars >= Math.Max(MinimumStarsForSolve, 12);
                    if (EnableBlindSolveRetry &&
                        solver.NumExtractedStars >= 6 &&
                        (isNarrowFov || allowAggressiveRetry || startBlind || hasRichStarField))
                    {
                        Debug.WriteLine($"[PlateSolver] 🔄 RETRY: Attempting blind solve (no coordinate hint)...");

                        double[] scaleGuesses = (allowAggressiveRetry || hasRichStarField || startBlind)
                            ? new[]
                            {
                                plateScale_ArcsecPerPixel,
                                plateScale_ArcsecPerPixel * 0.33,
                                plateScale_ArcsecPerPixel * 0.5,
                                plateScale_ArcsecPerPixel * 0.75,
                                plateScale_ArcsecPerPixel * 1.25,
                                plateScale_ArcsecPerPixel * 1.5,
                                plateScale_ArcsecPerPixel * 2.0,
                                plateScale_ArcsecPerPixel * 3.0
                            }
                            : new[] { plateScale_ArcsecPerPixel };

                        foreach (double scaleGuess in scaleGuesses.Where(v => v > 0.01).Distinct())
                        {
                            int remainingBudgetMs = Math.Max(0, TotalSolveTimeoutMs - (int)stopwatch.ElapsedMilliseconds);
                            if (remainingBudgetMs < 1500)
                            {
                                Debug.WriteLine($"[PlateSolver] ⏱️ Blind retry skipped - remaining solve budget only {remainingBudgetMs}ms");
                                break;
                            }

                            Debug.WriteLine($"[PlateSolver] Blind retry with scale guess {scaleGuess:F3} arcsec/pixel");

                            var blindSolver = new PWLib.BackgroundPlateSolver(fitsFilePath);
                            blindSolver.ArcsecPerPixelGuess = scaleGuess;
                            blindSolver.UseStartingCoordinates = false;  // Blind solve!

                            blindSolver.BeginSolve();

                            int blindIterations = 0;
                            while (!blindSolver.IsFinished && stopwatch.ElapsedMilliseconds < TotalSolveTimeoutMs)
                            {
                                await Task.Delay(SolvePollIntervalMs);
                                blindIterations++;

                                if (blindIterations % (5000 / SolvePollIntervalMs) == 0)  // Log every 5 seconds
                                {
                                    Debug.WriteLine($"[PlateSolver] Blind solve progress: T={blindIterations * SolvePollIntervalMs}ms, Extracted={blindSolver.NumExtractedStars}, Scale={scaleGuess:F3}");
                                }
                            }

                            if (!blindSolver.IsFinished && stopwatch.ElapsedMilliseconds >= TotalSolveTimeoutMs)
                            {
                                Debug.WriteLine($"[PlateSolver] ⏱️ Blind solve timeout reached total budget of {TotalSolveTimeoutMs / 1000.0:F0}s");
                            }

                            if (blindSolver.FoundMatch)
                            {
                                Debug.WriteLine($"[PlateSolver] ✅ BLIND SOLVE SUCCESS!");
                                Debug.WriteLine($"[PlateSolver] Solved RA:  {blindSolver.CenterRaRad2000 * 180.0 / Math.PI / 15.0:F6} hours");
                                Debug.WriteLine($"[PlateSolver] Solved Dec: {blindSolver.CenterDecRad2000 * 180.0 / Math.PI:F6} deg");
                                Debug.WriteLine($"[PlateSolver] Matched {blindSolver.NumMatchedStars}/{blindSolver.NumExtractedStars} stars");
                                Debug.WriteLine($"[PlateSolver] =========================================");

                                return new PlateSolveResult
                                {
                                    Success = true,
                                    CenterRa_J2000_Rads = blindSolver.CenterRaRad2000,
                                    CenterDec_J2000_Rads = blindSolver.CenterDecRad2000,
                                    RotationAngle_Deg = blindSolver.RotationAngleDegs,
                                    PlateScale_ArcsecPerPixel = blindSolver.MatchedArcsecPerPixel,
                                    NumExtractedStars = blindSolver.NumExtractedStars,
                                    NumMatchedStars = blindSolver.NumMatchedStars,
                                    SolveTime_ms = stopwatch.Elapsed.TotalMilliseconds,
                                    PlateSolutionObject = blindSolver.Plate
                                };
                            }
                        }

                        Debug.WriteLine($"[PlateSolver] ❌ Blind solve also failed");
                    }
                    
                    Debug.WriteLine($"[PlateSolver] Solve time: {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
                    Debug.WriteLine($"[PlateSolver] =========================================");
                    
                    return new PlateSolveResult
                    {
                        Success = false,
                        ErrorMessage = stopwatch.ElapsedMilliseconds >= TotalSolveTimeoutMs
                            ? $"Plate solve timeout after {TotalSolveTimeoutMs / 1000.0:F0}s"
                            : isNarrowFov
                                ? $"No catalog match found (narrow FOV: {fovWidth_Deg:F2}° x {fovHeight_Deg:F2}°, {solver.NumExtractedStars} stars)"
                                : "No catalog match found",
                        NumExtractedStars = solver.NumExtractedStars,
                        SolveTime_ms = stopwatch.Elapsed.TotalMilliseconds
                    };
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                
                Debug.WriteLine($"[PlateSolver] ❌ EXCEPTION: {ex.Message}");
                Debug.WriteLine($"[PlateSolver] Stack: {ex.StackTrace}");
                Debug.WriteLine($"[PlateSolver] =========================================");
                
                return new PlateSolveResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    SolveTime_ms = stopwatch.Elapsed.TotalMilliseconds
                };
            }
        }
        
        /// <summary>
        /// Get diagnostic information for narrow FOV troubleshooting
        /// </summary>
        public string GetNarrowFovDiagnostics(double plateScale, int width, int height, int extractedStars)
        {
            double fovW = width * plateScale / 3600.0;
            double fovH = height * plateScale / 3600.0;
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== NARROW FOV DIAGNOSTICS ===");
            sb.AppendLine($"FOV: {fovW:F3}° x {fovH:F3}° ({fovW * 60:F1}' x {fovH * 60:F1}')");
            sb.AppendLine($"Plate Scale: {plateScale:F3} arcsec/pixel");
            sb.AppendLine($"Extracted Stars: {extractedStars}");
            sb.AppendLine();
            sb.AppendLine("RECOMMENDATIONS:");
            
            if (fovW < 0.5)
            {
                sb.AppendLine("• FOV extremely narrow (<0.5°)");
                sb.AppendLine("  → Consider longer exposure for more stars");
                sb.AppendLine("  → Pointing accuracy must be <0.2° for reliable solve");
                sb.AppendLine("  → Consider using ASTAP with narrow database");
            }
            
            if (extractedStars < 10)
            {
                sb.AppendLine("• Too few stars extracted");
                sb.AppendLine("  → Increase exposure time");
                sb.AppendLine("  → Reduce binning (more pixels = better star detection)");
                sb.AppendLine("  → Check focus quality");
            }
            
            if (extractedStars >= 10 && extractedStars < 20)
            {
                sb.AppendLine("• Marginal star count");
                sb.AppendLine("  → Solve may work but unreliable");
                sb.AppendLine("  → Consider pointing to denser star field");
            }
            
            sb.AppendLine("==============================");
            return sb.ToString();
        }
        
        /// <summary>
        /// Calculate pointing error between expected and solved coordinates
        /// </summary>
        public static (double raError_Arcsec, double decError_Arcsec, double totalError_Arcsec) CalculatePointingError(
            double expectedRa_Rads,
            double expectedDec_Rads,
            double solvedRa_Rads,
            double solvedDec_Rads)
        {
            // Convert to degrees
            double expectedRa_Deg = expectedRa_Rads * 180.0 / Math.PI;
            double expectedDec_Deg = expectedDec_Rads * 180.0 / Math.PI;
            double solvedRa_Deg = solvedRa_Rads * 180.0 / Math.PI;
            double solvedDec_Deg = solvedDec_Rads * 180.0 / Math.PI;
            
            // RA error (wrap to -180 to +180)
            double raError_Deg = solvedRa_Deg - expectedRa_Deg;
            if (raError_Deg > 180.0) raError_Deg -= 360.0;
            if (raError_Deg < -180.0) raError_Deg += 360.0;
            
            // Scale RA by cos(Dec) to get true angular separation
            double decMean_Rad = (expectedDec_Rads + solvedDec_Rads) / 2.0;
            raError_Deg *= Math.Cos(decMean_Rad);
            
            // Dec error
            double decError_Deg = solvedDec_Deg - expectedDec_Deg;
            
            // Convert to arcseconds
            double raError_Arcsec = raError_Deg * 3600.0;
            double decError_Arcsec = decError_Deg * 3600.0;
            
            // Total error (RMS)
            double totalError_Arcsec = Math.Sqrt(raError_Arcsec * raError_Arcsec + decError_Arcsec * decError_Arcsec);
            
            return (raError_Arcsec, decError_Arcsec, totalError_Arcsec);
        }
    }
}
