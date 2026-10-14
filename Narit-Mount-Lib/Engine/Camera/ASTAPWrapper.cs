using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// Wrapper for ASTAP plate solver command-line interface
    /// Downloads: http://www.hnsky.org/astap.htm
    /// Requires: astap.exe and star database (h18.dat or g17.dat)
    /// </summary>
    public class ASTAPWrapper
    {
        #region Configuration

        /// <summary>
        /// Path to ASTAP executable
        /// </summary>
        public string ASTAPExecutablePath { get; set; } = @"C:\Program Files\astap\astap.exe";

        /// <summary>
        /// Search radius in degrees (0-180)
        /// Larger = slower but more robust
        /// </summary>
        public double SearchRadius { get; set; } = 30.0;

        /// <summary>
        /// Expected field of view in degrees (optional but speeds up solving)
        /// </summary>
        public double? FieldOfView { get; set; } = null;

        /// <summary>
        /// Downsample factor (1-4)
        /// Higher = faster but less accurate
        /// </summary>
        public int DownsampleFactor { get; set; } = 2;

        /// <summary>
        /// Timeout in seconds
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Keep intermediate files for debugging
        /// </summary>
        public bool KeepIntermediateFiles { get; set; } = false;

        #endregion

        #region Public Methods

        /// <summary>
        /// Check if ASTAP is installed and available
        /// </summary>
        public bool IsASTAPAvailable()
        {
            if (!File.Exists(ASTAPExecutablePath))
            {
                System.Diagnostics.Debug.WriteLine($"[ASTAP] Executable not found: {ASTAPExecutablePath}");
                return false;
            }

            // Check for star database
            string astapDir = Path.GetDirectoryName(ASTAPExecutablePath);
            bool hasH18 = File.Exists(Path.Combine(astapDir, "h18.dat"));
            bool hasG17 = File.Exists(Path.Combine(astapDir, "g17.dat"));

            if (!hasH18 && !hasG17)
            {
                System.Diagnostics.Debug.WriteLine("[ASTAP] No star database found (h18.dat or g17.dat)");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"[ASTAP] Available at: {ASTAPExecutablePath}");
            System.Diagnostics.Debug.WriteLine($"[ASTAP] Database: {(hasH18 ? "h18.dat" : "g17.dat")}");
            return true;
        }

        /// <summary>
        /// Solve plate from camera image data
        /// </summary>
        public async Task<PlateSolution> SolveAsync(CameraImageData imageData)
        {
            if (imageData == null)
            {
                System.Diagnostics.Debug.WriteLine("[ASTAP] Error: Null image data");
                return null;
            }

            try
            {
                // Create temp FITS file
                string tempFitsPath = Path.Combine(Path.GetTempPath(), $"astap_temp_{Guid.NewGuid()}.fits");
                
                // Save as FITS (TODO: implement FITS writer or use PNG)
                // For now, we'll use PNG as ASTAP supports it
                string tempPngPath = Path.Combine(Path.GetTempPath(), $"astap_temp_{Guid.NewGuid()}.png");
                
                System.Diagnostics.Debug.WriteLine($"[ASTAP] Saving image to: {tempPngPath}");
                SaveAsPNG(imageData, tempPngPath);

                // Solve
                var solution = await SolveFileAsync(tempPngPath);

                // Cleanup
                if (!KeepIntermediateFiles)
                {
                    try
                    {
                        if (File.Exists(tempPngPath))
                            File.Delete(tempPngPath);
                        
                        // Delete ASTAP output files
                        string wcsPath = Path.ChangeExtension(tempPngPath, ".wcs");
                        if (File.Exists(wcsPath))
                            File.Delete(wcsPath);
                        
                        string iniPath = Path.ChangeExtension(tempPngPath, ".ini");
                        if (File.Exists(iniPath))
                            File.Delete(iniPath);
                    }
                    catch { }
                }

                return solution;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ASTAP] Solve error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Solve plate from image file
        /// </summary>
        public async Task<PlateSolution> SolveFileAsync(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                System.Diagnostics.Debug.WriteLine($"[ASTAP] Image file not found: {imagePath}");
                return null;
            }

            if (!IsASTAPAvailable())
            {
                System.Diagnostics.Debug.WriteLine("[ASTAP] ASTAP not available");
                return null;
            }

            try
            {
                // Build command arguments
                string args = BuildCommandArguments(imagePath);
                
                System.Diagnostics.Debug.WriteLine($"[ASTAP] Running: {ASTAPExecutablePath} {args}");

                // Run ASTAP
                var result = await RunASTAPAsync(args);

                if (result.exitCode != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[ASTAP] Failed with exit code: {result.exitCode}");
                    System.Diagnostics.Debug.WriteLine($"[ASTAP] Output: {result.output}");
                    return null;
                }

                // Parse WCS output
                string wcsPath = Path.ChangeExtension(imagePath, ".wcs");
                if (!File.Exists(wcsPath))
                {
                    System.Diagnostics.Debug.WriteLine("[ASTAP] WCS file not created");
                    return null;
                }

                var solution = ParseWCSFile(wcsPath);
                
                if (solution != null && solution.Solved)
                {
                    System.Diagnostics.Debug.WriteLine($"[ASTAP] [SUCCESS] Plate solved!");
                    System.Diagnostics.Debug.WriteLine($"[ASTAP]   RA: {solution.RA_Center:F6}°");
                    System.Diagnostics.Debug.WriteLine($"[ASTAP]   Dec: {solution.Dec_Center:F6}°");
                    System.Diagnostics.Debug.WriteLine($"[ASTAP]   Rotation: {solution.Rotation:F2}°");
                    System.Diagnostics.Debug.WriteLine($"[ASTAP]   Pixel Scale: {solution.PixelScale:F3} arcsec/pixel");
                }

                return solution;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ASTAP] Solve error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Build ASTAP command-line arguments
        /// </summary>
        private string BuildCommandArguments(string imagePath)
        {
            string args = $"-f \"{imagePath}\"";
            
            // Search radius
            args += $" -r {SearchRadius}";
            
            // Field of view (if known)
            if (FieldOfView.HasValue)
            {
                args += $" -fov {FieldOfView.Value}";
            }
            
            // Downsample factor
            if (DownsampleFactor > 1)
            {
                args += $" -z {DownsampleFactor}";
            }
            
            // Output WCS file
            string wcsPath = Path.ChangeExtension(imagePath, ".wcs");
            args += $" -o \"{wcsPath}\"";
            
            return args;
        }

        /// <summary>
        /// Run ASTAP process asynchronously
        /// </summary>
        private async Task<(int exitCode, string output)> RunASTAPAsync(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ASTAPExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ASTAPExecutablePath)
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var outputBuilder = new System.Text.StringBuilder();
                
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuilder.AppendLine(e.Data);
                        System.Diagnostics.Debug.WriteLine($"[ASTAP] {e.Data}");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();

                // Wait with timeout
                var completedTask = await Task.WhenAny(
                    Task.Run(() => process.WaitForExit()),
                    Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds))
                );

                if (completedTask == Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)))
                {
                    // Timeout
                    System.Diagnostics.Debug.WriteLine("[ASTAP] Timeout - killing process");
                    try { process.Kill(); } catch { }
                    return (-1, "Timeout");
                }

                return (process.ExitCode, outputBuilder.ToString());
            }
        }

        /// <summary>
        /// Parse WCS (World Coordinate System) file
        /// </summary>
        private PlateSolution ParseWCSFile(string wcsPath)
        {
            try
            {
                string content = File.ReadAllText(wcsPath);
                
                var solution = new PlateSolution();

                // Parse CRVAL1 (RA center)
                var raMatch = Regex.Match(content, @"CRVAL1\s*=\s*([-+]?\d+\.?\d*)");
                if (raMatch.Success)
                    solution.RA_Center = double.Parse(raMatch.Groups[1].Value);

                // Parse CRVAL2 (Dec center)
                var decMatch = Regex.Match(content, @"CRVAL2\s*=\s*([-+]?\d+\.?\d*)");
                if (decMatch.Success)
                    solution.Dec_Center = double.Parse(decMatch.Groups[1].Value);

                // Parse CDELT1 (pixel scale X)
                var cdelt1Match = Regex.Match(content, @"CDELT1\s*=\s*([-+]?\d+\.?\d*[eE]?[-+]?\d*)");
                if (cdelt1Match.Success)
                {
                    double cdelt1 = double.Parse(cdelt1Match.Groups[1].Value);
                    solution.PixelScale = Math.Abs(cdelt1) * 3600.0; // degrees to arcsec
                }

                // Parse CROTA1 or CROTA2 (rotation angle)
                var rotaMatch = Regex.Match(content, @"CROTA[12]\s*=\s*([-+]?\d+\.?\d*)");
                if (rotaMatch.Success)
                    solution.Rotation = double.Parse(rotaMatch.Groups[1].Value);

                // Check if solved
                solution.Solved = solution.RA_Center != 0 && solution.Dec_Center != 0;

                return solution;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ASTAP] WCS parse error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save camera image data as PNG
        /// </summary>
        private void SaveAsPNG(CameraImageData imageData, string pngPath)
        {
            try
            {
                var bitmap = imageData.GetBitmapImage();
                
                if (bitmap != null)
                {
                    // Save BitmapImage to PNG
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    
                    using (var stream = File.Create(pngPath))
                    {
                        encoder.Save(stream);
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[ASTAP] Saved PNG: {pngPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ASTAP] PNG save error: {ex.Message}");
                throw;
            }
        }

        #endregion
    }

    #region Data Structures

    /// <summary>
    /// Plate solving solution containing RA/Dec coordinates
    /// </summary>
    public class PlateSolution
    {
        /// <summary>
        /// Was the plate successfully solved?
        /// </summary>
        public bool Solved { get; set; }

        /// <summary>
        /// Right Ascension of image center (degrees, 0-360)
        /// </summary>
        public double RA_Center { get; set; }

        /// <summary>
        /// Declination of image center (degrees, -90 to +90)
        /// </summary>
        public double Dec_Center { get; set; }

        /// <summary>
        /// Pixel scale (arcseconds per pixel)
        /// </summary>
        public double PixelScale { get; set; }

        /// <summary>
        /// Rotation angle (degrees)
        /// </summary>
        public double Rotation { get; set; }

        /// <summary>
        /// Field of view width (degrees)
        /// </summary>
        public double FOV_Width { get; set; }

        /// <summary>
        /// Field of view height (degrees)
        /// </summary>
        public double FOV_Height { get; set; }

        /// <summary>
        /// Number of stars matched
        /// </summary>
        public int StarsMatched { get; set; }

        public override string ToString()
        {
            if (!Solved)
                return "Not solved";
            
            return $"RA={RA_Center:F6}° Dec={Dec_Center:F6}° Scale={PixelScale:F3}\"/px Rot={Rotation:F2}°";
        }
    }

    #endregion
}
